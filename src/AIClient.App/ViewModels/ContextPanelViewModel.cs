using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AIClient.App.Canvas;
using AIClient.App.Controls;
using AIClient.App.Services;
using AIClient.Application.Interfaces;
using AIClient.Domain.Graph;

namespace AIClient.App.ViewModels;

/// <summary>
/// The right-hand context surface: whatever the user is looking at, inspected.
/// </summary>
/// <remarks>
/// <para>
/// This is the surface that makes the product feel like one system: nothing selected, it
/// explains the workspace; a node selected, it is a node inspector; an edge, a
/// relationship inspector; many nodes, a summary; the agent working, an activity log.
/// One panel, five shapes, chosen by what deserves the user's attention right now -
/// never a dashboard of permanently-mounted, permanently-ignored boxes.
/// </para>
/// <para>
/// The view model is deliberately read-only about the graph: inspecting opens things and
/// asks the AI questions through events, and edits go through the same change sets every
/// other surface uses. An inspector that quietly mutates what it inspects is how "context"
/// becomes "a second editor nobody asked for".
/// </para>
/// </remarks>
public sealed partial class ContextPanelViewModel : ObservableObject
{
    private readonly IGraphService _graph;
    private readonly CanvasViewModel _canvas;

    [ObservableProperty]
    private ContextPanelMode _mode = ContextPanelMode.Workspace;

    // Node inspector state.
    [ObservableProperty]
    private GraphNode? _inspectedNode;

    [ObservableProperty]
    private GraphEdge? _inspectedEdge;

    [ObservableProperty]
    private string _inspectedNodeKind = string.Empty;

    [ObservableProperty]
    private ObservableCollection<NodeRelationRow> _relations = [];

    [ObservableProperty]
    private int _selectionCount;

    [ObservableProperty]
    private string _selectionKinds = string.Empty;

    // Workspace overview state.
    [ObservableProperty]
    private string _workspaceName = string.Empty;

    [ObservableProperty]
    private string _workspaceRoot = string.Empty;

    [ObservableProperty]
    private bool _hasWorkspace;

    [ObservableProperty]
    private string _graphSummary = "Empty";

    [ObservableProperty]
    private ObservableCollection<GraphTimelineEntry> _timeline = [];

    // AI activity state.
    [ObservableProperty]
    private bool _isAiWorking;

    [ObservableProperty]
    private string _aiStateText = "Idle";

    [ObservableProperty]
    private string _aiModelName = string.Empty;

    [ObservableProperty]
    private bool _isApprovalPending;

    /// <summary>Actions the host routes: focusing canvas, opening files, asking the AI.</summary>
    public event EventHandler<string>? FocusNodeRequested;

    public event EventHandler<string>? OpenPathRequested;

    public event EventHandler<string>? AskAiRequested;

    public ContextPanelViewModel(IGraphService graph, CanvasViewModel canvas)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(canvas);

        _graph = graph;
        _canvas = canvas;

        _canvas.Controller.StateChanged += OnCanvasStateChanged;
        _canvas.NodeActivated += OnNodeActivated;
        _graph.SnapshotChanged += OnGraphSnapshotChanged;
        _graph.TimelineChanged += OnTimelineChanged;

