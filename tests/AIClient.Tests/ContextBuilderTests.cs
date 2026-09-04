using AIClient.Application.DTOs;
using AIClient.Application.Services;
using AIClient.Domain.Enums;
using AIClient.Domain.Interfaces;
using AIClient.Domain.Models;
using AIClient.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIClient.Tests;

/// <summary>
/// Section 18: every request is built from the conversation, never from the last message alone.
/// </summary>
/// <remarks>
/// Run against the real conversation store rather than a stub. The builder's job is to turn
/// what was actually persisted into a prompt, so the two have to agree about ordering, about
/// which rows are eligible, and about how attachments were saved - a hand-written stub would
/// let those drift apart silently.
/// </remarks>
public sealed class ContextBuilderTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private ContextBuilder _builder = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _builder = new ContextBuilder(_db.Conversations(), NullLogger<ContextBuilder>.Instance);
    }

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task The_whole_conversation_is_sent_in_order_not_just_the_latest_message()
    {
        var chat = await NewChatAsync(
            User("first question"),
            Assistant("first answer"),
            User("second question"));

        var result = await BuildAsync(chat);

        Assert.Equal(["user", "assistant", "user"], result.Messages.Select(m => m.Role));
        Assert.Equal(
            ["first question", "first answer", "second question"],
            result.Messages.Select(m => m.Content));
    }

    [Fact]
    public async Task The_system_prompt_leads_the_request()
    {
        var chat = await NewChatAsync(User("hello"));

        var result = await BuildAsync(chat, systemPrompt: "  You are terse.  ");

        Assert.Equal("system", result.Messages[0].Role);
        Assert.Equal("You are terse.", result.Messages[0].Content);
        Assert.Equal(2, result.Messages.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_system_prompt_produces_no_system_turn_at_all(string? prompt)
    {
        // Sending an empty system message is not the same as sending none: some providers
        // charge for it, and all of them treat it as an instruction to say nothing.
        var chat = await NewChatAsync(User("hello"));

        var result = await BuildAsync(chat, systemPrompt: prompt);

        Assert.DoesNotContain("system", result.Messages.Select(m => m.Role));
    }

    [Fact]
    public async Task Regenerating_rebuilds_the_history_as_it_stood_before_the_answer()
    {
        var chat = await NewChatAsync(User("question"), Assistant("first attempt"), User("later turn"));
        var answer = (await _db.Conversations().GetAsync(chat))!.Messages
            .Single(m => m.Content == "first attempt");

        var result = await BuildAsync(chat, upToMessageId: answer.Id);

        // Exclusive boundary: the answer being replaced, and everything after it, is gone.
        Assert.Equal(["question"], result.Messages.Select(m => m.Content));
    }

    [Fact]
    public async Task An_unknown_boundary_message_leaves_the_history_intact()
    {
        // The message can be deleted between the click and the build; truncating to nothing
        // would send an empty prompt.
        var chat = await NewChatAsync(User("question"), Assistant("answer"));

        var result = await BuildAsync(chat, upToMessageId: Guid.CreateVersion7());

        Assert.Equal(2, result.Messages.Count);
    }

    [Fact]
    public async Task A_failed_turn_is_not_offered_back_to_the_model()
    {
        var chat = await NewChatAsync(
            User("question"),
            new NewMessage
            {
                Role = MessageRole.Assistant,
                Content = "half an ans",
                Status = MessageStatus.Failed,
            },
            User("try again"));

        var result = await BuildAsync(chat);

        Assert.Equal(["question", "try again"], result.Messages.Select(m => m.Content));
    }

    [Fact]
    public async Task A_cancelled_turn_is_kept_because_its_partial_text_is_still_context()
    {
        // Pressing Stop leaves a real, readable answer. Dropping it would make the follow-up
        // question refer to something the model cannot see.
        var chat = await NewChatAsync(
            User("question"),
            new NewMessage
            {
                Role = MessageRole.Assistant,
                Content = "as far as I got",
                Status = MessageStatus.Cancelled,
            });

        var result = await BuildAsync(chat);

        Assert.Contains("as far as I got", result.Messages.Select(m => m.Content));
    }

    [Fact]
    public async Task An_empty_placeholder_is_skipped()
    {
        var chat = await NewChatAsync(
            User("question"),
            new NewMessage
            {
                Role = MessageRole.Assistant,
                Content = "   ",
                Status = MessageStatus.Streaming,
            });

        var result = await BuildAsync(chat);

        Assert.Equal(["question"], result.Messages.Select(m => m.Content));
    }

    [Fact]
    public async Task A_system_message_stored_in_the_transcript_is_not_repeated_as_a_turn()
    {
        // The system prompt arrives through the request, from settings. A stored System row
        // would otherwise be sent twice, or out of position.
        var chat = await NewChatAsync(
            new NewMessage { Role = MessageRole.System, Content = "stored instruction" },
            User("question"));

        var result = await BuildAsync(chat, systemPrompt: "from settings");

        Assert.Equal(["system", "user"], result.Messages.Select(m => m.Role));
        Assert.DoesNotContain("stored instruction", result.Messages.Select(m => m.Content));
    }

    [Fact]
    public async Task Attachment_text_is_inlined_ahead_of_the_question()
    {
        var chat = await NewChatAsync(new NewMessage
        {
            Role = MessageRole.User,
            Content = "What does this do?",
            Attachments =
            [
                new NewAttachment
                {
                    FileName = "Widget.cs",
                    MimeType = "text/plain",
                    Size = 24,
                    TextContent = "Console.WriteLine(\"hi\");",
                },
            ],
        });

        var content = Assert.Single((await BuildAsync(chat)).Messages).Content;

        Assert.Contains("<file name=\"Widget.cs\">", content, StringComparison.Ordinal);
        Assert.Contains("Console.WriteLine(\"hi\");", content, StringComparison.Ordinal);
        Assert.Contains("</file>", content, StringComparison.Ordinal);

        // Files first, question last: that is the order the question is written in.
        Assert.True(
            content.IndexOf("</file>", StringComparison.Ordinal)
                < content.IndexOf("What does this do?", StringComparison.Ordinal),
            "The question must follow the file content.");
    }

    [Fact]
    public async Task A_truncated_attachment_says_so_inside_the_prompt()
    {
        // Without the marker the model treats a cut-off file as the whole file and confidently
        // explains why the missing half does not exist.
        var chat = await NewChatAsync(new NewMessage
        {
            Role = MessageRole.User,
            Content = "Review this.",
            Attachments =
            [
                new NewAttachment
                {
                    FileName = "Huge.cs",
                    MimeType = "text/plain",
                    Size = 5_000_000,
                    TextContent = "class Huge {",
                    IsTruncated = true,
                },
            ],
        });

        var content = Assert.Single((await BuildAsync(chat)).Messages).Content;

        Assert.Contains("truncated", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_message_that_is_nothing_but_an_attachment_is_still_sent()
    {
        // Dragging a file in and pressing Enter without typing anything is a real request.
        var chat = await NewChatAsync(new NewMessage
        {
            Role = MessageRole.User,
            Content = string.Empty,
            Attachments =
            [
                new NewAttachment
                {
                    FileName = "notes.md",
                    MimeType = "text/markdown",
                    Size = 11,
                    TextContent = "# Some notes",
                },
            ],
        });

        var content = Assert.Single((await BuildAsync(chat)).Messages).Content;

        Assert.Contains("# Some notes", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_trimmed_when_the_context_window_is_unknown()
    {
        // A provider that does not publish a window is not a licence to guess one: the
        // request goes out whole and the provider decides.
        var chat = await NewChatAsync(Enumerable.Range(0, 20).Select(i => User($"turn {i}")).ToArray());

        var result = await BuildAsync(chat, contextWindow: null);

        Assert.Equal(20, result.Messages.Count);
        Assert.Equal(0, result.DroppedMessageCount);
    }

    [Fact]
    public async Task Oldest_turns_are_dropped_first_until_the_prompt_fits()
    {
        var messages = Enumerable.Range(0, 6)
            .Select(i => i % 2 == 0
                ? User($"question {i} {Filler}")
                : Assistant($"answer {i} {Filler}"))
            .ToArray();

        var chat = await NewChatAsync(messages);

        var result = await BuildAsync(chat, contextWindow: 300, reservedOutputTokens: 0);

        Assert.True(result.DroppedMessageCount > 0, "Six long turns cannot fit a 300-token budget.");
        Assert.Equal(6, result.Messages.Count + result.DroppedMessageCount);
        Assert.True(result.EstimatedTokens <= 300, $"Estimated {result.EstimatedTokens} tokens.");

        // The newest exchange is the one the answer depends on; the oldest is expendable.
        var kept = string.Concat(result.Messages.Select(m => m.Content));
        Assert.Contains("question 4", kept, StringComparison.Ordinal);
        Assert.Contains("answer 5", kept, StringComparison.Ordinal);
        Assert.DoesNotContain("question 0", kept, StringComparison.Ordinal);
        Assert.DoesNotContain("answer 1", kept, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trimming_never_drops_the_system_prompt()
    {
        var chat = await NewChatAsync(
            Enumerable.Range(0, 6).Select(i => User($"turn {i} {Filler}")).ToArray());

        var result = await BuildAsync(
            chat, systemPrompt: "Answer in Russian.", contextWindow: 300, reservedOutputTokens: 0);

        // Losing the instruction mid-conversation is the most visible possible regression:
        // the assistant would switch language halfway through.
        Assert.Equal("system", result.Messages[0].Role);
        Assert.Equal("Answer in Russian.", result.Messages[0].Content);
    }

    [Fact]
    public async Task The_final_question_survives_even_when_it_alone_overflows_the_window()
    {
        // Truncating the user's actual question would produce a confident answer to something
        // they did not ask. Letting the provider reject it is the honest outcome.
        var chat = await NewChatAsync(
            User($"old turn {Filler}"),
            User(new string('q', 4000)));

        var result = await BuildAsync(chat, contextWindow: 50, reservedOutputTokens: 0);

        var kept = Assert.Single(result.Messages);
        Assert.StartsWith("qqqq", kept.Content, StringComparison.Ordinal);
        Assert.Equal(1, result.DroppedMessageCount);
    }

    [Fact]
    public async Task A_history_left_starting_on_an_assistant_reply_is_realigned()
    {
        // Several providers reject a conversation whose first turn is an assistant message.
        // Trimming is exactly what creates that shape.
        var chat = await NewChatAsync(
            User($"one {Filler}"),
            Assistant($"two {Filler}"),
            User($"three {Filler}"),
            Assistant($"four {Filler}"));

        var result = await BuildAsync(chat, contextWindow: 250, reservedOutputTokens: 0);

        Assert.Equal("user", result.Messages[0].Role);
    }

    [Fact]
    public async Task A_reserve_larger_than_the_window_still_leaves_room_for_the_question()
    {
        // window - reserve would be negative here, and a negative budget drops everything.
        var chat = await NewChatAsync(User("short question"));

        var result = await BuildAsync(chat, contextWindow: 100, reservedOutputTokens: 4096);

        Assert.NotEmpty(result.Messages);
        Assert.Equal(0, result.DroppedMessageCount);
    }

    [Fact]
    public async Task The_estimate_grows_with_the_conversation()
    {
        var chat = await NewChatAsync(User("first"));
        var small = await BuildAsync(chat);

        await _db.Conversations().AddMessageAsync(chat, Assistant(Filler));
        var larger = await BuildAsync(chat);

        Assert.True(
            larger.EstimatedTokens > small.EstimatedTokens,
            $"{larger.EstimatedTokens} was not greater than {small.EstimatedTokens}.");
    }

    [Fact]
    public async Task Building_from_a_conversation_that_was_deleted_fails_loudly()
    {
        // Silently returning an empty prompt would send a request with no messages, and the
        // provider's complaint would point at the wrong thing.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _builder.BuildAsync(
                new ContextBuildRequest { ConversationId = Guid.CreateVersion7() },
                TestContext.Current.CancellationToken));
    }

    /// <summary>360 Latin characters, which the estimator prices at about 100 tokens.</summary>
    private static readonly string Filler = new('a', 360);

    // Section 18, the agent half: a tool exchange has to come back out of the database in the
    // one shape every provider accepts - the call and its answer, in that order, or neither.

    [Fact]
    public async Task A_tool_exchange_is_replayed_as_the_pair_the_provider_expects()
    {
        var chat = await NewChatAsync(
            User("what is in the readme?"),
            AssistantCalling("Let me look.", Call("call_1", "read_file", """{"path":"README.md"}""")),
            ToolResult("call_1", "read_file", "# Project"),
            Assistant("It is the project readme."));

        var result = await BuildAsync(chat);

        Assert.Equal(["user", "assistant", "tool", "assistant"], result.Messages.Select(m => m.Role));

        var call = Assert.Single(result.Messages[1].ToolCalls);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("read_file", call.Name);
        Assert.Contains("README.md", call.ArgumentsJson, StringComparison.Ordinal);

        // The answer is addressed to the call. Without the id the provider cannot pair them.
        Assert.Equal("call_1", result.Messages[2].ToolCallId);
        Assert.Equal("read_file", result.Messages[2].Name);
        Assert.Equal("# Project", result.Messages[2].Content);
    }

    [Fact]
    public async Task An_assistant_turn_that_said_nothing_and_only_acted_is_still_sent()
    {
        // The normal shape of an agent step: no commentary, just the call. Judged by text alone
        // this row looks empty, and dropping it would orphan the answer that follows.
        var chat = await NewChatAsync(
            User("read the readme"),
            AssistantCalling(string.Empty, Call("call_1", "read_file")),
            ToolResult("call_1", "read_file", "# Project"));

        var result = await BuildAsync(chat);

        Assert.Equal(["user", "assistant", "tool"], result.Messages.Select(m => m.Role));
        Assert.Single(result.Messages[1].ToolCalls);
    }

    [Fact]
    public async Task Several_calls_in_one_step_are_answered_as_one_block()
    {
        var chat = await NewChatAsync(
            User("compare the two files"),
            AssistantCalling(
                string.Empty,
                Call("call_1", "read_file"),
                Call("call_2", "read_file")),
            ToolResult("call_1", "read_file", "first"),
            ToolResult("call_2", "read_file", "second"));

        var result = await BuildAsync(chat);

        Assert.Equal(["user", "assistant", "tool", "tool"], result.Messages.Select(m => m.Role));
        Assert.Equal(2, result.Messages[1].ToolCalls.Count);
        Assert.Equal(["call_1", "call_2"], result.Messages.Skip(2).Select(m => m.ToolCallId));
    }

    [Fact]
    public async Task A_call_that_never_got_an_answer_loses_the_call_and_keeps_the_words()
    {
        // The app can be killed between writing the call and writing its result. What the model
        // said is still true and still useful; the unanswered call is a 400.
        var chat = await NewChatAsync(
            User("read the readme"),
            AssistantCalling("I will read it now.", Call("call_1", "read_file")),
            User("never mind"));

        var result = await BuildAsync(chat);

        Assert.Equal(["user", "assistant", "user"], result.Messages.Select(m => m.Role));
        Assert.Empty(result.Messages[1].ToolCalls);
        Assert.Equal("I will read it now.", result.Messages[1].Content);
    }

    [Fact]
    public async Task A_call_with_no_words_and_no_answer_is_dropped_outright()
    {
        var chat = await NewChatAsync(
            User("read the readme"),
            AssistantCalling(string.Empty, Call("call_1", "read_file")),
            User("never mind"));

        var result = await BuildAsync(chat);

        // Nothing survives stripping the calls from it, so there is nothing to send.
        Assert.Equal(["user", "user"], result.Messages.Select(m => m.Role));
    }

    [Fact]
    public async Task An_answer_whose_call_is_gone_is_dropped()
    {
        var chat = await NewChatAsync(
            User("read the readme"),
            ToolResult("call_1", "read_file", "# Project"),
            User("well?"));

        var result = await BuildAsync(chat);

        Assert.Equal(["user", "user"], result.Messages.Select(m => m.Role));
    }

    [Fact]
    public async Task An_answer_stored_ahead_of_its_call_counts_as_no_answer_at_all()
    {
        // Presence is not pairing. A result that sits before the call it names cannot be sent, so
        // treating it as an answer would keep the call and leave it unanswered - the same 400,
        // arrived at by being too clever.
        var chat = await NewChatAsync(
            User("read the readme"),
            ToolResult("call_1", "read_file", "# Project"),
            AssistantCalling("Let me look.", Call("call_1", "read_file")));

        var result = await BuildAsync(chat);

        Assert.Equal(["user", "assistant"], result.Messages.Select(m => m.Role));
        Assert.Empty(result.Messages[1].ToolCalls);
    }

    [Fact]
    public async Task A_transcript_too_damaged_to_read_costs_the_step_and_not_the_request()
    {
        var chat = await NewChatAsync(
            User("read the readme"),
            new NewMessage
            {
                Role = MessageRole.Assistant,
                Content = "Let me look.",
                ToolCallsJson = "{ this is not json",
            },
            User("well?"));

        var result = await BuildAsync(chat);

        Assert.Equal(["user", "assistant", "user"], result.Messages.Select(m => m.Role));
        Assert.Empty(result.Messages[1].ToolCalls);
    }

    [Fact]
    public async Task A_refused_call_stays_in_the_history_so_the_model_can_correct_itself()
    {
        // A refusal is a complete message, not a failed one. Dropping it would let the model
        // propose the same forbidden path on every following step.
        var chat = await NewChatAsync(
            User("read the whole disk"),
            AssistantCalling(string.Empty, Call("call_1", "read_file")),
            new NewMessage
            {
                Role = MessageRole.Tool,
                Content = "The path leaves the workspace.",
                ToolCallId = "call_1",
                ToolName = "read_file",
                ToolSucceeded = false,
            });

        var result = await BuildAsync(chat);

        Assert.Equal(["user", "assistant", "tool"], result.Messages.Select(m => m.Role));
        Assert.Equal("The path leaves the workspace.", result.Messages[2].Content);
    }

    [Fact]
    public async Task Trimming_drops_a_call_and_its_answer_together()
    {
        // The reason the builder groups them at all. Dropping the call alone would leave an
        // orphaned answer; dropping the answer alone would leave an unanswered call.
        var chat = await NewChatAsync(
            User("a"),
            AssistantCalling(string.Empty, Call("call_1", "read_file")),
            ToolResult("call_1", "read_file", new string('b', 720)),
            User("final question"));

        var result = await BuildAsync(chat, contextWindow: 150, reservedOutputTokens: 0);

        Assert.Equal(["final question"], result.Messages.Select(m => m.Content));
        Assert.Equal(3, result.DroppedMessageCount);
    }

    [Fact]
    public async Task What_a_call_carries_is_counted_against_the_budget()
    {
        // A write_file call is three kilobytes of source in its arguments and nothing in its
        // text. Pricing it by text alone would overrun the window by exactly the amount that
        // matters, and the provider would be the one to notice.
        var chat = await NewChatAsync(
            User("write the file"),
            AssistantCalling(string.Empty, Call("call_1", "write_file", Filler)),
            ToolResult("call_1", "write_file", "ok"));

        var result = await BuildAsync(chat);

        Assert.True(
            result.EstimatedTokens > 100,
            $"A 360-character argument object was priced at {result.EstimatedTokens} tokens in total.");
    }

    private async Task<Guid> NewChatAsync(params NewMessage[] messages)
    {
        var service = _db.Conversations();
        var chat = await service.CreateAsync("Fixture");

        foreach (var message in messages)
        {
            await service.AddMessageAsync(chat.Id, message);
        }

        return chat.Id;
    }

    private Task<ContextBuildResult> BuildAsync(
        Guid conversationId,
        string? systemPrompt = null,
        int? contextWindow = null,
        int reservedOutputTokens = 1024,
        Guid? upToMessageId = null) =>
        _builder.BuildAsync(
            new ContextBuildRequest
            {
                ConversationId = conversationId,
                SystemPrompt = systemPrompt,
                ContextWindow = contextWindow,
                ReservedOutputTokens = reservedOutputTokens,
                UpToMessageId = upToMessageId,
            },
            TestContext.Current.CancellationToken);

    private static NewMessage User(string content) =>
        new() { Role = MessageRole.User, Content = content };

    private static NewMessage Assistant(string content) =>
        new() { Role = MessageRole.Assistant, Content = content };

    /// <summary>An assistant step that decided to act, stored the way the agent loop stores it.</summary>
    private static NewMessage AssistantCalling(string content, params AIToolCall[] calls) =>
        new()
        {
            Role = MessageRole.Assistant,
            Content = content,
            ToolCallsJson = AgentTranscript.Write(calls),
        };

    private static NewMessage ToolResult(string callId, string name, string content) =>
        new()
        {
            Role = MessageRole.Tool,
            Content = content,
            ToolCallId = callId,
            ToolName = name,
            ToolSucceeded = true,
        };

    private static AIToolCall Call(string id, string name, string argumentsJson = "{}") =>
        new(id, name, argumentsJson);
}
