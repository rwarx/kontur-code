using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIClient.Application.Services;
using AIClient.Domain.Enums;
using AIClient.Domain.Interfaces;
using AIClient.Domain.Models;
using AIClient.Infrastructure.Http;
using Microsoft.Extensions.Logging;

namespace AIClient.Infrastructure.Providers.OpenAiCompatible;

/// <summary>
/// Shared implementation for every backend that speaks the OpenAI
/// <c>/chat/completions</c> protocol.
/// </summary>
/// <remarks>
/// OpenRouter and NVIDIA differ in their base URL, their catalogue endpoint and a couple
/// of headers - not in how a chat turn works. Putting the streaming, cancellation and
/// error handling here means a new OpenAI-compatible backend (Ollama, LM Studio, Groq,
/// Together) is a subclass with a handful of overrides rather than a reimplementation.
///
/// Two behaviours in here are deliberate and load-bearing:
/// <list type="bullet">
/// <item><description>
/// <c>HttpCompletionOption.ResponseHeadersRead</c> on the streaming call. Without it
/// <c>HttpClient</c> buffers the entire response before returning, and "streaming" degrades
/// to a long wait followed by the whole answer at once.
/// </description></item>
/// <item><description>
/// The API key is read per request from <see cref="ISecureStorage"/> and attached to the
/// request message, never to <c>HttpClient.DefaultRequestHeaders</c>. A shared client with
/// a default Authorization header would leak one provider's key into another's request and
/// would not pick up a key the user just changed in Settings.
/// </description></item>
/// </list>
/// </remarks>
public abstract class OpenAiCompatibleProvider : IAIProvider
{
    /// <summary>Response body kept for diagnostics on failure. Enough to be useful, bounded so a HTML error page cannot flood a log.</summary>
    private const int MaxErrorBodyLength = 4096;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISecureStorage _secureStorage;

    protected OpenAiCompatibleProvider(
        IHttpClientFactory httpClientFactory,
        ISecureStorage secureStorage,
        ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _secureStorage = secureStorage;
        Logger = logger;
    }

    public abstract string Id { get; }
    public abstract string DisplayName { get; }

    /// <summary>Base URL including the version segment, without a trailing slash.</summary>
    protected abstract string BaseUrl { get; }

    /// <summary>Named <see cref="HttpClient"/> registration, which carries the timeout and retry policy.</summary>
    protected abstract string HttpClientName { get; }

    protected ILogger Logger { get; }

    /// <summary>Relative catalogue path. Overridden where a provider deviates from <c>models</c>.</summary>
    protected virtual string ModelsPath => "models";

    protected virtual string ChatCompletionsPath => "chat/completions";

    /// <summary>Adds provider-specific headers. The Authorization header is added by the base class.</summary>
    protected virtual void ConfigureRequest(HttpRequestMessage request)
    {
    }

    /// <summary>Projects a provider's catalogue JSON onto the shared descriptor shape.</summary>
    protected abstract IReadOnlyList<AIModelDescriptor> ParseModels(JsonDocument document);

