using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using AIClient.App.Services;
using AIClient.App.ViewModels.Canvas;
using AIClient.Application.Interfaces;
using AIClient.Domain.Enums;
using AIClient.Domain.Graph;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIClient.App.ViewModels;

/// <summary>
/// What the graph knows about the current selection, and what can be asked about it.
/// </summary>
/// <remarks>
/// <para>
/// Reads the graph and never writes it. Everything shown here is a fact already in
/// <see cref="IGraphService.Current"/> or in the change log; the panel has no state of its own worth
/// persisting, which is why closing it loses nothing.
/// </para>
/// <para>
/// It is fed by the shell rather than by the canvas directly - child view models in this project do
/// not hold references to each other - and its AI actions leave as an event for the same reason. The
/// question travels to the existing chat; nothing here talks to a provider.
/// </para>
/// </remarks>
public sealed partial class InspectorViewModel : ObservableObject
{
    /// <summary>How far back the change log is searched for entries about one node.</summary>
    private const int HistorySearched = 60;

    /// <summary>How many of those are shown. The panel is a summary, not the timeline.</summary>
    private const int HistoryShown = 6;

    /// <summary>Metadata rows worth showing before the list stops being scannable.</summary>
    private const int FactsShown = 8;

    private readonly IGraphService _graph;
    private readonly IWorkspaceService _workspace;
    private readonly ISettingsService _settings;
    private readonly ILogger<InspectorViewModel> _logger;

    private readonly List<Guid> _nodeIds = [];

    private GraphNode? _node;
    private int _relationCount;

    /// <summary>Cancels the history read for a node the person has already navigated away from.</summary>
    private CancellationTokenSource? _historyLoad;

    /// <summary>
    /// True when there is something to inspect. The panel takes no room otherwise, so an unselected
    /// canvas is the whole window.
    /// </summary>
    [ObservableProperty]
    private bool _isVisible;

    /// <summary>Exactly one node - the full detail view.</summary>
    [ObservableProperty]
    private bool _isSingle;

    /// <summary>Several nodes - counts and the AI actions, because per-field detail would lie.</summary>
    [ObservableProperty]
    private bool _isMulti;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _kindLabel = string.Empty;

    [ObservableProperty]
    private string _glyph = "●";

    [ObservableProperty]
    private Brush _kindBrush = Brushes.Gray;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    private string? _summary;

    /// <summary>The path and line span, shown as one line under the header.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSource))]
    [NotifyCanExecuteChangedFor(nameof(OpenSourceCommand))]
    [NotifyCanExecuteChangedFor(nameof(ViewCodeCommand))]
    private string? _sourceText;

    /// <summary>Only set when the status is worth a word: a missing file, an archived node.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string? _statusText;

    /// <summary>"Indexed", "Added by you", "Added by an agent" - where this fact came from.</summary>
    [ObservableProperty]
    private string _originText = string.Empty;

    /// <summary>"3 nodes · 4 relations" for a group selection.</summary>
    [ObservableProperty]
    private string _headline = string.Empty;

    /// <summary>"2 files · 1 folder" - what the group is made of.</summary>
    [ObservableProperty]
    private string _kindBreakdown = string.Empty;

    [ObservableProperty]
    private bool _isHistoryLoading;

    [ObservableProperty]
    private string? _notice;

    public InspectorViewModel(
        IGraphService graph,
        IWorkspaceService workspace,
        ISettingsService settings,
        ILogger<InspectorViewModel> logger)
    {
        _graph = graph;
        _workspace = workspace;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// A question about the selection, on its way to the existing chat.
    /// </summary>
    /// <remarks>
    /// The same record the canvas raises, so the shell has one handler and there is one path from a
    /// selection to a model.
    /// </remarks>
    public event EventHandler<CanvasAiRequest>? AiRequested;

    /// <summary>A related node was clicked; the canvas should select and centre it.</summary>
    public event EventHandler<Guid>? NodeActivated;

    /// <summary>
    /// The inspected node's file should be shown.
    /// </summary>
    /// <remarks>
    /// An event rather than a call, for the same reason as the two above: the code panel belongs to
    /// the canvas, and this view model does not know the canvas exists.
    /// </remarks>
    public event EventHandler<Guid>? CodeRequested;

    /// <summary>Relations pointing away from this node, by kind.</summary>
    public ObservableCollection<InspectorRelation> Outgoing { get; } = [];

    public ObservableCollection<InspectorRelation> Incoming { get; } = [];

    /// <summary>Whatever the indexer or a model thought worth recording.</summary>
    public ObservableCollection<InspectorFact> Facts { get; } = [];

    /// <summary>Change log entries that touched this node, newest first.</summary>
    public ObservableCollection<InspectorHistoryEntry> History { get; } = [];

    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);

