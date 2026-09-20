using AIClient.Application.DTOs;
using AIClient.Application.Services;
using AIClient.Domain.Enums;
using AIClient.Domain.Models;

namespace AIClient.Tests;

/// <summary>
/// The one definition of what a stored message costs in the prompt.
/// </summary>
/// <remarks>
/// Pure arithmetic over DTOs, so these tests build messages directly rather than going through the
/// database: the subject is the costing rules, not persistence. Sizes are compared against each
/// other rather than against literals - the estimator's constants are an implementation detail, and
/// a test that pinned "1 130 tokens" would fail the first time they were tuned while saying nothing
/// about whether the attribution was still right.
/// </remarks>
public sealed class ContextCostTests
{
    [Fact]
    public void An_attachment_body_is_charged_to_the_message_that_carried_it()
    {
        var bare = User("Review this");
        var withFile = User("Review this", Attachment(new string('x', 3_600)));

        // A thousand tokens of file against a three-word question. It lands on the user turn
        // because that is where the model reads it - inlined into the question, not in a category
        // of its own.
        Assert.True(ContextCost.OfProse(withFile) - ContextCost.OfProse(bare) > 900);
    }

    [Fact]
    public void An_empty_message_still_costs_the_per_message_overhead()
    {
        // Role and delimiters go on the wire whether or not there is anything to say, so a
        // placeholder that never received tokens is not free.
        Assert.True(ContextCost.OfProse(User(string.Empty)) > 0);
    }

    [Fact]
    public void Tool_call_arguments_are_counted_apart_from_what_the_model_said()
    {
        var message = Assistant(
            "Writing it.",
            Calls(new AIToolCall("call-1", "write_file", Arguments(new string('x', 3_600)))));

        // The prose is a sentence; the arguments are a file. Charging both to the assistant would
        // make the panel's assistant band swallow the tool band and hide where a window went.
        Assert.True(ContextCost.OfToolCalls(message) > 900);
        Assert.True(ContextCost.OfProse(message) < 50);
    }

    [Fact]
    public void Several_calls_in_one_step_are_all_counted()
    {
        var one = Assistant("Reading.", Calls(new AIToolCall("call-1", "read_file", Arguments("a"))));

        var three = Assistant(
            "Reading.",
            Calls(
                new AIToolCall("call-1", "read_file", Arguments("a")),
                new AIToolCall("call-2", "read_file", Arguments("b")),
                new AIToolCall("call-3", "read_file", Arguments("c"))));

        // A parallel step is the common case for an agent, and the window it fills is the sum.
        Assert.True(ContextCost.OfToolCalls(three) > ContextCost.OfToolCalls(one) * 2);
    }

    [Fact]
    public void Only_an_assistant_turn_can_have_asked_for_a_tool()
    {
        // A role check rather than a shape check: the column is only meaningful on an assistant
        // row, and reading it anywhere else would double-count a step that was already charged.
        var impostor = User("Hello") with
        {
            ToolCallsJson = Calls(new AIToolCall("call-1", "read_file", Arguments("a"))),
        };

        Assert.Equal(0, ContextCost.OfToolCalls(impostor));
    }

    [Fact]
    public void A_step_that_only_wrote_text_costs_nothing_in_tool_traffic()
    {
        Assert.Equal(0, ContextCost.OfToolCalls(Assistant("Just talking.")));
    }

    [Fact]
    public void What_one_message_costs_is_its_prose_and_its_calls_together()
    {
        var message = Assistant(
            "Writing it.",
            Calls(new AIToolCall("call-1", "write_file", Arguments(new string('x', 1_800)))));

        Assert.Equal(
            ContextCost.OfProse(message) + ContextCost.OfToolCalls(message),
            ContextCost.OfMessage(message));
    }

    [Fact]
    public void Folded_history_is_left_out_because_the_model_no_longer_sees_it()
    {
        var big = User(new string('x', 3_600));

        var before = ContextCost.OfHistory([big], systemPrompt: null);
        var after = ContextCost.OfHistory([big with { IsCompacted = true }], systemPrompt: null);

        // The row stays on disk so the transcript keeps showing it. Counting it would leave the
        // panel's bar unmoved after a fold, which is the one moment a user is watching it.
        Assert.True(after < before);
    }

    [Fact]
    public void A_failed_turn_is_left_out_because_the_builder_will_not_send_it()
    {
        var failed = User(new string('x', 3_600)) with { Status = MessageStatus.Failed };

        // Matching ContextBuilder.SelectHistory on purpose: a turn that is not sent is not part of
        // what fills the window, and disagreeing with the builder here is how a bar starts lying.
        Assert.Equal(
            ContextCost.OfHistory([], systemPrompt: null),
            ContextCost.OfHistory([failed], systemPrompt: null));
    }

    [Fact]
    public void The_system_prompt_is_part_of_what_fills_the_window()
    {
        var withPrompt = ContextCost.OfHistory([], "You are a careful assistant.");
        var without = ContextCost.OfHistory([], systemPrompt: null);

        // Sent on every request and charged on every request, which is why a long house prompt is
        // worth seeing in the panel's "other" band rather than hidden.
        Assert.True(withPrompt > without);
    }

