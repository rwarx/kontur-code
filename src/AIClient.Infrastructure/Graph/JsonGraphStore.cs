using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIClient.Application.Configuration;
using AIClient.Application.Interfaces;
using AIClient.Domain.Graph;
using Microsoft.Extensions.Logging;

namespace AIClient.Infrastructure.Graph;

/// <summary>
/// Saves graphs as JSON under the application data directory: one file per key in
/// <c>graphs/</c>.
/// </summary>
/// <remarks>
/// <para>
/// One document per workspace, written whole. Writing the whole snapshot is what makes
/// save, load and restore the same operation, and a graph is small enough that partial
/// writes would buy nothing but their own failure modes.
/// </para>
/// <para>
/// Every write is atomic - a temporary file, then a move over the target - because the
/// application can be closed mid-save, and half a graph is worse than the previous whole
/// one: the user cannot see what is missing from a canvas that loads.
/// </para>
/// <para>
/// Enum values are written as strings (<see cref="JsonStringEnumConverter"/>) so a file
/// written before a kind was renamed fails to load loudly rather than silently reshaping
/// the graph: an unknown word cannot become a number without somebody noticing, while a
/// renamed kind read back as its old ordinal would quietly draw every node in the wrong
/// colour forever.
/// </para>
/// <para>
/// A file that fails to parse is reported as absent (a warning is logged, null is
/// returned) rather than as an error, because a corrupt graph is recoverable by
/// re-indexing the workspace and a crash loop at startup is not. The same applies to a
/// file that is implausibly large: more nodes than this application would ever write is
/// treated as not ours, and left alone.
/// </para>
/// </remarks>
public sealed class JsonGraphStore : IGraphStore
{
    /// <summary>Directory under the data directory that holds one graph file per key.</summary>
    private const string DirectoryName = "graphs";

    /// <summary>
    /// How long a key may be and still become a file name as itself.
    /// </summary>
    /// <remarks>
    /// Well under the 255-character file-name ceiling, so a long-but-legal key never fails
    /// at the file system; longer ones fall back to their hash, which is short by
    /// construction.
    /// </remarks>
    private const int MaxKeyLength = 100;

    /// <summary>
    /// More nodes than this in a file means the file was not written by us.
    /// </summary>
    /// <remarks>
    /// The indexer caps a graph at a few hundred nodes and plans are smaller still; a
    /// file claiming tens of thousands has been hand-edited, concatenated or pointed at
    /// the wrong reader, and honouring it would allocate for minutes before anybody saw a
    /// canvas.
    /// </remarks>
    private const int MaxNodes = 20_000;

    /// <summary>
    /// The serialisation options, matching the application's settings files: camelCase
    /// properties, no indentation, enums as their names.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IAppPaths _paths;
    private readonly ILogger<JsonGraphStore> _logger;

