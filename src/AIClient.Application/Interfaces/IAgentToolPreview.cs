using AIClient.Application.DTOs;
using AIClient.Application.Services;

namespace AIClient.Application.Interfaces;

/// <summary>
/// A tool that can say what a call would do, before it does it.
/// </summary>
/// <remarks>
/// <para>
/// Optional, and separate from <see cref="IAgentTool"/> on purpose. A read has nothing to preview and
/// a tool that cannot describe itself is still a perfectly good tool; folding this into the main
/// interface would force every implementation to write a method returning nothing useful.
/// </para>
/// <para>
/// This is what makes the approval question answerable. "write_file wants to change Widget.cs" is not
/// a decision anyone can make - "here are the four lines it will replace" is. The description is
/// computed from the workspace as it stands at the moment of asking, so it is a forecast rather than a
/// promise: a preview shown to the user is not what the tool is later held to.
/// </para>
/// <para>
/// Called before the user is asked and never as part of carrying the call out, which means it must
/// change nothing. A describe that writes to the workspace would make declining a call as dangerous
/// as accepting it.
/// </para>
/// </remarks>
public interface IAgentToolPreview
{
    /// <summary>
    /// Describes what a call with these arguments would do.
    /// </summary>
    /// <remarks>
    /// Never throws for an ordinary reason: arguments that make no sense, a missing file or a path
    /// outside the workspace all come back as <see cref="AgentToolPreview.None"/> or as a summary
    /// saying so. The call is about to be refused by the tool itself anyway, and a preview is not the
    /// place to report it - <see cref="IAgentTool.ExecuteAsync"/> is.
    /// </remarks>
    Task<AgentToolPreview> DescribeAsync(
        AgentToolArguments arguments,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What a call would do, in the two forms a person reads: the line and the detail.
/// </summary>
/// <remarks>
/// The same split as <see cref="AgentToolResult"/>, and for the same reason. Someone approving a
/// change glances at one line and only opens the diff if the line surprises them, so the line has to
/// carry the decision on its own.
/// </remarks>
public sealed record AgentToolPreview
{
    /// <summary>
    /// One line naming the effect: <c>Create src/Widget.cs</c>, <c>Overwrite 42 lines in
    /// src/Widget.cs</c>, <c>Delete docs/old.md</c>.
    /// </summary>
    /// <remarks>
    /// Create against overwrite is the distinction worth the most here. Only the tool can tell them
    /// apart, and it is most of what the person is deciding about.
    /// </remarks>
    public string? Summary { get; init; }

    /// <summary>What will change, in full. A unified diff, typically. Null when there is nothing to show.</summary>
    public string? Preview { get; init; }

    /// <summary>Nothing to say - which is not a failure, and is the answer for most tools.</summary>
    public static AgentToolPreview None { get; } = new();

    public static AgentToolPreview Describe(string? summary, string? preview = null) =>
        new() { Summary = summary, Preview = preview };
}
