using System.Text.Json;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;

namespace AIClient.Server;

/// <summary>
/// Streaming runs: plain chat turns, agent runs, approval polling/answers, cancellation.
/// </summary>
/// <remarks>
/// Every stream is NDJSON: one JSON object per line. Plain chat text keeps the
/// prototype's <c>{type: delta|done|error}</c> contract; everything else arrives as
/// <c>{type: event, name, data}</c>. A run always ends with exactly one terminal
/// frame (<c>done</c> is folded into the final event; <c>error</c> on failure).
/// </remarks>
public static class RunEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Map(WebApplication app)
    {
        // RequireAuthorization on the group rather than on each route: a new endpoint added to this
        // file is then protected by default, and the failure mode of forgetting is a 500 at
        // registration rather than an open door on the machine.
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapPost("/chat/send", SendChatAsync);
        g.MapPost("/chat/regenerate", RegenerateAsync);
        g.MapPost("/agent/run", RunAgentAsync);
        g.MapPost("/ai/chat", QuickAskAsync);
        g.MapGet("/runs/{runId:guid}/approval", GetApproval);
        g.MapPost("/runs/{runId:guid}/approval", AnswerApprovalAsync);
        g.MapPost("/runs/{runId:guid}/cancel", CancelRun);
    }

    private sealed record AttachmentIn(string FileName, string? MimeType, string? TextContent);

    private sealed record ChatSendRequest(
        Guid? RunId,
        Guid? ConversationId,
        string? Title,
        string Content,
        string ProviderId,
        string ModelId,
        IReadOnlyList<AttachmentIn>? Attachments);

    private sealed record ChatRegenerateRequest(
        Guid? RunId,
        Guid ConversationId,
        Guid AssistantMessageId,
        string ProviderId,
        string ModelId);

    private sealed record AgentRunHttpRequest(
        Guid? RunId,
        Guid? ConversationId,
        string? Title,
        string Content,
        string ProviderId,
        string ModelId,
        string Mode,
        IReadOnlyList<AttachmentIn>? Attachments);

    private sealed record ApprovalAnswerRequest(Guid ApprovalId, string Outcome, string? Reason);

    /// <summary>
    /// Prototype-compatible quick ask: one user turn over an existing conversation,
    /// streamed as <c>{type: delta|done|error}</c> NDJSON.
    /// </summary>
    private sealed record QuickAskRequest(Guid ConversationId, string Content, string ProviderId, string ModelId);

    private static IReadOnlyList<NewAttachment> ToAttachments(IReadOnlyList<AttachmentIn>? list) =>
        (list ?? []).Select(a => new NewAttachment
        {
            FileName = a.FileName,
            MimeType = a.MimeType ?? "text/plain",
            TextContent = a.TextContent,
        }).ToList();

    private static async Task<Guid> EnsureConversationAsync(
        IConversationService conversations,
        Guid? conversationId,
        string? title,
        string providerId,
        string modelId,
        CancellationToken ct)
    {
        if (conversationId is { } id)
        {
            return id;
        }

        var summary = await conversations.CreateAsync(title, providerId, modelId, ct).ConfigureAwait(false);
        return summary.Id;
    }

    private static async Task StreamAsync(
        HttpResponse response,
        Func<Func<object, Task>, Task> produce,
        CancellationToken ct)
    {
        response.ContentType = "application/x-ndjson; charset=utf-8";
        response.Headers.CacheControl = "no-store";

        async Task Emit(object evt)
        {
            var line = JsonSerializer.Serialize(evt, Json) + "\n";
            await response.WriteAsync(line, ct).ConfigureAwait(false);
            await response.Body.FlushAsync(ct).ConfigureAwait(false);
        }

        try
        {
            await produce(Emit).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await Emit(new { type = "event", name = "cancelled", data = new { } }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Emit(new { type = "error", message = "The run failed before it started.", details = ex.Message }).ConfigureAwait(false);
        }
    }

    private static async Task SendChatAsync(
        ChatSendRequest req,
        IConversationService conversations,
        IChatService chat,
        RunRegistry runs,
        HttpContext context)
    {
        var runId = req.RunId ?? Guid.NewGuid();
        var entry = runs.Start(runId);
        try
        {
            await StreamAsync(context.Response, async emit =>
            {
                await emit(new { type = "event", name = "run-started", data = new { runId } }).ConfigureAwait(false);
                var conversationId = await EnsureConversationAsync(
                    conversations, req.ConversationId, req.Title,
                    req.ProviderId, req.ModelId, entry.Cancellation.Token).ConfigureAwait(false);

                var send = new SendMessageRequest
                {
                    ConversationId = conversationId,
                    Content = req.Content,
                    ProviderId = req.ProviderId,
                    ModelId = req.ModelId,
                    Attachments = ToAttachments(req.Attachments),
                };

                await foreach (var evt in chat.SendMessageAsync(send, entry.Cancellation.Token).ConfigureAwait(false))
                {
                    await emit(EventMapping.MapChat(evt)).ConfigureAwait(false);
                }
            }, context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            runs.Finish(runId);
        }
    }

    private static async Task RegenerateAsync(
        ChatRegenerateRequest req,
        IChatService chat,
        RunRegistry runs,
        HttpContext context)
    {
        var runId = req.RunId ?? Guid.NewGuid();
        var entry = runs.Start(runId);
        try
        {
            await StreamAsync(context.Response, async emit =>
            {
                await emit(new { type = "event", name = "run-started", data = new { runId } }).ConfigureAwait(false);
                var regen = new RegenerateRequest
                {
                    ConversationId = req.ConversationId,
                    AssistantMessageId = req.AssistantMessageId,
                    ProviderId = req.ProviderId,
                    ModelId = req.ModelId,
                };

                await foreach (var evt in chat.RegenerateAsync(regen, entry.Cancellation.Token).ConfigureAwait(false))
                {
                    await emit(EventMapping.MapChat(evt)).ConfigureAwait(false);
                }
            }, context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            runs.Finish(runId);
        }
    }

    private static async Task RunAgentAsync(
        AgentRunHttpRequest req,
        IConversationService conversations,
        IAgentService agent,
        RunRegistry runs,
        HttpContext context)
    {
        var runId = req.RunId ?? Guid.NewGuid();
        var entry = runs.Start(runId);
        try
        {
            await StreamAsync(context.Response, async emit =>
            {
                await emit(new { type = "event", name = "run-started", data = new { runId } }).ConfigureAwait(false);
                var conversationId = await EnsureConversationAsync(
                    conversations, req.ConversationId, req.Title,
                    req.ProviderId, req.ModelId, entry.Cancellation.Token).ConfigureAwait(false);

                if (!Enum.TryParse<AgentMode>(req.Mode, ignoreCase: true, out var mode))
                {
                    mode = AgentMode.Build;
                }

                var run = new AgentRunRequest
                {
                    ConversationId = conversationId,
                    Content = req.Content,
                    ProviderId = req.ProviderId,
                    ModelId = req.ModelId,
                    Attachments = ToAttachments(req.Attachments),
                    Mode = mode,
                };

                HttpAgentApproval.CurrentRunId.Value = runId;
                try
                {
                    await foreach (var evt in agent.RunAsync(run, entry.Cancellation.Token).ConfigureAwait(false))
                    {
                        if (evt is AgentEvent.ToolCallProposed)
                        {
                            // The question is now on the registry; the renderer polls for it.
                        }

                        await emit(EventMapping.MapAgent(evt)).ConfigureAwait(false);
                    }
                }
                finally
                {
                    HttpAgentApproval.CurrentRunId.Value = null;
                }
            }, context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            runs.Finish(runId);
        }
    }

    private static IResult GetApproval(Guid runId, RunRegistry runs)
    {
        if (!runs.TryGet(runId, out var entry) || entry is null || entry.PendingApproval is not { } pending)
        {
            return Results.NoContent();
        }

        return Results.Ok(pending);
    }

    private static IResult AnswerApprovalAsync(Guid runId, ApprovalAnswerRequest req, RunRegistry runs)
    {
        if (!runs.TryGet(runId, out var entry) || entry is null)
        {
            return Results.NotFound(new { error = "Unknown or finished run." });
        }

        var decision = req.Outcome.ToLowerInvariant() switch
        {
            "allowed" => AgentApprovalDecision.Allow(),
            "allowed-for-run" or "allowedforrun" or "allowed_for_run" => AgentApprovalDecision.AllowForRun(),
            _ => AgentApprovalDecision.Deny(req.Reason),
        };

        var ok = entry.Answer(new PendingApprovalAnswer
        {
            ApprovalId = req.ApprovalId,
            Decision = decision,
        });

        return ok ? Results.Ok(new { answered = true }) : Results.Conflict(new { error = "No such approval is pending." });
    }

    private static IResult CancelRun(Guid runId, RunRegistry runs)
    {
        if (runs.TryGet(runId, out var entry) && entry is not null)
        {
            entry.Cancel();
        }

        return Results.Ok(new { cancelled = true });
    }

    private static async Task QuickAskAsync(
        QuickAskRequest req,
        IChatService chat,
        RunRegistry runs,
        HttpContext context)
    {
        var runId = Guid.NewGuid();
        var entry = runs.Start(runId);
        try
        {
            await StreamAsync(context.Response, async emit =>
            {
                var send = new SendMessageRequest
                {
                    ConversationId = req.ConversationId,
                    Content = req.Content,
                    ProviderId = req.ProviderId,
                    ModelId = req.ModelId,
                };

                var done = false;
                await foreach (var evt in chat.SendMessageAsync(send, entry.Cancellation.Token).ConfigureAwait(false))
                {
                    switch (evt)
                    {
                        case ChatTurnEvent.ContentDelta(var id, var text):
                            await emit(new { type = "delta", messageId = id, text }).ConfigureAwait(false);
                            break;
                        case ChatTurnEvent.Completed(var id, var input, var output, var ms, _, _, _):
                            await emit(new
                            {
                                type = "done",
                                messageId = id,
                                inputTokens = input,
                                outputTokens = output,
                                ms,
                            }).ConfigureAwait(false);
                            done = true;
                            break;
                        case ChatTurnEvent.Failed(var id, _, var user, var details, _):
                            await emit(new
                            {
                                type = "error",
                                messageId = id,
                                message = user,
                                details,
                            }).ConfigureAwait(false);
                            done = true;
                            break;
                        default:
                            break;
                    }
                }

                if (!done)
                {
                    await emit(new { type = "done" }).ConfigureAwait(false);
                }
            }, context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            runs.Finish(runId);
        }
    }
}