    public JsonGraphStore(IAppPaths paths, ILogger<JsonGraphStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);

        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// Writes the snapshot under the given key, atomically: a temporary file first, then
    /// a move over the target.
    /// </summary>
    /// <remarks>
    /// The directory is created here rather than in the constructor so that merely
    /// registering the store never touches the disk - a store that is never used leaves
    /// nothing behind.
    /// </remarks>
    public async Task SaveAsync(string key, GraphSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(snapshot);

        var directory = Path.Combine(_paths.DataDirectory, DirectoryName);
        var target = Path.Combine(directory, FileNameFor(key));

        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(ToDto(snapshot), JsonOptions);
        var temporary = target + ".tmp";

        await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, target, overwrite: true);
    }

    /// <summary>
    /// Reads the snapshot stored under the given key, or null when nothing is stored or
    /// what is stored cannot be read.
    /// </summary>
    /// <remarks>
    /// Read failures are logged at warning level with the file named, then answered as
    /// absent: the caller (the graph service) loads an empty graph, and the workspace
    /// indexer rebuilds what it can.
    /// </remarks>
    public async Task<GraphSnapshot?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var target = Path.Combine(Path.Combine(_paths.DataDirectory, DirectoryName), FileNameFor(key));

        if (!File.Exists(target))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(target, cancellationToken).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<SnapshotDto>(json, JsonOptions);

            return dto is null ? null : FromDto(dto);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Graph file '{File}' could not be read and will be treated as absent.", target);
            return null;
        }
    }

    /// <summary>
    /// The file name for a key: the key itself when it makes a safe name, otherwise a
    /// short hash of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Callers pass workspace roots, which contain path separators and drive letters -
    /// characters a file name cannot hold. A key of only <c>[a-z0-9._-]</c> (compared
    /// lowercased and invariant) becomes its own file name, which keeps the directory
    /// readable for the common case; anything else, or anything too long, becomes the
    /// first 16 hex characters of its SHA-256, prefixed with <c>~</c>.
    /// </para>
    /// <para>
    /// The prefix is a character the safe set does not contain, so a hashed name can
    /// never collide with a key that became its own name - two workspaces sharing a file
    /// is a bug that would show up as one canvas overwriting another, long before anybody
    /// thought to look here.
    /// </para>
    /// </remarks>
    private static string FileNameFor(string key)
    {
        var lowered = key.Trim().ToLowerInvariant();

        if (lowered.Length > 0 && lowered.Length <= MaxKeyLength && lowered.All(IsSafeNameCharacter))
        {
            return lowered + ".json";
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
        return "~" + hash + ".json";
    }

    /// <summary>Whether a character survives as part of a file name everywhere this app runs.</summary>
    private static bool IsSafeNameCharacter(char c) =>
        (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c is '.' or '_' or '-';

    /// <summary>The snapshot as it is written: plain lists of plain data, nothing derived.</summary>
    private static SnapshotDto ToDto(GraphSnapshot snapshot) => new()
    {
        Version = snapshot.Version,
        Nodes = [.. snapshot.Nodes.Select(ToDto)],
        Edges = [.. snapshot.Edges.Select(ToDto)],
    };

    private static NodeDto ToDto(GraphNode node) => new()
    {
        Id = node.Id,
        Kind = node.Kind,
        Title = node.Title,
        Subtitle = node.Subtitle,
        Detail = node.Detail,
        Path = node.Path,
        X = node.X,
        Y = node.Y,
        Width = node.Width,
        Height = node.Height,
        Metric = node.Metric,
    };

    private static EdgeDto ToDto(GraphEdge edge) => new()
    {
        Id = edge.Id,
        SourceId = edge.SourceId,
        TargetId = edge.TargetId,
        Kind = edge.Kind,
        Label = edge.Label,
    };

    /// <summary>
    /// Rebuilds a snapshot from what was read, defensively: entries without an id are
    /// skipped, and a file holding more nodes than this application writes is refused
    /// outright.
    /// </summary>
    /// <returns>The snapshot, or null when the file was implausibly large and should be treated as not ours.</returns>
    private GraphSnapshot? FromDto(SnapshotDto dto)
    {
        if (dto.Nodes.Count > MaxNodes)
        {
            _logger.LogWarning(
                "Graph file holds {Count} nodes, more than the {Max} this application writes; treating it as absent.",
                dto.Nodes.Count,
                MaxNodes);

            return null;
        }

        var nodes = new List<GraphNode>(dto.Nodes.Count);

        foreach (var candidate in dto.Nodes)
        {
            if (string.IsNullOrWhiteSpace(candidate.Id))
            {
                continue;
            }

            nodes.Add(new GraphNode
            {
                Id = candidate.Id,
                Kind = candidate.Kind,
                // A null title in a hand-edited file cannot be repaired into a good one;
                // an empty title at least keeps the node loadable and visible.
                Title = candidate.Title ?? string.Empty,
                Subtitle = candidate.Subtitle,
                Detail = candidate.Detail,
                Path = candidate.Path,
                X = candidate.X,
                Y = candidate.Y,
                Width = candidate.Width,
                Height = candidate.Height,
                Metric = candidate.Metric,
            });
        }

        var edges = new List<GraphEdge>(dto.Edges.Count);

        foreach (var candidate in dto.Edges)
        {
            if (string.IsNullOrWhiteSpace(candidate.Id)
                || string.IsNullOrWhiteSpace(candidate.SourceId)
                || string.IsNullOrWhiteSpace(candidate.TargetId))
            {
                continue;
            }

            edges.Add(new GraphEdge
            {
                Id = candidate.Id,
                SourceId = candidate.SourceId,
                TargetId = candidate.TargetId,
                Kind = candidate.Kind,
                Label = candidate.Label,
            });
        }

        return new GraphSnapshot
        {
            Version = dto.Version,
            Nodes = nodes,
            Edges = edges,
        };
    }

    /// <summary>What a snapshot looks like on disk. Mutable and stringly on purpose: the shape is the contract, not the type.</summary>
    private sealed record SnapshotDto
    {
        public int Version { get; set; }

        public List<NodeDto> Nodes { get; set; } = [];

        public List<EdgeDto> Edges { get; set; } = [];
    }

    private sealed record NodeDto
    {
        public string? Id { get; set; }

        public GraphNodeKind Kind { get; set; }

        public string? Title { get; set; }

        public string? Subtitle { get; set; }

        public string? Detail { get; set; }

        public string? Path { get; set; }

        public double X { get; set; }

        public double Y { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }

        public int? Metric { get; set; }
    }

    private sealed record EdgeDto
    {
        public string? Id { get; set; }

        public string? SourceId { get; set; }

        public string? TargetId { get; set; }

        public GraphEdgeKind Kind { get; set; }

        public string? Label { get; set; }
    }
}
