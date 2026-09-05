using AIClient.App.Services;
using AIClient.Application.DTOs;
using AIClient.Application.Graph;
using AIClient.Application.Interfaces;
using AIClient.Domain.Graph;
using Microsoft.Extensions.Logging;

namespace AIClient.App.Graph;

/// <summary>
/// The canvas half of <see cref="AgentMode.PlanCanvas"/>: takes the plan the agent submits
/// and turns it into graph the user can see, question and undo.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline the product promises is AI → proposal → change set → accept/reject →
/// GraphMutator → timeline, and this class is the proposal step. It never mutates the
/// graph directly: the plan becomes a <see cref="GraphChangeSet"/> and goes through
/// <see cref="IGraphService.ApplyAsync"/> like every other change, so the drawing is
/// undoable, persisted and counted in the timeline exactly like a user edit. The one
/// decision the agent is not allowed to make for the user - whether to draw at all - is
/// asked out loud, on the UI thread, through the same dialog service the rest of the
/// app asks questions with.
/// </para>
/// <para>
/// It is registered over <see cref="TranscriptPlanSink"/> in the App composition, and
/// <see cref="CanDraw"/> is honest rather than optimistic: the canvas always exists in
/// this build, so the answer is always yes.
/// </para>
/// </remarks>
public sealed class CanvasPlanSink : IAgentPlanSink
{
    private readonly IGraphService _graph;
    private readonly IDialogService _dialogs;
    private readonly ILogger<CanvasPlanSink> _logger;
    private Func<string?>? _workspaceRoot;

    public CanvasPlanSink(
        IGraphService graph,
        IDialogService dialogs,
        ILogger<CanvasPlanSink> logger)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(logger);

