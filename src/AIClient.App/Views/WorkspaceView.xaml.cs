using System.Windows;
using System.Windows.Controls;
using AIClient.App.ViewModels;
using Localization = AIClient.App.Services.Localization;

namespace AIClient.App.Views;

/// <summary>
/// The workspace host's view layer: mode tab strip construction and page titles.
/// </summary>
/// <remarks>
/// The tab strip is built in code for the same reason the sidebar's navigation is: a row
/// is icon, label, tooltip and mode in one place, and the strip's selection is mirrored
/// both ways with the view model's own change events as the arbiter.
/// </remarks>
public partial class WorkspaceView : UserControl
{
    private bool _suppressTabEvents;

    private sealed record ModeTab(WorkspaceMode Mode, string LabelKey, string ToolTipKey);

    private static readonly ModeTab[] Tabs =
    [
        new(WorkspaceMode.Canvas, "S.Mode.Canvas", "S.Mode.Canvas.ToolTip"),
        new(WorkspaceMode.Graph, "S.Mode.Graph", "S.Mode.Graph.ToolTip"),
        new(WorkspaceMode.Files, "S.Mode.Files", "S.Mode.Files.ToolTip"),
        new(WorkspaceMode.Code, "S.Mode.Code", "S.Mode.Code.ToolTip"),
        new(WorkspaceMode.Chat, "S.Mode.Chat", "S.Mode.Chat.ToolTip"),
    ];

    private static readonly string[] PageTitleKeys =
    [
        "S.Mode.Title.Providers",
        "S.Mode.Title.Tasks",
        "S.Mode.Title.Settings",
    ];

    public WorkspaceView()
    {
        InitializeComponent();

        foreach (var tab in Tabs)
        {
            var item = new ListBoxItem
            {
                Tag = tab,
            };
            item.SetResourceReference(ContentControl.ContentProperty, tab.LabelKey);
            item.SetResourceReference(FrameworkElement.ToolTipProperty, tab.ToolTipKey);

            ModeTabs.Items.Add(item);
        }

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => SyncSelection();

    private void OnDataContextChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel previous)
        {
            previous.Workspace.PropertyChanged -= OnWorkspacePropertyChanged;
            previous.LanguageRefreshed -= OnLanguageRefreshed;
        }

        if (e.NewValue is MainViewModel current)
        {
            current.Workspace.PropertyChanged += OnWorkspacePropertyChanged;
            current.LanguageRefreshed += OnLanguageRefreshed;
            SyncSelection();
        }
    }

    private void OnLanguageRefreshed(object? sender, EventArgs e) => SyncSelection();

    private void OnWorkspacePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.WorkspaceViewModel.Mode))
        {
            SyncSelection();
        }
    }

    private void SyncSelection()
    {
        if (DataContext is not MainViewModel main)
        {
            return;
        }

        _suppressTabEvents = true;

        var index = Array.FindIndex(Tabs, tab => tab.Mode == main.Workspace.Mode);
        ModeTabs.SelectedIndex = index >= 0 ? index : -1;

        PageTitle.Text = main.Workspace.Mode switch
        {
            WorkspaceMode.Models => Localization.T(PageTitleKeys[0]),
            WorkspaceMode.Tasks => Localization.T(PageTitleKeys[1]),
            WorkspaceMode.Settings => Localization.T(PageTitleKeys[2]),
            _ => string.Empty,
        };

        _suppressTabEvents = false;
    }

    private void OnModeTabSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTabEvents || DataContext is not MainViewModel main)
        {
            return;
        }

        if (ModeTabs.SelectedItem is ListBoxItem { Tag: ModeTab tab })
        {
            main.Workspace.SwitchModeCommand.Execute(tab.Mode);
        }
    }

    private void OnBackToCanvas(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel main)
        {
            main.Workspace.SwitchModeCommand.Execute(WorkspaceMode.Canvas);
        }
    }
}
