using System.Text.Json;
using AIClient.Domain.Interfaces;
using AIClient.Domain.Models;
using Microsoft.Extensions.Logging;

namespace AIClient.Infrastructure.Providers.OpenAiCompatible;

/// <summary>
/// A user-defined OpenAI-compatible backend: any endpoint the user names that speaks
/// <c>/chat/completions</c> - a proxy, a self-hosted gateway, a niche provider.
/// </summary>
/// <remarks>
/// One instance per custom provider row, constructed at runtime by
/// <see cref="CustomProviderCatalog"/> rather than by the DI container, because the set
/// of custom providers is user state and not build state. Everything the built-in
/// subclasses override is supplied here from the row: id, display name and base URL.
/// </remarks>
public sealed class CustomOpenAiCompatibleProvider : OpenAiCompatibleProvider
{
    private readonly string _id;
    private readonly string _displayName;
    private readonly string _baseUrl;

    public CustomOpenAiCompatibleProvider(
        string id,
        string displayName,
        string baseUrl,
        IHttpClientFactory httpClientFactory,
        ISecureStorage secureStorage,
        ILogger<CustomOpenAiCompatibleProvider> logger)
        : base(httpClientFactory, secureStorage, logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        _id = id;
        _displayName = displayName;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public override string Id => _id;

    public override string DisplayName => _displayName;

    protected override string BaseUrl => _baseUrl;

    protected override string HttpClientName => "custom";

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
                ContextWindow = ReadInt(item, "context_length"),
                MaxOutputTokens = ReadInt(item, "max_completion_tokens"),
                SupportsStreaming = true,

                // Unknown endpoint: silence about capabilities is read as "unknown", the
                // same posture the NVIDIA and Groq catalogues get. The request goes out
                // and the endpoint is the one that gets to say no.
                SupportsTools = false,
                SupportedParameters = [],
                RawMetadataJson = item.GetRawText(),
            });
        }

        SortByName(models);
        return models;
    }
}
