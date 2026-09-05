using AIClient.App.Services;
using AIClient.App.ViewModels.Canvas;
using AIClient.Application.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIClient.App.ViewModels;

/// <summary>Which pane the main area is showing.</summary>
public enum ShellPage
{
    Chat = 0,
    Settings = 1,

    /// <summary>The knowledge graph, spatially. A place to work, not a dialog.</summary>
    Canvas = 2,
}

/// <summary>
/// The shell: owns the sidebar, the chat pane and the settings pane, and routes between them.
/// </summary>
/// <remarks>
/// The child ViewModels do not know about each other. When a session is opened or a title is
/// generated, the event lands here and this class decides what else needs to change. Wiring
/// them directly would make the sidebar depend on the chat pane and vice versa.
/// </remarks>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IConversationService _conversations;
    private readonly ISettingsService _settings;
    private readonly IProviderRegistry _registry;
    private readonly IAppThemeService _themeService;
    private readonly ILocalizationService _localization;
    private readonly IConnectivityMonitor _connectivity;
    private readonly ILogger<MainViewModel> _logger;

    [ObservableProperty]
    private ShellPage _currentPage = ShellPage.Chat;

    [ObservableProperty]
    private bool _isSidebarVisible = true;

    [ObservableProperty]
    private bool _isCommandPaletteOpen;

    [ObservableProperty]
    private bool _isFirstRunVisible;

    /// <summary>Set while no provider can be reached, so the shell can show an offline strip.</summary>
    [ObservableProperty]
    private bool _isOffline;

    public MainViewModel(
        ChatViewModel chat,
        SessionListViewModel sessions,
        ModelPickerViewModel modelPicker,
        SettingsViewModel settings,
        CommandPaletteViewModel commandPalette,
        FirstRunViewModel firstRun,
        CanvasViewModel canvas,
        InspectorViewModel inspector,
        IConversationService conversations,
        ISettingsService settingsService,
        IProviderRegistry registry,
        IAppThemeService themeService,
        ILocalizationService localization,
        IConnectivityMonitor connectivity,
        ILogger<MainViewModel> logger)
    {
        Chat = chat;
        Sessions = sessions;
        ModelPicker = modelPicker;
        Settings = settings;
        CommandPalette = commandPalette;
        FirstRun = firstRun;
        Canvas = canvas;
        Inspector = inspector;

        _conversations = conversations;
        _settings = settingsService;
        _registry = registry;
        _themeService = themeService;
        _localization = localization;
        _connectivity = connectivity;
        _logger = logger;

        IsOffline = !connectivity.IsOnline;
        connectivity.ConnectivityChanged += OnConnectivityChanged;

        Sessions.SessionOpened += OnSessionOpened;
        Sessions.SessionDeleted += OnSessionDeleted;
        Chat.TitleChanged += OnChatTitleChanged;
        ModelPicker.ModelSelected += (_, model) => Chat.SelectedModel = model;
        Settings.SettingsApplied += OnSettingsApplied;
        CommandPalette.CommandInvoked += OnPaletteCommand;
        FirstRun.Finished += OnFirstRunFinished;

        // A language switch is written in words, and half of those words are computed in the
        // child ViewModels rather than bound from the string table, so each is told to rebuild.
        _localization.LanguageChanged += OnLanguageChanged;

        // The canvas and the inspector do not know about each other either. A selection made on the
        // canvas arrives here and is handed on; a related node clicked in the inspector comes back
        // the same way. Subscribed once, in the constructor, because both are singletons whose state
        // outlives a visit to the page.
        Canvas.SelectionChanged += (_, selection) => Inspector.Show(selection);
        Inspector.NodeActivated += (_, nodeId) => Canvas.Focus(nodeId);

        // The code panel is the canvas's, so the inspector asks for it the way it asks for a jump.
        // Nothing awaits the read: the panel shows its own progress and reports its own refusals.
        Inspector.CodeRequested += (_, nodeId) => Canvas.ShowCodeAsync(nodeId);

        // One handler for both surfaces, so there is exactly one path from a selection to a model.
        Canvas.AiRequested += OnGraphAiRequested;
        Inspector.AiRequested += OnGraphAiRequested;
    }

    public ChatViewModel Chat { get; }
    public SessionListViewModel Sessions { get; }
    public ModelPickerViewModel ModelPicker { get; }
    public SettingsViewModel Settings { get; }
    public CommandPaletteViewModel CommandPalette { get; }
    public FirstRunViewModel FirstRun { get; }
    public CanvasViewModel Canvas { get; }
    public InspectorViewModel Inspector { get; }

    public bool IsChatVisible => CurrentPage == ShellPage.Chat;
    public bool IsSettingsVisible => CurrentPage == ShellPage.Settings;
    public bool IsCanvasVisible => CurrentPage == ShellPage.Canvas;

    /// <summary>Runs the startup sequence once the window is up.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await Sessions.LoadAsync(cancellationToken).ConfigureAwait(true);
        await ModelPicker.LoadAsync(cancellationToken).ConfigureAwait(true);
        await Settings.LoadAsync(cancellationToken).ConfigureAwait(true);

        var general = _settings.Current.General;

        if (!general.HasCompletedFirstRun)
        {
            IsFirstRunVisible = true;
            return;
        }

        if (general.RestoreLastConversation && general.LastConversationId is { } lastId)
        {
            await OpenConversationAsync(lastId, cancellationToken).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void NewChat()
    {
        Chat.StartNewConversation();
        Sessions.ActiveConversationId = null;
        CurrentPage = ShellPage.Chat;
    }

    [RelayCommand]
    private void ShowSettings() => CurrentPage = ShellPage.Settings;

    [RelayCommand]
    private void ShowChat() => CurrentPage = ShellPage.Chat;

    /// <summary>
    /// Opens the canvas, loading the graph the first time.
    /// </summary>
    /// <remarks>
    /// Lazily, not at startup: someone who only ever chats should not pay for a graph read, and the
    /// load is idempotent, so every later visit is free.
    /// </remarks>
    [RelayCommand]
    private async Task ShowCanvasAsync()
    {
        CurrentPage = ShellPage.Canvas;

        try
        {
            await Canvas.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The canvas reports its own failures in the page; this is only the last resort.
            _logger.LogError(ex, "The canvas could not be opened.");
        }
    }

    /// <summary>
    /// Carries a question about a selection to the conversation.
    /// </summary>
    /// <remarks>
    /// The page changes first, so the answer is visible while it streams rather than arriving in a
    /// pane nobody is looking at. The selection stays on the canvas: coming back to it after reading
    /// the answer is the point of asking from there.
    /// </remarks>
    private async void OnGraphAiRequested(object? sender, CanvasAiRequest request)
    {
        CurrentPage = ShellPage.Chat;

        try
        {
            await Chat.AskAboutGraphAsync(request.Selection, request.Prompt, request.Label, request.Files)
                      .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The question about {Label} could not be sent.", request.Label);
        }
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    [RelayCommand]
    private void ToggleCommandPalette()
    {
        IsCommandPaletteOpen = !IsCommandPaletteOpen;

        if (IsCommandPaletteOpen)
        {
            CommandPalette.Reset();
        }
    }

    [RelayCommand]
    private async Task ToggleThemeAsync() => await _themeService.ToggleAsync().ConfigureAwait(true);

    /// <summary>
    /// Ctrl+K. Reveals the sidebar if it is collapsed, then asks the view for the caret.
    /// </summary>
    [RelayCommand]
    private void FocusSearch()
    {
        IsSidebarVisible = true;
        SearchRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Section 25. The chat pane owns the file dialog and the writing.</summary>
    [RelayCommand]
    private async Task ExportAsync(ExportFormat format)
    {
        try
        {
            await Chat.ExportAsync(format).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export as {Format} failed.", format);
        }
    }

    /// <summary>Dismisses the wizard and picks up whatever it configured.</summary>
    private async void OnFirstRunFinished(object? sender, EventArgs e)
    {
        IsFirstRunVisible = false;

        try
        {
            await _settings.UpdateAsync<Application.Configuration.GeneralSettings>(
                g => g.HasCompletedFirstRun = true).ConfigureAwait(true);

            // The wizard may have added a key, which changes both lists.
            await Settings.LoadProvidersAsync().ConfigureAwait(true);
            await ModelPicker.LoadAsync().ConfigureAwait(true);

            Chat.FocusInput();
        }
        catch (Exception ex)
        {
            // The user is already in the app; failing to record the flag only means the
            // wizard reappears next launch, which is better than blocking here.
            _logger.LogError(ex, "Could not complete first-run setup.");
        }
    }

    private async void OnSessionOpened(object? sender, Guid conversationId)
    {
        try
        {
            await OpenConversationAsync(conversationId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open conversation {Id}.", conversationId);
        }
    }

    private async Task OpenConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await Chat.LoadConversationAsync(conversationId, cancellationToken).ConfigureAwait(true);

        Sessions.ActiveConversationId = conversationId;
        CurrentPage = ShellPage.Chat;

        // The picker owns model state; ask it to match what this conversation was using.
        var detail = await _conversations.GetAsync(conversationId, cancellationToken).ConfigureAwait(true);
        ModelPicker.SelectModel(detail?.ProviderId, detail?.ModelId);

        // Remembered so the next launch reopens where the user left off.
        await _settings.UpdateAsync<Application.Configuration.GeneralSettings>(
            g => g.LastConversationId = conversationId,
            cancellationToken).ConfigureAwait(true);
    }

    private void OnSessionDeleted(object? sender, Guid conversationId)
    {
        if (Chat.ConversationId == conversationId)
        {
            Chat.StartNewConversation();
            Sessions.ActiveConversationId = null;
        }
    }

    private async void OnChatTitleChanged(object? sender, ConversationTitleChangedEventArgs e)
    {
        try
        {
            await Sessions.RefreshRowAsync(e.ConversationId).ConfigureAwait(true);
            Sessions.ActiveConversationId = e.ConversationId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not refresh the sidebar row for {Id}.", e.ConversationId);
        }
    }

    private async void OnSettingsApplied(object? sender, EventArgs e)
    {
        try
        {
            Chat.ApplyRenderingSettings();
            await ModelPicker.LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not apply the updated settings.");
        }
    }

    private async void OnPaletteCommand(object? sender, PaletteCommand command)
    {
        IsCommandPaletteOpen = false;

        try
        {
            switch (command)
            {
                case PaletteCommand.NewChat:
                    NewChat();
                    break;

                case PaletteCommand.SearchChats:
                    Sessions.SearchQuery = string.Empty;
                    FocusSearch();
                    break;

                case PaletteCommand.ChangeModel:
                    ModelPickerRequested?.Invoke(this, EventArgs.Empty);
                    break;

                case PaletteCommand.OpenCanvas:
                    await ShowCanvasAsync().ConfigureAwait(true);
                    break;

                case PaletteCommand.OpenSettings:
                    ShowSettings();
                    break;

                case PaletteCommand.ToggleTheme:
                    await _themeService.ToggleAsync().ConfigureAwait(true);
                    break;

                case PaletteCommand.ExportMarkdown:
                    await Chat.ExportAsync(ExportFormat.Markdown).ConfigureAwait(true);
                    break;

                case PaletteCommand.ExportJson:
                    await Chat.ExportAsync(ExportFormat.Json).ConfigureAwait(true);
                    break;

                case PaletteCommand.ExportText:
                    await Chat.ExportAsync(ExportFormat.PlainText).ConfigureAwait(true);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command palette action {Command} failed.", command);
        }
    }

    /// <summary>
    /// Section 31. Mirrors the monitor into the strip's binding.
    /// </summary>
    /// <remarks>
    /// The OS raises its network notifications on a thread-pool thread. A bound bool would in
    /// fact survive that, since WPF marshals a lone property change - but relying on it would
    /// leave whoever next writes a collection here with a crash and no clue why.
    /// </remarks>
    private void OnConnectivityChanged(object? sender, bool isOnline) =>
        UiThread.Post(() => IsOffline = !isOnline);

    /// <summary>Hands a language switch to the panes whose words are computed in code.</summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Chat.OnLanguageChanged();
        Sessions.OnLanguageChanged();
        FirstRun.OnLanguageChanged();
        Canvas.OnLanguageChanged();
        Inspector.OnLanguageChanged();
        CommandPalette.OnLanguageChanged();
    }

    /// <summary>Raised so the view can focus the search box, which is a view concern.</summary>
    public event EventHandler? SearchRequested;

    /// <summary>Raised so the view can open the model picker flyout.</summary>
    public event EventHandler? ModelPickerRequested;

    partial void OnCurrentPageChanged(ShellPage value)
    {
        OnPropertyChanged(nameof(IsChatVisible));
        OnPropertyChanged(nameof(IsSettingsVisible));
        OnPropertyChanged(nameof(IsCanvasVisible));
    }
}
