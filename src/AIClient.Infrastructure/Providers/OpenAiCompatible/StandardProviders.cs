using System.Collections.Frozen;
using System.Text.Json;
using AIClient.Domain.Interfaces;
using AIClient.Domain.Models;
using Microsoft.Extensions.Logging;

namespace AIClient.Infrastructure.Providers.OpenAiCompatible;

/// <summary>
/// The hosted catalogues the build ships with beyond OpenRouter and NVIDIA: OpenAI,
/// Groq, xAI, Mistral and DeepSeek - every one of them behind the OpenAI wire protocol,
/// each a handful of overrides on <see cref="OpenAiCompatibleProvider"/>.
/// </summary>
/// <remarks>
/// The differences between these backends are exactly the four things the base class
/// parametrises: where they live, what their catalogue looks like, and nothing else.
/// Streaming, tools, cancellation and error handling are the one shared implementation,
/// so a capability these providers add later appears in all of them at once.
/// </remarks>
public sealed class OpenAiProvider : OpenAiCompatibleProvider
{
    public const string ProviderId = "openai";

    /// <summary>Where the user gets a key. Surfaced by Settings, not used for requests.</summary>
    public const string ApiKeyUrl = "https://platform.openai.com/api-keys";

    /// <summary>
    /// Context windows OpenAI documents but does not return from the catalogue endpoint,
    /// which lists ids and owners only. Matched on a longest prefix of the model id.
    /// Presentation metadata only - it never gates a request.
    /// </summary>
    private static readonly FrozenDictionary<string, int> KnownContextWindows =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5"] = 400_000,
            ["gpt-4.1"] = 1_000_000,
            ["chatgpt-4o"] = 128_000,
            ["gpt-4o"] = 128_000,
            ["o4-mini"] = 200_000,
            ["o3"] = 200_000,
            ["o1"] = 200_000,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Substrings that identify a vision-capable deployment.</summary>
    private static readonly FrozenSet<string> VisionMarkers =
        new[] { "gpt-4o", "gpt-4.1", "chatgpt-4o", "o3", "o4-mini", "o1" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public OpenAiProvider(
        IHttpClientFactory httpClientFactory,
        ISecureStorage secureStorage,
        ILogger<OpenAiProvider> logger)
        : base(httpClientFactory, secureStorage, logger)
    {
    }

    public override string Id => ProviderId;

    public override string DisplayName => "OpenAI";

    protected override string BaseUrl => "https://api.openai.com/v1";

    protected override string HttpClientName => ProviderId;

    protected override IReadOnlyList<AIModelDescriptor> ParseModels(JsonDocument document)
    {
        var models = new List<AIModelDescriptor>();

        foreach (var item in ReadDataArray(document))
        {
            var modelId = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(modelId))
            {
                continue;
            }

            models.Add(new AIModelDescriptor
            {
                ModelId = modelId,
                Name = HumaniseModelId(modelId),
                ContextWindow = ReadInt(item, "context_length") ?? LongestPrefixLookup(modelId, KnownContextWindows),
                SupportsStreaming = true,
                SupportsImages = MatchesAny(modelId, VisionMarkers),

                // Every current OpenAI model accepts function calling, and the catalogue
                // publishes no flag to read - claiming it is the honest default here, the
                // opposite of the NVIDIA case where the audience is mostly open weights.
                SupportsTools = true,
                SupportedParameters = [],
                RawMetadataJson = item.GetRawText(),
            });
        }

        SortByName(models);
        return models;
    }

