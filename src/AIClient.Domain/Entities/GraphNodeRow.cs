using AIClient.Domain.Enums;

namespace AIClient.Domain.Entities;

/// <summary>
/// One stored node of the knowledge graph.
/// </summary>
/// <remarks>
/// <para>
/// A row, not the model. <see cref="Graph.GraphNode"/> is what the application reasons about:
/// immutable, holding a parsed <see cref="Domain.Workspace.WorkspacePath"/> and a metadata
/// dictionary. This class is the shape SQLite can hold - mutable, strings and integers - and the
/// <c>Row</c> suffix is there so that no file ever has both names in scope and has to guess which
/// one it is looking at.
/// </para>
/// <para>
/// Separating them is what keeps the storage rules out of the domain: <see cref="Kind"/> is text
/// because a new kind must not be a migration, <see cref="Status"/> and <see cref="Origin"/> are
/// integers because their names are allowed to change, and none of that leaks into the mutator.
/// </para>
/// </remarks>
public sealed class GraphNodeRow
{
    /// <summary>UUIDv7 so that insertion order and key order agree, which keeps the index dense.</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Canonical kind text, e.g. <c>file</c> or <c>decision</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// The stable identity: <c>src/Auth/AuthService.cs</c> under kind <c>file</c>, a fully
    /// qualified name under kind <c>class</c>, the node's own id for anything a person invented.
    /// </summary>
    /// <remarks>
    /// Unique together with <see cref="Kind"/>. That pair is why re-indexing is an upsert rather
    /// than a rebuild, and therefore why <see cref="Id"/> survives it - and with the id, every
    /// canvas placement and every hand-drawn edge pointing at this node.
    /// </remarks>
    public string Key { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>One or two sentences for a human and for a model. Never file contents.</summary>
    public string? Summary { get; set; }

    /// <summary>Workspace-relative path, or null for a node that is not a place on disk.</summary>
    public string? SourcePath { get; set; }

    public int? StartLine { get; set; }
    public int? EndLine { get; set; }

    /// <summary>Open-ended extras as a JSON object of string values, or null.</summary>
    public string? MetadataJson { get; set; }

    public GraphNodeStatus Status { get; set; } = GraphNodeStatus.Active;

    public GraphOrigin Origin { get; set; } = GraphOrigin.User;

    /// <summary>The agent run that produced this node, when one did.</summary>
    public Guid? SourceExecutionId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
