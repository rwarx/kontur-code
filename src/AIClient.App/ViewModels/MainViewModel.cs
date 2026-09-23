using AIClient.App.Services;
using AIClient.Application.Configuration;
using AIClient.Application.Graph;
using AIClient.Application.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIClient.App.ViewModels;

/// <summary>
/// The shell: owns the sidebar, the workspace and its modes, the context panel, the palette
/// and the overlays, and routes between them.
/// </summary>
/// <remarks>
/// <para>
/// The child view models do not know about each other. When a session is opened, a title is
/// generated, the agent starts or stops working, or a surface asks the AI a question, the
/// event lands here and this class decides what else changes. Wiring them directly would
/// make the sidebar depend on the workspace and the workspace on the chat, each direction
/// buying a little convenience and paying in rigidity.
/// </para>
/// <para>
/// <see cref="WorkspaceMode"/> replaces the shell's old two-page routing: settings, models
/// and tasks are workspace modes now, not a second navigation axis, so there is exactly one
/// answer to "what am I looking at".
/// </para>
/// </remarks>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IConversationService _conversations;
    private readonly ISettingsService _settings;
    private readonly IProviderRegistry _registry;
    private readonly IAppThemeService _themeService;
    private readonly ILocalizationService _localization;
    private readonly IConnectivityMonitor _connectivity;
    private readonly GraphContextSource _graphContext;
    private readonly ILogger<MainViewModel> _logger;

    [ObservableProperty]
    private bool _isSidebarVisible = true;

    [ObservableProperty]
    private bool _isSidebarCollapsed;

    [ObservableProperty]
    private bool _isContextPanelVisible = true;

    [ObservableProperty]
    private bool _isCommandPaletteOpen;

    [ObservableProperty]
    private bool _isFirstRunVisible;

    /// <summary>Set while no provider can be reached, so the shell can show an offline strip.</summary>
    [ObservableProperty]
    private bool _isOffline;

    /// <summary>Enables the title bar's back arrow; true when there is a prior view to return to.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    private bool _canGoBack;

    /// <summary>Enables the title bar's forward arrow; true after the back arrow has been used.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoForwardCommand))]
    private bool _canGoForward;

    // Back/forward navigation over workspace modes: the title bar's arrows walk this the way
    // an editor's do. The guard tells a history-driven switch from a fresh one, so walking
    // back does not itself count as a new destination.
    private readonly Stack<WorkspaceMode> _backModes = new();
    private readonly Stack<WorkspaceMode> _forwardModes = new();
    private WorkspaceMode _currentMode;
    private bool _isNavigatingHistory;

    public MainViewModel(
        ChatViewModel chat,
        SessionListViewModel sessions,
        ModelPickerViewModel modelPicker,
        SessionContextViewModel sessionContext,
        SettingsViewModel settings,
        CommandPaletteViewModel commandPalette,
        FirstRunViewModel firstRun,
        WorkspaceViewModel workspace,
        TasksViewModel tasks,
        ModelsPageViewModel modelsPage,
        IConversationService conversations,
        ISettingsService settingsService,
        IProviderRegistry registry,
        IAppThemeService themeService,
        ILocalizationService localization,
        IConnectivityMonitor connectivity,
        GraphContextSource graphContext,
        ILogger<MainViewModel> logger)
    {
        Chat = chat;
        Sessions = sessions;
        ModelPicker = modelPicker;
        SessionContext = sessionContext;
        Settings = settings;
        CommandPalette = commandPalette;
        FirstRun = firstRun;
        Workspace = workspace;
        Tasks = tasks;
        ModelsPage = modelsPage;

        _conversations = conversations;
        _settings = settingsService;
        _registry = registry;
        _themeService = themeService;
        _localization = localization;
        _connectivity = connectivity;
        _graphContext = graphContext;
        _logger = logger;

        // The sun/moon morph reads this; without the subscription it would freeze on
        // whatever the theme was at startup.
        _themeService.EffectiveThemeChanged += (_, _) => OnPropertyChanged(nameof(IsDarkTheme));

        IsOffline = !connectivity.IsOnline;
        connectivity.ConnectivityChanged += OnConnectivityChanged;

        Sessions.SessionOpened += OnSessionOpened;
        Sessions.SessionDeleted += OnSessionDeleted;
        Chat.TitleChanged += OnChatTitleChanged;
        ModelPicker.ModelSelected += (_, model) => Chat.SelectedModel = model;
        Settings.SettingsApplied += OnSettingsApplied;
        CommandPalette.CommandInvoked += OnPaletteCommand;
        FirstRun.Finished += OnFirstRunFinished;

        // The workspace asks; the shell routes the question to the chat with the graph
        // context attached, because the chat is the one place a prompt is composed.
        Workspace.AskAiRequested += OnWorkspaceAskAi;

        // The shell remembers where the workspace has been, so the title bar's arrows can
        // return there. The workspace's mode is the single source of "what am I looking at",
        // so one subscription catches every switch no matter who made it.
        _currentMode = Workspace.Mode;
        Workspace.PropertyChanged += OnWorkspacePropertyChanged;

        // The AI state the context surface and status bar show is the chat's own state,
        // mirrored rather than duplicated.
        Chat.PropertyChanged += OnChatStateChanged;
        Chat.Approval.PropertyChanged += OnChatStateChanged; // the gate's IsAsking drives the activity panel

        // Folding rewrites which rows the model will be shown and adds a summary message, so the
        // transcript on screen is stale the moment the panel's button returns. The panel does not
        // know the chat pane exists; the fold arrives here and the reload is asked for from here.
        SessionContext.Compacted += OnConversationCompacted;

        // A language switch is written in words, and half of those words are computed in the
        // child ViewModels rather than bound from the string table, so each is told to rebuild.
        _localization.LanguageChanged += OnLanguageChanged;

        MirrorAiState();
    }

    public ChatViewModel Chat { get; }

    public SessionListViewModel Sessions { get; }

    public ModelPickerViewModel ModelPicker { get; }

    public SessionContextViewModel SessionContext { get; }

    public SettingsViewModel Settings { get; }

    public CommandPaletteViewModel CommandPalette { get; }

    public FirstRunViewModel FirstRun { get; }

    public WorkspaceViewModel Workspace { get; }

    public TasksViewModel Tasks { get; }

    public ModelsPageViewModel ModelsPage { get; }

    /// <summary>
    /// Whether the theme on screen is dark. Drives the sidebar's sun/moon morph: the icon
    /// names what one click buys, not what is already on screen.
    /// </summary>
    public bool IsDarkTheme => _themeService.EffectiveTheme == ThemeMode.Dark;

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

        // The workspace brings up its graph (loading the persisted canvas for the open
        // folder, or indexing a folder met for the first time) before any surface reads it.
        await Workspace.InitializeAsync(cancellationToken).ConfigureAwait(true);
        await ModelsPage.LoadAsync(cancellationToken).ConfigureAwait(true);

        if (general.RestoreLastConversation && general.LastConversationId is { } lastId)
        {
            await OpenConversationAsync(lastId, cancellationToken).ConfigureAwait(true);
        }
    }

    // ------------------------------------------------------------- commands

    [RelayCommand]
    private void NewChat()
    {
        Chat.StartNewConversation();
        Sessions.ActiveConversationId = null;
        Workspace.SwitchModeCommand.Execute(WorkspaceMode.Chat);
    }

    [RelayCommand]
    private void ShowSettings() => Workspace.SwitchModeCommand.Execute(WorkspaceMode.Settings);

    [RelayCommand]
    private void ShowChat() => Workspace.SwitchModeCommand.Execute(WorkspaceMode.Chat);

    /// <summary>
    /// Opens the canvas by switching the workspace to its canvas mode, loading the graph
    /// the first time.
    /// </summary>
    /// <remarks>
    /// Lazily, not at startup: someone who only ever chats should not pay for a graph read, and
    /// Workspace.InitializeAsync has already brought up the graph before any surface reads it.
    /// </remarks>
    [RelayCommand]
    private Task ShowCanvasAsync()
    {
        Workspace.SwitchModeCommand.Execute(WorkspaceMode.Canvas);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void ToggleSidebar()
    {
        if (IsSidebarCollapsed)
        {
            IsSidebarCollapsed = false;
            IsSidebarVisible = true;
        }
        else
        {
            IsSidebarVisible = !IsSidebarVisible;
        }
    }

    [RelayCommand]
    private void ToggleContextPanel() => IsContextPanelVisible = !IsContextPanelVisible;

    /// <summary>
    /// Builds the context report for the open chat, then asks the view to show it.
    /// </summary>
    /// <remarks>
    /// The report is read before the flyout opens, so the panel never appears holding the previous
    /// chat's numbers. The provider and model come from the picker rather than from the conversation's
    /// own record, because the question the panel answers is how much room the next message has.
    /// </remarks>
    [RelayCommand]
    private async Task ShowContextAsync()
    {
        try
        {
            await SessionContext
                .LoadAsync(Chat.ConversationId, Chat.SelectedModel?.ProviderId, Chat.SelectedModel?.ModelId)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The panel draws its own empty state, so opening it with nothing in it beats not opening.
            _logger.LogError(ex, "The context report could not be built.");
        }

        ContextRequested?.Invoke(this, EventArgs.Empty);
    }

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
        IsSidebarCollapsed = false;
        SearchRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Ctrl+I. Asks the AI about whatever the workspace currently has selected; the graph
    /// context itself is attached by the funnel the request flows through.
    /// </summary>
    [RelayCommand]
    private void AskAiAboutSelection()
    {
        var prompt = Workspace.Canvas.Controller.SelectedNodeIds.Count > 0
            ? "Explain the role of these parts of the workspace and how they relate"
            : "Explain the overall structure of this workspace";

        Workspace.RequestAskAi(prompt);
    }

    /// <summary>
    /// The title bar's back arrow (Alt+Left). Returns to the previously shown workspace mode
    /// and keeps the place being left on the forward trail - an editor's history, over modes.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        if (_backModes.Count == 0)
        {
            return;
        }

        _forwardModes.Push(_currentMode);
        NavigateHistory(_backModes.Pop());
    }

    /// <summary>The forward arrow (Alt+Right): replays a mode the back arrow walked away from.</summary>
    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward()
    {
        if (_forwardModes.Count == 0)
        {
            return;
        }

        _backModes.Push(_currentMode);
        NavigateHistory(_forwardModes.Pop());
    }

    private void NavigateHistory(WorkspaceMode mode)
    {
        // Flag the switch as history-driven so OnWorkspacePropertyChanged does not read it as
        // a new destination and wipe the trail we are walking.
        _isNavigatingHistory = true;

        try
        {
            Workspace.SwitchModeCommand.Execute(mode);
            _currentMode = mode;
        }
        finally
        {
            _isNavigatingHistory = false;
        }

        UpdateNavigationState();
    }

    private void UpdateNavigationState()
    {
        CanGoBack = _backModes.Count > 0;
        CanGoForward = _forwardModes.Count > 0;
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

    // --------------------------------------------------------------- events

    /// <summary>Raised so the view can focus the search box, which is a view concern.</summary>
    public event EventHandler? SearchRequested;

    /// <summary>Raised so the view can open the model picker flyout.</summary>
    public event EventHandler? ModelPickerRequested;

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
        Workspace.SwitchModeCommand.Execute(WorkspaceMode.Chat);

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

    /// <summary>
    /// Re-reads the transcript after a fold, so the pane shows the history the model will now see.
    /// </summary>
    /// <remarks>
    /// Only when the fold was of the chat that is open. Nothing stops the panel from being pointed at
    /// one conversation while the user opens another, and reloading the pane with a different chat's
    /// rows because a background fold finished would be worse than showing nothing.
    /// </remarks>
    private async void OnConversationCompacted(object? sender, Guid conversationId)
    {
        if (Chat.ConversationId != conversationId)
        {
            return;
        }

        try
        {
            await Chat.LoadConversationAsync(conversationId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The fold itself succeeded and is on disk; only the view is behind.
            _logger.LogError(ex, "The transcript could not be reloaded after folding {Id}.", conversationId);
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

    /// <summary>
    /// The workspace asked the AI something. The prompt is written into the composer for
    /// the user to see and edit before it is sent - the honest form of context sharing:
    /// what the model reads is what the user reads.
    /// </summary>
    /// <remarks>
    /// The graph context is attached here, at the one funnel every "ask the AI about the
    /// workspace" path flows through: the canvas toolbar, the Inspector's node and
    /// selection buttons, the palette and Ctrl+I all land in this handler. Without the
    /// block, "explain this selection" reaches the model as a question about nodes it has
    /// never heard of. With it, the block opens by naming the selected nodes explicitly,
    /// so the question and the referent arrive together.
    /// </remarks>
    private void OnWorkspaceAskAi(object? sender, string prompt)
    {
        var focus = Workspace.Canvas.Controller.SelectedNodeIds;
        var context = _graphContext.BuildContext(focus.Count > 0 ? focus : null);

        Chat.Draft = context is null
            ? prompt
            : $"{prompt}\n\n{context}";

        Workspace.SwitchModeCommand.Execute(WorkspaceMode.Chat);
        Chat.FocusInput();
    }

    private void OnChatStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatViewModel.IsGenerating)
            or nameof(ChatViewModel.IsAgentMode)
            or nameof(ChatViewModel.SelectedAgentMode)
            or nameof(ChatViewModel.SelectedModel)
            or nameof(Chat.Approval.IsAsking))
        {
            MirrorAiState();
        }
    }

    /// <summary>
    /// Tracks the workspace mode into the back/forward trail. Every switch flows through the
    /// workspace's one <see cref="WorkspaceViewModel.Mode"/>, so this is the single place the
    /// history needs to watch - whoever moved it, the arrows stay honest.
    /// </summary>
    private void OnWorkspacePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WorkspaceViewModel.Mode))
        {
            return;
        }

        var mode = Workspace.Mode;

        if (mode == _currentMode || _isNavigatingHistory)
        {
            // A history-driven switch is bookkept by NavigateHistory; a no-op switch is nothing.
            return;
        }

        // A fresh navigation retires the current place to the back trail and drops the
        // forward trail, exactly as a browser does when you leave a back-stack midway.
        _backModes.Push(_currentMode);
        _forwardModes.Clear();
        _currentMode = mode;
        UpdateNavigationState();
    }

    private void MirrorAiState()
    {
        var stateText = Chat.IsGenerating
            ? Chat.IsAgentMode ? Localization.T("S.AiState.Working") : Localization.T("S.AiState.Answering")
            : Chat.Approval.IsAsking ? Localization.T("S.AiState.WaitingApproval") : Localization.T("S.Main.Ai.Idle");

        Workspace.Context.SetAiState(
            Chat.IsGenerating,
            stateText,
            Chat.SelectedModel?.Name ?? string.Empty,
            Chat.Approval.IsAsking);
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

                // The new palette surface: modes, workspace, graph, panels, AI.
                case PaletteCommand.SwitchToCanvas:
                case PaletteCommand.SwitchToGraph:
                case PaletteCommand.SwitchToFiles:
                case PaletteCommand.SwitchToCode:
                case PaletteCommand.SwitchToChat:
                case PaletteCommand.ShowModels:
                case PaletteCommand.ShowTasks:
                    Workspace.SwitchModeCommand.Execute(command switch
                    {
                        PaletteCommand.SwitchToCanvas => WorkspaceMode.Canvas,
                        PaletteCommand.SwitchToGraph => WorkspaceMode.Graph,
                        PaletteCommand.SwitchToFiles => WorkspaceMode.Files,
                        PaletteCommand.SwitchToCode => WorkspaceMode.Code,
                        PaletteCommand.ShowModels => WorkspaceMode.Models,
                        PaletteCommand.ShowTasks => WorkspaceMode.Tasks,
                        _ => WorkspaceMode.Chat,
                    });
                    break;

                case PaletteCommand.OpenWorkspace:
                    await Workspace.OpenWorkspaceCommand.ExecuteAsync(null).ConfigureAwait(true);
                    break;

                case PaletteCommand.CloseWorkspace:
                    await Workspace.CloseWorkspaceCommand.ExecuteAsync(null).ConfigureAwait(true);
                    break;

                case PaletteCommand.RefreshGraph:
                    await Workspace.RefreshGraphCommand.ExecuteAsync(null).ConfigureAwait(true);
                    break;

                case PaletteCommand.FitGraph:
                    Workspace.SwitchModeCommand.Execute(WorkspaceMode.Canvas);
                    Workspace.Canvas.Controller.NotifyGesture(
                        AIClient.App.Canvas.GraphCanvas.GestureKind.BackgroundDoubleClicked, null);
                    break;

                case PaletteCommand.UndoGraph:
                    await Workspace.Canvas.UndoCommand.ExecuteAsync(null).ConfigureAwait(true);
                    break;

                case PaletteCommand.RedoGraph:
                    await Workspace.Canvas.RedoCommand.ExecuteAsync(null).ConfigureAwait(true);
                    break;

                case PaletteCommand.AskAiAboutSelection:
                    AskAiAboutSelection();
                    break;

                case PaletteCommand.ToggleContextPanel:
                    ToggleContextPanel();
                    break;

                case PaletteCommand.ToggleSidebar:
                    ToggleSidebar();
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
        CommandPalette.OnLanguageChanged();
        SessionContext.OnLanguageChanged();
        Workspace.Canvas.RefreshLanguage();
        Workspace.Context.RefreshLanguage();
        MirrorAiState();
        LanguageRefreshed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised so the view can open the context flyout, once its report has been built.</summary>
    public event EventHandler? ContextRequested;

    /// <summary>Raised after a language switch so views with code-built rows can rebuild them.</summary>
    public event EventHandler? LanguageRefreshed;
}
