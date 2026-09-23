namespace AIClient.Application.Interfaces;

/// <summary>
/// A stretch of heard speech, while it is still being refined or once it has settled.
/// </summary>
/// <remarks>
/// One class for both degrees of confidence rather than two, because the consumer treats them
/// the same way - it composes text - and only the guarantee differs: a partial may be replaced
/// by a better guess a moment later, a final will never be revisited.
/// </remarks>
public sealed class SpeechTranscriptEventArgs : EventArgs
{
    /// <summary>The words heard in this segment so far. Empty means silence, never an error.</summary>
    public required string Text { get; init; }
}

/// <summary>Why a dictation session could not go on.</summary>
public sealed class SpeechErrorEventArgs : EventArgs
{
    /// <summary>A technical description of what went wrong, fit for a log line and a banner.</summary>
    public required string Message { get; init; }
}

/// <summary>
/// Turns speech from the microphone into text, one session at a time.
/// </summary>
/// <remarks>
/// <para>
/// The interface is deliberately silent about engines: whether the words come from a local
/// recognizer, an on-device neural model or a cloud service is the implementation's business,
/// and swapping one for another must not touch the composer. What the contract fixes is the
/// rhythm every engine shares:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="PartialTranscript"/> events are guesses and arrive often; each describes the current segment as heard so far and replaces the previous guess.</description></item>
/// <item><description><see cref="FinalTranscript"/> events are settled segments and arrive rarely; they are never revised, so the consumer is free to commit them.</description></item>
/// <item><description><see cref="IsListening"/> flips exactly once per successful <see cref="StartAsync"/> and <see cref="StopAsync"/>, and every flip is announced by <see cref="IsListeningChanged"/>.</description></item>
/// </list>
/// <para>
/// Events arrive on whatever thread the engine uses. The subscribing layer owns the hop back
/// to its own context - for WPF, that is the dispatcher - because a speech engine has no
/// reason to know which framework is listening to it.
/// </para>
/// </remarks>
public interface ISpeechToTextService
{
    /// <summary>Whether this machine can dictate at all. Read once at composition time.</summary>
    bool IsAvailable { get; }

    /// <summary>Whether a session is open right now.</summary>
    bool IsListening { get; }

    /// <summary>Raised whenever <see cref="IsListening"/> changes. May arrive on any thread.</summary>
    event EventHandler? IsListeningChanged;

    /// <summary>The engine's current best guess for the segment it is still hearing. May arrive on any thread.</summary>
    event EventHandler<SpeechTranscriptEventArgs>? PartialTranscript;

    /// <summary>A segment the engine will not revisit. May arrive on any thread.</summary>
    event EventHandler<SpeechTranscriptEventArgs>? FinalTranscript;

    /// <summary>
    /// Raised when a session fails, with the session already closed. May arrive on any thread.
    /// The message is technical on purpose: where the words failed to come from is exactly the
    /// detail that makes a "dictation failed" banner diagnosable.
    /// </summary>
    event EventHandler<SpeechErrorEventArgs>? Failed;

    /// <summary>
    /// Opens a session and starts listening. When the returned task completes, the engine is
    /// running and events may already be flowing.
    /// </summary>
    /// <remarks>
    /// Fails by raising <see cref="Failed"/> rather than by throwing, for the same reason the
    /// rest of the application reports failures where they happen: the caller is a button, and
    /// a button that throws has nowhere to put the exception.
    /// </remarks>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the session and releases the microphone. Safe to call when nothing is open,
    /// which is what lets the caller stop caring which end of the toggle it is on.
    /// </summary>
    Task StopAsync();
}