        _graph = graph;
        _dialogs = dialogs;
        _logger = logger;
    }

    public bool CanDraw => true;

    /// <summary>
    /// Lets the composition hand the sink the workspace root without giving the sink a
    /// service reference it would only use once.
    /// </summary>
    public void SetWorkspaceRoot(Func<string?> root) => _workspaceRoot = root;

    public async Task<AgentPlanAcceptance> AcceptAsync(AgentPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var changeSet = BuildChangeSet(plan, _graph.Current);

        // The question has to be asked on the thread that owns the windows; the agent
        // loop is a background task and would otherwise deadlock or throw on the UI
        // assert, exactly like the approval gate it sits beside.
        var accepted = await UiThread.RunAsync(() => AskAsync(plan, changeSet)).ConfigureAwait(false);

        if (!accepted)
        {
            return AgentPlanAcceptance.NotDrawn(
                "The user chose not to draw this plan, so it lives in the conversation only. "
                + "Do not tell them to look at the canvas.");
        }

        // Apply runs on the UI thread alongside every other graph writer; the work itself
        // is in-memory diffing, so the hop is about ordering, not offloading.
        var result = await UiThread.RunAsync(() => _graph.ApplyAsync(changeSet, cancellationToken)).ConfigureAwait(false);

        if (result.Applied.Count == 0)
        {
            return AgentPlanAcceptance.NotDrawn(
                "Drawing the plan was refused by the graph (every change was rejected), so it was not "
                + "drawn. Tell the user the plan is in the conversation.");
        }

        // Persisted immediately: a plan the user accepted must survive a crash on the
        // next line, because the agent is about to tell them it is drawn.
        var key = WorkspaceGraphKeys.FromWorkspaceRoot(_workspaceRoot?.Invoke());
        await _graph.SaveAsync(key, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Plan '{Title}' drawn: {Applied} changes applied, {Rejected} rejected.",
            plan.Title, result.Applied.Count, result.Rejected.Count);

        return AgentPlanAcceptance.DrawnOn(
            $"The plan is drawn on the canvas ({changeSet.Changes.OfType<AddNode>().Count()} nodes): "
            + "tell the user it is there and what its parts are, briefly.");
    }

    /// <summary>One question, in plain words, with the shape of what would be drawn.</summary>
    private async Task<bool> AskAsync(AgentPlan plan, GraphChangeSet changeSet)
    {
        var nodeCount = changeSet.Changes.OfType<AddNode>().Count();
        var edgeCount = changeSet.Changes.OfType<AddEdge>().Count();

        return await _dialogs.ConfirmAsync(
            "Draw this plan on the canvas?",
            $"'{Trim(plan.Title, 80)}' — {nodeCount} nodes, {edgeCount} connections. "
            + "You can undo it from the timeline.",
            "Draw").ConfigureAwait(true);
    }

    /// <summary>
    /// Turns a plan into a change set: a plan node, a node per part, containment from the
    /// plan to its parts and dependencies between parts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ids carry a per-plan stamp so two plans with the same names never merge into one
    /// shape; the mutator would refuse the duplicate ids of a name collision anyway, and
    /// refusing is the honest outcome for two plans claiming one identity.
    /// </para>
    /// <para>
    /// Parts with a path keep it, so a drawn node is a real thing on disk the inspector
    /// can open. The subgraph lands beside the existing content rather than on top of it:
    /// a new plan should not have to be dragged out from under yesterday's workspace
    /// before it can be read.
    /// </para>
    /// </remarks>
    private static GraphChangeSet BuildChangeSet(AgentPlan plan, GraphSnapshot current)
    {
        var changes = new List<GraphChange>();
        var stamp = Guid.NewGuid().ToString("N")[..8];
        var planId = $"plan:{stamp}";

        var anchorX = 0.0;
        var anchorY = 0.0;

        if (current.Nodes.Count > 0)
        {
            var right = current.Nodes.Max(n => n.X + n.Width / 2);
            var top = current.Nodes.Min(n => n.Y - n.Height / 2);
            anchorX = right + 320;
            anchorY = top;
        }

        changes.Add(new AddNode(new GraphNode
        {
            Id = planId,
            Kind = GraphNodeKind.Plan,
            Title = Trim(plan.Title, 60),
            Subtitle = plan.Parts is { Count: > 0 } ? $"{plan.Parts.Count} parts" : null,
            Detail = plan.Goal,
            X = anchorX,
            Y = anchorY,
            Width = 220,
            Height = 64,
        }));

        var partIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (plan.Parts is { Count: > 0 })
        {
            foreach (var part in plan.Parts)
            {
                var partId = $"part:{stamp}:{Trim(part.Name, 40)}";
                partIds[part.Name] = partId;

                changes.Add(new AddNode(new GraphNode
                {
                    Id = partId,
                    Kind = MapKind(part.Kind),
                    Title = Trim(part.Name, 48),
                    Subtitle = part.Path,
                    Detail = part.Purpose,
                    Path = part.Path,
                    X = anchorX,
                    Y = anchorY,
                    Width = 190,
                    Height = 56,
                }));
            }

            foreach (var part in plan.Parts)
            {
                if (!partIds.TryGetValue(part.Name, out var partId))
                {
                    continue;
                }

                changes.Add(new AddEdge(new GraphEdge
                {
                    Id = $"pe:{stamp}:{partId}",
                    SourceId = planId,
                    TargetId = partId,
                    Kind = GraphEdgeKind.Plans,
                }));

                foreach (var dependency in part.DependsOn)
                {
                    if (partIds.TryGetValue(dependency, out var dependencyId))
                    {
                        changes.Add(new AddEdge(new GraphEdge
                        {
                            Id = $"pd:{stamp}:{partId}:{dependencyId}",
                            SourceId = partId,
                            TargetId = dependencyId,
                            Kind = GraphEdgeKind.Depends,
                        }));
                    }
                }
            }
        }

        return new GraphChangeSet
        {
            Title = $"Agent plan: {Trim(plan.Title, 60)}",
            Description = plan.Goal,
            Origin = GraphChangeOrigin.Agent,
            Changes = changes,
        };
    }

    private static GraphNodeKind MapKind(AgentPlanPartKind kind) => kind switch
    {
        AgentPlanPartKind.Folder => GraphNodeKind.Folder,
        AgentPlanPartKind.File => GraphNodeKind.File,
        AgentPlanPartKind.Module => GraphNodeKind.Module,
        AgentPlanPartKind.Service => GraphNodeKind.Service,
        AgentPlanPartKind.Interface => GraphNodeKind.Interface,
        AgentPlanPartKind.Data => GraphNodeKind.Data,
        AgentPlanPartKind.View => GraphNodeKind.View,
        AgentPlanPartKind.Test => GraphNodeKind.Test,
        AgentPlanPartKind.External => GraphNodeKind.External,
        _ => GraphNodeKind.Note,
    };

    private static string Trim(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty
        : text.Trim().Length <= max ? text.Trim()
        : text.Trim()[..(max - 1)] + "…";
}
