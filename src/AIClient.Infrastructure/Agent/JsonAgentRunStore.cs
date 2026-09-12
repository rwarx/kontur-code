using System.Text.Json;
using AIClient.Application.Configuration;
using AIClient.Application.Interfaces;
using AIClient.Domain.Models;
using Microsoft.Extensions.Logging;

namespace AIClient.Infrastructure.Agent;

/// <summary>
/// Persists agent run checkpoints as JSON files. Each checkpoint is one file,
/// atomically written to avoid corruption on crash.
/// </summary>
public sealed class JsonAgentRunStore : IAgentRunStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    private readonly string _directory;
    private readonly ILogger<JsonAgentRunStore> _logger;

    public JsonAgentRunStore(IAppPaths appPaths, ILogger<JsonAgentRunStore> logger)
    {
        _directory = Path.Combine(appPaths.DataDirectory, "agent-runs");
        _logger = logger;

        Directory.CreateDirectory(_directory);
    }

    public async Task SaveCheckpointAsync(AgentRunCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        var path = GetPath(checkpoint.RunId);
        var tempPath = path + ".tmp";

        try
        {
            var json = JsonSerializer.Serialize(checkpoint, SerializerOptions);
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);

            // Atomic move: overwrite the existing file or create a new one.
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not save checkpoint for run {RunId}.", checkpoint.RunId);

            try { File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    public async Task<AgentRunCheckpoint?> LoadCheckpointAsync(Guid runId, CancellationToken cancellationToken)
    {
        var path = GetPath(runId);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<AgentRunCheckpoint>(json, SerializerOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not load checkpoint for run {RunId}.", runId);
            return null;
        }
    }

    public async Task<IReadOnlyList<AgentRunCheckpoint>> GetResumableAsync(CancellationToken cancellationToken)
    {
        var checkpoints = new List<AgentRunCheckpoint>();

        if (!Directory.Exists(_directory))
        {
            return checkpoints;
        }

        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                var checkpoint = JsonSerializer.Deserialize<AgentRunCheckpoint>(json, SerializerOptions);

                if (checkpoint is { CanResume: true })
                {
                    checkpoints.Add(checkpoint);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not read checkpoint file {File}.", file);
            }
        }

        return checkpoints.OrderByDescending(c => c.CreatedAt).ToList();
    }

    public Task DeleteCheckpointAsync(Guid runId, CancellationToken cancellationToken)
    {
        var path = GetPath(runId);

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not delete checkpoint for run {RunId}.", runId);
        }

        return Task.CompletedTask;
    }

    private string GetPath(Guid runId) => Path.Combine(_directory, $"{runId:N}.json");
}
