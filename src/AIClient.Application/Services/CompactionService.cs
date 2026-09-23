using System.Text;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Domain.Enums;
using AIClient.Domain.Interfaces;
using AIClient.Domain.Models;
using Microsoft.Extensions.Logging;

namespace AIClient.Application.Services;

/// <summary>
/// Folds the older half of a conversation into a model-written summary.
/// </summary>
/// <remarks>
/// <para>
/// The alternative this replaces is silent loss. When the prompt outgrows the window the context
/// builder drops the oldest turns, so the model stops knowing what was decided early in the chat
/// and nothing on screen says why. A summary keeps the decisions and loses only the wording.
/// </para>
/// <para>
/// Nothing is deleted. The folded rows stay on disk with <c>IsCompacted</c> set: the transcript
/// still shows every original message, and only the prompt forgets them.
/// </para>
/// <para>
/// One live summary is an invariant. A summary written by an earlier pass is folded into the next
/// one wherever its row happens to sit, because rows are appended and it would otherwise sit
/// permanently inside the region compaction leaves alone, accumulating one block per pass.
/// </para>
/// </remarks>
public sealed class CompactionService : ICompactionService
{
    /// <summary>
    /// Below this there is nothing worth doing: a summary of one turn is longer than the turn.
    /// </summary>
    private const int MinimumFoldableMessages = 2;

    /// <summary>Room left for the summary itself, and the ceiling asked of the model.</summary>
    private const int SummaryMaxTokens = 1200;

    /// <summary>How much of one message is quoted into the summarisation prompt.</summary>
    private const int ProseExcerptChars = 2000;

    /// <summary>
    /// The same, for tool traffic. Lower because it is the bulkiest and the least worth quoting:
    /// what matters about a file listing is that it happened and what it found, not its 40 lines.
    /// </summary>
    private const int ToolExcerptChars = 600;

    /// <summary>
    /// What the model is asked to produce. Deliberately about substance rather than brevity: a
    /// summary that reads well but drops the file paths and the constraints has failed at the only
    /// job it has, which is to let the work continue without the original messages.
    /// </summary>
    private const string Instruction =
        """
        You are compacting a conversation so that it can continue past the model's context window.
        Summarise the transcript below so that someone who cannot see the original messages could
        pick the work up from your summary alone.

        Capture, where the transcript contains them:
        - what the user is trying to achieve, and any constraint, preference or rule they stated
        - decisions that were reached, and the reasoning given for them
        - files, paths, commands, identifiers, versions and error messages that were named
        - what has already been done, and what is still outstanding
        - anything the user corrected, rejected or asked to be done differently

        Rules:
        - Write in the language the transcript is written in.
        - Be specific. An exact name, path or number is worth more than a description of one.
        - Invent nothing. If the transcript does not say it, leave it out.
        - Do not address the user, do not introduce yourself, and do not offer further help.
        - Plain prose and short lists, roughly 400 words at most.
        """;

    private readonly IConversationService _conversations;
    private readonly IProviderRegistry _providers;
    private readonly ISettingsService _settings;
    private readonly ILogger<CompactionService> _logger;

    public CompactionService(
        IConversationService conversations,
        IProviderRegistry providers,
        ISettingsService settings,
        ILogger<CompactionService> logger)
    {
        _conversations = conversations;
        _providers = providers;
        _settings = settings;
        _logger = logger;
    }

    public async Task<bool> ShouldCompactAsync(
        Guid conversationId,
        string? providerId,
        string? modelId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await _conversations.GetAsync(conversationId, cancellationToken).ConfigureAwait(false);

        if (conversation is null)
        {
            return false;
        }

        var window = await WindowAsync(
            providerId ?? conversation.ProviderId,
            modelId ?? conversation.ModelId,
            cancellationToken).ConfigureAwait(false);

        return ContextCost.Fullness(conversation, window) is { } percent
            && percent >= _settings.Current.Chat.CompactAtPercent;
    }

