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
    private readonly ILocalizationService _localization;
    private readonly IWorkspaceService _workspace;
    private readonly IDialogService _dialogs;
    private readonly IAppPaths _paths;
    private readonly ILogger<SettingsViewModel> _logger;

    private bool _isLoading;

    [ObservableProperty]
    private ThemeMode _theme;

    /// <summary>The language the interface is written in; switching applies immediately.</summary>
    [ObservableProperty]
    private LanguageOption _selectedLanguage;

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

    /// <summary>
    /// Whether the agent may run programs at all.
    /// </summary>
    /// <remarks>
    /// Sits among the three budgets above but is not one of them. Those bound how much of something the
    /// agent may do; this one decides whether a whole class of thing is possible, and it is the only
    /// setting on this screen whose consequence reaches outside the workspace folder. The view spells that
    /// out beside the switch rather than in a tooltip.
    /// </remarks>
    [ObservableProperty]
    private bool _allowCommands;

    /// <summary>The allowlist as one line of text, which is how it is edited.</summary>
    /// <remarks>
    /// A text box rather than a list with add and remove buttons. The list is a dozen short names that a
    /// user changes once and then leaves alone, so typing 'dotnet, git, npm' beats three dialogs - the
    /// care belongs in the parse, not in the widget.
    /// </remarks>
    [ObservableProperty]
    private string _allowedCommands = string.Empty;

    /// <summary>What is wrong with the allowlist as typed, or null when nothing is.</summary>
    [ObservableProperty]
    private string? _allowedCommandsProblem;

    [ObservableProperty]
    private int _commandTimeoutSeconds;

    [ObservableProperty]
    private int _maxCommandOutputCharacters;

    public SettingsViewModel(
        ISettingsService settings,
        IProviderRegistry registry,
        IAppThemeService themeService,
        ILocalizationService localization,
        IWorkspaceService workspace,
        IDialogService dialogs,
        IAppPaths paths,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _registry = registry;
        _themeService = themeService;
        _localization = localization;
        _workspace = workspace;
        _dialogs = dialogs;
        _paths = paths;
        _logger = logger;

        // Rebuilt labels: the shortcut reference and the workspace sentence change with the
        // language, so they are recomputed when the dictionary is swapped.
        _localization.LanguageChanged += (_, _) => RefreshLocalized();

        _selectedLanguage = Languages[0];
        Shortcuts = BuildShortcuts();
    }

    public ObservableCollection<ProviderSettingsViewModel> Providers { get; } = [];

    /// <summary>Log levels offered in the Storage section.</summary>
    public IReadOnlyList<string> LogLevels { get; } = ["Trace", "Debug", "Information", "Warning", "Error"];

    public IReadOnlyList<ThemeMode> ThemeModes { get; } = [ThemeMode.System, ThemeMode.Light, ThemeMode.Dark];

    /// <summary>Languages offered, always shown in their own name so they are recognisable everywhere.</summary>
    public IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new(UiLanguage.English, "English"),
        new(UiLanguage.Russian, "Русский"),
        new(UiLanguage.German, "Deutsch"),
    ];

    /// <summary>
    /// The shortcut reference (section 23). Declared here rather than scraped from the
    /// window's InputBindings: the gestures the shell registers and the list shown to the
    /// user are both short and both need a human description, which a KeyGesture has not got.
    /// Rebuilt on a language change, since the descriptions are the localized part.
    /// </summary>
    public IReadOnlyList<ShortcutInfo> Shortcuts { get; private set; } = [];

    public string DataDirectory => _paths.DataDirectory;
    public string DatabasePath => _paths.DatabasePath;
    public string LogsDirectory => _paths.LogsDirectory;

    public bool HasWorkspace => WorkspaceRoot is { Length: > 0 };

    /// <summary>The open folder, or a sentence saying there is none.</summary>
    public string WorkspaceLabel => WorkspaceRoot is { Length: > 0 } root
        ? root
        : _localization.T("S.Settings.Agent.Workspace.None");

    public string AppVersion =>
        typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    /// <summary>The About line, which carries the version into a localized sentence.</summary>
    public string VersionText => string.Format(_localization.T("S.Settings.About.Version"), AppVersion);

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
            SelectedLanguage = Languages.First(l => l.Language == current.General.Language);

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

            // The switch first: the warning under the allowlist depends on it, and assigning the list
            // recomputes that warning.
            AllowCommands = current.Agent.AllowCommands;
            AllowedCommands = string.Join(", ", current.Agent.AllowedCommands);
            CommandTimeoutSeconds = current.Agent.CommandTimeoutSeconds;
            MaxCommandOutputCharacters = current.Agent.MaxCommandOutputCharacters;

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
        var chosen = _dialogs.OpenFolder(_localization.T("S.Dialog.ChooseWorkspace"), WorkspaceRoot);

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

        WorkspaceProblem = result.Error ?? _localization.T("S.Chat.NotWorkspace");
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

    /// <summary>Switches the interface language; the service persists it and notifies the other panes.</summary>
    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (!_isLoading)
        {
            _ = _localization.SetLanguageAsync(value.Language);
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

    partial void OnAllowCommandsChanged(bool value)
    {
        Save<AgentSettings>(a => a.AllowCommands = value);
        AllowedCommandsProblem = CommandListProblem(AllowedCommands, value);
    }

    /// <remarks>
    /// The typed text is parsed but never written back to the property. Reformatting the box while it has
    /// focus would move the caret, and what matters is the list this saves rather than the punctuation it
    /// was typed with; anything that could not name a program is reported above instead of stored.
    /// </remarks>
    partial void OnAllowedCommandsChanged(string value)
    {
        var names = ParseCommandNames(value);

        Save<AgentSettings>(a => a.AllowedCommands = names);
        AllowedCommandsProblem = CommandListProblem(value, AllowCommands);
    }

    partial void OnCommandTimeoutSecondsChanged(int value) =>
        Save<AgentSettings>(a => a.CommandTimeoutSeconds = Math.Clamp(value, 5, 3600));

    partial void OnMaxCommandOutputCharactersChanged(int value) =>
        Save<AgentSettings>(a => a.MaxCommandOutputCharacters = Math.Clamp(value, 1_000, 200_000));

    /// <summary>
    /// What is wrong with the allowlist as typed, or null when nothing is.
    /// </summary>
    /// <remarks>
    /// Both cases below fail silently otherwise. An entry that cannot be a program name is dropped by the
    /// parse, so a user who typed a full path would never learn why their program is still refused; and an
    /// empty list with the switch on leaves the model holding a tool that refuses every call, which costs a
    /// step and an approval prompt to find out. Nothing is said while the switch is off, where neither
    /// matters.
    /// </remarks>
    private string? CommandListProblem(string text, bool allowed)
    {
        if (!allowed)
        {
            return null;
        }

        var dropped = Tokenize(text).Where(entry => !CouldNameAProgram(entry)).ToList();

        if (dropped.Count > 0)
        {
            return string.Format(
                _localization.T("S.Settings.Programs.Dropped"),
                string.Join(", ", dropped));
        }

        return ParseCommandNames(text).Count == 0
            ? _localization.T("S.Settings.Programs.Empty")
            : null;
    }

    /// <summary>Recomputes everything on this screen that is written in words rather than bound as a number.</summary>
    private void RefreshLocalized()
    {
        Shortcuts = BuildShortcuts();
        OnPropertyChanged(nameof(Shortcuts));
        OnPropertyChanged(nameof(WorkspaceLabel));
        OnPropertyChanged(nameof(VersionText));
        AllowedCommandsProblem = CommandListProblem(AllowedCommands, AllowCommands);
    }

    private IReadOnlyList<ShortcutInfo> BuildShortcuts() =>
    [
        new(_localization.T("S.Settings.Shortcut.NewChat"), "Ctrl+N"),
        new(_localization.T("S.Settings.Shortcut.Search"), "Ctrl+K"),
        new(_localization.T("S.Settings.Shortcut.OpenSettings"), "Ctrl+,"),
        new(_localization.T("S.Settings.Shortcut.Palette"), "Ctrl+Shift+P"),
        new(_localization.T("S.Settings.Shortcut.Send"), "Enter"),
        new(_localization.T("S.Settings.Shortcut.NewLine"), "Shift+Enter"),
        new(_localization.T("S.Settings.Shortcut.Stop"), "Esc"),
    ];

    /// <summary>Splits the allowlist box the way people type it: by commas, semicolons or spaces.</summary>
    private static string[] Tokenize(string text) =>
        text.Split(
            [',', ';', ' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Reads the box into the list the tool compares against, in the order it was typed.
    /// </summary>
    /// <remarks>
    /// The filter grants nothing and enforces nothing - the tool refuses a name carrying a path or a shell
    /// operator whatever this list says. What it buys is that such a mistake is visible here, in the screen
    /// where it was made, rather than as a refusal three steps into a run.
    /// </remarks>
    private static List<string> ParseCommandNames(string text)
    {
        var names = new List<string>();

        foreach (var entry in Tokenize(text))
        {
            if (CouldNameAProgram(entry)
                && !names.Any(existing => string.Equals(existing, entry, StringComparison.OrdinalIgnoreCase)))
            {
                names.Add(entry);
            }
        }

        return names;
    }

    /// <summary>Whether a word could be the name of a program, as opposed to a path or a command line.</summary>
    private static bool CouldNameAProgram(string entry) =>
        entry.Length is > 0 and <= 128
        && entry.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_' or '+');
}

/// <summary>One row in the keyboard shortcut reference.</summary>
public sealed record ShortcutInfo(string Description, string Keys);

/// <summary>One entry in the language picker: the language and its own name, as it is written natively.</summary>
public sealed record LanguageOption(UiLanguage Language, string NativeName);
