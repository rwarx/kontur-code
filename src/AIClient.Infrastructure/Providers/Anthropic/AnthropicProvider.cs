using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIClient.Application.Services;
using AIClient.Domain.Enums;
using AIClient.Domain.Interfaces;
using AIClient.Domain.Models;
using AIClient.Infrastructure.Http;
using Microsoft.Extensions.Logging;

namespace AIClient.Infrastructure.Providers.Anthropic;

/// <summary>
/// Wire DTOs for the Anthropic Messages API (<c>/v1/messages</c>). Kept in one place
/// beside the provider that speaks it, for the same reason the OpenAI shapes live in
/// <see cref="OpenAiWire"/>: the protocol's vocabulary and its reader belong together.
/// </summary>
internal static class AnthropicWire
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // ------------------------------------------------------------- request

    public sealed class MessagesRequest
    {
        public string Model { get; set; } = string.Empty;

        public IReadOnlyList<RequestMessage> Messages { get; set; } = [];

        /// <summary>The system prompt. Anthropic takes it here, not as a message.</summary>
        public string? System { get; set; }

        /// <summary>Required by Anthropic - there is no "provider default" omission here.</summary>
        public int MaxTokens { get; set; }

        public double? Temperature { get; set; }

        public double? TopP { get; set; }

        public bool Stream { get; set; }

        public IReadOnlyList<ToolSpec>? Tools { get; set; }

        public ToolChoiceSpec? ToolChoice { get; set; }
    }

    public sealed class RequestMessage
    {
        /// <summary>Anthropic accepts only <c>user</c> and <c>assistant</c> here.</summary>
        public string Role { get; set; } = "user";

        /// <summary>
        /// The turn's content, in either form the API accepts: a JSON string for text-only
        /// turns, an array of blocks when the turn carries tool calls or results. One wire
        /// field, two shapes - which is why it is a node rather than two typed properties.
        /// </summary>
        public JsonNode? Content { get; set; }
    }

    public sealed class ContentBlock
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "text";

        /// <summary>The text of a <c>text</c> block.</summary>
        public string? Text { get; set; }

        /// <summary>The result text of a <c>tool_result</c> block, which Anthropic names "content".</summary>
        [JsonPropertyName("content")]
        public string? ResultText { get; set; }

        public string? Id { get; set; }

        public string? Name { get; set; }

        public JsonObject? Input { get; set; }

        [JsonPropertyName("tool_use_id")]
        public string? ToolUseId { get; set; }
    }

    public sealed class ToolSpec
    {
        public string Name { get; set; } = string.Empty;

        public string? Description { get; set; }

        [JsonPropertyName("input_schema")]
        public JsonNode? InputSchema { get; set; }
    }

    /// <summary>
    /// How hard to push tool use. Anthropic's vocabulary: <c>auto</c> lets the model
    /// decide, <c>any</c> forces a call - which is what the app's "required" means.
    /// </summary>
    public sealed class ToolChoiceSpec
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "auto";
    }

    // ------------------------------------------------------------- response

    public sealed class ResponseError
    {
        public string? Type { get; set; }

        public string? Message { get; set; }
    }

    public sealed class UsageInfo
    {
        public int? InputTokens { get; set; }

        public int? OutputTokens { get; set; }
    }

    // ------------------------------------------------------------- stream events

    public sealed class StreamEvent
    {
        public string? Type { get; set; }

        public MessageStartInfo? Message { get; set; }

        public DeltaInfo? Delta { get; set; }

        public ContentBlockStart? ContentBlock { get; set; }

        public UsageInfo? Usage { get; set; }

        public ResponseError? Error { get; set; }
    }

    public sealed class MessageStartInfo
    {
        public UsageInfo? Usage { get; set; }
    }

    public sealed class DeltaInfo
    {
        public string? Type { get; set; }

        public string? Text { get; set; }

        public string? Thinking { get; set; }

        /// <summary>A fragment of a tool call's input JSON, streamed as it is produced.</summary>
        public string? PartialJson { get; set; }

        public string? StopReason { get; set; }
    }

    public sealed class ContentBlockStart
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "text";

        public string? Id { get; set; }

        public string? Name { get; set; }
    }
}