    public async Task<CompactionResult> CompactAsync(
        CompactionRequest request,
        CancellationToken cancellationToken = default)
    {
        var conversation = await _conversations.GetAsync(request.ConversationId, cancellationToken)
            .ConfigureAwait(false);

        if (conversation is null)
        {
            return CompactionResult.Skipped("the conversation no longer exists");
        }

        var provider = _providers.GetProvider(request.ProviderId);

        if (provider is null)
        {
            return CompactionResult.Skipped($"the provider '{request.ProviderId}' is not available");
        }

        var model = await _providers.GetModelAsync(request.ProviderId, request.ModelId, cancellationToken)
            .ConfigureAwait(false);

        var chat = _settings.Current.Chat;

        if (!request.Force)
        {
            var percent = ContextCost.Fullness(conversation, model?.ContextWindow);

            if (percent is null)
            {
                return CompactionResult.Skipped("the model's context window is unknown");
            }

            if (percent < chat.CompactAtPercent)
            {
                return CompactionResult.Skipped(
                    $"the context is only {percent.Value:F0}% full, below the {chat.CompactAtPercent}% threshold");
            }
        }

        var plan = Plan(conversation.Messages, request.KeepRecentMessages ?? chat.CompactKeepRecentMessages);

        if (plan.Count < MinimumFoldableMessages)
        {
            return CompactionResult.Skipped($"only {plan.Count} message(s) are old enough to fold");
        }

        var summary = await SummariseAsync(provider, model, request.ModelId, plan, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(summary))
        {
            return CompactionResult.Skipped("the model did not return a summary");
        }

        // Written before anything is marked, and deliberately so. Dying between the two leaves a
        // summary beside history that is still live, which costs tokens; the other order would
        // lose the history with nothing standing in for it.
        var stored = await _conversations.AddMessageAsync(
            request.ConversationId,
            new NewMessage
            {
                Role = MessageRole.User,
                Content = Wrap(summary, plan.Count),
                IsContextSummary = true,
                ProviderId = request.ProviderId,
                ModelId = request.ModelId,
            },
            cancellationToken).ConfigureAwait(false);

        var folded = await _conversations
            .MarkCompactedAsync([.. plan.All.Select(m => m.Id)], cancellationToken)
            .ConfigureAwait(false);

        var saved = plan.All.Sum(ContextCost.OfMessage) - ContextCost.OfMessage(stored);

        _logger.LogInformation(
            "Compacted conversation {ConversationId}: folded {Folded} message(s), saving about {Saved} token(s).",
            request.ConversationId, folded, saved);

        return new CompactionResult
        {
            MessagesFolded = folded,
            TokensSaved = Math.Max(0, saved),
            Summary = stored,
        };
    }

    /// <summary>The model's published window, or null when there is nothing to measure against.</summary>
    private async Task<int?> WindowAsync(string? providerId, string? modelId, CancellationToken cancellationToken)
    {
        if (providerId is null || modelId is null)
        {
            return null;
        }

        var model = await _providers.GetModelAsync(providerId, modelId, cancellationToken).ConfigureAwait(false);
        return model?.ContextWindow;
    }

    /// <summary>Wraps the model's prose so the next prompt reads it as context, not as a request.</summary>
    /// <remarks>
    /// Stored with the marker rather than adding it at send time, so what the transcript shows and
    /// what the model receives are the same text. The tag form matches the <c>&lt;file&gt;</c>
    /// blocks attachments already use, which models handle well and never answer directly.
    /// </remarks>
    private static string Wrap(string summary, int foldedCount) =>
        $"""
         <context-summary folded-messages="{foldedCount}">
         The earlier part of this conversation is no longer included message by message. This is
         what it contained:

         {summary.Trim()}
         </context-summary>
         """;

