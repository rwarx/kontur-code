using AIClient.Domain.Interfaces;
using AIClient.Domain.Models;

namespace AIClient.Tests.Support;

/// <summary>
/// An <see cref="IAIProvider"/> that replays a scripted event sequence and records the
/// request it was handed.
/// </summary>
/// <remarks>
/// Used where the subject under test is the orchestration rather than the wire format:
/// <c>ChatService</c> ordering, persistence, cancellation and the section 14 rule that a
/// parameter the model does not support is never sent. The recorded
/// <see cref="AIChatRequest"/> is how that last one is asserted, since the request object is
/// what <c>ChatService</c> is responsible for and the JSON is the provider's business.
/// </remarks>
public sealed class ScriptedProvider : IAIProvider
{
    private readonly Func<AIChatRequest, CancellationToken, IAsyncEnumerable<AIStreamEvent>> _script;

    public ScriptedProvider(
        string id = "test",
        Func<AIChatRequest, CancellationToken, IAsyncEnumerable<AIStreamEvent>>? script = null)
    {
        Id = id;
        _script = script ?? ((_, ct) => Replay([
            new AIStreamEvent.ContentDelta("Hello"),
            new AIStreamEvent.ContentDelta(", world"),
            new AIStreamEvent.Completed("stop"),
        ], ct));
    }

    /// <summary>A provider that streams the given text one delta per element, then completes.</summary>
    public static ScriptedProvider Streaming(string id, params string[] deltas) =>
        new(id, (_, ct) => Replay(
            [
                .. deltas.Select(d => (AIStreamEvent)new AIStreamEvent.ContentDelta(d)),
                new AIStreamEvent.Completed("stop"),
            ],
            ct));

    /// <summary>A provider that replays a fixed sequence verbatim, terminal event included.</summary>
    public static ScriptedProvider Emitting(string id, params AIStreamEvent[] events) =>
        new(id, (_, ct) => Replay(events, ct));

    /// <summary>A provider whose stream throws part-way through.</summary>
    public static ScriptedProvider Throwing(string id, Exception exception, params string[] deltasFirst) =>
        new(id, (_, ct) => ThrowAfter(deltasFirst, exception, ct));

    public string Id { get; }
    public string DisplayName => Id;

    /// <summary>Every request handed to <see cref="StreamChatAsync"/>, in order.</summary>
    public List<AIChatRequest> Requests { get; } = [];

    public AIChatRequest LastRequest =>
        Requests.Count > 0 ? Requests[^1] : throw new InvalidOperationException("The provider was never called.");

    public List<AIModelDescriptor> Catalogue { get; } = [];

    /// <summary>
    /// How many times the catalogue was fetched. The registry is supposed to serve the picker
    /// from SQLite, so a test that opens the picker expects this not to move.
    /// </summary>
    public int CatalogueFetches { get; private set; }

    /// <summary>
    /// When set, the catalogue fetch fails - an offline machine or a revoked key. Section 31
    /// requires the cached list to survive that.
    /// </summary>
    public Exception? CatalogueFault { get; set; }

    public Task<IReadOnlyList<AIModelDescriptor>> GetModelsAsync(CancellationToken cancellationToken)
    {
        CatalogueFetches++;

        return CatalogueFault is not null
            ? Task.FromException<IReadOnlyList<AIModelDescriptor>>(CatalogueFault)
            : Task.FromResult<IReadOnlyList<AIModelDescriptor>>(Catalogue);
    }

    public IAsyncEnumerable<AIStreamEvent> StreamChatAsync(
        AIChatRequest request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return _script(request, cancellationToken);
    }

    public Task<ProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderTestResult(true, "OK", Catalogue.Count));

    private static async IAsyncEnumerable<AIStreamEvent> Replay(
        IEnumerable<AIStreamEvent> events,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var evt in events)
        {
            // Observing the token between events is what a real provider does at every
            // network read, and is what makes a cancellation test deterministic.
            cancellationToken.ThrowIfCancellationRequested();

            // Yielding the thread keeps the sequence genuinely asynchronous; a synchronous
            // one would hide ordering bugs that only appear once a continuation is posted.
            await Task.Yield();
            yield return evt;
        }
    }

    private static async IAsyncEnumerable<AIStreamEvent> ThrowAfter(
        IEnumerable<string> deltas,
        Exception exception,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var delta in deltas)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new AIStreamEvent.ContentDelta(delta);
        }

        await Task.Yield();
        throw exception;
    }
}
