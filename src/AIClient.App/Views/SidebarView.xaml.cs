using System.Windows;
using System.Windows.Controls;
using AIClient.App.Controls;
using AIClient.App.ViewModels;
using AIClient.Application.DTOs;

namespace AIClient.App.Views;

/// <summary>
/// The sidebar's view layer: focus handling, navigation selection, and the session list's
/// open-on-select behaviour.
/// </summary>
/// <remarks>
/// <para>
/// The navigation rows are built in code rather than XAML because the collapse state hides
/// labels, and a code-built row is one place where the icon, label, tooltip and mode agree,
/// rather than five XAML rows that each hard-code the same quartet.
/// </para>
/// <para>
/// Session selection opens the conversation (rather than waiting for a second click) but
/// only when the selection is genuinely new - re-clicking the row of an open conversation
/// must not reload it and lose the composer's draft.
/// </para>
/// </remarks>
public partial class SidebarView : UserControl
{
    private bool _suppressNavEvents;

    private sealed record NavItem(WorkspaceMode Mode, IconKind Icon, string Label, string ToolTip);

    private static readonly NavItem[] Nav =
    [
        new(WorkspaceMode.Canvas, IconKind.Canvas, "Canvas", "The workspace as a spatial map (Ctrl+Shift+P → Canvas)"),
        new(WorkspaceMode.Graph, IconKind.Graph, "Graph", "The same map as a structure"),
        new(WorkspaceMode.Files, IconKind.Files, "Files", "The workspace's file tree"),
        new(WorkspaceMode.Code, IconKind.Code, "Code", "Open documents"),
        new(WorkspaceMode.Chat, IconKind.Chat, "Chat", "The conversation"),
        new(WorkspaceMode.Models, IconKind.Models, "Models", "Providers and the model catalogue"),
        new(WorkspaceMode.Tasks, IconKind.Tasks, "Tasks", "Agent activity"),
    ];

    public SidebarView()
    {
        InitializeComponent();

        foreach (var item in Nav)
        {
            // The rail version of every row carries its own tooltip, so collapsing
            // hides labels without hiding meaning.
            var row = new ListBoxItem
            {
                Content = new NavRow(item),
                Tag = item,
                ToolTip = item.ToolTip,
            };

            NavList.Items.Add(row);
        }

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    /// <summary>One nav row: icon and label, laid out to the shared template.</summary>
    private sealed class NavRow(NavItem item)
    {
        public IconKind Icon => item.Icon;

        public string Label => item.Label;

        public override string ToString() => item.Label;
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

    /// <summary>Brings the highlight back to the current mode without raising its event.</summary>
    private void SyncSelection()
    {
        if (DataContext is not MainViewModel main)
        {
            return;
        }

        _suppressNavEvents = true;

        var index = Array.FindIndex(Nav, item => item.Mode == main.Workspace.Mode);
        NavList.SelectedIndex = index >= 0 ? index : -1;

        _suppressNavEvents = false;
    }

    private void OnNavSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressNavEvents || DataContext is not MainViewModel main)
        {
            return;
        }

        if (NavList.SelectedItem is ListBoxItem { Tag: NavItem item })
        {
            main.Workspace.SwitchModeCommand.Execute(item.Mode);
        }
    }

    /// <summary>Ctrl+K lands here: the search box is focused and selected.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnSessionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || DataContext is not MainViewModel main)
        {
            return;
        }

        if (e.AddedItems[0] is not ConversationSummary session)
        {
            return;
        }

        // Only a genuinely different conversation opens: reselecting the current one (the
        // list restores it on load) would reload the transcript and drop a half-written
        // reply, which is the kind of surprise nobody forgives.
        if (session.Id == main.Sessions.ActiveConversationId)
        {
            return;
        }

        main.Sessions.OpenCommand.Execute(session);
    }

    private void OnSessionScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not MainViewModel main)
        {
            return;
        }

        // Infinite scroll with a 240px runway, same policy the old sidebar had.
        if (e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 240
            && e.ExtentHeight > e.ViewportHeight)
        {
            if (main.Sessions.LoadMoreCommand.CanExecute(null))
            {
                main.Sessions.LoadMoreCommand.Execute(null);
            }
        }
    }
}
