namespace AIClient.Application.DTOs;

/// <summary>
/// How much one call can cost if it is wrong, which is what decides whether the user is asked
/// first.
/// </summary>
/// <remarks>
/// The grouping is by what cannot be taken back, not by what the tool touches. Reading the wrong
/// file wastes a step; writing the wrong file destroys work the user may not have committed; running
/// the wrong command can do anything at all. Those are three different questions to put to a person,
/// so they are three different levels here rather than a single <c>IsDestructive</c> flag.
/// </remarks>
public enum AgentToolRisk
{
    /// <summary>Reads, and changes nothing. Runs without asking.</summary>
    Read,

    /// <summary>Changes something in the workspace. Asks first, and shows what will change.</summary>
    Write,

    /// <summary>Runs a program. Asks every time, and the answer is never remembered.</summary>
    Execute,
}

/// <summary>
/// What one tool call produced: the text the model reads, and what the transcript shows a person.
/// </summary>
/// <remarks>
/// <para>
/// A failed call is still a result. The model has to be told what went wrong in the same channel it
/// would have been told the answer, because a tool message is the only thing it can react to - an
/// exception thrown out of a tool ends the turn and teaches it nothing.
/// </para>
/// <para>
/// <see cref="Content"/> and <see cref="Summary"/> have different audiences and are allowed to
/// disagree. The model wants the twelve lines it asked to read; the person scrolling the transcript
/// wants one line saying which file was read. Writing one string for both readers produces text
/// that serves neither.
/// </para>
/// </remarks>
public sealed record AgentToolResult
{
    public required bool Success { get; init; }

    /// <summary>
    /// The tool message handed back to the model. Never empty, including on failure: a blank tool
    /// result reads as a tool that silently did nothing.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>One line for the collapsed card in the transcript.</summary>
    public string? Summary { get; init; }

    /// <summary>
    /// What the expanded card shows, when that is something other than <see cref="Content"/> - a
    /// diff, for instance, where the model only needs to be told the write succeeded.
    /// </summary>
    public string? Detail { get; init; }

    public static AgentToolResult Ok(string content, string? summary = null, string? detail = null) =>
        new() { Success = true, Content = content, Summary = summary, Detail = detail };

    public static AgentToolResult Fail(string error, string? summary = null) =>
        new() { Success = false, Content = error, Summary = summary };
}
