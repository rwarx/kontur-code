using AIClient.Domain.Interfaces;
using AIClient.Infrastructure.Providers;
using AIClient.Infrastructure.Providers.OpenAiCompatible;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AIClient.Tests.Support;

/// <summary>
/// Builds the real provider implementations over a scripted HTTP handler.
/// </summary>
/// <remarks>
/// These are the production classes, not stand-ins: the only thing replaced is the socket.
/// That is what lets the suite assert on URL construction, headers, the request body, SSE
/// framing and error classification while satisfying section 36's requirement that provider
/// tests never need a committed API key.
/// </remarks>
public static class ProviderHarness
{
    /// <summary>A placeholder credential. Not a key, and never sent anywhere real.</summary>
    public const string DummyKey = "sk-test-placeholder-not-a-real-key";

    public static OpenRouterProvider OpenRouter(
        FakeHttpMessageHandler handler,
        ISecureStorage? secureStorage = null) =>
        new(new StubHttpClientFactory(handler),
            secureStorage ?? FakeSecureStorage.With(OpenRouterProvider.ProviderId, DummyKey),
            NullLogger<OpenRouterProvider>.Instance);

    public static NvidiaProvider Nvidia(
        FakeHttpMessageHandler handler,
        ISecureStorage? secureStorage = null,
        string? baseUrl = null) =>
        new(new StubHttpClientFactory(handler),
            secureStorage ?? FakeSecureStorage.With(NvidiaProvider.ProviderId, DummyKey),
            Options.Create(baseUrl is null
                ? new ProviderEndpointOptions()
                : new ProviderEndpointOptions { Nvidia = baseUrl }),
            NullLogger<NvidiaProvider>.Instance);

    /// <summary>Drains a provider stream into a list, which is what most assertions want.</summary>
    public static async Task<List<Domain.Models.AIStreamEvent>> CollectAsync(
        IAsyncEnumerable<Domain.Models.AIStreamEvent> stream,
        CancellationToken cancellationToken = default)
    {
        var events = new List<Domain.Models.AIStreamEvent>();

        await foreach (var evt in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            events.Add(evt);
        }

        return events;
    }

    /// <summary>Concatenates every content delta, i.e. the answer the user would have seen.</summary>
    public static string TextOf(IEnumerable<Domain.Models.AIStreamEvent> events) =>
        string.Concat(events.OfType<Domain.Models.AIStreamEvent.ContentDelta>().Select(e => e.Text));
}
