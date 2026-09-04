namespace AIClient.Domain.Models;

/// <summary>
/// One message as sent to a provider. Deliberately separate from the persisted
/// <see cref="Entities.Message"/>: the wire shape must be free to change (attachments
/// inlined, history trimmed, tool calls added) without touching the database schema.
/// </summary>
/// <remarks>
/// Four roles, and the last two only ever appear in pairs. An <c>assistant</c> message with
/// <see cref="ToolCalls"/> must be followed by one <c>tool</c> message per call, each carrying
/// the matching <see cref="ToolCallId"/>; a provider handed the first without the second
/// returns 400 rather than continuing. That pairing is a property of the transcript, so it is
/// enforced where the transcript is built rather than here.
/// </remarks>
/// <param name="Role">Wire role: <c>system</c>, <c>user</c>, <c>assistant</c> or <c>tool</c>.</param>
/// <param name="Content">
/// Final text, with any attachment content already inlined. Empty on an assistant message
/// that only calls tools, which is the common case - the model announces nothing and acts.
/// </param>
public sealed record AIChatMessage(string Role, string Content)
{
    /// <summary>
    /// Tools this assistant turn decided to call. Empty on every other role.
    /// </summary>
    public IReadOnlyList<AIToolCall> ToolCalls { get; init; } = [];

    /// <summary>
    /// On a <c>tool</c> message, the <see cref="AIToolCall.Id"/> being answered. Null elsewhere.
    /// </summary>
    public string? ToolCallId { get; init; }

    /// <summary>
    /// On a <c>tool</c> message, the tool that produced the content. Null elsewhere. Optional in
    /// the protocol but sent anyway: it is what makes a saved transcript readable without
    /// resolving ids by hand.
    /// </summary>
    public string? Name { get; init; }

    public static AIChatMessage System(string content) => new("system", content);

    public static AIChatMessage User(string content) => new("user", content);

    public static AIChatMessage Assistant(string content) => new("assistant", content);

    /// <summary>The assistant's decision to act, with whatever text preceded it.</summary>
    public static AIChatMessage Assistant(string content, IReadOnlyList<AIToolCall> toolCalls) =>
        new("assistant", content) { ToolCalls = toolCalls };

    /// <summary>One tool's answer, addressed to the call that asked for it.</summary>
    public static AIChatMessage Tool(string toolCallId, string name, string content) =>
        new("tool", content) { ToolCallId = toolCallId, Name = name };
}
