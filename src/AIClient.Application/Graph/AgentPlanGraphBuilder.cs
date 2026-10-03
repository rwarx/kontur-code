using AIClient.Application.DTOs;
using AIClient.Domain.Graph;

namespace AIClient.Application.Graph;

/// <summary>
/// Turns the plan a planning run submits into the change set a canvas draws: a plan node, a node per
/// part, containment from the plan to its parts, and dependencies between the parts.
/// </summary>
/// <remarks>
/// <para>
/// Pure and host-free on purpose. The shape of a drawn plan - what a node is, where it lands, which
/// edges join it - is the same whether the canvas is WPF's or the sidecar's, and it is the one part of
/// the pipeline worth testing on its own: the sink around it only applies the set and persists it, and
/// the mutator it hands the set to is tested elsewhere. Keeping the geometry here, behind a static call
/// that takes a plan and the current graph and returns a <see cref="GraphChangeSet"/>, lets both hosts
/// draw the same picture and lets a test assert it without a graph service, a workspace or a UI thread.
/// </para>
/// <para>
/// The subgraph lands beside the existing content rather than on top of it, and its parts are fanned
/// into a column so the drawing is readable the moment it appears rather than a stack the user has to
/// pull apart. Ids carry a per-plan stamp so two plans with the same part names never merge into one
/// shape; a genuine name collision within one plan still collides, and the mutator refusing the
/// duplicate id is the honest outcome for two parts claiming one identity.
/// </para>
/// </remarks>
public static class AgentPlanGraphBuilder
{
    private const double PlanWidth = 220;
    private const double PlanHeight = 64;
    private const double PartWidth = 200;
    private const double PartHeight = 56;

    /// <summary>Horizontal gap from the existing content's right edge to the plan node's centre.</summary>
    private const double AnchorGap = 360;

    /// <summary>Horizontal gap from the plan node to the column of parts.</summary>
    private const double ColumnGap = 320;

    /// <summary>Vertical distance between one part's centre and the next.</summary>
    private const double RowSpacing = 92;

    /// <summary>
    /// Builds the change set for a plan, positioned relative to <paramref name="current"/> so it does
    /// not land on top of whatever is already drawn.
    /// </summary>
    public static GraphChangeSet Build(AgentPlan plan, GraphSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(current);

        var changes = new List<GraphChange>();
        var stamp = Guid.NewGuid().ToString("N")[..8];
        var planId = $"plan:{stamp}";

        // Anchor beside the existing content: to the right of its rightmost edge, level with its
        // vertical middle. An empty graph draws from the origin.
        var planX = 0.0;
        var planY = 0.0;

        if (current.Nodes.Count > 0)
        {
            planX = current.Nodes.Max(n => n.X + n.Width / 2) + AnchorGap;
            planY = current.Nodes.Average(n => n.Y);
        }

        var parts = plan.Parts;

        changes.Add(new AddNode(new GraphNode
        {
            Id = planId,
            Kind = GraphNodeKind.Plan,
            Title = Trim(plan.Title, 60),
            Subtitle = parts.Count > 0 ? $"{parts.Count} parts" : null,
            Detail = plan.Goal,
            X = planX,
            Y = planY,
            Width = PlanWidth,
            Height = PlanHeight,
        }));

        if (parts.Count == 0)
        {
            return Set(plan, changes);
        }

        // The parts sit in one column to the right of the plan, centred on it, so the plan reads as the
        // head of the group and the "plans" edges all fan out in the same direction.
        var columnX = planX + ColumnGap;
        var firstRowY = planY - (parts.Count - 1) * RowSpacing / 2.0;

        var partIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var partId = $"part:{stamp}:{Trim(part.Name, 40)}";

            // Last writer wins for a repeated name, matching the id derivation: the second node carries
            // the same id and the mutator rejects it, which is the intended refusal for a collision.
            partIds[part.Name] = partId;

            changes.Add(new AddNode(new GraphNode
            {
                Id = partId,
                Kind = MapKind(part.Kind),
                Title = Trim(part.Name, 48),
                Subtitle = part.Path,
                Detail = part.Purpose,
                Path = part.Path,
                X = columnX,
                Y = firstRowY + i * RowSpacing,
                Width = PartWidth,
                Height = PartHeight,
            }));
        }

        foreach (var part in parts)
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
                // A dependency naming another part draws an edge; one naming nothing (or itself) is left
                // undrawn rather than turned into a dangling or self-referential edge the mutator would
                // reject. The plan still keeps the name - it is on the part - it simply has no line.
                if (partIds.TryGetValue(dependency, out var dependencyId) && dependencyId != partId)
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

        return Set(plan, changes);
    }

    private static GraphChangeSet Set(AgentPlan plan, List<GraphChange> changes) => new()
    {
        Title = $"Agent plan: {Trim(plan.Title, 60)}",
        Description = plan.Goal,
        Origin = GraphChangeOrigin.Agent,
        Changes = changes,
    };

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
