using System.Runtime.CompilerServices;
using AIClient.Application.Configuration;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Application.Services;
using AIClient.Domain.Enums;
using AIClient.Domain.Interfaces;
using AIClient.Domain.Models;
using AIClient.Infrastructure.Repositories;
using AIClient.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIClient.Tests;

/// <summary>
/// One chat turn end to end: what is persisted, in what order, what reaches the provider,
/// and what happens when the answer never arrives.
/// </summary>
/// <remarks>
/// The conversation store, the context builder and the title generator are the real ones over a
/// real SQLite file. Only the provider is scripted, because it is the single collaborator whose
/// behaviour a test needs to dictate - a stream that stops half way, a model that reports no
/// usage, an error arriving after usable text. Faking the persistence too would leave the
/// ordering guarantees documented on <see cref="ChatService"/> untested, and those are the whole
/// reason the class exists.
/// </remarks>
public sealed class ChatServiceTests : IAsyncLifetime
{
    private const string ProviderId = "test";
    private const string ModelId = "test/model";

    private TestDatabase _db = null!;
    private ConversationService _conversations = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _conversations = _db.Conversations();
    }

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task A_turn_arrives_as_the_question_then_a_placeholder_then_deltas_then_a_completion()
    {
        var conversationId = await NewUntitledChatAsync();
        var service = Service(ScriptedProvider.Streaming(ProviderId, "Data ", "binding ", "explained."));

        var events = await CollectAsync(
            service.SendMessageAsync(Ask(conversationId, "Explain WPF data binding"), Token));

        // The ViewModel appends bubbles in exactly this order. Any other order shows the answer
        // above the question, or streams tokens into a bubble that does not exist yet.
        Assert.Collection(
            events,
            e => Assert.Equal(MessageRole.User, Assert.IsType<ChatTurnEvent.UserMessageSaved>(e).Message.Role),
            e => Assert.Equal("WPF data binding", Assert.IsType<ChatTurnEvent.TitleGenerated>(e).Title),
            e => Assert.Equal(
                MessageRole.Assistant,
                Assert.IsType<ChatTurnEvent.AssistantMessageStarted>(e).Message.Role),
            e => Assert.Equal("Data ", Assert.IsType<ChatTurnEvent.ContentDelta>(e).Text),
            e => Assert.Equal("binding ", Assert.IsType<ChatTurnEvent.ContentDelta>(e).Text),
            e => Assert.Equal("explained.", Assert.IsType<ChatTurnEvent.ContentDelta>(e).Text),
            e => Assert.IsType<ChatTurnEvent.Completed>(e));
    }

    [Fact]
    public async Task The_question_and_an_empty_answer_are_committed_before_a_token_is_requested()
    {
        // Section 20: a crash, a kill or a power cut between here and the last token must still
        // leave a transcript that reads correctly on restart.
        var conversationId = await NewChatAsync();
        List<MessageDto> onOpen = [];

        var provider = new ScriptedProvider(ProviderId, (_, _) => Observe());
        await CollectAsync(Service(provider).SendMessageAsync(Ask(conversationId, "Ping"), Token));

        Assert.Collection(
            onOpen,
            m => Assert.Equal((MessageRole.User, "Ping", MessageStatus.Complete), (m.Role, m.Content, m.Status)),
            m => Assert.Equal(
                (MessageRole.Assistant, string.Empty, MessageStatus.Streaming),
                (m.Role, m.Content, m.Status)));

        // The placeholder records who was asked, so a transcript mixing models stays readable.
        Assert.Equal((ProviderId, ModelId), (onOpen[1].ProviderId, onOpen[1].ModelId));

        async IAsyncEnumerable<AIStreamEvent> Observe()
        {
            onOpen = [.. await MessagesAsync(conversationId)];
            yield return new AIStreamEvent.ContentDelta("Pong");
            yield return new AIStreamEvent.Completed("stop");
        }
    }

    [Fact]
    public async Task The_streamed_answer_is_committed_as_one_complete_message()
    {
        var conversationId = await NewChatAsync();
        var service = Service(ScriptedProvider.Streaming(ProviderId, "Hello", ", ", "world"));

        await CollectAsync(service.SendMessageAsync(Ask(conversationId), Token));

        var answer = (await MessagesAsync(conversationId))[^1];

        // Deltas are assembled once, by the service. A UI that re-sent its own accumulated text
        // would be the only record of the answer, and Stop or a crash would lose it.
        Assert.Equal("Hello, world", answer.Content);
        Assert.Equal(MessageStatus.Complete, answer.Status);
        Assert.Null(answer.ErrorMessage);
        Assert.NotNull(answer.GenerationTimeMs);
    }

    [Fact]
    public async Task Token_counts_reported_by_the_provider_are_kept_in_place_of_the_estimate()
    {
        var conversationId = await NewChatAsync();
        var service = Service(ScriptedProvider.Emitting(
            ProviderId,
            new AIStreamEvent.ContentDelta("Counted."),
            new AIStreamEvent.Usage(41, 7),
            new AIStreamEvent.Completed("stop")));

        var events = await CollectAsync(service.SendMessageAsync(Ask(conversationId), Token));

        var completed = Assert.IsType<ChatTurnEvent.Completed>(events[^1]);
        Assert.Equal((41, 7), (completed.InputTokens, completed.OutputTokens));

        // Persisted as well as reported: section 32's per-message counts have to survive a
        // restart, and re-estimating them later would produce different numbers.
        var answer = (await MessagesAsync(conversationId))[^1];
        Assert.Equal((41, 7), (answer.InputTokens, answer.OutputTokens));
    }

    [Fact]
    public async Task A_provider_that_reports_no_usage_falls_back_to_the_estimated_input_count()
    {
        // Most providers omit usage unless asked, and some omit it regardless. An estimate is
        // more useful than a blank, provided nothing pretends it came from the provider.
        var conversationId = await NewChatAsync();
        var service = Service(ScriptedProvider.Streaming(ProviderId, "No usage reported."));

        var events = await CollectAsync(
            service.SendMessageAsync(Ask(conversationId, "How many tokens is this question?"), Token));

        var completed = Assert.IsType<ChatTurnEvent.Completed>(events[^1]);
        Assert.True(completed.InputTokens > 0);
        Assert.Null(completed.OutputTokens);
    }

    [Fact]
    public async Task Auto_titling_replaces_New_Chat_before_the_answer_starts_arriving()
    {
        var conversationId = await NewUntitledChatAsync();
        var service = Service(new ScriptedProvider(ProviderId));

        var events = await CollectAsync(
            service.SendMessageAsync(Ask(conversationId, "Explain SQLite WAL mode"), Token));

        var titleIndex = events.FindIndex(e => e is ChatTurnEvent.TitleGenerated);
        Assert.Equal(
            "SQLite WAL mode",
            Assert.IsType<ChatTurnEvent.TitleGenerated>(events[titleIndex]).Title);

        // The event is not the only record of it - the sidebar reads the database.
        Assert.Equal("SQLite WAL mode", (await _conversations.GetAsync(conversationId, Token))!.Title);

        // Ahead of the answer, so the sidebar is not stuck on "New Chat" while the model thinks.
        Assert.True(titleIndex < events.FindIndex(e => e is ChatTurnEvent.AssistantMessageStarted));
    }

    [Fact]
    public async Task Auto_titling_can_be_turned_off_without_disturbing_the_turn()
    {
        var conversationId = await NewUntitledChatAsync();
        var settings = new StubSettingsService().With<GeneralSettings>(g => g.AutoGenerateTitles = false);

        var events = await CollectAsync(Service(new ScriptedProvider(ProviderId), settings: settings)
            .SendMessageAsync(Ask(conversationId, "Explain SQLite WAL mode"), Token));

        Assert.DoesNotContain(events, e => e is ChatTurnEvent.TitleGenerated);
        Assert.Equal("New Chat", (await _conversations.GetAsync(conversationId, Token))!.Title);
        Assert.IsType<ChatTurnEvent.Completed>(events[^1]);
    }

    [Fact]
    public async Task A_title_the_user_chose_is_never_overwritten_by_the_first_message()
    {
        var conversationId = (await _conversations.CreateAsync("Release checklist", cancellationToken: Token)).Id;

        var events = await CollectAsync(Service(new ScriptedProvider(ProviderId))
            .SendMessageAsync(Ask(conversationId, "Explain SQLite WAL mode"), Token));

        Assert.DoesNotContain(events, e => e is ChatTurnEvent.TitleGenerated);
        Assert.Equal("Release checklist", (await _conversations.GetAsync(conversationId, Token))!.Title);
    }

    [Fact]
    public async Task An_unconfigured_provider_fails_the_turn_and_points_at_Settings()
    {
        // What a user hits after disabling a provider a saved chat was still using: the stored
        // id no longer resolves. It has to read as a settings problem, not as a broken answer.
        var conversationId = await NewChatAsync();

        var events = await CollectAsync(Service().SendMessageAsync(Ask(conversationId), Token));

        Assert.Collection(
            events,
            e => Assert.IsType<ChatTurnEvent.UserMessageSaved>(e),
            e => Assert.IsType<ChatTurnEvent.AssistantMessageStarted>(e),
            e => Assert.Equal(AIErrorKind.NotConfigured, Assert.IsType<ChatTurnEvent.Failed>(e).Kind));

        var failed = Assert.IsType<ChatTurnEvent.Failed>(events[^1]);
        Assert.Contains("Settings", failed.UserMessage, StringComparison.Ordinal);
        Assert.False(failed.IsRetryable);

        // The placeholder does not stay on "Streaming" forever.
        var answer = (await MessagesAsync(conversationId))[^1];
        Assert.Equal((MessageStatus.Failed, AIErrorKind.NotConfigured), (answer.Status, answer.ErrorKind));
    }

    [Fact]
    public async Task A_failure_part_way_through_keeps_the_text_that_already_arrived()
    {
        // Half an answer is still worth reading, and discarding it would also discard the only
        // evidence of where the stream broke.
        var conversationId = await NewChatAsync();
        var service = Service(ScriptedProvider.Throwing(
            ProviderId, new HttpRequestException("connection reset"), "The first half"));

        var events = await CollectAsync(service.SendMessageAsync(Ask(conversationId), Token));

        Assert.Equal("The first half", Text(events));

        var failed = Assert.IsType<ChatTurnEvent.Failed>(events[^1]);
        Assert.Equal(AIErrorKind.NetworkError, failed.Kind);

        // Retryable, so the error card offers the button rather than only an apology.
        Assert.True(failed.IsRetryable);

        var answer = (await MessagesAsync(conversationId))[^1];
        Assert.Equal("The first half", answer.Content);
        Assert.Equal(MessageStatus.Failed, answer.Status);
        Assert.NotNull(answer.ErrorMessage);
    }

    [Fact]
    public async Task An_error_event_ends_the_turn_as_a_failure_even_when_a_completion_follows()
    {
        // Providers that report a failure inside a 200 response often send their usual final
        // chunk afterwards. Reporting that as a completion would show a truncated answer as
        // finished, with no sign anything went wrong.
        var conversationId = await NewChatAsync();
        var service = Service(ScriptedProvider.Emitting(
            ProviderId,
            new AIStreamEvent.ContentDelta("Partial"),
            new AIStreamEvent.Error(AIErrorKind.RateLimited, "Rate limit reached. Try again shortly.", "429"),
            new AIStreamEvent.Completed("stop")));

        var events = await CollectAsync(service.SendMessageAsync(Ask(conversationId), Token));

        Assert.Equal(AIErrorKind.RateLimited, Assert.IsType<ChatTurnEvent.Failed>(events[^1]).Kind);
        Assert.DoesNotContain(events, e => e is ChatTurnEvent.Completed);
        Assert.Equal("Partial", (await MessagesAsync(conversationId))[^1].Content);
    }

    [Fact]
    public async Task A_completion_carrying_no_text_is_reported_rather_than_shown_as_an_empty_bubble()
    {
        var conversationId = await NewChatAsync();
        var service = Service(ScriptedProvider.Emitting(ProviderId, new AIStreamEvent.Completed("stop")));

        var events = await CollectAsync(service.SendMessageAsync(Ask(conversationId), Token));

        var failed = Assert.IsType<ChatTurnEvent.Failed>(events[^1]);
        Assert.Contains("empty", failed.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(failed.IsRetryable);
        Assert.Equal(MessageStatus.Failed, (await MessagesAsync(conversationId))[^1].Status);
    }

    [Fact]
    public async Task Reasoning_traces_are_consumed_without_leaking_into_the_answer()
    {
        // Reasoning models emit these constantly. The MVP does not render them, but a provider
        // that sends them must still stream its answer correctly.
        var conversationId = await NewChatAsync();
        var service = Service(ScriptedProvider.Emitting(
            ProviderId,
            new AIStreamEvent.ReasoningDelta("The user wants a greeting."),
            new AIStreamEvent.ContentDelta("Hello."),
            new AIStreamEvent.Completed("stop")));

        var events = await CollectAsync(service.SendMessageAsync(Ask(conversationId), Token));

        Assert.Equal("Hello.", Text(events));
        Assert.Equal("Hello.", (await MessagesAsync(conversationId))[^1].Content);
    }

    [Fact]
    public async Task Stopping_a_turn_keeps_the_partial_answer_and_marks_it_cancelled()
    {
        // Section 22. Stop means "stop", not "undo": the text already on screen stays, and stays
        // usable as context for the next turn.
        var conversationId = await NewChatAsync();
        var service = Service(ScriptedProvider.Streaming(ProviderId, "Half an ", "answer"));
        using var cts = new CancellationTokenSource();
        var events = new List<ChatTurnEvent>();

        await foreach (var evt in service.SendMessageAsync(Ask(conversationId), cts.Token))
        {
            events.Add(evt);

            if (evt is ChatTurnEvent.ContentDelta)
            {
                await cts.CancelAsync();
            }
        }

        Assert.Equal("Half an ", Text(events));
        Assert.IsType<ChatTurnEvent.Cancelled>(events[^1]);

        // Cancelled rather than Failed, and with no error text: nothing went wrong.
        var answer = (await MessagesAsync(conversationId))[^1];
        Assert.Equal("Half an ", answer.Content);
        Assert.Equal(MessageStatus.Cancelled, answer.Status);
        Assert.Null(answer.ErrorMessage);
    }

    [Fact]
    public async Task Partial_text_reaches_the_database_while_the_answer_is_still_streaming()
    {
        // The periodic flush is what bounds the cost of a crash mid-answer. Genuinely slow, on
        // purpose: the interval is a wall-clock second, and injecting a clock would mean testing
        // a seam that exists only for the test instead of the behaviour that ships.
        var conversationId = await NewChatAsync();
        var service = Service(new ScriptedProvider(ProviderId, (_, ct) => SlowStream(ct)));
        var snapshots = new List<(string Content, MessageStatus Status)>();

        await foreach (var evt in service.SendMessageAsync(Ask(conversationId), Token))
        {
            if (evt is ChatTurnEvent.ContentDelta)
            {
                var streaming = (await MessagesAsync(conversationId))[^1];
                snapshots.Add((streaming.Content, streaming.Status));
            }
        }

        // Still marked Streaming at that point, so a transcript recovered after a crash shows
        // the answer as unfinished rather than as a complete but truncated one.
        Assert.Contains(("Saved while streaming", MessageStatus.Streaming), snapshots);

        var answer = (await MessagesAsync(conversationId))[^1];
        Assert.Equal("Saved while streaming, then the rest.", answer.Content);
        Assert.Equal(MessageStatus.Complete, answer.Status);
    }

    [Fact]
    public async Task A_parameter_the_model_does_not_list_is_left_out_of_the_request()
    {
        // Section 14. OpenRouter publishes supported_parameters per model, and several models
        // answer 400 to an unsupported field rather than ignoring it. Falling back to the
        // model's own default is the better outcome.
        var conversationId = await NewChatAsync();
        var provider = new ScriptedProvider(ProviderId);
        var registry = new StubProviderRegistry(provider)
            .WithModel(ProviderId, ModelId, supportedParameters: ["max_tokens"]);
        var settings = new StubSettingsService().With<ChatSettings>(c =>
        {
            c.Temperature = 0.7;
            c.TopP = 0.95;
            c.MaxTokens = 512;
        });

        await CollectAsync(Service(registry: registry, settings: settings)
            .SendMessageAsync(Ask(conversationId), Token));

        Assert.Null(provider.LastRequest.Temperature);
        Assert.Null(provider.LastRequest.TopP);
        Assert.Equal(512, provider.LastRequest.MaxTokens);
    }

    [Fact]
    public async Task A_model_whose_catalogue_entry_lists_nothing_still_receives_the_defaults()
    {
        // An empty supported_parameters means the catalogue was silent, not that the model
        // refuses everything. Dropping parameters there would quietly ignore the user's
        // Temperature setting for most of NVIDIA's models.
        var conversationId = await NewChatAsync();
        var provider = new ScriptedProvider(ProviderId);
        var registry = new StubProviderRegistry(provider).WithModel(ProviderId, ModelId);
        var settings = new StubSettingsService().With<ChatSettings>(c => c.Temperature = 0.25);

        await CollectAsync(Service(registry: registry, settings: settings)
            .SendMessageAsync(Ask(conversationId), Token));

        Assert.Equal(0.25, provider.LastRequest.Temperature);
    }

    [Theory]
    [InlineData(8192, 4096, 4096)]
    [InlineData(1024, 4096, 1024)]
    public async Task Max_tokens_never_exceeds_what_the_model_allows(int configured, int limit, int expected)
    {
        var conversationId = await NewChatAsync();
        var provider = new ScriptedProvider(ProviderId);
        var registry = new StubProviderRegistry(provider).WithModel(ProviderId, ModelId, maxOutputTokens: limit);
        var settings = new StubSettingsService().With<ChatSettings>(c => c.MaxTokens = configured);

        await CollectAsync(Service(registry: registry, settings: settings)
            .SendMessageAsync(Ask(conversationId), Token));

        Assert.Equal(expected, provider.LastRequest.MaxTokens);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task An_unset_or_zero_max_tokens_lets_the_model_use_its_own_default(int? configured)
    {
        var conversationId = await NewChatAsync();
        var provider = new ScriptedProvider(ProviderId);
        var settings = new StubSettingsService().With<ChatSettings>(c => c.MaxTokens = configured);

        await CollectAsync(Service(provider, settings: settings).SendMessageAsync(Ask(conversationId), Token));

        Assert.Null(provider.LastRequest.MaxTokens);
    }

    [Fact]
    public async Task A_model_that_cannot_stream_is_asked_for_a_single_response()
    {
        var conversationId = await NewChatAsync();
        var provider = new ScriptedProvider(ProviderId);
        var registry = new StubProviderRegistry(provider)
            .WithModel(ProviderId, ModelId, supportsStreaming: false);

        await CollectAsync(Service(registry: registry).SendMessageAsync(Ask(conversationId), Token));

        Assert.False(provider.LastRequest.Stream);
    }

    [Fact]
    public async Task A_model_missing_from_the_cache_is_still_usable()
    {
        // A chat saved before a catalogue refresh can name a model the cache no longer lists.
        // Refusing to send would strand the conversation; rejecting an unknown id is the
        // provider's job, and its 404 says something useful.
        var conversationId = await NewChatAsync();
        var provider = new ScriptedProvider(ProviderId);

        var events = await CollectAsync(Service(provider).SendMessageAsync(Ask(conversationId), Token));

        Assert.IsType<ChatTurnEvent.Completed>(events[^1]);
        Assert.True(provider.LastRequest.Stream);
        Assert.Equal(ModelId, provider.LastRequest.ModelId);
    }

    [Fact]
    public async Task The_whole_conversation_is_sent_with_the_system_prompt_first()
    {
        // Section 18. Sending only the latest turn is the single most common way a chat client
        // loses its memory, and it looks like a model problem rather than a client bug.
        var conversationId = await NewChatAsync();
        await AddAsync(conversationId, MessageRole.User, "First question");
        await AddAsync(conversationId, MessageRole.Assistant, "First answer");

        var provider = new ScriptedProvider(ProviderId);
        var settings = new StubSettingsService().With<ChatSettings>(c => c.SystemPrompt = "Be terse.");

        await CollectAsync(Service(provider, settings: settings)
            .SendMessageAsync(Ask(conversationId, "Second question"), Token));

        var messages = provider.LastRequest.Messages;
        Assert.Equal(["system", "user", "assistant", "user"], messages.Select(m => m.Role));
        Assert.Equal("Be terse.", messages[0].Content);
        Assert.Equal("Second question", messages[^1].Content);

        // The empty placeholder created moments ago must not travel as an assistant turn:
        // several providers reject an empty message outright.
        Assert.DoesNotContain(messages, m => m.Content.Length == 0);
    }

    [Fact]
    public async Task A_conversations_own_system_prompt_overrides_the_global_one()
    {
        var conversationId = await NewChatAsync();

        await using (var db = _db.CreateDbContext())
        {
            var conversation = await db.Conversations.FirstAsync(c => c.Id == conversationId, Token);
            conversation.SystemPrompt = "You are a SQL reviewer.";
            await db.SaveChangesAsync(Token);
        }

        var provider = new ScriptedProvider(ProviderId);
        var settings = new StubSettingsService().With<ChatSettings>(c => c.SystemPrompt = "Be terse.");

        await CollectAsync(Service(provider, settings: settings).SendMessageAsync(Ask(conversationId), Token));

        Assert.Equal("You are a SQL reviewer.", provider.LastRequest.Messages[0].Content);
    }

    [Fact]
    public async Task A_failed_turn_is_left_out_of_the_next_request()
    {
        // A failed message holds an error string, not an answer. Replaying it would teach the
        // model that its own turn was the apology the user just saw.
        var conversationId = await NewChatAsync();
        var service = Service(ScriptedProvider.Throwing(ProviderId, new HttpRequestException("reset")));
        await CollectAsync(service.SendMessageAsync(Ask(conversationId, "First question"), Token));

        var provider = new ScriptedProvider(ProviderId);
        await CollectAsync(Service(provider).SendMessageAsync(Ask(conversationId, "Second question"), Token));

        Assert.Equal(["user", "user"], provider.LastRequest.Messages.Select(m => m.Role));
    }

    [Fact]
    public async Task Regenerating_replaces_the_old_answer_instead_of_appending_a_second_one()
    {
        var conversationId = await NewChatAsync();
        await CollectAsync(Service(ScriptedProvider.Streaming(ProviderId, "First answer"))
            .SendMessageAsync(Ask(conversationId, "Why?"), Token));

        var replaced = (await MessagesAsync(conversationId))[^1];

        var events = await CollectAsync(Service(ScriptedProvider.Streaming(ProviderId, "Second answer"))
            .RegenerateAsync(Regenerate(conversationId, replaced.Id), Token));

        var messages = await MessagesAsync(conversationId);
        Assert.Collection(
            messages,
            m => Assert.Equal("Why?", m.Content),
            m => Assert.Equal("Second answer", m.Content));

        // Gone, not merely hidden - and the question was not posted twice.
        Assert.DoesNotContain(messages, m => m.Id == replaced.Id);
        Assert.IsType<ChatTurnEvent.AssistantMessageStarted>(events[0]);
        Assert.IsType<ChatTurnEvent.Completed>(events[^1]);
    }

    [Fact]
    public async Task The_regenerated_request_no_longer_contains_the_answer_being_replaced()
    {
        // The point of Regenerate is a second attempt at the same question. Leaving the first
        // answer in the history would ask the model to continue it instead.
        var conversationId = await NewChatAsync();
        await CollectAsync(Service(ScriptedProvider.Streaming(ProviderId, "First answer"))
            .SendMessageAsync(Ask(conversationId, "Why?"), Token));

        var replaced = (await MessagesAsync(conversationId))[^1];
        var provider = new ScriptedProvider(ProviderId);

        await CollectAsync(Service(provider).RegenerateAsync(Regenerate(conversationId, replaced.Id), Token));

        Assert.Equal(["user"], provider.LastRequest.Messages.Select(m => m.Role));
        Assert.Equal("Why?", Assert.Single(provider.LastRequest.Messages).Content);
    }

    [Fact]
    public async Task Regenerating_may_switch_to_a_different_model()
    {
        // The retry bar offers a model picker: the usual reason to regenerate is that this
        // model answered badly.
        var conversationId = await NewChatAsync();
        await CollectAsync(Service(ScriptedProvider.Streaming(ProviderId, "First answer"))
            .SendMessageAsync(Ask(conversationId, "Why?"), Token));

        var replaced = (await MessagesAsync(conversationId))[^1];
        var other = new ScriptedProvider("other");
        var registry = new StubProviderRegistry(other);

        await CollectAsync(Service(registry: registry).RegenerateAsync(
            Regenerate(conversationId, replaced.Id) with { ProviderId = "other", ModelId = "other/model" },
            Token));

        Assert.Equal("other/model", other.LastRequest.ModelId);

        var answer = (await MessagesAsync(conversationId))[^1];
        Assert.Equal(("other", "other/model"), (answer.ProviderId, answer.ModelId));
    }

    [Fact]
    public async Task An_attached_file_is_stored_with_the_question_and_inlined_into_the_prompt()
    {
        var conversationId = await NewChatAsync();
        var provider = new ScriptedProvider(ProviderId);

        var events = await CollectAsync(Service(provider).SendMessageAsync(
            Ask(conversationId, "What does this do?") with
            {
                Attachments =
                [
                    new NewAttachment
                    {
                        FileName = "Program.cs",
                        MimeType = "text/plain",
                        Size = 24,
                        TextContent = "Console.WriteLine(\"hi\");",
                    },
                ],
            },
            Token));

        var saved = Assert.IsType<ChatTurnEvent.UserMessageSaved>(events[0]).Message;
        Assert.Equal("Program.cs", Assert.Single(saved.Attachments).FileName);

        // The model needs the text, not a filename, and the file comes first because that is
        // how the question reads: "this" refers to what precedes it.
        var sent = provider.LastRequest.Messages[^1].Content;
        Assert.Contains("<file name=\"Program.cs\">", sent, StringComparison.Ordinal);
        Assert.Contains("Console.WriteLine", sent, StringComparison.Ordinal);
        Assert.EndsWith("What does this do?", sent, StringComparison.Ordinal);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A chat whose title the user already chose, so auto-titling stays out of the way. Most
    /// tests here are about the turn, not the sidebar.
    /// </summary>
    private async Task<Guid> NewChatAsync() =>
        (await _conversations.CreateAsync("Chat", cancellationToken: Token)).Id;

    /// <summary>A chat still called "New Chat", for the tests that are about titling.</summary>
    private async Task<Guid> NewUntitledChatAsync() =>
        (await _conversations.CreateAsync(cancellationToken: Token)).Id;

    /// <summary>
    /// The real service over the real conversation store, with only the provider scripted.
    /// </summary>
    /// <remarks>
    /// Passing <paramref name="registry"/> replaces the whole catalogue, which is how a test
    /// publishes model capabilities; passing neither leaves a registry that knows no providers,
    /// which is the unconfigured case.
    /// </remarks>
    private ChatService Service(
        IAIProvider? provider = null,
        IProviderRegistry? registry = null,
        ISettingsService? settings = null) =>
        new(_conversations,
            registry ?? (provider is null ? new StubProviderRegistry() : new StubProviderRegistry(provider)),
            new ContextBuilder(_conversations, NullLogger<ContextBuilder>.Instance),
            settings ?? new StubSettingsService(),
            NullLogger<ChatService>.Instance);

    private static SendMessageRequest Ask(Guid conversationId, string content = "Hello?") =>
        new()
        {
            ConversationId = conversationId,
            Content = content,
            ProviderId = ProviderId,
            ModelId = ModelId,
        };

    private static RegenerateRequest Regenerate(Guid conversationId, Guid assistantMessageId) =>
        new()
        {
            ConversationId = conversationId,
            AssistantMessageId = assistantMessageId,
            ProviderId = ProviderId,
            ModelId = ModelId,
        };

    private Task AddAsync(Guid conversationId, MessageRole role, string content) =>
        _conversations.AddMessageAsync(conversationId, new NewMessage { Role = role, Content = content }, Token);

    private async Task<IReadOnlyList<MessageDto>> MessagesAsync(Guid conversationId) =>
        (await _conversations.GetAsync(conversationId, Token))!.Messages;

    private static async Task<List<ChatTurnEvent>> CollectAsync(IAsyncEnumerable<ChatTurnEvent> events)
    {
        var collected = new List<ChatTurnEvent>();

        await foreach (var evt in events)
        {
            collected.Add(evt);
        }

        return collected;
    }

    /// <summary>The answer as the UI would have assembled it from the deltas alone.</summary>
    private static string Text(IEnumerable<ChatTurnEvent> events) =>
        string.Concat(events.OfType<ChatTurnEvent.ContentDelta>().Select(d => d.Text));

    /// <summary>
    /// A stream with a pause longer than the flush interval in the middle, so the delta after it
    /// crosses the threshold and a third delta gives the test a chance to observe the result.
    /// </summary>
    private static async IAsyncEnumerable<AIStreamEvent> SlowStream(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new AIStreamEvent.ContentDelta("Saved ");
        await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellationToken).ConfigureAwait(false);
        yield return new AIStreamEvent.ContentDelta("while streaming");
        yield return new AIStreamEvent.ContentDelta(", then the rest.");
        yield return new AIStreamEvent.Completed("stop");
    }
}
