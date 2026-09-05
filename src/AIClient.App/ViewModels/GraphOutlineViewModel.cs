using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AIClient.App.Controls;
using AIClient.App.Services;
using AIClient.Application.Interfaces;
using AIClient.Domain.Graph;

namespace AIClient.App.ViewModels;

/// <summary>
/// The graph's outline: the same canvas content as a structure, for finding a thing by
/// name rather than by eye.
/// </summary>
/// <remarks>
/// A spatial canvas is the best view of a graph until you want "where is
/// AuthenticationMiddleware" - then a grouped, alphabetical list is. The outline and the
/// canvas are two projections of one snapshot: selecting here selects there, and the
/// reverse, so the outline doubles as a keyboard-friendly selector for the canvas.
/// </remarks>
public sealed partial class GraphOutlineViewModel : ObservableObject
{
    private readonly IGraphService _graph;
    private readonly CanvasViewModel _canvas;

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    private bool _isEmpty = true;

    public ObservableCollection<OutlineGroupViewModel> Groups { get; } = [];

    /// <summary>Raised when a row is chosen: the host focuses the node on the canvas.</summary>
    public event EventHandler<string>? NodeActivated;

    public GraphOutlineViewModel(IGraphService graph, CanvasViewModel canvas)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(canvas);

        _graph = graph;
        _canvas = canvas;

        _graph.SnapshotChanged += OnSnapshotChanged;

        Rebuild();
    }

    partial void OnFilterChanged(string value) => Rebuild();

    private void OnSnapshotChanged(object? sender, GraphSnapshot snapshot) => UiThread.Post(Rebuild);

    [RelayCommand]
    private void ClearFilter() => Filter = string.Empty;

    private void Rebuild()
    {
        Groups.Clear();

        var snapshot = _graph.Current;
        IsEmpty = snapshot.Nodes.Count == 0;

        var query = string.IsNullOrWhiteSpace(Filter)
            ? snapshot.Nodes
            : snapshot.Nodes.Where(node =>
                node.Title.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                || (node.Path?.Contains(Filter, StringComparison.OrdinalIgnoreCase) ?? false));

        foreach (var group in query
                     .GroupBy(node => node.Kind)
                     .OrderBy(group => group.Key))
        {
            var groupViewModel = new OutlineGroupViewModel(
                group.Key,
                WorkspaceIcons.ForNodeKind(group.Key));

            foreach (var node in group
                         .OrderBy(node => node.Title, StringComparer.OrdinalIgnoreCase)
                         .Take(300))
            {
                var row = new OutlineNodeViewModel(node);
                row.Activated += OnRowActivated;
                groupViewModel.Nodes.Add(row);
            }

            Groups.Add(groupViewModel);
        }
    }

    private void OnRowActivated(object? sender, string nodeId)
    {
        _canvas.Controller.SetSelection(AIClient.App.Canvas.SelectionMode.Replace, nodeId);
        NodeActivated?.Invoke(this, nodeId);
    }
}

/// <summary>One kind bucket in the outline, with its count and its glyph.</summary>
public sealed class OutlineGroupViewModel
{
    public OutlineGroupViewModel(GraphNodeKind kind, IconKind icon)
    {
        Kind = kind;
        Icon = icon;
    }

    public GraphNodeKind Kind { get; }

    public IconKind Icon { get; }

    public string Title => Kind.ToString().ToLowerInvariant();

    public ObservableCollection<OutlineNodeViewModel> Nodes { get; } = [];

    public int Count => Nodes.Count;
}

/// <summary>One node in the outline: the name, the path, an activation click.</summary>
public sealed class OutlineNodeViewModel
{
    public OutlineNodeViewModel(GraphNode node)
    {
        NodeId = node.Id;
        Title = node.Title;
        Path = node.Path;
        IsSelected = false;
    }

    public string NodeId { get; }

    public string Title { get; }

    public string? Path { get; }

    public bool IsSelected { get; set; }

    public event EventHandler<string>? Activated;

    public void Activate() => Activated?.Invoke(this, NodeId);
}
