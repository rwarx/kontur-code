using AIClient.Application.DTOs;
using AIClient.Application.Graph;
using AIClient.Application.Interfaces;
using AIClient.Domain.Graph;

namespace AIClient.Server;

/// <summary>
/// The sidecar's half of <see cref="AgentMode.PlanCanvas"/>: takes the plan a planning run submits and
/// draws it onto the shared graph, so a plan reaches the Electron canvas rather than only the transcript.
/// </summary>
/// <remarks>
/// <para>
/// It is the server counterpart of the WPF shell's canvas sink, and the reason the Electron build's
/// <c>submit_plan</c> was, until now, only ever text: Infrastructure registers <see cref="TranscriptPlanSink"/>
/// by default, whose <see cref="IAgentPlanSink.CanDraw"/> is false, and nothing overrode it here. This
/// class does, from <c>Program.cs</c>, on the same last-registration-wins footing as the approval gate.
/// </para>
/// <para>
/// It draws without asking. In the WPF host the question is put on the UI thread through a dialog, but a
/// planning run has no UI thread and <c>submit_plan</c> is a read-risk tool the user reached for on
/// purpose - an approval card in front of the plan they just asked for would be noise, and the summary
/// says as much. The change set goes through <see cref="IGraphService.ApplyAsync"/> like every other
/// writer, so the drawing is undoable and counted on the timeline; it is then persisted at once, because
/// the model is about to tell the user the plan is on the canvas and a crash on the next line must not
/// make a liar of it. The renderer learns to re-pull the graph from the <c>submit_plan</c> tool-finished
/// event, so no new wire event is needed.
/// </para>
/// </remarks>
public sealed class ServerCanvasPlanSink : IAgentPlanSink
{
    private readonly IGraphService _graph;
    private readonly GraphPersistence _persistence;
    private readonly ILogger<ServerCanvasPlanSink> _logger;

    public ServerCanvasPlanSink(
        IGraphService graph,
        GraphPersistence persistence,
        ILogger<ServerCanvasPlanSink> logger)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(logger);

        _graph = graph;
        _persistence = persistence;
        _logger = logger;
    }

    /// <summary>The canvas always exists in this build; the honest answer is yes.</summary>
    public bool CanDraw => true;

    public async Task<AgentPlanAcceptance> AcceptAsync(AgentPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var changeSet = AgentPlanGraphBuilder.Build(plan, _graph.Current);
        var result = await _graph.ApplyAsync(changeSet, cancellationToken).ConfigureAwait(false);

        if (result.Applied.Count == 0)
        {
            _logger.LogWarning(
                "Plan '{Title}' drew nothing: all {Rejected} change(s) were rejected.",
                plan.Title, result.Rejected.Count);

            return AgentPlanAcceptance.NotDrawn(
                "Drawing the plan was refused by the graph (every change was rejected), so it was not "
                + "drawn. Tell the user the plan is in the conversation, and do not point them at a canvas.");
        }

        // Persisted immediately under the open workspace's key: a plan the user is about to be told is
        // drawn must survive a crash, and this is the sidecar's equivalent of the WPF host's SaveAsync.
        await _persistence.SaveCurrentAsync(cancellationToken).ConfigureAwait(false);

        var nodeCount = changeSet.Changes.OfType<AddNode>().Count();

        _logger.LogInformation(
            "Plan '{Title}' drawn: {Applied} change(s) applied, {Rejected} rejected.",
            plan.Title, result.Applied.Count, result.Rejected.Count);

        return AgentPlanAcceptance.DrawnOn(
            $"The plan is drawn on the canvas beside the chat ({nodeCount} nodes): tell the user it is "
            + "there and what its parts are, briefly.");
    }
}
