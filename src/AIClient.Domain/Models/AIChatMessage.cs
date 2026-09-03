namespace AIClient.Domain.Models;

/// <summary>
/// One message as sent to a provider. Deliberately separate from the persisted
/// <see cref="Entities.Message"/>: the wire shape must be free to change (attachments
/// inlined, history trimmed, tool calls added) without touching the database schema.
/// </summary>
/// <param name="Role">Wire role: <c>system</c>, <c>user</c>, <c>assistant</c> or <c>tool</c>.</param>
/// <param name="Content">Final text, with any attachment content already inlined.</param>
public sealed record AIChatMessage(string Role, string Content)
{
    public static AIChatMessage System(string content) => new("system", content);
    public static AIChatMessage User(string content) => new("user", content);
    public static AIChatMessage Assistant(string content) => new("assistant", content);
}
