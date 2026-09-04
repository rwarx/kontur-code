using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Domain.Enums;
using AIClient.Domain.Interfaces;
using AIClient.Domain.Models;
using Microsoft.Extensions.Logging;

namespace AIClient.Application.Services;

/// <summary>
/// Builds the message list sent to a model from a stored conversation.
/// </summary>
/// <remarks>
/// Today it composes four sources: the system prompt, a selection on the Canvas, the conversation
/// history, and any attachment text. The composition order and the trimming pass are the parts that
/// will stay when retrieved memory and tool definitions become additional sources - which is why
/// they are separated here rather than inlined into the chat service.
///
/// Trimming is oldest-first and always keeps the system prompt and the most recent user
/// turn, because dropping either produces a request that cannot be answered sensibly.
/// </remarks>
public sealed class ContextBuilder : IContextBuilder
{
    /// <summary>
    /// The window assumed for sizing a graph block when the real one is unknown.
    /// </summary>
    /// <remarks>
    /// An unknown window disables trimming, which is safe for history - it was sent before and fitted
    /// - but not for a block that could inline half a repository on its first use. A share of a small
    /// window is a block that fits everywhere, and the cost of guessing low is a shorter excerpt.
    /// </remarks>
    private const int AssumedContextWindow = 8192;

    private readonly IConversationService _conversations;
    private readonly IGraphContextSource? _graph;
    private readonly ILogger<ContextBuilder> _logger;

    /// <param name="graph">
    /// Optional: with no graph source a selection is ignored rather than fatal, which is what lets
    /// the existing tests build a context without one.
    /// </param>
    public ContextBuilder(
        IConversationService conversations,
        ILogger<ContextBuilder> logger,
        IGraphContextSource? graph = null)
    {
        _conversations = conversations;
        _graph = graph;
        _logger = logger;
    }

    public async Task<ContextBuildResult> BuildAsync(
        ContextBuildRequest request,
        CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(request.ConversationId, cancellationToken)
            .ConfigureAwait(false);

        if (conversation is null)
        {
            throw new InvalidOperationException($"Conversation {request.ConversationId} was not found.");
        }

        var history = SelectHistory(conversation.Messages, request.UpToMessageId);
        var blocks = Group([.. history.Select(ToTurn)]);

        var systemPrompt = string.IsNullOrWhiteSpace(request.SystemPrompt) ? null : request.SystemPrompt.Trim();
        var budget = CalculateBudget(request);

        systemPrompt = await WithGraphAsync(systemPrompt, request, budget, cancellationToken)
            .ConfigureAwait(false);

        var dropped = budget is null ? 0 : Trim(blocks, systemPrompt, budget.Value);

        var turns = blocks.SelectMany(block => block).ToList();

        var messages = new List<AIChatMessage>(turns.Count + 1);
        if (systemPrompt is not null)
        {
            messages.Add(AIChatMessage.System(systemPrompt));
        }

        messages.AddRange(turns.Select(ToWire));

        var estimated = TokenEstimator.EstimateMessage(systemPrompt) + turns.Sum(t => t.Tokens);

        if (dropped > 0)
        {
            _logger.LogInformation(
                "Trimmed {Dropped} message(s) from conversation {ConversationId} to fit a {Budget}-token budget.",
                dropped, request.ConversationId, budget);
        }

        return new ContextBuildResult(messages, estimated, dropped);
    }

    /// <summary>
    /// Appends what a Canvas selection means to the system prompt, when there is one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In the system turn rather than beside the question, for two reasons. It survives the trimming
    /// pass, which a long conversation would otherwise eat first; and every path that builds a prompt
    /// - send, regenerate, an agent step - gets it identically without knowing it exists.
    /// </para>
    /// <para>
    /// Added before trimming on purpose: the block is part of the fixed cost, so a large selection
    /// pushes old turns out of the window instead of overflowing it.
    /// </para>
    /// </remarks>
    private async Task<string?> WithGraphAsync(
        string? systemPrompt,
        ContextBuildRequest request,
        int? budget,
        CancellationToken cancellationToken)
    {
        if (_graph is null || request.Selection is not { IsEmpty: false } selection)
        {
            return systemPrompt;
        }

        var block = await _graph
            .BuildAsync(
                selection,
                budget ?? (AssumedContextWindow - request.ReservedOutputTokens),
                cancellationToken)
            .ConfigureAwait(false);

        if (block is null)
        {
            return systemPrompt;
        }

        _logger.LogDebug(
            "Described {Nodes} selected graph node(s) in the prompt.",
            selection.NodeIds.Count);

        return systemPrompt is null ? block : $"{systemPrompt}\n\n{block}";
    }

