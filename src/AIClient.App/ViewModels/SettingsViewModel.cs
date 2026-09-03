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

    public SettingsViewModel(
        ISettingsService settings,
        IProviderRegistry registry,
        IAppThemeService themeService,
        IDialogService dialogs,
        IAppPaths paths,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _registry = registry;
        _themeService = themeService;
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
}

/// <summary>One row in the keyboard shortcut reference.</summary>
public sealed record ShortcutInfo(string Description, string Keys);
