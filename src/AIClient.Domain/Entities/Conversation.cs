namespace AIClient.Domain.Entities;

/// <summary>
/// A chat session. Survives application restarts; this is the unit the sidebar lists.
/// </summary>
public sealed class Conversation
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>
    /// Shown in the sidebar. Auto-derived from the first user message unless
    /// <see cref="IsTitleUserDefined"/> is set by an explicit rename.
    /// </summary>
    public required string Title { get; set; }

    /// <summary>True once the user renames the chat, which stops auto-titling from overwriting it.</summary>
    public bool IsTitleUserDefined { get; set; }

    /// <summary>Provider selected for the next message. May be null for an empty chat.</summary>
    public string? ProviderId { get; set; }

    /// <summary>Provider-native model id for the next message, e.g. <c>openai/gpt-4o</c>.</summary>
    public string? ModelId { get; set; }

    /// <summary>Per-conversation system prompt. Null falls back to the global chat settings.</summary>
    public string? SystemPrompt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Bumped on every message. Drives sidebar ordering and the Today/Yesterday grouping.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Pinned conversations sort above everything else.</summary>
    public bool IsPinned { get; set; }

    public ICollection<Message> Messages { get; set; } = [];
}