    internal static bool MatchesAny(string modelId, FrozenSet<string> markers)
    {
        foreach (var marker in markers)
        {
            if (modelId.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Groq - the LPU inference host behind an OpenAI-compatible API. Its catalogue is the
/// most informative of the plain ones: per-model context windows and completion limits.
/// </summary>
public sealed class GroqProvider : OpenAiCompatibleProvider
{
    public const string ProviderId = "groq";

    public const string ApiKeyUrl = "https://console.groq.com/keys";

    /// <summary>Substrings identifying multimodal deployments, which the catalogue does not flag.</summary>
    private static readonly FrozenSet<string> VisionMarkers =
        new[] { "vision", "llama-4", "maverick", "scout" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public GroqProvider(
        IHttpClientFactory httpClientFactory,
        ISecureStorage secureStorage,
        ILogger<GroqProvider> logger)
        : base(httpClientFactory, secureStorage, logger)
    {
    }

    public override string Id => ProviderId;

    public override string DisplayName => "Groq";

    protected override string BaseUrl => "https://api.groq.com/openai/v1";

    protected override string HttpClientName => ProviderId;

    protected override IReadOnlyList<AIModelDescriptor> ParseModels(JsonDocument document)
    {
        var models = new List<AIModelDescriptor>();

        foreach (var item in ReadDataArray(document))
        {
            var modelId = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(modelId))
            {
                continue;
            }

            models.Add(new AIModelDescriptor
            {
                ModelId = modelId,
                Name = ReadString(item, "id") is { } raw ? HumaniseModelId(raw) : modelId,
                ContextWindow = ReadInt(item, "context_length") ?? ReadInt(item, "context_window"),
                MaxOutputTokens = ReadInt(item, "max_completion_tokens"),
                SupportsStreaming = true,
                SupportsImages = OpenAiProvider.MatchesAny(modelId, VisionMarkers),

                // The Groq catalogue publishes no tool flag. Silence is read as "unknown"
                // rather than "no" - the empty parameter list is what marks it as such, and
                // agent mode offers tools anyway. See ModelInfo.ToolsRuledOut.
                SupportsTools = false,
                SupportedParameters = [],
                RawMetadataJson = item.GetRawText(),
            });
        }

        SortByName(models);
        return models;
    }
}

/// <summary>
/// xAI - Grok models behind an OpenAI-compatible API at <c>api.x.ai</c>.
/// </summary>
public sealed class XaiProvider : OpenAiCompatibleProvider
{
    public const string ProviderId = "xai";

    public const string ApiKeyUrl = "https://console.x.ai";

    private static readonly FrozenDictionary<string, int> KnownContextWindows =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["grok-4"] = 256_000,
            ["grok-code-fast"] = 256_000,
            ["grok-3"] = 131_072,
            ["grok-2"] = 131_072,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public XaiProvider(
        IHttpClientFactory httpClientFactory,
        ISecureStorage secureStorage,
        ILogger<XaiProvider> logger)
        : base(httpClientFactory, secureStorage, logger)
    {
    }

    public override string Id => ProviderId;

    public override string DisplayName => "xAI Grok";

    protected override string BaseUrl => "https://api.x.ai/v1";

    protected override string HttpClientName => ProviderId;

    protected override IReadOnlyList<AIModelDescriptor> ParseModels(JsonDocument document)
    {
        var models = new List<AIModelDescriptor>();

        foreach (var item in ReadDataArray(document))
        {
            var modelId = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(modelId))
            {
                continue;
            }

            models.Add(new AIModelDescriptor
            {
                ModelId = modelId,
                Name = HumaniseModelId(modelId),
                ContextWindow = ReadInt(item, "context_length") ?? LongestPrefixLookup(modelId, KnownContextWindows),
                SupportsStreaming = true,
                SupportsImages = OpenAiProvider.MatchesAny(modelId, new[] { "vision", "grok-4" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase)),
                SupportsTools = true,
                SupportedParameters = [],
                RawMetadataJson = item.GetRawText(),
            });
        }

        SortByName(models);
        return models;
    }
}

/// <summary>
/// Mistral - <c>api.mistral.ai</c>, whose catalogue is the richest of this group: a
/// capabilities object (function calling, vision) and a documented context length.
/// </summary>
public sealed class MistralProvider : OpenAiCompatibleProvider
{
    public const string ProviderId = "mistral";

    public const string ApiKeyUrl = "https://console.mistral.ai/api-keys";

    public MistralProvider(
        IHttpClientFactory httpClientFactory,
        ISecureStorage secureStorage,
        ILogger<MistralProvider> logger)
        : base(httpClientFactory, secureStorage, logger)
    {
    }

    public override string Id => ProviderId;

    public override string DisplayName => "Mistral";

    protected override string BaseUrl => "https://api.mistral.ai/v1";

    protected override string HttpClientName => ProviderId;

    protected override IReadOnlyList<AIModelDescriptor> ParseModels(JsonDocument document)
    {
        var models = new List<AIModelDescriptor>();

        foreach (var item in ReadDataArray(document))
        {
            var modelId = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(modelId))
            {
                continue;
            }

            // Legacy and embedding endpoints ride the same catalogue; entries that cannot
            // chat have nothing to do in a chat client's picker.
            var capabilities = item.TryGetProperty("capabilities", out var c) ? c : default;
            var isChat = capabilities.ValueKind != JsonValueKind.Object ||
                (ReadBool(capabilities, "completion_chat") ?? true);

            if (!isChat)
            {
                continue;
            }

            models.Add(new AIModelDescriptor
            {
                ModelId = modelId,
                Name = ReadString(item, "name") ?? HumaniseModelId(modelId),
                Description = ReadString(item, "description"),
                ContextWindow = ReadInt(item, "max_context_length") ?? ReadInt(item, "context_length"),
                SupportsStreaming = true,
                SupportsImages = ReadBool(capabilities, "vision") ?? false,
                SupportsTools = ReadBool(capabilities, "function_calling") ?? false,
                SupportedParameters = [],
                RawMetadataJson = item.GetRawText(),
            });
        }

        SortByName(models);
        return models;
    }

    /// <summary>Reads an optional boolean capability, tolerating a missing or null field.</summary>
    private static bool? ReadBool(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.True
            ? true
            : element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(propertyName, out var falseValue) &&
                falseValue.ValueKind == JsonValueKind.False
                ? false
                : null;
}

/// <summary>
/// DeepSeek - <c>api.deepseek.com</c>, whose catalogue lists ids and owners only. The
/// two models the API serves have documented, fixed context windows.
/// </summary>
public sealed class DeepSeekProvider : OpenAiCompatibleProvider
{
    public const string ProviderId = "deepseek";

    public const string ApiKeyUrl = "https://platform.deepseek.com/api_keys";

    private static readonly FrozenDictionary<string, int> KnownContextWindows =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["deepseek-chat"] = 131_072,
            ["deepseek-reasoner"] = 131_072,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public DeepSeekProvider(
        IHttpClientFactory httpClientFactory,
        ISecureStorage secureStorage,
        ILogger<DeepSeekProvider> logger)
        : base(httpClientFactory, secureStorage, logger)
    {
    }

    public override string Id => ProviderId;

    public override string DisplayName => "DeepSeek";

    protected override string BaseUrl => "https://api.deepseek.com/v1";

    protected override string HttpClientName => ProviderId;

    protected override IReadOnlyList<AIModelDescriptor> ParseModels(JsonDocument document)
    {
        var models = new List<AIModelDescriptor>();

        foreach (var item in ReadDataArray(document))
        {
            var modelId = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(modelId))
            {
                continue;
            }

            models.Add(new AIModelDescriptor
            {
                ModelId = modelId,
                Name = HumaniseModelId(modelId),
                ContextWindow = ReadInt(item, "context_length")
                    ?? LongestPrefixLookup(modelId, KnownContextWindows),
                SupportsStreaming = true,
                SupportsImages = false,
                SupportsTools = true,
                SupportedParameters = [],
                RawMetadataJson = item.GetRawText(),
            });
        }

        SortByName(models);
        return models;
    }
}
