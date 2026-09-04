using System.Collections.ObjectModel;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Domain.Enums;
using AIClient.Domain.Graph;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIClient.Avalonia.ViewModels;

/// <summary>
/// The chat surface of the first Avalonia phase: sessions, streaming answers, and the graph
/// selection riding along with a question.
/// </summary>
/// <remarks>
/// <para>
/// A compact surface bound to the existing <see cref="IChatService"/> pipeline, not a port of
/// the full WPF <c>ChatViewModel</c>: no attachments, no agent tool cards, no markdown
/// renderer yet - those are the Phase 5 parity items. Everything it does show comes through
/// the same service calls the WPF app uses, so a conversation started in either shell reads
/// the same rows in the same database.
/// </para>
/// <para>
/// The graph selection is held, never sent on its own: <see cref="AskAboutGraphAsync"/>
/// stores what was selected and - when a prompt came with it - sends. The selection travels
/// on <see cref="SendMessageRequest.Selection"/> and becomes context inside the ordinary
/// context build, exactly as it does in the WPF shell.
/// </para>
/// </remarks>
public sealed partial class ChatPaneViewModel : ObservableObject
{
    private readonly IConversationService _conversations;
    private readonly IChatService _chat;
    private readonly IProviderRegistry _providers;
    private readonly ISettingsService _settings;
    private readonly ILogger<ChatPaneViewModel> _logger;

    private Guid? _conversationId;
    private GraphSelection? _pendingSelection;
    private CancellationTokenSource? _turn;

    public ChatPaneViewModel(
        IConversationService conversations,
        IChatService chat,
        IProviderRegistry providers,
        ISettingsService settings,
        ILogger<ChatPaneViewModel> logger)
    {
        _conversations = conversations;
        _chat = chat;
        _providers = providers;
        _settings = settings;
        _logger = logger;

        _providers.ModelsChanged += (_, _) => UiThreadAvalonia.Post(async () => await LoadModelsAsync());
    }

    public ObservableCollection<SessionRow> Sessions { get; } = [];

    public ObservableCollection<MessageRow> Messages { get; } = [];

    public ObservableCollection<ModelInfo> Models { get; } = [];

    [ObservableProperty]
    private SessionRow? _selectedSession;

    [ObservableProperty]
    private ModelInfo? _selectedModel;

    [ObservableProperty]
    private string _draft = string.Empty;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private string? _notice;

    /// <summary>What the canvas put on the way, until the next message takes it.</summary>
    [ObservableProperty]
    private string? _selectionChip;

    /// <summary>Raised when a held selection wants the composer's attention.</summary>
    public event EventHandler? FocusInputRequested;

    public bool CanSend => !IsStreaming && !string.IsNullOrWhiteSpace(Draft) && SelectedModel is not null;

    public async Task ActivateAsync()
    {
        if (Sessions.Count == 0 && Messages.Count == 0)
        {
            await RefreshSessionsAsync();
            await LoadModelsAsync();

            // The most recent chat opens so the app resumes rather than greets.
            if (Sessions.Count > 0)
            {
                SelectedSession = Sessions[0];
            }
        }
    }

