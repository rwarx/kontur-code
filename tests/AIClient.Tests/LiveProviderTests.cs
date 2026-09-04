using AIClient.Domain.Enums;
using AIClient.Domain.Interfaces;
using AIClient.Domain.Models;
using AIClient.Infrastructure.Providers;
using AIClient.Infrastructure.Providers.OpenAiCompatible;
using AIClient.Tests.Support;
using Microsoft.Extensions.Options;

namespace AIClient.Tests;

/// <summary>
/// The only tests in the suite that reach the real providers over the network. Every one of them
/// skips itself unless a key is present in the environment.
/// </summary>
/// <remarks>
/// <para>
/// Section 36 forbids a committed API key, and no committed key means no unconditional live test:
/// the suite has to be green on a fresh clone with no credentials and no network. These read the
/// key from an environment variable and report themselves as skipped when it is absent, which
/// keeps the wire format verifiable by hand without weakening that rule. The variables are read,
/// never written, and no test here stores a key through <c>ISecureStorage</c>.
/// </para>
/// <para>
/// Run them with a key in the environment and the trait filter dropped:
/// <code>
/// $env:AICLIENT_TEST_OPENROUTER_KEY = "sk-or-v1-..."
/// dotnet test --filter "FullyQualifiedName~LiveProviderTests"
/// </code>
/// </para>
/// <para>
/// The clients here are plain <see cref="HttpClient"/> instances rather than the named,
/// policy-wrapped ones the application registers, so a failure points at the provider rather than
/// at the retry configuration.
/// </para>
/// </remarks>
[Trait("Category", "Live")]
public sealed class LiveProviderTests : IDisposable
{
    private const string OpenRouterKeyVariable = "AICLIENT_TEST_OPENROUTER_KEY";
    private const string NvidiaKeyVariable = "AICLIENT_TEST_NVIDIA_KEY";

