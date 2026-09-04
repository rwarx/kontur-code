namespace AIClient.Application.Services;

/// <summary>
/// The instructions an agent run is given on top of whatever the user configured.
/// </summary>
/// <remarks>
/// <para>
/// Prompt text, not documentation, and it earns its place: the tool descriptions say what each tool
/// does, but nothing else tells the model how to behave across a whole run - to look before it
/// changes anything, to stop when the task is done, to treat a refusal as a decision rather than an
/// obstacle. A model given the tools and none of this reads one file and starts rewriting.
/// </para>
/// <para>
/// Kept in one place, and composed rather than concatenated blindly, because the user's own system
/// prompt has to survive. Someone who wrote "answer in Russian" in Settings means it during an agent
/// run too, so their words go first and these rules follow.
/// </para>
/// </remarks>
public static class AgentPrompt
{
    /// <summary>
    /// How to work, independent of which folder is open. Deliberately short: a long prompt competes
    /// with the tool descriptions for the model's attention, and loses.
    /// </summary>
    private const string Discipline = """
        How to work:
        - Look before you change anything. Find the file, read it, then edit it.
        - Prefer edit_file to write_file on a file that already exists. write_file replaces every
          line, so anything you do not reproduce is deleted.
        - Change one thing at a time and read what came back. A result that says a call failed is
          telling you what to fix; making the same call again will fail the same way.
        - Say in a sentence what you are about to do before you do it, and say what you found
          afterwards. Someone is watching this happen and can stop you.
        - Stop when the task is done, and answer with words rather than another call. Do not tidy,
          refactor or improve anything you were not asked about.

        What you cannot do:
        - Reach outside the project folder. Absolute paths, '..' and links leading out are refused.
        - Change anything without the user's approval. If they say no, that is their decision: say
          what you would have done and ask, rather than looking for another route to the same edit.
        """;

    /// <summary>
    /// What the model is told on the last permitted step, when the tools have been withheld.
    /// </summary>
    /// <remarks>
    /// Sent as well as, not instead of, <c>tool_choice: none</c>. The provider setting is what
    /// actually prevents another call; this paragraph is what stops the model from spending its last
    /// step announcing the call it was about to make, which is what it otherwise does when the tools
    /// silently stop working.
    /// </remarks>
    public const string LastStep = """
        The step budget for this task is spent, so the tools are no longer available and no further
        call will be made. Do not plan one. Say what you did, what you found, and what is left
        undone, so the user can decide whether to carry on.
        """;

    /// <summary>
    /// Builds the system prompt for a run.
    /// </summary>
    /// <param name="basePrompt">The conversation's own prompt, or the configured default. May be null.</param>
    /// <param name="workspaceRoot">The open folder, or null when there is none.</param>
    /// <param name="canRunCommands">Whether the user has allowed programs to be run.</param>
    public static string Compose(string? basePrompt, string? workspaceRoot, bool canRunCommands = false)
    {
        var parts = new List<string>(4);

        if (!string.IsNullOrWhiteSpace(basePrompt))
        {
            parts.Add(basePrompt.Trim());
        }

        parts.Add(workspaceRoot is null ? NoWorkspace : Workspace(workspaceRoot));

        if (canRunCommands && workspaceRoot is not null)
        {
            parts.Add(Commands);
        }

        parts.Add(Discipline);

        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// Added only when running programs is switched on, and it is about restraint rather than capability.
    /// </summary>
    /// <remarks>
    /// The tool description already says how to call it. What a run needs on top of that is a sense of
    /// proportion: every command is a question put to a person who is watching, and a model that runs the
    /// test suite after each of six edits has asked six times for what one call would have told it. The
    /// last line matters most - a model that cannot get a command approved will otherwise keep proposing
    /// variations of it until the step budget is gone.
    /// </remarks>
    private const string Commands = """
        You can also run programs, and every run needs the user's approval before it happens - they see
        the exact command and can refuse it. So:
        - Verify with a command rather than by assertion. After changing code, build it; after changing
          behaviour, run the tests. "This should compile" is worth nothing next to an exit code.
        - Batch the checking. Make the whole change, then build once. Do not build after every edit.
        - Read the output before deciding what it means. A failing build names the file and the line.
        - If a command is refused or not allowed, stop asking. Say what you would have run, what it
          would have told you, and carry on with what you can do without it.
        """;

    private static string Workspace(string root) =>
        $"""
        You are working on a project on the user's machine, through the tools you have been given.
        The project folder is:

        {root}

        Every path you send is relative to that folder - 'src/Program.cs', not a full path. You have
        not seen this project before: nothing about its contents is in this conversation unless a tool
        put it there, so start by listing or searching rather than guessing at filenames.
        """;

    /// <summary>
    /// What to say when the tools exist but every one of them will refuse.
    /// </summary>
    /// <remarks>
    /// The tools are still offered in this state, so a model that ignores this paragraph learns the
    /// same thing from the first refusal. Saying it up front saves a step of the budget and, more
    /// usefully, lets the model tell the user what to do about it instead of reporting a mysterious
    /// failure.
    /// </remarks>
    private const string NoWorkspace = """
        No project folder is open, so every file tool will refuse. Do not keep trying them: tell the
        user to open a folder first, and answer whatever you can from the conversation alone.
        """;
}
