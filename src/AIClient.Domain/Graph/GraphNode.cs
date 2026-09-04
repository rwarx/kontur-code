using AIClient.Domain.Enums;
using AIClient.Domain.Workspace;

namespace AIClient.Domain.Graph;

/// <summary>
/// One thing the project consists of, as the graph knows it.
/// </summary>
/// <remarks>
/// <para>
/// Immutable, and separate from the row that persists it. A snapshot of the graph is handed to the
/// UI thread, to a context build and to an agent step while an indexing pass is applying changes
/// underneath; if those readers held the tracked entity, they would see half-applied edits. The
/// entity is the write side, this is the read side, and the mapping between them is deliberately
/// dull.
/// </para>
/// <para>
/// A node never holds file contents. <see cref="Source"/> and the line span are a reference to be
/// resolved through the workspace sandbox when someone actually needs the text, which keeps the
/// graph small and keeps every read subject to the same containment and size rules.
/// </para>
/// </remarks>
public sealed record GraphNode
{
    /// <summary>
    /// The shared empty metadata, and the default for a node that records no extras.
    /// </summary>
    /// <remarks>
    /// One instance rather than one per node. Indexing a real repository produces tens of thousands
    /// of nodes and most of them carry nothing here, so the allocation is worth avoiding - and
    /// having a name for "nothing was recorded" keeps a reader that finds no metadata on disk from
    /// inventing its own empty dictionary with its own comparer.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> NoMetadata =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase).AsReadOnly();

    public required Guid Id { get; init; }

    public required GraphNodeKind Kind { get; init; }

    /// <summary>
    /// Stable identity within the kind, and the reason a re-index does not renumber the graph.
    /// </summary>
    /// <remarks>
    /// Canonical and derived, not decorative: <c>src/Auth/AuthService.cs</c> for a file,
    /// <c>AIClient.Auth.AuthService</c> for a type, <c>AIClient.Auth.AuthService.Login(string)</c>
    /// for a member, a bare GUID for something a person or a model invented. Unique per kind, so an
    /// indexing pass upserts by it and every canvas placement and hand-drawn link survives.
    /// </remarks>
    public required string Key { get; init; }

    /// <summary>Short label, which is what a canvas card and an inspector header show.</summary>
    public required string Title { get; init; }

    /// <summary>One or two sentences. Null when nobody has written one.</summary>
    public string? Summary { get; init; }

    /// <summary>Where the thing lives, when it lives in a file at all.</summary>
    public WorkspacePath? Source { get; init; }

    /// <summary>1-based first line of the span this node covers. Null for a whole file.</summary>
    public int? StartLine { get; init; }

    public int? EndLine { get; init; }

    /// <summary>
    /// Open-ended extras: a language, a namespace, a status colour, whatever an indexer or an agent
    /// found worth recording. Flat and stringly typed on purpose - a schema here would have to be
    /// migrated every time a new kind of node appeared.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = NoMetadata;

    public GraphNodeStatus Status { get; init; } = GraphNodeStatus.Active;

    public GraphOrigin Origin { get; init; } = GraphOrigin.User;

    /// <summary>The agent run that created this node, when one did.</summary>
    public Guid? SourceExecutionId { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>True when an indexing pass owns this node and may therefore change or remove it.</summary>
    public bool IsIndexerOwned => Origin == GraphOrigin.Indexer;
}
