using System.Text.Json;
using AIClient.Domain.Enums;
using AIClient.Domain.Interfaces;
using AIClient.Domain.Models;
using AIClient.Infrastructure.Providers;
using AIClient.Infrastructure.Providers.OpenAiCompatible;
using AIClient.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIClient.Tests;

/// <summary>
/// The catalogues of the standard OpenAI-compatible backends beyond OpenRouter and NVIDIA:
/// what each one publishes, and what the picker should show for it.
/// </summary>
/// <remarks>
/// The providers under test are the production classes over a scripted socket, the same
/// discipline the OpenRouter and NVIDIA suites follow. The fixtures carry the entry shapes
/// that actually break parsers: the id-only entry, the string-encoded number, the entry
/// whose capabilities object is absent.
/// </remarks>
public sealed class StandardProviderCatalogueTests
{
    // ---------------------------------------------------------------- OpenAI / xAI / DeepSeek

    [Fact]
    public async Task OpenAI_ids_alone_still_become_models_with_known_context_windows()
    {
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.IdOnlyCatalogue);
        var provider = ProviderHarness.OpenAI(handler);

        var models = await provider.GetModelsAsync(Token);

        // Four entries in, three out: the empty id could never be requested.
        Assert.Equal(3, models.Count);
        Assert.Equal("Gpt 4.1 Mini", models.Single(m => m.ModelId == "gpt-4.1-mini").Name);

        // The catalogue says nothing; the known-facts table does.
        var gpt41 = models.Single(m => m.ModelId == "gpt-4.1-mini");
        Assert.Equal(1_000_000, gpt41.ContextWindow);
        Assert.True(gpt41.SupportsImages, "every 4.x model is multimodal.");
        Assert.True(gpt41.SupportsTools);
    }

    [Fact]
    public async Task OpenAI_models_without_a_known_family_show_no_context_badge_rather_than_a_wrong_one()
    {
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.IdOnlyCatalogue);

        // An id matching no known prefix: null is "unknown", which the picker renders as
        // no badge - a wrong window would be worse than none.
        Assert.Null((await ProviderHarness.OpenAI(handler).GetModelsAsync(Token))
            .Single(m => m.ModelId == "some-unknown-model").ContextWindow);
    }

    // ---------------------------------------------------------------- Groq

    [Fact]
    public async Task Groq_context_windows_come_from_the_catalogue_itself()
    {
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.GroqCatalogue);

        var models = await ProviderHarness.Groq(handler).GetModelsAsync(Token);

        var llama = Assert.Single(models, m => m.ModelId == "llama-3.3-70b-versatile");
        Assert.Equal(131_072, llama.ContextWindow);
        Assert.Equal(32_768, llama.MaxOutputTokens);
        Assert.False(llama.SupportsTools, "Groq's catalogue publishes no tool flag; silence is unknown.");

        // Multimodal ids are recognised by their family name, which the catalogue omits.
        var scout = Assert.Single(models, m => m.ModelId.Contains("scout"));
        Assert.True(scout.SupportsImages);
    }

    // ---------------------------------------------------------------- Mistral

    [Fact]
    public async Task Mistral_capabilities_decide_tools_and_vision_and_non_chat_entries_are_skipped()
    {
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.MistralCatalogue);

        var models = await ProviderHarness.Mistral(handler).GetModelsAsync(Token);

        // Three entries in, two out: an embedding model has nothing to do in a chat picker.
        Assert.Equal(2, models.Count);

        var large = Assert.Single(models, m => m.ModelId == "mistral-large-latest");
        Assert.True(large.SupportsTools);
        Assert.False(large.SupportsImages);
        Assert.Equal(131_072, large.ContextWindow);

        Assert.True(Assert.Single(models, m => m.ModelId == "pixtral-large-latest").SupportsImages);
    }

    // ---------------------------------------------------------------- Custom providers

    [Fact]
    public async Task A_custom_provider_speaks_the_standard_wire_against_its_own_base_url()
    {
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.IdOnlyCatalogue);
        var provider = ProviderHarness.Custom("custom-test01", "My gateway", "https://gw.example.com/v1", handler);

        var models = await provider.GetModelsAsync(Token);

        Assert.Equal(3, models.Count);

        var request = handler.LastRequest;
        Assert.Equal("https://gw.example.com/v1/models", request.Uri.ToString());

        // The key arrives per request, from secure storage, under the custom id.
        Assert.Equal($"Bearer {ProviderHarness.DummyKey}", request.Header("Authorization"));
    }

    [Fact]
    public async Task A_custom_base_url_without_a_host_is_rejected_rather_than_stored()
    {
        var db = await TestDatabase.CreateAsync();
        try
        {
            var store = db.CustomStore(FakeSecureStorage.Empty());

            await Assert.ThrowsAsync<ArgumentException>(() =>
                store.AddAsync("Broken", "not a url at all", Token));
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    // ---------------------------------------------------------------- Registry CRUD (real SQLite)

    [Fact]
    public async Task A_custom_provider_added_through_the_registry_persists_and_resolves()
    {
        var db = await TestDatabase.CreateAsync();
        try
        {
            var storage = new FakeSecureStorage();
            var registry = db.Registry(storage);

            await registry.LoadCustomProvidersAsync(Token);
            var added = await registry.AddCustomProviderAsync("My gateway", "https://gw.example.com/v1", Token);

            Assert.True(added.IsCustom);
            Assert.Equal("My gateway", added.Name);

            // The implementation resolves by the minted id and the row exists.
            Assert.NotNull(registry.GetProvider(added.Id));

            await using var check = db.CreateDbContext();
            var row = await check.Providers.SingleAsync(p => p.Id == added.Id, Token);
            Assert.Equal(CustomProviderStore.RowType, row.Type);
            Assert.Equal("https://gw.example.com/v1", row.BaseUrlOverride);
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task Removing_a_custom_provider_takes_its_models_its_key_and_its_implementation()
    {
        var db = await TestDatabase.CreateAsync();
        try
        {
            var storage = FakeSecureStorage.Empty();
            var registry = db.Registry(storage);

            await registry.LoadCustomProvidersAsync(Token);
            var added = await registry.AddCustomProviderAsync("To remove", "https://x.example.com/v1", Token);

            await registry.SetApiKeyAsync(added.Id, "sk-something", Token);

            // Cached models, written directly: the point of the test is the cascade, not
            // the catalogue fetch.
            await using (var seed = db.CreateDbContext())
            {
                seed.Models.Add(new Domain.Entities.Model
                {
                    Id = $"{added.Id}:some-model",
                    ProviderId = added.Id,
                    ModelId = "some-model",
                    Name = "Some Model",
                });
                await seed.SaveChangesAsync(Token);
            }

            await registry.RemoveCustomProviderAsync(added.Id, Token);

            Assert.Null(registry.GetProvider(added.Id));
            Assert.False(await registry.HasApiKeyAsync(added.Id, Token));

            await using var check = db.CreateDbContext();
            Assert.Equal(0, await check.Providers.CountAsync(p => p.Id == added.Id, Token));

            // Cascade: a provider's cached catalogue has no reason to outlive it.
            Assert.Equal(0, await check.Models.CountAsync(m => m.ProviderId == added.Id, Token));
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_built_in_provider_cannot_be_removed_through_the_custom_path()
    {
        var db = await TestDatabase.CreateAsync();
        try
        {
            var registry = db.Registry(FakeSecureStorage.Empty());

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                registry.RemoveCustomProviderAsync(OpenRouterProvider.ProviderId, Token));
        }
        finally
        {
            await db.DisposeAsync();
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
