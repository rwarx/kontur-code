using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using AIClient.App.Services;
using AIClient.Application.Configuration;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Application.Markdown;
using AIClient.Domain.Enums;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIClient.App.ViewModels;

/// <summary>
/// The conversation pane: composing, sending, streaming, and the per-message actions.
/// </summary>
/// <remarks>
/// This class talks to <see cref="IChatService"/> and nothing else. It has no idea that
/// OpenRouter or NVIDIA exist, never sees an HTTP client, and never touches the database -
/// which is what section 7 asks for and what makes adding a provider a change in one
/// assembly instead of three.
///
/// Two details that are easy to get wrong and matter a great deal here:
/// <list type="bullet">
/// <item><description>
/// Markdown is re-rendered on a 60 ms timer, not on every token. Re-parsing per token turns
/// a fast model into a slideshow.
/// </description></item>
/// <item><description>
/// The streaming loop runs on the caller's context so <see cref="MessageViewModel"/> updates
/// land on the dispatcher. The work that blocks - HTTP and SQLite - is already async inside
/// the service, so the UI thread is never the thing waiting.
/// </description></item>
/// </list>
/// </remarks>
public sealed partial class ChatViewModel : ObservableObject
{
    /// <summary>
    /// Markdown re-render cadence. Fast enough to look continuous, slow enough that a
    /// 100-tokens-per-second stream costs about 16 parses a second rather than 100.
    /// </summary>
    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(60);

    private readonly IChatService _chatService;
    private readonly IConversationService _conversations;
    private readonly IAttachmentService _attachments;
    private readonly IExportService _exportService;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly IConnectivityMonitor _connectivity;
    private readonly MarkdownParser _markdownParser;
    private readonly ILogger<ChatViewModel> _logger;
    private readonly DispatcherTimer _renderTimer;

    private CancellationTokenSource? _turnCancellation;
    private MessageViewModel? _streamingMessage;

    [ObservableProperty]
    private Guid? _conversationId;

    [ObservableProperty]
    private string _title = "New Chat";

    [ObservableProperty]
    private string _draft = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isGenerating;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private ModelInfo? _selectedModel;

    [ObservableProperty]
    private bool _autoScroll = true;

    /// <summary>Set when a turn fails before any message exists to attach the error to.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBannerOpen))]
    private string? _bannerMessage;

    /// <summary>
    /// Two-way companion to <see cref="BannerMessage"/> for the notice bar.
    /// </summary>
    /// <remarks>
    /// The bar has no closed event: its dismiss button writes <c>IsOpen = false</c> straight
    /// back through the binding. Setting the message to null from here keeps that one gesture
    /// from leaving a dismissed bar that reopens the next time the property is re-read.
    /// </remarks>
    public bool IsBannerOpen
    {
        get => BannerMessage is { Length: > 0 };
        set
        {
            if (!value)
            {
                BannerMessage = null;
            }
        }
    }

    /// <summary>
    /// True when the user has scrolled away from the tail. Auto-scroll is suppressed while
    /// it is set, so reading earlier output is not interrupted by an arriving token.
    /// </summary>
    [ObservableProperty]
    private bool _isScrolledAway;

    /// <summary>Mirrors the chat setting so the view can route Enter without reading settings.</summary>
    [ObservableProperty]
    private bool _sendWithEnter = true;

    /// <summary>Mirrors the chat setting; inherited by every code block in the transcript.</summary>
    [ObservableProperty]
    private bool _highlightCode = true;

    /// <summary>Hint under the composer, which changes with the Enter/Shift+Enter setting.</summary>
    [ObservableProperty]
    private string _inputHint = string.Empty;

