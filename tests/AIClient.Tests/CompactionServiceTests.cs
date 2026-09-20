using AIClient.Application.Configuration;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Application.Services;
using AIClient.Domain.Enums;
using AIClient.Domain.Interfaces;
using AIClient.Domain.Models;
using AIClient.Infrastructure.Repositories;
using AIClient.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIClient.Tests;

/// <summary>
/// What compaction folds, what it refuses to fold, and what the prompt looks like afterwards.
/// </summary>
/// <remarks>
/// The conversation store is the real one over real SQLite, because the invariant being tested is
/// a property of what ends up on disk: which rows carry <c>IsCompacted</c>, where the summary row
/// lands, and whether the next request the context builder assembles is one a provider would
/// accept. Only the model is scripted - it is the single collaborator whose answer a test has to
/// dictate.
/// </remarks>
public sealed class CompactionServiceTests : IAsyncLifetime
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
    public async Task Old_turns_are_folded_and_the_recent_ones_are_left_alone()
    {
        var conversationId = await ChatAsync(turns: 8, billedInput: 9_000);
        var service = Service(out var provider);

        var result = await service.CompactAsync(Request(keepRecent: 4), Token);

        Assert.Equal(12, result.MessagesFolded);
        Assert.Null(result.SkippedReason);

        var messages = await MessagesAsync(conversationId);
        var live = messages.Where(m => !m.IsCompacted).ToList();

        // Four kept turns plus the summary. Everything older carries the flag instead of being
        // deleted, which is what keeps the transcript a faithful record.
        Assert.Equal(5, live.Count);
        Assert.Single(live, m => m.IsContextSummary);
        Assert.Equal(12, messages.Count(m => m.IsCompacted));
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task The_summary_is_a_user_turn_carrying_the_model_prose_inside_a_marker()
    {
        await ChatAsync(turns: 8, billedInput: 9_000);
        var service = Service(out _, summary: "Chose SQLite. Rejected LiteDB.");

        var result = await service.CompactAsync(Request(keepRecent: 4), Token);

        var summary = Assert.IsType<MessageDto>(result.Summary);

        // A user turn, not a second system turn: several providers reject more than one, and the
        // marker is what stops the model answering the recap as though it were a question.
        Assert.Equal(MessageRole.User, summary.Role);
        Assert.True(summary.IsContextSummary);
        Assert.Contains("Chose SQLite. Rejected LiteDB.", summary.Content, StringComparison.Ordinal);
        Assert.Contains("folded-messages=\"12\"", summary.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_transcript_the_model_is_asked_to_summarise_never_quotes_attachment_bodies()
    {
        var conversationId = await NewChatAsync();

        await _conversations.AddMessageAsync(
            conversationId,
            new NewMessage
            {
                Role = MessageRole.User,
                Content = "Review this",
                Attachments =
                [
                    new NewAttachment
                    {
                        FileName = "secrets.env",
                        MimeType = "text/plain",
                        TextContent = "API_KEY=super-secret-value",
                    },
                ],
            },
            Token);

        await AssistantAsync(conversationId, "Reviewed.", billedInput: 9_000);
        await UserAsync(conversationId, "And now?");

        var service = Service(out var provider);

        await service.CompactAsync(Request(keepRecent: 1), Token);

        var sent = Assert.Single(provider.Requests).Messages.Last().Content;

        // The file name is context; the body is not the summariser's business and would put a key
        // into a request that had no reason to carry one.
        Assert.Contains("secrets.env", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-value", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tool_result_is_never_left_answering_a_call_that_was_folded_away()
    {
        var conversationId = await NewChatAsync();

        await UserAsync(conversationId, "Read the config");

        // The boundary is arranged to fall between the step and its answer: keeping two would
        // otherwise leave the tool row live while the assistant row that asked for it is folded.
        await StepAsync(conversationId, "call-1", "read_file", billedInput: 9_000);
        await ToolResultAsync(conversationId, "call-1", "read_file", "port = 8080");
        await UserAsync(conversationId, "Thanks");

        var service = Service(out _);

        await service.CompactAsync(Request(keepRecent: 2), Token);

        var messages = await MessagesAsync(conversationId);
        var step = messages.Single(m => m.ToolCallsJson is not null);
        var answer = messages.Single(m => m.Role == MessageRole.Tool);

        // Either both go or both stay. A half-pair is the one shape every provider rejects.
        Assert.Equal(step.IsCompacted, answer.IsCompacted);
    }

    [Fact]
    public async Task A_step_whose_calls_were_never_answered_stays_out_of_the_fold()
    {
        var conversationId = await NewChatAsync();

        await UserAsync(conversationId, "Read the config");
        await AssistantAsync(conversationId, "Looking.", billedInput: 9_000);
        await UserAsync(conversationId, "Well?");

        // A crashed run: the calls exist, nothing answered them. Folding this would describe
        // calls in a summary while the transcript still shows them unanswered.
        var orphan = await StepAsync(conversationId, "call-9", "read_file");
        await UserAsync(conversationId, "Still waiting");

        var service = Service(out _);

        await service.CompactAsync(Request(keepRecent: 1), Token);

        var messages = await MessagesAsync(conversationId);

        Assert.False(messages.Single(m => m.Id == orphan.Id).IsCompacted);
    }

    [Fact]
    public async Task The_newest_message_is_kept_even_when_the_setting_says_to_keep_nothing()
    {
        var conversationId = await ChatAsync(turns: 6, billedInput: 9_000);
        var service = Service(out _);

        await service.CompactAsync(Request(keepRecent: 0), Token);

        var messages = await MessagesAsync(conversationId);
        var newest = messages.Where(m => !m.IsContextSummary).OrderBy(m => m.SequenceNumber).Last();

        // It is the question being asked. A summary of the question is not something a model can
        // answer.
        Assert.False(newest.IsCompacted);
    }

    [Fact]
    public async Task An_earlier_summary_is_absorbed_so_only_one_ever_stands_in_the_prompt()
    {
        var conversationId = await ChatAsync(turns: 8, billedInput: 9_000);
        var service = Service(out _);

        await service.CompactAsync(Request(keepRecent: 4), Token);

        await AssistantAsync(conversationId, "More work.", billedInput: 9_000);
        await UserAsync(conversationId, "And now?");

        await service.CompactAsync(Request(keepRecent: 2), Token);

        var live = (await MessagesAsync(conversationId)).Where(m => !m.IsCompacted).ToList();

        // Summaries are appended, so the first one sits inside the keep-recent window forever and
        // would never age out on its own. Absorbing it is what stops one accumulating per pass.
        Assert.Single(live, m => m.IsContextSummary);
    }

    [Fact]
    public async Task The_next_request_leads_with_the_summary_however_late_its_row_was_written()
    {
        var conversationId = await ChatAsync(turns: 8, billedInput: 9_000);
        var service = Service(out _);

        await service.CompactAsync(Request(keepRecent: 4), Token);

        var builder = new ContextBuilder(_conversations, NullLogger<ContextBuilder>.Instance);

        var context = await builder.BuildAsync(
            new ContextBuildRequest
            {
                ConversationId = conversationId,
                ContextWindow = 200_000,
            },
            Token);

        var first = context.Messages.First(m => m.Role != "system");

        // The row was appended last; the history it stands in for came first. Sent in row order it
        // would be the final thing the model reads before the question it has to answer.
        Assert.Contains("<context-summary", first.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chat_with_room_left_is_refused_unless_the_user_asked_for_it()
    {
        await ChatAsync(turns: 8, billedInput: 100);
        var service = Service(out var provider);

        var declined = await service.CompactAsync(Request(keepRecent: 4), Token);

        Assert.Equal(0, declined.MessagesFolded);
        Assert.NotNull(declined.SkippedReason);
        Assert.Empty(provider.Requests);

        // A manual request is a decision, not a heuristic: refusing it would leave the button
        // doing nothing with no explanation.
        var forced = await service.CompactAsync(Request(keepRecent: 4, force: true), Token);

        Assert.True(forced.MessagesFolded > 0);
    }

    [Fact]
    public async Task A_model_with_no_published_window_is_left_alone_until_asked_directly()
    {
        await ChatAsync(turns: 8, billedInput: 9_000);
        var service = Service(out _, contextWindow: null);

        var result = await service.CompactAsync(Request(keepRecent: 4), Token);

        Assert.Equal(0, result.MessagesFolded);
        Assert.NotNull(result.SkippedReason);
        Assert.False(await service.ShouldCompactAsync(await NewChatAsync(), ProviderId, ModelId, Token));
    }

    [Fact]
    public async Task A_summarisation_that_fails_folds_nothing()
    {
        var conversationId = await ChatAsync(turns: 8, billedInput: 9_000);

        var provider = ScriptedProvider.Emitting(
            ProviderId,
            new AIStreamEvent.Error(AIErrorKind.RateLimited, "Slow down", null));

        var service = Service(provider);

        var result = await service.CompactAsync(Request(keepRecent: 4), Token);

        Assert.Equal(0, result.MessagesFolded);
        Assert.NotNull(result.SkippedReason);

        // Nothing marked and no stand-in written: the alternative is history the model cannot see
        // with nothing in its place.
        Assert.DoesNotContain(await MessagesAsync(conversationId), m => m.IsCompacted);
    }

    [Fact]
    public async Task The_summarisation_request_is_a_single_shot_that_leaves_sampling_alone()
    {
        await ChatAsync(turns: 8, billedInput: 9_000);
        var service = Service(out var provider);

        await service.CompactAsync(Request(keepRecent: 4), Token);

        var request = Assert.Single(provider.Requests);

        Assert.False(request.Stream);
        Assert.Null(request.Temperature);
        Assert.Null(request.TopP);
        Assert.Empty(request.Tools);
        Assert.Equal(2, request.Messages.Count);
        Assert.Equal("system", request.Messages[0].Role);
    }

    [Fact]
    public async Task A_chat_too_short_to_have_history_reports_that_rather_than_folding_one_turn()
    {
        var conversationId = await NewChatAsync();

        await UserAsync(conversationId, "Hello");
        await AssistantAsync(conversationId, "Hello.", billedInput: 9_000);

        var service = Service(out var provider);

        var result = await service.CompactAsync(Request(keepRecent: 4, force: true), Token);

        Assert.Equal(0, result.MessagesFolded);
        Assert.NotNull(result.SkippedReason);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task A_conversation_that_no_longer_exists_is_reported_not_thrown()
    {
        var service = Service(out _);

        var result = await service.CompactAsync(
            new CompactionRequest
            {
                ConversationId = Guid.CreateVersion7(),
                ProviderId = ProviderId,
                ModelId = ModelId,
            },
            Token);

        Assert.Equal(0, result.MessagesFolded);
        Assert.NotNull(result.SkippedReason);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private CompactionRequest Request(int? keepRecent = null, bool force = false) => new()
    {
        ConversationId = _conversationId,
        ProviderId = ProviderId,
        ModelId = ModelId,
        KeepRecentMessages = keepRecent,
        Force = force,
    };

    private Guid _conversationId;

    private CompactionService Service(
        out ScriptedProvider provider,
        string summary = "Earlier: the schema was chosen and the migrations were written.",
        int? contextWindow = 10_000)
    {
        provider = ScriptedProvider.Emitting(
            ProviderId,
            new AIStreamEvent.ContentDelta(summary),
            new AIStreamEvent.Completed("stop"));

        return Service(provider, contextWindow);
    }

    private CompactionService Service(ScriptedProvider provider, int? contextWindow = 10_000) =>
        new(_conversations,
            new StubProviderRegistry(provider).WithModel(ProviderId, ModelId, contextWindow, maxOutputTokens: 4_000),
            new StubSettingsService().With<ChatSettings>(c => c.CompactAtPercent = 85),
            NullLogger<CompactionService>.Instance);

    private async Task<Guid> NewChatAsync()
    {
        _conversationId = (await _conversations.CreateAsync("Chat", ProviderId, ModelId, Token)).Id;
        return _conversationId;
    }

    /// <summary>
    /// A chat of <paramref name="turns"/> question-and-answer pairs, the last answer billed at
    /// <paramref name="billedInput"/> so the fullness gate can be steered from a test.
    /// </summary>
    private async Task<Guid> ChatAsync(int turns, int billedInput)
    {
        var conversationId = await NewChatAsync();

        for (var i = 0; i < turns; i++)
        {
            await UserAsync(conversationId, $"Question {i + 1}: what about the storage layer?");
            await AssistantAsync(
                conversationId,
                $"Answer {i + 1}: it is EF Core over SQLite, with migrations checked in.",
                i == turns - 1 ? billedInput : null);
        }

        return conversationId;
    }

    private Task<MessageDto> UserAsync(Guid conversationId, string content) =>
        _conversations.AddMessageAsync(
            conversationId,
            new NewMessage { Role = MessageRole.User, Content = content },
            Token);

    private async Task<MessageDto> AssistantAsync(Guid conversationId, string content, int? billedInput = null)
    {
        var message = await _conversations.AddMessageAsync(
            conversationId,
            new NewMessage
            {
                Role = MessageRole.Assistant,
                Content = content,
                ProviderId = ProviderId,
                ModelId = ModelId,
            },
            Token);

        return billedInput is null ? message : await BillAsync(message, billedInput.Value, outputTokens: 200);
    }

    /// <summary>An assistant turn that asked for a tool and is waiting for the answer.</summary>
    private async Task<MessageDto> StepAsync(
        Guid conversationId,
        string callId,
        string toolName,
        int? billedInput = null)
    {
        var message = await _conversations.AddMessageAsync(
            conversationId,
            new NewMessage
            {
                Role = MessageRole.Assistant,
                Content = string.Empty,
                ProviderId = ProviderId,
                ModelId = ModelId,
                ToolCallsJson = AgentTranscript.Write([new AIToolCall(callId, toolName, """{"path":"app.config"}""")]),
            },
            Token);

        return billedInput is null ? message : await BillAsync(message, billedInput.Value, outputTokens: 50);
    }

    /// <summary>
    /// Records what a provider charged for a turn, which is what steers the fullness gate.
    /// </summary>
    /// <remarks>
    /// A second write rather than a field on <see cref="NewMessage"/>, because that is how the real
    /// orchestrators do it: usage only arrives once the stream is over.
    /// </remarks>
    private async Task<MessageDto> BillAsync(MessageDto message, int inputTokens, int outputTokens)
    {
        await _conversations.UpdateMessageAsync(
            new MessageUpdate
            {
                MessageId = message.Id,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
            },
            Token);

        return message with { InputTokens = inputTokens, OutputTokens = outputTokens };
    }

    private Task<MessageDto> ToolResultAsync(Guid conversationId, string callId, string toolName, string content) =>
        _conversations.AddMessageAsync(
            conversationId,
            new NewMessage
            {
                Role = MessageRole.Tool,
                Content = content,
                ToolCallId = callId,
                ToolName = toolName,
                ToolSucceeded = true,
            },
            Token);

    private async Task<IReadOnlyList<MessageDto>> MessagesAsync(Guid conversationId) =>
        (await _conversations.GetAsync(conversationId, Token))!.Messages;
}
