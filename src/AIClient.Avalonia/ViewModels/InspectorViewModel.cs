using System.Collections.ObjectModel;
using AIClient.Avalonia.Services;
using AIClient.Avalonia.ViewModels.Canvas;
using AIClient.Application.Interfaces;
using AIClient.Domain.Enums;
using AIClient.Domain.Graph;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIClient.Avalonia.ViewModels;

/// <summary>
/// The context surface: what the current selection is, and what can be asked about it.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the WPF app in a deliberately leaner shape. It reads the graph and never
/// writes; every answer it gives is derived from the snapshot the canvas already projects.
/// Nothing selected means the panel is not there at all - a workspace-level summary rides in
/// the shell instead of a permanently docked panel nobody is reading.
/// </para>
/// <para>
/// Fed by the shell: <c>Canvas.SelectionChanged → Inspector.Show</c>. It answers with events,
/// never with direct calls - <see cref="AiRequested"/> hands a question to the existing chat,
/// <see cref="NodeActivated"/> asks the canvas to focus a relation's other end, and
/// <see cref="CodeRequested"/> opens the file behind the node.
/// </para>
/// </remarks>
public sealed partial class InspectorViewModel : ObservableObject
{
    private readonly IGraphService _graph;
    private readonly IWorkspaceService _workspace;
    private readonly ILogger<InspectorViewModel> _logger;

    /// <summary>The history scan in flight, cancelled when the selection moves on.</summary>
    private CancellationTokenSource? _historyLoad;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private bool _isSingle;

    [ObservableProperty]
    private string _headline = string.Empty;

    [ObservableProperty]
    private string _kindLabel = string.Empty;

    [ObservableProperty]
    private string _glyph = "●";

    [ObservableProperty]
    private string _kindColour = "#8B93A5";

    [ObservableProperty]
    private string? _summary;

    [ObservableProperty]
    private string _sourceText = string.Empty;

    /// <summary>A file that is gone, or an indexer/user origin - said rather than implied.</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _originText = string.Empty;

    public ObservableCollection<Fact> Facts { get; } = [];
    public ObservableCollection<Relation> Outgoing { get; } = [];
    public ObservableCollection<Relation> Incoming { get; } = [];
    public ObservableCollection<HistoryEntry> History { get; } = [];
    public ObservableCollection<string> KindBreakdown { get; } = [];

    [ObservableProperty]
    private bool _hasSource;

    public InspectorViewModel(
        IGraphService graph,
        IWorkspaceService workspace,
        ILogger<InspectorViewModel> logger)
    {
        _graph = graph;
        _workspace = workspace;
        _logger = logger;
    }

    public event EventHandler<CanvasAiRequest>? AiRequested;

    public event EventHandler<Guid>? NodeActivated;

    public event EventHandler<Guid>? CodeRequested;

    /// <summary>Shows whatever the canvas says is selected. Empty selection hides the panel.</summary>
    public void Show(CanvasSelection selection)
    {
        _historyLoad?.Cancel();

        History.Clear();
        KindBreakdown.Clear();

        if (selection.NodeIds.Count == 0)
        {
            IsVisible = false;
            return;
        }

        IsVisible = true;

        if (selection.Node is { } node)
        {
            ShowNode(node);
        }
        else
        {
            ShowGroup(selection);
        }
    }

    private void ShowNode(GraphNode node)
    {
        IsSingle = true;
        Headline = string.IsNullOrWhiteSpace(node.Title) ? node.Key : node.Title;
        KindLabel = CanvasKindVisuals.LabelOf(node.Kind.Value);
        Glyph = CanvasKindVisuals.GlyphOf(node.Kind);
        KindColour = CanvasKindVisuals.ColourOf(node.Kind);
        Summary = node.Summary;
        SourceText = node.Source is null
            ? string.Empty
            : node.StartLine is { } start
                ? $"{node.Source.Value}:{start}"
                : node.Source.Value;
        HasSource = node.Source is not null;

        StatusText = node.Status switch
        {
            GraphNodeStatus.Missing => "The file behind this node is no longer on disk.",
            GraphNodeStatus.Archived => "Archived. Kept for the history, hidden from the roots.",
            _ => string.Empty,
        };

        OriginText = node.Origin switch
        {
            GraphOrigin.Indexer => "From the project files",
            GraphOrigin.User => "Added by hand",
            GraphOrigin.Chat => "Added from a conversation",
            GraphOrigin.Agent => "Added by an agent",
            GraphOrigin.Import => "Imported",
            _ => string.Empty,
        };

        Facts.Clear();
        foreach (var pair in node.Metadata.Take(8))
        {
            Facts.Add(new Fact(CanvasKindVisuals.LabelOf(pair.Key), pair.Value));
        }

        Outgoing.Clear();
        Incoming.Clear();

        var snapshot = _graph.Current;

        foreach (var edge in snapshot.EdgesOf(node.Id))
        {
            if (edge.FromId == node.Id &&
                snapshot.TryGetNode(edge.ToId, out var target) &&
                target is not null)
            {
                Outgoing.Add(Relation.Of(edge, target, to: true));
            }
            else if (edge.ToId == node.Id &&
                     snapshot.TryGetNode(edge.FromId, out var source) &&
                     source is not null)
            {
                Incoming.Add(Relation.Of(edge, source, to: false));
            }
        }

        LoadHistory(node.Id);
    }

