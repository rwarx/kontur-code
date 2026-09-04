using AIClient.Application.DTOs;
using AIClient.Domain.Enums;
using AIClient.Domain.Models;

namespace AIClient.Application.Interfaces;

/// <summary>
/// Carries a task through as many model steps as it takes: call a tool, read the result, call
/// another, then answer.
/// </summary>
/// <remarks>
/// <para>
/// The difference from <see cref="IChatService"/> is the loop, and only the loop. A chat turn is one
/// request and one answer; a run is a request, the tool calls it produced, their results, another
/// request, and so on until the model answers with words or a budget stops it. Everything else - the
/// order of the writes, the shape of the events, the single terminal event - is deliberately the
/// same, because a conversation written by one and reopened by the other has to be one transcript.
/// </para>
/// <para>
/// Every step is committed before the next one is requested, tool results included. That is what
/// makes a run survivable: a crash halfway through leaves a conversation whose calls and answers are
/// paired, so it opens correctly and can be carried on with an ordinary message.
/// </para>
/// </remarks>
public interface IAgentService
{
    /// <summary>
    /// Sends a task and streams everything that happens while it is carried out.
    /// </summary>
    /// <remarks>
    /// The sequence always ends with exactly one of <see cref="AgentEvent.Completed"/>,
    /// <see cref="AgentEvent.Failed"/> or <see cref="AgentEvent.Cancelled"/>. Provider failures are
    /// reported rather than thrown, for the same reason as in chat: a failure can arrive after work
    /// the user can see and would rather keep.
    /// </remarks>
    IAsyncEnumerable<AgentEvent> RunAsync(AgentRunRequest request, CancellationToken cancellationToken);
}

/// <summary>Input for <see cref="IAgentService.RunAsync"/>.</summary>
public sealed record AgentRunRequest
{
    public required Guid ConversationId { get; init; }

    /// <summary>What the user asked for, in their own words.</summary>
    public required string Content { get; init; }

    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }

    public IReadOnlyList<NewAttachment> Attachments { get; init; } = [];
}

/// <summary>Why a run stopped, when it stopped without failing.</summary>
public enum AgentStopReason
{
    /// <summary>
    /// The model answered instead of calling another tool. The ordinary ending, and the only one
    /// that means the task was seen through.
    /// </summary>
    Answered,

    /// <summary>
    /// The step budget was spent.
    /// </summary>
    /// <remarks>
    /// The last permitted step is taken with tools withheld, so a run that hits this limit still ends
    /// in a sentence: the model is made to say what it found and what it did not finish, rather than
    /// leaving the user with a transcript whose last entry is a file listing.
    /// </remarks>
    StepLimit,

    /// <summary>
    /// The clock ran out.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="StepLimit"/> this gets no wrap-up turn, because a wrap-up turn takes time
    /// and time is the thing that has run out. The transcript can therefore end on a tool result,
    /// which reads correctly and continues correctly - it is simply less tidy.
    /// </remarks>
    TimeLimit,
}

/// <summary>What became of one tool call.</summary>
public enum AgentCallOutcome
{
    /// <summary>The tool ran and did what was asked.</summary>
    Succeeded,

    /// <summary>
    /// The call was made and did not work: bad arguments, a missing file, a refused path, a repeat.
    /// </summary>
    Failed,

    /// <summary>
    /// The user said no. Kept apart from <see cref="Failed"/> because it is not a mistake anyone
    /// made and should not be shown as one.
    /// </summary>
    Denied,
}

/// <summary>
/// What the ViewModel watches while a run happens.
/// </summary>
/// <remarks>
/// <para>
/// A closed hierarchy rather than one record with a kind flag, for the reason <c>AIStreamEvent</c>
/// gives: a new kind of thing an agent can do is then an additive change, and the compiler points at
/// every switch that has to account for it.
/// </para>
/// <para>
/// Tool calls are reported in three events rather than one because the interesting part is the gap
/// between them. <see cref="ToolCallProposed"/> is where the user is being asked;
/// <see cref="ToolCallStarted"/> means the answer was yes; <see cref="ToolCallFinished"/> carries
/// the persisted row. A card in the transcript is created by the first event that mentions a call id
/// and updated by the rest, so a call that never gets past the proposal still shows.
/// </para>
/// </remarks>
public abstract record AgentEvent
{
    /// <summary>The task has been persisted. Carries its assigned id.</summary>
    public sealed record UserMessageSaved(MessageDto Message) : AgentEvent;

