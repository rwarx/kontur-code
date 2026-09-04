using System.Collections.ObjectModel;
using System.IO;
using AIClient.Avalonia.ViewModels.Canvas;
using AIClient.Application.Configuration;
using AIClient.Application.Interfaces;
using AIClient.Domain.Graph;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIClient.Avalonia.ViewModels;

/// <summary>
/// The shell: which pane is showing, whether the sidebar is open, and the routing between
/// the canvas and the chat.
/// </summary>
/// <remarks>
/// <para>
/// The routing rules are the product's UX in miniature, so they live here and nowhere else:
/// a selection on the canvas points the inspector; a question about a selection switches to
/// the chat pane and rides with the next message; activating a relation's other end focuses
/// that node on the canvas. Child view models raise events and never reference each other -
/// this class is the one place that knows how they fit together.
/// </remarks>
public sealed partial class ShellViewModel : ObservableObject
{
    public enum ShellPage
    {
        Chat,
        Settings,
        Canvas,
    }

    private readonly CanvasViewModel _canvas;
    private readonly InspectorViewModel _inspector;
    private readonly ISettingsService _settings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChatVisible))]
    [NotifyPropertyChangedFor(nameof(IsSettingsVisible))]
    [NotifyPropertyChangedFor(nameof(IsCanvasVisible))]
    private ShellPage _currentPage = ShellPage.Chat;

    [ObservableProperty]
    private bool _isSidebarOpen = true;

    [ObservableProperty]
    private bool _isPaletteOpen;

    public ShellViewModel(
        ChatPaneViewModel chat,
        SettingsPaneViewModel settings,
        CanvasViewModel canvas,
        InspectorViewModel inspector,
        CommandPaletteViewModel palette,
        IWorkspaceService workspace,
        ISettingsService settingsService)
    {
        Chat = chat;
        Settings = settings;
        _canvas = canvas;
        _inspector = inspector;
        Palette = palette;
        _settings = settingsService;

        WorkspaceName = workspace.IsOpen
            ? Path.GetFileName(workspace.Root!.TrimEnd(Path.DirectorySeparatorChar))
            : "No folder open";

        workspace.RootChanged += (_, root) => WorkspaceName = string.IsNullOrWhiteSpace(root)
            ? "No folder open"
            : Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));

        WirePanes();
        palette.SetCommands(BuildCommands());
    }

    public ChatPaneViewModel Chat { get; }

    public SettingsPaneViewModel Settings { get; }

    public CanvasViewModel Canvas => _canvas;

    public InspectorViewModel Inspector => _inspector;

    public CommandPaletteViewModel Palette { get; }

    public bool IsChatVisible => CurrentPage == ShellPage.Chat;

    public bool IsSettingsVisible => CurrentPage == ShellPage.Settings;

    public bool IsCanvasVisible => CurrentPage == ShellPage.Canvas;

    /// <summary>The open project, shown beside the logo.</summary>
    public string WorkspaceName { get; private set; } = "No folder open";

    private void WirePanes()
    {
        _canvas.SelectionChanged += (_, selection) => _inspector.Show(selection);

        _inspector.NodeActivated += (_, nodeId) =>
        {
            ShowCanvas();
            _canvas.Focus(nodeId);
        };

        _inspector.CodeRequested += (_, nodeId) =>
        {
            ShowCanvas();
            _ = _canvas.ShowCodeAsync(nodeId);
        };

        _inspector.OpenSourceRequested += (_, _) => _canvas.OpenSourceCommand.Execute(null);

        // One handler for both surfaces: the answer is visible while it streams, and the
        // selection stays on the canvas.
        _canvas.AiRequested += (_, request) => AskAboutGraph(request);
        _inspector.AiRequested += (_, request) => AskAboutGraph(request);
    }

    private void AskAboutGraph(CanvasAiRequest request)
    {
        CurrentPage = ShellPage.Chat;
        Chat.AskAboutGraphAsync(request.Selection, request.Prompt, request.Label);
    }

    [RelayCommand]
    private void ShowChat() => CurrentPage = ShellPage.Chat;

    [RelayCommand]
    private void ShowSettings() => CurrentPage = ShellPage.Settings;

    [RelayCommand]
    private void ShowCanvas()
    {
        CurrentPage = ShellPage.Canvas;
        _ = _canvas.InitializeAsync();
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    [RelayCommand]
    private void TogglePalette()
    {
        IsPaletteOpen = !IsPaletteOpen;
        if (IsPaletteOpen)
        {
            Palette.Reset();
        }
    }

    [RelayCommand]
    private void ClosePalette() => IsPaletteOpen = false;

    /// <summary>
    /// The command palette's commands, registered by the features that own them. A new
    /// command is a row here, not a case in a switch - which is the whole reason the palette
    /// survives the shell growing.
    /// </summary>
    private IReadOnlyList<ShellCommand> BuildCommands() =>
    [
        new("New chat", "Chat", "Ctrl+N", () => Chat.NewChatAsync(), "create conversation"),
        new("Search chats", "Chat", "Ctrl+K", () => { ShowChat(); return Task.CompletedTask; }, "find sessions"),
        new("Open canvas", "Workspace", "Ctrl+G", ShowCanvasAsync, "graph project"),
        new("Open chat", "Workspace", "", () => { ShowChat(); return Task.CompletedTask; }, "conversation"),
        new("Open settings", "Workspace", "Ctrl+,", () => { ShowSettings(); return Task.CompletedTask; }, "preferences providers"),
        new("Index workspace", "Graph", "", IndexWorkspaceAsync, "scan folder reindex"),
        new("Fit graph to content", "Graph", "F", () => { ShowCanvas(); _canvas.FitToContentCommand.Execute(null); return Task.CompletedTask; }, "zoom whole"),
        new("Auto layout", "Graph", "", AutoLayoutAsync, "arrange tidy"),
        new("Toggle sidebar", "View", "Ctrl+B", () => { ToggleSidebar(); return Task.CompletedTask; }, "hide panel"),
        new("Toggle theme", "View", "", ToggleThemeAsync, "dark light"),
        new("Command palette", "View", "Ctrl+Shift+P", () => Task.CompletedTask, "commands"),
    ];

    private Task ShowCanvasAsync()
    {
        ShowCanvas();
        return Task.CompletedTask;
    }

    private async Task IndexWorkspaceAsync()
    {
        ShowCanvas();
        await _canvas.IndexWorkspaceCommand.ExecuteAsync(null);
    }

    private async Task AutoLayoutAsync()
    {
        ShowCanvas();
        await _canvas.AutoLayoutCommand.ExecuteAsync(null);
    }

    /// <summary>Cycles System → Light → Dark, applies it at once, and persists the choice.</summary>
    private async Task ToggleThemeAsync()
    {
        var next = (ThemeMode)(((int)_settings.Current.Appearance.Theme + 1) % 3);

        await _settings.UpdateAsync<AppearanceSettings>(appearance => appearance.Theme = next);

        App.ApplyThemeFromSettings();
    }
}

/// <summary>One palette entry: what it says, where it belongs, and what it does.</summary>
public sealed record ShellCommand(
    string Title,
    string Category,
    string Shortcut,
    Func<Task> Run,
    string Keywords);
