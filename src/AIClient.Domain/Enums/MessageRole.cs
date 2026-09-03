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

    /// <summary>Reserved for future tool/function-calling support. Not produced in the MVP.</summary>
    Tool = 3,
}
