using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Domain.Enums;

namespace AIClient.Application.Services;

/// <summary>
/// Renders a conversation to Markdown, JSON or plain text. Pure string work: the caller
/// owns the file dialog and the write, which keeps this testable without a file system.
/// </summary>
public sealed class ExportService : IExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Export(ConversationDetail conversation, ExportFormat format) => format switch
    {
        ExportFormat.Markdown => ExportMarkdown(conversation),
        ExportFormat.Json => ExportJson(conversation),
        ExportFormat.PlainText => ExportPlainText(conversation),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported export format."),
    };

    public string GetFileDialogFilter(ExportFormat format) => format switch
    {
        ExportFormat.Markdown => "Markdown (*.md)|*.md|All files (*.*)|*.*",
        ExportFormat.Json => "JSON (*.json)|*.json|All files (*.*)|*.*",
        ExportFormat.PlainText => "Text file (*.txt)|*.txt|All files (*.*)|*.*",
        _ => "All files (*.*)|*.*",
    };

    public string SuggestFileName(ConversationDetail conversation, ExportFormat format)
    {
        var extension = format switch
        {
            ExportFormat.Markdown => ".md",
            ExportFormat.Json => ".json",
            _ => ".txt",
        };

        var name = Sanitize(conversation.Title);
        if (name.Length == 0)
        {
            name = "conversation";
        }

        return $"{name}_{conversation.CreatedAt.LocalDateTime:yyyy-MM-dd}{extension}";
    }

    private static string ExportMarkdown(ConversationDetail conversation)
    {
        var sb = new StringBuilder();

        sb.Append("# ").AppendLine(conversation.Title).AppendLine();
        sb.Append("> Exported ").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm")).AppendLine("  ");
        sb.Append("> Created ").Append(conversation.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm")).AppendLine("  ");

        if (conversation.ModelId is not null)
        {
            sb.Append("> Model `").Append(conversation.ModelId).AppendLine("`  ");
        }

        sb.AppendLine().AppendLine("---").AppendLine();

        foreach (var message in Renderable(conversation))
        {
            sb.Append("## ").AppendLine(RoleLabel(message.Role));

            if (message.Attachments.Count > 0)
            {
                sb.Append("**Attachments:** ")
                  .AppendLine(string.Join(", ", message.Attachments.Select(a => $"`{a.FileName}`")))
                  .AppendLine();
            }

            sb.AppendLine(message.Content.TrimEnd()).AppendLine();

            if (message.Status == MessageStatus.Failed && message.ErrorMessage is not null)
            {
                sb.Append("> **Error:** ").AppendLine(message.ErrorMessage).AppendLine();
            }
            else if (message.Status == MessageStatus.Cancelled)
            {
                sb.AppendLine("> *Generation was stopped.*").AppendLine();
            }

            sb.AppendLine("---").AppendLine();
        }

        return sb.ToString();
    }

    private static string ExportJson(ConversationDetail conversation)
    {
        // A dedicated shape rather than the DTO: an export file is a published format and
        // must not shift every time an internal DTO gains a field.
        var payload = new
        {
            schema = "aiclient.conversation/v1",
            exportedAt = DateTimeOffset.Now,
            conversation = new
            {
                id = conversation.Id,
                title = conversation.Title,
                createdAt = conversation.CreatedAt,
                updatedAt = conversation.UpdatedAt,
                providerId = conversation.ProviderId,
                modelId = conversation.ModelId,
                systemPrompt = conversation.SystemPrompt,
            },
            messages = Renderable(conversation).Select(m => new
            {
                id = m.Id,
                role = m.Role.ToString().ToLowerInvariant(),
                content = m.Content,
                createdAt = m.CreatedAt,
                model = m.ModelId,
                provider = m.ProviderId,
                inputTokens = m.InputTokens,
                outputTokens = m.OutputTokens,
                status = m.Status == MessageStatus.Complete ? null : m.Status.ToString().ToLowerInvariant(),
                error = m.ErrorMessage,
                attachments = m.Attachments.Count == 0
                    ? null
                    : m.Attachments.Select(a => new { a.FileName, a.MimeType, a.Size }).ToArray(),
            }).ToArray(),
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string ExportPlainText(ConversationDetail conversation)
    {
        var sb = new StringBuilder();

        sb.AppendLine(conversation.Title);
        sb.AppendLine(new string('=', Math.Min(conversation.Title.Length, 60)));
        sb.AppendLine();

        foreach (var message in Renderable(conversation))
        {
            sb.Append(RoleLabel(message.Role))
              .Append("  [")
              .Append(message.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"))
              .AppendLine("]");

            sb.AppendLine(new string('-', 60));

            foreach (var attachment in message.Attachments)
            {
                sb.Append("[attachment] ").AppendLine(attachment.FileName);
            }

            sb.AppendLine(message.Content.TrimEnd());

            if (message.Status == MessageStatus.Failed && message.ErrorMessage is not null)
            {
                sb.Append("[error] ").AppendLine(message.ErrorMessage);
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Messages worth exporting: system turns are configuration rather than conversation,
    /// and an empty placeholder from an interrupted turn is noise.
    /// </summary>
    private static IEnumerable<MessageDto> Renderable(ConversationDetail conversation) =>
        conversation.Messages
            .Where(m => m.Role is MessageRole.User or MessageRole.Assistant)
            .Where(m => !string.IsNullOrWhiteSpace(m.Content) || m.ErrorMessage is not null)
            .OrderBy(m => m.SequenceNumber)
            .ThenBy(m => m.CreatedAt);

    private static string RoleLabel(MessageRole role) => role switch
    {
        MessageRole.User => "User",
        MessageRole.Assistant => "Assistant",
        MessageRole.System => "System",
        _ => role.ToString(),
    };

    /// <summary>Strips characters Windows rejects in a file name and collapses whitespace.</summary>
    private static string Sanitize(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(title.Length);

        foreach (var c in title)
        {
            if (Array.IndexOf(invalid, c) >= 0 || c == '…')
            {
                continue;
            }

            sb.Append(char.IsWhiteSpace(c) ? '_' : c);
        }

        var result = sb.ToString().Trim('_', '.', ' ');
        return result.Length > 60 ? result[..60].TrimEnd('_') : result;
    }
}
