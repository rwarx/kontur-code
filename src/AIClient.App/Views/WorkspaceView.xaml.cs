using System.Windows;
using System.Windows.Controls;
using AIClient.App.ViewModels;

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

    private sealed record ModeTab(WorkspaceMode Mode, string Label, string ToolTip);

    private static readonly ModeTab[] Tabs =
    [
        new(WorkspaceMode.Canvas, "Canvas", "The workspace as a spatial map"),
        new(WorkspaceMode.Graph, "Graph", "The same map as a structure"),
        new(WorkspaceMode.Files, "Files", "The workspace's file tree"),
        new(WorkspaceMode.Code, "Code", "Open documents"),
        new(WorkspaceMode.Chat, "Chat", "The conversation"),
    ];

    private static readonly string[] PageTitles =
    [
        "Models",
        "Tasks & Agents",
        "Settings",
    ];

    public WorkspaceView()
    {
        InitializeComponent();

        foreach (var tab in Tabs)
        {
            var item = new ListBoxItem
            {
                Content = tab.Label,
                Tag = tab,
                ToolTip = tab.ToolTip,
            };

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
        }

        if (e.NewValue is MainViewModel current)
        {
            current.Workspace.PropertyChanged += OnWorkspacePropertyChanged;
            SyncSelection();
        }
    }

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
            WorkspaceMode.Models => PageTitles[0],
            WorkspaceMode.Tasks => PageTitles[1],
            WorkspaceMode.Settings => PageTitles[2],
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
