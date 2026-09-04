using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AIClient.Infrastructure.Providers.OpenAiCompatible;

/// <summary>
/// Wire contracts for the OpenAI <c>/chat/completions</c> shape, which OpenRouter,
/// NVIDIA NIM, Together, Groq, Ollama and LM Studio all speak.
/// </summary>
/// <remarks>
/// Every optional field is nullable and annotated to be omitted when null. That is not a
/// style choice: sending <c>"temperature": null</c>, or sending a parameter a model does
/// not accept, is a hard 400 on several of these backends.
/// </remarks>
internal static class OpenAiWire
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public sealed class ChatRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("messages")]
        public required IReadOnlyList<ChatMessage> Messages { get; init; }

        [JsonPropertyName("stream")]
        public bool Stream { get; init; }

        [JsonPropertyName("temperature")]
        public double? Temperature { get; init; }

        [JsonPropertyName("top_p")]
        public double? TopP { get; init; }

        [JsonPropertyName("max_tokens")]
        public int? MaxTokens { get; init; }

        /// <summary>
        /// Asks for a usage block in the final streamed chunk. Providers that do not know
        /// this field ignore it; those that do return token counts we would otherwise
        /// have to estimate.
        /// </summary>
        [JsonPropertyName("stream_options")]
        public StreamOptions? StreamOptions { get; init; }

        /// <summary>
        /// Tools the model may call. Null - not an empty array - when there are none, because
        /// <c>"tools": []</c> is a 400 on several backends that accept the field's absence.
        /// </summary>
        [JsonPropertyName("tools")]
        public IReadOnlyList<ToolSpec>? Tools { get; init; }

        /// <summary>
        /// <c>auto</c>, <c>none</c> or <c>required</c>. Omitted for auto, which is the default
        /// everywhere and the value least likely to be rejected by a partial implementation.
        /// </summary>
        [JsonPropertyName("tool_choice")]
        public string? ToolChoice { get; init; }
    }

    /// <summary>
    /// One advertised tool. The <c>type</c>/<c>function</c> nesting is redundant today -
    /// <c>function</c> is the only type any of these providers implements - but it is what the
    /// protocol specifies, and a flattened object is rejected.
    /// </summary>
    public sealed class ToolSpec
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = "function";

        [JsonPropertyName("function")]
        public required FunctionSpec Function { get; init; }
    }

    public sealed class FunctionSpec
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        /// <summary>
        /// The argument schema, verbatim. A <see cref="JsonNode"/> rather than a string so it
        /// is written as an object; serialising the string would send the schema as a quoted
        /// blob, which every provider rejects.
        /// </summary>
        [JsonPropertyName("parameters")]
        public required JsonNode Parameters { get; init; }
    }

    /// <summary>A completed tool call, as sent back inside an assistant message.</summary>
    public sealed class ToolCallSpec
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("type")]
        public string Type { get; init; } = "function";

        [JsonPropertyName("function")]
        public required FunctionCallSpec Function { get; init; }
    }

    public sealed class FunctionCallSpec
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        /// <summary>
        /// The arguments as a JSON <em>string</em>, double-encoded. That is the protocol: the
        /// model emits text and the field is typed as text, so an object here is a 400.
        /// </summary>
        [JsonPropertyName("arguments")]
        public required string Arguments { get; init; }
    }

    public sealed class StreamOptions
    {
        [JsonPropertyName("include_usage")]
        public bool IncludeUsage { get; init; } = true;
    }

    public sealed class ChatMessage
    {
        [JsonPropertyName("role")]
        public required string Role { get; init; }

        /// <summary>
        /// Nullable, and omitted when null, because an assistant turn that only calls tools has
        /// no text. Sending <c>""</c> instead would be accepted but would put an empty assistant
        /// message into the model's own view of the conversation.
        /// </summary>
        [JsonPropertyName("content")]
        public required string? Content { get; init; }

        /// <summary>Present only on an assistant message that decided to call something.</summary>
        [JsonPropertyName("tool_calls")]
        public IReadOnlyList<ToolCallSpec>? ToolCalls { get; init; }

        /// <summary>Present only on a <c>tool</c> message, naming the call being answered.</summary>
        [JsonPropertyName("tool_call_id")]
        public string? ToolCallId { get; init; }

        /// <summary>The tool that produced the content. Optional in the protocol, sent anyway.</summary>
        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }

    /// <summary>One streamed chunk. Also covers the non-streaming response, which differs only in field names.</summary>
    public sealed class ChatCompletionChunk
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("model")]
        public string? Model { get; init; }

        [JsonPropertyName("choices")]
        public List<Choice>? Choices { get; init; }

        [JsonPropertyName("usage")]
        public UsageInfo? Usage { get; init; }

        /// <summary>Some gateways return a 200 with an error object instead of an error status.</summary>
        [JsonPropertyName("error")]
        public ErrorInfo? Error { get; init; }
    }

    public sealed class Choice
    {
        [JsonPropertyName("index")]
        public int Index { get; init; }

        /// <summary>Present when streaming.</summary>
        [JsonPropertyName("delta")]
        public Delta? Delta { get; init; }

        /// <summary>Present when not streaming.</summary>
        [JsonPropertyName("message")]
        public Delta? Message { get; init; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; init; }
    }

    public sealed class Delta
    {
        [JsonPropertyName("role")]
        public string? Role { get; init; }

        [JsonPropertyName("content")]
        public string? Content { get; init; }

        /// <summary>
        /// Reasoning text from models that expose it. OpenRouter uses <c>reasoning</c>;
        /// NVIDIA's DeepSeek-R1 deployments use <c>reasoning_content</c>.
        /// </summary>
        [JsonPropertyName("reasoning")]
        public string? Reasoning { get; init; }

        [JsonPropertyName("reasoning_content")]
        public string? ReasoningContent { get; init; }

        /// <summary>
        /// Tool calls. Streamed as fragments keyed by <see cref="ToolCallChunk.Index"/>; complete
        /// in one go when the response is not streamed. The same class serves both, because the
        /// only difference is whether <c>function.arguments</c> arrives whole.
        /// </summary>
        [JsonPropertyName("tool_calls")]
        public List<ToolCallChunk>? ToolCalls { get; init; }
    }

    public sealed class ToolCallChunk
    {
        /// <summary>
        /// Position in the call array, and the key fragments are joined on. Defaults to 0 when
        /// absent, which is why the accumulator also watches the id: a provider that omits the
        /// index on a second parallel call would otherwise overwrite the first.
        /// </summary>
        [JsonPropertyName("index")]
        public int Index { get; init; }

        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("function")]
        public FunctionChunk? Function { get; init; }
    }

    public sealed class FunctionChunk
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        /// <summary>A slice of the argument JSON when streaming, the whole of it when not.</summary>
        [JsonPropertyName("arguments")]
        public string? Arguments { get; init; }
    }

    public sealed class UsageInfo
    {
        [JsonPropertyName("prompt_tokens")]
        public int? PromptTokens { get; init; }

        [JsonPropertyName("completion_tokens")]
        public int? CompletionTokens { get; init; }

        [JsonPropertyName("total_tokens")]
        public int? TotalTokens { get; init; }
    }

    public sealed class ErrorInfo
    {
        [JsonPropertyName("message")]
        public string? Message { get; init; }

        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("code")]
        public JsonElement Code { get; init; }
    }
}
