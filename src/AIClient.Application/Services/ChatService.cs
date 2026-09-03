using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Domain.Enums;
using AIClient.Domain.Interfaces;
using AIClient.Domain.Models;
using Microsoft.Extensions.Logging;

namespace AIClient.Application.Services;

/// <summary>
/// Runs one chat turn end to end: persist the question, build the context, stream the
/// answer, persist the result.
/// </summary>
/// <remarks>
/// Ordering is the important part. The user message and an empty assistant placeholder
/// are committed before the first token is requested, so a crash, a kill, or a power cut
/// mid-stream still leaves a transcript that reads correctly on restart. Partial text is
/// flushed periodically rather than only at the end, for the same reason.
/// </remarks>
public sealed class ChatService : IChatService
{
    /// <summary>
    /// How often streamed text is written to the database. Every token would mean a write
    /// per token; only at the end would lose everything on a crash. A second is a
    /// reasonable worst-case loss for an operation that is already network-bound.
    /// </summary>
    private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(1);

    private readonly IConversationService _conversations;
    private readonly IProviderRegistry _providers;
    private readonly IContextBuilder _contextBuilder;
    private readonly ISettingsService _settings;
    private readonly ILogger<ChatService> _logger;

    public ChatService(
        IConversationService conversations,
        IProviderRegistry providers,
        IContextBuilder contextBuilder,
        ISettingsService settings,
        ILogger<ChatService> logger)
    {
        _conversations = conversations;
        _providers = providers;
        _contextBuilder = contextBuilder;
        _settings = settings;
        _logger = logger;
    }

