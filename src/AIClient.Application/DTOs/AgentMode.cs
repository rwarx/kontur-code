namespace AIClient.Application.DTOs;

/// <summary>
/// What a run is for, and therefore what it is allowed to do.
/// </summary>
/// <remarks>
/// <para>
/// One task and one set of tools are not the same thing. "Read this project and tell me how you would
/// add authentication" and "add authentication" are different requests, and a model given the writing
/// tools will answer the first by doing the second - it has them, the task is adjacent, and nothing
/// stops it. The mode is what stops it: the tools that change files are withheld for the whole run
/// rather than declined one dialog at a time.
/// </para>
/// <para>
/// Chosen per message rather than per conversation. A plan and the work that follows it belong in one
/// transcript - the plan is the context the build needs - so switching mode must not mean starting a
/// new chat.
/// </para>
/// </remarks>
public enum AgentMode
{
    /// <summary>
    /// Carry the task out. Every tool, with the ordinary approval gate in front of the ones that
    /// change something.
    /// </summary>
    Build,

    /// <summary>
    /// Work out what would have to be done, and stop there.
    /// </summary>
    /// <remarks>
    /// Reading tools only, plus the one that records the plan. A run in this mode cannot write, move,
    /// delete or run anything, which is what makes it safe to point at a project nobody has read yet -
    /// there is no approval dialog to get wrong because there is nothing to approve.
    /// </remarks>
    Plan,

    /// <summary>
    /// Plan, and describe the shape of the finished thing so it can be drawn.
    /// </summary>
    /// <remarks>
    /// The same permissions as <see cref="Plan"/> and one extra demand: the plan has to name the parts
    /// the project will be made of and how they depend on each other, because that is what a canvas
    /// draws. Useful before a line exists - a project that has not been started has nothing to read,
    /// but it can still be laid out.
    /// </remarks>
    PlanCanvas,
}

/// <summary>Convenience over <see cref="AgentMode"/>, so the same question is not asked two ways.</summary>
public static class AgentModes
{
    /// <summary>True for the modes that only plan, whichever of them it is.</summary>
    /// <remarks>
    /// Written once here because the answer decides four separate things - the tools offered, the
    /// prompt, whether a folder is required, and whether commands are mentioned at all - and four
    /// copies of <c>mode is Plan or PlanCanvas</c> is four places to forget a third planning mode.
    /// </remarks>
    public static bool IsPlanning(this AgentMode mode) => mode is AgentMode.Plan or AgentMode.PlanCanvas;

    /// <summary>True when the mode changes things, and so needs somewhere to change them.</summary>
    public static bool NeedsWorkspace(this AgentMode mode) => mode == AgentMode.Build;

    /// <summary>The name the user picked it by, for a hint or a log line.</summary>
    public static string DisplayName(this AgentMode mode) => mode switch
    {
        AgentMode.Plan => "Plan",
        AgentMode.PlanCanvas => "Plan + canvas",
        _ => "Build",
    };
}