    public async Task<IReadOnlyList<AIModelDescriptor>> GetModelsAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/{ModelsPath}");
        await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);

        var client = _httpClientFactory.CreateClient(HttpClientName);

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

            var models = ParseModels(document);

            Logger.LogInformation("Fetched {Count} model(s) from {Provider}.", models.Count, DisplayName);
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

        if (!request.Stream)
        {
            // A model that cannot stream still has to work. One request, one event sequence,
            // identical to the caller.
            await foreach (var evt in SendNonStreamingAsync(payload, cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }

            yield break;
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/{ChatCompletionsPath}")
        {
            Content = JsonContent.Create(payload, options: OpenAiWire.SerializerOptions),
        };

        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        await AuthorizeAsync(httpRequest, cancellationToken).ConfigureAwait(false);

        var client = _httpClientFactory.CreateClient(HttpClientName);

        // ResponseHeadersRead is what makes this actually stream: the call returns as soon
        // as headers arrive instead of buffering the whole body first.
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

        var finishReason = (string?)null;
        var sawUsage = false;
        var toolCalls = new ToolCallAccumulator();

        await foreach (var data in ServerSentEventReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (data.Length == 0)
            {
                continue;
            }

            // The OpenAI protocol ends the stream with a literal sentinel rather than
            // simply closing the connection.
            if (data == "[DONE]")
            {
                break;
            }

            OpenAiWire.ChatCompletionChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<OpenAiWire.ChatCompletionChunk>(data, OpenAiWire.SerializerOptions);
            }
            catch (JsonException ex)
            {
                // One malformed frame must not abort a stream that is otherwise fine;
                // providers occasionally interleave keep-alive noise.
                Logger.LogDebug(ex, "Skipped an unparseable {Provider} stream frame.", DisplayName);
                continue;
            }

            if (chunk is null)
            {
                continue;
            }

            // Some gateways report failures inside a 200 response.
            if (chunk.Error is { } error)
            {
                var kind = ClassifyStreamError(error);
                yield return new AIStreamEvent.Error(
                    kind,
                    string.IsNullOrWhiteSpace(error.Message)
                        ? $"{DisplayName} reported an error while generating."
                        : error.Message,
                    $"type={error.Type}");
                yield break;
            }

            if (chunk.Usage is { } usage)
            {
                sawUsage = true;
                yield return new AIStreamEvent.Usage(usage.PromptTokens, usage.CompletionTokens);
            }

            var choice = chunk.Choices?.FirstOrDefault();
            if (choice is null)
            {
                continue;
            }

            if (choice.FinishReason is { Length: > 0 } reason)
            {
                finishReason = reason;
            }

            var delta = choice.Delta ?? choice.Message;
            if (delta is null)
            {
                continue;
            }

            var reasoning = delta.Reasoning ?? delta.ReasoningContent;
            if (!string.IsNullOrEmpty(reasoning))
            {
                yield return new AIStreamEvent.ReasoningDelta(reasoning);
            }

            if (!string.IsNullOrEmpty(delta.Content))
            {
                yield return new AIStreamEvent.ContentDelta(delta.Content);
            }

            if (delta.ToolCalls is { Count: > 0 } fragments)
            {
                foreach (var progress in toolCalls.Add(fragments))
                {
                    yield return progress;
                }
            }
        }

        // A content filter stop is not a transport failure, but the user needs to be told
        // why the answer is short rather than being left to guess.
        if (finishReason == "content_filter")
        {
            yield return new AIStreamEvent.Error(
                AIErrorKind.ContentFiltered,
                $"{DisplayName} stopped the response because it was flagged by a content filter.",
                "finish_reason=content_filter");
            yield break;
        }

        if (!sawUsage)
        {
            Logger.LogDebug("{Provider} did not report token usage for this response.", DisplayName);
        }

        if (toolCalls.HasCalls)
        {
            var calls = toolCalls.Build();

            LogToolCallDefects(toolCalls);

            if (calls.Count > 0)
            {
                yield return new AIStreamEvent.ToolCalls(calls);
            }
        }

        yield return new AIStreamEvent.Completed(finishReason);
    }

    /// <summary>Projects the provider-agnostic request onto the OpenAI payload.</summary>
    /// <remarks>
    /// Tool-related fields are omitted entirely when no tools were offered, rather than sent
    /// empty. A gateway that predates tool calling ignores fields it does not know but several
    /// reject <c>"tools": []</c> outright, so plain chat produces byte-for-byte the payload it
    /// produced before any of this existed.
    /// </remarks>
    private OpenAiWire.ChatRequest BuildPayload(AIChatRequest request) => new()
    {
        Model = request.ModelId,
        Messages = request.Messages.Select(ToWire).ToList(),
        Stream = request.Stream,
        Temperature = request.Temperature,
        TopP = request.TopP,
        MaxTokens = request.MaxTokens,
        StreamOptions = request.Stream ? new OpenAiWire.StreamOptions() : null,
        Tools = request.Tools.Count == 0 ? null : request.Tools.Select(ToWire).ToList(),
        ToolChoice = request.Tools.Count == 0 ? null : ToWire(request.ToolChoice),
    };

    private static OpenAiWire.ChatMessage ToWire(AIChatMessage message) => new()
    {
        Role = message.Role,
        // An assistant turn that only calls tools has no text, and the field is dropped rather
        // than sent as an empty string, which would show up in the model's own history.
        Content = message.Content.Length == 0 && message.ToolCalls.Count > 0 ? null : message.Content,
        ToolCalls = message.ToolCalls.Count == 0
            ? null
            : message.ToolCalls
                .Select(call => new OpenAiWire.ToolCallSpec
                {
                    Id = call.Id,
                    Function = new OpenAiWire.FunctionCallSpec
                    {
                        Name = call.Name,
                        Arguments = call.ArgumentsJson,
                    },
                })
                .ToList(),
        ToolCallId = message.ToolCallId,
        Name = message.Name,
    };

    private OpenAiWire.ToolSpec ToWire(AIToolDefinition tool)
    {
        JsonNode? parameters;

        try
        {
            parameters = JsonNode.Parse(tool.ParametersJsonSchema);
        }
        catch (JsonException ex)
        {
            // A tool's schema is a compile-time constant, so this is a defect rather than
            // anything a user did. Classified as an invalid request so it surfaces as a failed
            // turn naming the tool instead of an unhandled exception mid-stream.
            throw new AIProviderException(
                AIErrorKind.InvalidRequest,
                $"The '{tool.Name}' tool has an invalid parameter schema and cannot be offered to the model.",
                $"{ex.GetType().Name}: {ex.Message}",
                Id,
                ex);
        }

        if (parameters is null)
        {
            throw new AIProviderException(
                AIErrorKind.InvalidRequest,
                $"The '{tool.Name}' tool has an empty parameter schema and cannot be offered to the model.",
                "schema parsed to null",
                Id);
        }

        return new OpenAiWire.ToolSpec
        {
            Function = new OpenAiWire.FunctionSpec
            {
                Name = tool.Name,
                Description = tool.Description,
                Parameters = parameters,
            },
        };
    }

    private static string? ToWire(AIToolChoice choice) => choice switch
    {
        AIToolChoice.None => "none",
        AIToolChoice.Required => "required",
        // Auto is the default on every backend, and omitting the field is accepted by more of
        // them than sending the word is.
        _ => null,
    };

    /// <summary>Fallback path for models that do not support streaming.</summary>
    private async IAsyncEnumerable<AIStreamEvent> SendNonStreamingAsync(
        OpenAiWire.ChatRequest payload,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/{ChatCompletionsPath}")
        {
            Content = JsonContent.Create(payload, options: OpenAiWire.SerializerOptions),
        };

        await AuthorizeAsync(httpRequest, cancellationToken).ConfigureAwait(false);

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var failure = await CreateErrorAsync(response, cancellationToken).ConfigureAwait(false);
            yield return new AIStreamEvent.Error(failure.Kind, failure.UserMessage, failure.TechnicalDetails);
            yield break;
        }

        var body = await response.Content
            .ReadFromJsonAsync<OpenAiWire.ChatCompletionChunk>(OpenAiWire.SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        var choice = body?.Choices?.FirstOrDefault();
        var message = choice?.Message ?? choice?.Delta;
        var content = message?.Content;

        if (!string.IsNullOrEmpty(content))
        {
            yield return new AIStreamEvent.ContentDelta(content);
        }

        if (body?.Usage is { } usage)
        {
            yield return new AIStreamEvent.Usage(usage.PromptTokens, usage.CompletionTokens);
        }

        // Complete already, but folded through the same accumulator so the two paths cannot
        // disagree about ids, ordering or a call the provider left half-specified.
        if (message?.ToolCalls is { Count: > 0 } fragments)
        {
            var accumulator = new ToolCallAccumulator();
            accumulator.Add(fragments);

            var calls = accumulator.Build();

            LogToolCallDefects(accumulator);

            if (calls.Count > 0)
            {
                yield return new AIStreamEvent.ToolCalls(calls);
            }
        }

        yield return new AIStreamEvent.Completed(choice?.FinishReason);
    }

    /// <summary>
    /// Reports the two ways a provider can hand over a tool call that cannot be used. Logged at
    /// warning because both mean the model asked for something the app is about to ignore, and
    /// neither is visible anywhere else.
    /// </summary>
    private void LogToolCallDefects(ToolCallAccumulator accumulator)
    {
        if (accumulator.DiscardedCount > 0)
        {
            Logger.LogWarning(
                "{Provider} sent {Count} tool call(s) with no function name; they were ignored.",
                DisplayName,
                accumulator.DiscardedCount);
        }

        if (accumulator.TruncatedCount > 0)
        {
            Logger.LogWarning(
                "{Provider} sent {Count} tool call(s) whose arguments exceeded the size cap and were truncated.",
                DisplayName,
                accumulator.TruncatedCount);
        }
    }

    public async Task<ProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        // The catalogue endpoint is the cheapest authenticated call these APIs offer:
        // it validates the key without spending tokens.
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

    /// <summary>
    /// Attaches the bearer token for this request only.
    /// </summary>
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

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        ConfigureRequest(request);
    }

    /// <summary>Reads a failed response and turns it into a classified exception.</summary>
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
            // A body we cannot read is not worth failing over; the status code still classifies.
            Logger.LogDebug(ex, "Could not read the error body from {Provider}.", DisplayName);
        }

        // Logged without the body: provider error text can echo request content.
        Logger.LogWarning(
            "{Provider} returned HTTP {StatusCode} for {Method} {Path}.",
            DisplayName,
            (int)response.StatusCode,
            response.RequestMessage?.Method,
            response.RequestMessage?.RequestUri?.AbsolutePath);

        return ProviderErrorMapper.FromHttpStatus(response.StatusCode, body, DisplayName, Id);
    }

    /// <summary>Classifies an error object delivered inside a 200 response.</summary>
    private static AIErrorKind ClassifyStreamError(OpenAiWire.ErrorInfo error)
    {
        var text = $"{error.Type} {error.Message}";

        if (ProviderErrorMapper.LooksLikeContextOverflow(text))
        {
            return AIErrorKind.ContextLengthExceeded;
        }

        if (text.Contains("rate", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("limit", StringComparison.OrdinalIgnoreCase))
        {
            return AIErrorKind.RateLimited;
        }

        if (text.Contains("content_filter", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("content policy", StringComparison.OrdinalIgnoreCase))
        {
            return AIErrorKind.ContentFiltered;
        }

        return AIErrorKind.ServerError;
    }

    /// <summary>Reads an optional string property, tolerating a missing or null field.</summary>
    /// <remarks>
    /// Every reader here starts by checking the kind, because callers legitimately pass
    /// <c>default</c> for a nested object the catalogue entry omitted, and
    /// <c>TryGetProperty</c> on an undefined element throws rather than returning false.
    /// One sparse entry must not fail the whole catalogue.
    /// </remarks>
    protected static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Reads an optional integer, accepting the string form some catalogues use.</summary>
    protected static int? ReadInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>
    /// Reads a price, which OpenRouter returns as a decimal string in USD per token.
    /// </summary>
    protected static decimal? ReadPricePerMillion(JsonElement element, string propertyName)
    {
        var raw = element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value)
            ? value
            : default;

        var perToken = raw.ValueKind switch
        {
            JsonValueKind.String when decimal.TryParse(
                raw.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            JsonValueKind.Number when raw.TryGetDecimal(out var number) => number,
            _ => (decimal?)null,
        };

        // A price of exactly zero means "free", which is worth showing; null means unknown.
        return perToken is { } value2 ? value2 * 1_000_000m : null;
    }
}