    public async IAsyncEnumerable<ChatTurnEvent> SendMessageAsync(
        SendMessageRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Persist the question first. If anything below fails, the user's own words survive.
        var userMessage = await _conversations.AddMessageAsync(
            request.ConversationId,
            new NewMessage
            {
                Role = MessageRole.User,
                Content = request.Content,
                Attachments = request.Attachments,
            },
            cancellationToken).ConfigureAwait(false);

        yield return new ChatTurnEvent.UserMessageSaved(userMessage);

        // Auto-title on the first exchange, before the answer arrives, so the sidebar
        // stops saying "New Chat" while the model is still thinking.
        if (_settings.Current.General.AutoGenerateTitles)
        {
            var title = await TryAutoTitleAsync(request.ConversationId, cancellationToken).ConfigureAwait(false);
            if (title is not null)
            {
                yield return new ChatTurnEvent.TitleGenerated(request.ConversationId, title);
            }
        }

        await foreach (var evt in RunTurnAsync(
                           request.ConversationId,
                           request.ProviderId,
                           request.ModelId,
                           upToMessageId: null,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    public async IAsyncEnumerable<ChatTurnEvent> RegenerateAsync(
        RegenerateRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Discard the old answer and everything after it, so the model sees exactly the
        // history that preceded the message being replaced.
        await _conversations.DeleteFromMessageAsync(request.AssistantMessageId, inclusive: true, cancellationToken)
            .ConfigureAwait(false);

        await foreach (var evt in RunTurnAsync(
                           request.ConversationId,
                           request.ProviderId,
                           request.ModelId,
                           upToMessageId: null,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    /// <summary>
    /// The shared body of send and regenerate: everything from "history is ready" to
    /// "the answer is persisted".
    /// </summary>
    private async IAsyncEnumerable<ChatTurnEvent> RunTurnAsync(
        Guid conversationId,
        string providerId,
        string modelId,
        Guid? upToMessageId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var placeholder = await _conversations.AddMessageAsync(
            conversationId,
            new NewMessage
            {
                Role = MessageRole.Assistant,
                Content = string.Empty,
                Status = MessageStatus.Streaming,
                ProviderId = providerId,
                ModelId = modelId,
            },
            cancellationToken).ConfigureAwait(false);

        yield return new ChatTurnEvent.AssistantMessageStarted(placeholder);

        // Everything that can fail before the stream opens is resolved here, so the
        // iterator below only has to deal with streaming failures.
        var preparation = await PrepareAsync(conversationId, providerId, modelId, upToMessageId, cancellationToken)
            .ConfigureAwait(false);

        if (preparation.Failure is { } earlyFailure)
        {
            await PersistFailureAsync(placeholder.Id, string.Empty, earlyFailure, CancellationToken.None)
                .ConfigureAwait(false);

            yield return new ChatTurnEvent.Failed(
                placeholder.Id,
                earlyFailure.Kind,
                earlyFailure.UserMessage,
                earlyFailure.TechnicalDetails,
                earlyFailure.IsRetryable);

            yield break;
        }

        var provider = preparation.Provider!;
        var chatRequest = preparation.Request!;

        var buffer = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();
        var lastPersist = stopwatch.Elapsed;
        int? inputTokens = null;
        int? outputTokens = null;

        // The provider's async sequence is stepped by hand rather than with `await foreach`
        // so that a mid-stream exception can be caught without a try/catch around a yield,
        // which C# does not allow.
        var stream = provider.StreamChatAsync(chatRequest, cancellationToken).GetAsyncEnumerator(cancellationToken);

        try
        {
            while (true)
            {
                AIStreamEvent? current;
                AIProviderException? failure = null;
                var cancelled = false;

                try
                {
                    current = await stream.MoveNextAsync().ConfigureAwait(false) ? stream.Current : null;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    current = null;
                    cancelled = true;
                }
                catch (Exception ex)
                {
                    current = null;
                    failure = ProviderErrorMapper.FromException(ex, provider.DisplayName, provider.Id);
                }

                if (cancelled)
                {
                    stopwatch.Stop();
                    await PersistCancellationAsync(placeholder.Id, buffer.ToString(), stopwatch).ConfigureAwait(false);
                    yield return new ChatTurnEvent.Cancelled(placeholder.Id);
                    yield break;
                }

                if (failure is not null)
                {
                    stopwatch.Stop();
                    await PersistFailureAsync(placeholder.Id, buffer.ToString(), failure, CancellationToken.None)
                        .ConfigureAwait(false);

                    yield return new ChatTurnEvent.Failed(
                        placeholder.Id, failure.Kind, failure.UserMessage, failure.TechnicalDetails, failure.IsRetryable);
                    yield break;
                }

                if (current is null)
                {
                    // The provider ended without a terminal event. Treat what we have as complete.
                    break;
                }

                switch (current)
                {
                    case AIStreamEvent.ContentDelta delta:
                        buffer.Append(delta.Text);
                        yield return new ChatTurnEvent.ContentDelta(placeholder.Id, delta.Text);

                        // Periodic flush bounds how much a crash can cost.
                        if (stopwatch.Elapsed - lastPersist > PersistInterval)
                        {
                            lastPersist = stopwatch.Elapsed;
                            await _conversations.UpdateMessageAsync(
                                new MessageUpdate { MessageId = placeholder.Id, Content = buffer.ToString() },
                                CancellationToken.None).ConfigureAwait(false);
                        }

                        break;

                    case AIStreamEvent.Usage usage:
                        inputTokens = usage.InputTokens ?? inputTokens;
                        outputTokens = usage.OutputTokens ?? outputTokens;
                        break;

                    case AIStreamEvent.Error error:
                        stopwatch.Stop();
                        var mapped = new AIProviderException(
                            error.Kind, error.Message, error.TechnicalDetails, provider.Id);

                        await PersistFailureAsync(placeholder.Id, buffer.ToString(), mapped, CancellationToken.None)
                            .ConfigureAwait(false);

                        yield return new ChatTurnEvent.Failed(
                            placeholder.Id, mapped.Kind, mapped.UserMessage, mapped.TechnicalDetails, mapped.IsRetryable);
                        yield break;

                    case AIStreamEvent.Completed:
                        goto finished;

                    case AIStreamEvent.ReasoningDelta:
                        // Reasoning traces are not rendered in the MVP. Consumed and dropped
                        // so that models which emit them still stream their answers correctly.
                        break;
                }
            }

            finished:
            stopwatch.Stop();

            var content = buffer.ToString();

            // A completed stream with no text means the model produced nothing. Saying so
            // is more useful than showing an empty bubble.
            if (string.IsNullOrEmpty(content))
            {
                await _conversations.UpdateMessageAsync(
                    new MessageUpdate
                    {
                        MessageId = placeholder.Id,
                        Content = string.Empty,
                        Status = MessageStatus.Failed,
                        ErrorKind = AIErrorKind.Unknown,
                        ErrorMessage = "The model returned an empty response.",
                        GenerationTimeMs = (int)stopwatch.ElapsedMilliseconds,
                    },
                    CancellationToken.None).ConfigureAwait(false);

                yield return new ChatTurnEvent.Failed(
                    placeholder.Id,
                    AIErrorKind.Unknown,
                    "The model returned an empty response.",
                    null,
                    IsRetryable: true);
                yield break;
            }

            await _conversations.UpdateMessageAsync(
                new MessageUpdate
                {
                    MessageId = placeholder.Id,
                    Content = content,
                    Status = MessageStatus.Complete,
                    InputTokens = inputTokens ?? preparation.EstimatedInputTokens,
                    OutputTokens = outputTokens,
                    GenerationTimeMs = (int)stopwatch.ElapsedMilliseconds,
                },
                CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation(
                "Turn completed for conversation {ConversationId} using {ProviderId}/{ModelId} in {ElapsedMs} ms.",
                conversationId, providerId, modelId, stopwatch.ElapsedMilliseconds);

            yield return new ChatTurnEvent.Completed(
                placeholder.Id,
                inputTokens ?? preparation.EstimatedInputTokens,
                outputTokens,
                (int)stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resolves the provider, loads the model's capabilities, builds the context and
    /// assembles the request. Returns a failure instead of throwing so the caller can
    /// persist it against the placeholder message.
    /// </summary>
    private async Task<Preparation> PrepareAsync(
        Guid conversationId,
        string providerId,
        string modelId,
        Guid? upToMessageId,
        CancellationToken cancellationToken)
    {
        try
        {
            var provider = _providers.GetProvider(providerId);
            if (provider is null)
            {
                return Preparation.Failed(new AIProviderException(
                    AIErrorKind.NotConfigured,
                    $"The provider '{providerId}' is not available. Configure it in Settings → Providers.",
                    null,
                    providerId));
            }

            var model = await _providers.GetModelAsync(providerId, modelId, cancellationToken).ConfigureAwait(false);
            var chat = _settings.Current.Chat;

            var conversation = await _conversations.GetAsync(conversationId, cancellationToken).ConfigureAwait(false);
            var systemPrompt = conversation?.SystemPrompt ?? chat.SystemPrompt;

            var context = await _contextBuilder.BuildAsync(
                new ContextBuildRequest
                {
                    ConversationId = conversationId,
                    SystemPrompt = systemPrompt,
                    ContextWindow = model?.ContextWindow,
                    ReservedOutputTokens = chat.ReservedOutputTokens,
                    UpToMessageId = upToMessageId,
                },
                cancellationToken).ConfigureAwait(false);

            // Only send parameters the model is known to accept. Sending an unsupported
            // one is a hard 400 on several providers, which is a worse outcome than
            // falling back to the model's own default.
            var request = new AIChatRequest
            {
                ModelId = modelId,
                Messages = context.Messages,
                Temperature = Allow(model, "temperature") ? chat.Temperature : null,
                TopP = Allow(model, "top_p") ? chat.TopP : null,
                MaxTokens = Allow(model, "max_tokens") ? ClampMaxTokens(chat.MaxTokens, model) : null,
                Stream = model?.SupportsStreaming ?? true,
            };

            return Preparation.Ready(provider, request, context.EstimatedTokens);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to prepare a chat turn for conversation {ConversationId}.", conversationId);
            return Preparation.Failed(ProviderErrorMapper.FromException(ex, providerId, providerId));
        }
    }

    private static bool Allow(ModelInfo? model, string parameter) => model?.Supports(parameter) ?? true;

    /// <summary>Keeps a global max-tokens setting from exceeding what a given model allows.</summary>
    private static int? ClampMaxTokens(int? configured, ModelInfo? model)
    {
        if (configured is not { } value || value <= 0)
        {
            return null;
        }

        return model?.MaxOutputTokens is { } limit && value > limit ? limit : value;
    }

    private async Task PersistCancellationAsync(Guid messageId, string content, Stopwatch stopwatch)
    {
        // CancellationToken.None on purpose: the token that just fired is the reason we
        // are here, and the partial answer still has to be saved.
        await _conversations.UpdateMessageAsync(
            new MessageUpdate
            {
                MessageId = messageId,
                Content = content,
                Status = MessageStatus.Cancelled,
                GenerationTimeMs = (int)stopwatch.ElapsedMilliseconds,
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task PersistFailureAsync(
        Guid messageId,
        string partialContent,
        AIProviderException failure,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Chat turn failed: {Kind} from provider {ProviderId}.",
            failure.Kind, failure.ProviderId ?? "unknown");

        await _conversations.UpdateMessageAsync(
            new MessageUpdate
            {
                MessageId = messageId,
                Content = partialContent,
                Status = MessageStatus.Failed,
                ErrorKind = failure.Kind,
                ErrorMessage = failure.UserMessage,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> TryAutoTitleAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        try
        {
            return await _conversations.TryApplyAutoTitleAsync(conversationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A title is cosmetic; never let it break the turn.
            _logger.LogWarning(ex, "Auto-titling failed for conversation {ConversationId}.", conversationId);
            return null;
        }
    }

    /// <summary>Outcome of the pre-stream setup: either a ready request or a failure to report.</summary>
    private sealed record Preparation(
        IAIProvider? Provider,
        AIChatRequest? Request,
        int EstimatedInputTokens,
        AIProviderException? Failure)
    {
        public static Preparation Ready(IAIProvider provider, AIChatRequest request, int estimatedTokens)
            => new(provider, request, estimatedTokens, null);

        public static Preparation Failed(AIProviderException failure)
            => new(null, null, 0, failure);
    }
}
