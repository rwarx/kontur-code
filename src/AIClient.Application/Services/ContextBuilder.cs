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
/// Today it composes three sources: the system prompt, the conversation history, and any
/// attachment text. The composition order and the trimming pass are the parts that will
/// stay when project files, retrieved memory and tool definitions become additional
/// sources - which is why they are separated here rather than inlined into the chat service.
///
/// Trimming is oldest-first and always keeps the system prompt and the most recent user
/// turn, because dropping either produces a request that cannot be answered sensibly.
/// </remarks>
public sealed class ContextBuilder : IContextBuilder
{
    private readonly IConversationService _conversations;
    private readonly ILogger<ContextBuilder> _logger;

    public ContextBuilder(IConversationService conversations, ILogger<ContextBuilder> logger)
    {
        _conversations = conversations;
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
        var turns = history.Select(ToTurn).ToList();

        var systemPrompt = string.IsNullOrWhiteSpace(request.SystemPrompt) ? null : request.SystemPrompt.Trim();
        var budget = CalculateBudget(request);
        var dropped = budget is null ? 0 : Trim(turns, systemPrompt, budget.Value);

        var messages = new List<AIChatMessage>(turns.Count + 1);
        if (systemPrompt is not null)
        {
            messages.Add(AIChatMessage.System(systemPrompt));
        }

        messages.AddRange(turns.Select(t => new AIChatMessage(t.WireRole, t.Text)));

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

        return ordered
            // System turns are supplied separately, from settings or the conversation.
            .Where(m => m.Role is MessageRole.User or MessageRole.Assistant)
            // A failed turn has no content worth sending, and an empty assistant turn
            // (a placeholder that never received tokens) would confuse the model.
            .Where(m => m.Status != MessageStatus.Failed)
            .Where(m => !string.IsNullOrWhiteSpace(m.Content) || m.Attachments.Count > 0)
            .ToList();
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

        var wireRole = message.Role == MessageRole.User ? "user" : "assistant";
        return new Turn(message.Id, wireRole, text, TokenEstimator.EstimateMessage(text), message.Role);
    }

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
    /// Drops oldest turns until the prompt fits. Returns how many were removed.
    /// </summary>
    private static int Trim(List<Turn> turns, string? systemPrompt, int budget)
    {
        var fixedCost = TokenEstimator.EstimateMessage(systemPrompt);
        var total = fixedCost + turns.Sum(t => t.Tokens);
        var dropped = 0;

        // Always keep the final turn: it is the question being asked. If it alone
        // overflows the window the provider will say so, which is the honest outcome -
        // silently truncating the user's actual question would be worse.
        while (total > budget && turns.Count > 1)
        {
            total -= turns[0].Tokens;
            turns.RemoveAt(0);
            dropped++;
        }

        // Trimming can leave the history starting on an assistant turn, which several
        // providers reject. Drop that dangling reply too.
        while (turns.Count > 1 && turns[0].Role == MessageRole.Assistant)
        {
            turns.RemoveAt(0);
            dropped++;
        }

        return dropped;
    }

    private readonly record struct Turn(Guid Id, string WireRole, string Text, int Tokens, MessageRole Role);
}