    /// <summary>
    /// Deliberately no merged detail for a multi-select: a summary of what is in the band,
    /// and the actions that make sense for a band.
    /// </summary>
    private void ShowGroup(CanvasSelection selection)
    {
        IsSingle = false;
        Headline = $"{selection.NodeIds.Count} nodes · {selection.RelationCount} relations";
        KindLabel = string.Empty;
        Glyph = "◈";
        KindColour = "#8FA3BF";
        Summary = null;
        SourceText = string.Empty;
        StatusText = string.Empty;
        OriginText = string.Empty;
        HasSource = false;

        Facts.Clear();
        Outgoing.Clear();
        Incoming.Clear();

        var snapshot = _graph.Current;
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var id in selection.NodeIds)
        {
            if (snapshot.TryGetNode(id, out var node) && node is not null)
            {
                var label = CanvasKindVisuals.LabelOf(node.Kind.Value);
                kinds[label] = kinds.GetValueOrDefault(label) + 1;
            }
        }

        foreach (var line in kinds
                     .OrderByDescending(pair => pair.Value)
                     .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                     .Take(4))
        {
            KindBreakdown.Add($"{line.Value} × {line.Key}");
        }
    }

    /// <summary>
    /// The change sets that touched this node, newest first. A scan, not a query: the log is
    /// short by design and the graph knows what a mutation touched better than an index does.
    /// </summary>
    private async void LoadHistory(Guid nodeId)
    {
        var token = RestartHistory();

        try
        {
            var history = await _graph.HistoryAsync(limit: 60, cancellationToken: token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            foreach (var change in history)
            {
                if (!change.Mutations.Any(mutation => Touches(mutation, nodeId)))
                {
                    continue;
                }

                History.Add(new HistoryEntry(change.Summary, Describe(change.State, change.CreatedAt)));

                if (History.Count >= 6)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The selection moved on; the newer scan owns the panel now.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Node history could not be read.");
        }
    }

    private CancellationToken RestartHistory()
    {
        var previous = _historyLoad;
        _historyLoad = new CancellationTokenSource();
        previous?.Cancel();

        return _historyLoad!.Token;
    }

    private static bool Touches(GraphMutation mutation, Guid nodeId) => mutation switch
    {
        GraphMutation.AddNode add => add.Node.Id == nodeId,
        GraphMutation.UpdateNode update => update.Node.Id == nodeId,
        GraphMutation.RemoveNode remove => remove.NodeId == nodeId,
        GraphMutation.AddEdge add => add.Edge.FromId == nodeId || add.Edge.ToId == nodeId,
        // An edge removal is skipped rather than guessed: the row keeps only the id, and
        // "removed a relation of yours" is not worth a second lookup to be sure.
        GraphMutation.RemoveEdge => false,
        _ => false,
    };

    private static string Describe(GraphChangeState state, DateTimeOffset at) => state switch
    {
        GraphChangeState.Proposed => $"Proposed · {at.LocalDateTime:MMM d, HH:mm}",
        GraphChangeState.Applied => $"Applied · {at.LocalDateTime:MMM d, HH:mm}",
        GraphChangeState.Reverted => $"Reverted · {at.LocalDateTime:MMM d, HH:mm}",
        GraphChangeState.Discarded => $"Discarded · {at.LocalDateTime:MMM d, HH:mm}",
        _ => at.LocalDateTime.ToString("MMM d, HH:mm"),
    };

    /// <summary>Asks the AI about the node on show. Wired from the view, which has the id.</summary>
    public void AskAbout(GraphSelection selection, string? action, string label) =>
        AiRequested?.Invoke(this, new CanvasAiRequest(selection, CanvasAiPrompts.For(action), label));

    /// <summary>Reveals the file behind the shown node; the canvas answers with a notice.</summary>
    public event EventHandler? OpenSourceRequested;

    public void RaiseOpenSource() => OpenSourceRequested?.Invoke(this, EventArgs.Empty);

    public sealed record Fact(string Label, string Value);

    public sealed record Relation(Guid NodeId, string Glyph, string Colour, string Title, string KindLabel)
    {
        public static Relation Of(GraphEdge edge, GraphNode other, bool to) =>
            new(
                other.Id,
                CanvasKindVisuals.GlyphOf(other.Kind),
                CanvasKindVisuals.ColourOf(other.Kind),
                string.IsNullOrWhiteSpace(other.Title) ? other.Key : other.Title,
                $"{(to ? "→" : "←")} {CanvasKindVisuals.LabelOf(edge.Kind.Value)}");
    }

    public sealed record HistoryEntry(string Summary, string When);
}
