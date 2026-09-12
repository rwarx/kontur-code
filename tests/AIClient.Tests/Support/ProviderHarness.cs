using AIClient.Domain.Interfaces;
using AIClient.Infrastructure.Providers;
using AIClient.Infrastructure.Providers.Anthropic;
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

    public static OpenAiProvider OpenAI(
        FakeHttpMessageHandler handler,
        ISecureStorage? secureStorage = null) =>
        new(new StubHttpClientFactory(handler),
            secureStorage ?? FakeSecureStorage.With(OpenAiProvider.ProviderId, DummyKey),
            NullLogger<OpenAiProvider>.Instance);

    public static AnthropicProvider Anthropic(
        FakeHttpMessageHandler handler,
        ISecureStorage? secureStorage = null) =>
        new(new StubHttpClientFactory(handler),
            secureStorage ?? FakeSecureStorage.With(AnthropicProvider.ProviderId, DummyKey),
            NullLogger<AnthropicProvider>.Instance);

    public static GroqProvider Groq(
        FakeHttpMessageHandler handler,
        ISecureStorage? secureStorage = null) =>
        new(new StubHttpClientFactory(handler),
            secureStorage ?? FakeSecureStorage.With(GroqProvider.ProviderId, DummyKey),
            NullLogger<GroqProvider>.Instance);

    public static XaiProvider Xai(
        FakeHttpMessageHandler handler,
        ISecureStorage? secureStorage = null) =>
        new(new StubHttpClientFactory(handler),
            secureStorage ?? FakeSecureStorage.With(XaiProvider.ProviderId, DummyKey),
            NullLogger<XaiProvider>.Instance);

    public static MistralProvider Mistral(
        FakeHttpMessageHandler handler,
        ISecureStorage? secureStorage = null) =>
        new(new StubHttpClientFactory(handler),
            secureStorage ?? FakeSecureStorage.With(MistralProvider.ProviderId, DummyKey),
            NullLogger<MistralProvider>.Instance);

    public static DeepSeekProvider DeepSeek(
        FakeHttpMessageHandler handler,
        ISecureStorage? secureStorage = null) =>
        new(new StubHttpClientFactory(handler),
            secureStorage ?? FakeSecureStorage.With(DeepSeekProvider.ProviderId, DummyKey),
            NullLogger<DeepSeekProvider>.Instance);

    /// <summary>A user-defined provider, built the way CustomProviderStore builds one.</summary>
    public static CustomOpenAiCompatibleProvider Custom(
        string id,
        string name,
        string baseUrl,
        FakeHttpMessageHandler handler,
        ISecureStorage? secureStorage = null) =>
        new(id,
            name,
            baseUrl,
            new StubHttpClientFactory(handler),
            secureStorage ?? FakeSecureStorage.With(id, DummyKey),
            NullLogger<CustomOpenAiCompatibleProvider>.Instance);

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