    /// <summary>
    /// Chooses what this pass will fold, respecting the pairing rules the prompt builder relies on.
    /// </summary>
    private static FoldPlan Plan(IReadOnlyList<MessageDto> messages, int keep)
    {
        var live = messages
            .Where(m => !m.IsCompacted)
            .OrderBy(m => m.SequenceNumber)
            .ThenBy(m => m.CreatedAt)
            .ToList();

        var boundary = Math.Max(0, live.Count - Math.Max(0, keep));

        // The newest row is never folded whatever the setting says: it is the question being asked,
        // and a summary of the question is not something a model can answer.
        boundary = Math.Min(boundary, Math.Max(0, live.Count - 1));

        // A message still being written cannot be summarised. The placeholder for the turn that
        // triggered this pass is one of them, and so is a row left behind by a crashed run.
        var streaming = live.FindIndex(m => m.Status == MessageStatus.Streaming);
        if (streaming >= 0 && streaming < boundary)
        {
            boundary = streaming;
        }

        var history = live.Take(boundary).ToList();

        // A tool result whose call was folded away answers nothing, which every provider rejects.
        // The boundary moves forward past any that would be orphaned, so a step and its results
        // leave together - the same rule that makes ContextBuilder treat them as one block.
        while (history.Count < live.Count && live[history.Count].Role == MessageRole.Tool)
        {
            history.Add(live[history.Count]);
        }

        // The mirror case, reached only when a step's calls were never answered at all: folding it
        // would describe calls in a summary that nothing answers. That row stays behind instead.
        while (history.Count > 0 && history[^1] is { Role: MessageRole.Assistant, ToolCallsJson: not null })
        {
            history.RemoveAt(history.Count - 1);
        }

        if (history.Count == 0)
        {
            return FoldPlan.Empty;
        }

        // Whatever an earlier pass wrote is folded in as well, wherever its row sits. Rows are
        // appended, so the previous summary is the newest row in the table and would never become
        // old enough to fold on its own - leaving one surviving summary per pass, all of them in
        // every prompt from then on.
        var folding = history.Select(m => m.Id).ToHashSet();

        var superseded = live
            .Where(m => m.IsContextSummary && !folding.Contains(m.Id))
            .ToList();

        return new FoldPlan(superseded, history);
    }