    /// <summary>
    /// Selects the messages eligible for the prompt: complete turns only, stopping before
    /// <paramref name="upToMessageId"/> when regenerating.
    /// </summary>
    private static List<MessageDto> SelectHistory(IReadOnlyList<MessageDto> messages, Guid? upToMessageId)
    {
        var ordered = messages
            .OrderBy(m => m.SequenceNumber)
            .ThenBy(m => m.CreatedAt)
            .ToList();

        if (upToMessageId is { } boundary)
        {
            var index = ordered.FindIndex(m => m.Id == boundary);
            if (index >= 0)
            {
                ordered = ordered.Take(index).ToList();
            }
        }

        var eligible = ordered
            // System turns are supplied separately, from settings or the conversation.
            .Where(m => m.Role is MessageRole.User or MessageRole.Assistant or MessageRole.Tool)
            // A failed turn has no content worth sending, and an empty assistant turn
            // (a placeholder that never received tokens) would confuse the model.
            .Where(m => m.Status != MessageStatus.Failed)
            .Where(HasSomethingToSend);

        return Repair(eligible);
    }

    /// <summary>
    /// Whether a stored message carries anything a model can use.
    /// </summary>
    /// <remarks>
    /// An assistant message with tool calls and no text is the normal shape of an agent step - the
    /// model announces nothing and acts - so the presence of calls counts as content. Judging it
    /// by text alone would drop exactly the messages that explain what the agent did.
    /// </remarks>
    private static bool HasSomethingToSend(MessageDto message) =>
        !string.IsNullOrWhiteSpace(message.Content)
        || message.Attachments.Count > 0
        || message.ToolCallsJson is not null;

    /// <summary>
    /// Removes the surviving half of any broken assistant/tool pair.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pair can break for ordinary reasons: the app was killed between writing the call and
    /// writing its result, the user deleted one message, or a tool result was too damaged to read
    /// back. Whatever the cause, a call with no answer and an answer with no call are both a 400
    /// from every provider, which would make the conversation impossible to continue at all.
    /// </para>
    /// <para>
    /// An assistant message whose answers are missing keeps its text and loses its calls, because
    /// what it said is still true and still useful; one that said nothing is dropped entirely. A
    /// result whose call is missing has nothing to be attached to and is dropped.
    /// </para>
    /// </remarks>
    private static List<MessageDto> Repair(IEnumerable<MessageDto> messages)
    {
        var eligible = messages.ToList();

        // Where each call's answer sits, not merely whether one exists. A result that comes before
        // the call it names cannot be sent - it is dropped below - so counting it as an answer
        // would keep the call and leave it unanswered, which is the exact 400 this method exists
        // to prevent. The last position wins, because any answer after the call is enough.
        var answers = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < eligible.Count; i++)
        {
            if (eligible[i] is { Role: MessageRole.Tool, ToolCallId: { } id })
            {
                answers[id] = i;
            }
        }

        // Filled as the walk moves forward, so a result is only kept when the call it answers
        // came earlier. Order is part of the pairing, not just presence.
        var asked = new HashSet<string>(StringComparer.Ordinal);
        var repaired = new List<MessageDto>(eligible.Count);

        for (var i = 0; i < eligible.Count; i++)
        {
            var message = eligible[i];

            switch (message.Role)
            {
                case MessageRole.Assistant when message.ToolCallsJson is not null:
                    var calls = AgentTranscript.Read(message.ToolCallsJson);

                    if (calls.Count > 0 && calls.All(call => Answered(call.Id, i)))
                    {
                        foreach (var call in calls)
                        {
                            asked.Add(call.Id);
                        }

                        repaired.Add(message);
                    }
                    else if (!string.IsNullOrWhiteSpace(message.Content))
                    {
                        repaired.Add(message with { ToolCallsJson = null });
                    }

                    break;

                case MessageRole.Tool:
                    if (message.ToolCallId is not null && asked.Contains(message.ToolCallId))
                    {
                        repaired.Add(message);
                    }

                    break;

                default:
                    repaired.Add(message);
                    break;
            }
        }

        return repaired;

