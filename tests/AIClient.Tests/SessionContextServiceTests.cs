using AIClient.Application.Configuration;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Application.Services;
using AIClient.Domain.Enums;
using AIClient.Domain.Models;
using AIClient.Infrastructure.Repositories;
using AIClient.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIClient.Tests;

/// <summary>
/// The numbers the context panel draws, and which of them are billed rather than guessed.
/// </summary>
/// <remarks>
/// Two kinds of number meet in this report and the tests keep them apart deliberately. The token
/// counts come from what a provider charged for the newest turn, so they are exact and describe one
/// request. The breakdown is estimated locally, because no provider says which messages filled the
/// window - so those assertions are about proportion and attribution, never about an exact count a
/// change to the estimator would invalidate.
/// </remarks>
public sealed class SessionContextServiceTests : IAsyncLifetime
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
    public async Task The_total_is_prompt_completion_and_cache_with_reasoning_shown_but_not_added()
    {
        var conversationId = await NewChatAsync();

        await UserAsync(conversationId, "Where does the schema live?");
        await AssistantAsync(conversationId, "In the migrations folder.", new Billed(434, 323, 120, 73_152, 0));

        var report = await Report(conversationId);

        Assert.NotNull(report);
        Assert.Equal(434, report.InputTokens);
        Assert.Equal(323, report.OutputTokens);
        Assert.Equal(120, report.ReasoningTokens);
        Assert.Equal(73_152, report.CacheReadTokens);

        // Reasoning is a slice of the completion, not an extra charge, so adding it would report
        // more tokens than the provider billed.
        Assert.Equal(73_909, report.TotalTokens);
        Assert.Equal(37, Math.Round(report.UsagePercent!.Value));
    }

    [Fact]
    public async Task Only_the_newest_turn_is_counted_because_a_prompt_already_contains_the_history()
    {
        var conversationId = await NewChatAsync();

        await UserAsync(conversationId, "First");
        await AssistantAsync(conversationId, "First answer.", new Billed(1_000, 100));
        await UserAsync(conversationId, "Second");
        await AssistantAsync(conversationId, "Second answer.", new Billed(2_400, 150));

        var report = await Report(conversationId);

        // Summing the two would report 3 400 + 250 for a request that carried 2 400 - the arithmetic
        // that turns a 74k prompt into a reported million.
        Assert.Equal(2_400, report!.InputTokens);
        Assert.Equal(2_550, report.TotalTokens);
    }

    [Fact]
    public async Task A_streaming_turn_is_ignored_until_it_reports_what_it_cost()
    {
        var conversationId = await NewChatAsync();

        await UserAsync(conversationId, "First");
        await AssistantAsync(conversationId, "Answered.", new Billed(1_000, 100));

        await _conversations.AddMessageAsync(
            conversationId,
            new NewMessage
            {
                Role = MessageRole.Assistant,
                Content = "Half written",
                Status = MessageStatus.Streaming,
                ProviderId = ProviderId,
                ModelId = ModelId,
            },
            Token);

        var report = await Report(conversationId);

        // The in-flight row has no usage yet. Reading it would show the panel dropping to zero the
        // moment the user pressed send.
        Assert.Equal(1_000, report!.InputTokens);
    }

    [Fact]
    public async Task Tool_call_arguments_are_charged_to_the_tools_not_to_the_assistant_that_asked()
    {
        var conversationId = await NewChatAsync();

        await UserAsync(conversationId, "Write the config");

        var arguments = $$"""{"path":"app.config","content":"{{new string('x', 4_000)}}"}""";

        await _conversations.AddMessageAsync(
            conversationId,
            new NewMessage
            {
                Role = MessageRole.Assistant,
                Content = "Writing it.",
                ProviderId = ProviderId,
                ModelId = ModelId,
                ToolCallsJson = AgentTranscript.Write([new AIToolCall("call-1", "write_file", arguments)]),
            },
            Token);

        var report = await Report(conversationId);
        var breakdown = report!.Breakdown;

        // A write_file call carrying four kilobytes of source is tool cost, not something the model
        // said - which is what makes the bar's tool band the honest place to look when a chat fills.
        Assert.True(breakdown.ToolTokens > breakdown.AssistantTokens * 10);
    }

    [Fact]
    public async Task An_attachment_is_charged_to_the_turn_that_carried_it()
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
                        FileName = "Program.cs",
                        MimeType = "text/plain",
                        TextContent = new string('y', 8_000),
                    },
                ],
            },
            Token);

        var report = await Report(conversationId);

        // Where the model sees it: inlined into the question, not in a category of its own.
        Assert.True(report!.Breakdown.UserTokens > 1_000);
        Assert.Equal(0, report.Breakdown.AssistantTokens);
    }

    [Fact]
    public async Task Folded_history_leaves_the_breakdown_so_the_bar_drops_after_a_compaction()
    {
        var conversationId = await NewChatAsync();

        var first = await UserAsync(conversationId, new string('a', 6_000));
        await AssistantAsync(conversationId, "Noted.", new Billed(1_000, 100));

        var before = (await Report(conversationId))!.Breakdown.UserTokens;

        await _conversations.MarkCompactedAsync([first.Id], Token);

        var after = (await Report(conversationId))!.Breakdown.UserTokens;

        Assert.True(after < before);
        Assert.Equal(1, (await Report(conversationId))!.CompactedMessageCount);
    }

    [Fact]
    public async Task A_model_the_catalogue_does_not_know_reports_an_unknown_window_rather_than_failing()
    {
        var conversationId = await NewChatAsync();

        await UserAsync(conversationId, "Hello");
        await AssistantAsync(conversationId, "Hello.", new Billed(500, 50));

        var report = await Report(conversationId, catalogued: false);

        Assert.NotNull(report);
        Assert.Null(report.ContextWindow);
        Assert.Null(report.UsagePercent);
        Assert.Null(report.TotalCost);
        Assert.False(report.ShouldCompact);

        // The ids are still worth showing: "MiMo V2.5" with no window beats an empty panel.
        Assert.Equal(ModelId, report.ModelName);
    }

    [Fact]
    public async Task Cost_accumulates_over_the_whole_chat_even_though_tokens_do_not()
    {
        var conversationId = await NewChatAsync();

        await UserAsync(conversationId, "First");
        await AssistantAsync(conversationId, "First answer.", new Billed(1_000_000, 0));
        await UserAsync(conversationId, "Second");
        await AssistantAsync(conversationId, "Second answer.", new Billed(0, 1_000_000));

        var report = await Report(conversationId);

        // Money is the one figure that is summed: a turn that has been paid for stays paid for.
        Assert.Equal(4.00m, report!.TotalCost);
    }

    [Fact]
    public async Task The_warning_fires_at_the_configured_threshold_and_not_before()
    {
        var conversationId = await NewChatAsync();

        await UserAsync(conversationId, "Hello");
        await AssistantAsync(conversationId, "Hello.", new Billed(8_000, 100));

        // 8 100 of 10 000 is 81%, under the configured 85%.
        Assert.False((await Report(conversationId, window: 10_000))!.ShouldCompact);

        await UserAsync(conversationId, "More");
        await AssistantAsync(conversationId, "More.", new Billed(8_600, 100));

        // 8 700 is 87%. The panel reads the same CompactAtPercent setting compaction gates on, so a
        // user who sees the warning and presses the button is not told there is nothing to fold.
        Assert.True((await Report(conversationId, window: 10_000))!.ShouldCompact);
    }

    [Fact]
    public async Task The_report_answers_for_the_model_that_will_receive_the_next_message()
    {
        var conversationId = await NewChatAsync();

        await UserAsync(conversationId, "Hello");
        await AssistantAsync(conversationId, "Hello.", new Billed(1_000, 100));

        var service = new SessionContextService(
            _conversations,
            new StubProviderRegistry()
                .WithModel(ProviderId, ModelId, contextWindow: 10_000)
                .WithModel(ProviderId, "test/large", contextWindow: 1_000_000),
            new StubSettingsService(),
            NullLogger<SessionContextService>.Instance);

        var report = await service.GetReportAsync(conversationId, ProviderId, "test/large", Token);

        // The picker's choice wins over what the conversation last recorded: the question the panel
        // answers is "how much room is there for the next message".
        Assert.Equal(1_000_000, report!.ContextWindow);
    }

    [Fact]
    public async Task A_conversation_that_does_not_exist_reports_nothing_rather_than_an_empty_panel()
    {
        var service = Service();

        Assert.Null(await service.GetReportAsync(Guid.CreateVersion7(), ProviderId, ModelId, Token));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<SessionContextReport?> Report(
        Guid conversationId,
        bool catalogued = true,
        int window = 200_000) =>
        await Service(catalogued, window).GetReportAsync(conversationId, ProviderId, ModelId, Token);

    private SessionContextService Service(bool catalogued = true, int window = 200_000)
    {
        var registry = new StubProviderRegistry();

        if (catalogued)
        {
            registry.WithModel(new ModelInfo
            {
                ProviderId = ProviderId,
                ProviderName = "Test Provider",
                ModelId = ModelId,
                Name = "Test Model",
                ContextWindow = window,
                PromptPricePerMillion = 3.00m,
                CompletionPricePerMillion = 1.00m,
            });
        }

        return new SessionContextService(
            _conversations,
            registry,
            new StubSettingsService().With<ChatSettings>(c => c.CompactAtPercent = 85),
            NullLogger<SessionContextService>.Instance);
    }

    private async Task<Guid> NewChatAsync() =>
        (await _conversations.CreateAsync("Chat", ProviderId, ModelId, Token)).Id;

    private Task<MessageDto> UserAsync(Guid conversationId, string content) =>
        _conversations.AddMessageAsync(
            conversationId,
            new NewMessage { Role = MessageRole.User, Content = content },
            Token);

    private async Task<MessageDto> AssistantAsync(Guid conversationId, string content, Billed billed)
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

        await _conversations.UpdateMessageAsync(
            new MessageUpdate
            {
                MessageId = message.Id,
                InputTokens = billed.Input,
                OutputTokens = billed.Output,
                ReasoningTokens = billed.Reasoning,
                CacheReadTokens = billed.CacheRead,
                CacheWriteTokens = billed.CacheWrite,
            },
            Token);

        return message;
    }

    /// <summary>What a provider charged for one turn, as the five figures it reports.</summary>
    private readonly record struct Billed(
        int Input,
        int Output,
        int? Reasoning = null,
        int? CacheRead = null,
        int? CacheWrite = null);
}
