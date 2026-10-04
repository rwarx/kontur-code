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

    /// <summary>
    /// How often a silent stream says something.
    /// </summary>
    /// <remarks>
    /// A reasoning model routinely spends minutes deciding before its first token, and an approval
    /// question sits open for as long as a person takes. During either, zero bytes are written, and
    /// Kestrel's <c>MinResponseDataRate</c> — like any proxy in front of it — will close a connection
    /// that has gone quiet. The run then ends with no output and no error, which reads as a hang.
    ///
    /// Five seconds is comfortably under every idle timeout in the path, and the frames are small
    /// enough that a two-hour run costs a few hundred kilobytes.
    /// </remarks>
    private static readonly TimeSpan StreamHeartbeatInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Says "that run id is taken" in the same JSON shape every other failure on this surface uses,
    /// so the renderer has one thing to parse.
    /// </summary>
    /// <remarks>
    /// A raw string literal with no interpolation, so the message is exactly what is written rather
    /// than a format that could throw while reporting a failure.
    /// </remarks>
    private static async Task WriteConflictAsync(HttpContext context)
    {
        context.Response.ContentType ??= "application/json; charset=utf-8";

        await context.Response
            .WriteAsync(
                """{"error":"That run id is already in flight, or too many runs are open. Wait for it to finish, or start a new one."}""",
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task StreamAsync(
        HttpResponse response,
        Func<Func<object, Task>, Task> produce,
        CancellationToken ct)
    {
        response.ContentType = "application/x-ndjson; charset=utf-8";
        response.Headers.CacheControl = "no-store";

        // Serialised because the heartbeat and the producer both write to the same body, and two
        // concurrent WriteAsync calls on one response interleave into a corrupt stream.
        var writeGate = new SemaphoreSlim(1, 1);

        async Task Emit(object evt)
        {
            var line = JsonSerializer.Serialize(evt, Json) + "\n";

            await writeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

            try
            {
                await response.WriteAsync(line, ct).ConfigureAwait(false);
                await response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                writeGate.Release();
            }
        }

        using var heartbeat = new Timer(
            // CancellationToken.None on purpose: the timer's own cancellation has already fired by the
            // time this can run, and a heartbeat that stopped itself is the bug being fixed here.
            _ => _ = Emit(new { type = "ping" }),
            null,
            StreamHeartbeatInterval,
            StreamHeartbeatInterval);

        try
        {
            await produce(Emit).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client is gone, so there is nobody to tell. Emitting with the cancelled token would
            // throw from inside the catch and escape as an unhandled exception.
        }
        catch (Exception ex)
        {
            await Emit(new { type = "error", message = "The run failed before it started.", details = ex.Message }).ConfigureAwait(false);
        }
        finally
        {
            await heartbeat.DisposeAsync().ConfigureAwait(false);
            writeGate.Dispose();
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

        if (entry is null)
        {
            // The id is already in flight, or too many runs are open. Refusing is the point: the
            // dictionary indexer this replaced silently overwrote the live run, orphaning its
            // cancellation token and leaving its approval waiter waiting for an answer that could
            // never arrive.
            //
            // Written by hand because these handlers return Task rather than Task<IResult>:
            // the response has to be finished here, before the streaming path starts anything.
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await WriteConflictAsync(context).ConfigureAwait(false);
            return;
        }
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

        if (entry is null)
        {
            // The id is already in flight, or too many runs are open. Refusing is the point: the
            // dictionary indexer this replaced silently overwrote the live run, orphaning its
            // cancellation token and leaving its approval waiter waiting for an answer that could
            // never arrive.
            //
            // Written by hand because these handlers return Task rather than Task<IResult>:
            // the response has to be finished here, before the streaming path starts anything.
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await WriteConflictAsync(context).ConfigureAwait(false);
            return;
        }
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

        if (entry is null)
        {
            // The id is already in flight, or too many runs are open. Refusing is the point: the
            // dictionary indexer this replaced silently overwrote the live run, orphaning its
            // cancellation token and leaving its approval waiter waiting for an answer that could
            // never arrive.
            //
            // Written by hand because these handlers return Task rather than Task<IResult>:
            // the response has to be finished here, before the streaming path starts anything.
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await WriteConflictAsync(context).ConfigureAwait(false);
            return;
        }
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

        if (entry is null)
        {
            // The id is already in flight, or too many runs are open. Refusing is the point: the
            // dictionary indexer this replaced silently overwrote the live run, orphaning its
            // cancellation token and leaving its approval waiter waiting for an answer that could
            // never arrive.
            //
            // Written by hand because these handlers return Task rather than Task<IResult>:
            // the response has to be finished here, before the streaming path starts anything.
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await WriteConflictAsync(context).ConfigureAwait(false);
            return;
        }
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
