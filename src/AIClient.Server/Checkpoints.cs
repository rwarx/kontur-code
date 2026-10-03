using System.Text.Json;

namespace AIClient.Server;

/// <summary>
/// File-snapshot checkpoints, persisted as one JSON document per checkpoint.
/// </summary>
/// <remarks>
/// The graph half of a checkpoint is stored through <c>IGraphService</c> under a
/// checkpoint key; this store holds the metadata and the file contents.
/// Nothing here invents data: contents are captured from the workspace at creation
/// and written back verbatim on restore.
/// </remarks>
public sealed class JsonCheckpointStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly string _directory;

    public JsonCheckpointStore(IHostEnvironment env)
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AIClient",
            "checkpoints");
        Directory.CreateDirectory(_directory);
    }

    public Task<IReadOnlyList<CheckpointRecord>> ListAsync(CancellationToken ct = default)
    {
        var list = new List<CheckpointRecord>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var record = JsonSerializer.Deserialize<CheckpointRecord>(
                    File.ReadAllText(file), Json);
                if (record is not null)
                {
                    list.Add(record);
                }
            }
            catch
            {
                // A corrupt checkpoint is skipped, not fatal.
            }
        }

        list.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return Task.FromResult<IReadOnlyList<CheckpointRecord>>(list);
    }

    public async Task<CheckpointRecord?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var path = PathFor(id);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<CheckpointRecord>(stream, Json, ct).ConfigureAwait(false);
    }

    public async Task SaveAsync(CheckpointRecord record, CancellationToken ct = default)
    {
        var path = PathFor(record.Id);
        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, record, Json, ct).ConfigureAwait(false);
        }

        File.Move(tmp, path, overwrite: true);
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var path = PathFor(id);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string PathFor(Guid id) => Path.Combine(_directory, id + ".json");
}

/// <summary>One persisted checkpoint.</summary>
public sealed record CheckpointRecord
{
    public required Guid Id { get; init; }
    public required Guid ConversationId { get; init; }
    public required string Label { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public Guid? MessageId { get; init; }

    /// <summary>Workspace-relative path to captured content.</summary>
    public Dictionary<string, string> Files { get; init; } = new();

    /// <summary>Graph key holding the canvas snapshot, when the checkpoint drew one.</summary>
    public string? GraphKey { get; init; }
}