    public ChatViewModel(
        IChatService chatService,
        IConversationService conversations,
        IAttachmentService attachments,
        IExportService exportService,
        ISettingsService settings,
        IDialogService dialogs,
        IConnectivityMonitor connectivity,
        MarkdownParser markdownParser,
        ILogger<ChatViewModel> logger)
    {
        _chatService = chatService;
        _conversations = conversations;
        _attachments = attachments;
        _exportService = exportService;
        _settings = settings;
        _dialogs = dialogs;
        _connectivity = connectivity;
        _markdownParser = markdownParser;
        _logger = logger;

        _renderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RenderInterval };
        _renderTimer.Tick += OnRenderTick;

        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));

        ApplyRenderingSettings();
    }

    public ObservableCollection<MessageViewModel> Messages { get; } = [];

    /// <summary>Files staged for the next message. Shown as removable chips above the input.</summary>
    public ObservableCollection<PendingAttachment> PendingAttachments { get; } = [];

    /// <summary>
    /// Starter prompts for the empty state (section 33). Fixed rather than generated: four
    /// predictable entries are more useful than a rotating set nobody can rely on.
    /// </summary>
    public IReadOnlyList<ChatSuggestion> Suggestions { get; } =
    [
        new("Explain code", "Walk me through what a snippet does", "Explain what this code does, step by step:\n\n"),
        new("Write a function", "Generate an implementation from a description", "Write a function that "),
        new("Analyse a file", "Attach a file and ask about it", "Review the attached file and summarise what it does."),
        new("Help me debug", "Work through an error message", "I'm getting this error and I can't work out why:\n\n"),
    ];

    /// <summary>Drives the "What can I help you with?" state instead of an empty grey panel.</summary>
    public bool IsEmpty => Messages.Count == 0;

    public bool CanSend => !IsGenerating && (Draft.Trim().Length > 0 || PendingAttachments.Count > 0);

    /// <summary>Raised when the transcript grows, so the view can scroll to the end.</summary>
    public event EventHandler? ScrollToEndRequested;

    /// <summary>Raised when the composer should take focus, after a new chat or a suggestion.</summary>
    public event EventHandler? FocusInputRequested;

    /// <summary>Raised when a title is generated, so the sidebar can update its row.</summary>
    public event EventHandler<ConversationTitleChangedEventArgs>? TitleChanged;

    /// <summary>Asks the view to put the caret in the composer.</summary>
    public void FocusInput() => FocusInputRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Loads a stored conversation into the pane.</summary>
    public async Task LoadConversationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        IsLoading = true;

        try
        {
            var detail = await _conversations.GetAsync(id, cancellationToken).ConfigureAwait(true);

            if (detail is null)
            {
                _logger.LogWarning("Conversation {Id} no longer exists.", id);
                StartNewConversation();
                return;
            }

            ConversationId = detail.Id;
            Title = detail.Title;
            BannerMessage = null;

            var renderMarkdown = _settings.Current.Chat.RenderMarkdown;

            Messages.Clear();
            foreach (var message in detail.Messages)
            {
                Messages.Add(new MessageViewModel(message, renderMarkdown, _markdownParser));
            }

            ClearPendingAttachments();
            RequestScrollToEnd();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Resets the pane to an unsaved new chat. The row is created on first send.</summary>
    public void StartNewConversation()
    {
        CancelTurn();

        ConversationId = null;
        Title = "New Chat";
        BannerMessage = null;

        Messages.Clear();
        ClearPendingAttachments();
        Draft = string.Empty;
        IsScrolledAway = false;

        FocusInputRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops a starter prompt into the composer so the user can finish the sentence.</summary>
    [RelayCommand]
    private void UseSuggestion(ChatSuggestion? suggestion)
    {
        if (suggestion is null)
        {
            return;
        }

        Draft = suggestion.Prompt;
        FocusInputRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Draft.Trim();

        if (text.Length == 0 && PendingAttachments.Count == 0)
        {
            return;
        }

        if (SelectedModel is not { } model)
        {
            BannerMessage = "Choose a model before sending a message.";
            return;
        }

        // Cleared immediately: the message is already committed from the user's point of
        // view, and leaving it in the box invites an accidental double send.
        Draft = string.Empty;
        BannerMessage = null;

        var attachments = PendingAttachments.Select(a => a.Attachment).ToList();
        ClearPendingAttachments();

        var conversationId = await EnsureConversationAsync(model).ConfigureAwait(true);

        var request = new SendMessageRequest
        {
            ConversationId = conversationId,
            Content = text,
            ProviderId = model.ProviderId,
            ModelId = model.ModelId,
            Attachments = attachments,
        };

        await RunTurnAsync(token => _chatService.SendMessageAsync(request, token)).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(IsGenerating))]
    private void Stop()
    {
        _logger.LogInformation("Generation stopped by the user.");
        CancelTurn();
    }

    /// <summary>Re-answers an assistant message, discarding it and anything after it.</summary>
    [RelayCommand]
    private async Task RegenerateAsync(MessageViewModel? message)
    {
        if (message is null || IsGenerating || ConversationId is not { } conversationId)
        {
            return;
        }

        if (SelectedModel is not { } model)
        {
            BannerMessage = "Choose a model before regenerating.";
            return;
        }

        // The service deletes from this message onward; the UI has to match that view of
        // history or the two will disagree about what the model was shown.
        var index = Messages.IndexOf(message);
        if (index >= 0)
        {
            while (Messages.Count > index)
            {
                Messages.RemoveAt(Messages.Count - 1);
            }
        }

        var request = new RegenerateRequest
        {
            ConversationId = conversationId,
            AssistantMessageId = message.Id,
            ProviderId = model.ProviderId,
            ModelId = model.ModelId,
        };

        await RunTurnAsync(token => _chatService.RegenerateAsync(request, token)).ConfigureAwait(true);
    }

    /// <summary>Retries the failed turn that produced this message.</summary>
    [RelayCommand]
    private Task RetryAsync(MessageViewModel? message) => RegenerateAsync(message);

    [RelayCommand]
    private void CopyMessage(MessageViewModel? message)
    {
        if (message is not null)
        {
            _dialogs.CopyToClipboard(message.Content);
        }
    }

    [RelayCommand]
    private static void CopyText(string? text)
    {
        // Static because the code-block Copy button passes its own text; nothing else is needed.
        if (!string.IsNullOrEmpty(text))
        {
            System.Windows.Clipboard.SetText(text);
        }
    }

    [RelayCommand]
    private static void BeginEdit(MessageViewModel? message)
    {
        if (message is { IsUser: true })
        {
            message.EditDraft = message.Content;
            message.IsEditing = true;
        }
    }

    [RelayCommand]
    private static void CancelEdit(MessageViewModel? message)
    {
        if (message is not null)
        {
            message.IsEditing = false;
            message.EditDraft = string.Empty;
        }
    }

    /// <summary>
    /// Saves an edited user message and re-answers from that point.
    /// </summary>
    /// <remarks>
    /// Everything after the edited message is discarded. Keeping the old answers would leave
    /// a transcript where the model appears to have replied to a question it never saw.
    /// </remarks>
    [RelayCommand]
    private async Task SaveEditAsync(MessageViewModel? message)
    {
        if (message is not { IsUser: true } || IsGenerating || ConversationId is not { } conversationId)
        {
            return;
        }

        var edited = message.EditDraft.Trim();

        if (edited.Length == 0 || edited == message.Content)
        {
            message.IsEditing = false;
            return;
        }

        if (SelectedModel is not { } model)
        {
            BannerMessage = "Choose a model before editing.";
            return;
        }

        message.IsEditing = false;

        await _conversations.UpdateMessageAsync(new MessageUpdate
        {
            MessageId = message.Id,
            Content = edited,
        }).ConfigureAwait(true);

        message.ReplaceContent(edited);

        // Drop the replies that followed the old wording, in the UI and in the database.
        await _conversations.DeleteFromMessageAsync(message.Id, inclusive: false).ConfigureAwait(true);

        var index = Messages.IndexOf(message);
        while (index >= 0 && Messages.Count > index + 1)
        {
            Messages.RemoveAt(Messages.Count - 1);
        }

        var detail = await _conversations.GetAsync(conversationId).ConfigureAwait(true);
        var lastAssistant = detail?.Messages.LastOrDefault(m => m.Role == MessageRole.Assistant);

        var request = new RegenerateRequest
        {
            ConversationId = conversationId,
            AssistantMessageId = lastAssistant?.Id ?? Guid.Empty,
            ProviderId = model.ProviderId,
            ModelId = model.ModelId,
        };

        await RunTurnAsync(token => _chatService.RegenerateAsync(request, token)).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteMessageAsync(MessageViewModel? message)
    {
        if (message is null || IsGenerating)
        {
            return;
        }

        if (_settings.Current.General.ConfirmBeforeDelete)
        {
            var confirmed = await _dialogs.ConfirmAsync(
                "Delete message",
                "This message will be removed from the conversation.").ConfigureAwait(true);

            if (!confirmed)
            {
                return;
            }
        }

        await _conversations.DeleteMessageAsync(message.Id).ConfigureAwait(true);
        Messages.Remove(message);
    }

    [RelayCommand]
    private async Task AttachFilesAsync()
    {
        var paths = _dialogs.OpenFiles(_attachments.BuildFileDialogFilter());

        foreach (var path in paths)
        {
            var result = await _attachments.LoadAsync(path).ConfigureAwait(true);

            if (!result.Success || result.Attachment is null)
            {
                BannerMessage = result.ErrorMessage;
                continue;
            }

            PendingAttachments.Add(new PendingAttachment(result.Attachment));
        }

        OnPropertyChanged(nameof(CanSend));
        SendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Accepts files dropped onto the input area.</summary>
    public async Task AddDroppedFilesAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!_attachments.IsSupported(path))
            {
                BannerMessage = $"'{Path.GetFileName(path)}' is not a supported file type.";
                continue;
            }

            var result = await _attachments.LoadAsync(path).ConfigureAwait(true);

            if (!result.Success || result.Attachment is null)
            {
                BannerMessage = result.ErrorMessage;
                continue;
            }

            PendingAttachments.Add(new PendingAttachment(result.Attachment));
        }

        OnPropertyChanged(nameof(CanSend));
        SendCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RemoveAttachment(PendingAttachment? attachment)
    {
        if (attachment is not null)
        {
            PendingAttachments.Remove(attachment);
            OnPropertyChanged(nameof(CanSend));
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Writes the conversation to disk in the chosen format.</summary>
    public async Task ExportAsync(ExportFormat format)
    {
        if (ConversationId is not { } id)
        {
            return;
        }

        var detail = await _conversations.GetAsync(id).ConfigureAwait(true);

        if (detail is null)
        {
            return;
        }

        var path = _dialogs.SaveFile(
            _exportService.GetFileDialogFilter(format),
            _exportService.SuggestFileName(detail, format));

        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, _exportService.Export(detail, format)).ConfigureAwait(true);
            BannerMessage = $"Exported to {Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Export failed.");
            await _dialogs.ShowErrorAsync("Export failed", ex.Message).ConfigureAwait(true);
        }
    }

    /// <summary>Re-reads the chat settings that affect how the pane behaves and renders.</summary>
    public void ApplyRenderingSettings()
    {
        var chat = _settings.Current.Chat;

        foreach (var message in Messages)
        {
            message.SetMarkdownRendering(chat.RenderMarkdown);
        }

        AutoScroll = chat.AutoScroll;
        HighlightCode = chat.HighlightCode;
        SendWithEnter = chat.SendWithEnter;

        InputHint = chat.SendWithEnter
            ? "Enter to send · Shift+Enter for a new line"
            : "Shift+Enter to send · Enter for a new line";
    }

    /// <summary>
    /// Runs one turn, translating <see cref="ChatTurnEvent"/>s into UI state.
    /// </summary>
    /// <remarks>
    /// Shared by Send, Regenerate and Retry: all three differ only in which service call
    /// produces the event stream, and duplicating this loop three times would mean three
    /// places to get cancellation and error handling subtly wrong.
    /// </remarks>
    private async Task RunTurnAsync(Func<CancellationToken, IAsyncEnumerable<ChatTurnEvent>> start)
    {
        CancelTurn();

        _turnCancellation = new CancellationTokenSource();
        var token = _turnCancellation.Token;

        IsGenerating = true;
        _renderTimer.Start();

        var renderMarkdown = _settings.Current.Chat.RenderMarkdown;

        try
        {
            await foreach (var evt in start(token).WithCancellation(token).ConfigureAwait(true))
            {
                switch (evt)
                {
                    case ChatTurnEvent.UserMessageSaved(var dto):
                        Messages.Add(new MessageViewModel(dto, renderMarkdown, _markdownParser));
                        RequestScrollToEnd();
                        break;

                    case ChatTurnEvent.AssistantMessageStarted(var dto):
                        _streamingMessage = new MessageViewModel(dto, renderMarkdown, _markdownParser);
                        Messages.Add(_streamingMessage);
                        RequestScrollToEnd();
                        break;

                    case ChatTurnEvent.ContentDelta(_, var text):
                        _streamingMessage?.AppendDelta(text);
                        break;

                    case ChatTurnEvent.Completed(_, var input, var output, var elapsed):
                        _streamingMessage?.Complete(input, output, elapsed);

                        // A finished answer is the strongest proof there is that the provider
                        // is reachable, which clears the offline strip on a network the OS
                        // considered healthy all along (a captive portal, most often).
                        _connectivity.ReportReachable();
                        break;

                    case ChatTurnEvent.Failed(_, var kind, var userMessage, var details, var retryable):
                        HandleFailure(kind, userMessage, details, retryable);
                        break;

                    case ChatTurnEvent.Cancelled:
                        _streamingMessage?.Cancel();
                        break;

                    case ChatTurnEvent.TitleGenerated(var id, var title):
                        Title = title;
                        TitleChanged?.Invoke(this, new ConversationTitleChangedEventArgs(id, title));
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the user pressed Stop. The service has already persisted whatever
            // text arrived before the cancellation.
            _streamingMessage?.Cancel();
        }
        catch (Exception ex)
        {
            // A bug rather than a provider failure - provider failures arrive as Failed events.
            _logger.LogError(ex, "Unexpected failure during a chat turn.");
            HandleFailure(AIErrorKind.Unknown, "Something went wrong while generating the response.", ex.Message, true);
        }
        finally
        {
            _renderTimer.Stop();

            // One final parse so the last tokens are rendered rather than left as raw text.
            _streamingMessage?.RebuildBlocks();
            _streamingMessage = null;

            IsGenerating = false;

            _turnCancellation?.Dispose();
            _turnCancellation = null;

            RequestScrollToEnd();
        }
    }

    private void HandleFailure(AIErrorKind kind, string userMessage, string? details, bool isRetryable)
    {
        if (_streamingMessage is not null)
        {
            _streamingMessage.Fail(userMessage, details, isRetryable);
        }
        else
        {
            // No message to attach the error to - a failure before the turn even started.
            BannerMessage = userMessage;
        }

        switch (ReachabilityEvidence(kind))
        {
            case true:
                _connectivity.ReportReachable();
                break;

            case false:
                _connectivity.ReportUnreachable();
                break;
        }

        _logger.LogWarning("Chat turn failed: {Kind}.", kind);
    }

    /// <summary>
    /// What a failure says about the connection, as opposed to what it says about the request.
    /// </summary>
    /// <returns>
    /// Null where the failure proves nothing either way, so the offline strip keeps whatever
    /// state it already had rather than guessing from ambiguous evidence.
    /// </returns>
    private static bool? ReachabilityEvidence(AIErrorKind kind) => kind switch
    {
        AIErrorKind.NetworkError => false,

        // The provider answered. It refused, but it answered, so the connection works.
        AIErrorKind.InvalidApiKey
            or AIErrorKind.PermissionDenied
            or AIErrorKind.NotFound
            or AIErrorKind.RateLimited
            or AIErrorKind.ServerError
            or AIErrorKind.ServiceUnavailable
            or AIErrorKind.ContextLengthExceeded
            or AIErrorKind.ModelUnavailable
            or AIErrorKind.InvalidRequest
            or AIErrorKind.ContentFiltered => true,

        // Timeout is genuinely ambiguous - a dead link and a slow model are indistinguishable
        // from here. NotConfigured and Cancelled never put a packet on the wire. Unknown is a
        // bug in this application, not a statement about the network.
        _ => null,
    };

    /// <summary>Creates the conversation row on first send, so an abandoned draft leaves nothing behind.</summary>
    private async Task<Guid> EnsureConversationAsync(ModelInfo model)
    {
        if (ConversationId is { } existing)
        {
            await _conversations.SetModelAsync(existing, model.ProviderId, model.ModelId).ConfigureAwait(true);
            return existing;
        }

        var created = await _conversations
            .CreateAsync(providerId: model.ProviderId, modelId: model.ModelId)
            .ConfigureAwait(true);

        ConversationId = created.Id;
        Title = created.Title;

        return created.Id;
    }

    private void OnRenderTick(object? sender, EventArgs e) => _streamingMessage?.RebuildBlocks();

    private void CancelTurn()
    {
        if (_turnCancellation is { IsCancellationRequested: false })
        {
            _turnCancellation.Cancel();
        }
    }

    private void ClearPendingAttachments()
    {
        PendingAttachments.Clear();
        OnPropertyChanged(nameof(CanSend));
        SendCommand.NotifyCanExecuteChanged();
    }

    private void RequestScrollToEnd()
    {
        if (AutoScroll)
        {
            ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    partial void OnDraftChanged(string value)
    {
        OnPropertyChanged(nameof(CanSend));
        SendCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedModelChanged(ModelInfo? value)
    {
        if (value is not null && ConversationId is { } id)
        {
            // Fire-and-forget is acceptable here: it records a preference, and a failure
            // costs the user nothing beyond re-picking the model next time.
            _ = _conversations.SetModelAsync(id, value.ProviderId, value.ModelId);
        }
    }
}

/// <summary>A starter prompt on the empty-state screen.</summary>
/// <param name="Prompt">Text placed in the composer, deliberately unfinished so the user continues it.</param>
public sealed record ChatSuggestion(string Title, string Description, string Prompt);

/// <summary>A file staged for the next message, with a display size for its chip.</summary>
public sealed class PendingAttachment
{
    public PendingAttachment(NewAttachment attachment)
    {
        Attachment = attachment;
    }

    public NewAttachment Attachment { get; }

    public string FileName => Attachment.FileName;

    public bool IsTruncated => Attachment.IsTruncated;

    /// <summary>Human-readable size for the chip label.</summary>
    public string DisplaySize => Attachment.Size switch
    {
        < 1024 => $"{Attachment.Size} B",
        < 1024 * 1024 => $"{Attachment.Size / 1024.0:0.#} KB",
        _ => $"{Attachment.Size / (1024.0 * 1024.0):0.#} MB",
    };
}

/// <summary>Announces an auto-generated title so the sidebar row can be updated in place.</summary>
public sealed class ConversationTitleChangedEventArgs : EventArgs
{
    public ConversationTitleChangedEventArgs(Guid conversationId, string title)
    {
        ConversationId = conversationId;
        Title = title;
    }

    public Guid ConversationId { get; }
    public string Title { get; }
}