    [Fact]
    public void The_billed_turn_is_the_newest_by_sequence_and_not_by_list_order()
    {
        var older = Billed(sequence: 1, input: 1_000);
        var newer = Billed(sequence: 2, input: 2_400);

        // Ordering is asserted against a reversed list on purpose: a projection that changes its
        // sort would otherwise pass here and report a stale prompt size in the panel.
        Assert.Equal(newer.Id, ContextCost.LastBilledTurn([newer, older])?.Id);
        Assert.Equal(newer.Id, ContextCost.LastBilledTurn([older, newer])?.Id);
    }

    [Fact]
    public void A_turn_still_streaming_has_not_said_what_it_cost()
    {
        var done = Billed(sequence: 1, input: 1_000);

        var inFlight = Billed(sequence: 2, input: null) with { Status = MessageStatus.Streaming };

        // Reading the in-flight row would show the panel dropping to zero the moment the user
        // pressed send, and recover only once the answer finished.
        Assert.Equal(done.Id, ContextCost.LastBilledTurn([done, inFlight])?.Id);
    }

    [Fact]
    public void A_turn_billed_for_output_alone_still_counts()
    {
        var output = Billed(sequence: 1, input: null, output: 120);

        // Some providers report only what they generated. Requiring both figures would leave the
        // panel blank for a whole family of models.
        Assert.Equal(output.Id, ContextCost.LastBilledTurn([output])?.Id);
    }

    [Fact]
    public void Nothing_is_reported_before_the_first_answer_arrives()
    {
        Assert.Null(ContextCost.LastBilledTurn([User("Hello")]));
    }

    [Fact]
    public void Fullness_believes_the_provider_when_it_reports_more_than_the_estimate()
    {
        // A short chat that was billed for 8 000: tool schemas and images are part of a request and
        // invisible to a local estimator, so the bill is the larger and truer number.
        var chat = Chat(null, User("Hi"), Billed(sequence: 2, input: 8_000, output: 0));

        Assert.Equal(80, ContextCost.Fullness(chat, 10_000)!.Value, precision: 0);
    }

    [Fact]
    public void Fullness_believes_the_estimate_when_messages_have_arrived_since_the_last_bill()
    {
        // The bill describes the request before these were added. Trusting it would let a chat sail
        // past its window on the strength of a stale figure.
        var chat = Chat(
            null,
            Billed(sequence: 1, input: 100, output: 10),
            User(new string('x', 36_000)));

        Assert.True(ContextCost.Fullness(chat, 10_000) > 90);
    }

    [Fact]
    public void Reasoning_is_left_out_of_the_billed_figure()
    {
        var chat = Chat(
            null,
            Billed(sequence: 1, input: 500, output: 100) with { ReasoningTokens = 400 });

        // Providers report it as a slice of the completion. Adding it would fill a window on paper
        // that the model still has room in, and compact a chat that did not need it.
        Assert.Equal(60, ContextCost.Fullness(chat, 1_000)!.Value, precision: 0);
    }

    [Fact]
    public void Cached_reads_count_because_they_are_still_occupying_the_window()
    {
        var chat = Chat(null, Billed(sequence: 1, input: 400, output: 100) with { CacheReadTokens = 7_500 });

        // A discount on the price is not a discount on the space: 73k of cached prompt is 73k the
        // next message has to fit alongside.
        Assert.Equal(80, ContextCost.Fullness(chat, 10_000)!.Value, precision: 0);
    }

    [Fact]
    public void An_unpublished_window_reports_nothing_rather_than_guessing_one()
    {
        var chat = Chat(null, Billed(sequence: 1, input: 8_000, output: 100));

        Assert.Null(ContextCost.Fullness(chat, null));
        Assert.Null(ContextCost.Fullness(chat, 0));
    }

    private static readonly Guid ConversationId = Guid.CreateVersion7();

    private static MessageDto User(string content, params AttachmentDto[] attachments) =>
        Message(MessageRole.User, content) with { Attachments = attachments };

    private static MessageDto Assistant(string content, string? toolCallsJson = null) =>
        Message(MessageRole.Assistant, content) with { ToolCallsJson = toolCallsJson };

    /// <summary>A completed assistant turn a provider charged for.</summary>
    private static MessageDto Billed(int sequence, int? input, int? output = null) =>
        Message(MessageRole.Assistant, "Answered.") with
        {
            SequenceNumber = sequence,
            InputTokens = input,
            OutputTokens = output,
        };

    private static MessageDto Message(MessageRole role, string content) => new()
    {
        Id = Guid.CreateVersion7(),
        ConversationId = ConversationId,
        Role = role,
        Content = content,
    };

    private static ConversationDetail Chat(string? systemPrompt, params MessageDto[] messages) => new()
    {
        Id = ConversationId,
        Title = "Chat",
        SystemPrompt = systemPrompt,
        Messages = messages,
    };

    private static AttachmentDto Attachment(string text) => new()
    {
        Id = Guid.CreateVersion7(),
        FileName = "Program.cs",
        MimeType = "text/plain",
        Size = text.Length,
        TextContent = text,
    };

    private static string Calls(params AIToolCall[] calls) => AgentTranscript.Write(calls);

    /// <summary>A write_file argument object, so the cost being measured is the payload.</summary>
    private static string Arguments(string content) =>
        $$"""{"path":"app.config","content":"{{content}}"}""";
}
