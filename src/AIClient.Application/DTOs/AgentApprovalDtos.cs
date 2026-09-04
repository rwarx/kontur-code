namespace AIClient.Application.DTOs;

/// <summary>
/// One thing the agent wants to do, put to the person who will live with the result.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is written for a human reader in a hurry. The model's own arguments are carried
/// verbatim in <see cref="ArgumentsJson"/> because a person who distrusts the summary must be able
/// to see the literal request, but the fields above it exist so that the common case - glance,
/// approve - does not require reading JSON.
/// </para>
/// <para>
/// The request says what is proposed, never whether it is allowed. Policy lives in the agent loop,
/// which knows the risk level, the settings and what has already been approved this run; a gate that
/// decided for itself would make the rule depend on which gate happened to be installed.
/// </para>
/// </remarks>
public sealed record AgentApprovalRequest
{
    /// <summary>The conversation the step belongs to, so a gate can show it in context.</summary>
    public required Guid ConversationId { get; init; }

    /// <summary>The tool as the model named it.</summary>
    public required string ToolName { get; init; }

    /// <summary>What a wrong call would cost, and therefore how hard the question should be.</summary>
    public required AgentToolRisk Risk { get; init; }

    /// <summary>The arguments as the model produced them.</summary>
    public required string ArgumentsJson { get; init; }

    /// <summary>
    /// One line naming the effect: <c>Overwrite src/Widget.cs</c>, <c>Delete 3 files</c>.
    /// </summary>
    /// <remarks>
    /// Comes from the tool rather than from the loop. Only the tool knows that a
    /// <c>write_file</c> to a path that does not exist is a create rather than an overwrite, and
    /// that distinction is most of what the person is deciding about.
    /// </remarks>
    public string? Summary { get; init; }

    /// <summary>
    /// What will change, in full: a unified diff for a write, the command line for a run. Null when
    /// the tool cannot say in advance.
    /// </summary>
    /// <remarks>
    /// Rendering belongs to the host - a terminal colours a diff differently from a WPF window - so
    /// this stays text. It is not a substitute for <see cref="ArgumentsJson"/>: a preview is
    /// computed from the workspace as it is now, and the workspace can change between the question
    /// and the answer.
    /// </remarks>
    public string? Preview { get; init; }

    /// <summary>
    /// Whether this exact call has already been approved earlier in the same run, and is being
    /// asked again only because the answer was not remembered.
    /// </summary>
    /// <remarks>
    /// Worth showing. A person who sees the same question a fourth time should be told that they
    /// are not misreading it, and it is the honest way to present a risk level whose answers are
    /// deliberately never cached.
    /// </remarks>
    public bool IsRepeat { get; init; }
}

/// <summary>What the person said.</summary>
public enum AgentApprovalOutcome
{
    /// <summary>No. The call is not made, and the model is told why.</summary>
    Denied,

    /// <summary>Yes, this once.</summary>
    Allowed,

    /// <summary>
    /// Yes, and stop asking about this tool for the rest of the run.
    /// </summary>
    /// <remarks>
    /// Scoped to one run rather than saved, because consent given while watching a task is not
    /// consent given tomorrow to a different one. The loop refuses to remember an
    /// <see cref="AgentToolRisk.Execute"/> answer at all, however this is set.
    /// </remarks>
    AllowedForRun,
}

/// <summary>
/// The answer, with the words the model will read if it was no.
/// </summary>
/// <remarks>
/// A denial is not an error and does not end the turn. It comes back to the model as an ordinary
/// tool result saying the user declined, which is what lets it try a smaller change or explain what
/// it was attempting instead of failing silently.
/// </remarks>
public sealed record AgentApprovalDecision
{
    public required AgentApprovalOutcome Outcome { get; init; }

    /// <summary>
    /// Why, when the answer was no. Passed to the model verbatim.
    /// </summary>
    /// <remarks>
    /// The most useful denial explains itself - "not that file, use the one in src" - and a model
    /// given that sentence corrects course in one step. A bare refusal usually produces the same
    /// call again.
    /// </remarks>
    public string? Reason { get; init; }

    public bool IsAllowed => Outcome is AgentApprovalOutcome.Allowed or AgentApprovalOutcome.AllowedForRun;

    public static AgentApprovalDecision Allow() => new() { Outcome = AgentApprovalOutcome.Allowed };

    public static AgentApprovalDecision AllowForRun() => new() { Outcome = AgentApprovalOutcome.AllowedForRun };

    public static AgentApprovalDecision Deny(string? reason = null) =>
        new() { Outcome = AgentApprovalOutcome.Denied, Reason = reason };
}
