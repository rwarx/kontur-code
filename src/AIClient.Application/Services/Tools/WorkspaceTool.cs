using System.Diagnostics.CodeAnalysis;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Domain.Workspace;

namespace AIClient.Application.Services.Tools;

/// <summary>
/// Shared ground for the tools that reach the user's files: the workspace they go through, and the
/// two or three lines every one of them would otherwise repeat.
/// </summary>
/// <remarks>
/// <para>
/// None of these tools touches the file system. They go through <see cref="IWorkspaceService"/>, which
/// is where containment, the protected names and the size caps live, so a tool cannot widen its own
/// access by taking a shortcut - and a new tool inherits every one of those rules by construction
/// rather than by remembering to.
/// </para>
/// <para>
/// What a tool actually does is turn a JSON argument object into one workspace call and its result
/// into prose. That second half matters more than it looks: the model reads it as the whole of what
/// happened, so a result that omits the line count or hides a truncation leaves it working from a
/// picture of the file that is quietly wrong.
/// </para>
/// </remarks>
public abstract class WorkspaceTool : IAgentTool
{
    protected WorkspaceTool(IWorkspaceService workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Workspace = workspace;
    }

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract string ParametersJsonSchema { get; }

    public abstract AgentToolRisk Risk { get; }

    protected IWorkspaceService Workspace { get; }

    public abstract Task<AgentToolResult> ExecuteAsync(
        AgentToolArguments arguments,
        CancellationToken cancellationToken);

    /// <summary>A refusal, labelled with the tool that refused.</summary>
    protected AgentToolResult Refuse(string error) => AgentToolResult.Fail(error, Name);

    /// <summary>An answer, with the one line the transcript shows for it.</summary>
    protected static AgentToolResult Done(string content, string summary, string? detail = null) =>
        AgentToolResult.Ok(content, summary, detail);

    /// <summary>
    /// Reads a path argument, or produces the refusal to send back in its place.
    /// </summary>
    /// <remarks>
    /// Two failures collapse into one call here - the argument being absent or not a string, and the
    /// path itself being one the workspace will not accept - because to the model they are the same
    /// kind of mistake and both are corrected the same way.
    /// </remarks>
    protected bool TryPath(
        AgentToolArguments arguments,
        string name,
        [NotNullWhen(true)] out WorkspacePath? path,
        [NotNullWhen(false)] out AgentToolResult? failure)
    {
        path = null;

        if (!arguments.TryGetString(name, out var raw, out var argumentError))
        {
            failure = Refuse(argumentError);
            return false;
        }

        if (!WorkspacePath.TryParse(raw, out path, out var pathError))
        {
            failure = Refuse(pathError);
            return false;
        }

        failure = null;
        return true;
    }

    /// <summary>
    /// Reads an optional path argument, falling back to the workspace root.
    /// </summary>
    /// <remarks>
    /// For the tools where "everywhere" is a sensible request. A model that omits the path of a
    /// listing means the top of the project, and answering that is better than spending a step on a
    /// refusal it will correct to <c>.</c> anyway.
    /// </remarks>
    protected bool TryOptionalPath(
        AgentToolArguments arguments,
        string name,
        [NotNullWhen(true)] out WorkspacePath? path,
        [NotNullWhen(false)] out AgentToolResult? failure)
    {
        var raw = arguments.GetString(name);

        if (string.IsNullOrWhiteSpace(raw))
        {
            path = WorkspacePath.Root;
            failure = null;
            return true;
        }

        if (!WorkspacePath.TryParse(raw, out path, out var pathError))
        {
            failure = Refuse(pathError);
            return false;
        }

        failure = null;
        return true;
    }

    /// <summary>Rounded to whole units, because a byte count is noise in a tool result.</summary>
    protected static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };
}
