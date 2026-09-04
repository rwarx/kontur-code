using System.Buffers;
using System.Text;
using AIClient.Application.Configuration;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;

namespace AIClient.Application.Services.Tools;

/// <summary>
/// Runs one program in the open folder and hands back what it printed.
/// </summary>
/// <remarks>
/// <para>
/// The tool that turns a file editor into something that can tell whether its edit compiled. Everything
/// else the agent has changes text and hopes; this one gets an exit code, which is the only feedback in
/// the set that comes from outside the model's own reasoning.
/// </para>
/// <para>
/// It is also the only tool here that the workspace sandbox does not contain. A path guard bounds what
/// a program is started on, not what it does once it is running: <c>npm install</c> reaches the network,
/// a test suite reads whatever the machine will give it, and a build script is a program the project
/// author wrote. So the containment for this one is a different shape - four gates, none of which the
/// model can open:
/// </para>
/// <list type="number">
/// <item>Off entirely until the user turns it on, in Settings, once.</item>
/// <item>An allowlist of program names. Not on it is a refusal, and only a person edits the list.</item>
/// <item>Approval on every single call. <c>AgentToolRisk.Execute</c> is exempt from the standing yes
/// that a run can accumulate for a file tool, so ten commands is ten questions.</item>
/// <item>No shell. The program is started directly with an argument list, so a pipe, a redirect or a
/// second command chained with <c>&amp;&amp;</c> is text passed to the program rather than syntax.</item>
/// </list>
/// <para>
/// The fourth gate is what makes the second one worth having. An allowlist in front of a shell is
/// decoration - <c>cmd /c whatever</c> passes it - so this tool has to be incapable of asking for a
/// shell, which is why its schema has no command-line field for the model to smuggle one into.
/// </para>
/// </remarks>
public sealed class RunCommandTool : WorkspaceTool, IAgentToolPreview, IAgentToolAvailability
{
    /// <summary>
    /// Characters of one argument shown in a summary before it is cut short.
    /// </summary>
    /// <remarks>
    /// A summary is one line in a transcript. An argument can be a whole commit message, and letting one
    /// wrap over six lines would bury the program name that the line exists to show.
    /// </remarks>
    private const int MaxSummaryArgumentLength = 60;

    /// <summary>Whitespace of any kind, which turns a program name into a command line.</summary>
    private static readonly SearchValues<char> Whitespace = SearchValues.Create(" \t\n\r\f\v");

    /// <summary>
    /// Characters that mean something to a shell, and nothing in a program's name.
    /// </summary>
    /// <remarks>
    /// None of these can do any harm here - there is no shell to interpret them - which is exactly why
    /// they are refused. A model that writes one is expecting a shell, so the useful answer is to say
    /// there is not one rather than to look for a program called <c>dotnet&amp;&amp;git</c> and report
    /// that it is not installed.
    /// </remarks>
    private static readonly SearchValues<char> Operators = SearchValues.Create("&|<>;\"'`$%()[]{}*?^!~,=\n");

    private readonly ISettingsService _settings;
    private readonly IProcessRunner _runner;

    public RunCommandTool(IWorkspaceService workspace, ISettingsService settings, IProcessRunner runner)
        : base(workspace)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(runner);

