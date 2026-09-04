namespace AIClient.Application.DTOs;

/// <summary>
/// One program to run: what to run, where, and the two bounds on what it may cost.
/// </summary>
/// <remarks>
/// <para>
/// The program and its arguments are separate on purpose, and the arguments are a list rather than a
/// line. A command line has to be parsed by somebody, and the somebody is a shell: the moment
/// <c>git commit -m "fix; rm -rf /"</c> becomes one string, the quoting rules of whatever interprets
/// it decide what actually happens. A list is handed to the operating system as a list, so a semicolon
/// is a semicolon and an argument that looks like a second command is one argument.
/// </para>
/// <para>
/// <see cref="WorkingDirectory"/> is absolute and is expected to have been checked already. This is a
/// request object, not a gate; the caller in the Application layer is where the workspace decides
/// whether that directory may be used at all.
/// </para>
/// </remarks>
public sealed record ProcessRunRequest
{
    /// <summary>The program. A bare name is looked up on PATH; a path is not accepted.</summary>
    public required string FileName { get; init; }

    /// <summary>The arguments, each passed through verbatim.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Absolute path the program starts in.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>How long the program may run before it is killed.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Characters of output kept before the rest is dropped.</summary>
    public int MaxOutputCharacters { get; init; } = 20_000;
}

/// <summary>
/// What running a program produced.
/// </summary>
/// <remarks>
/// <para>
/// Standard output and standard error arrive interleaved in <see cref="Output"/> rather than as two
/// fields. Compilers and test runners split their output between the two streams by conventions that
/// vary per tool, and a model shown them separately has to guess how they interleaved - which is
/// exactly the information that says which file the error belongs to.
/// </para>
/// <para>
/// <see cref="ExitCode"/> is null when there is nothing to report one for: the program could not be
/// started, or it was killed for running too long. Those are distinguished by
/// <see cref="Started"/> and <see cref="TimedOut"/>, because "the program failed" and "the program
/// does not exist" call for different next moves from whoever reads this.
/// </para>
/// </remarks>
public sealed record ProcessRunResult
{
    /// <summary>Whether the program started at all.</summary>
    public required bool Started { get; init; }

    /// <summary>The exit code, or null when the program never finished on its own.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Standard output and standard error, interleaved as they were written.</summary>
    public string Output { get; init; } = string.Empty;

    /// <summary>Set when the program was killed for exceeding its time limit.</summary>
    public bool TimedOut { get; init; }

    /// <summary>Set when output was cut short at the cap.</summary>
    public bool Truncated { get; init; }

    /// <summary>How long the program ran.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Why the program could not be started, when it could not.
    /// </summary>
    /// <remarks>
    /// A sentence rather than an exception, and one safe to hand to a model: it names the program and
    /// what went wrong with it, and never the full path it was looked for under.
    /// </remarks>
    public string? Error { get; init; }

    public static ProcessRunResult Failed(string error) =>
        new() { Started = false, Error = error };
}
