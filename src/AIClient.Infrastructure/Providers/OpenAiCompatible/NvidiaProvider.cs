using System.Collections.Frozen;
using System.Text.Json;
using AIClient.Domain.Interfaces;
using AIClient.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIClient.Infrastructure.Providers.OpenAiCompatible;

/// <summary>
/// NVIDIA - the hosted NIM catalogue at <c>integrate.api.nvidia.com</c>, which serves
/// the models listed on build.nvidia.com behind an OpenAI-compatible API.
/// </summary>
/// <remarks>
/// The endpoint is injected rather than baked in. NVIDIA ships the same models three ways -
/// the hosted API used here, a self-hosted NIM container, and on-prem deployments - and they
/// differ only in base URL. Pointing this provider at a local NIM is a settings change, not
/// a code change, and nothing above the provider layer notices.
///
/// The catalogue is the weak point: <c>/v1/models</c> returns little beyond ids, with no
/// context window, pricing or capability flags. Rather than hardcode a model list - which
/// section 8 rules out and which would go stale - every returned id is surfaced and known
/// families are annotated with their published context window. An unrecognised model still
/// works; it just shows no context badge until the user sends a message.
/// </remarks>
public sealed class NvidiaProvider : OpenAiCompatibleProvider
{
    public const string ProviderId = "nvidia";

    /// <summary>Where the user gets a key. Surfaced by Settings, not used for requests.</summary>
    public const string ApiKeyUrl = "https://build.nvidia.com/settings/api-keys";

    /// <summary>
    /// Context windows NVIDIA documents but does not return from the catalogue endpoint.
    /// Matched on a prefix of the model id, longest first, so
    /// <c>meta/llama-3.1-405b-instruct</c> resolves without an entry of its own.
    /// This is presentation metadata only - it never gates a request.
    /// </summary>
    private static readonly FrozenDictionary<string, int> KnownContextWindows =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["deepseek-ai/deepseek-r1"] = 128_000,
            ["deepseek-ai/deepseek-v3"] = 128_000,
            ["meta/llama-4"] = 1_000_000,
            ["meta/llama-3.3"] = 128_000,
            ["meta/llama-3.2"] = 128_000,
            ["meta/llama-3.1"] = 128_000,
            ["meta/llama3"] = 8_192,
            ["meta/codellama"] = 16_384,
            ["mistralai/mistral-large"] = 128_000,
            ["mistralai/mixtral-8x22b"] = 64_000,
            ["mistralai/mixtral-8x7b"] = 32_768,
            ["mistralai/codestral"] = 32_768,
            ["nvidia/llama-3.1-nemotron"] = 128_000,
            ["nvidia/llama-3.3-nemotron"] = 128_000,
            ["nvidia/nemotron"] = 128_000,
            ["qwen/qwen2.5-coder"] = 32_768,
            ["qwen/qwen2.5"] = 32_768,
            ["qwen/qwq"] = 32_768,
            ["google/gemma-3"] = 128_000,
            ["google/gemma-2"] = 8_192,
            ["microsoft/phi-4"] = 16_384,
            ["microsoft/phi-3"] = 128_000,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Substrings that identify a vision-capable deployment. NVIDIA encodes this in the
    /// model id because the catalogue exposes no modality field.
    /// </summary>
    private static readonly FrozenSet<string> VisionMarkers =
        new[] { "vision", "vila", "neva", "llama-4", "gemma-3", "phi-3.5-vision", "-vl" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly ProviderEndpointOptions _endpoints;

    public NvidiaProvider(
        IHttpClientFactory httpClientFactory,
        ISecureStorage secureStorage,
        IOptions<ProviderEndpointOptions> endpoints,
        ILogger<NvidiaProvider> logger)
        : base(httpClientFactory, secureStorage, logger)
    {
        _endpoints = endpoints.Value;
    }

    public override string Id => ProviderId;

    public override string DisplayName => "NVIDIA";

    protected override string BaseUrl => _endpoints.Nvidia.TrimEnd('/');

    protected override string HttpClientName => ProviderId;

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

            models.Add(new AIModelDescriptor
            {
                ModelId = modelId,
                Name = FormatDisplayName(modelId),
                Description = ReadString(item, "owned_by") is { Length: > 0 } owner
                    ? $"Published by {owner}"
                    : null,
                ContextWindow = ReadInt(item, "context_length") ?? ResolveContextWindow(modelId),
                SupportsStreaming = true,
                SupportsImages = IsVisionModel(modelId),

                // The catalogue says nothing about tool support, and claiming it falsely is
                // worse than omitting it. Left false until the model page is machine-readable.
                SupportsTools = false,

                // NVIDIA's hosted tier does not publish per-token prices through the API.
                PromptPricePerMillion = null,
                CompletionPricePerMillion = null,

                // Empty means "unknown", which ChatService reads as "send the defaults".
                // NVIDIA accepts temperature, top_p and max_tokens across the catalogue.
                SupportedParameters = [],
                RawMetadataJson = item.GetRawText(),
            });
        }

        models.Sort(static (x, y) => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));

        return models;
    }

    /// <summary>
    /// Turns <c>meta/llama-3.1-70b-instruct</c> into <c>Meta / Llama 3.1 70B Instruct</c>.
    /// The raw id stays the request identifier; this is only what the picker shows.
    /// </summary>
    private static string FormatDisplayName(string modelId)
    {
        var slash = modelId.IndexOf('/');
        var vendor = slash > 0 ? modelId[..slash] : null;
        var name = slash > 0 ? modelId[(slash + 1)..] : modelId;

        var words = name.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var parts = new List<string>(words.Length);

        foreach (var word in words)
        {
            parts.Add(FormatWord(word));
        }

        var formatted = string.Join(' ', parts);

        return vendor is null ? formatted : $"{FormatWord(vendor)} / {formatted}";
    }

    private static string FormatWord(string word)
    {
        // Parameter counts read better upper-cased: "70b" -> "70B", "8x7b" -> "8X7B".
        if (char.IsDigit(word[0]))
        {
            return word.EndsWith('b') || word.EndsWith('B')
                ? word.ToUpperInvariant()
                : word;
        }

        return char.ToUpperInvariant(word[0]) + word[1..];
    }

    /// <summary>Longest-prefix lookup, so a specific entry always beats a family entry.</summary>
    private static int? ResolveContextWindow(string modelId)
    {
        var bestLength = 0;
        int? best = null;

        foreach (var (prefix, window) in KnownContextWindows)
        {
            if (prefix.Length > bestLength && modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                bestLength = prefix.Length;
                best = window;
            }
        }

        return best;
    }

    private static bool IsVisionModel(string modelId)
    {
        foreach (var marker in VisionMarkers)
        {
            if (modelId.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
