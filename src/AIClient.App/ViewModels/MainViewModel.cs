using AIClient.App.Services;
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

    public MainViewModel(
        ChatViewModel chat,
        SessionListViewModel sessions,
        ModelPickerViewModel modelPicker,
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
        IConnectivityMonitor connectivity,
        GraphContextSource graphContext,
        ILogger<MainViewModel> logger)
    {
        Chat = chat;
        Sessions = sessions;
        ModelPicker = modelPicker;
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
        _connectivity = connectivity;
        _graphContext = graphContext;
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

        // The workspace asks; the shell routes the question to the chat with the graph
        // context attached, because the chat is the one place a prompt is composed.
        Workspace.AskAiRequested += OnWorkspaceAskAi;

        // The AI state the context surface and status bar show is the chat's own state,
        // mirrored rather than duplicated.
        Chat.PropertyChanged += OnChatStateChanged;
        Chat.Approval.PropertyChanged += OnChatStateChanged; // the gate's IsAsking drives the activity panel

        MirrorAiState();
    }

    public ChatViewModel Chat { get; }

    public SessionListViewModel Sessions { get; }

    public ModelPickerViewModel ModelPicker { get; }

    public SettingsViewModel Settings { get; }

    public CommandPaletteViewModel CommandPalette { get; }

    public FirstRunViewModel FirstRun { get; }

    public WorkspaceViewModel Workspace { get; }

    public TasksViewModel Tasks { get; }

    public ModelsPageViewModel ModelsPage { get; }

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

    /// <summary>Cycles the sidebar between full, collapsed rail and hidden.</summary>
    [RelayCommand]
    private void CollapseSidebar() => (IsSidebarVisible, IsSidebarCollapsed) = IsSidebarVisible && !IsSidebarCollapsed
        ? (true, true)
        : (true, false);

    [RelayCommand]
    private void ToggleContextPanel() => IsContextPanelVisible = !IsContextPanelVisible;

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
    /// Ctrl+I. Asks the AI about whatever the workspace currently has selected, with the
    /// graph context the model needs to answer about the code it cannot see.
    /// </summary>
    [RelayCommand]
    private void AskAiAboutSelection()
    {
        var focus = Workspace.Canvas.Controller.SelectedNodeIds;
        var context = _graphContext.BuildContext(focus.Count > 0 ? focus : null);

        var prompt = string.IsNullOrEmpty(context)
            ? "Explain the overall structure of this workspace"
            : $"Explain the role of these parts of the workspace and how they relate:\n\n{context}";

        Workspace.RequestAskAi(prompt);
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
    private void OnWorkspaceAskAi(object? sender, string prompt)
    {
        Chat.Draft = prompt;
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

    private void MirrorAiState()
    {
        var stateText = Chat.IsGenerating
            ? Chat.IsAgentMode ? "Working" : "Answering"
            : Chat.Approval.IsAsking ? "Waiting for approval" : "Idle";

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
}
