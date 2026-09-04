using AIClient.Application.DTOs;

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
    /// <param name="mode">
    /// Which run this is. A planning run gets different instructions rather than the same instructions
    /// with a warning attached: the discipline of a build - look, change one thing, verify - is not the
    /// discipline of a plan, and telling a model both leaves it doing neither well.
    /// </param>
    public static string Compose(
        string? basePrompt,
        string? workspaceRoot,
        bool canRunCommands = false,
        AgentMode mode = AgentMode.Build)
    {
        var parts = new List<string>(5);

        if (!string.IsNullOrWhiteSpace(basePrompt))
        {
            parts.Add(basePrompt.Trim());
        }

        if (mode.IsPlanning())
        {
            parts.Add(workspaceRoot is null ? PlanningAlone : PlanningWorkspace(workspaceRoot));
            parts.Add(Planning);

            if (mode == AgentMode.PlanCanvas)
            {
                parts.Add(Canvas);
            }

            return string.Join("\n\n", parts);
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

    /// <summary>
    /// How to plan, which is not how to build.
    /// </summary>
    /// <remarks>
    /// The first paragraph is the one that earns its place. A model that has been given read tools and
    /// asked for a plan will otherwise spend a step offering to make the change and waiting to be told
    /// yes - there is nothing to say yes to, and the offer reads to the user as the plan having failed.
    /// </remarks>
    private const string Planning = """
        You are planning, not building. Nothing here changes a file: the tools you have can read the
        project, and submit_plan records what you worked out. There is no way to write from this mode,
        so do not offer to and do not ask to be let - the user switches to Build when they want the
        plan carried out.

        How to plan:
        - Read enough to be specific. A plan that names the files it will touch is worth more than one
          that says "update the service layer", and the only way to name them is to look.
        - Plan the work, not the conversation. Each step is something that gets done, in the order it
          happens, small enough that someone can tell afterwards whether it worked.
        - Put what you are unsure about in 'risks' rather than planning around it quietly. A guess the
          user can correct is useful; a guess they cannot see is a defect waiting to be built.
        - Call submit_plan once, with the whole plan in the one call. Then tell the user the plan in
          your own words - what you would do and why - and stop.
        """;

    /// <summary>
    /// Added for <see cref="AgentMode.PlanCanvas"/>, and it is entirely about <c>parts</c>.
    /// </summary>
    /// <remarks>
    /// The same plan serves both planning modes, so the drawing is only as good as that one field. Left
    /// to itself a model fills <c>parts</c> with restatements of the steps, which draws as a row of
    /// disconnected boxes; the naming rule matters just as much, because a dependency naming a part that
    /// does not exist is a line that cannot be drawn.
    /// </remarks>
    private const string Canvas = """
        This plan gets drawn as a diagram, and 'parts' is what gets drawn. So:
        - List the pieces the finished project is made of - folders, files, modules, services,
          interfaces, data, views, tests - not the steps that create them. The steps are in 'steps'.
        - Give each part one short name, and spell it exactly that way in every 'depends_on' that
          mentions it. A dependency naming a part that is not in the list draws as nothing.
        - Point 'depends_on' at what a part needs in order to work, not at whatever happens to be
          built before it.
        - Keep to the parts that carry the shape of the project. A box for every file is a picture of
          a folder listing.
        """;

    private static string PlanningWorkspace(string root) =>
        $"""
        You are planning work on a project on the user's machine, and you can read it through the
        tools you have been given. The project folder is:

        {root}

        Every path you send is relative to that folder - 'src/Program.cs', not a full path. You have
        not seen this project before: nothing about its contents is in this conversation unless a tool
        put it there, so read it before planning against it rather than guessing at filenames.
        """;

    /// <summary>
    /// The counterpart of <see cref="NoWorkspace"/>, and the opposite advice.
    /// </summary>
    /// <remarks>
    /// A plan for a project that does not exist yet is the ordinary case for these modes, not a
    /// degraded one - which is why no folder is required to start a planning run at all. Saying so is
    /// what stops the model from opening with "you need to open a folder first", the single most
    /// useless thing it could say to someone who has just asked how to begin.
    /// </remarks>
    private const string PlanningAlone = """
        No project folder is open, and for planning that is fine: a project that does not exist yet
        has nothing to read. Plan from what the user has told you, ask about anything that would
        change the shape of the plan, and do not tell them to open a folder - that is for building.
        """;
}