        RefreshTimeline();
        Reinspect();
    }

    private void OnGraphSnapshotChanged(object? sender, GraphSnapshot snapshot) => UiThread.Post(Reinspect);

    private void OnTimelineChanged(object? sender, EventArgs e) => UiThread.Post(RefreshTimeline);

    private void OnNodeActivated(object? sender, string nodeId)
    {
        if (_graph.Current.TryGetNode(nodeId, out var node))
        {
            Inspect(node);
        }
    }

    private void OnCanvasStateChanged(object? sender, EventArgs e) => Reinspect();

    /// <summary>Re-derives the panel's shape from the current canvas and graph state.</summary>
    /// <remarks>Priority order matters: a pending approval outranks an idle selection, which
    /// outranks the workspace overview, because the user's attention ranks the same way.</remarks>
    private void Reinspect()
    {
        var controller = _canvas.Controller;
        var snapshot = _graph.Current;

        if (IsApprovalPending)
        {
            Mode = ContextPanelMode.AiActivity;
            return;
        }

        if (controller.SelectedEdgeId is { } edgeId && snapshot.TryGetEdge(edgeId, out var edge))
        {
            Inspect(edge);
            return;
        }

        var selection = controller.SelectedNodeIds;

        if (selection.Count == 1
            && snapshot.TryGetNode(selection.First(), out var node))
        {
            Inspect(node);
            return;
        }

        if (selection.Count > 1)
        {
            Mode = ContextPanelMode.Selection;
            SelectionCount = selection.Count;

            var kinds = snapshot.Nodes
                .Where(n => selection.Contains(n.Id))
                .Select(n => n.Kind)
                .GroupBy(kind => kind)
                .OrderByDescending(group => group.Count())
                .Select(group => $"{group.Count()} {group.Key.ToString().ToLowerInvariant()}")
                .Take(4);

            SelectionKinds = string.Join(" · ", kinds);
            return;
        }

        Mode = ContextPanelMode.Workspace;
        RebuildWorkspaceSummary();
    }

    private void Inspect(GraphNode node)
    {
        Mode = ContextPanelMode.Node;
        InspectedNode = node;
        InspectedNodeKind = node.Kind.ToString().ToLowerInvariant();
        RebuildRelations(node);
    }

    private void Inspect(GraphEdge edge)
    {
        Mode = ContextPanelMode.Edge;
        InspectedEdge = edge;
        Relations.Clear();
    }

    private void RebuildRelations(GraphNode node)
    {
        Relations.Clear();

        foreach (var edge in _graph.Current.EdgesOf(node.Id))
        {
            var otherId = edge.SourceId == node.Id ? edge.TargetId : edge.SourceId;

            if (!_graph.Current.TryGetNode(otherId, out var other))
            {
                continue;
            }

            Relations.Add(new NodeRelationRow(
                edge.Id,
                other.Id,
                other.Title,
                other.Kind,
                edge.Kind.ToString().ToLowerInvariant(),
                edge.SourceId == node.Id));
        }
    }

    private void RebuildWorkspaceSummary()
    {
        var snapshot = _graph.Current;

        if (snapshot.Nodes.Count == 0)
        {
            GraphSummary = "Empty canvas";
            return;
        }

        var folderCount = snapshot.Nodes.Count(n => n.Kind == GraphNodeKind.Folder);
        var fileCount = snapshot.Nodes.Count - folderCount;

        GraphSummary = $"{snapshot.Nodes.Count} nodes · {snapshot.Edges.Count} connections"
            + (folderCount > 0 ? $" · {folderCount} folders" : string.Empty)
            + (fileCount > 0 ? $" · {fileCount} files" : string.Empty);
    }

    private void RefreshTimeline()
    {
        Timeline.Clear();

        foreach (var entry in _graph.Timeline.Take(12))
        {
            Timeline.Add(entry);
        }
    }

    // ------------------------------------------------------------ workspace

    /// <summary>The host publishes workspace identity here; the overview reads it.</summary>
    public void SetWorkspace(string? root)
    {
        HasWorkspace = root is not null;
        WorkspaceRoot = root ?? string.Empty;
        WorkspaceName = root is null
            ? "No workspace"
            : System.IO.Path.GetFileName(root.TrimEnd(System.IO.Path.DirectorySeparatorChar));

        Reinspect();
    }

    /// <summary>The host publishes the AI's live state here.</summary>
    public void SetAiState(bool isWorking, string stateText, string modelName, bool approvalPending)
    {
        IsAiWorking = isWorking;
        AiStateText = stateText;
        AiModelName = modelName;
        IsApprovalPending = approvalPending;

        // An approval arriving must promote the panel immediately; a completion must not
        // yank it away from whatever the user selected while waiting.
        if (approvalPending)
        {
            Mode = ContextPanelMode.AiActivity;
        }
        else if (Mode == ContextPanelMode.AiActivity && !isWorking)
        {
            Reinspect();
        }
    }

    // ------------------------------------------------------------- commands

    [RelayCommand]
    private void FocusNode(string? nodeId)
    {
        if (nodeId is not null)
        {
            FocusNodeRequested?.Invoke(this, nodeId);
        }
    }

    [RelayCommand]
    private void OpenPath(string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            OpenPathRequested?.Invoke(this, path);
        }
    }

    [RelayCommand]
    private void CopyTitle()
    {
        if (InspectedNode is { Title: { } title })
        {
            // The clipboard can be briefly locked by another process; the retry mirrors
            // what DialogService.CopyToClipboard does, without taking a service
            // dependency for one line of code.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    System.Windows.Clipboard.SetText(title);
                    return;
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    Thread.Sleep(40);
                }
            }
        }
    }

    [RelayCommand]
    private void AskExplain() => AskAiRequested?.Invoke(this, "Explain this node's role in the architecture");

    [RelayCommand]
    private void AskRelations() => AskAiRequested?.Invoke(this, "Explain this node's relationships and what depends on it");

    [RelayCommand]
    private void AskTests() => AskAiRequested?.Invoke(this, "Suggest tests for this node's behaviour");

    [RelayCommand]
    private void AskAboutSelection() => AskAiRequested?.Invoke(this, "Explain this selection and how the parts relate");

    /// <summary>A relation row's activation, as a command so the view can route clicks.</summary>
    [RelayCommand]
    private void RelationRowActivated(NodeRelationRow row) => FocusNodeRequested?.Invoke(this, row.NodeId);
}

/// <summary>One line of a node's relations list: the other node, the edge's kind and direction.</summary>
public sealed record NodeRelationRow(
    string EdgeId,
    string NodeId,
    string Title,
    GraphNodeKind Kind,
    string EdgeKind,
    bool IsOutgoing)
{
    public IconKind Icon => WorkspaceIcons.ForNodeKind(Kind);

    public string KindLabel => Kind.ToString().ToLowerInvariant();

    public string Direction => IsOutgoing ? "→" : "←";
}

public enum ContextPanelMode
{
    Workspace,
    Node,
    Edge,
    Selection,
    AiActivity,
}
