using System.Text.Json;
using AIClient.Domain.Enums;
using AIClient.Domain.Models;
using AIClient.Tests.Support;

namespace AIClient.Tests;

/// <summary>
/// The Anthropic Messages wire, driven through the real provider over a scripted socket.
/// </summary>
/// <remarks>
/// Anthropic is the one backend in the build that does not speak the OpenAI protocol, so
/// the things worth pinning are exactly the places the two diverge: the auth headers, the
/// system prompt extracted to the top level, the mandatory max_tokens, the tool_use and
/// tool_result block pairing, and a stream grammar of named events instead of chunks.
/// </remarks>
public sealed class AnthropicProviderTests
{
    [Fact]
    public async Task Requests_carry_the_key_in_the_anthropic_headers_not_a_bearer_token()
    {
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.AnthropicCatalogue);

        await ProviderHarness.Anthropic(handler).GetModelsAsync(Token);

        var request = handler.LastRequest;
        Assert.Equal("https://api.anthropic.com/v1/models", request.Uri.ToString());
        Assert.Equal(ProviderHarness.DummyKey, request.Header("x-api-key"));
        Assert.Equal("2023-06-01", request.Header("anthropic-version"));
        Assert.Null(request.Header("Authorization"));
    }

    [Fact]
    public async Task A_rejected_key_is_reported_as_a_configured_failure()
    {
        var handler = new FakeHttpMessageHandler().RespondError(
            System.Net.HttpStatusCode.Unauthorized,
            WireFixtures.AnthropicUnauthorizedBody);

        var result = await ProviderHarness.Anthropic(handler).TestConnectionAsync(Token);

        Assert.False(result.Success);
        Assert.NotNull(result.TechnicalDetails);
    }

    [Fact]
    public async Task The_system_prompt_is_extracted_and_max_tokens_is_always_present()
    {
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.AnthropicTextStream);
        var provider = ProviderHarness.Anthropic(handler);

        var request = new AIChatRequest
        {
            ModelId = "claude-sonnet-4-5",
            Messages =
            [
                AIChatMessage.System("You are helpful."),
                AIChatMessage.User("Hello."),
            ],
            // No max_tokens supplied: Anthropic requires one, so the default must appear.
            Stream = true,
        };

        _ = await ProviderHarness.CollectAsync(provider.StreamChatAsync(request, Token));

        var payload = JsonDocument.Parse(handler.LastRequest.Body!);
        var root = payload.RootElement;

        Assert.Equal("You are helpful.", root.GetProperty("system").GetString());
        Assert.True(root.TryGetProperty("max_tokens", out var maxTokens));
        Assert.Equal(8_192, maxTokens.GetInt32());

        // System messages never ride in the message list.
        var messages = root.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("Hello.", messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task A_configured_max_tokens_is_sent_rather_than_the_default()
    {
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.AnthropicTextStream);
        var provider = ProviderHarness.Anthropic(handler);

        var request = new AIChatRequest
        {
            ModelId = "claude-sonnet-4-5",
            Messages = [AIChatMessage.User("Hello.")],
            MaxTokens = 512,
            Stream = true,
        };

        _ = await ProviderHarness.CollectAsync(provider.StreamChatAsync(request, Token));

        var payload = JsonDocument.Parse(handler.LastRequest.Body!);
        Assert.Equal(512, payload.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task A_text_stream_arrives_as_deltas_with_usage_and_a_mapped_finish_reason()
    {
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.AnthropicTextStream);
        var provider = ProviderHarness.Anthropic(handler);

        var events = await ProviderHarness.CollectAsync(provider.StreamChatAsync(
            new AIChatRequest
            {
                ModelId = "claude-sonnet-4-5",
                Messages = [AIChatMessage.User("Hello.")],
                Stream = true,
            },
            Token));

        Assert.Equal("Hello there.", ProviderHarness.TextOf(events));
        Assert.Contains(events, e => e is AIStreamEvent.Usage { InputTokens: 25 });
        Assert.Contains(events, e => e is AIStreamEvent.Usage { OutputTokens: 9 });

        var completed = events.OfType<AIStreamEvent.Completed>().Single();

        // "end_turn" is Anthropic's word; the app knows "stop".
        Assert.Equal("stop", completed.FinishReason);
    }

    [Fact]
    public async Task Thinking_frames_arrive_as_reasoning_deltas_distinct_from_the_answer()
    {
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.AnthropicThinkingStream);
        var provider = ProviderHarness.Anthropic(handler);

        var events = await ProviderHarness.CollectAsync(provider.StreamChatAsync(
            new AIChatRequest
            {
                ModelId = "claude-sonnet-4-5",
                Messages = [AIChatMessage.User("Hello.")],
                Stream = true,
            },
            Token));

        Assert.Equal(["Let me think."], events.OfType<AIStreamEvent.ReasoningDelta>().Select(e => e.Text));
        Assert.Equal("The answer.", ProviderHarness.TextOf(events));
    }

    [Fact]
    public async Task Tool_use_blocks_reassemble_into_whole_calls_with_parsed_arguments()
    {
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.AnthropicToolUseStream);
        var provider = ProviderHarness.Anthropic(handler);

        var events = await ProviderHarness.CollectAsync(provider.StreamChatAsync(
            new AIChatRequest
            {
                ModelId = "claude-sonnet-4-5",
                Messages = [AIChatMessage.User("Read the app file.")],
                Stream = true,
            },
            Token));

        var calls = events.OfType<AIStreamEvent.ToolCalls>().Single().Calls;
        var call = Assert.Single(calls);

        Assert.Equal("toolu_01", call.Id);
        Assert.Equal("read_file", call.Name);

        // The fragments assemble into parseable JSON, the shape the tool runner needs.
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        Assert.Equal("src/App.cs", arguments.RootElement.GetProperty("path").GetString());

        Assert.Equal("tool_calls", events.OfType<AIStreamEvent.Completed>().Single().FinishReason);
    }

    [Fact]
    public async Task The_transcripts_tool_turns_become_tool_result_blocks_in_user_turns()
    {
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.AnthropicTextStream);
        var provider = ProviderHarness.Anthropic(handler);

        var request = new AIChatRequest
        {
            ModelId = "claude-sonnet-4-5",
            Messages =
            [
                AIChatMessage.User("List the files."),
                AIChatMessage.Assistant(string.Empty,
                [
                    new AIToolCall("toolu_a", "list_files", "{\"path\":\".\"}"),
                    new AIToolCall("toolu_b", "read_file", "{\"path\":\"a.cs\"}"),
                ]),
                AIChatMessage.Tool("toolu_a", "list_files", "one, two"),
                AIChatMessage.Tool("toolu_b", "read_file", "contents"),
            ],
            Stream = true,
        };

        _ = await ProviderHarness.CollectAsync(provider.StreamChatAsync(request, Token));

        var payload = JsonDocument.Parse(handler.LastRequest.Body!);
        var messages = payload.RootElement.GetProperty("messages");

        // user, assistant, and one user turn holding both results - not two.
        Assert.Equal(3, messages.GetArrayLength());

        var results = messages[2].GetProperty("content");
        Assert.Equal(2, results.GetArrayLength());
        Assert.Equal("tool_result", results[0].GetProperty("type").GetString());
        Assert.Equal("toolu_a", results[0].GetProperty("tool_use_id").GetString());
        Assert.Equal("one, two", results[0].GetProperty("content").GetString());
        Assert.Equal("toolu_b", results[1].GetProperty("tool_use_id").GetString());
    }

    [Fact]
    public async Task Offered_tools_are_sent_with_their_schemas_and_required_pushes_any()
    {
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.AnthropicTextStream);
        var provider = ProviderHarness.Anthropic(handler);

        var request = new AIChatRequest
        {
            ModelId = "claude-sonnet-4-5",
            Messages = [AIChatMessage.User("List the files.")],
            Tools =
            [
                new AIToolDefinition
                {
                    Name = "list_files",
                    Description = "Lists a directory.",
                    ParametersJsonSchema = """{"type":"object","properties":{"path":{"type":"string"}}}""",
                },
            ],
            ToolChoice = AIToolChoice.Required,
            Stream = true,
        };

        _ = await ProviderHarness.CollectAsync(provider.StreamChatAsync(request, Token));

        var payload = JsonDocument.Parse(handler.LastRequest.Body!);

        var tools = payload.RootElement.GetProperty("tools");
        Assert.Equal(1, tools.GetArrayLength());
        Assert.Equal("list_files", tools[0].GetProperty("name").GetString());
        Assert.True(tools[0].TryGetProperty("input_schema", out _), "the schema travels with the tool.");

        Assert.Equal("any", payload.RootElement.GetProperty("tool_choice").GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_catalogue_without_display_names_falls_back_to_the_id()
    {
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.AnthropicCatalogue);

        var models = await ProviderHarness.Anthropic(handler).GetModelsAsync(Token);

        // Three entries in, two out: the empty id could never be requested.
        Assert.Equal(2, models.Count);
        Assert.Equal("Claude Sonnet 4.5", models.Single(m => m.ModelId == "claude-sonnet-4-5").Name);
        Assert.All(models, m => Assert.Equal(200_000, m.ContextWindow));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
