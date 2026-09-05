using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AIClient.App.Canvas;
using AIClient.App.Controls;
using AIClient.App.ViewModels;
using AIClient.Domain.Graph;

namespace AIClient.App.Views;

/// <summary>
/// The canvas mode's view layer: attaching the renderer to the controller, framing
/// commands, and the context menu that follows whatever is under the pointer.
/// </summary>
/// <remarks>
/// <para>
/// Fit and focus need the element's actual size, so the view answers the view model's
/// requests for them rather than the view model measuring anything itself.
/// </para>
/// <para>
/// The context menu is built per-invocation: a node's menu and the background's menu are
/// different menus, and a WPF <see cref="ContextMenu"/> is cheap to build once per click
/// compared to keeping one in sync with selection state.
/// </para>
/// </remarks>
public partial class CanvasView : UserControl
{
    private CanvasViewModel? _canvasVm;

    public CanvasView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (_canvasVm is not null)
        {
            _canvasVm.FitRequested -= OnFitRequested;
            _canvasVm.FocusSelectionRequested -= OnFocusSelectionRequested;
            _canvasVm.DetachFrom(Surface);
        }

        if (e.NewValue is not WorkspaceViewModel workspace)
        {
            _canvasVm = null;
            return;
        }

        _canvasVm = workspace.Canvas;

        _canvasVm.AttachTo(Surface);
        MiniMap.SetController(workspace.Canvas.Controller);

        _canvasVm.FitRequested += OnFitRequested;
        _canvasVm.FocusSelectionRequested += OnFocusSelectionRequested;

        // The first attach happens before layout: one fit when the surface has a size,
        // so a freshly loaded graph opens framed rather than at origin.
        if (Surface.ActualWidth > 0 && workspace.Canvas.NodeCount > 0)
        {
            OnFitRequested(this, EventArgs.Empty);
        }
        else
        {
            Surface.Loaded += OnSurfaceLoaded;
        }

