using System.Collections.ObjectModel;
using System.Windows.Threading;
using AIClient.App.Services;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIClient.App.ViewModels;

/// <summary>
/// The session list: recent chats, search, rename, pin and delete.
/// </summary>
/// <remarks>
/// Loads a page at a time rather than the whole table (section 27). Search is debounced,
/// because issuing a LIKE query per keystroke over every message body is exactly the kind
/// of thing that makes a chat app feel slow once there is real history in it.
/// </remarks>
public sealed partial class SessionListViewModel : ObservableObject
{
    private const int PageSize = 50;

    /// <summary>Long enough to skip the intermediate states of a typed word, short enough to feel live.</summary>
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(220);

    private readonly IConversationService _conversations;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly ILogger<SessionListViewModel> _logger;
    private readonly DispatcherTimer _searchTimer;

    private CancellationTokenSource? _searchCancellation;
    private bool _hasMore = true;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private ConversationSummary? _selectedSession;

    /// <summary>Id of the chat currently open, so the row can be highlighted without owning selection.</summary>
    [ObservableProperty]
    private Guid? _activeConversationId;

    public SessionListViewModel(
        IConversationService conversations,
        ISettingsService settings,
        IDialogService dialogs,
        ILogger<SessionListViewModel> logger)
    {
        _conversations = conversations;
        _settings = settings;
        _dialogs = dialogs;
        _logger = logger;

        _searchTimer = new DispatcherTimer { Interval = SearchDebounce };
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await RunSearchAsync().ConfigureAwait(true);
        };

        Sessions.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(EmptyMessage));
        };
    }

    public ObservableCollection<ConversationSummary> Sessions { get; } = [];

    public bool IsEmpty => Sessions.Count == 0 && !IsLoading;

    public bool IsSearching => SearchQuery.Trim().Length > 0;

    /// <summary>
    /// What to show in place of the list. "No results" and "no chats yet" call for different
    /// wording - the first means try another query, the second means start one.
    /// </summary>
    public string EmptyMessage => IsSearching
        ? $"No chats match “{SearchQuery.Trim()}”."
        : "No chats yet.\nStart one with New Chat.";

    /// <summary>Raised when a row is chosen, so the shell can open it in the chat pane.</summary>
    public event EventHandler<Guid>? SessionOpened;

    /// <summary>Raised when the open chat is deleted, so the shell can reset the pane.</summary>
    public event EventHandler<Guid>? SessionDeleted;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;

        try
        {
            var page = await _conversations.GetSummariesAsync(0, PageSize, cancellationToken).ConfigureAwait(true);

            Sessions.Clear();
            foreach (var session in page)
            {
                Sessions.Add(session);
            }

            _hasMore = page.Count == PageSize;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>Appends the next page. Called when the list is scrolled to the bottom.</summary>
    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (!_hasMore || IsLoading || IsSearching)
        {
            return;
        }

        IsLoading = true;

        try
        {
            var page = await _conversations.GetSummariesAsync(Sessions.Count, PageSize).ConfigureAwait(true);

            foreach (var session in page)
            {
                Sessions.Add(session);
            }

            _hasMore = page.Count == PageSize;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Open(ConversationSummary? session)
    {
        // The list raises this from SelectionChanged, which also fires when a reload
        // restores the selection. Reopening the chat that is already on screen would
        // discard an unsent draft for no reason.
        if (session is not null && session.Id != ActiveConversationId)
        {
            SessionOpened?.Invoke(this, session.Id);
        }
    }

    [RelayCommand]
    private async Task RenameAsync(ConversationSummary? session)
    {
        if (session is null)
        {
            return;
        }

        var newTitle = PromptForTitle(session.Title);

        if (string.IsNullOrWhiteSpace(newTitle) || newTitle == session.Title)
        {
            return;
        }

        await _conversations.RenameAsync(session.Id, newTitle.Trim()).ConfigureAwait(true);

        ReplaceRow(session, session with { Title = newTitle.Trim() });
    }

    [RelayCommand]
    private async Task DeleteAsync(ConversationSummary? session)
    {
        if (session is null)
        {
            return;
        }

        if (_settings.Current.General.ConfirmBeforeDelete)
        {
            var confirmed = await _dialogs.ConfirmAsync(
                "Delete chat",
                $"\"{session.Title}\" and all of its messages will be permanently deleted.").ConfigureAwait(true);

            if (!confirmed)
            {
                return;
            }
        }

        await _conversations.DeleteAsync(session.Id).ConfigureAwait(true);

        Sessions.Remove(session);
        _logger.LogInformation("Conversation deleted.");

        SessionDeleted?.Invoke(this, session.Id);
    }

    [RelayCommand]
    private async Task TogglePinAsync(ConversationSummary? session)
    {
        if (session is null)
        {
            return;
        }

        var pinned = !session.IsPinned;
        await _conversations.SetPinnedAsync(session.Id, pinned).ConfigureAwait(true);

        // Pinning reorders the list, so a reload is simpler and cheaper than resorting here.
        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void ClearSearch() => SearchQuery = string.Empty;

    /// <summary>Inserts or updates a row after a chat is created or auto-titled.</summary>
    public async Task RefreshRowAsync(Guid conversationId)
    {
        var existing = Sessions.FirstOrDefault(s => s.Id == conversationId);
        var page = await _conversations.GetSummariesAsync(0, PageSize).ConfigureAwait(true);
        var updated = page.FirstOrDefault(s => s.Id == conversationId);

        if (updated is null)
        {
            return;
        }

        if (existing is null)
        {
            // A new chat belongs at the top, below any pinned rows.
            var insertAt = Sessions.TakeWhile(s => s.IsPinned).Count();
            Sessions.Insert(insertAt, updated);
        }
        else
        {
            ReplaceRow(existing, updated);
        }
    }

    private void ReplaceRow(ConversationSummary old, ConversationSummary updated)
    {
        var index = Sessions.IndexOf(old);

        if (index >= 0)
        {
            Sessions[index] = updated;
        }
    }

    private async Task RunSearchAsync()
    {
        var query = SearchQuery.Trim();

        // Each keystroke supersedes the one before it; an in-flight query for a stale prefix
        // is wasted work and can arrive out of order.
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();

        var token = _searchCancellation.Token;

        try
        {
            if (query.Length == 0)
            {
                await LoadAsync(token).ConfigureAwait(true);
                return;
            }

            IsLoading = true;

            var results = await _conversations.SearchAsync(query, PageSize, token).ConfigureAwait(true);

            if (token.IsCancellationRequested)
            {
                return;
            }

            Sessions.Clear();
            foreach (var result in results)
            {
                Sessions.Add(result);
            }

            _hasMore = false;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke.
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    private static string? PromptForTitle(string currentTitle)
    {
        var dialog = new Views.Dialogs.RenameDialog(currentTitle);
        return dialog.ShowDialog() == true ? dialog.ChatTitle : null;
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    /// <summary>Keeps the highlighted row in step when a chat is opened from elsewhere.</summary>
    partial void OnActiveConversationIdChanged(Guid? value) =>
        SelectedSession = value is null
            ? null
            : Sessions.FirstOrDefault(s => s.Id == value);

    partial void OnSearchQueryChanged(string value)
    {
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(EmptyMessage));

        _searchTimer.Stop();
        _searchTimer.Start();
    }
}