    public bool HasSource => !string.IsNullOrWhiteSpace(SourceText);

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);

    /// <summary>
    /// Shows a selection, or hides the panel when there is none.
    /// </summary>
    /// <remarks>
    /// Called for every selection change, including the ones made mid-drag, so it stays cheap: the
    /// graph is already in memory and only the change log is read asynchronously.
    /// </remarks>
    public void Show(CanvasSelection selection)
    {
        _nodeIds.Clear();
        _nodeIds.AddRange(selection.NodeIds);
        _node = selection.Node;
        _relationCount = selection.RelationCount;

        Notice = null;
        IsVisible = _nodeIds.Count > 0;
        IsSingle = _node is not null;
        IsMulti = IsVisible && _node is null;

        CancelHistory();
        History.Clear();

        if (_node is { } node)
        {
            ShowNode(node);
            return;
        }

        Outgoing.Clear();
        Incoming.Clear();
        Facts.Clear();

        if (IsMulti)
        {
            ShowGroup();
        }
    }

    /// <summary>Hides the panel and forgets everything, so the canvas gets the whole window back.</summary>
    public void Clear() => Show(new CanvasSelection([], null, 0));

    private void ShowNode(GraphNode node)
    {
        Title = string.IsNullOrWhiteSpace(node.Title) ? node.Key : node.Title;
        KindLabel = CanvasKindVisuals.LabelOf(node.Kind.Value);
        Glyph = CanvasKindVisuals.GlyphOf(node.Kind);
        KindBrush = CanvasKindVisuals.BrushOf(node.Kind);
        Summary = node.Summary;
        SourceText = SourceLine(node);
        StatusText = StatusLine(node.Status);
        OriginText = OriginLine(node.Origin);
        Headline = KindLabel;

        FillFacts(node);
        FillRelations(node);

        _ = LoadHistoryAsync(node.Id);
    }

    /// <summary>
    /// What a group selection says about itself: how big it is, and what it is made of.
    /// </summary>
    /// <remarks>
    /// Deliberately not a merged detail view. Averaging or concatenating fields across nodes invents
    /// a thing the graph does not contain, and the useful question about a group is the AI one.
    /// </remarks>
    private void ShowGroup()
    {
        Title = Localization.T("S.Inspector.Group.Nodes", _nodeIds.Count);
        KindLabel = Localization.T("S.Inspector.Group.Selection");
        Glyph = "◫";
        KindBrush = Brushes.Gray;
        Summary = null;
        SourceText = null;
        StatusText = null;
        OriginText = string.Empty;

        Headline = _relationCount > 0
            ? Localization.T("S.Inspector.Group.NodesRelations", _nodeIds.Count, _relationCount)
            : Localization.T("S.Inspector.Group.Nodes", _nodeIds.Count);

        var snapshot = _graph.Current;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var id in _nodeIds)
        {
            if (snapshot.TryGetNode(id, out var node) && node is not null)
            {
                var label = CanvasKindVisuals.LabelOf(node.Kind.Value);
                counts[label] = counts.TryGetValue(label, out var seen) ? seen + 1 : 1;
            }
        }

        KindBreakdown = string.Join(
            " · ",
            counts.OrderByDescending(pair => pair.Value)
                  .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                  .Take(4)
                  .Select(pair => $"{pair.Value} {Plural(pair.Key, pair.Value)}"));
    }

    /// <summary>Crude but honest pluralisation - the kind labels in this app are all regular.</summary>
    private static string Plural(string label, int count) =>
        count == 1 ? label.ToLowerInvariant() : label.ToLowerInvariant() + "s";

    private void FillFacts(GraphNode node)
    {
        Facts.Clear();

        foreach (var pair in node.Metadata.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Take(FactsShown))
        {
            Facts.Add(new InspectorFact(CanvasKindVisuals.LabelOf(pair.Key), pair.Value));
        }
    }

    /// <summary>
    /// Both directions, because the interesting question is often the incoming one - "what breaks if
    /// I change this" is answered by the arrows pointing at a node, not the ones leaving it.
    /// </summary>
    private void FillRelations(GraphNode node)
    {
        Outgoing.Clear();
        Incoming.Clear();

        var snapshot = _graph.Current;

        foreach (var edge in snapshot.Outgoing(node.Id))
        {
            if (Row(snapshot, edge, edge.ToId, "→") is { } row)
            {
                Outgoing.Add(row);
            }
        }

        foreach (var edge in snapshot.Incoming(node.Id))
        {
            if (Row(snapshot, edge, edge.FromId, "←") is { } row)
            {
                Incoming.Add(row);
            }
        }

        static InspectorRelation? Row(GraphSnapshot snapshot, GraphEdge edge, Guid otherId, string arrow)
        {
            if (snapshot.Node(otherId) is not { } other)
            {
                return null;
            }

            var kind = string.IsNullOrWhiteSpace(edge.Label)
                ? CanvasKindVisuals.LabelOf(edge.Kind.Value)
                : edge.Label!;

            return new InspectorRelation(
                kind,
                arrow,
                string.IsNullOrWhiteSpace(other.Title) ? other.Key : other.Title,
                CanvasKindVisuals.GlyphOf(other.Kind),
                CanvasKindVisuals.BrushOf(other.Kind),
                other.Id);
        }
    }

    private static string? SourceLine(GraphNode node)
    {
        if (node.Source is not { } source || source.IsRoot)
        {
            return null;
        }

        var path = source.Value;

        return node.StartLine is { } start
            ? node.EndLine is { } end && end > start ? $"{path} : {start}-{end}" : $"{path} : {start}"
            : path;
    }

    private static string? StatusLine(GraphNodeStatus status) => status switch
    {
        GraphNodeStatus.Missing => Localization.T("S.Inspector.Status.Missing"),
        GraphNodeStatus.Archived => Localization.T("S.Inspector.Status.Archived"),
        _ => null,
    };

    /// <summary>
    /// Provenance in a person's words. Worth a line of its own: whether a fact came from the code or
    /// from a model changes how much it should be trusted.
    /// </summary>
    private static string OriginLine(GraphOrigin origin) => origin switch
    {
        GraphOrigin.Indexer => Localization.T("S.Inspector.Origin.Indexer"),
        GraphOrigin.User => Localization.T("S.Inspector.Origin.User"),
        GraphOrigin.Chat => Localization.T("S.Inspector.Origin.Chat"),
        GraphOrigin.Agent => Localization.T("S.Inspector.Origin.Agent"),
        GraphOrigin.Import => Localization.T("S.Inspector.Origin.Import"),
        _ => string.Empty,
    };

    /// <summary>
    /// The entries in the change log that touched this node.
    /// </summary>
    /// <remarks>
    /// Filtered in memory over a bounded window rather than queried, because the log is a list of
    /// change sets with their mutations serialised inside them - there is no column to index on a
    /// node id, and adding one would mean a table whose only purpose is this panel. Sixty entries is
    /// well past what six rows can show.
    /// </remarks>
    private async Task LoadHistoryAsync(Guid nodeId)
    {
        var cts = new CancellationTokenSource();
        _historyLoad = cts;
        IsHistoryLoading = true;

        try
        {
            var log = await _graph.HistoryAsync(null, HistorySearched, cts.Token).ConfigureAwait(true);

            if (cts.IsCancellationRequested)
            {
                return;
            }

            History.Clear();

            foreach (var change in log)
            {
                if (History.Count == HistoryShown)
                {
                    break;
                }

                if (!change.Mutations.Any(mutation => Touches(mutation, nodeId)))
                {
                    continue;
                }

                var when = change.AppliedAt ?? change.CreatedAt;

                History.Add(new InspectorHistoryEntry(
                    change.Summary,
                    when.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture),
                    OriginLine(change.Origin)));
            }
        }
        catch (OperationCanceledException)
        {
            // The selection moved on. Nothing to report.
        }
        catch (Exception ex)
        {
            // A panel section that cannot be filled is not worth a dialog, and the message would
            // carry a database path.
            _logger.LogWarning(ex, "The change log could not be read for the inspector.");
        }
        finally
        {
            if (ReferenceEquals(_historyLoad, cts))
            {
                _historyLoad = null;
                IsHistoryLoading = false;
                cts.Dispose();
            }
        }
    }

    /// <summary>
    /// Whether a mutation is about this node.
    /// </summary>
    /// <remarks>
    /// <see cref="GraphMutation.RemoveEdge"/> carries only an edge id, so an edge that is already
    /// gone cannot be attributed to either end - it is skipped rather than guessed at. The default
    /// arm is the same choice for a mutation kind added later: not shown beats wrongly shown.
    /// </remarks>
    private static bool Touches(GraphMutation mutation, Guid nodeId) => mutation switch
    {
        GraphMutation.AddNode add => add.Node.Id == nodeId,
        GraphMutation.UpdateNode update => update.Node.Id == nodeId,
        GraphMutation.RemoveNode remove => remove.NodeId == nodeId,
        GraphMutation.AddEdge edge => edge.Edge.FromId == nodeId || edge.Edge.ToId == nodeId,
        _ => false,
    };

    private void CancelHistory()
    {
        var running = _historyLoad;
        _historyLoad = null;
        running?.Cancel();
        running?.Dispose();
        IsHistoryLoading = false;
    }

    public void OnLanguageChanged()
    {
    }

    /// <summary>
    /// Hands the selection and a question to the chat.
    /// </summary>
    /// <remarks>
    /// Identical in effect to the same action on the canvas: one event, one chat, one context build.
    /// The nodes are re-read from the snapshot rather than kept alongside the ids, because between
    /// selecting something and asking about it an indexing pass may have changed what it is.
    /// </remarks>
    [RelayCommand]
    private void AskAi(string? action)
    {
        if (_nodeIds.Count == 0)
        {
            return;
        }

        var graph = _graph.Current;
        var selection = GraphSelection.Nodes([.. _nodeIds], _settings.Current.Canvas.ContextDepth);
        var target = CanvasAiPrompts.Describe([.. _nodeIds.Select(graph.Node).OfType<GraphNode>()]);
        var label = IsSingle ? Title : Headline;

        AiRequested?.Invoke(
            this,
            new CanvasAiRequest(
                selection,
                CanvasAiPrompts.For(action, target.Subject),
                label,
                target.Files));
    }

    /// <summary>Reveals the file behind the inspected node.</summary>
    [RelayCommand(CanExecute = nameof(HasSource))]
    private void OpenSource()
    {
        if (_node is not { } node)
        {
            return;
        }

        Notice = SourceLauncher.Reveal(_workspace.Root, node);
    }

    /// <summary>Shows the file behind the inspected node without leaving the application.</summary>
    /// <remarks>
    /// Next to <see cref="OpenSource"/> rather than instead of it: reading a few lines and going to
    /// the file in Explorer are different intentions, and the second one is how you get to an editor.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(HasSource))]
    private void ViewCode()
    {
        if (_node is { } node)
        {
            CodeRequested?.Invoke(this, node.Id);
        }
    }

    /// <summary>Jumps the canvas to a related node.</summary>
    [RelayCommand]
    private void Activate(Guid nodeId)
    {
        if (nodeId != Guid.Empty)
        {
            NodeActivated?.Invoke(this, nodeId);
        }
    }
}

/// <summary>One row in the relations list.</summary>
/// <remarks>
/// Carries the other node's glyph and colour so the list reads like the canvas does - the same
/// shape and the same colour for the same kind of thing, in both places.
/// </remarks>
public sealed record InspectorRelation(
    string Kind,
    string Arrow,
    string Title,
    string Glyph,
    Brush Brush,
    Guid NodeId);

/// <summary>One metadata row: whatever the indexer or a model recorded.</summary>
public sealed record InspectorFact(string Name, string Value);

/// <summary>One change log entry, as the inspector shows it.</summary>
public sealed record InspectorHistoryEntry(string Summary, string When, string Origin);