        Surface.ContextMenu = BuildContextMenu();
    }

    private void OnSurfaceLoaded(object sender, RoutedEventArgs e)
    {
        Surface.Loaded -= OnSurfaceLoaded;

        if (_canvasVm is { NodeCount: > 0 })
        {
            OnFitRequested(this, EventArgs.Empty);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_canvasVm is not null)
        {
            _canvasVm.FitRequested -= OnFitRequested;
            _canvasVm.FocusSelectionRequested -= OnFocusSelectionRequested;
            _canvasVm.DetachFrom(Surface);
        }
    }

    private void OnFitRequested(object? sender, EventArgs e)
    {
        if (_canvasVm is null)
        {
            return;
        }

        var bounds = GraphProjection.ContentBounds(_canvasVm.Snapshot);

        if (bounds.IsEmpty)
        {
            return;
        }

        var (zoom, offsetX, offsetY) = _canvasVm.Controller.ComputeFit(
            bounds, new Size(Surface.ActualWidth, Surface.ActualHeight));

        _canvasVm.Controller.SetViewport(zoom, offsetX, offsetY);
    }

    private void OnFocusSelectionRequested(object? sender, EventArgs e)
    {
        if (_canvasVm is null)
        {
            return;
        }

        var bounds = GraphProjection.SelectionBounds(
            _canvasVm.Snapshot, [.. _canvasVm.Controller.SelectedNodeIds]);

        if (bounds.IsEmpty)
        {
            return;
        }

        var (zoom, offsetX, offsetY) = _canvasVm.Controller.ComputeFit(
            bounds, new Size(Surface.ActualWidth, Surface.ActualHeight), padding: 120);

        _canvasVm.Controller.SetViewport(zoom, offsetX, offsetY);
    }

    // --------------------------------------------------------- context menu

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        // Node items.
        var inspect = new MenuItem { Header = "Inspect", Icon = KonturIcon(IconKind.Node) };
        inspect.Click += OnInspectClicked;

        var open = new MenuItem { Header = "Open file", Icon = KonturIcon(IconKind.Open), InputGestureText = "Enter" };
        open.Click += OnOpenClicked;

        var askAi = new MenuItem { Header = "Ask AI about this", Icon = KonturIcon(IconKind.Sparkle), InputGestureText = "Ctrl+I" };
        askAi.Click += OnAskAiClicked;

        var remove = new MenuItem { Header = "Remove from canvas", Icon = KonturIcon(IconKind.Trash), InputGestureText = "Del" };
        remove.Click += OnRemoveClicked;

        var separator = new Separator();

        // Background items.
        var fit = new MenuItem { Header = "Fit to view", Icon = KonturIcon(IconKind.Fit) };
        fit.Click += (_, _) => OnFitRequested(this, EventArgs.Empty);

        var refresh = new MenuItem { Header = "Refresh workspace graph", Icon = KonturIcon(IconKind.Refresh) };
        refresh.Click += OnRefreshClicked;

        menu.Items.Add(inspect);
        menu.Items.Add(open);
        menu.Items.Add(askAi);
        menu.Items.Add(remove);
        menu.Items.Add(separator);
        menu.Items.Add(fit);
        menu.Items.Add(refresh);

        menu.Opened += OnMenuOpened;

        return menu;
    }

    private static KonturIcon KonturIcon(IconKind kind) => new() { Kind = kind, IsHitTestVisible = false };

    private void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || _canvasVm is null || DataContext is not WorkspaceViewModel workspace)
        {
            return;
        }

        var hover = _canvasVm.Controller.HoverNodeId;
        var isNode = hover is not null;
        var node = isNode ? _canvasVm.Snapshot.TryGetNode(hover!, out var found) ? found : null : null;

        // Only the items that mean something now are shown: a context menu that shrugs
        // half its rows greyed out is a menu that has not been opened yet.
        SetVisible(menu.Items[0], isNode);
        SetVisible(menu.Items[1], node?.Path is not null);
        SetVisible(menu.Items[2], isNode);
        SetVisible(menu.Items[3], isNode);
        SetVisible(menu.Items[4], isNode);
        SetVisible(menu.Items[5], !isNode && _canvasVm.NodeCount > 0);
        SetVisible(menu.Items[6], workspace.HasWorkspace);
    }

    private static void SetVisible(object item, bool visible) =>
        ((UIElement)item).Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private void OnInspectClicked(object sender, RoutedEventArgs e)
    {
        if (_canvasVm?.Controller.HoverNodeId is { } id)
        {
            _canvasVm.Controller.SetSelection(AIClient.App.Canvas.SelectionMode.Replace, id);
        }
    }

    private void OnOpenClicked(object sender, RoutedEventArgs e)
    {
        if (_canvasVm is null || DataContext is not WorkspaceViewModel workspace)
        {
            return;
        }

        var hover = _canvasVm.Controller.HoverNodeId;

        if (hover is null || !_canvasVm.Snapshot.TryGetNode(hover, out var node) || node.Path is null)
        {
            return;
        }

        workspace.Context.OpenPathCommand.Execute(node.Path);
    }

    private void OnAskAiClicked(object sender, RoutedEventArgs e)
    {
        if (_canvasVm?.Controller.HoverNodeId is { } id)
        {
            _canvasVm.Controller.SetSelection(AIClient.App.Canvas.SelectionMode.Replace, id);
        }

        _canvasVm?.AskAiCommand.Execute(null);
    }

    private void OnRemoveClicked(object sender, RoutedEventArgs e)
    {
        if (_canvasVm?.Controller.HoverNodeId is { } id)
        {
            _canvasVm.Controller.SetSelection(AIClient.App.Canvas.SelectionMode.Replace, id);
            _canvasVm.Controller.DeleteSelection();
        }
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel workspace)
        {
            _ = workspace.RefreshGraphCommand.ExecuteAsync(null);
        }
    }
}
