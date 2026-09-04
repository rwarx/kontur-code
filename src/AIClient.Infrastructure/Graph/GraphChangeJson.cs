using System.Text.Json;
using AIClient.Domain.Entities;
using AIClient.Domain.Graph;

namespace AIClient.Infrastructure.Graph;

/// <summary>
/// Reads and writes the mutation list of a change log entry.
/// </summary>
/// <remarks>
/// <para>
/// The row shapes are reused as the wire format rather than a second set of DTOs. They already hold
/// exactly the primitives that survive a round trip, <see cref="GraphRowMapper"/> already knows how
/// to convert them, and a parallel hierarchy of json records would be one more place to forget a
/// field when a node gains one.
/// </para>
/// <para>
/// Reading is all-or-nothing, which is the one place this file departs from the forgiving tone
/// elsewhere. A half-read mutation list is worse than none: applied as an inverse it would leave the
/// graph somewhere between the two states, silently, with nothing to report. So a damaged entry
/// yields nothing and the caller says so - the timeline shows an entry it cannot detail, and undo
/// refuses instead of half-working.
/// </para>
/// </remarks>
internal static class GraphChangeJson
{
    private const string AddNode = "add_node";
    private const string UpdateNode = "update_node";
    private const string RemoveNode = "remove_node";
    private const string AddEdge = "add_edge";
    private const string RemoveEdge = "remove_edge";

    private static readonly JsonSerializerOptions Format = new(JsonSerializerDefaults.Web);

    public static string Write(IReadOnlyList<GraphMutation> mutations) =>
        JsonSerializer.Serialize(mutations.Select(ToDto).ToList(), Format);

    /// <summary>
    /// Parses a stored mutation list. False when the text is there but cannot be trusted.
    /// </summary>
    public static bool TryRead(string? json, out IReadOnlyList<GraphMutation> mutations)
    {
        mutations = [];

        if (string.IsNullOrWhiteSpace(json))
        {
            // An empty inverse is the ordinary state of a proposal, not damage.
            return true;
        }

        List<MutationDto>? read;

        try
        {
            read = JsonSerializer.Deserialize<List<MutationDto>>(json, Format);
        }
        catch (JsonException)
        {
            return false;
        }

        if (read is null)
        {
            return false;
        }

        var parsed = new List<GraphMutation>(read.Count);

        foreach (var dto in read)
        {
            if (ToMutation(dto) is not { } mutation)
            {
                return false;
            }

            parsed.Add(mutation);
        }

        mutations = parsed;
        return true;
    }

    private static MutationDto ToDto(GraphMutation mutation) => mutation switch
    {
        GraphMutation.AddNode add => new MutationDto { Op = AddNode, Node = GraphRowMapper.ToRow(add.Node) },
        GraphMutation.UpdateNode edit => new MutationDto { Op = UpdateNode, Node = GraphRowMapper.ToRow(edit.Node) },
        GraphMutation.RemoveNode drop => new MutationDto { Op = RemoveNode, Id = drop.NodeId },
        GraphMutation.AddEdge add => new MutationDto { Op = AddEdge, Edge = GraphRowMapper.ToRow(add.Edge) },
        GraphMutation.RemoveEdge drop => new MutationDto { Op = RemoveEdge, Id = drop.EdgeId },

        // Unreachable while the hierarchy stays closed; here so that adding a case and forgetting
        // this file fails loudly in a test rather than quietly in a user's change log.
        _ => throw new NotSupportedException($"No json shape for {mutation.GetType().Name}."),
    };

    private static GraphMutation? ToMutation(MutationDto dto) => dto switch
    {
        { Op: AddNode, Node: { } node } => new GraphMutation.AddNode(GraphRowMapper.ToDomain(node)),
        { Op: UpdateNode, Node: { } node } => new GraphMutation.UpdateNode(GraphRowMapper.ToDomain(node)),
        { Op: RemoveNode, Id: { } id } => new GraphMutation.RemoveNode(id),
        { Op: AddEdge, Edge: { } edge } => new GraphMutation.AddEdge(GraphRowMapper.ToDomain(edge)),
        { Op: RemoveEdge, Id: { } id } => new GraphMutation.RemoveEdge(id),
        _ => null,
    };

    /// <summary>One mutation on the wire: the operation, and whichever payload it needs.</summary>
    private sealed record MutationDto
    {
        public string Op { get; init; } = string.Empty;

        public GraphNodeRow? Node { get; init; }

        public GraphEdgeRow? Edge { get; init; }

        /// <summary>The identity, for the two operations that carry nothing else.</summary>
        public Guid? Id { get; init; }
    }
}
