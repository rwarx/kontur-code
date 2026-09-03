using System.Text.Json;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Application.Services;
using AIClient.Domain.Enums;

namespace AIClient.Tests;

/// <summary>
/// Section 25: exporting a conversation to Markdown, JSON and plain text.
/// </summary>
/// <remarks>
/// The service is pure string work - the caller owns the save dialog - so the whole feature is
/// testable without touching the file system. The JSON assertions parse the output rather than
/// matching text, because the point of a published schema is that it can be read back.
/// </remarks>
public sealed class ExportServiceTests
{
    private readonly ExportService _service = new();

    [Fact]
    public void Markdown_carries_the_title_the_roles_and_the_bodies()
    {
        var markdown = _service.Export(Sample(), ExportFormat.Markdown);

        Assert.Contains("# Compiler question", markdown, StringComparison.Ordinal);
        Assert.Contains("## User", markdown, StringComparison.Ordinal);
        Assert.Contains("## Assistant", markdown, StringComparison.Ordinal);
        Assert.Contains("Why does this not compile?", markdown, StringComparison.Ordinal);
        Assert.Contains("Because the type is nullable.", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_keeps_a_fenced_code_block_intact()
    {
        // The export is meant to be pasted into a document or an issue. Escaping or
        // re-indenting a code fence would break the one thing people export chats for.
        var conversation = Sample(assistantContent: "Try:\n\n```csharp\nvar x = 1;\n```\n");

        var markdown = _service.Export(conversation, ExportFormat.Markdown);

        Assert.Contains("```csharp\nvar x = 1;\n```", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_names_the_attached_files()
    {
        var markdown = _service.Export(Sample(withAttachment: true), ExportFormat.Markdown);

        Assert.Contains("**Attachments:**", markdown, StringComparison.Ordinal);
        Assert.Contains("`Widget.cs`", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_system_prompt_is_configuration_and_is_not_exported_as_a_turn()
    {
        var conversation = Sample(extra:
        [
            Message(MessageRole.System, "You are a terse assistant.", sequence: 99),
        ]);

        var markdown = _service.Export(conversation, ExportFormat.Markdown);

        Assert.DoesNotContain("## System", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("terse assistant", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_placeholder_from_an_interrupted_turn_is_skipped()
    {
        // A conversation closed mid-stream leaves a blank assistant row. Exporting an empty
        // "## Assistant" heading would look like data loss.
        var conversation = Sample(extra:
        [
            Message(MessageRole.Assistant, "", sequence: 2, status: MessageStatus.Streaming),
        ]);

        var markdown = _service.Export(conversation, ExportFormat.Markdown);

        Assert.Equal(2, Occurrences(markdown, "## "));
    }

    [Fact]
    public void A_failed_turn_is_exported_with_its_error()
    {
        var conversation = Sample(extra:
        [
            Message(
                MessageRole.Assistant,
                "Partial answ",
                sequence: 2,
                status: MessageStatus.Failed,
                error: "OpenRouter is rate-limiting this key."),
        ]);

        var markdown = _service.Export(conversation, ExportFormat.Markdown);

        Assert.Contains("**Error:** OpenRouter is rate-limiting this key.", markdown, StringComparison.Ordinal);
        Assert.Contains("Partial answ", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stopped_turn_says_so_rather_than_looking_truncated()
    {
        var conversation = Sample(extra:
        [
            Message(MessageRole.Assistant, "Half an answer", sequence: 2, status: MessageStatus.Cancelled),
        ]);

        var markdown = _service.Export(conversation, ExportFormat.Markdown);

        Assert.Contains("Generation was stopped", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_is_valid_and_carries_a_versioned_schema_marker()
    {
        var json = _service.Export(Sample(withAttachment: true), ExportFormat.Json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // A version in the file is what makes an importer possible later without guessing.
        Assert.Equal("aiclient.conversation/v1", root.GetProperty("schema").GetString());
        Assert.Equal("Compiler question", root.GetProperty("conversation").GetProperty("title").GetString());

        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());

        var attachments = messages[0].GetProperty("attachments");
        Assert.Equal("Widget.cs", attachments[0].GetProperty("FileName").GetString());
    }

    [Fact]
    public void Json_omits_status_for_a_message_that_completed_normally()
    {
        // WhenWritingNull plus a null for Complete: the common case stays uncluttered and
        // the presence of the field means something went wrong.
        var json = _service.Export(Sample(), ExportFormat.Json);

        using var document = JsonDocument.Parse(json);
        var message = document.RootElement.GetProperty("messages")[1];

        Assert.False(message.TryGetProperty("status", out _));
        Assert.False(message.TryGetProperty("attachments", out _));
    }

    [Fact]
    public void Json_does_not_escape_non_Latin_text_into_unreadable_sequences()
    {
        // UnsafeRelaxedJsonEscaping is deliberate: an exported file is read by people, and
        // \u041f\u0440\u0438 is not something anyone wants to see in their own transcript.
        var json = _service.Export(Sample(assistantContent: "Привет — это ответ."), ExportFormat.Json);

        Assert.Contains("Привет — это ответ.", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_text_labels_each_turn_and_separates_them()
    {
        var text = _service.Export(Sample(withAttachment: true), ExportFormat.PlainText);

        Assert.StartsWith("Compiler question", text, StringComparison.Ordinal);
        Assert.Contains("User  [", text, StringComparison.Ordinal);
        Assert.Contains("Assistant  [", text, StringComparison.Ordinal);
        Assert.Contains("[attachment] Widget.cs", text, StringComparison.Ordinal);
        Assert.Contains(new string('-', 60), text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_format_is_rejected_rather_than_silently_defaulted()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _service.Export(Sample(), (ExportFormat)42));
    }

    [Theory]
    [InlineData(ExportFormat.Markdown, ".md")]
    [InlineData(ExportFormat.Json, ".json")]
    [InlineData(ExportFormat.PlainText, ".txt")]
    public void The_suggested_file_name_matches_the_format(ExportFormat format, string extension)
    {
        var name = _service.SuggestFileName(Sample(), format);

        Assert.EndsWith(extension, name, StringComparison.Ordinal);
        Assert.Contains("Compiler_question", name, StringComparison.Ordinal);
        Assert.Contains(_createdAt.LocalDateTime.ToString("yyyy-MM-dd"), name, StringComparison.Ordinal);
    }

    [Fact]
    public void A_title_full_of_illegal_characters_still_yields_a_usable_file_name()
    {
        // Titles come from user text and from the auto-titler, which happily produces
        // "C:/path?" or a trailing ellipsis. Windows rejects both.
        var name = _service.SuggestFileName(Sample(title: "Why does C:\\src\\*.cs fail?…"), ExportFormat.Markdown);

        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain('*', name);
        Assert.DoesNotContain('?', name);
        Assert.DoesNotContain('…', name);
        Assert.Equal(name, Path.GetFileName(name));
    }

    [Fact]
    public void A_title_that_sanitises_to_nothing_falls_back_to_a_default()
    {
        var name = _service.SuggestFileName(Sample(title: "???"), ExportFormat.Json);

        Assert.StartsWith("conversation_", name, StringComparison.Ordinal);
    }

    [Fact]
    public void A_very_long_title_is_capped_so_the_path_stays_valid()
    {
        var name = _service.SuggestFileName(Sample(title: new string('t', 300)), ExportFormat.Markdown);

        Assert.True(name.Length < 100, $"File name was {name.Length} characters long.");
    }

    [Theory]
    [InlineData(ExportFormat.Markdown, "*.md")]
    [InlineData(ExportFormat.Json, "*.json")]
    [InlineData(ExportFormat.PlainText, "*.txt")]
    public void The_dialog_filter_offers_the_format_and_a_fallback(ExportFormat format, string pattern)
    {
        var filter = _service.GetFileDialogFilter(format);

        Assert.Contains(pattern, filter, StringComparison.Ordinal);
        Assert.Contains("All files (*.*)|*.*", filter, StringComparison.Ordinal);
    }

    private readonly DateTimeOffset _createdAt = new(2026, 5, 17, 9, 30, 0, TimeSpan.Zero);
    private readonly Guid _conversationId = Guid.CreateVersion7();

    private ConversationDetail Sample(
        string title = "Compiler question",
        string assistantContent = "Because the type is nullable.",
        bool withAttachment = false,
        IReadOnlyList<MessageDto>? extra = null)
    {
        var messages = new List<MessageDto>
        {
            Message(MessageRole.User, "Why does this not compile?", sequence: 0, withAttachment: withAttachment),
            Message(MessageRole.Assistant, assistantContent, sequence: 1),
        };

        if (extra is not null)
        {
            messages.AddRange(extra);
        }

        return new ConversationDetail
        {
            Id = _conversationId,
            Title = title,
            ProviderId = "openrouter",
            ModelId = "openai/gpt-5-mini",
            CreatedAt = _createdAt,
            UpdatedAt = _createdAt.AddMinutes(2),
            Messages = messages,
        };
    }

    private MessageDto Message(
        MessageRole role,
        string content,
        int sequence,
        MessageStatus status = MessageStatus.Complete,
        string? error = null,
        bool withAttachment = false) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ConversationId = _conversationId,
            Role = role,
            Content = content,
            Status = status,
            ErrorMessage = error,
            SequenceNumber = sequence,
            CreatedAt = _createdAt.AddSeconds(sequence),
            ProviderId = "openrouter",
            ModelId = "openai/gpt-5-mini",
            Attachments = withAttachment
                ?
                [
                    new AttachmentDto
                    {
                        Id = Guid.CreateVersion7(),
                        FileName = "Widget.cs",
                        MimeType = "text/plain",
                        Size = 128,
                    },
                ]
                : [],
        };

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