    /// <summary>An auto-generated title was applied, so the sidebar can update in place.</summary>
    public sealed record TitleGenerated(Guid ConversationId, string Title) : AgentEvent;

    /// <summary>
    /// A new step has begun and its assistant row exists.
    /// </summary>
    /// <param name="Step">1-based, and counted against the step budget.</param>
    /// <param name="Message">The placeholder row that is about to receive tokens.</param>
    public sealed record StepStarted(int Step, MessageDto Message) : AgentEvent;

    /// <summary>A chunk of answer text. Delta only.</summary>
    public sealed record ContentDelta(Guid MessageId, string Text) : AgentEvent;

    /// <summary>
    /// A chunk of reasoning from a model that exposes it. Surfaced rather than dropped, because a run
    /// that spends a minute deciding what to read is otherwise a minute of nothing on screen.
    /// </summary>
    public sealed record ReasoningDelta(Guid MessageId, string Text) : AgentEvent;

    /// <summary>
    /// The model wants to make a call. Emitted before the user is asked, and before anything runs.
    /// </summary>
    /// <remarks>
    /// <see cref="AgentToolRisk.Read"/> calls are never put to the user, so for those this event and
    /// <see cref="ToolCallStarted"/> arrive together. A call naming a tool that does not exist is not
    /// proposed at all: there is nothing to propose, so it goes straight to
    /// <see cref="ToolCallFinished"/>.
    /// </remarks>
    public sealed record ToolCallProposed(Guid MessageId, AIToolCall Call, AgentToolRisk Risk) : AgentEvent;

    /// <summary>The call was allowed and the tool is running.</summary>
    public sealed record ToolCallStarted(Guid MessageId, AIToolCall Call) : AgentEvent;

    /// <summary>
    /// The call is over, however it ended, and its answer is in the transcript.
    /// </summary>
    /// <param name="Message">
    /// The persisted tool row. Its content is what the model will read on the next step.
    /// </param>
    /// <param name="Summary">One line for the collapsed card, when the tool offered one.</param>
    /// <param name="Detail">
    /// What the expanded card shows when that differs from the content - a diff, typically. Not
    /// persisted, so a transcript reopened later falls back to the content the model was given.
    /// </param>
    public sealed record ToolCallFinished(
        AIToolCall Call,
        AgentCallOutcome Outcome,
        MessageDto Message,
        string? Summary,
        string? Detail) : AgentEvent;

    /// <summary>
    /// One step is over and its assistant row is committed.
    /// </summary>
    /// <param name="CalledTools">
    /// False means the model answered, which is also the end of the run.
    /// </param>
    public sealed record StepCompleted(
        int Step,
        Guid MessageId,
        int? InputTokens,
        int? OutputTokens,
        bool CalledTools) : AgentEvent;

    /// <summary>
    /// The run is over and nothing went wrong.
    /// </summary>
    /// <param name="MessageId">The last assistant row, which holds the answer.</param>
    /// <param name="Steps">Steps actually taken, for the "did 7 steps in 40s" line.</param>
    public sealed record Completed(
        Guid MessageId,
        int Steps,
        AgentStopReason Reason,
        int ElapsedMs) : AgentEvent;

    /// <summary>
    /// The run failed. Everything done up to the failure stands: files that were written stay
    /// written, and the text of the step that failed is kept.
    /// </summary>
    public sealed record Failed(
        Guid MessageId,
        AIErrorKind Kind,
        string UserMessage,
        string? TechnicalDetails,
        bool IsRetryable) : AgentEvent;

    /// <summary>
    /// The user pressed Stop.
    /// </summary>
    /// <remarks>
    /// Nothing is undone, because a tool call that already happened cannot be taken back by ending
    /// the run - and pretending otherwise would be the more dangerous behaviour. A call that was
    /// waiting for an answer when Stop was pressed simply never happens, and the next turn will not
    /// see it: an unanswered call is dropped when the history is replayed.
    /// </remarks>
    public sealed record Cancelled(Guid MessageId, int Steps) : AgentEvent;
}
