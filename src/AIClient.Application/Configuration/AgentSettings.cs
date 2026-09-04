namespace AIClient.Application.Configuration;

/// <summary>
/// What the agent is allowed to do to a workspace.
/// </summary>
/// <remarks>
/// Every value here bounds something a language model drives. The defaults are the ones a user
/// who never opens Settings should get, which means they are the cautious ones: caps low enough
/// that a runaway loop wastes a step rather than a context window, and no folder open until the
/// user picks one.
/// </remarks>
public sealed class AgentSettings
{
    /// <summary>
    /// The folder the agent may read and write, or null when none is open.
    /// </summary>
    /// <remarks>
    /// Persisted so the workspace survives a restart, which is what makes a multi-session task
    /// possible at all. Re-validated on load rather than trusted: the folder may have been
    /// deleted, moved, or - if the file were edited by hand - replaced with something the
    /// workspace rules refuse.
    /// </remarks>
    public string? WorkspaceRoot { get; set; }

    /// <summary>
    /// Largest file the agent may read or write, in bytes. Checked before the file is opened.
    /// </summary>
    /// <remarks>
    /// Half a megabyte of source is already far more than a model can use in one step; the cap
    /// exists so that pointing the agent at a folder containing a database dump costs one
    /// refusal instead of the whole context window.
    /// </remarks>
    public long MaxFileBytes { get; set; } = 512 * 1024;

    /// <summary>Characters returned from a single read before the result is cut short.</summary>
    public int MaxReadCharacters { get; set; } = 60_000;

    /// <summary>Entries returned from a single listing before it is cut short.</summary>
    public int MaxListEntries { get; set; } = 400;

    /// <summary>Matches returned from a single search before it is cut short.</summary>
    public int MaxSearchResults { get; set; } = 150;

    /// <summary>
    /// Folder and file names left out of listings and searches, matched exactly and
    /// case-insensitively.
    /// </summary>
    /// <remarks>
    /// Noise reduction rather than security: build output and dependency trees are enormous, and
    /// a listing that spends its budget on <c>node_modules</c> tells the model nothing about the
    /// project. An ignored path is still readable when named explicitly, because the user may
    /// genuinely want the agent to look at a build log. The names that are refused outright -
    /// version-control internals and files that carry credentials - are not configurable and do
    /// not appear here.
    /// </remarks>
    public List<string> IgnoredNames { get; set; } =
    [
        ".git", ".svn", ".hg",
        ".vs", ".idea", ".vscode",
        "bin", "obj", "packages", "TestResults",
        "node_modules", "bower_components", "vendor",
        "dist", "build", "out", "target",
        "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache",
        ".venv", "venv", ".tox",
        ".next", ".nuxt", ".svelte-kit", ".turbo", ".parcel-cache",
        ".gradle", ".dart_tool", ".terraform",
        ".cache", "coverage",
    ];

    /// <summary>
    /// Tool-calling steps one turn may take before the run is stopped and the user is told.
    /// </summary>
    /// <remarks>
    /// The cost of a loop that will not converge is paid per step, in money and in the user's time,
    /// so the bound has to exist and has to be low enough to notice. Twenty-five is roughly three
    /// times what a well-scoped task takes: enough that a real refactor across a dozen files
    /// finishes, few enough that a model reading the same file over and over is cut off within a
    /// minute rather than an hour.
    /// </remarks>
    public int MaxSteps { get; set; } = 25;

    /// <summary>
    /// Wall-clock ceiling on one turn, in seconds. Zero or less means no ceiling.
    /// </summary>
    /// <remarks>
    /// A separate bound from <see cref="MaxSteps"/> because the two fail differently. A step budget
    /// does nothing about one step that hangs - a search over a network drive, a provider that
    /// accepted the request and went quiet - and a time budget does nothing about twenty-five fast
    /// steps that achieve nothing. Both are cheap to check and each catches what the other misses.
    /// </remarks>
    public int MaxDurationSeconds { get; set; } = 600;

    /// <summary>
    /// How many times one identical call may be repeated within a turn before it is refused.
    /// </summary>
    /// <remarks>
    /// The characteristic failure of a tool-using model is not a wrong call but the same call
    /// forever: it reads a file, fails to notice the answer, and reads it again. Counting exact
    /// repeats - same tool, same arguments - catches that without touching the legitimate case of
    /// reading one file several times as it is edited, because those calls differ. The third
    /// attempt comes back as a tool result saying so, which is the one message that reliably
    /// breaks the cycle.
    /// </remarks>
    public int MaxIdenticalCalls { get; set; } = 3;
}