        _settings = settings;
        _runner = runner;
    }

    public override string Name => "run_command";

    /// <summary>
    /// What the model is told, and the four sentences that stop the everyday mistakes.
    /// </summary>
    /// <remarks>
    /// The "no shell" paragraph is not documentation, it is error prevention: a model that has used a
    /// terminal writes <c>cd src &amp;&amp; dotnet build</c> on the first attempt, and finding out by
    /// refusal costs a step and an approval prompt. Saying the shape of the call up front is cheaper than
    /// correcting it afterwards.
    /// </remarks>
    public override string Description =>
        "Runs one program in the project folder and returns its combined output and exit code. Use it to "
        + "build, to run tests, and to ask version control what changed - it is how you find out whether an "
        + "edit actually works. "
        + "There is no shell: '&&', '||', '|', '>', '<', ';', '*' and environment variables like '$HOME' "
        + "have no meaning here and are passed to the program as ordinary text. Run one program per call, "
        + "and give each argument as its own entry in 'args' - never a single string with spaces in it. "
        + "To work in a subfolder, set 'working_directory'; there is no 'cd'. "
        + "Only programs the user has allowed can run, and every call needs the user's approval, so keep "
        + "them few and say why you need each one. If a program is refused, do not look for another way to "
        + "run it: report what you wanted to run and let the user decide.";

    public override string ParametersJsonSchema =>
        """
        {
          "type": "object",
          "properties": {
            "command": {
              "type": "string",
              "description": "The program to run, by name and nothing else: 'dotnet', 'git', 'npm'. Not a path, not a command line, no arguments."
            },
            "args": {
              "type": "array",
              "items": { "type": "string" },
              "description": "The arguments, one per entry. For 'dotnet build src/App.csproj' this is [\"build\", \"src/App.csproj\"]."
            },
            "working_directory": {
              "type": "string",
              "description": "Folder to run in, relative to the project root. Omit for the project root itself."
            },
            "timeout_seconds": {
              "type": "integer",
              "description": "Seconds to wait before the program is killed. Optional, and capped by the user's setting."
            }
          },
          "required": ["command"]
        }
        """;

    public override AgentToolRisk Risk => AgentToolRisk.Execute;

    /// <summary>
    /// Whether the tool can do anything at all, which decides whether the model is offered it.
    /// </summary>
    /// <remarks>
    /// Offering a tool that refuses every call is worse than not offering it. A model that sees
    /// <c>run_command</c> in its list will try to build the project, spend a step and an approval prompt
    /// on being told no, and then reason about why the build failed - none of which is what happened.
    /// </remarks>
    public bool IsAvailable => Settings.AllowCommands && Workspace.IsOpen;

    private AgentSettings Settings => _settings.Current.Agent;

    public override async Task<AgentToolResult> ExecuteAsync(
        AgentToolArguments arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var settings = Settings;

        // Re-checked here rather than trusted from IsAvailable. The setting can be turned off while a
        // run is in flight, and this is the check that is actually load-bearing.
        if (!settings.AllowCommands)
        {
            return Refuse(
                "Running programs is switched off. The user can turn it on under Settings → Agent, and "
                + "until they do, nothing can be run. Say what you wanted to run and why.");
        }

        if (!arguments.TryGetString("command", out var command, out var commandError))
        {
            return Refuse(commandError);
        }

        if (Describe(command) is { } malformed)
        {
            return Refuse(malformed);
        }

        if (!IsAllowed(command, settings))
        {
            return Refuse(Rejection(command, settings));
        }

        if (!arguments.TryGetStringArray("args", out var args, out var argsError))
        {
            return Refuse(argsError);
        }

        if (!arguments.TryGetInt32("timeout_seconds", out var requested, out var timeoutError))
        {
            return Refuse(timeoutError);
        }

        if (!TryOptionalPath(arguments, "working_directory", out var relative, out var pathFailure))
        {
            return pathFailure;
        }

        var resolved = await Workspace.ResolveDirectoryAsync(relative, cancellationToken).ConfigureAwait(false);

        if (!resolved.Success)
        {
            return Refuse(resolved.Error!);
        }

        var result = await _runner.RunAsync(
            new ProcessRunRequest
            {
                FileName = command,
                Arguments = args,
                WorkingDirectory = resolved.Value!,
                Timeout = Timeout(requested, settings),
                MaxOutputCharacters = Math.Max(1_000, settings.MaxCommandOutputCharacters),
            },
            cancellationToken).ConfigureAwait(false);

        return Report(command, args, relative, result);
    }

    /// <summary>
    /// The one line the user reads before saying yes, and the command underneath it.
    /// </summary>
    /// <remarks>
    /// The only approval prompt in the application where the preview is the thing being approved rather
    /// than a rendering of it. A diff for a write can be summarised, because the summary and the diff say
    /// the same thing in different detail; a command cannot, because the flag that makes
    /// <c>git clean -xfd</c> destructive is in the argument list. So the full command goes in the preview,
    /// argument per line, unabbreviated.
    /// </remarks>
    public Task<AgentToolPreview> DescribeAsync(
        AgentToolArguments arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (!arguments.TryGetString("command", out var command, out _))
        {
            return Task.FromResult(AgentToolPreview.None);
        }

        arguments.TryGetStringArray("args", out var args, out _);
        var settings = Settings;

        var summary = $"Run {Line(command, args, MaxSummaryArgumentLength)}";
        var folder = arguments.GetString("working_directory");
        var where = string.IsNullOrWhiteSpace(folder) || folder is "." or "/" ? null : folder.Trim();

        if (where is not null)
        {
            summary = $"{summary} in {where}";
        }

        if (!settings.AllowCommands)
        {
            return Task.FromResult(AgentToolPreview.Describe(
                $"{summary} - running programs is switched off, so this will be refused"));
        }

        if (!IsAllowed(command, settings))
        {
            return Task.FromResult(AgentToolPreview.Describe(
                $"{summary} - '{command.Trim()}' is not on the allowed list, so this will be refused"));
        }

        var preview = new StringBuilder();
        preview.Append("Program: ").AppendLine(command.Trim());

        foreach (var argument in args)
        {
            preview.Append("Argument: ").AppendLine(argument);
        }

        preview.Append("Folder: ").AppendLine(where ?? "the project root");
        preview.Append("Timeout: ").Append(Timeout(null, settings).TotalSeconds).AppendLine("s");

        // Said in the dialog and not only in the documentation. The absence of a shell is the reason a
        // command that looks dangerous is less so, and the reason one the user expects to chain will not.
        preview.AppendLine();
        preview.Append(
            "This runs directly, without a shell, so nothing in the arguments above can chain a second "
            + "command. It can still change files, reach the network and take as long as its timeout "
            + "allows.");

        return Task.FromResult(AgentToolPreview.Describe(summary, preview.ToString()));
    }

    /// <summary>
    /// Turns the run into the two things the model needs: whether it worked, and the output.
    /// </summary>
    /// <remarks>
    /// The exit code leads, because it is the sentence a model acts on, and the output follows in a fence
    /// so that a build error containing backticks does not end the block early. A zero exit code with no
    /// output at all still says something - a great many tools succeed silently - so that case gets a
    /// sentence rather than an empty result the model would read as a broken tool.
    /// </remarks>
    private AgentToolResult Report(
        string command,
        IReadOnlyList<string> args,
        Domain.Workspace.WorkspacePath folder,
        ProcessRunResult result)
    {
        var label = Line(command, args, MaxSummaryArgumentLength);

        if (!result.Started)
        {
            return Refuse(result.Error ?? $"'{command.Trim()}' could not be started.");
        }

        var body = new StringBuilder();

        if (result.TimedOut)
        {
            body.Append($"'{label}' was still running after {result.Duration.TotalSeconds:0}s and was stopped, ")
                .AppendLine("along with anything it had started. Whatever it had already done stands.");
        }
        else
        {
            body.Append(result.ExitCode == 0 ? "Succeeded" : $"Failed with exit code {result.ExitCode}")
                .Append(" after ")
                .Append($"{result.Duration.TotalSeconds:0.#}")
                .AppendLine("s.");
        }

        if (result.Truncated)
        {
            body.AppendLine("The output was longer than the limit; the beginning was dropped and the end kept.");
        }

        if (result.Output.Length == 0)
        {
            body.AppendLine("It printed nothing.");
        }
        else
        {
            body.AppendLine().AppendLine("Output:").AppendLine("```").AppendLine(result.Output.TrimEnd()).AppendLine("```");
        }

        var where = folder.IsRoot ? string.Empty : $" in {folder}";
        var outcome = result.TimedOut ? "timed out" : result.ExitCode == 0 ? "ok" : $"exit {result.ExitCode}";

        // A failing build is not a failing tool. The call did what it was asked, and reporting it as a
        // failure would have the loop treat a compiler error as something to retry rather than to fix.
        return Done(body.ToString().TrimEnd(), $"{Name} {label}{where} - {outcome}", result.Output);
    }

    private TimeSpan Timeout(int? requested, AgentSettings settings)
    {
        var ceiling = settings.CommandTimeoutSeconds > 0 ? settings.CommandTimeoutSeconds : 120;
        var seconds = requested is { } value && value > 0 ? Math.Min(value, ceiling) : ceiling;

        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Whether the name is on the user's list, compared the way a person would write it.
    /// </summary>
    /// <remarks>
    /// A trailing <c>.exe</c> is ignored on both sides. Nothing else is normalised: a name containing a
    /// separator has already been refused by <see cref="Describe"/>, so this is a comparison of two bare
    /// names and not a path comparison pretending to be one.
    /// </remarks>
    private static bool IsAllowed(string command, AgentSettings settings) =>
        settings.AllowedCommands is { Count: > 0 } allowed
        && allowed.Any(entry => string.Equals(Bare(entry), Bare(command), StringComparison.OrdinalIgnoreCase));

    private static string Bare(string name)
    {
        var trimmed = name.Trim();

        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }

    /// <summary>
    /// What the model is told when a program is not allowed.
    /// </summary>
    /// <remarks>
    /// Names the list, because a model told only "not allowed" tries a synonym: <c>msbuild</c> after
    /// <c>dotnet</c>, then <c>cmd</c>. Showing what is available turns three wasted steps into one
    /// correct call, and the closing sentence is what stops the search for a way around.
    /// </remarks>
    private static string Rejection(string command, AgentSettings settings)
    {
        var allowed = settings.AllowedCommands is { Count: > 0 } list
            ? string.Join(", ", list.Select(entry => entry.Trim()).Where(entry => entry.Length > 0).Order(StringComparer.OrdinalIgnoreCase))
            : "nothing";

        return $"'{command.Trim()}' is not a program the user has allowed, so it was not run. "
            + $"Allowed: {allowed}. Only the user can add to that list, under Settings → Agent. "
            + "Do not try a different program to achieve the same thing - say what you needed and why.";
    }

    /// <summary>
    /// Refuses the shapes of "command" that are a misunderstanding rather than a typo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each of these would otherwise fail somewhere less helpful. A path escapes the allowlist, which
    /// compares names: <c>C:\tools\dotnet.exe</c> is not <c>dotnet</c> and must not be treated as it, and
    /// <c>..\..\evil</c> must not be resolvable at all. A command line - <c>"dotnet build"</c> - would be
    /// looked up as a program with a space in its name and fail with something obscure. And the shell
    /// operators are refused here rather than silently passed through, because a model that wrote one
    /// meant it to do something, and letting <c>&amp;&amp;</c> arrive as part of a program name would look
    /// like a missing program instead of the absence of a shell.
    /// </para>
    /// <para>
    /// Returns the refusal, or null when the name is usable.
    /// </para>
    /// </remarks>
    private static string? Describe(string command)
    {
        var value = command.Trim();

        if (value.Length == 0)
        {
            return "'command' cannot be empty.";
        }

        if (value.Length > 128)
        {
            return "'command' is not a program name. Send just the name, such as 'dotnet'.";
        }

        if (value.AsSpan().ContainsAny('/', '\\', ':'))
        {
            return $"'{value}' looks like a path. Send only the program's name - 'dotnet', not a full "
                + "path to it - and let the machine find it.";
        }

        if (value.AsSpan().ContainsAny(Whitespace))
        {
            return $"'{value}' is a command line, not a program. Put the program in 'command' and every "
                + "argument in 'args': {\"command\": \"dotnet\", \"args\": [\"build\"]}.";
        }

        if (value.AsSpan().ContainsAny(Operators))
        {
            return $"'{value}' is not a program name. There is no shell here, so operators like '&&' and "
                + "'|' cannot be used: run one program per call and chain them yourself, a call at a time.";
        }

        return null;
    }

    /// <summary>The command as a person would write it, with long arguments cut short.</summary>
    private static string Line(string command, IReadOnlyList<string> args, int maxArgument)
    {
        var line = new StringBuilder(command.Trim());

        foreach (var argument in args)
        {
            var value = argument.ReplaceLineEndings(" ");

            if (value.Length > maxArgument)
            {
                value = value[..maxArgument] + "…";
            }

            line.Append(' ').Append(value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value);
        }

        return line.ToString();
    }
}
