namespace AIClient.Tests.Support;

/// <summary>
/// Canned provider payloads, shaped like the real ones.
/// </summary>
/// <remarks>
/// Trimmed from actual responses: the field names, the string-encoded numbers, the two
/// different ways OpenRouter reports modalities and the entries that are missing optional
/// objects entirely are all as the live APIs send them. Sanitised catalogue data only - no
/// keys, no account identifiers, nothing that needs a credential to obtain.
/// </remarks>
public static class WireFixtures
{
    /// <summary>
    /// An OpenRouter catalogue covering the awkward cases: an entry with no id, an entry with
    /// no <c>pricing</c>/<c>top_provider</c>/<c>architecture</c> objects, a context window
    /// available only under <c>top_provider</c> and encoded as a string, a free model priced
    /// at exactly zero, and both the array and legacy-string forms of the modality field.
    /// </summary>
    public const string OpenRouterCatalogue = """
    {
      "data": [
        {
          "id": "openai/gpt-5-mini",
          "name": "OpenAI: GPT-5 Mini",
          "description": "A small, fast general-purpose model.",
          "context_length": 400000,
          "architecture": {
            "input_modalities": ["text", "image"],
            "output_modalities": ["text"],
            "tokenizer": "GPT"
          },
          "pricing": { "prompt": "0.00000025", "completion": "0.000002", "request": "0" },
          "top_provider": { "context_length": 400000, "max_completion_tokens": 128000, "is_moderated": true },
          "supported_parameters": ["max_tokens", "top_p", "tools", "tool_choice", "seed"]
        },
        {
          "id": "anthropic/claude-sonnet-4.5",
          "name": "Anthropic: Claude Sonnet 4.5",
          "description": "A general-purpose model with a large context window.",
          "context_length": 200000,
          "architecture": { "modality": "text+image->text", "tokenizer": "Claude" },
          "pricing": { "prompt": "0.000003", "completion": "0.000015" },
          "top_provider": { "context_length": 200000, "max_completion_tokens": 64000 },
          "supported_parameters": ["max_tokens", "temperature", "top_p", "stop"]
        },
        {
          "id": "deepseek/deepseek-r1:free",
          "name": "DeepSeek: R1 (free)",
          "architecture": { "input_modalities": ["text"], "output_modalities": ["text"] },
          "pricing": { "prompt": "0", "completion": "0" },
          "top_provider": { "context_length": "163840" },
          "supported_parameters": []
        },
        {
          "id": "",
          "name": "Malformed entry with no id"
        },
        {
          "id": "z-ai/glm-4.6",
          "name": "Z.AI: GLM 4.6"
        }
      ]
    }
    """;

    /// <summary>
    /// An NVIDIA catalogue. The endpoint really does return this little - ids and an owner,
    /// with no context window, price or capability flag anywhere.
    /// </summary>
    public const string NvidiaCatalogue = """
    {
      "object": "list",
      "data": [
        { "id": "meta/llama-3.1-70b-instruct", "object": "model", "created": 735790403, "owned_by": "meta" },
        { "id": "nvidia/vila", "object": "model", "created": 735790403, "owned_by": "nvidia" },
        { "id": "moonshotai/kimi-k2-instruct", "object": "model", "created": 735790403, "owned_by": "moonshotai" },
        { "id": "qwen/qwen2.5-coder-32b-instruct", "object": "model", "created": 735790403, "owned_by": "qwen" },
        { "id": "   ", "object": "model" }
      ]
    }
    """;

    /// <summary>
    /// A complete chat stream: a comment keep-alive, a role-only opening frame, two content
    /// frames, a final frame carrying both the finish reason and usage, then the sentinel.
    /// </summary>
    public const string ChatStream =
        ": OPENROUTER PROCESSING\n" +
        "\n" +
        "data: {\"id\":\"gen-1\",\"model\":\"test/model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}\n" +
        "\n" +
        "data: {\"id\":\"gen-1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hello\"}}]}\n" +
        "\n" +
        "data: {\"id\":\"gen-1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\", world\"}}]}\n" +
        "\n" +
        "data: {\"id\":\"gen-1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]," +
        "\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":3,\"total_tokens\":14}}\n" +
        "\n" +
        "data: [DONE]\n" +
        "\n";

    /// <summary>A stream carrying a reasoning trace before the answer, as thinking models send.</summary>
    public const string ReasoningStream =
        "data: {\"choices\":[{\"delta\":{\"reasoning\":\"Let me think.\"}}]}\n" +
        "\n" +
        "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\" Still thinking.\"}}]}\n" +
        "\n" +
        "data: {\"choices\":[{\"delta\":{\"content\":\"42\"}}]}\n" +
        "\n" +
        "data: [DONE]\n" +
        "\n";

    /// <summary>
    /// A gateway reporting a failure inside a 200 response, which is how OpenRouter surfaces
    /// an upstream error once the stream has already opened.
    /// </summary>
    public const string ErrorInStream =
        "data: {\"choices\":[{\"delta\":{\"content\":\"Partial\"}}]}\n" +
        "\n" +
        "data: {\"error\":{\"message\":\"Provider returned error\",\"code\":429,\"type\":\"rate_limit_exceeded\"}}\n" +
        "\n" +
        "data: [DONE]\n" +
        "\n";

    /// <summary>A stream that stops on a content filter rather than on <c>stop</c>.</summary>
    public const string ContentFilteredStream =
        "data: {\"choices\":[{\"delta\":{\"content\":\"I ca\"}}]}\n" +
        "\n" +
        "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"content_filter\"}]}\n" +
        "\n" +
        "data: [DONE]\n" +
        "\n";

    /// <summary>
    /// A stream with an unparseable frame in the middle. One bad frame must not abort a
    /// stream that is otherwise fine.
    /// </summary>
    public const string StreamWithGarbageFrame =
        "data: {\"choices\":[{\"delta\":{\"content\":\"one\"}}]}\n" +
        "\n" +
        "data: {this is not json\n" +
        "\n" +
        "data: {\"choices\":[{\"delta\":{\"content\":\"two\"}}]}\n" +
        "\n" +
        "data: [DONE]\n" +
        "\n";

    /// <summary>A non-streaming completion, for models that cannot stream.</summary>
    public const string NonStreamingCompletion = """
    {
      "id": "gen-2",
      "model": "test/model",
      "choices": [
        {
          "index": 0,
          "message": { "role": "assistant", "content": "The whole answer at once." },
          "finish_reason": "stop"
        }
      ],
      "usage": { "prompt_tokens": 7, "completion_tokens": 5, "total_tokens": 12 }
    }
    """;

    /// <summary>A rejected key, in the nested shape both providers use.</summary>
    public const string UnauthorizedBody = """
    { "error": { "message": "No auth credentials found", "code": 401 } }
    """;

    /// <summary>An over-long prompt, reported as a 400 rather than a dedicated status.</summary>
    public const string ContextOverflowBody = """
    { "error": { "message": "This model's maximum context length is 8192 tokens. However, your messages resulted in 9130 tokens.", "code": 400, "type": "invalid_request_error" } }
    """;

    /// <summary>
    /// Splits a body into fixed-size pieces, so a stream test can force reads to end
    /// part-way through a JSON frame.
    /// </summary>
    public static IReadOnlyList<string> SplitEvery(string body, int size) =>
        [.. Enumerable.Range(0, (body.Length + size - 1) / size)
            .Select(i => body.Substring(i * size, Math.Min(size, body.Length - (i * size))))];
}
