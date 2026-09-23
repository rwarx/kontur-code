using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AIClient.Application.Services;

/// <summary>
/// Assembles the context report the session panel displays.
/// </summary>
/// <remarks>
/// <para>
/// Two different kinds of number meet here and must not be confused. The token counts a provider
/// billed are authoritative but only exist for turns that finished, and only for the newest one is
/// the total meaningful - it is the size of the prompt as it was actually sent. The breakdown, by
/// contrast, is estimated locally, because no provider says which of the messages filled the
/// window. The estimate uses the same code the prompt builder uses, so the bar the user sees and
/// the trimming the app performs agree with each other.
/// </para>
/// <para>
/// Everything is read once per open. Nothing is cached: a report is cheap, and a stale one would
/// be worse than none, since the whole point is to answer "right now".
/// </para>
/// </remarks>
public sealed class SessionContextService : ISessionContextService
{
    private readonly IConversationService _conversations;
    private readonly IProviderRegistry _providers;
    private readonly ISettingsService _settings;
    private readonly ILogger<SessionContextService> _logger;

    public SessionContextService(
        IConversationService conversations,
        IProviderRegistry providers,
        ISettingsService settings,
        ILogger<SessionContextService> logger)
    {
        _conversations = conversations;
        _providers = providers;
        _settings = settings;
        _logger = logger;
    }

    public async Task<SessionContextReport?> GetReportAsync(
        Guid conversationId,
        string? providerId = null,
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        var conversation = await _conversations.GetAsync(conversationId, cancellationToken).ConfigureAwait(false);

        if (conversation is null)
        {
            return null;
        }

        // The picker's current choice wins over what the conversation last recorded: the panel is
        // answering "how much room is there for the next message", and that is the model that will
        // receive it.
        var resolvedProviderId = providerId ?? conversation.ProviderId;
        var resolvedModelId = modelId ?? conversation.ModelId;

        var model = resolvedProviderId is not null && resolvedModelId is not null
            ? await _providers.GetModelAsync(resolvedProviderId, resolvedModelId, cancellationToken)
                .ConfigureAwait(false)
            : null;

        if (model is null && resolvedModelId is not null)
        {
            // Worth recording: with no catalogue entry there is no window and no price, so the
            // panel has to draw both as unknown and nothing on screen explains why.
            _logger.LogDebug(
                "No catalogue entry for {ProviderId}/{ModelId}; context window and cost unknown.",
                resolvedProviderId,
                resolvedModelId);
        }

        var messages = conversation.Messages;
        var usage = LastTurnUsage(messages);
        var window = model?.ContextWindow;

        var chat = _settings.Current.Chat;
        var percent = window is > 0 ? usage.Total * 100.0 / window.Value : (double?)null;

        return new SessionContextReport
        {
            ConversationId = conversation.Id,
            Title = conversation.Title,
            ProviderName = model?.ProviderName ?? resolvedProviderId,
            ModelName = model?.Name ?? resolvedModelId,
            ContextWindow = window,
            InputTokens = usage.Input,
            OutputTokens = usage.Output,
            ReasoningTokens = usage.Reasoning,
            CacheReadTokens = usage.CacheRead,
            CacheWriteTokens = usage.CacheWrite,
            TotalTokens = usage.Total,
            UsagePercent = percent,
            MessageCount = messages.Count(m => !m.IsContextSummary),
            UserMessageCount = messages.Count(m => m is { Role: MessageRole.User, IsContextSummary: false }),
            AssistantMessageCount = messages.Count(m => m.Role == MessageRole.Assistant),
            ToolMessageCount = messages.Count(m => m.Role == MessageRole.Tool),
            TotalCost = TotalCost(messages, model),
            CreatedAt = conversation.CreatedAt,
            LastActivityAt = conversation.UpdatedAt,
            Breakdown = Breakdown(messages, conversation.SystemPrompt ?? chat.SystemPrompt),
            ShouldCompact = percent is { } filled && filled >= chat.CompactAtPercent,
            CompactedMessageCount = messages.Count(m => m.IsCompacted),
        };
    }

