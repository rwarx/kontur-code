using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;

namespace AIClient.Tests.Support;

/// <summary>
/// An <see cref="ICompactionService"/> that folds nothing, for the tests that are about something
/// else.
/// </summary>
/// <remarks>
/// <para>
/// Both orchestrators ask this at the head of a turn, and the real implementation would need a
/// provider scripted to answer a summarisation request that the test never arranged. Refusing by
/// default keeps those tests measuring what they were written to measure: a chat turn with the
/// history it was given, not a chat turn with a summary in front of it.
/// </para>
/// <para>
/// <see cref="Compact"/> makes it agree to one pass, so a test can assert that the orchestrator
/// reports what came back without needing a model at all.
/// </para>
/// </remarks>
public sealed class StubCompactionService : ICompactionService
{
    private CompactionResult? _result;

    /// <summary>How many times the orchestrator asked whether a fold was needed.</summary>
    public int ShouldCompactCalls { get; private set; }

    /// <summary>How many times it went ahead and asked for one.</summary>
    public int CompactCalls { get; private set; }

    /// <summary>The request the last fold was asked for, so a test can check what was passed.</summary>
    public CompactionRequest? LastRequest { get; private set; }

    /// <summary>Arranges one successful fold, reported the way the real service would report it.</summary>
    public StubCompactionService Compact(int messagesFolded = 4, int tokensSaved = 1000)
    {
        _result = new CompactionResult { MessagesFolded = messagesFolded, TokensSaved = tokensSaved };
        return this;
    }

    public Task<bool> ShouldCompactAsync(
        Guid conversationId,
        string? providerId,
        string? modelId,
        CancellationToken cancellationToken = default)
    {
        ShouldCompactCalls++;
        return Task.FromResult(_result is not null);
    }

    public Task<CompactionResult> CompactAsync(
        CompactionRequest request,
        CancellationToken cancellationToken = default)
    {
        CompactCalls++;
        LastRequest = request;

        // Once only. A stub that agreed forever would compact at the head of every agent step and
        // turn a five-step run into five identical events.
        var result = _result ?? CompactionResult.Skipped("nothing to fold");
        _result = null;

        return Task.FromResult(result);
    }
}
