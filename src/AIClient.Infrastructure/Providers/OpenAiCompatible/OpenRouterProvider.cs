using System.Text.Json;
using AIClient.Domain.Interfaces;
using AIClient.Domain.Models;
using Microsoft.Extensions.Logging;

namespace AIClient.Infrastructure.Providers.OpenAiCompatible;

/// <summary>
/// OpenRouter - a gateway in front of most commercial and open models.
/// </summary>
/// <remarks>
/// Its catalogue is the richest of the providers here: real context windows, per-token
/// pricing, modality flags and an explicit list of accepted sampling parameters. All of it
/// is read rather than hardcoded, so a model added upstream today shows up in the picker
/// on the next refresh without a code change.
/// </remarks>
public sealed class OpenRouterProvider : OpenAiCompatibleProvider
{
    public const string ProviderId = "openrouter";

    /// <summary>Where the user gets a key. Surfaced by Settings, not used for requests.</summary>
    public const string ApiKeyUrl = "https://openrouter.ai/keys";

    public OpenRouterProvider(
        IHttpClientFactory httpClientFactory,
        ISecureStorage secureStorage,
        ILogger<OpenRouterProvider> logger)
        : base(httpClientFactory, secureStorage, logger)
    {
    }

    public override string Id => ProviderId;

    public override string DisplayName => "OpenRouter";

    protected override string BaseUrl => "https://openrouter.ai/api/v1";

    protected override string HttpClientName => ProviderId;

    /// <summary>
    /// OpenRouter attributes traffic with these two headers and shows the app on its
    /// public leaderboard. Neither is required, neither carries user data.
    /// </summary>
    protected override void ConfigureRequest(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/aiclient/aiclient");
        request.Headers.TryAddWithoutValidation("X-Title", "AI Client");
    }

    protected override IReadOnlyList<AIModelDescriptor> ParseModels(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var models = new List<AIModelDescriptor>(data.GetArrayLength());

        foreach (var item in data.EnumerateArray())
        {
            var modelId = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(modelId))
            {
                continue;
            }

            var pricing = item.TryGetProperty("pricing", out var p) ? p : default;
            var architecture = item.TryGetProperty("architecture", out var a) ? a : default;
            var topProvider = item.TryGetProperty("top_provider", out var tp) ? tp : default;

            var supportedParameters = ReadStringArray(item, "supported_parameters");

            models.Add(new AIModelDescriptor
            {
                ModelId = modelId,
                Name = ReadString(item, "name") ?? modelId,
                Description = ReadString(item, "description"),
                ContextWindow = ReadInt(item, "context_length")
                    ?? ReadInt(topProvider, "context_length"),
                MaxOutputTokens = ReadInt(topProvider, "max_completion_tokens"),

                // Every OpenRouter model streams; the catalogue has no flag for it.
                SupportsStreaming = true,
                SupportsImages = HasModality(architecture, "image"),

                // "tools" in supported_parameters is how OpenRouter advertises function
                // calling. Nothing in the MVP uses it, but the picker shows the badge and
                // the agent work later depends on it.
                SupportsTools = supportedParameters.Contains("tools", StringComparer.OrdinalIgnoreCase),

                PromptPricePerMillion = ReadPricePerMillion(pricing, "prompt"),
                CompletionPricePerMillion = ReadPricePerMillion(pricing, "completion"),
                SupportedParameters = supportedParameters,
                RawMetadataJson = item.GetRawText(),
            });
        }

        // Alphabetical by display name: the picker groups by provider and the catalogue
        // arrives in no useful order.
        models.Sort(static (x, y) => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));

        return models;
    }

    /// <summary>
    /// Reads <c>architecture.input_modalities</c>, falling back to the older
    /// <c>architecture.modality</c> string ("text+image-&gt;text") still present on some entries.
    /// </summary>
    private static bool HasModality(JsonElement architecture, string modality)
    {
        if (architecture.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (architecture.TryGetProperty("input_modalities", out var modalities) &&
            modalities.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in modalities.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String &&
                    string.Equals(entry.GetString(), modality, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        var legacy = ReadString(architecture, "modality");
        return legacy is not null && legacy.Contains(modality, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>(array.GetArrayLength());

        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { Length: > 0 } value)
            {
                values.Add(value);
            }
        }

        return values;
    }
}