        bool Answered(string callId, int askedAt) =>
            answers.TryGetValue(callId, out var answeredAt) && answeredAt > askedAt;
    }

    /// <summary>
    /// Renders one stored message into wire text, inlining attachment content.
    /// </summary>
    private static Turn ToTurn(MessageDto message)
    {
        var text = message.Content;

        if (message.Attachments.Count > 0)
        {
            var builder = new System.Text.StringBuilder();

            // Attachments come first so the question that follows them reads as being
            // about the files, which is how users write these prompts.
            foreach (var attachment in message.Attachments)
            {
                if (string.IsNullOrEmpty(attachment.TextContent))
                {
                    continue;
                }

                builder.Append("<file name=\"")
                    .Append(attachment.FileName)
                    .Append("\">\n")
                    .Append(attachment.TextContent);

                if (attachment.IsTruncated)
                {
                    builder.Append("\n… [file truncated - only the first part is shown]");
                }

                builder.Append("\n</file>\n\n");
            }

            builder.Append(text);
            text = builder.ToString();
        }

        // Only an assistant turn can carry calls, and reading them here rather than at send time
        // means a transcript damaged on disk costs one step of history instead of the request.
        var calls = message.Role == MessageRole.Assistant
            ? AgentTranscript.Read(message.ToolCallsJson)
            : [];

        // The arguments are counted, not just the text. An assistant step whose only content is a
        // write_file call carrying three kilobytes of source would otherwise be estimated at zero
        // and the budget would be overrun by exactly the amount that matters.
        var tokens = TokenEstimator.EstimateMessage(text)
            + calls.Sum(call => TokenEstimator.EstimateMessage(call.ArgumentsJson));

        return new Turn(message.Id, message.Role, text, tokens, calls, message.ToolCallId, message.ToolName);
    }

    /// <summary>
    /// Collects each assistant turn together with the tool results answering it.
    /// </summary>
    /// <remarks>
    /// Trimming is what forces this. A tool result is only legal when the call it answers is still
    /// in the transcript, so an assistant step and its results have to leave together or stay
    /// together - dropping the call alone turns the next request into a 400. Grouping them once,
    /// here, means the trimming pass never has to know that rule.
    /// </remarks>
    private static List<List<Turn>> Group(List<Turn> turns)
    {
        var blocks = new List<List<Turn>>();

        foreach (var turn in turns)
        {
            // The head of a block is the turn that made the calls; anything else starts its own.
            if (turn.Role == MessageRole.Tool && blocks.Count > 0 && blocks[^1][0].Calls.Count > 0)
            {
                blocks[^1].Add(turn);
                continue;
            }

            blocks.Add([turn]);
        }

        return blocks;
    }

    /// <summary>Renders one turn in the shape the provider contract expects.</summary>
    private static AIChatMessage ToWire(Turn turn) => turn.Role switch
    {
        MessageRole.User => AIChatMessage.User(turn.Text),

        // The id and the name are guaranteed by Repair, which drops any result that lost its call.
        // The fallbacks exist so a defect there is a worse answer rather than a crash.
        MessageRole.Tool => AIChatMessage.Tool(
            turn.ToolCallId ?? string.Empty,
            turn.ToolName ?? "tool",
            turn.Text),

        _ => turn.Calls.Count > 0
            ? AIChatMessage.Assistant(turn.Text, turn.Calls)
            : AIChatMessage.Assistant(turn.Text),
    };

    /// <summary>
    /// Token budget for the prompt, or null when the model's window is unknown and
    /// trimming must be skipped.
    /// </summary>
    private static int? CalculateBudget(ContextBuildRequest request)
    {
        if (request.ContextWindow is not { } window || window <= 0)
        {
            return null;
        }

        var budget = window - request.ReservedOutputTokens;

        // A pathological setting (reserve larger than the window) must not produce a
        // negative budget that drops everything.
        return budget > 0 ? budget : window / 2;
    }

    /// <summary>
    /// Drops oldest blocks until the prompt fits. Returns how many messages were removed.
    /// </summary>
    private static int Trim(List<List<Turn>> blocks, string? systemPrompt, int budget)
    {
        var fixedCost = TokenEstimator.EstimateMessage(systemPrompt);
        var total = fixedCost + blocks.Sum(Cost);
        var dropped = 0;

        // Always keep the final block: it is the question being asked. If it alone
        // overflows the window the provider will say so, which is the honest outcome -
        // silently truncating the user's actual question would be worse.
        while (total > budget && blocks.Count > 1)
        {
            total -= Cost(blocks[0]);
            dropped += blocks[0].Count;
            blocks.RemoveAt(0);
        }

        // Trimming can leave the history starting on an assistant turn, which several
        // providers reject. Drop that dangling reply too - and its tool results with it,
        // which is exactly what a block being atomic buys.
        while (blocks.Count > 1 && blocks[0][0].Role == MessageRole.Assistant)
        {
            dropped += blocks[0].Count;
            blocks.RemoveAt(0);
        }

        return dropped;

        static int Cost(List<Turn> block) => block.Sum(turn => turn.Tokens);
    }

    /// <param name="Calls">Tool calls on an assistant turn; empty on every other role.</param>
    private readonly record struct Turn(
        Guid Id,
        MessageRole Role,
        string Text,
        int Tokens,
        IReadOnlyList<AIToolCall> Calls,
        string? ToolCallId,
        string? ToolName);
}
