using System.Text.Json;
using AIClient.Domain.Entities;
using AIClient.Domain.Graph;
using AIClient.Domain.Workspace;

namespace AIClient.Infrastructure.Graph;

/// <summary>
/// Converts between the graph as the application reasons about it and the graph as SQLite holds it.
/// </summary>
/// <remarks>
/// <para>
/// The one place that knows both shapes. Everything above it works with immutable records, parsed
/// paths and a metadata dictionary; everything below it works with mutable rows of strings and
/// integers. Keeping the translation here is what lets the domain stay free of storage concerns and
/// the schema stay free of domain ones.
/// </para>
/// <para>
/// Reading is forgiving in the same way <see cref="Application.Services.AgentTranscript"/> is: this
/// text came off a disk, possibly written by an older build, and a row that cannot be understood
/// perfectly is worth more degraded than it is worth refusing to open the project over. A path that
/// no longer parses becomes no path; metadata that will not deserialise becomes no metadata.
/// </para>
/// </remarks>
internal static class GraphRowMapper
{
    private static readonly JsonSerializerOptions Format = new(JsonSerializerDefaults.Web);

    public static GraphNode ToDomain(GraphNodeRow row) => new()
    {
        Id = row.Id,
        Kind = GraphNodeKind.From(row.Kind),
        Key = row.Key,
        Title = row.Title,
        Summary = row.Summary,
        Source = ParsePath(row.SourcePath),
        StartLine = row.StartLine,
        EndLine = row.EndLine,
        Metadata = ReadMetadata(row.MetadataJson),
        Status = row.Status,
        Origin = row.Origin,
        SourceExecutionId = row.SourceExecutionId,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
    };

    public static GraphEdge ToDomain(GraphEdgeRow row) => new()
    {
        Id = row.Id,
        FromId = row.FromId,
        ToId = row.ToId,
        Kind = GraphEdgeKind.From(row.Kind),
        Label = row.Label,
        Order = row.Order,
        Origin = row.Origin,
        SourceExecutionId = row.SourceExecutionId,
        CreatedAt = row.CreatedAt,
    };

    public static GraphNodeRow ToRow(GraphNode node) => Fill(new GraphNodeRow { Id = node.Id }, node);

    public static GraphEdgeRow ToRow(GraphEdge edge) => Fill(new GraphEdgeRow { Id = edge.Id }, edge);

    /// <summary>
    /// Copies a node onto an existing row, leaving the row's identity alone.
    /// </summary>
    /// <remarks>
    /// Used for the update half of an upsert, where the row is already tracked by the context and
    /// replacing the instance would be a delete and an insert - which would take the placements and
    /// the edges pointing at it down with them.
    /// </remarks>
    public static GraphNodeRow Fill(GraphNodeRow row, GraphNode node)
    {
        row.Kind = node.Kind.Value;
        row.Key = node.Key;
        row.Title = node.Title;
        row.Summary = node.Summary;

        // ToString rather than Value: the root's canonical value is the empty string, which will not
        // parse back, and "." will.
        row.SourcePath = node.Source?.ToString();
        row.StartLine = node.StartLine;
        row.EndLine = node.EndLine;
        row.MetadataJson = WriteMetadata(node.Metadata);
        row.Status = node.Status;
        row.Origin = node.Origin;
        row.SourceExecutionId = node.SourceExecutionId;
        row.CreatedAt = node.CreatedAt;
        row.UpdatedAt = node.UpdatedAt;

        return row;
    }

    public static GraphEdgeRow Fill(GraphEdgeRow row, GraphEdge edge)
    {
        row.FromId = edge.FromId;
        row.ToId = edge.ToId;
        row.Kind = edge.Kind.Value;
        row.Label = edge.Label;
        row.Order = edge.Order;
        row.Origin = edge.Origin;
        row.SourceExecutionId = edge.SourceExecutionId;
        row.CreatedAt = edge.CreatedAt;

        return row;
    }

    private static WorkspacePath? ParsePath(string? raw) =>
        string.IsNullOrEmpty(raw) ? null : WorkspacePath.TryParse(raw, out var path, out _) ? path : null;

    private static string? WriteMetadata(IReadOnlyDictionary<string, string> metadata) =>
        metadata.Count == 0 ? null : JsonSerializer.Serialize(metadata, Format);

    private static IReadOnlyDictionary<string, string> ReadMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return GraphNode.NoMetadata;
        }

        try
        {
            var read = JsonSerializer.Deserialize<Dictionary<string, string>>(json, Format);

            return read is null or { Count: 0 }
                ? GraphNode.NoMetadata
                : new Dictionary<string, string>(read, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return GraphNode.NoMetadata;
        }
    }
}
