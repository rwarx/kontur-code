namespace AIClient.Domain.Enums;

/// <summary>
/// Author of a single message inside a conversation.
/// Stored in SQLite as an <see cref="int"/>; values are therefore stable and must never be renumbered.
/// </summary>
public enum MessageRole
{
    /// <summary>Instruction prepended to the conversation. Never displayed as a chat bubble.</summary>
    System = 0,

    /// <summary>Written by the human.</summary>
    User = 1,

    /// <summary>Produced by the model.</summary>
    Assistant = 2,

    /// <summary>
    /// The answer a tool gave to a call the assistant made. Written by the agent loop, and never
    /// by a person.
    /// </summary>
    /// <remarks>
    /// A row with this role is only meaningful next to the assistant row that asked for it: it
    /// carries <see cref="Entities.Message.ToolCallId"/>, and a provider handed one without the
    /// matching assistant call - or an assistant call without its answers - returns 400 rather
    /// than continuing. The transcript therefore stores both halves or neither.
    /// </remarks>
    Tool = 3,
}
