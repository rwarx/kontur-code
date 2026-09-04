using System.Collections.ObjectModel;
using AIClient.App.Services;
using AIClient.Application.Configuration;
using AIClient.Application.Interfaces;
using AIClient.Domain.Enums;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIClient.App.ViewModels;

/// <summary>
/// Settings: General, Appearance, Providers, Models, Chat, Storage, Shortcuts, About.
/// </summary>
/// <remarks>
/// Each property writes through to <see cref="ISettingsService"/> on change rather than
/// waiting for a Save button. A settings screen that silently discards changes when the
/// window is closed is a worse failure than an extra write per keystroke, and the writes
/// are one small JSON row.
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IProviderRegistry _registry;
    private readonly IAppThemeService _themeService;
    private readonly IWorkspaceService _workspace;
    private readonly IDialogService _dialogs;
    private readonly IAppPaths _paths;
    private readonly ILogger<SettingsViewModel> _logger;

    private bool _isLoading;

    [ObservableProperty]
    private ThemeMode _theme;

    [ObservableProperty]
    private double _chatFontSize;

    [ObservableProperty]
    private double _codeFontSize;

    [ObservableProperty]
    private bool _useMicaBackdrop;

    [ObservableProperty]
    private bool _restoreLastConversation;

    [ObservableProperty]
    private bool _confirmBeforeDelete;

    [ObservableProperty]
    private bool _autoGenerateTitles;

    [ObservableProperty]
    private double _temperature;

    [ObservableProperty]
    private bool _isTemperatureEnabled;

    [ObservableProperty]
    private double _topP;

    [ObservableProperty]
    private bool _isTopPEnabled;

    [ObservableProperty]
    private int _maxTokens;

    [ObservableProperty]
    private bool _isMaxTokensEnabled;

    [ObservableProperty]
    private string _systemPrompt = string.Empty;

    [ObservableProperty]
    private bool _sendWithEnter;

    [ObservableProperty]
    private bool _renderMarkdown;

    [ObservableProperty]
    private bool _highlightCode;

    [ObservableProperty]
    private bool _autoScroll;

    [ObservableProperty]
    private bool _showTokenUsage;

    [ObservableProperty]
    private int _maxHistoryMessages;

    [ObservableProperty]
    private int _requestTimeoutSeconds;

    [ObservableProperty]
    private int _maxAttachmentKilobytes;

    [ObservableProperty]
    private string _minimumLogLevel = "Information";

    [ObservableProperty]
    private int _logRetentionDays;

    /// <summary>
    /// The folder the agent may work in, mirrored from the workspace rather than from settings.
    /// </summary>
    /// <remarks>
    /// The workspace is what actually decides this. It refuses folders the user is allowed to pick
    /// but the agent must not have - a drive root, a system folder, this application's own data -
    /// and it persists the choice itself, so showing the stored setting here would sometimes name a
    /// folder that was refused.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkspace))]
    [NotifyPropertyChangedFor(nameof(WorkspaceLabel))]
    private string? _workspaceRoot;

    /// <summary>Why the last folder was not opened, in the workspace's own words.</summary>
    [ObservableProperty]
    private string? _workspaceProblem;

    [ObservableProperty]
    private int _maxAgentSteps;

    [ObservableProperty]
    private int _maxAgentSeconds;

    [ObservableProperty]
    private int _maxAgentFileKilobytes;

    public SettingsViewModel(
        ISettingsService settings,
        IProviderRegistry registry,
        IAppThemeService themeService,
        IWorkspaceService workspace,
        IDialogService dialogs,
        IAppPaths paths,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _registry = registry;
        _themeService = themeService;
        _workspace = workspace;
        _dialogs = dialogs;
        _paths = paths;
        _logger = logger;
    }

    public ObservableCollection<ProviderSettingsViewModel> Providers { get; } = [];

    /// <summary>Log levels offered in the Storage section.</summary>
    public IReadOnlyList<string> LogLevels { get; } = ["Trace", "Debug", "Information", "Warning", "Error"];

    public IReadOnlyList<ThemeMode> ThemeModes { get; } = [ThemeMode.System, ThemeMode.Light, ThemeMode.Dark];

    /// <summary>
    /// The shortcut reference (section 23). Declared here rather than scraped from the
    /// window's InputBindings: the gestures the shell registers and the list shown to the
    /// user are both short and both need a human description, which a KeyGesture has not got.
    /// </summary>
    public IReadOnlyList<ShortcutInfo> Shortcuts { get; } =
    [
        new("New chat", "Ctrl+N"),
        new("Search chats", "Ctrl+K"),
        new("Open settings", "Ctrl+,"),
        new("Command palette", "Ctrl+Shift+P"),
        new("Send message", "Enter"),
        new("New line in the message", "Shift+Enter"),
        new("Stop generating", "Esc"),
    ];

    public string DataDirectory => _paths.DataDirectory;
    public string DatabasePath => _paths.DatabasePath;
    public string LogsDirectory => _paths.LogsDirectory;

    public bool HasWorkspace => WorkspaceRoot is { Length: > 0 };

    /// <summary>The open folder, or a sentence saying there is none.</summary>
    public string WorkspaceLabel => WorkspaceRoot is { Length: > 0 } root
        ? root
        : "No folder open. The agent cannot read or change anything until one is.";

    public string AppVersion =>
        typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    /// <summary>Raised after a change that other panes need to react to.</summary>
    public event EventHandler? SettingsApplied;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        // Guards the property setters below: assigning them fires the change handlers,
        // which would otherwise write every value straight back to the database.
        _isLoading = true;

        try
        {
            var current = _settings.Current;

            Theme = current.Appearance.Theme;
            ChatFontSize = current.Appearance.ChatFontSize;
            CodeFontSize = current.Appearance.CodeFontSize;
            UseMicaBackdrop = current.Appearance.UseMicaBackdrop;

            RestoreLastConversation = current.General.RestoreLastConversation;
            ConfirmBeforeDelete = current.General.ConfirmBeforeDelete;
            AutoGenerateTitles = current.General.AutoGenerateTitles;

            // A null sampling parameter means "do not send this field at all", which the
            // UI represents as an unchecked box next to a disabled slider.
            IsTemperatureEnabled = current.Chat.Temperature is not null;
            Temperature = current.Chat.Temperature ?? 0.7;
            IsTopPEnabled = current.Chat.TopP is not null;
            TopP = current.Chat.TopP ?? 1.0;
            IsMaxTokensEnabled = current.Chat.MaxTokens is not null;
            MaxTokens = current.Chat.MaxTokens ?? 4096;

            SystemPrompt = current.Chat.SystemPrompt ?? string.Empty;
            SendWithEnter = current.Chat.SendWithEnter;
            RenderMarkdown = current.Chat.RenderMarkdown;
            HighlightCode = current.Chat.HighlightCode;
            AutoScroll = current.Chat.AutoScroll;
            ShowTokenUsage = current.Chat.ShowTokenUsage;
            MaxHistoryMessages = current.Chat.MaxHistoryMessages;
            RequestTimeoutSeconds = current.Chat.RequestTimeoutSeconds;

            MaxAttachmentKilobytes = (int)(current.Storage.MaxAttachmentBytes / 1024);
            MinimumLogLevel = current.Storage.MinimumLogLevel;
            LogRetentionDays = current.Storage.LogRetentionDays;

            MaxAgentSteps = current.Agent.MaxSteps;
            MaxAgentSeconds = current.Agent.MaxDurationSeconds;
            MaxAgentFileKilobytes = (int)(current.Agent.MaxFileBytes / 1024);

            // Asked of the workspace, which has already re-checked the stored folder against the
            // disk: it may have been deleted or moved since it was chosen.
            WorkspaceRoot = _workspace.Root;
            WorkspaceProblem = null;
        }
        finally
        {
            _isLoading = false;
        }

        await LoadProvidersAsync(cancellationToken).ConfigureAwait(true);
    }

    public async Task LoadProvidersAsync(CancellationToken cancellationToken = default)
    {
        var providers = await _registry.GetProvidersAsync(cancellationToken).ConfigureAwait(true);

        Providers.Clear();

        foreach (var provider in providers)
        {
            Providers.Add(new ProviderSettingsViewModel(provider, _registry, _dialogs, _logger));
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenInExplorer(_paths.DataDirectory);

    [RelayCommand]
    private void OpenLogsFolder() => OpenInExplorer(_paths.LogsDirectory);

    /// <summary>
    /// Picks the folder the agent works in.
    /// </summary>
    /// <remarks>
    /// The refusal is shown rather than swallowed. A folder the workspace will not take is nearly
    /// always one of three things - a drive root, a system folder, or this application's own data -
    /// and a picker that closed and changed nothing would leave the user to guess which.
    /// </remarks>
    [RelayCommand]
    private async Task ChooseWorkspaceAsync()
    {
        var chosen = _dialogs.OpenFolder("Choose the folder the agent may work in", WorkspaceRoot);

        if (chosen is not { Length: > 0 })
        {
            return;
        }

        var result = await _workspace.OpenAsync(chosen).ConfigureAwait(true);

        if (result is { Success: true, Value: { Length: > 0 } opened })
        {
            WorkspaceRoot = opened;
            WorkspaceProblem = null;

            _logger.LogInformation("A workspace folder was opened from Settings.");
            return;
        }

        WorkspaceProblem = result.Error ?? "That folder cannot be used as a workspace.";
    }

    /// <summary>Closes the workspace, which is the off switch for everything the agent can reach.</summary>
    [RelayCommand]
    private async Task CloseWorkspaceAsync()
    {
        await _workspace.CloseAsync().ConfigureAwait(true);

        WorkspaceRoot = null;
        WorkspaceProblem = null;
    }

    [RelayCommand]
    private void OpenWorkspaceFolder()
    {
        if (WorkspaceRoot is { Length: > 0 } root)
        {
            OpenInExplorer(root);
        }
    }

    private void OpenInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open a folder in Explorer.");
        }
    }

    private async void Save<TSection>(Action<TSection> mutate, bool notify = false)
        where TSection : class
    {
        if (_isLoading)
        {
            return;
        }

        try
        {
            await _settings.UpdateAsync(mutate).ConfigureAwait(true);

            if (notify)
            {
                SettingsApplied?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save a settings change.");
        }
    }

    partial void OnThemeChanged(ThemeMode value)
    {
        if (!_isLoading)
        {
            _ = _themeService.SetThemeAsync(value);
        }
    }

    partial void OnChatFontSizeChanged(double value) =>
        Save<AppearanceSettings>(a => a.ChatFontSize = Math.Clamp(value, 10, 24), notify: true);

    partial void OnCodeFontSizeChanged(double value) =>
        Save<AppearanceSettings>(a => a.CodeFontSize = Math.Clamp(value, 9, 22), notify: true);

    partial void OnUseMicaBackdropChanged(bool value) =>
        Save<AppearanceSettings>(a => a.UseMicaBackdrop = value);

    partial void OnRestoreLastConversationChanged(bool value) =>
        Save<GeneralSettings>(g => g.RestoreLastConversation = value);

    partial void OnConfirmBeforeDeleteChanged(bool value) =>
        Save<GeneralSettings>(g => g.ConfirmBeforeDelete = value);

    partial void OnAutoGenerateTitlesChanged(bool value) =>
        Save<GeneralSettings>(g => g.AutoGenerateTitles = value);

    partial void OnTemperatureChanged(double value) =>
        Save<ChatSettings>(c => c.Temperature = IsTemperatureEnabled ? Math.Clamp(value, 0, 2) : null);

    partial void OnIsTemperatureEnabledChanged(bool value) =>
        Save<ChatSettings>(c => c.Temperature = value ? Temperature : null);

    partial void OnTopPChanged(double value) =>
        Save<ChatSettings>(c => c.TopP = IsTopPEnabled ? Math.Clamp(value, 0, 1) : null);

    partial void OnIsTopPEnabledChanged(bool value) =>
        Save<ChatSettings>(c => c.TopP = value ? TopP : null);

    partial void OnMaxTokensChanged(int value) =>
        Save<ChatSettings>(c => c.MaxTokens = IsMaxTokensEnabled ? Math.Max(1, value) : null);

    partial void OnIsMaxTokensEnabledChanged(bool value) =>
        Save<ChatSettings>(c => c.MaxTokens = value ? MaxTokens : null);

    partial void OnSystemPromptChanged(string value) =>
        Save<ChatSettings>(c => c.SystemPrompt = string.IsNullOrWhiteSpace(value) ? null : value);

    partial void OnSendWithEnterChanged(bool value) =>
        Save<ChatSettings>(c => c.SendWithEnter = value);

    partial void OnRenderMarkdownChanged(bool value) =>
        Save<ChatSettings>(c => c.RenderMarkdown = value, notify: true);

    partial void OnHighlightCodeChanged(bool value) =>
        Save<ChatSettings>(c => c.HighlightCode = value, notify: true);

    partial void OnAutoScrollChanged(bool value) =>
        Save<ChatSettings>(c => c.AutoScroll = value, notify: true);

    partial void OnShowTokenUsageChanged(bool value) =>
        Save<ChatSettings>(c => c.ShowTokenUsage = value, notify: true);

    partial void OnMaxHistoryMessagesChanged(int value) =>
        Save<ChatSettings>(c => c.MaxHistoryMessages = Math.Clamp(value, 2, 1000));

    partial void OnRequestTimeoutSecondsChanged(int value) =>
        Save<ChatSettings>(c => c.RequestTimeoutSeconds = Math.Clamp(value, 10, 3600));

    partial void OnMaxAttachmentKilobytesChanged(int value) =>
        Save<StorageSettings>(s => s.MaxAttachmentBytes = Math.Clamp(value, 1, 10240) * 1024L);

    partial void OnMinimumLogLevelChanged(string value) =>
        Save<StorageSettings>(s => s.MinimumLogLevel = value);

    partial void OnLogRetentionDaysChanged(int value) =>
        Save<StorageSettings>(s => s.LogRetentionDays = Math.Clamp(value, 1, 365));

    // The two agent budgets are clamped rather than validated, for the same reason as the rest of
    // this screen: a number typed into a box should bound the run, not refuse to be saved.
    partial void OnMaxAgentStepsChanged(int value) =>
        Save<AgentSettings>(a => a.MaxSteps = Math.Clamp(value, 1, 100));

    partial void OnMaxAgentSecondsChanged(int value) =>
        Save<AgentSettings>(a => a.MaxDurationSeconds = Math.Clamp(value, 0, 7200));

    partial void OnMaxAgentFileKilobytesChanged(int value) =>
        Save<AgentSettings>(a => a.MaxFileBytes = Math.Clamp(value, 1, 8192) * 1024L);
}

/// <summary>One row in the keyboard shortcut reference.</summary>
public sealed record ShortcutInfo(string Description, string Keys);