/// <summary>
/// Anthropic - the native Messages API, which is not an OpenAI-compatible endpoint and
/// therefore not an <c>OpenAiCompatibleProvider</c> subclass.
/// </summary>
/// <remarks>
/// <para>
/// Where the wire differs from OpenAI, the request builder and the stream reader absorb
/// it, and the app above <see cref="IAIProvider"/> sees the same events it sees from any
/// other backend: the system prompt extracted to the top level, <c>max_tokens</c>
/// required and defaulted, tool calls as <c>tool_use</c>/<c>tool_result</c> content
/// blocks, and an SSE grammar of named events rather than one chunk shape.
/// </para>
/// <para>
/// The API key rides in an <c>x-api-key</c> header read per request from secure storage,
/// the same discipline the OpenAI-compatible base class follows.
/// </para>
/// </remarks>
public sealed class AnthropicProvider : IAIProvider
{
    public const string ProviderId = "anthropic";

    public const string ApiKeyUrl = "https://console.anthropic.com/settings/keys";

    /// <summary>
    /// Anthropic requires max_tokens on every call. When neither the model nor the user
    /// supplied one, this is the ceiling sent - enough for long answers without pushing
    /// small models over their own limits.
    /// </summary>
    private const int DefaultMaxTokens = 8_192;

    /// <summary>Response body kept for diagnostics on failure, bounded as the OpenAI path is.</summary>
    private const int MaxErrorBodyLength = 4096;

    private const string ApiVersion = "2023-06-01";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISecureStorage _secureStorage;
    private readonly ILogger<AnthropicProvider> _logger;

    public AnthropicProvider(
        IHttpClientFactory httpClientFactory,
        ISecureStorage secureStorage,
        ILogger<AnthropicProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _secureStorage = secureStorage;
        _logger = logger;
    }

    public string Id => ProviderId;

    public string DisplayName => "Anthropic";

    private static string BaseUrl => "https://api.anthropic.com/v1";