    /// <summary>A syntactically plausible key that cannot be valid. Used to exercise the 401 path.</summary>
    private const string RevokedKey = "sk-or-v1-" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly LiveHttpClientFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task OpenRouter_returns_a_usable_catalogue()
    {
        var provider = OpenRouter(RequireKey(OpenRouterKeyVariable), out _);

        var models = await provider.GetModelsAsync(Token);

        AssertUsableCatalogue(models);

        // The gateway fans out to many vendors, and the picker groups on the prefix.
        Assert.Contains(models, m => m.ModelId.Contains('/', StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenRouter_reports_a_working_connection()
    {
        var provider = OpenRouter(RequireKey(OpenRouterKeyVariable), out _);

        var result = await provider.TestConnectionAsync(Token);

        Assert.True(result.Success, result.Message);
        Assert.True(result.ModelCount > 0);
    }

    [Fact]
    public async Task OpenRouter_streams_a_short_answer()
    {
        var key = RequireKey(OpenRouterKeyVariable);
        var provider = OpenRouter(key, out var logger);

        var model = await PickCheapestAsync(provider);
        var events = await AskAsync(provider, model);

        AssertAnswered(events, logger.Text, key);
    }

    [Fact]
    public async Task OpenRouter_reports_a_revoked_key_without_repeating_it()
    {
        // Needs the network but not a valid key. Gated on the same variable, since that is what
        // says this machine is allowed to reach OpenRouter at all.
        RequireKey(OpenRouterKeyVariable);
        var provider = OpenRouter(RevokedKey, out var logger);

        var events = await AskAsync(provider, "openai/gpt-4o-mini");

        AssertRejected(events, logger.Text, RevokedKey);
    }

    [Fact]
    public async Task Nvidia_returns_a_usable_catalogue()
    {
        var provider = Nvidia(RequireKey(NvidiaKeyVariable), out _);

        var models = await provider.GetModelsAsync(Token);

        AssertUsableCatalogue(models);
    }

    [Fact]
    public async Task Nvidia_reports_a_working_connection()
    {
        var provider = Nvidia(RequireKey(NvidiaKeyVariable), out _);

        var result = await provider.TestConnectionAsync(Token);

        Assert.True(result.Success, result.Message);
        Assert.True(result.ModelCount > 0);
    }

    [Fact]
    public async Task Nvidia_streams_a_short_answer()
    {
        var key = RequireKey(NvidiaKeyVariable);
        var provider = Nvidia(key, out var logger);

        var model = await PickCheapestAsync(provider);
        var events = await AskAsync(provider, model);

        AssertAnswered(events, logger.Text, key);
    }

    [Fact]
    public async Task Nvidia_reports_a_revoked_key_without_repeating_it()
    {
        RequireKey(NvidiaKeyVariable);
        var provider = Nvidia(RevokedKey, out var logger);

        var events = await AskAsync(provider, "meta/llama-3.1-8b-instruct");

        AssertRejected(events, logger.Text, RevokedKey);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Reads the key, or skips the test. Returning the value rather than a bool keeps the guard on
    /// one line at the top of each test, where it reads as a precondition.
    /// </summary>
    private static string RequireKey(string variable)
    {
        var key = Environment.GetEnvironmentVariable(variable);

        Assert.SkipUnless(
            !string.IsNullOrWhiteSpace(key),
            $"Set {variable} to run the live provider tests. They are skipped by default because "
                + "section 36 forbids a committed API key.");

        return key!;
    }

    private OpenRouterProvider OpenRouter(string apiKey, out RecordingLogger<OpenRouterProvider> logger)
    {
        logger = new RecordingLogger<OpenRouterProvider>();

        return new OpenRouterProvider(
            _factory,
            FakeSecureStorage.With(OpenRouterProvider.ProviderId, apiKey),
            logger);
    }

    private NvidiaProvider Nvidia(string apiKey, out RecordingLogger<NvidiaProvider> logger)
    {
        logger = new RecordingLogger<NvidiaProvider>();

        return new NvidiaProvider(
            _factory,
            FakeSecureStorage.With(NvidiaProvider.ProviderId, apiKey),
            Options.Create(new ProviderEndpointOptions()),
            logger);
    }

    /// <summary>
    /// Asks for one word, cheaply. Temperature 0 and a small cap keep the answer short and the
    /// bill negligible, and a system message is included so the request exercises the same shape
    /// the app sends.
    /// </summary>
    private static async Task<List<AIStreamEvent>> AskAsync(IAIProvider provider, string modelId)
    {
        var request = new AIChatRequest
        {
            ModelId = modelId,
            Messages =
            [
                new AIChatMessage("system", "You are terse. Answer with a single word."),
                new AIChatMessage("user", "Reply with the single word: pong."),
            ],
            Temperature = 0,
            MaxTokens = 64,
            Stream = true,
        };

        return await ProviderHarness.CollectAsync(provider.StreamChatAsync(request, Token), Token);
    }

    /// <summary>
    /// The cheapest streaming model in the live catalogue, so the choice survives a model being
    /// retired upstream. A free model wins outright.
    /// </summary>
    private static async Task<string> PickCheapestAsync(IAIProvider provider)
    {
        var models = await provider.GetModelsAsync(Token);

        var model = models
            .Where(m => m.SupportsStreaming)
            .OrderBy(m => m.PromptPricePerMillion ?? decimal.MaxValue)
            .ThenBy(m => m.ModelId, StringComparer.Ordinal)
            .FirstOrDefault();

        Assert.SkipWhen(model is null, $"{provider.DisplayName} offers no streaming model to these credentials.");

        return model!.ModelId;
    }

    private static void AssertUsableCatalogue(IReadOnlyList<AIModelDescriptor> models)
    {
        Assert.NotEmpty(models);
        Assert.All(models, model =>
        {
            Assert.False(string.IsNullOrWhiteSpace(model.ModelId));
            Assert.False(string.IsNullOrWhiteSpace(model.Name));
        });

        // A duplicate id would make the picker ambiguous and collide on the model table's key.
        Assert.Distinct(models.Select(m => m.ModelId).ToList(), StringComparer.Ordinal);

        // Not every entry advertises a context window, but a catalogue where none does means the
        // parser is looking at the wrong field.
        Assert.Contains(models, m => m.ContextWindow > 0);
    }

    private static void AssertAnswered(IReadOnlyList<AIStreamEvent> events, string logText, string apiKey)
    {
        var failure = events.OfType<AIStreamEvent.Error>().FirstOrDefault();
        Assert.True(failure is null, $"The provider reported: {failure?.Kind} - {failure?.Message}");

        Assert.NotEmpty(events);
        Assert.IsType<AIStreamEvent.Completed>(events[^1]);
        Assert.NotEmpty(ProviderHarness.TextOf(events));

        AssertKeptQuiet(logText, apiKey);
    }

    private static void AssertRejected(IReadOnlyList<AIStreamEvent> events, string logText, string apiKey)
    {
        // A 401 arrives as an event rather than an exception, because an error can also turn up
        // mid-stream after usable text. Either way the turn ends, and the UI needs the kind to
        // decide that Retry is pointless.
        var failure = Assert.IsType<AIStreamEvent.Error>(Assert.Single(events));

        Assert.Equal(AIErrorKind.InvalidApiKey, failure.Kind);
        Assert.False(string.IsNullOrWhiteSpace(failure.Message));

        AssertKeptQuiet(logText, apiKey);
        AssertKeptQuiet(failure.Message, apiKey);
        AssertKeptQuiet(failure.TechnicalDetails ?? string.Empty, apiKey);
    }

    /// <summary>
    /// Section 26 against a real request: the key went into an Authorization header on the wire, so
    /// this is the one assertion that cannot be satisfied by a fake.
    /// </summary>
    private static void AssertKeptQuiet(string text, string apiKey)
    {
        Assert.DoesNotContain(apiKey, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer ", text, StringComparison.Ordinal);

        // The tail alone is enough to finish a partially leaked key.
        Assert.DoesNotContain(apiKey[^12..], text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The only factory in the suite that opens a real socket.
    /// </summary>
    /// <remarks>
    /// The timeout is generous because a cold model on a shared endpoint can take a while to
    /// produce its first token, and a timeout here would look like a provider bug.
    /// </remarks>
    private sealed class LiveHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly List<HttpClient> _clients = [];

        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            _clients.Add(client);

            return client;
        }

        public void Dispose()
        {
            foreach (var client in _clients)
            {
                client.Dispose();
            }

            _clients.Clear();
        }
    }
}
