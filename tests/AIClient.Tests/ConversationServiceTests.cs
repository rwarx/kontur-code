using AIClient.Application.DTOs;
using AIClient.Domain.Enums;
using AIClient.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace AIClient.Tests;

/// <summary>
/// Conversation and message persistence: the behaviour behind New Chat, Rename, Delete,
/// Search, Edit and Regenerate.
/// </summary>
public sealed class ConversationServiceTests : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await TestDatabase.CreateAsync();

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task A_new_chat_gets_a_placeholder_title_that_auto_titling_may_replace()
    {
        var service = _db.Conversations();

        var created = await service.CreateAsync();
        var detail = await service.GetAsync(created.Id);

        Assert.Equal("New Chat", created.Title);
        Assert.False(detail!.IsTitleUserDefined);
    }

    [Fact]
    public async Task An_explicit_title_is_marked_user_defined_so_auto_titling_leaves_it_alone()
    {
        var service = _db.Conversations();

        var created = await service.CreateAsync("  Deployment notes  ");
        var detail = await service.GetAsync(created.Id);

        Assert.Equal("Deployment notes", created.Title);
        Assert.True(detail!.IsTitleUserDefined);
    }

    [Fact]
    public async Task Renaming_marks_the_title_user_defined()
    {
        var service = _db.Conversations();
        var created = await service.CreateAsync();

        await service.RenameAsync(created.Id, "  Release checklist ");

        var detail = await service.GetAsync(created.Id);

        Assert.Equal("Release checklist", detail!.Title);
        Assert.True(detail.IsTitleUserDefined);
    }

    [Fact]
    public async Task Messages_are_numbered_consecutively_and_read_back_in_order()
    {
        var service = _db.Conversations();
        var chat = await service.CreateAsync();

        await service.AddMessageAsync(chat.Id, User("first"));
        await service.AddMessageAsync(chat.Id, Assistant("second"));
        await service.AddMessageAsync(chat.Id, User("third"));

        var detail = await service.GetAsync(chat.Id);

        Assert.Equal([0, 1, 2], detail!.Messages.Select(m => m.SequenceNumber));
        Assert.Equal(["first", "second", "third"], detail.Messages.Select(m => m.Content));
    }

    [Fact]
    public async Task Sequence_numbers_stay_unique_after_a_message_is_deleted_from_the_middle()
    {
        // The ordinal comes from MAX(SequenceNumber), not from the row count. With a hole in
        // the middle - which Edit and Delete both create - counting would hand out an ordinal
        // that already exists and two messages would collide in the transcript.
        var service = _db.Conversations();
        var chat = await service.CreateAsync();

        await service.AddMessageAsync(chat.Id, User("zero"));
        var middle = await service.AddMessageAsync(chat.Id, Assistant("one"));
        await service.AddMessageAsync(chat.Id, User("two"));

        await service.DeleteMessageAsync(middle.Id);

        var appended = await service.AddMessageAsync(chat.Id, Assistant("three"));

        Assert.Equal(3, appended.SequenceNumber);

        var detail = await service.GetAsync(chat.Id);
        Assert.Equal([0, 2, 3], detail!.Messages.Select(m => m.SequenceNumber));
    }

    [Fact]
    public async Task Adding_a_message_bumps_the_conversation_timestamp()
    {
        var service = _db.Conversations();
        var chat = await service.CreateAsync();

        await TouchAsync(chat.Id, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));

        await service.AddMessageAsync(chat.Id, User("bump"));

        var detail = await service.GetAsync(chat.Id);

        // The sidebar orders by this; a chat that just received a message has to float up.
        Assert.True(detail!.UpdatedAt.Year >= 2026);
    }

    [Fact]
    public async Task Pinned_chats_sort_above_more_recent_unpinned_ones()
    {
        var service = _db.Conversations();

        var pinned = await service.CreateAsync("Pinned");
        var recent = await service.CreateAsync("Recent");

        await TouchAsync(pinned.Id, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await TouchAsync(recent.Id, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        await service.SetPinnedAsync(pinned.Id, true);

        var summaries = await service.GetSummariesAsync();

        Assert.Equal(["Pinned", "Recent"], summaries.Select(s => s.Title));
    }

    [Fact]
    public async Task The_preview_is_the_latest_message_collapsed_to_one_line()
    {
        var service = _db.Conversations();
        var chat = await service.CreateAsync();

        await service.AddMessageAsync(chat.Id, User("question"));
        await service.AddMessageAsync(chat.Id, Assistant("  first line\nsecond line  "));

        var summary = Assert.Single(await service.GetSummariesAsync());

        Assert.Equal("first line", summary.Preview);
        Assert.Equal(2, summary.MessageCount);
    }

    [Fact]
    public async Task A_long_preview_is_truncated_with_an_ellipsis()
    {
        var service = _db.Conversations();
        var chat = await service.CreateAsync();

        await service.AddMessageAsync(chat.Id, Assistant(new string('a', 150)));

        var summary = Assert.Single(await service.GetSummariesAsync());

        Assert.Equal(101, summary.Preview!.Length);
        Assert.EndsWith("…", summary.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paging_returns_a_window_rather_than_the_whole_table()
    {
        var service = _db.Conversations();

        for (var i = 0; i < 5; i++)
        {
            var chat = await service.CreateAsync($"Chat {i}");
            await TouchAsync(chat.Id, new DateTimeOffset(2026, 1, 1 + i, 0, 0, 0, TimeSpan.Zero));
        }

        var page = await service.GetSummariesAsync(skip: 1, take: 2);

        // Newest first, so skipping one lands on Chat 3.
        Assert.Equal(["Chat 3", "Chat 2"], page.Select(s => s.Title));
    }

    [Fact]
    public async Task Search_matches_both_titles_and_message_bodies()
    {
        var service = _db.Conversations();

        var byTitle = await service.CreateAsync("Kubernetes rollout");
        var byBody = await service.CreateAsync("Unrelated title");
        await service.AddMessageAsync(byBody.Id, User("How do I debug a kubernetes pod?"));
        await service.CreateAsync("Nothing to do with it");

        var results = await service.SearchAsync("kubernetes");

        Assert.Equal(2, results.Count);
        Assert.Contains(byTitle.Id, results.Select(r => r.Id));
        Assert.Contains(byBody.Id, results.Select(r => r.Id));
    }

    [Theory]
    [InlineData("100%", "100% coverage", "1000 coverage")]
    [InlineData("a_b", "a_b naming", "axb naming")]
    public async Task Search_treats_LIKE_wildcards_in_the_query_as_literal_characters(
        string query,
        string shouldMatch,
        string shouldNotMatch)
    {
        // Without escaping, "100%" matches anything starting with 100 and "a_b" matches "axb".
        // A user typing a percent sign is looking for a percent sign.
        var service = _db.Conversations();

        await service.CreateAsync(shouldMatch);
        await service.CreateAsync(shouldNotMatch);

        var results = await service.SearchAsync(query);

        var match = Assert.Single(results);
        Assert.Equal(shouldMatch, match.Title);
    }

    [Fact]
    public async Task A_blank_search_falls_back_to_the_full_list()
    {
        var service = _db.Conversations();
        await service.CreateAsync("One");
        await service.CreateAsync("Two");

        Assert.Equal(2, (await service.SearchAsync("   ")).Count);
    }

    [Fact]
    public async Task An_update_leaves_the_fields_it_does_not_mention_alone()
    {
        // This is what lets the streaming flush write only the accumulated text without
        // restating status and token counts a second apart.
        var service = _db.Conversations();
        var chat = await service.CreateAsync();

        var message = await service.AddMessageAsync(chat.Id, new NewMessage
        {
            Role = MessageRole.Assistant,
            Content = string.Empty,
            Status = MessageStatus.Streaming,
            ProviderId = "openrouter",
            ModelId = "openai/gpt-5-mini",
        });

        await service.UpdateMessageAsync(new MessageUpdate { MessageId = message.Id, Content = "partial" });

        var stored = Assert.Single((await service.GetAsync(chat.Id))!.Messages);

        Assert.Equal("partial", stored.Content);
        Assert.Equal(MessageStatus.Streaming, stored.Status);
        Assert.Equal("openai/gpt-5-mini", stored.ModelId);
    }

    [Fact]
    public async Task Completing_a_message_clears_the_error_from_a_previous_attempt()
    {
        var service = _db.Conversations();
        var chat = await service.CreateAsync();
        var message = await service.AddMessageAsync(chat.Id, Assistant(string.Empty));

        await service.UpdateMessageAsync(new MessageUpdate
        {
            MessageId = message.Id,
            Status = MessageStatus.Failed,
            ErrorKind = AIErrorKind.RateLimited,
            ErrorMessage = "Too many requests.",
        });

        await service.UpdateMessageAsync(new MessageUpdate
        {
            MessageId = message.Id,
            Content = "It worked this time.",
            Status = MessageStatus.Complete,
        });

        var stored = Assert.Single((await service.GetAsync(chat.Id))!.Messages);

        // A stale error banner under a successful answer would be worse than no banner.
        Assert.Equal(MessageStatus.Complete, stored.Status);
        Assert.Null(stored.ErrorMessage);
        Assert.Null(stored.ErrorKind);
    }

    [Fact]
    public async Task Updating_a_message_that_no_longer_exists_is_not_an_error()
    {
        // The user can delete a conversation while a turn is still streaming; the flush that
        // lands afterwards must not take the app down.
        var service = _db.Conversations();

        await service.UpdateMessageAsync(new MessageUpdate
        {
            MessageId = Guid.CreateVersion7(),
            Content = "orphan",
        });
    }

    [Fact]
    public async Task Regenerate_discards_the_answer_and_everything_after_it()
    {
        var service = _db.Conversations();
        var chat = await service.CreateAsync();

        await service.AddMessageAsync(chat.Id, User("first question"));
        var answer = await service.AddMessageAsync(chat.Id, Assistant("first answer"));
        await service.AddMessageAsync(chat.Id, User("follow-up"));

        await service.DeleteFromMessageAsync(answer.Id, inclusive: true);

        var detail = await service.GetAsync(chat.Id);

        Assert.Equal(["first question"], detail!.Messages.Select(m => m.Content));
    }

    [Fact]
    public async Task An_exclusive_delete_keeps_the_anchor_message()
    {
        // What Edit needs: the edited user message stays, the answers it produced do not.
        var service = _db.Conversations();
        var chat = await service.CreateAsync();

        var question = await service.AddMessageAsync(chat.Id, User("question"));
        await service.AddMessageAsync(chat.Id, Assistant("answer"));

        await service.DeleteFromMessageAsync(question.Id, inclusive: false);

        var detail = await service.GetAsync(chat.Id);

        Assert.Equal(["question"], detail!.Messages.Select(m => m.Content));
    }

    [Fact]
    public async Task Deleting_from_an_unknown_message_does_nothing()
    {
        var service = _db.Conversations();
        var chat = await service.CreateAsync();
        await service.AddMessageAsync(chat.Id, User("keep me"));

        await service.DeleteFromMessageAsync(Guid.CreateVersion7(), inclusive: true);

        Assert.Single((await service.GetAsync(chat.Id))!.Messages);
    }

    [Fact]
    public async Task Attachments_round_trip_with_their_message()
    {
        var service = _db.Conversations();
        var chat = await service.CreateAsync();

        await service.AddMessageAsync(chat.Id, new NewMessage
        {
            Role = MessageRole.User,
            Content = "What does this do?",
            Attachments =
            [
                new NewAttachment
                {
                    FileName = "Program.cs",
                    MimeType = "text/plain",
                    Size = 42,
                    TextContent = "Console.WriteLine(\"hi\");",
                    IsTruncated = true,
                },
            ],
        });

        var message = Assert.Single((await service.GetAsync(chat.Id))!.Messages);
        var attachment = Assert.Single(message.Attachments);

        Assert.Equal("Program.cs", attachment.FileName);
        Assert.Equal("Console.WriteLine(\"hi\");", attachment.TextContent);
        Assert.True(attachment.IsTruncated);
    }

    [Fact]
    public async Task An_unknown_conversation_reads_back_as_null_rather_than_throwing()
    {
        Assert.Null(await _db.Conversations().GetAsync(Guid.CreateVersion7()));
    }

    [Fact]
    public async Task Deleting_a_conversation_removes_it_from_the_list()
    {
        var service = _db.Conversations();
        var chat = await service.CreateAsync("Doomed");
        await service.AddMessageAsync(chat.Id, User("something"));

        await service.DeleteAsync(chat.Id);

        Assert.Empty(await service.GetSummariesAsync());
        Assert.Null(await service.GetAsync(chat.Id));
    }

    [Fact]
    public async Task An_auto_title_is_derived_from_the_first_user_message()
    {
        var service = _db.Conversations();
        var chat = await service.CreateAsync();

        await service.AddMessageAsync(chat.Id, User("Explain this WPF binding error, please."));
        await service.AddMessageAsync(chat.Id, Assistant("Sure."));

        var title = await service.TryApplyAutoTitleAsync(chat.Id);

        Assert.Equal("WPF binding error, please", title);
        Assert.Equal(title, (await service.GetAsync(chat.Id))!.Title);
    }

    [Fact]
    public async Task An_auto_title_never_overwrites_a_name_the_user_chose()
    {
        var service = _db.Conversations();
        var chat = await service.CreateAsync("My own name");

        await service.AddMessageAsync(chat.Id, User("Write a function that parses JSON."));

        Assert.Null(await service.TryApplyAutoTitleAsync(chat.Id));
        Assert.Equal("My own name", (await service.GetAsync(chat.Id))!.Title);
    }

    [Fact]
    public async Task Auto_titling_a_chat_with_no_user_message_yet_does_nothing()
    {
        var service = _db.Conversations();
        var chat = await service.CreateAsync();

        Assert.Null(await service.TryApplyAutoTitleAsync(chat.Id));
    }

    [Fact]
    public async Task Auto_titling_a_conversation_that_was_deleted_mid_turn_does_nothing()
    {
        Assert.Null(await _db.Conversations().TryApplyAutoTitleAsync(Guid.CreateVersion7()));
    }

    [Fact]
    public async Task Everything_survives_a_new_set_of_contexts()
    {
        // Section 39's closing requirement: close the app, reopen it, the conversation is
        // still there. Every call already uses its own context, so re-reading through fresh
        // ones is the same thing the next launch does.
        var chat = await _db.Conversations().CreateAsync("Persistent");
        await _db.Conversations().AddMessageAsync(chat.Id, User("still here?"));

        var reopened = await _db.Conversations().GetAsync(chat.Id);

        Assert.Equal("Persistent", reopened!.Title);
        Assert.Equal("still here?", Assert.Single(reopened.Messages).Content);
    }

    private static NewMessage User(string content) =>
        new() { Role = MessageRole.User, Content = content };

    private static NewMessage Assistant(string content) =>
        new() { Role = MessageRole.Assistant, Content = content };

    /// <summary>Forces a conversation's timestamp, so ordering tests do not race the clock.</summary>
    private async Task TouchAsync(Guid id, DateTimeOffset when)
    {
        await using var db = _db.CreateDbContext();

        await db.Conversations
            .Where(c => c.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, when));
    }
}