    public async Task<IReadOnlyList<AIModelDescriptor>> GetModelsAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/models");
        await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);

        var client = _httpClientFactory.CreateClient(ProviderId);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw await CreateErrorAsync(response, cancellationToken).ConfigureAwait(false);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var models = new List<AIModelDescriptor>();

            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array)
            {
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
                        Name = ReadString(item, "display_name") ?? modelId,
                        // The catalogue endpoint publishes no context field; the documented
                        // window for every current chat model is 200k.
                        ContextWindow = 200_000,
                        SupportsStreaming = true,
                        SupportsImages = true,
                        SupportsTools = true,
                        SupportedParameters = ["temperature", "top_p", "max_tokens"],
                        RawMetadataJson = item.GetRawText(),
                    });
                }
            }

            models.Sort(static (x, y) => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));

            _logger.LogInformation("Fetched {Count} model(s) from {Provider}.", models.Count, DisplayName);
            return models;
        }
        catch (JsonException ex)
        {
            throw new AIProviderException(
                AIErrorKind.Unknown,
                $"{DisplayName} returned a model list that could not be read.",
                $"{ex.GetType().Name}: {ex.Message}",
                Id,
                ex);
        }
        catch (Exception ex) when (ex is not AIProviderException)
        {
            throw ProviderErrorMapper.FromException(ex, DisplayName, Id);
        }
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamChatAsync(
        AIChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = BuildPayload(request);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/messages")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, AnthropicWire.SerializerOptions),
                Encoding.UTF8,
                "application/json"),
        };

        await AuthorizeAsync(httpRequest, cancellationToken).ConfigureAwait(false);

        var client = _httpClientFactory.CreateClient(ProviderId);

        using var response = await client
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var failure = await CreateErrorAsync(response, cancellationToken).ConfigureAwait(false);
            yield return new AIStreamEvent.Error(failure.Kind, failure.UserMessage, failure.TechnicalDetails);
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var sawUsage = false;
        var toolAccumulator = new List<ToolCallAccumulator>();
        var stopReason = (string?)null;

        await foreach (var data in ServerSentEventReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (data.Length == 0)
            {
                continue;
            }

            AnthropicWire.StreamEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<AnthropicWire.StreamEvent>(data, AnthropicWire.SerializerOptions);
            }
            catch (JsonException ex)
            {
                // One malformed frame must not abort a stream that is otherwise fine.
                _logger.LogDebug(ex, "Skipped an unparseable {Provider} stream frame.", DisplayName);
                continue;
            }

            if (evt is null)
            {
                continue;
            }

            // An in-band failure inside a 200 response, same shape as the OpenAI gateways.
            if (evt.Error is { } error)
            {
                yield return new AIStreamEvent.Error(
                    AIErrorKind.ServerError,
                    string.IsNullOrWhiteSpace(error.Message)
                        ? $"{DisplayName} reported an error while generating."
                        : error.Message,
                    $"type={error.Type}");
                yield break;
            }

            switch (evt.Type)
            {
                case "message_start":
                    if (evt.Message?.Usage is { } startUsage)
                    {
                        sawUsage = true;
                        yield return new AIStreamEvent.Usage(startUsage.InputTokens, null);
                    }

                    break;

                case "content_block_start":
                    if (evt.ContentBlock is { Type: "tool_use", Id: { } toolId, Name: { } toolName })
                    {
                        toolAccumulator.Add(new ToolCallAccumulator(toolId, toolName));
                    }

                    break;

                case "content_block_delta":
                    switch (evt.Delta?.Type)
                    {
                        case "text_delta" when evt.Delta.Text is { Length: > 0 } text:
                            yield return new AIStreamEvent.ContentDelta(text);
                            break;

                        case "thinking_delta" when evt.Delta.Thinking is { Length: > 0 } thinking:
                            yield return new AIStreamEvent.ReasoningDelta(thinking);
                            break;

                        case "input_json_delta" when evt.Delta.PartialJson is { Length: > 0 } fragment:
                            if (toolAccumulator.Count == 0)
                            {
                                // A delta for a block that never started: the frame is
                                // dropped, not crashed on - the call is already broken.
                                continue;
                            }

                            toolAccumulator[^1].Arguments.Append(fragment);
                            break;
                    }

                    break;

                case "message_delta":
                    if (evt.Delta?.StopReason is { Length: > 0 } reason)
                    {
                        stopReason = reason;
                    }

                    if (evt.Usage?.OutputTokens is { } output)
                    {
                        sawUsage = true;
                        yield return new AIStreamEvent.Usage(null, output);
                    }

                    break;
            }
        }

        if (toolAccumulator.Count > 0)
        {
            var calls = new List<AIToolCall>(toolAccumulator.Count);

            foreach (var entry in toolAccumulator)
            {
                calls.Add(new AIToolCall(entry.Id, entry.Name, entry.Arguments.ToString()));
            }

            yield return new AIStreamEvent.ToolCalls(calls);
        }

        if (!sawUsage)
        {
            _logger.LogDebug("{Provider} did not report token usage for this response.", DisplayName);
        }

        yield return new AIStreamEvent.Completed(MapFinishReason(stopReason));
    }

    /// <summary>
    /// Anthropic's stop vocabulary mapped onto the app's. The agent loop keys off the
    /// calls themselves, so the reason is presentation - but "end_turn" showing up raw in
    /// a saved transcript would read as a bug to anyone diffing providers.
    /// </summary>
    private static string? MapFinishReason(string? stopReason) => stopReason switch
    {
        "end_turn" or "stop_sequence" => "stop",
        "max_tokens" => "length",
        "tool_use" => "tool_calls",
        "refusal" => "content_filter",
        _ => stopReason,
    };

    public async Task<ProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var models = await GetModelsAsync(cancellationToken).ConfigureAwait(false);

            return new ProviderTestResult(
                Success: true,
                Message: $"Connected. {models.Count} model{(models.Count == 1 ? string.Empty : "s")} available.",
                ModelCount: models.Count);
        }
        catch (AIProviderException ex)
        {
            return new ProviderTestResult(false, ex.UserMessage, null, ex.TechnicalDetails);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var mapped = ProviderErrorMapper.FromException(ex, DisplayName, Id);
            return new ProviderTestResult(false, mapped.UserMessage, null, mapped.TechnicalDetails);
        }
    }

    // ------------------------------------------------------------- request build

    /// <summary>
    /// Projects the app's transcript onto the Messages API shape. The OpenAI-compatible
    /// base class has the mirror of this method; the divergences are the system prompt,
    /// the mandatory max_tokens and the tool-call message pairing.
    /// </summary>
    private AnthropicWire.MessagesRequest BuildPayload(AIChatRequest request)
    {
        var system = new StringBuilder();
        var messages = new List<AnthropicWire.RequestMessage>();
        var tools = request.Tools.Count == 0 ? null : request.Tools.Select(ToWire).ToList();

        foreach (var message in request.Messages)
        {
            switch (message.Role)
            {
                case "system":
                    if (system.Length > 0)
                    {
                        system.Append("\n\n");
                    }

                    system.Append(message.Content);
                    break;

                case "assistant" when message.ToolCalls.Count > 0:
                    messages.Add(AssistantWithToolCalls(message));
                    break;

                case "tool":
                    AddToolResult(messages, message);
                    break;

                case "user":
                case "assistant":
                    messages.Add(new AnthropicWire.RequestMessage
                    {
                        Role = message.Role,
                        Content = message.Content.Length == 0 ? null : JsonValue.Create(message.Content),
                    });
                    break;
            }
        }

        return new AnthropicWire.MessagesRequest
        {
            Model = request.ModelId,
            System = system.Length == 0 ? null : system.ToString(),
            Messages = messages,
            MaxTokens = request.MaxTokens is { } limit && limit > 0 ? limit : DefaultMaxTokens,
            Temperature = request.Temperature,
            TopP = request.TopP,
            Stream = request.Stream,
            Tools = tools is { Count: > 0 } ? tools : null,
            ToolChoice = tools is { Count: > 0 }
                ? request.ToolChoice == AIToolChoice.Required
                    ? new AnthropicWire.ToolChoiceSpec { Type = "any" }
                    : null // auto is the API default; omitting it is accepted everywhere.
                : null,
        };
    }

    private static AnthropicWire.RequestMessage AssistantWithToolCalls(AIChatMessage message)
    {
        var blocks = new List<AnthropicWire.ContentBlock>();

        if (message.Content.Length > 0)
        {
            blocks.Add(new AnthropicWire.ContentBlock { Type = "text", Text = message.Content });
        }

        foreach (var call in message.ToolCalls)
        {
            blocks.Add(new AnthropicWire.ContentBlock
            {
                Type = "tool_use",
                Id = call.Id,
                Name = call.Name,
                Input = ParseToolInput(call.ArgumentsJson),
            });
        }

        return new AnthropicWire.RequestMessage
        {
            Role = "assistant",
            Content = JsonSerializer.SerializeToNode(blocks, AnthropicWire.SerializerOptions),
        };
    }

    /// <summary>
    /// Anthropic has no <c>tool</c> role: a tool's answer is a <c>tool_result</c> block in
    /// the following user turn. Consecutive tool results fold into one turn, which is what
    /// the API requires and what the agent loop produces.
    /// </summary>
    private static void AddToolResult(List<AnthropicWire.RequestMessage> messages, AIChatMessage message)
    {
        var last = messages.Count > 0 ? messages[^1] : null;

        if (last is not { Role: "user", Content: JsonArray results })
        {
            last = new AnthropicWire.RequestMessage { Role = "user", Content = new JsonArray() };
            messages.Add(last);
            results = (JsonArray)last.Content!;
        }

        results.Add(JsonSerializer.SerializeToNode(new AnthropicWire.ContentBlock
        {
            Type = "tool_result",
            ToolUseId = message.ToolCallId,
            ResultText = message.Content,
        }, AnthropicWire.SerializerOptions));
    }

    private static AnthropicWire.ToolSpec ToWire(AIToolDefinition tool)
    {
        JsonNode? schema;

        try
        {
            schema = JsonNode.Parse(tool.ParametersJsonSchema);
        }
        catch (JsonException ex)
        {
            throw new AIProviderException(
                AIErrorKind.InvalidRequest,
                $"The '{tool.Name}' tool has an invalid parameter schema and cannot be offered to the model.",
                $"{ex.GetType().Name}: {ex.Message}",
                ProviderId,
                ex);
        }

        return new AnthropicWire.ToolSpec
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = schema,
        };
    }

    /// <summary>
    /// The tool arguments arrive as text and must go out as an object. Malformed arguments
    /// become an empty object rather than a dead request - the model corrects course on
    /// the tool result it gets back, which is the same recovery path the OpenAI loop uses.
    /// </summary>
    private static JsonObject ParseToolInput(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(argumentsJson) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // ------------------------------------------------------------- transport

    private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var apiKey = await _secureStorage.GetAsync(Id, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new AIProviderException(
                AIErrorKind.NotConfigured,
                $"No API key is configured for {DisplayName}. Add one in Settings → Providers.",
                null,
                Id);
        }

        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
    }

    private async Task<AIProviderException> CreateErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string? body = null;

        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length > MaxErrorBodyLength)
            {
                body = body[..MaxErrorBodyLength];
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the error body from {Provider}.", DisplayName);
        }

        _logger.LogWarning(
            "{Provider} returned HTTP {StatusCode} for {Method} {Path}.",
            DisplayName,
            (int)response.StatusCode,
            response.RequestMessage?.Method,
            response.RequestMessage?.RequestUri?.AbsolutePath);

        return ProviderErrorMapper.FromHttpStatus(response.StatusCode, body, DisplayName, Id);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>One tool call being reassembled across <c>input_json_delta</c> frames.</summary>
    private sealed class ToolCallAccumulator(string id, string name)
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        public StringBuilder Arguments { get; } = new();
    }
}
