using System.Net;
using System.Text.Json;
using AIClient.Domain.Enums;
using AIClient.Domain.Interfaces;
using AIClient.Domain.Models;
using AIClient.Tests.Support;

namespace AIClient.Tests;

/// <summary>
/// Sections 6, 7 and 22: real streaming, and stopping it.
/// </summary>
/// <remarks>
/// The providers are the production classes with a scripted socket underneath, so what is
/// under test is the code that ships: the request it builds, the SSE it consumes, the events
/// it emits and what happens when the user presses Stop. No key is involved anywhere, which
/// is section 36's requirement for provider tests.
/// </remarks>
public sealed class ProviderStreamingTests
{
    [Fact]
    public async Task A_stream_arrives_as_deltas_then_usage_then_a_completion()
    {
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.ChatStream);

        var events = await StreamAsync(handler);

        Assert.Equal("Hello, world", ProviderHarness.TextOf(events));

        var usage = Assert.Single(events.OfType<AIStreamEvent.Usage>());
        Assert.Equal(11, usage.InputTokens);
        Assert.Equal(3, usage.OutputTokens);

        // The sequence has to end with exactly one terminal event, or the UI never leaves
        // its loading state.
        var completed = Assert.IsType<AIStreamEvent.Completed>(events[^1]);
        Assert.Equal("stop", completed.FinishReason);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(17)]
    [InlineData(512)]
    public async Task The_answer_is_the_same_however_the_bytes_were_chunked(int chunkSize)
    {
        // A provider flushing mid-frame is normal. Dropping a token because of it would be
        // invisible in a test that hands the reader one buffer.
        var handler = new FakeHttpMessageHandler()
            .RespondSse(WireFixtures.SplitEvery(WireFixtures.ChatStream, chunkSize));

        Assert.Equal("Hello, world", ProviderHarness.TextOf(await StreamAsync(handler)));
    }

    [Fact]
    public async Task An_opening_frame_carrying_only_a_role_produces_no_visible_token()
    {
        // Every OpenAI-compatible stream starts with {"role":"assistant","content":""}.
        // Emitting it would append an empty delta and, in the UI, a flicker of nothing.
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.ChatStream);

        var deltas = (await StreamAsync(handler)).OfType<AIStreamEvent.ContentDelta>().ToList();

        Assert.Equal(2, deltas.Count);
        Assert.DoesNotContain(string.Empty, deltas.Select(d => d.Text));
    }

    [Fact]
    public async Task Reasoning_is_reported_apart_from_the_answer()
    {
        // Two providers spell it two ways, and the UI has to be able to hide it without
        // hiding the answer.
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.ReasoningStream);

        var events = await StreamAsync(handler);

        Assert.Equal(
            "Let me think. Still thinking.",
            string.Concat(events.OfType<AIStreamEvent.ReasoningDelta>().Select(e => e.Text)));
        Assert.Equal("42", ProviderHarness.TextOf(events));
    }

    [Fact]
    public async Task A_failure_reported_inside_a_200_response_ends_the_stream_as_an_error()
    {
        // OpenRouter opens the stream, then reports an upstream failure in a frame. The
        // partial text stays - the user can see how far it got - but the turn is not
        // presented as finished.
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.ErrorInStream);

        var events = await StreamAsync(handler);

        Assert.Equal("Partial", ProviderHarness.TextOf(events));

        var error = Assert.IsType<AIStreamEvent.Error>(events[^1]);
        Assert.Equal(AIErrorKind.RateLimited, error.Kind);
        Assert.Equal("Provider returned error", error.Message);
        Assert.DoesNotContain(events, e => e is AIStreamEvent.Completed);
    }

    [Fact]
    public async Task A_content_filter_stop_is_explained_rather_than_passed_off_as_a_normal_finish()
    {
        // The answer is cut short and nothing failed, so the user is owed a reason. A plain
        // Completed here would look like the model simply had little to say.
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.ContentFilteredStream);

        var events = await StreamAsync(handler);

        Assert.Equal("I ca", ProviderHarness.TextOf(events));

        var error = Assert.IsType<AIStreamEvent.Error>(events[^1]);
        Assert.Equal(AIErrorKind.ContentFiltered, error.Kind);
        Assert.DoesNotContain(events, e => e is AIStreamEvent.Completed);
    }

    [Fact]
    public async Task One_unparseable_frame_does_not_cost_the_rest_of_the_stream()
    {
        // Gateways interleave keep-alive noise. Aborting on it would lose an answer that
        // was otherwise arriving fine.
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.StreamWithGarbageFrame);

        var events = await StreamAsync(handler);

        Assert.Equal("onetwo", ProviderHarness.TextOf(events));
        Assert.IsType<AIStreamEvent.Completed>(events[^1]);
    }

    [Fact]
    public async Task An_http_failure_becomes_an_error_event_rather_than_an_exception()
    {
        // Mid-chat the UI is enumerating a stream. An exception thrown out of the iterator
        // would have to be caught in three places; an Error event travels the same channel
        // as the text and renders as the error card.
        var handler = new FakeHttpMessageHandler()
            .RespondError(HttpStatusCode.Unauthorized, WireFixtures.UnauthorizedBody);

        var error = Assert.IsType<AIStreamEvent.Error>(Assert.Single(await StreamAsync(handler)));

        Assert.Equal(AIErrorKind.InvalidApiKey, error.Kind);

        // Section 26: the key does not come back out, not even in diagnostics.
        Assert.DoesNotContain(ProviderHarness.DummyKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            ProviderHarness.DummyKey, error.TechnicalDetails ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_that_cannot_stream_still_produces_the_same_event_sequence()
    {
        // Section 7: the caller consumes one shape. A non-streaming model arriving as a
        // different sequence would put a branch in the ViewModel for something the user
        // cannot even see.
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.NonStreamingCompletion);

        var events = await StreamAsync(handler, Request(stream: false));

        Assert.Equal("The whole answer at once.", ProviderHarness.TextOf(events));

        var usage = Assert.Single(events.OfType<AIStreamEvent.Usage>());
        Assert.Equal(7, usage.InputTokens);
        Assert.Equal(5, usage.OutputTokens);
        Assert.Equal("stop", Assert.IsType<AIStreamEvent.Completed>(events[^1]).FinishReason);
    }

    [Fact]
    public async Task A_non_streaming_request_says_so_and_asks_for_no_streaming_extras()
    {
        // stream_options only means anything alongside stream:true, and some backends reject
        // the combination rather than ignoring it.
        var handler = new FakeHttpMessageHandler().RespondJson(WireFixtures.NonStreamingCompletion);

        await StreamAsync(handler, Request(stream: false));

        var body = BodyOf(handler);
        Assert.False(body.GetProperty("stream").GetBoolean());
        Assert.False(body.TryGetProperty("stream_options", out _));

        // No SSE Accept header either: the response is one JSON document.
        Assert.Null(handler.LastRequest.Header("Accept"));
    }

    [Fact]
    public async Task The_request_body_is_the_documented_chat_completions_shape()
    {
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.ChatStream);

        await StreamAsync(handler);

        var body = BodyOf(handler);
        Assert.Equal("test/model", body.GetProperty("model").GetString());
        Assert.True(body.GetProperty("stream").GetBoolean());

        // Section 18: the whole conversation, in order, system prompt first. Sending only the
        // latest turn is the single most common way a chat client loses its memory.
        var messages = body.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["system", "user"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("Hello?", messages[1].GetProperty("content").GetString());

        // Asking for usage in the final chunk is what makes the token counts in the UI real
        // rather than estimated.
        Assert.True(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public async Task A_parameter_the_caller_left_unset_is_absent_rather_than_sent_as_null()
    {
        // Section 14: a parameter the model does not support must not be sent, and
        // "temperature": null is sending it. Several backends answer 400 to exactly that.
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.ChatStream);

        await StreamAsync(handler);

        var body = BodyOf(handler);
        Assert.False(body.TryGetProperty("temperature", out _));
        Assert.False(body.TryGetProperty("top_p", out _));
        Assert.False(body.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public async Task Parameters_that_are_set_travel_under_their_documented_names()
    {
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.ChatStream);

        await StreamAsync(handler, Request(temperature: 0.35, topP: 0.9, maxTokens: 2048));

        var body = BodyOf(handler);
        Assert.Equal(0.35, body.GetProperty("temperature").GetDouble());
        Assert.Equal(0.9, body.GetProperty("top_p").GetDouble());
        Assert.Equal(2048, body.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task The_stream_is_requested_as_an_event_stream_from_the_documented_endpoint()
    {
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.ChatStream);

        await StreamAsync(handler);

        var request = handler.LastRequest;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", request.Uri.ToString());
        Assert.Equal($"Bearer {ProviderHarness.DummyKey}", request.Header("Authorization"));

        // Section 28: an http:// endpoint would put both the key and the conversation on the
        // wire in clear text.
        Assert.Equal(Uri.UriSchemeHttps, request.Uri.Scheme);

        // Without this a gateway is free to answer with a buffered application/json body,
        // and the token-by-token requirement quietly stops being met.
        Assert.Equal("text/event-stream", request.Header("Accept"));
    }

    [Fact]
    public async Task Stopping_mid_stream_abandons_the_response_instead_of_draining_it()
    {
        // Section 22. Stop has to abort the HTTP read; the deltas already yielded stay on
        // screen, because the user asked to stop, not to undo.
        var handler = new FakeHttpMessageHandler()
            .RespondSse(WireFixtures.SplitEvery(WireFixtures.ChatStream, 24));
        using var cts = new CancellationTokenSource();
        var seen = new List<AIStreamEvent>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            var provider = ProviderHarness.OpenRouter(handler);

            await foreach (var evt in provider.StreamChatAsync(Request(), cts.Token))
            {
                seen.Add(evt);
                await cts.CancelAsync();
            }
        });

        // One delta arrived before Stop, and nothing was invented afterwards: no Completed
        // event, so the UI does not present a cancelled turn as a finished one.
        Assert.Equal("Hello", ProviderHarness.TextOf(seen));
        Assert.DoesNotContain(seen, e => e is AIStreamEvent.Completed);
    }

    [Fact]
    public async Task Without_a_stored_key_the_turn_fails_before_a_byte_is_sent()
    {
        // Unlike a transport failure this is a configuration problem, so it surfaces as an
        // exception that ChatService turns into a failed turn pointing at Settings - rather
        // than as a 401 that reads like a wrong key.
        var handler = new FakeHttpMessageHandler();

        var error = await Assert.ThrowsAsync<AIProviderException>(
            () => StreamAsync(handler, provider: ProviderHarness.OpenRouter(handler, new FakeSecureStorage())));

        Assert.Equal(AIErrorKind.NotConfigured, error.Kind);
        Assert.Contains("Settings", error.UserMessage, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_second_provider_streams_through_the_same_code_path()
    {
        // Section 9's payoff: NVIDIA is a base URL and two overrides, so the same fixture
        // has to come out identically. If it did not, the shared base class would be a
        // false economy.
        var handler = new FakeHttpMessageHandler().RespondSse(WireFixtures.ChatStream);

        var events = await StreamAsync(handler, provider: ProviderHarness.Nvidia(handler));

        Assert.Equal("Hello, world", ProviderHarness.TextOf(events));
        Assert.IsType<AIStreamEvent.Completed>(events[^1]);
        Assert.Equal(
            "https://integrate.api.nvidia.com/v1/chat/completions", handler.LastRequest.Uri.ToString());
    }

    /// <summary>A minimal two-message request; every sampling parameter is unset by default.</summary>
    private static AIChatRequest Request(
        double? temperature = null,
        double? topP = null,
        int? maxTokens = null,
        bool stream = true) =>
        new()
        {
            ModelId = "test/model",
            Messages = [AIChatMessage.System("Be terse."), AIChatMessage.User("Hello?")],
            Temperature = temperature,
            TopP = topP,
            MaxTokens = maxTokens,
            Stream = stream,
        };

    private static Task<List<AIStreamEvent>> StreamAsync(
        FakeHttpMessageHandler handler,
        AIChatRequest? request = null,
        IAIProvider? provider = null) =>
        ProviderHarness.CollectAsync(
            (provider ?? ProviderHarness.OpenRouter(handler))
                .StreamChatAsync(request ?? Request(), TestContext.Current.CancellationToken));

    /// <summary>The request body as the provider actually serialised it.</summary>
    /// <remarks>
    /// Cloned so the element outlives the document: asserting on the JSON the provider sent
    /// is the only way to cover section 14, where the absence of a field is the behaviour.
    /// </remarks>
    private static JsonElement BodyOf(FakeHttpMessageHandler handler) =>
        JsonDocument.Parse(handler.LastRequest.Body!).RootElement.Clone();
}
