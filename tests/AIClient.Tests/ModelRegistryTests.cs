using System.Text.Json;
using AIClient.Domain.Enums;
using AIClient.Domain.Models;
using AIClient.Infrastructure.Providers;
using AIClient.Infrastructure.Providers.OpenAiCompatible;
using AIClient.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace AIClient.Tests;

/// <summary>
/// Section 10's model registry, over a real SQLite database.
/// </summary>
/// <remarks>
/// The registry is the seam between the network and the UI, and almost everything worth
/// asserting about it is a persistence question: the picker reads cached rows rather than
/// making an HTTP call (sections 27 and 31), a refresh replaces the cache without duplicating
/// it, and a failed refresh leaves yesterday's list intact. So this runs against the migrated
/// schema with a scripted provider standing in for the catalogue endpoint - no key, no socket.
/// </remarks>
public sealed class ModelRegistryTests : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await TestDatabase.CreateAsync();

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task A_refresh_caches_the_catalogue_and_returns_it()
    {
        var provider = ProviderWith(Descriptor("openai/gpt-5-mini", "OpenAI: GPT-5 Mini"));
        var registry = Registry(provider);

        var models = await registry.RefreshModelsAsync(OpenRouterProvider.ProviderId, Token);

        var model = Assert.Single(models);
        Assert.Equal("openai/gpt-5-mini", model.ModelId);

        // Named for the picker's header, not for the id in the request.
        Assert.Equal("OpenRouter", model.ProviderName);

        await using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.Models.CountAsync(Token));
    }

    [Fact]
    public async Task Every_field_the_picker_shows_survives_the_round_trip_through_SQLite()
    {
        var provider = ProviderWith(new AIModelDescriptor
        {
            ModelId = "openai/gpt-5-mini",
            Name = "OpenAI: GPT-5 Mini",
            Description = "A small, fast general-purpose model.",
            ContextWindow = 400_000,
            MaxOutputTokens = 128_000,
            SupportsStreaming = true,
            SupportsImages = true,
            SupportsTools = true,
            PromptPricePerMillion = 0.25m,
            CompletionPricePerMillion = 2m,
            SupportedParameters = ["max_tokens", "temperature", "top_p"],
            RawMetadataJson = """{"id":"openai/gpt-5-mini"}""",
        });

        await Registry(provider).RefreshModelsAsync(OpenRouterProvider.ProviderId, Token);

        var model = Assert.Single(await Registry(provider).GetAllModelsAsync(Token));

        Assert.Equal("A small, fast general-purpose model.", model.Description);
        Assert.Equal(400_000, model.ContextWindow);
        Assert.Equal(128_000, model.MaxOutputTokens);
        Assert.True(model.SupportsImages);
        Assert.True(model.SupportsTools);

        // Decimal through a SQLite column that has no decimal type: worth pinning, because
        // a price shown as 0.25000000000000001 in the picker would look like a bug.
        Assert.Equal(0.25m, model.PromptPricePerMillion);
        Assert.Equal(2m, model.CompletionPricePerMillion);

        // Stored as one comma-joined column and split on the way out - section 14 reads this
        // list to decide which sampling parameters may be sent.
        Assert.Equal(["max_tokens", "temperature", "top_p"], model.SupportedParameters);
    }

    [Fact]
    public async Task An_unknown_parameter_list_comes_back_empty_rather_than_as_one_blank_entry()
    {
        // Splitting an empty column carelessly yields [""], and Supports("temperature") would
        // then answer false for every model whose catalogue said nothing - silently dropping
        // the user's temperature setting on most of the picker.
        var provider = ProviderWith(Descriptor("z-ai/glm-4.6", "Z.AI: GLM 4.6"));

        await Registry(provider).RefreshModelsAsync(OpenRouterProvider.ProviderId, Token);

        var model = Assert.Single(await Registry(provider).GetAllModelsAsync(Token));

        Assert.Empty(model.SupportedParameters);
        Assert.True(model.Supports("temperature"));
    }

    [Fact]
    public async Task Refreshing_twice_updates_the_cached_row_instead_of_duplicating_it()
    {
        // The unique index on (provider, model) means a second insert would throw, but the
        // failure the user would see first is the same model listed twice in the picker.
        var provider = ProviderWith(Descriptor("openai/gpt-5-mini", "GPT-5 Mini"));
        var registry = Registry(provider);

        await registry.RefreshModelsAsync(OpenRouterProvider.ProviderId, Token);

        provider.Catalogue[0] = Descriptor("openai/gpt-5-mini", "OpenAI: GPT-5 Mini (renamed)");

        var models = await registry.RefreshModelsAsync(OpenRouterProvider.ProviderId, Token);

        var model = Assert.Single(models);
        Assert.Equal("OpenAI: GPT-5 Mini (renamed)", model.Name);
    }

    [Fact]
    public async Task A_model_the_provider_no_longer_lists_is_dropped_from_the_cache()
    {
        // Offering a retired model would let the user pick something that answers 404.
        var provider = ProviderWith(
            Descriptor("openai/gpt-5-mini", "GPT-5 Mini"),
            Descriptor("openai/gpt-4o-legacy", "GPT-4o (legacy)"));
        var registry = Registry(provider);

        await registry.RefreshModelsAsync(OpenRouterProvider.ProviderId, Token);
        provider.Catalogue.RemoveAt(1);

        var models = await registry.RefreshModelsAsync(OpenRouterProvider.ProviderId, Token);

        Assert.Equal(["openai/gpt-5-mini"], models.Select(m => m.ModelId));
    }

    [Fact]
    public async Task A_refresh_records_when_it_happened()
    {
        // Settings shows it, and it is the only way for the user to tell a stale cache from
        // an empty one.
        var before = DateTimeOffset.UtcNow;
        var registry = Registry(ProviderWith(Descriptor("a/b", "A B")));

        await registry.RefreshModelsAsync(OpenRouterProvider.ProviderId, Token);

        var info = (await registry.GetProvidersAsync(Token))
            .Single(p => p.Id == OpenRouterProvider.ProviderId);

        Assert.NotNull(info.ModelsRefreshedAt);
        Assert.InRange(info.ModelsRefreshedAt.Value, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task Opening_the_picker_reads_the_database_and_touches_no_provider()
    {
        // Sections 27 and 31. An HTTP round trip on every picker open would be slow with a
        // connection and broken without one.
        var provider = ProviderWith(Descriptor("a/b", "A B"));
        var registry = Registry(provider);

        await registry.RefreshModelsAsync(OpenRouterProvider.ProviderId, Token);
        var fetchesAfterRefresh = provider.CatalogueFetches;

        await registry.GetAllModelsAsync(Token);
        await registry.GetModelsAsync(OpenRouterProvider.ProviderId, Token);
        await registry.GetModelAsync(OpenRouterProvider.ProviderId, "a/b", Token);

        Assert.Equal(fetchesAfterRefresh, provider.CatalogueFetches);
    }

    [Fact]
    public async Task With_no_connection_the_last_known_model_list_is_still_offered()
    {
        // Section 31: the app keeps working offline, minus the parts that need a network.
        var provider = ProviderWith(Descriptor("a/b", "A B"));
        var registry = Registry(provider);

        await registry.RefreshModelsAsync(OpenRouterProvider.ProviderId, Token);

        provider.CatalogueFault = new HttpRequestException("No such host is known.");

        Assert.Single(await registry.GetAllModelsAsync(Token));
    }

    [Fact]
    public async Task A_failed_refresh_leaves_the_previous_cache_untouched()
    {
        // The catalogue fetch happens before anything is written, so a mid-refresh failure
        // cannot half-replace the list. Losing the picker's contents because a refresh timed
        // out would be a worse outcome than a stale entry.
        var provider = ProviderWith(Descriptor("a/b", "A B"), Descriptor("c/d", "C D"));
        var registry = Registry(provider);

        await registry.RefreshModelsAsync(OpenRouterProvider.ProviderId, Token);

        provider.CatalogueFault = new TimeoutException("The request timed out.");

        await Assert.ThrowsAsync<TimeoutException>(
            () => registry.RefreshModelsAsync(OpenRouterProvider.ProviderId, Token));

        Assert.Equal(2, (await registry.GetAllModelsAsync(Token)).Count);
    }

    [Fact]
    public async Task Refreshing_a_provider_the_build_does_not_ship_is_a_programming_error()
    {
        // Not a user-facing failure: every id the UI can reach comes from GetProvidersAsync.
        var registry = Registry(ProviderWith(Descriptor("a/b", "A B")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.RefreshModelsAsync("does-not-exist", Token));
    }

    [Fact]
    public async Task The_picker_is_grouped_by_provider_and_sorted_by_model_name()
    {
        // Section 10 asks for a picker grouped by provider. The order is decided here rather
        // than in the view, so it is the same everywhere the list appears.
        var openRouter = ProviderWith(Descriptor("or/zeta", "Zeta"), Descriptor("or/alpha", "Alpha"));
        var nvidia = new ScriptedProvider(NvidiaProvider.ProviderId);
        nvidia.Catalogue.AddRange([Descriptor("nv/beta", "Beta"), Descriptor("nv/aardvark", "Aardvark")]);

        var registry = _db.Registry(new FakeSecureStorage(), openRouter, nvidia);
        await registry.RefreshModelsAsync(OpenRouterProvider.ProviderId, Token);
        await registry.RefreshModelsAsync(NvidiaProvider.ProviderId, Token);

        // OpenRouter sorts before NVIDIA because of its seeded SortOrder, not alphabetically.
        Assert.Equal(
            ["Alpha", "Zeta", "Aardvark", "Beta"],
            (await registry.GetAllModelsAsync(Token)).Select(m => m.Name));
    }

    [Fact]
    public async Task A_disabled_provider_keeps_its_cache_but_disappears_from_the_picker()
    {
        // Turning a provider off is not the same as forgetting it: the models stay cached so
        // turning it back on costs nothing, and Settings still shows the count.
        var provider = ProviderWith(Descriptor("a/b", "A B"));
        var registry = Registry(provider);

        await registry.RefreshModelsAsync(OpenRouterProvider.ProviderId, Token);
        await registry.SetEnabledAsync(OpenRouterProvider.ProviderId, isEnabled: false, Token);

        Assert.Empty(await registry.GetAllModelsAsync(Token));
        Assert.Single(await registry.GetModelsAsync(OpenRouterProvider.ProviderId, Token));

        var info = (await registry.GetProvidersAsync(Token))
            .Single(p => p.Id == OpenRouterProvider.ProviderId);

        Assert.False(info.IsEnabled);
        Assert.Equal(1, info.CachedModelCount);
    }

    [Fact]
    public async Task A_model_is_found_by_provider_and_native_id_and_unknown_ids_return_null()
    {
        // The conversation stores a provider id and a model id; resolving them back to a
        // descriptor is how a reopened chat knows what it was talking to.
        var registry = Registry(ProviderWith(Descriptor("a/b", "A B")));
        await registry.RefreshModelsAsync(OpenRouterProvider.ProviderId, Token);

        Assert.NotNull(await registry.GetModelAsync(OpenRouterProvider.ProviderId, "a/b", Token));
        Assert.Null(await registry.GetModelAsync(OpenRouterProvider.ProviderId, "a/gone", Token));

        // Right model id, wrong provider: two providers can list the same model, and the
        // request has to go to the one the user picked.
        Assert.Null(await registry.GetModelAsync(NvidiaProvider.ProviderId, "a/b", Token));
    }

    [Fact]
    public void An_unknown_provider_id_resolves_to_null_rather_than_throwing()
    {
        var registry = Registry(ProviderWith(Descriptor("a/b", "A B")));

        Assert.NotNull(registry.GetProvider(OpenRouterProvider.ProviderId));
        Assert.Null(registry.GetProvider("ollama"));
    }

    [Fact]
    public async Task Both_shipped_providers_are_listed_with_somewhere_to_get_a_key()
    {
        // Section 32's wizard and the Providers page both need this: a provider with no key
        // and no link is a dead end for the user.
        var registry = Registry(ProviderWith(Descriptor("a/b", "A B")));

        var providers = await registry.GetProvidersAsync(Token);

        Assert.Equal(["openrouter", "nvidia"], providers.Select(p => p.Id));
        Assert.All(providers, p => Assert.StartsWith("https://", p.ApiKeyUrl!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_provider_with_no_key_is_reported_as_not_configured_rather_than_untested()
    {
        // "Unknown" would show an amber dot the user cannot clear; "not configured" points at
        // the thing they actually have to do.
        var registry = Registry(ProviderWith(Descriptor("a/b", "A B")));

        var info = (await registry.GetProvidersAsync(Token))
            .Single(p => p.Id == OpenRouterProvider.ProviderId);

        Assert.False(info.HasApiKey);
        Assert.Equal(ConnectionState.NotConfigured, info.ConnectionState);
    }

    [Fact]
    public async Task Storing_a_key_is_reported_as_presence_only_and_the_value_never_comes_back()
    {
        // Sections 11 and 26: a key is written and never read back out for display. Asserting
        // on the serialised DTO rather than field by field, so a property added later that
        // carries the value fails this test instead of shipping.
        const string key = "sk-or-v1-not-a-real-key-0123456789";
        var storage = new FakeSecureStorage();
        var registry = _db.Registry(storage, ProviderWith(Descriptor("a/b", "A B")));

        await registry.SetApiKeyAsync(OpenRouterProvider.ProviderId, key, Token);

        var providers = await registry.GetProvidersAsync(Token);

        Assert.True(providers.Single(p => p.Id == OpenRouterProvider.ProviderId).HasApiKey);
        Assert.True(await registry.HasApiKeyAsync(OpenRouterProvider.ProviderId, Token));
        Assert.DoesNotContain(key, JsonSerializer.Serialize(providers), StringComparison.Ordinal);

        // Presence is checked without decrypting: the read path is ContainsAsync, not GetAsync.
        Assert.Empty(storage.Reads);
    }

    [Fact]
    public async Task A_pasted_key_is_trimmed_before_it_is_stored()
    {
        // Copying a key out of a browser routinely brings a newline with it, and the 401 that
        // follows reads exactly like a wrong key.
        var storage = new FakeSecureStorage();
        var registry = _db.Registry(storage, ProviderWith(Descriptor("a/b", "A B")));

        await registry.SetApiKeyAsync(OpenRouterProvider.ProviderId, "  sk-test-key \r\n", Token);

        Assert.Equal("sk-test-key", await storage.GetAsync(OpenRouterProvider.ProviderId, Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Saving_a_blank_key_removes_the_stored_one(string blank)
    {
        // Clearing the box in Settings is how a user revokes a key, and storing whitespace
        // would leave the provider looking configured while every request failed.
        var storage = FakeSecureStorage.With(OpenRouterProvider.ProviderId);
        var registry = _db.Registry(storage, ProviderWith(Descriptor("a/b", "A B")));

        await registry.SetApiKeyAsync(OpenRouterProvider.ProviderId, blank, Token);

        Assert.False(await registry.HasApiKeyAsync(OpenRouterProvider.ProviderId, Token));
    }

    [Fact]
    public async Task Deleting_a_key_leaves_the_cached_models_alone()
    {
        // The catalogue is not secret, and wiping it would make removing a key look like it
        // broke the app.
        var registry = _db.Registry(
            FakeSecureStorage.With(OpenRouterProvider.ProviderId),
            ProviderWith(Descriptor("a/b", "A B")));

        await registry.RefreshModelsAsync(OpenRouterProvider.ProviderId, Token);
        await registry.DeleteApiKeyAsync(OpenRouterProvider.ProviderId, Token);

        var info = (await registry.GetProvidersAsync(Token))
            .Single(p => p.Id == OpenRouterProvider.ProviderId);

        Assert.False(info.HasApiKey);
        Assert.Equal(ConnectionState.NotConfigured, info.ConnectionState);
        Assert.Single(await registry.GetAllModelsAsync(Token));
    }

    [Fact]
    public async Task A_successful_test_turns_the_status_dot_green_for_this_session_only()
    {
        // Connection state is per session on purpose: a "Connected" persisted from yesterday
        // would show green for a key that has since been revoked.
        var provider = ProviderWith(Descriptor("a/b", "A B"));
        var registry = _db.Registry(FakeSecureStorage.With(OpenRouterProvider.ProviderId), provider);

        var result = await registry.TestConnectionAsync(OpenRouterProvider.ProviderId, Token);

        Assert.True(result.Success);
        Assert.Equal(
            ConnectionState.Connected,
            (await registry.GetProvidersAsync(Token)).Single(p => p.Id == OpenRouterProvider.ProviderId)
                .ConnectionState);

        // A fresh registry over the same database has forgotten it.
        var reopened = _db.Registry(FakeSecureStorage.With(OpenRouterProvider.ProviderId), provider);

        Assert.Equal(
            ConnectionState.Unknown,
            (await reopened.GetProvidersAsync(Token)).Single(p => p.Id == OpenRouterProvider.ProviderId)
                .ConnectionState);
    }

    [Fact]
    public async Task Testing_a_provider_the_build_does_not_ship_returns_a_result_rather_than_throwing()
    {
        var registry = Registry(ProviderWith(Descriptor("a/b", "A B")));

        var result = await registry.TestConnectionAsync("ollama", Token);

        Assert.False(result.Success);
        Assert.Contains("ollama", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_picker_is_told_when_the_model_list_changes()
    {
        // Without this an open picker keeps showing an empty list after the first refresh,
        // and the user has to close and reopen it to see anything.
        var registry = Registry(ProviderWith(Descriptor("a/b", "A B")));
        var notified = new List<string>();
        registry.ModelsChanged += (_, providerId) => notified.Add(providerId);

        await registry.RefreshModelsAsync(OpenRouterProvider.ProviderId, Token);
        await registry.SetEnabledAsync(OpenRouterProvider.ProviderId, isEnabled: false, Token);

        Assert.Equal([OpenRouterProvider.ProviderId, OpenRouterProvider.ProviderId], notified);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A scripted OpenRouter, since that is the id the database seeds.</summary>
    private static ScriptedProvider ProviderWith(params AIModelDescriptor[] catalogue)
    {
        var provider = new ScriptedProvider(OpenRouterProvider.ProviderId);
        provider.Catalogue.AddRange(catalogue);
        return provider;
    }

    private ProviderRegistry Registry(ScriptedProvider provider) =>
        _db.Registry(new FakeSecureStorage(), provider);

    private static AIModelDescriptor Descriptor(string modelId, string name) =>
        new() { ModelId = modelId, Name = name };
}
