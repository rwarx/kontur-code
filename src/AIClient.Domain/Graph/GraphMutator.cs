using AIClient.Domain.Enums;

namespace AIClient.Domain.Graph;

/// <summary>
/// Applies a change set to a snapshot, and works out how to undo it.
/// </summary>
/// <remarks>
/// <para>
/// Pure: a snapshot and a set of mutations go in, a new snapshot comes out, nothing else is touched.
/// That is what lets the rules which actually matter - what an indexing pass may overwrite, what
/// becomes of an edge whose end is gone, what happens when two nodes claim one key - be settled in
/// one place and tested without a database and without a window.
/// </para>
/// <para>
/// Nothing is thrown. A mutation that cannot apply is refused in words and the batch carries on;
/// see <see cref="GraphApplyResult.Refused"/>.
/// </para>
/// </remarks>
public static class GraphMutator
{
    public static GraphApplyResult Apply(GraphSnapshot snapshot, GraphChangeSet change) =>
        Apply(snapshot, change.Mutations, change.Origin);

    public static GraphApplyResult Apply(
        GraphSnapshot snapshot,
        IReadOnlyList<GraphMutation> mutations,
        GraphOrigin origin = GraphOrigin.User)
    {
        var nodes = new Dictionary<Guid, GraphNode>();
        var edges = new Dictionary<Guid, GraphEdge>();

        // The two uniqueness rules storage enforces, mirrored here so that a violation is a sentence
        // rather than an exception out of SaveChanges - by which time the snapshot has already moved.
        var keys = new Dictionary<(string Kind, string Key), Guid>();
        var links = new Dictionary<(Guid From, Guid To, string Kind), Guid>();

        foreach (var node in snapshot.Nodes)
        {
            nodes[node.Id] = node;
            keys[(node.Kind.Value, node.Key)] = node.Id;
        }

        foreach (var edge in snapshot.Edges)
        {
            edges[edge.Id] = edge;
            links[(edge.FromId, edge.ToId, edge.Kind.Value)] = edge.Id;
        }

        var applied = new List<GraphMutation>();
        var inverse = new List<GraphMutation>();
        var refused = new List<string>();

        foreach (var mutation in mutations)
        {
            switch (mutation)
            {
                case GraphMutation.AddNode add:
                    PutNode(add.Node, mustExist: false);
                    break;

                case GraphMutation.UpdateNode update:
                    PutNode(update.Node, mustExist: true);
                    break;

                case GraphMutation.RemoveNode remove:
                    DropNode(remove.NodeId);
                    break;

                case GraphMutation.AddEdge add:
                    PutEdge(add.Edge);
                    break;

                case GraphMutation.RemoveEdge remove:
                    DropEdge(remove.EdgeId, ownershipMatters: true);
                    break;
            }
        }

        // Recorded forwards, undone backwards - within a single mutation as well as between them,
        // which is why a node's edges are recorded before the node itself and come back after it.
        inverse.Reverse();

        return new GraphApplyResult
        {
            // The same instance when nothing happened. Readers watch the version to decide whether
            // to rebuild, and a new snapshot over an unchanged graph makes every one of them work
            // for nothing.
            Snapshot = applied.Count == 0
                ? snapshot
                : GraphSnapshot.Create(nodes.Values, edges.Values, snapshot.Version + 1),
            Applied = applied,
            Inverse = inverse,
            Refused = refused,
        };

        // Whether this change is allowed to touch something that is already there. The invariant the
        // whole design leans on: an indexing pass owns only what it created. Without it, the second
        // walk of the workspace erases every link a person drew, and the graph is no more durable
        // than a cache.
        bool MayTouch(GraphOrigin owner) =>
            origin != GraphOrigin.Indexer || owner == GraphOrigin.Indexer;

        void PutNode(GraphNode node, bool mustExist)
        {
            nodes.TryGetValue(node.Id, out var existing);

            if (existing is null && mustExist)
            {
                refused.Add($"There is no node {node.Id} to update.");
                return;
            }

            if (existing is not null && !MayTouch(existing.Origin))
            {
                refused.Add(
                    $"A change from {origin} may not alter the {existing.Origin}-owned node {existing.Key}.");
                return;
            }

            if (keys.TryGetValue((node.Kind.Value, node.Key), out var holder) && holder != node.Id)
            {
                refused.Add($"The key {node.Kind.Value}:{node.Key} already belongs to another node.");
                return;
            }

            // A node whose key changed - a file that moved - gives up the old one, or nothing could
            // ever take that key again.
            if (existing is not null && (existing.Kind != node.Kind || existing.Key != node.Key))
            {
                keys.Remove((existing.Kind.Value, existing.Key));
            }

            nodes[node.Id] = node;
            keys[(node.Kind.Value, node.Key)] = node.Id;

            applied.Add(existing is null
                ? new GraphMutation.AddNode(node)
                : new GraphMutation.UpdateNode(node));

            inverse.Add(existing is null
                ? new GraphMutation.RemoveNode(node.Id)
                : new GraphMutation.UpdateNode(existing));
        }

        void DropNode(Guid nodeId)
        {
            if (!nodes.TryGetValue(nodeId, out var existing))
            {
                refused.Add($"There is no node {nodeId} to remove.");
                return;
            }

            if (!MayTouch(existing.Origin))
            {
                refused.Add(
                    $"A change from {origin} may not remove the {existing.Origin}-owned node {existing.Key}.");
                return;
            }

            // Ownership is not consulted for the cascade. A hand-drawn edge is worth keeping, but not
            // at the price of a row pointing at a node that is gone - and if this change is undone,
            // the inverse brings the edge back exactly as it was.
            foreach (var edge in edges.Values.Where(e => e.FromId == nodeId || e.ToId == nodeId).ToList())
            {
                DropEdge(edge.Id, ownershipMatters: false);
            }

            nodes.Remove(nodeId);

            if (keys.TryGetValue((existing.Kind.Value, existing.Key), out var holder) && holder == nodeId)
            {
                keys.Remove((existing.Kind.Value, existing.Key));
            }

            applied.Add(new GraphMutation.RemoveNode(nodeId));
            inverse.Add(new GraphMutation.AddNode(existing));
        }

        void PutEdge(GraphEdge edge)
        {
            // A relationship from a thing to itself is either a mistake or a tautology, and a canvas
            // has nowhere to draw it.
            if (edge.FromId == edge.ToId)
            {
                refused.Add($"A {edge.Kind.Value} edge from a node to itself says nothing.");
                return;
            }

            if (!nodes.ContainsKey(edge.FromId) || !nodes.ContainsKey(edge.ToId))
            {
                refused.Add($"A {edge.Kind.Value} edge needs both of its nodes to exist.");
                return;
            }

            edges.TryGetValue(edge.Id, out var existing);

            if (existing is not null && !MayTouch(existing.Origin))
            {
                refused.Add($"A change from {origin} may not alter a {existing.Origin}-owned edge.");
                return;
            }

            if (links.TryGetValue((edge.FromId, edge.ToId, edge.Kind.Value), out var holder)
                && holder != edge.Id)
            {
                refused.Add($"Those two nodes are already joined by a {edge.Kind.Value} edge.");
                return;
            }

            if (existing is not null)
            {
                links.Remove((existing.FromId, existing.ToId, existing.Kind.Value));
            }

            edges[edge.Id] = edge;
            links[(edge.FromId, edge.ToId, edge.Kind.Value)] = edge.Id;

            applied.Add(new GraphMutation.AddEdge(edge));

            inverse.Add(existing is null
                ? new GraphMutation.RemoveEdge(edge.Id)
                : new GraphMutation.AddEdge(existing));
        }

        void DropEdge(Guid edgeId, bool ownershipMatters)
        {
            if (!edges.TryGetValue(edgeId, out var existing))
            {
                refused.Add($"There is no edge {edgeId} to remove.");
                return;
            }

            if (ownershipMatters && !MayTouch(existing.Origin))
            {
                refused.Add($"A change from {origin} may not remove a {existing.Origin}-owned edge.");
                return;
            }

            edges.Remove(edgeId);

            if (links.TryGetValue((existing.FromId, existing.ToId, existing.Kind.Value), out var holder)
                && holder == edgeId)
            {
                links.Remove((existing.FromId, existing.ToId, existing.Kind.Value));
            }

            applied.Add(new GraphMutation.RemoveEdge(edgeId));
            inverse.Add(new GraphMutation.AddEdge(existing));
        }
    }
}