    /// <summary>
    /// The billed size of the most recent completed turn.
    /// </summary>
    /// <remarks>
    /// One turn rather than a sum over the chat, because these numbers describe one request. Input
    /// tokens already include the whole history that was sent, so adding earlier turns would count
    /// the same history once per turn - which is how a 74k prompt ends up being reported as a
    /// million. The selection itself lives on <see cref="ContextCost"/>, so the panel and
    /// compaction cannot disagree about which turn is the current one.
    /// </remarks>
    private static TurnUsage LastTurnUsage(IReadOnlyList<MessageDto> messages)
    {
        var last = ContextCost.LastBilledTurn(messages);

        if (last is null)
        {
            return default;
        }

        var input = last.InputTokens ?? 0;
        var output = last.OutputTokens ?? 0;
        var cacheRead = last.CacheReadTokens ?? 0;
        var cacheWrite = last.CacheWriteTokens ?? 0;

        return new TurnUsage(
            input,
            output,
            last.ReasoningTokens ?? 0,
            cacheRead,
            cacheWrite,
            // Reasoning is excluded on purpose: providers report it as a slice of the completion,
            // not as a separate charge.
            input + output + cacheRead + cacheWrite);
    }

    /// <summary>
    /// What the chat has cost so far, or null when the model has no published price.
    /// </summary>
    /// <remarks>
    /// Summed across every turn, unlike the token counts above, because money does accumulate.
    /// Cached reads are billed at the prompt rate here; providers that discount them report a
    /// smaller prompt count, so the discount is already reflected in what was recorded.
    /// </remarks>
    private static decimal? TotalCost(IReadOnlyList<MessageDto> messages, ModelInfo? model)
    {
        if (model is null || (model.PromptPricePerMillion is null && model.CompletionPricePerMillion is null))
        {
            return null;
        }

        var promptPrice = model.PromptPricePerMillion ?? 0m;
        var completionPrice = model.CompletionPricePerMillion ?? 0m;

        var input = messages.Sum(m => (long)(m.InputTokens ?? 0));
        var output = messages.Sum(m => (long)(m.OutputTokens ?? 0));

        return (input * promptPrice + output * completionPrice) / 1_000_000m;
    }

    /// <summary>
    /// Splits the estimated prompt into the four bands the stacked bar draws.
    /// </summary>
    /// <remarks>
    /// Costed by <see cref="ContextCost"/> so the bar, compaction and the prompt builder share one
    /// definition: attachment text lands on the user turn that carried it, tool-call arguments land
    /// on tools rather than on the assistant that asked, and the per-message overhead is charged
    /// once per message. Compacted rows are left out, because the model no longer sees them - that
    /// is what makes the bar drop after a fold.
    /// </remarks>
    private static ContextBreakdown Breakdown(IReadOnlyList<MessageDto> messages, string? systemPrompt)
    {
        var user = 0;
        var assistant = 0;
        var tool = 0;
        var other = TokenEstimator.EstimateMessage(systemPrompt);

        foreach (var message in messages)
        {
            if (message.IsCompacted)
            {
                continue;
            }

            var prose = ContextCost.OfProse(message);

            switch (message.Role)
            {
                case MessageRole.User:
                    user += prose;
                    break;

                case MessageRole.Assistant:
                    assistant += prose;

                    // The arguments belong with the tool traffic, not with what the model said.
                    // A write_file call carrying three kilobytes of source is tool cost.
                    tool += ContextCost.OfToolCalls(message);
                    break;

                case MessageRole.Tool:
                    tool += prose;
                    break;

                default:
                    other += prose;
                    break;
            }
        }

        return new ContextBreakdown
        {
            UserTokens = user,
            AssistantTokens = assistant,
            ToolTokens = tool,
            OtherTokens = other,
        };
    }

    private readonly record struct TurnUsage(
        int Input,
        int Output,
        int Reasoning,
        int CacheRead,
        int CacheWrite,
        int Total);
}
