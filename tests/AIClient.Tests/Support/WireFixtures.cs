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
    /// One tool call, streamed the way the protocol actually sends one: the id and the name in
    /// the opening frame with empty arguments, then the argument JSON a few characters at a time,
    /// then a frame carrying <c>finish_reason: tool_calls</c> and the usage block.
    /// </summary>
    public static readonly string ToolCallStream = Sse(
        """{"id":"gen-3","choices":[{"index":0,"delta":{"role":"assistant","content":null,"tool_calls":[{"index":0,"id":"call_read_1","type":"function","function":{"name":"read_file","arguments":""}}]}}]}""",
        """{"id":"gen-3","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"path\":"}}]}}]}""",
        """{"id":"gen-3","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"src/App"}}]}}]}""",
        """{"id":"gen-3","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":".xaml.cs\"}"}}]}}]}""",
        """{"id":"gen-3","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":120,"completion_tokens":18,"total_tokens":138}}""",
        "[DONE]");

    /// <summary>
    /// Two calls in one turn, opened in separate frames and with their arguments interleaved -
    /// which is what a model asking to read two files at once produces, and the case an
    /// accumulator keyed on anything but the index gets wrong.
    /// </summary>
    public static readonly string ParallelToolCallStream = Sse(
        """{"choices":[{"index":0,"delta":{"role":"assistant","content":null,"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"list_files","arguments":""}}]}}]}""",
        """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"call_b","type":"function","function":{"name":"search_files","arguments":""}}]}}]}""",
        """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"path\""}}]}}]}""",
        """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"function":{"arguments":"{\"query\""}}]}}]}""",
        """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":":\"src\"}"}}]}}]}""",
        """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"function":{"arguments":":\"TODO\"}"}}]}}]}""",
        """{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
        "[DONE]");

    /// <summary>
    /// Two calls from a backend that omits <c>index</c> altogether. Both deserialise to index 0,
    /// so only the differing ids say these are separate calls; joining on the index alone splices
    /// one name onto the other's arguments.
    /// </summary>
    public static readonly string UnindexedToolCallStream = Sse(
        """{"choices":[{"index":0,"delta":{"tool_calls":[{"id":"call_x","type":"function","function":{"name":"list_files","arguments":"{}"}}]}}]}""",
        """{"choices":[{"index":0,"delta":{"tool_calls":[{"id":"call_y","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"a.txt\"}"}}]}}]}""",
        """{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
        "[DONE]");

    /// <summary>
    /// A turn where the model both says something and calls a tool, and finishes with
    /// <c>stop</c> rather than <c>tool_calls</c>. Providers disagree here, which is why nothing
    /// downstream is allowed to decide on the finish reason alone.
    /// </summary>
    public static readonly string ToolCallWithTextStream = Sse(
        """{"choices":[{"index":0,"delta":{"role":"assistant","content":"Let me look at that file."}}]}""",
        """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_c","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"a.txt\"}"}}]}}]}""",
        """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
        "[DONE]");

    /// <summary>A call with an id but no function name, which cannot be dispatched to anything.</summary>
    public static readonly string AnonymousToolCallStream = Sse(
        """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_z","type":"function","function":{"arguments":"{}"}}]}}]}""",
        """{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
        "[DONE]");

    /// <summary>A tool call from a model that does not stream: the same shape, arriving whole.</summary>
    public const string NonStreamingToolCallCompletion = """
    {
      "id": "gen-4",
      "model": "test/model",
      "choices": [
        {
          "index": 0,
          "message": {
            "role": "assistant",
            "content": null,
            "tool_calls": [
              {
                "id": "call_whole",
                "type": "function",
                "function": { "name": "list_files", "arguments": "{\"path\":\".\"}" }
              }
            ]
          },
          "finish_reason": "tool_calls"
        }
      ],
      "usage": { "prompt_tokens": 90, "completion_tokens": 12, "total_tokens": 102 }
    }
    """;

    /// <summary>
    /// Splits a body into fixed-size pieces, so a stream test can force reads to end
    /// part-way through a JSON frame.
    /// </summary>
    public static IReadOnlyList<string> SplitEvery(string body, int size) =>
        [.. Enumerable.Range(0, (body.Length + size - 1) / size)
            .Select(i => body.Substring(i * size, Math.Min(size, body.Length - (i * size))))];

    /// <summary>
    /// Wraps one JSON document per SSE frame, with the blank line the protocol separates them
    /// with.
    /// </summary>
    /// <remarks>
    /// The tool-call fixtures below use this so each frame can be a raw string literal. Their
    /// payloads contain JSON-escaped quotes - the argument text is itself a JSON string - and
    /// written as ordinary literals they would need four levels of backslash to say
    /// <c>{"path":"a.cs"}</c>, which is how a fixture ends up testing the escaping rather than
    /// the parser. The frame separator stays an explicit <c>\n</c> rather than the file's own
    /// line endings, so a checkout that normalises them cannot change what is under test.
    /// </remarks>
    private static string Sse(params string[] frames) =>
        string.Concat(frames.Select(frame => $"data: {frame}\n\n"));

    // ==================================================================
    // Standard OpenAI-compatible catalogues (OpenAI, xAI, DeepSeek list ids alone;
    // Groq adds windows; Mistral adds capabilities).
    // ==================================================================

    /// <summary>The id-and-owner shape OpenAI, xAI and DeepSeek serve.</summary>
    public const string IdOnlyCatalogue = """
    {
      "object": "list",
      "data": [
        { "id": "gpt-4.1-mini", "object": "model", "owned_by": "system" },
        { "id": "gpt-4o", "object": "model", "owned_by": "system" },
        { "id": "some-unknown-model", "object": "model", "owned_by": "system" },
        { "id": "", "object": "model", "owned_by": "system" }
      ]
    }
    """;

    /// <summary>Groq's catalogue, which publishes per-model context windows.</summary>
    public const string GroqCatalogue = """
    {
      "object": "list",
      "data": [
        {
          "id": "llama-3.3-70b-versatile",
          "object": "model",
          "context_window": 131072,
          "max_completion_tokens": 32768
        },
        {
          "id": "meta-llama/llama-4-scout-17b-16e-instruct",
          "object": "model",
          "context_window": 131072
        }
      ]
    }
    """;

    /// <summary>Mistral's catalogue, which carries a capabilities object per entry.</summary>
    public const string MistralCatalogue = """
    {
      "object": "list",
      "data": [
        {
          "id": "mistral-large-latest",
          "name": "Mistral Large",
          "max_context_length": 131072,
          "capabilities": { "completion_chat": true, "function_calling": true, "vision": false }
        },
        {
          "id": "pixtral-large-latest",
          "name": "Pixtral Large",
          "max_context_length": 131072,
          "capabilities": { "completion_chat": true, "function_calling": true, "vision": true }
        },
        {
          "id": "mistral-embed",
          "name": "Mistral Embed",
          "capabilities": { "completion_chat": false, "function_calling": false, "vision": false }
        }
      ]
    }
    """;

    // ==================================================================
    // Anthropic Messages API.
    // ==================================================================

    public const string AnthropicCatalogue = """
    {
      "data": [
        { "id": "claude-sonnet-4-5", "display_name": "Claude Sonnet 4.5", "type": "model" },
        { "id": "claude-haiku-4-5", "display_name": "Claude Haiku 4.5", "type": "model" },
        { "id": "", "display_name": "Broken", "type": "model" }
      ],
      "has_more": false
    }
    """;

    /// <summary>A plain text answer, streamed the named-event way.</summary>
    public static readonly string AnthropicTextStream = Sse(
        """{"type":"message_start","message":{"usage":{"input_tokens":25,"output_tokens":1}}}""",
        """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" there."}}""",
        """{"type":"content_block_stop","index":0}""",
        """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":9}}""",
        """{"type":"message_stop"}""");

    /// <summary>Thinking text interleaved with the answer, as extended-thinking models emit.</summary>
    public static readonly string AnthropicThinkingStream = Sse(
        """{"type":"message_start","message":{"usage":{"input_tokens":30,"output_tokens":1}}}""",
        """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Let me think."}}""",
        """{"type":"content_block_stop","index":0}""",
        """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}""",
        """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"The answer."}}""",
        """{"type":"content_block_stop","index":1}""",
        """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":20}}""",
        """{"type":"message_stop"}""");

    /// <summary>
    /// One tool call: the opening frame names it, the argument JSON arrives as
    /// <c>input_json_delta</c> fragments, the stop reason says <c>tool_use</c>.
    /// </summary>
    public static readonly string AnthropicToolUseStream = Sse(
        """{"type":"message_start","message":{"usage":{"input_tokens":110,"output_tokens":2}}}""",
        """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_01","name":"read_file"}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"path\":"}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"\"src/App.cs\"}"}}""",
        """{"type":"content_block_stop","index":0}""",
        """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":16}}""",
        """{"type":"message_stop"}""");

    /// <summary>The non-streaming Messages response shape, with a tool_use block.</summary>
    public const string AnthropicNonStreamingToolUse = """
    {
      "id": "msg_01",
      "type": "message",
      "role": "assistant",
      "content": [
        { "type": "text", "text": "Reading the file." },
        { "type": "tool_use", "id": "toolu_whole", "name": "list_files", "input": { "path": "." } }
      ],
      "stop_reason": "tool_use",
      "usage": { "input_tokens": 88, "output_tokens": 14 }
    }
    """;

    /// <summary>Anthropic's rejected-key body, which is not the OpenAI shape.</summary>
    public const string AnthropicUnauthorizedBody = """
    { "type": "error", "error": { "type": "authentication_error", "message": "invalid x-api-key" } }
    """;
}
