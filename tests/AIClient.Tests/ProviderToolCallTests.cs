using System.Text.Json;
using AIClient.Domain.Enums;
using AIClient.Domain.Models;
using AIClient.Tests.Support;

namespace AIClient.Tests;

/// <summary>
/// Tool calling at the wire, which is where an agent either works or silently does the wrong
/// thing.
/// </summary>
/// <remarks>
/// <para>
/// Everything the agent loop does rests on two translations: offered tools have to reach the
/// provider in the shape it documents, and a tool call streamed back as fragments has to be
/// reassembled into exactly what the model asked for. Both are invisible from the UI - a
/// mis-joined argument string looks like the model asking to read the wrong file - so they are
/// asserted here against recorded frames rather than inferred from a working conversation.
/// </para>
/// <para>
/// No key and no network: the production providers over
/// <see cref="FakeHttpMessageHandler"/>, as everywhere else in the suite.
/// </para>
/// </remarks>
public sealed class ProviderToolCallTests
{
    [Fact]
    public async Task A_tool_call_split_across_frames_arrives_as_one_whole_call()
    {
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.ToolCallStream);

        var events = await StreamAsync(handler);

        var call = Assert.Single(Assert.Single(events.OfType<AIStreamEvent.ToolCalls>()).Calls);

        Assert.Equal("call_read_1", call.Id);
        Assert.Equal("read_file", call.Name);
        Assert.Equal("""{"path":"src/App.xaml.cs"}""", call.ArgumentsJson);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(29)]
    [InlineData(4096)]
    public async Task The_reassembled_call_is_the_same_however_the_bytes_were_chunked(int chunkSize)
    {
        // A tool call is the case where a frame straddling two reads is most costly: half a
        // JSON escape sequence dropped silently produces arguments that parse into a different
        // path, and the agent then edits a file the model never named.
        var handler = new FakeHttpMessageHandler()
            .RespondSse(WireFixtures.SplitEvery(WireFixtures.ToolCallStream, chunkSize));

        var call = Assert.Single(Assert.Single(
            (await StreamAsync(handler)).OfType<AIStreamEvent.ToolCalls>()).Calls);

        Assert.Equal("""{"path":"src/App.xaml.cs"}""", call.ArgumentsJson);
    }

    [Fact]
    public async Task The_whole_call_is_reported_once_and_immediately_before_the_completion()
    {
        // The agent loop acts on this event, so its position matters: after it the turn is over,
        // and a second copy would run every tool twice.
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.ToolCallStream);

        var events = await StreamAsync(handler);

        Assert.IsType<AIStreamEvent.ToolCalls>(events[^2]);
        Assert.IsType<AIStreamEvent.Completed>(events[^1]);
        Assert.Single(events.OfType<AIStreamEvent.ToolCalls>());
    }

    [Fact]
    public async Task Fragments_are_also_surfaced_as_they_arrive_so_the_ui_can_say_what_is_coming()
    {
        // The name is in the first fragment and the arguments take the rest of the turn. A UI
        // that waited for the whole call would show nothing at all during the slowest part.
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.ToolCallStream);

        var deltas = (await StreamAsync(handler)).OfType<AIStreamEvent.ToolCallDelta>().ToList();

        Assert.Equal(4, deltas.Count);
        Assert.Equal("read_file", deltas[0].Name);
        Assert.Equal("call_read_1", deltas[0].Id);
        Assert.All(deltas.Skip(1), delta => Assert.Null(delta.Name));
        Assert.All(deltas, delta => Assert.Equal(0, delta.Index));
    }

    [Fact]
    public async Task Two_calls_in_one_turn_keep_their_own_arguments()
    {
        // The interleaved case. Fragments for both calls arrive in the same turn, alternating,
        // and the only thing separating them is the index.
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.ParallelToolCallStream);

        var calls = Assert.Single((await StreamAsync(handler)).OfType<AIStreamEvent.ToolCalls>()).Calls;

        Assert.Equal(2, calls.Count);
        Assert.Equal(new AIToolCall("call_a", "list_files", """{"path":"src"}"""), calls[0]);
        Assert.Equal(new AIToolCall("call_b", "search_files", """{"query":"TODO"}"""), calls[1]);
    }

    [Fact]
    public async Task Two_calls_that_both_omit_the_index_are_still_two_calls()
    {
        // Both frames deserialise to index 0. Trusting the index alone merges them into one
        // call named "list_filesread_file" with two argument objects concatenated - which
        // dispatches to nothing and loses the turn.
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.UnindexedToolCallStream);

        var calls = Assert.Single((await StreamAsync(handler)).OfType<AIStreamEvent.ToolCalls>()).Calls;

        Assert.Equal(2, calls.Count);
        Assert.Equal(["list_files", "read_file"], calls.Select(c => c.Name));
        Assert.Equal(["call_x", "call_y"], calls.Select(c => c.Id));
    }

    [Fact]
    public async Task A_turn_that_both_answers_and_calls_produces_both()
    {
        // Some models narrate before acting, and some providers still report finish_reason
        // "stop" when they do. The text belongs on screen and the call still has to run.
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.ToolCallWithTextStream);

        var events = await StreamAsync(handler);

        Assert.Equal("Let me look at that file.", ProviderHarness.TextOf(events));
        Assert.Equal("read_file", Assert.Single(
            Assert.Single(events.OfType<AIStreamEvent.ToolCalls>()).Calls).Name);
        Assert.Equal("stop", Assert.IsType<AIStreamEvent.Completed>(events[^1]).FinishReason);
    }

    [Fact]
    public async Task A_call_with_no_function_name_is_dropped_rather_than_guessed_at()
    {
        // Nothing can be dispatched from an id alone, and picking a tool by any other signal
        // would run something the model did not ask for. The turn ends as an ordinary answer.
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.AnonymousToolCallStream);

        var events = await StreamAsync(handler);

        Assert.Empty(events.OfType<AIStreamEvent.ToolCalls>());
        Assert.IsType<AIStreamEvent.Completed>(events[^1]);
    }

    [Fact]
    public async Task A_model_that_cannot_stream_reports_its_call_the_same_way()
    {
        // Section 7 again, now for tool calls: the agent loop consumes one event sequence, so a
        // non-streaming model must not need a second code path in it.
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.NonStreamingToolCallCompletion);

        var events = await StreamAsync(handler, Request(stream: false));

        var call = Assert.Single(Assert.Single(events.OfType<AIStreamEvent.ToolCalls>()).Calls);
        Assert.Equal("call_whole", call.Id);
        Assert.Equal("list_files", call.Name);
        Assert.Equal("""{"path":"."}""", call.ArgumentsJson);
        Assert.Equal("tool_calls", Assert.IsType<AIStreamEvent.Completed>(events[^1]).FinishReason);
    }

    [Fact]
    public async Task Offered_tools_travel_in_the_documented_nesting_with_the_schema_as_an_object()
    {
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.ChatStream);

        await StreamAsync(handler, Request(tools: [ReadFile]));

        var tool = Assert.Single(BodyOf(handler).GetProperty("tools").EnumerateArray());
        Assert.Equal("function", tool.GetProperty("type").GetString());

        var function = tool.GetProperty("function");
        Assert.Equal("read_file", function.GetProperty("name").GetString());
        Assert.Equal("Reads one text file from the workspace.", function.GetProperty("description").GetString());

        // The schema has to arrive as an object. Serialised as the string it is carried in, it
        // would be a quoted blob and every provider answers 400.
        var parameters = function.GetProperty("parameters");
        Assert.Equal(JsonValueKind.Object, parameters.ValueKind);
        Assert.Equal("object", parameters.GetProperty("type").GetString());
        Assert.True(parameters.GetProperty("properties").TryGetProperty("path", out _));
    }

    [Fact]
    public async Task A_plain_chat_request_carries_no_tool_fields_at_all()
    {
        // The compatibility guarantee: adding tool calling must not change the payload of a
        // conversation that does not use it. An empty "tools": [] is a 400 on several backends,
        // so the fields are absent rather than empty.
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.ChatStream);

        await StreamAsync(handler);

        var body = BodyOf(handler);
        Assert.False(body.TryGetProperty("tools", out _));
        Assert.False(body.TryGetProperty("tool_choice", out _));
    }

    [Fact]
    public async Task Auto_is_expressed_by_leaving_tool_choice_out()
    {
        // Auto is the default everywhere, and omitting the field is accepted by more backends
        // than sending the word is.
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.ChatStream);

        await StreamAsync(handler, Request(tools: [ReadFile], choice: AIToolChoice.Auto));

        Assert.False(BodyOf(handler).TryGetProperty("tool_choice", out _));
    }

    [Theory]
    [InlineData(AIToolChoice.None, "none")]
    [InlineData(AIToolChoice.Required, "required")]
    public async Task A_forced_choice_is_sent_under_its_protocol_name(AIToolChoice choice, string expected)
    {
        // "none" is the one the agent loop uses: on the final step of a run the tools are still
        // advertised - removing them mid-conversation invalidates the transcript - but the model
        // has to answer with what it already has.
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.ChatStream);

        await StreamAsync(handler, Request(tools: [ReadFile], choice: choice));

        Assert.Equal(expected, BodyOf(handler).GetProperty("tool_choice").GetString());
    }

    [Fact]
    public async Task A_transcript_of_a_call_and_its_result_is_sent_back_in_the_documented_shape()
    {
        // This is what the second request of an agent step looks like, and it is unforgiving:
        // an assistant message whose tool_calls are not answered by a tool message with the
        // matching id is a 400 from every provider.
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.ChatStream);

        var request = Request() with
        {
            Messages =
            [
                AIChatMessage.User("What is in a.txt?"),
                AIChatMessage.Assistant(string.Empty, [new AIToolCall("call_1", "read_file", """{"path":"a.txt"}""")]),
                AIChatMessage.Tool("call_1", "read_file", "hello"),
            ],
        };

        await StreamAsync(handler, request);

        var messages = BodyOf(handler).GetProperty("messages").EnumerateArray().ToList();

        var assistant = messages[1];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());

        // No text, so no content field: an empty string here becomes an empty assistant turn in
        // the model's own view of the conversation.
        Assert.False(assistant.TryGetProperty("content", out _));

        var call = Assert.Single(assistant.GetProperty("tool_calls").EnumerateArray());
        Assert.Equal("call_1", call.GetProperty("id").GetString());
        Assert.Equal("function", call.GetProperty("type").GetString());
        Assert.Equal("read_file", call.GetProperty("function").GetProperty("name").GetString());

        // The arguments are a JSON string, not an object. Sending the object is a 400.
        var arguments = call.GetProperty("function").GetProperty("arguments");
        Assert.Equal(JsonValueKind.String, arguments.ValueKind);
        Assert.Equal("""{"path":"a.txt"}""", arguments.GetString());

        var result = messages[2];
        Assert.Equal("tool", result.GetProperty("role").GetString());
        Assert.Equal("call_1", result.GetProperty("tool_call_id").GetString());
        Assert.Equal("read_file", result.GetProperty("name").GetString());
        Assert.Equal("hello", result.GetProperty("content").GetString());
    }

    [Fact]
    public async Task An_assistant_message_that_said_something_before_acting_keeps_its_text()
    {
        // The narration is part of the answer the user reads, and dropping it from the replayed
        // transcript would make the model's own history disagree with the screen.
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.ChatStream);

        var request = Request() with
        {
            Messages =
            [
                AIChatMessage.User("Look at a.txt"),
                AIChatMessage.Assistant("Reading it now.", [new AIToolCall("call_1", "read_file", "{}")]),
                AIChatMessage.Tool("call_1", "read_file", "hello"),
            ],
        };

        await StreamAsync(handler, request);

        var messages = BodyOf(handler).GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal("Reading it now.", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task A_tool_whose_schema_is_not_json_fails_the_turn_naming_the_tool()
    {
        // A schema is a compile-time constant, so this is a defect rather than anything a user
        // did - but it must not escape as an unhandled exception out of an async iterator.
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.ChatStream);
        var broken = new AIToolDefinition
        {
            Name = "broken_tool",
            Description = "Has a schema that does not parse.",
            ParametersJsonSchema = "{ this is not json",
        };

        var error = await Assert.ThrowsAsync<AIProviderException>(
            () => StreamAsync(handler, Request(tools: [broken])));

        Assert.Equal(AIErrorKind.InvalidRequest, error.Kind);
        Assert.Contains("broken_tool", error.UserMessage, StringComparison.Ordinal);

        // Nothing was sent: a malformed payload is caught before the socket is opened.
        Assert.Empty(handler.Requests);
    }

    private static readonly AIToolDefinition ReadFile = new()
    {
        Name = "read_file",
        Description = "Reads one text file from the workspace.",
        ParametersJsonSchema = """
        {
          "type": "object",
          "properties": { "path": { "type": "string", "description": "Path relative to the workspace root." } },
          "required": ["path"]
        }
        """,
    };

    private static AIChatRequest Request(
        bool stream = true,
        IReadOnlyList<AIToolDefinition>? tools = null,
        AIToolChoice choice = AIToolChoice.Auto) =>
        new()
        {
            ModelId = "test/model",
            Messages = [AIChatMessage.System("Be terse."), AIChatMessage.User("Hello?")],
            Stream = stream,
            Tools = tools ?? [],
            ToolChoice = choice,
        };

    private static Task<List<AIStreamEvent>> StreamAsync(
        FakeHttpMessageHandler handler,
        AIChatRequest? request = null) =>
        ProviderHarness.CollectAsync(
            ProviderHarness.OpenRouter(handler)
                .StreamChatAsync(request ?? Request(), TestContext.Current.CancellationToken));

    private static JsonElement BodyOf(FakeHttpMessageHandler handler) =>
        JsonDocument.Parse(handler.LastRequest.Body!).RootElement.Clone();
}
