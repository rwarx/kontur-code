using System.Net;
using System.Text.Json;
using AIClient.Domain.Enums;
using AIClient.Domain.Interfaces;
using AIClient.Domain.Models;
using AIClient.Infrastructure.Providers.OpenAiCompatible;
using AIClient.Tests.Support;

namespace AIClient.Tests;

/// <summary>
/// Sections 8 and 9: model discovery through the real provider classes.
/// </summary>
/// <remarks>
/// These drive <see cref="OpenRouterProvider"/> and <see cref="NvidiaProvider"/> over a
/// scripted socket, so URL construction, headers, catalogue parsing and error classification
/// are all under test without a key anywhere near the suite - which is what section 36 asks
/// for. The fixtures are trimmed real responses, including the entries that omit optional
/// objects, because those are what break a parser.
/// </remarks>
public sealed class ProviderCatalogueTests
{
    [Fact]
    public async Task Every_usable_OpenRouter_entry_becomes_a_model_and_the_broken_one_is_skipped()
    {
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.OpenRouterCatalogue);

        var models = await ModelsOf(ProviderHarness.OpenRouter(handler));

        // Five entries in, four out: the one with an empty id could never be requested.
        Assert.Equal(4, models.Count);
        Assert.DoesNotContain(string.Empty, models.Select(m => m.ModelId));
    }

    [Fact]
    public async Task The_catalogue_is_sorted_by_display_name()
    {
        // It arrives in no useful order, and the picker shows it as given.
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.OpenRouterCatalogue);

        var models = await ModelsOf(ProviderHarness.OpenRouter(handler));

        Assert.Equal(
            ["Anthropic: Claude Sonnet 4.5", "DeepSeek: R1 (free)", "OpenAI: GPT-5 Mini", "Z.AI: GLM 4.6"],
            models.Select(m => m.Name));
    }

    [Fact]
    public async Task A_fully_described_model_carries_every_field_the_picker_shows()
    {
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.OpenRouterCatalogue);

        var model = (await ModelsOf(ProviderHarness.OpenRouter(handler)))
            .Single(m => m.ModelId == "openai/gpt-5-mini");

        Assert.Equal("OpenAI: GPT-5 Mini", model.Name);
        Assert.Equal("A small, fast general-purpose model.", model.Description);
        Assert.Equal(400_000, model.ContextWindow);
        Assert.Equal(128_000, model.MaxOutputTokens);
        Assert.True(model.SupportsStreaming);
        Assert.True(model.SupportsImages);
        Assert.True(model.SupportsTools);

        // Per-token USD on the wire, per-million in the app: 0.00000025 * 1e6.
        Assert.Equal(0.25m, model.PromptPricePerMillion);
        Assert.Equal(2m, model.CompletionPricePerMillion);
    }

    [Fact]
    public async Task The_legacy_modality_string_is_read_as_well_as_the_modern_array()
    {
        // Half the catalogue still reports "text+image->text" instead of input_modalities.
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.OpenRouterCatalogue);

        var model = (await ModelsOf(ProviderHarness.OpenRouter(handler)))
            .Single(m => m.ModelId == "anthropic/claude-sonnet-4.5");

        Assert.True(model.SupportsImages);
        Assert.False(model.SupportsTools, "tools is absent from this entry's supported_parameters.");
    }

    [Fact]
    public async Task A_context_window_hidden_under_top_provider_and_encoded_as_a_string_is_still_read()
    {
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.OpenRouterCatalogue);

        var model = (await ModelsOf(ProviderHarness.OpenRouter(handler)))
            .Single(m => m.ModelId == "deepseek/deepseek-r1:free");

        Assert.Equal(163_840, model.ContextWindow);
    }

    [Fact]
    public async Task A_free_model_is_priced_at_zero_rather_than_left_unknown()
    {
        // "Free" is worth showing in the picker. Null would render as "pricing unavailable".
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.OpenRouterCatalogue);

        var model = (await ModelsOf(ProviderHarness.OpenRouter(handler)))
            .Single(m => m.ModelId == "deepseek/deepseek-r1:free");

        Assert.Equal(0m, model.PromptPricePerMillion);
        Assert.Equal(0m, model.CompletionPricePerMillion);
    }

    [Fact]
    public async Task An_entry_missing_every_optional_object_does_not_take_the_catalogue_down_with_it()
    {
        // A regression test for a real bug: the readers were handed default(JsonElement) for
        // an absent nested object, and TryGetProperty on an undefined element throws rather
        // than returning false. One sparse entry failed the entire model fetch, so the user
        // saw "OpenRouter returned a model list that could not be read" and an empty picker.
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.OpenRouterCatalogue);

        var model = (await ModelsOf(ProviderHarness.OpenRouter(handler)))
            .Single(m => m.ModelId == "z-ai/glm-4.6");

        Assert.Equal("Z.AI: GLM 4.6", model.Name);
        Assert.Null(model.ContextWindow);
        Assert.Null(model.MaxOutputTokens);
        Assert.Null(model.PromptPricePerMillion);
        Assert.Null(model.CompletionPricePerMillion);
        Assert.False(model.SupportsImages);
        Assert.False(model.SupportsTools);
        Assert.Empty(model.SupportedParameters);
    }

    [Fact]
    public async Task The_original_catalogue_entry_is_kept_verbatim()
    {
        // Diagnostics for a model the app renders oddly: the raw entry answers "what did the
        // provider actually say", which a projected descriptor cannot.
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.OpenRouterCatalogue);

        var model = (await ModelsOf(ProviderHarness.OpenRouter(handler)))
            .Single(m => m.ModelId == "openai/gpt-5-mini");

        using var raw = JsonDocument.Parse(model.RawMetadataJson!);

        Assert.Equal("openai/gpt-5-mini", raw.RootElement.GetProperty("id").GetString());
        Assert.Equal("GPT", raw.RootElement.GetProperty("architecture").GetProperty("tokenizer").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":null}""")]
    [InlineData("""{"models":[{"id":"a/b"}]}""")]
    public async Task A_response_without_a_data_array_yields_no_models_instead_of_throwing(string body)
    {
        // An unexpected but well-formed shape is a provider change, not a crash: the picker
        // shows "no models" and Settings still works.
        var handler = new FakeHttpMessageHandler().RespondJson(body);

        Assert.Empty(await ModelsOf(ProviderHarness.OpenRouter(handler)));
    }

    [Fact]
    public async Task A_catalogue_that_is_not_json_is_reported_as_unreadable()
    {
        var handler = new FakeHttpMessageHandler().RespondJson("{ this is not json");

        var error = await Assert.ThrowsAsync<AIProviderException>(
            () => ModelsOf(ProviderHarness.OpenRouter(handler)));

        Assert.Equal(AIErrorKind.Unknown, error.Kind);
        Assert.Contains("OpenRouter", error.UserMessage, StringComparison.Ordinal);

        // Section 21: the parser's own complaint belongs behind "Technical details" rather
        // than in the sentence the user reads.
        Assert.NotNull(error.TechnicalDetails);
        Assert.NotEqual(error.UserMessage, error.TechnicalDetails);
        Assert.Contains("Json", error.TechnicalDetails, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_catalogue_is_fetched_from_the_documented_endpoint_with_a_bearer_token()
    {
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.OpenRouterCatalogue);

        await ModelsOf(ProviderHarness.OpenRouter(handler));

        var request = handler.LastRequest;
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://openrouter.ai/api/v1/models", request.Uri.ToString());
        Assert.Equal($"Bearer {ProviderHarness.DummyKey}", request.Header("Authorization"));

        // Not negotiable: an http:// base URL would put the key on the wire in clear text.
        Assert.Equal(Uri.UriSchemeHttps, request.Uri.Scheme);
    }

    [Fact]
    public async Task OpenRouter_attribution_headers_are_sent_and_carry_nothing_of_the_user()
    {
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.OpenRouterCatalogue);

        await ModelsOf(ProviderHarness.OpenRouter(handler));

        Assert.Equal("AI Client", handler.LastRequest.Header("X-Title"));
        Assert.NotNull(handler.LastRequest.Header("HTTP-Referer"));
    }

    [Fact]
    public async Task Without_a_stored_key_nothing_is_sent_and_the_user_is_pointed_at_Settings()
    {
        // Firing an unauthenticated request would burn a round trip and return a 401 that
        // reads like a bad key rather than a missing one.
        var handler = new FakeHttpMessageHandler();

        var error = await Assert.ThrowsAsync<AIProviderException>(
            () => ModelsOf(ProviderHarness.OpenRouter(handler, new FakeSecureStorage())));

        Assert.Equal(AIErrorKind.NotConfigured, error.Kind);
        Assert.Contains("Settings", error.UserMessage, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_rejected_key_is_classified_and_never_quoted_back()
    {
        var handler = new FakeHttpMessageHandler()
            .RespondError(HttpStatusCode.Unauthorized, WireFixtures.UnauthorizedBody);

        var error = await Assert.ThrowsAsync<AIProviderException>(
            () => ModelsOf(ProviderHarness.OpenRouter(handler)));

        Assert.Equal(AIErrorKind.InvalidApiKey, error.Kind);
        Assert.Equal(OpenRouterProvider.ProviderId, error.ProviderId);

        // Section 26. The key travels in one direction only; an error card that echoed it
        // would put it on screen and, worse, into whatever the user pastes into a bug report.
        AssertNoKeyAnywhere(error.UserMessage, error.TechnicalDetails);
    }

    [Fact]
    public async Task A_successful_connection_test_reports_how_many_models_the_key_can_see()
    {
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.OpenRouterCatalogue);

        var result = await ProviderHarness.OpenRouter(handler)
            .TestConnectionAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(4, result.ModelCount);
        Assert.Contains("4", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_connection_test_returns_a_result_rather_than_throwing()
    {
        // "Test" reporting a failure is the expected outcome of a wrong key, and Settings
        // shows it next to the status dot instead of unwinding to an exception dialog.
        var handler = new FakeHttpMessageHandler()
            .RespondError(HttpStatusCode.Unauthorized, WireFixtures.UnauthorizedBody);

        var result = await ProviderHarness.OpenRouter(handler)
            .TestConnectionAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Null(result.ModelCount);
        Assert.NotNull(result.TechnicalDetails);
        AssertNoKeyAnywhere(result.Message, result.TechnicalDetails);
    }

    [Fact]
    public async Task Every_model_NVIDIA_lists_is_offered_even_though_it_describes_none_of_them()
    {
        // Section 8 rules out a hardcoded list, and NVIDIA's catalogue is ids and an owner.
        // Surfacing them all beats shipping a list that goes stale.
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.NvidiaCatalogue);

        var models = await ModelsOf(ProviderHarness.Nvidia(handler));

        Assert.Equal(
            ["meta/llama-3.1-70b-instruct", "moonshotai/kimi-k2-instruct", "nvidia/vila", "qwen/qwen2.5-coder-32b-instruct"],
            models.Select(m => m.ModelId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_bare_model_id_is_turned_into_something_readable()
    {
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.NvidiaCatalogue);

        var models = await ModelsOf(ProviderHarness.Nvidia(handler));

        Assert.Equal(
            ["Meta / Llama 3.1 70B Instruct", "Moonshotai / Kimi K2 Instruct", "Nvidia / Vila", "Qwen / Qwen2.5 Coder 32B Instruct"],
            models.Select(m => m.Name));
    }

    [Theory]
    [InlineData("meta/llama-3.1-70b-instruct", 128_000)]
    [InlineData("meta/llama-3.1-405b-instruct", 128_000)]
    [InlineData("qwen/qwen2.5-coder-32b-instruct", 32_768)]
    [InlineData("nvidia/llama-3.3-nemotron-super-49b", 128_000)]
    [InlineData("moonshotai/kimi-k2-instruct", null)]
    public async Task A_documented_context_window_is_filled_in_by_model_family(string modelId, int? expected)
    {
        // The endpoint returns no context length at all. A family prefix covers models that
        // did not exist when the table was written; an unknown model simply shows no badge.
        var handler = new FakeHttpMessageHandler().RespondJson($$"""{"data":[{"id":"{{modelId}}"}]}""");

        var model = Assert.Single(await ModelsOf(ProviderHarness.Nvidia(handler)));

        Assert.Equal(expected, model.ContextWindow);
    }

    [Theory]
    [InlineData("nvidia/vila", true)]
    [InlineData("meta/llama-4-scout-17b", true)]
    [InlineData("qwen/qwen2-vl-7b", true)]
    [InlineData("meta/llama-3.1-70b-instruct", false)]
    [InlineData("moonshotai/kimi-k2-instruct", false)]
    public async Task Vision_capability_is_inferred_from_the_id_because_nothing_else_reports_it(
        string modelId, bool expected)
    {
        var handler = new FakeHttpMessageHandler().RespondJson($$"""{"data":[{"id":"{{modelId}}"}]}""");

        var model = Assert.Single(await ModelsOf(ProviderHarness.Nvidia(handler)));

        Assert.Equal(expected, model.SupportsImages);
    }

    [Fact]
    public async Task NVIDIA_claims_nothing_the_catalogue_does_not_actually_say()
    {
        // Advertising tool support or a price that turns out to be wrong is worse than
        // showing neither: the user would pick a model on the strength of it.
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.NvidiaCatalogue);

        var model = (await ModelsOf(ProviderHarness.Nvidia(handler)))
            .Single(m => m.ModelId == "meta/llama-3.1-70b-instruct");

        Assert.Equal("Published by meta", model.Description);
        Assert.True(model.SupportsStreaming);
        Assert.False(model.SupportsTools);
        Assert.Null(model.PromptPricePerMillion);
        Assert.Null(model.CompletionPricePerMillion);
        Assert.Empty(model.SupportedParameters);
    }

    [Theory]
    [InlineData("http://localhost:8000/v1", "http://localhost:8000/v1/models")]
    [InlineData("http://localhost:8000/v1/", "http://localhost:8000/v1/models")]
    [InlineData("https://nim.internal.example/v1", "https://nim.internal.example/v1/models")]
    public async Task Pointing_NVIDIA_at_a_self_hosted_NIM_is_a_configuration_change(string baseUrl, string expected)
    {
        // Section 9: the endpoint moves without touching the provider, let alone the UI.
        // The same models are served by the hosted API, a NIM container and an on-prem box.
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.NvidiaCatalogue);

        await ModelsOf(ProviderHarness.Nvidia(handler, baseUrl: baseUrl));

        Assert.Equal(expected, handler.LastRequest.Uri.ToString());
    }

    [Fact]
    public async Task The_default_NVIDIA_endpoint_is_the_hosted_API_over_https()
    {
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.NvidiaCatalogue);

        await ModelsOf(ProviderHarness.Nvidia(handler));

        Assert.Equal("https://integrate.api.nvidia.com/v1/models", handler.LastRequest.Uri.ToString());
    }

    [Fact]
    public async Task One_provider_headers_do_not_leak_into_another()
    {
        // The clients are named per provider and the bearer token is attached per request.
        // OpenRouter's attribution headers on an NVIDIA call would be the visible symptom of
        // a shared HttpClient carrying default headers - and the key would be next.
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.NvidiaCatalogue);

        await ModelsOf(ProviderHarness.Nvidia(handler));

        Assert.Null(handler.LastRequest.Header("X-Title"));
        Assert.Null(handler.LastRequest.Header("HTTP-Referer"));
    }

    [Fact]
    public async Task Each_provider_reads_only_its_own_stored_key()
    {
        // Keys are stored per provider id. Reading the wrong slot would send OpenRouter's
        // credential to NVIDIA, which is a disclosure rather than an inconvenience.
        var storage = FakeSecureStorage.With(NvidiaProvider.ProviderId, ProviderHarness.DummyKey);
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.NvidiaCatalogue);

        await ModelsOf(ProviderHarness.Nvidia(handler, storage));

        Assert.Equal([NvidiaProvider.ProviderId], storage.Reads);
    }

    private static Task<IReadOnlyList<AIModelDescriptor>> ModelsOf(IAIProvider provider) =>
        provider.GetModelsAsync(TestContext.Current.CancellationToken);

    /// <summary>Fails if a placeholder credential shows up in anything user-visible.</summary>
    private static void AssertNoKeyAnywhere(params string?[] texts)
    {
        foreach (var text in texts)
        {
            Assert.DoesNotContain(ProviderHarness.DummyKey, text ?? string.Empty, StringComparison.Ordinal);
        }
    }
}