    /// <summary>
    /// Asks the model to summarise its own history, in one non-streaming request.
    /// </summary>
    /// <remarks>
    /// The same model the chat is using, deliberately. A summary written by a different one answers
    /// in a different voice, and the transcript would read as though a stranger had joined.
    /// </remarks>
    private async Task<string?> SummariseAsync(
        IAIProvider provider,
        ModelInfo? model,
        string modelId,
        FoldPlan plan,
        CancellationToken cancellationToken)
    {
        // The summarisation request has to fit the same window the chat is running out of, so the
        // instruction and the answer are both held back before the transcript is measured.
        var budget = model?.ContextWindow is { } window && window > 0
            ? window - SummaryMaxTokens - TokenEstimator.EstimateMessage(Instruction)
            : (int?)null;

        var transcript = Render(plan, ProseExcerptChars, ToolExcerptChars);

        if (budget is { } limit && limit > 0 && TokenEstimator.Estimate(transcript) > limit)
        {
            // Tightened rather than truncated: a summary has to cover the whole span it replaces,
            // so a short quote from every message beats a full quote from half of them.
            transcript = Render(plan, ProseExcerptChars / 5, ToolExcerptChars / 5);
            transcript = Fit(transcript, limit);
        }

        var request = new AIChatRequest
        {
            ModelId = modelId,
            Messages = [AIChatMessage.System(Instruction), AIChatMessage.User(transcript)],

            // Sampling is left at the model's own defaults: one fewer field to be rejected, and
            // there is no version of this task that benefits from a temperature.
            MaxTokens = (model?.Supports("max_tokens") ?? true) ? SummaryMaxTokens : null,
            Stream = false,
        };

        var text = new StringBuilder();

        try
        {
            await foreach (var evt in provider.StreamChatAsync(request, cancellationToken).ConfigureAwait(false))
            {
                switch (evt)
                {
                    case AIStreamEvent.ContentDelta delta:
                        text.Append(delta.Text);
                        break;

                    case AIStreamEvent.Error error:
                        _logger.LogWarning(
                            "Summarisation was refused: {Kind} - {Message}", error.Kind, error.Message);
                        return null;

                    case AIStreamEvent.Completed:
                        return text.ToString();
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Compaction is a convenience. Failing it must never cost the turn that asked for it,
            // so the caller is told nothing happened and the trimming pass keeps the request legal.
            _logger.LogWarning(ex, "The summarisation request failed.");
            return null;
        }

        return text.ToString();
    }

    /// <summary>
    /// Renders the folded messages as a transcript for the summariser to read.
    /// </summary>
    /// <remarks>
    /// Attachments are named but not quoted. Their content is the bulkiest thing in a chat and the
    /// least worth carrying into a summary: what has to survive is which file was involved and what
    /// was concluded about it, both of which are in the surrounding turns.
    /// </remarks>
    private static string Render(FoldPlan plan, int proseLimit, int toolLimit)
    {
        var builder = new StringBuilder();

        foreach (var message in plan.All)
        {
            switch (message.Role)
            {
                // An earlier pass's summary leads the transcript and gets a wider excerpt: it is
                // already compressed, and squeezing it again is where detail goes to die.
                case MessageRole.User when message.IsContextSummary:
                    builder.AppendLine("### Summary of earlier history");
                    builder.AppendLine(Excerpt(message.Content, proseLimit * 2));
                    break;

                case MessageRole.User:
                    builder.AppendLine("### User");
                    builder.AppendLine(Excerpt(message.Content, proseLimit));

                    foreach (var attachment in message.Attachments)
                    {
                        builder.AppendLine($"[attached: {attachment.FileName}]");
                    }

                    break;

                case MessageRole.Assistant:
                    builder.AppendLine("### Assistant");

                    if (!string.IsNullOrWhiteSpace(message.Content))
                    {
                        builder.AppendLine(Excerpt(message.Content, proseLimit));
                    }

                    foreach (var call in AgentTranscript.Read(message.ToolCallsJson))
                    {
                        builder.AppendLine($"[called {call.Name}: {Excerpt(call.ArgumentsJson, toolLimit)}]");
                    }

                    break;

                case MessageRole.Tool:
                    var outcome = message.ToolSucceeded is false ? " (failed)" : string.Empty;
                    builder.AppendLine($"### Result of {message.ToolName ?? "tool"}{outcome}");
                    builder.AppendLine(Excerpt(message.Content, toolLimit));
                    break;
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string Excerpt(string? text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();

        return trimmed.Length <= limit
            ? trimmed
            : $"{trimmed[..limit]}… [{trimmed.Length - limit} more characters]";
    }

    /// <summary>
    /// Cuts a rendered transcript down to roughly <paramref name="limit"/> tokens, keeping the end.
    /// </summary>
    /// <remarks>
    /// Reached only when the fold is too large to quote even in excerpt - a chat that ran for
    /// hundreds of turns before anyone opened it. The end is kept because it is what the surviving
    /// messages refer to, and the start is the part an earlier pass has most likely summarised
    /// already. Proportional rather than token-exact: the estimator is an approximation itself, and
    /// the answer's reserve is still held back on top of this.
    /// </remarks>
    private static string Fit(string transcript, int limit)
    {
        var estimated = TokenEstimator.Estimate(transcript);

        if (estimated <= limit || transcript.Length == 0)
        {
            return transcript;
        }

        var keep = Math.Clamp((int)(transcript.Length * (limit / (double)estimated)), 0, transcript.Length);

        return $"… [the start of the folded history was too long to include]\n\n{transcript[^keep..]}";
    }

    /// <summary>
    /// What one pass will fold, in the order the summariser should read it.
    /// </summary>
    /// <param name="Superseded">Summaries from earlier passes, which this one absorbs.</param>
    /// <param name="History">The real turns being folded, oldest first.</param>
    private sealed record FoldPlan(
        IReadOnlyList<MessageDto> Superseded,
        IReadOnlyList<MessageDto> History)
    {
        /// <summary>Everything being folded, earlier summaries leading, then turns oldest first.</summary>
        public IReadOnlyList<MessageDto> All { get; } = [.. Superseded, .. History];

        /// <summary>
        /// How many real turns are being folded. The absorbed summaries are not counted: the number
        /// is reported to the user as "messages folded", and a stand-in is not a message they wrote.
        /// </summary>
        public int Count => History.Count;

        public static FoldPlan Empty { get; } = new([], []);
    }
}