    public async Task RefreshSessionsAsync()
    {
        try
        {
            var summaries = string.IsNullOrWhiteSpace(SearchQuery)
                ? await _conversations.GetSummariesAsync(0, 50)
                : await _conversations.SearchAsync(SearchQuery, 50);

            Sessions.Clear();

            foreach (var summary in summaries)
            {
                Sessions.Add(new SessionRow(summary.Id, summary.Title, summary.UpdatedAt));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sessions could not be read.");
            Notice = "Sessions could not be read.";
        }
    }

    public async Task LoadModelsAsync()
    {
        try
        {
            var models = await _providers.GetAllModelsAsync();

            var previous = SelectedModel?.ModelId;

            Models.Clear();

            foreach (var model in models)
            {
                Models.Add(model);
            }

            SelectedModel =
                Models.FirstOrDefault(m => m.ModelId == previous) ??
                Models.FirstOrDefault(m =>
                    m.ProviderId == _settings.Current.Chat.DefaultProviderId &&
                    m.ModelId == _settings.Current.Chat.DefaultModelId) ??
                Models.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The model catalogue could not be read.");
        }
    }

    partial void OnSelectedSessionChanged(SessionRow? value)
    {
        if (value is not null)
        {
            _ = OpenAsync(value);
        }
    }

    partial void OnDraftChanged(string value) => OnPropertyChanged(nameof(CanSend));

    partial void OnSelectedModelChanged(ModelInfo? value) => OnPropertyChanged(nameof(CanSend));

    partial void OnIsStreamingChanged(bool value) => OnPropertyChanged(nameof(CanSend));

    /// <summary>Creates a fresh conversation and clears the transcript.</summary>
    [RelayCommand]
    public async Task NewChatAsync()
    {
        _turn?.Cancel();
        _pendingSelection = null;
        SelectionChip = null;

        var created = await _conversations.CreateAsync();

        _conversationId = created.Id;
        Messages.Clear();
        SelectedSession = null;

        await RefreshSessionsAsync();

        FocusInputRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task DeleteSessionAsync(SessionRow? session)
    {
        if (session is null)
        {
            return;
        }

        await _conversations.DeleteAsync(session.Id);

        if (_conversationId == session.Id)
        {
            _conversationId = null;
            Messages.Clear();
        }

        await RefreshSessionsAsync();
    }

    /// <summary>Opens a conversation and reads its messages.</summary>
    private async Task OpenAsync(SessionRow session)
    {
        if (_conversationId == session.Id && Messages.Count > 0)
        {
            return;
        }

        try
        {
            var detail = await _conversations.GetAsync(session.Id);

            _conversationId = session.Id;
            Messages.Clear();

            if (detail is null)
            {
                return;
            }

            foreach (var message in detail.Messages)
            {
                Messages.Add(new MessageRow(message.Role, message.Content));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A conversation could not be opened.");
            Notice = "That conversation could not be opened.";
        }
    }

    /// <summary>Holds a canvas selection for the next message, with an optional ready question.</summary>
    public void AskAboutGraphAsync(GraphSelection selection, string prompt, string label)
    {
        _pendingSelection = selection;
        SelectionChip = $"Context: {label}";

        if (string.IsNullOrWhiteSpace(prompt))
        {
            // The person is going to type their own question; put the cursor there.
            FocusInputRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        Draft = prompt;
        _ = SendAsync();
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (IsStreaming || string.IsNullOrWhiteSpace(Draft))
        {
            return;
        }

        var model = SelectedModel;
        if (model is null)
        {
            Notice = "No model is available. Add a provider key in Settings, then refresh the models.";
            return;
        }

        Notice = null;

        if (_conversationId is null)
        {
            var created = await _conversations.CreateAsync();
            _conversationId = created.Id;
        }

        var conversationId = _conversationId.Value;
        var content = Draft.Trim();
        var selection = _pendingSelection;

        Draft = string.Empty;
        _pendingSelection = null;
        SelectionChip = null;

        var user = new MessageRow(MessageRole.User, content);
        Messages.Add(user);

        var answer = new MessageRow(MessageRole.Assistant, string.Empty) { IsStreaming = true };
        Messages.Add(answer);

        _turn = new CancellationTokenSource();

        IsStreaming = true;

        try
        {
            var request = new SendMessageRequest
            {
                ConversationId = conversationId,
                Content = content,
                ProviderId = model.ProviderId,
                ModelId = model.ModelId,
                Selection = selection,
            };

            await foreach (var turn in _chat.SendMessageAsync(request, _turn.Token))
            {
                switch (turn)
                {
                    case ChatTurnEvent.UserMessageSaved:
                        break;

                    case ChatTurnEvent.TitleGenerated(_, var title):
                        var row = Sessions.FirstOrDefault(s => s.Id == conversationId);
                        if (row is not null)
                        {
                            row.Title = title;
                        }

                        break;

                    case ChatTurnEvent.ContentDelta(_, var delta):
                        answer.Append(delta);
                        break;

                    case ChatTurnEvent.Completed:
                        answer.IsStreaming = false;
                        break;

                    case ChatTurnEvent.Failed failed:
                        answer.IsStreaming = false;
                        answer.StatusText = failed.UserMessage;
                        Notice = failed.UserMessage;
                        break;

                    case ChatTurnEvent.Cancelled:
                        answer.IsStreaming = false;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            answer.IsStreaming = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sending a message failed.");
            answer.IsStreaming = false;
            Notice = "The message could not be sent.";
        }
        finally
        {
            IsStreaming = false;
            _turn?.Dispose();
            _turn = null;

            // The preview line in the sidebar is worth one cheap reload per turn.
            await RefreshSessionsAsync();
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _turn?.Cancel();
    }

    partial void OnSearchQueryChanged(string value) => _ = RefreshSessionsAsync();

    /// <summary>The Avalonia dispatcher hop, named apart from the canvas view model's usage.</summary>
    private static class UiThreadAvalonia
    {
        public static void Post(Action action) =>
            global::Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }

    public sealed partial class SessionRow : ObservableObject
    {
        public SessionRow(Guid id, string title, DateTimeOffset updatedAt)
        {
            Id = id;
            _title = title;
            UpdatedAt = updatedAt;
        }

        public Guid Id { get; }

        [ObservableProperty]
        private string _title;

        public DateTimeOffset UpdatedAt { get; }

        public string When => UpdatedAt.LocalDateTime.ToString("MMM d");
    }

    public sealed partial class MessageRow : ObservableObject
    {
        public MessageRow(MessageRole role, string content)
        {
            Role = role;
            _content = content;
        }

        public MessageRole Role { get; }

        public bool IsUser => Role == MessageRole.User;

        [ObservableProperty]
        private string _content;

        [ObservableProperty]
        private bool _isStreaming;

        [ObservableProperty]
        private string? _statusText;

        public void Append(string delta) => Content += delta;
    }
}
