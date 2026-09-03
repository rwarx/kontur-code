namespace AIClient.Domain.Enums;

/// <summary>
/// Lifecycle of an assistant message. Persisted so that a message that was still
/// streaming when the app was killed is not silently shown as a complete answer.
/// </summary>
public enum MessageStatus
{
    /// <summary>Fully received, or a user message (which is complete the moment it is created).</summary>
    Complete = 0,

    /// <summary>Tokens are still arriving. Only ever transient in the UI; persisted only if the app crashed mid-stream.</summary>
    Streaming = 1,

    /// <summary>The user pressed Stop. Partial content is kept and remains usable as context.</summary>
    Cancelled = 2,

    /// <summary>The provider or transport failed. <see cref="Entities.Message.ErrorMessage"/> carries the reason.</summary>
    Failed = 3,
}
