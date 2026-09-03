using System.Text.Json;
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

        [JsonPropertyName("content")]
        public required string Content { get; init; }
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
