using System.Text.Json;
using AIClient.Application.Configuration;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Application.Services;
using AIClient.Application.Services.Tools;
using AIClient.Infrastructure.Configuration;
using AIClient.Infrastructure.Workspace;
using AIClient.Tests.Support;

namespace AIClient.Tests;

/// <summary>
/// The one tool that can run a program, and the four gates in front of it.
/// </summary>
/// <remarks>
/// <para>
/// Section 28 forbade executing code at all; the user overrode that and asked for a full coding agent,
/// which makes this the most consequential tool in the set and the one whose refusals are worth the most
/// assertions. Every test here is about a decision taken <em>before</em> a program starts - the setting,
/// the allowlist, the shape of the name, the folder - because that is where the containment lives.
/// </para>
/// <para>
/// The runner is substituted, deliberately and unlike the rest of the tool suite. A test that really
/// started a program would assert nothing about any of the above while quietly depending on which
/// toolchains are installed on the machine running it. What the stub does assert is stronger: that
/// nothing reached it at all when a gate was closed.
/// </para>
/// </remarks>
public sealed class RunCommandToolTests : IAsyncLifetime
{
    private readonly StubSettingsService _settings = new();
    private readonly RecordingLogger<WorkspaceService> _logger = new();

    private string _scratch = null!;
    private string _root = null!;
    private WorkspaceService _workspace = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "aiclient-command", Guid.CreateVersion7().ToString("n"));
        _root = Path.Combine(_scratch, "project");

        Directory.CreateDirectory(Path.Combine(_root, "src"));
        await File.WriteAllTextAsync(Path.Combine(_root, "README.md"), "# Project\n", Token);

        _workspace = new WorkspaceService(_settings, new AppPaths(Path.Combine(_scratch, "appdata")), _logger);

        var opened = await _workspace.OpenAsync(_root, Token);
        Assert.True(opened.Success, opened.Error);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth failing a run over a leftover temporary directory.
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Nothing_runs_until_the_user_switches_running_programs_on()
    {
        // The default, and the gate that matters most: an installation nobody has configured cannot
        // run a program even if the model asks perfectly.
        var runner = new StubProcessRunner();
        var tool = Tool(runner);

        var refusal = await Refused(tool, """{"command":"dotnet","args":["build"]}""");

        Assert.Empty(runner.Requests);
        Assert.Contains("switched off", refusal, StringComparison.Ordinal);
        Assert.Contains("Settings", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_program_the_user_has_not_allowed_does_not_run_and_the_list_is_named()
    {
        // Naming the list turns three wasted steps into one correct call: a model told only "no"
        // tries a synonym, then a shell.
        var runner = new StubProcessRunner();
        var tool = Allowing(runner, "dotnet", "git");

        var refusal = await Refused(tool, """{"command":"curl","args":["https://example.com"]}""");

        Assert.Empty(runner.Requests);
        Assert.Contains("'curl' is not a program the user has allowed", refusal, StringComparison.Ordinal);
        Assert.Contains("Allowed: dotnet, git", refusal, StringComparison.Ordinal);
        Assert.Contains("Only the user can add to that list", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_allowlist_allows_nothing()
    {
        // Switching the feature on is not the same as allowing a program, and a list emptied by hand
        // has to mean what it says rather than falling back to a built-in set.
        var runner = new StubProcessRunner();
        var tool = Tool(runner, agent =>
        {
            agent.AllowCommands = true;
            agent.AllowedCommands.Clear();
        });

        var refusal = await Refused(tool, """{"command":"dotnet"}""");

        Assert.Empty(runner.Requests);
        Assert.Contains("Allowed: nothing", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dotnet build", "command line")]
    [InlineData("C:\\tools\\dotnet.exe", "path")]
    [InlineData("./dotnet", "path")]
    [InlineData("../../evil", "path")]
    [InlineData("cmd /c dir", "path")]
    [InlineData("dotnet&&git", "not a program name")]
    [InlineData("dotnet|tee", "not a program name")]
    [InlineData("$SHELL", "not a program name")]
    [InlineData("", "cannot be empty")]
    public async Task A_command_that_is_not_a_bare_program_name_is_refused(string command, string because)
    {
        // Each of these would otherwise fail somewhere less helpful: a path escapes an allowlist that
        // compares names, a command line is looked up as a program with a space in it, and an operator
        // arrives as part of a name so the model reads "not installed" where the truth is "no shell".
        //
        // A value that is both - 'cmd /c dir' - is reported as a path, because that is the check that
        // stands between the allowlist and a program it never approved, and it goes first for that
        // reason rather than by accident.
        var runner = new StubProcessRunner();
        var tool = Allowing(runner, "dotnet", "git", "cmd");

        var refusal = await Refused(tool, $$"""{"command":{{Quote(command)}}}""");

        Assert.Empty(runner.Requests);
        Assert.Contains(because, refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_refusal_for_an_operator_says_there_is_no_shell_rather_than_naming_a_program()
    {
        // The distinction the model acts on. "dotnet&&git is not installed" invites a retry with a
        // different program; "there is no shell here" invites one call per program, which is correct.
        var tool = Allowing(new StubProcessRunner(), "dotnet");

        var refusal = await Refused(tool, """{"command":"dotnet&&git"}""");

        Assert.Contains("no shell", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_allowlist_ignores_case_and_a_trailing_exe()
    {
        // Both sides are written by hand - the user's list in Settings, the model's guess - and neither
        // spelling is wrong. Nothing else is normalised: a name with a separator never gets this far.
        var runner = new StubProcessRunner();
        var tool = Allowing(runner, "Dotnet.exe");

        await Ok(tool, """{"command":"DOTNET","args":["--version"]}""");

        Assert.Equal("DOTNET --version", runner.LastLine);
    }

    [Fact]
    public async Task Arguments_reach_the_program_exactly_as_they_were_sent()
    {
        // The whole point of an argument list. A space inside one argument is part of that argument, and
        // nothing on the way through is allowed to split it into two - the moment anything here splits
        // on spaces it has become a shell.
        var runner = new StubProcessRunner();
        var tool = Allowing(runner, "git");

        await Ok(tool, """{"command":"git","args":["commit","-m","two words && more"]}""");

        Assert.Equal(["commit", "-m", "two words && more"], runner.Last!.Arguments);
    }

    [Fact]
    public async Task A_missing_args_array_runs_the_program_with_none()
    {
        // 'args' is optional in the schema, and a program invoked bare - 'git status' has a default,
        // 'dotnet' prints its help - is a legitimate call rather than a malformed one.
        var runner = new StubProcessRunner();
        var tool = Allowing(runner, "dotnet");

        await Ok(tool, """{"command":"dotnet"}""");

        Assert.Empty(runner.Last!.Arguments);
    }

    [Fact]
    public async Task An_args_entry_that_is_not_a_string_is_refused_before_anything_runs()
    {
        // Models send an object here when they mean a flag with a value. Refusing says which shape is
        // wanted; coercing would send the model's JSON to a compiler as a filename.
        var runner = new StubProcessRunner();
        var tool = Allowing(runner, "dotnet");

        var refusal = await Refused(tool, """{"command":"dotnet","args":["build",{"c":"Release"}]}""");

        Assert.Empty(runner.Requests);
        Assert.Contains("has to be a string", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_working_directory_is_resolved_under_the_open_folder()
    {
        // There is no 'cd', so this is the only way into a subfolder, and it goes through the same path
        // guard as every file operation rather than doing its own arithmetic.
        var runner = new StubProcessRunner();
        var tool = Allowing(runner, "dotnet");

        await Ok(tool, """{"command":"dotnet","args":["build"],"working_directory":"src"}""");

        Assert.Equal(Path.Combine(_root, "src"), runner.Last!.WorkingDirectory);
    }

    [Theory]
    [InlineData("..", "'..' is not allowed")]
    [InlineData("src/../..", "'..' is not allowed")]
    [InlineData("C:\\Windows", "cannot name a drive")]
    [InlineData("/etc", "cannot start with a slash")]
    [InlineData(".git", "off limits")]
    [InlineData("README.md", "is a file, not a folder")]
    [InlineData("nowhere", "does not exist")]
    public async Task A_working_directory_the_workspace_refuses_stops_the_run(string folder, string because)
    {
        // The gate the sandbox does contribute to this tool. A program cannot be started on a folder the
        // file tools could not read, which is what keeps 'run the build in ..\\..\\Windows' from working.
        var runner = new StubProcessRunner();
        var tool = Allowing(runner, "dotnet");

        var refusal = await Refused(
            tool,
            $$"""{"command":"dotnet","working_directory":{{Quote(folder)}}}""");

        Assert.Empty(runner.Requests);
        Assert.Contains(because, refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_users_timeout_is_a_ceiling_the_model_cannot_raise()
    {
        // A model asking for an hour gets the user's two minutes. Asking for less than the ceiling is
        // honoured, because a model that knows its command is quick is telling the truth cheaply.
        var runner = new StubProcessRunner();
        var tool = Tool(runner, agent =>
        {
            agent.AllowCommands = true;
            agent.AllowedCommands = ["dotnet"];
            agent.CommandTimeoutSeconds = 30;
        });

        await Ok(tool, """{"command":"dotnet","timeout_seconds":3600}""");
        Assert.Equal(TimeSpan.FromSeconds(30), runner.Last!.Timeout);

        await Ok(tool, """{"command":"dotnet","timeout_seconds":5}""");
        Assert.Equal(TimeSpan.FromSeconds(5), runner.Last!.Timeout);

        await Ok(tool, """{"command":"dotnet"}""");
        Assert.Equal(TimeSpan.FromSeconds(30), runner.Last!.Timeout);
    }

    [Fact]
    public async Task A_failing_build_is_reported_as_an_answer_and_not_as_a_broken_tool()
    {
        // The distinction the loop turns on. A non-zero exit code is what the tool exists to find out,
        // so it comes back as a successful call carrying bad news; reporting it as a tool failure would
        // have the model retry the compiler instead of reading it.
        var tool = Allowing(
            Answering(new ProcessRunResult
            {
                Started = true,
                ExitCode = 1,
                Output = "Program.cs(4,17): error CS1002: ; expected",
                Duration = TimeSpan.FromSeconds(2.5),
            }),
            "dotnet");

        var result = await Run(tool, """{"command":"dotnet","args":["build"]}""");

        Assert.True(result.Success, result.Content);
        Assert.Contains("Failed with exit code 1", result.Content, StringComparison.Ordinal);
        Assert.Contains("error CS1002", result.Content, StringComparison.Ordinal);
        Assert.Contains("exit 1", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_comes_back_fenced_so_a_compiler_error_cannot_end_the_block_early()
    {
        // Build output is full of backticks and quotes. Unfenced, one of them closes the block and the
        // rest of the error reads as prose the model then reasons about as if it were an instruction.
        var tool = Allowing(
            Answering(new ProcessRunResult { Started = true, ExitCode = 0, Output = "note: `x` was here" }),
            "dotnet");

        var content = await Ok(tool, """{"command":"dotnet","args":["build"]}""");

        Assert.Contains("```\nnote: `x` was here\n```", content.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_program_that_printed_nothing_says_so_rather_than_coming_back_empty()
    {
        // A great many tools succeed silently. An empty result reads as a broken tool, and the model
        // spends its next step calling it again.
        var tool = Allowing(
            Answering(new ProcessRunResult { Started = true, ExitCode = 0, Output = string.Empty }),
            "git");

        var content = await Ok(tool, """{"command":"git","args":["add","."]}""");

        Assert.Contains("Succeeded", content, StringComparison.Ordinal);
        Assert.Contains("It printed nothing.", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_timeout_says_the_program_was_stopped_and_that_what_it_did_stands()
    {
        // Both halves matter. A model told only "timed out" assumes nothing happened, and a half-finished
        // migration or a partly written file is exactly the state where that assumption does damage.
        var tool = Allowing(
            Answering(new ProcessRunResult
            {
                Started = true,
                TimedOut = true,
                Output = "Restoring...",
                Duration = TimeSpan.FromSeconds(120),
            }),
            "dotnet");

        var result = await Run(tool, """{"command":"dotnet","args":["test"]}""");

        Assert.True(result.Success, result.Content);
        Assert.Contains("was stopped", result.Content, StringComparison.Ordinal);
        Assert.Contains("Whatever it had already done stands", result.Content, StringComparison.Ordinal);
        Assert.Contains("timed out", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Truncated_output_says_which_end_was_kept()
    {
        // The model has to know it is looking at a tail, or it reports "the build printed no errors"
        // about output whose first half was dropped.
        var tool = Allowing(
            Answering(new ProcessRunResult
            {
                Started = true,
                ExitCode = 0,
                Output = "the end",
                Truncated = true,
            }),
            "dotnet");

        var content = await Ok(tool, """{"command":"dotnet","args":["build"]}""");

        Assert.Contains("the beginning was dropped and the end kept", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_program_that_could_not_start_is_a_refusal_carrying_the_runners_reason()
    {
        // Not the same thing as a program that ran and failed, and the model acts on the difference:
        // one is something to fix in the code, the other is something to tell the user about the machine.
        var tool = Allowing(
            Answering(ProcessRunResult.Failed("'dotnet' could not be started. It may not be installed.")),
            "dotnet");

        var refusal = await Refused(tool, """{"command":"dotnet","args":["build"]}""");

        Assert.Contains("could not be started", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_approval_dialog_shows_every_argument_unabbreviated()
    {
        // The only preview in the application that is the thing being approved rather than a rendering
        // of it: the flag that makes 'git clean -xfd' destructive is in the argument list, so no argument
        // may be summarised away, however long the line gets.
        var tool = Allowing(new StubProcessRunner(), "git");

        var preview = await Describe(tool, """{"command":"git","args":["clean","-xfd","--dry-run"]}""");

        Assert.Equal("Run git clean -xfd --dry-run", preview.Summary);
        Assert.Contains("Program: git", preview.Preview, StringComparison.Ordinal);
        Assert.Contains("Argument: clean", preview.Preview, StringComparison.Ordinal);
        Assert.Contains("Argument: -xfd", preview.Preview, StringComparison.Ordinal);
        Assert.Contains("Argument: --dry-run", preview.Preview, StringComparison.Ordinal);
        Assert.Contains("Folder: the project root", preview.Preview, StringComparison.Ordinal);
        Assert.Contains("Timeout:", preview.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_dialog_says_there_is_no_shell_and_says_what_a_program_can_still_do()
    {
        // Said where the decision is made, not only in the documentation. The absence of a shell is the
        // reason a command that reads as dangerous is less so - and the reason one the user expects to
        // chain will not - and neither is guessable from the command line above it.
        var tool = Allowing(new StubProcessRunner(), "npm");

        var preview = await Describe(tool, """{"command":"npm","args":["install"],"working_directory":"src"}""");

        Assert.Equal("Run npm install in src", preview.Summary);
        Assert.Contains("Folder: src", preview.Preview, StringComparison.Ordinal);
        Assert.Contains("without a shell", preview.Preview, StringComparison.Ordinal);
        Assert.Contains("change files, reach the network", preview.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_call_that_will_be_refused_says_so_in_the_line_the_user_reads()
    {
        // Better than a dialog that asks permission for something that cannot happen: the user reads one
        // line, and "this will be refused" tells them the setting is the thing to change, not the answer.
        // Described one at a time, because both tools read the same live settings.
        var listed = await Describe(
            Allowing(new StubProcessRunner(), "dotnet"),
            """{"command":"curl"}""");

        var switched = await Describe(Tool(new StubProcessRunner()), """{"command":"dotnet"}""");

        Assert.Contains("not on the allowed list", listed.Summary, StringComparison.Ordinal);
        Assert.Contains("switched off", switched.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_tool_is_not_offered_to_the_model_while_it_could_only_refuse()
    {
        // Offering a tool that refuses every call is worse than not offering it: the model spends a step
        // and an approval prompt learning that, and then reasons about why the build failed.
        var runner = new StubProcessRunner();

        Assert.False(((IAgentToolAvailability)Tool(runner)).IsAvailable);
        Assert.True(((IAgentToolAvailability)Allowing(runner, "dotnet")).IsAvailable);

        await _workspace.CloseAsync(Token);
        Assert.False(((IAgentToolAvailability)Allowing(runner, "dotnet")).IsAvailable);
    }

    [Fact]
    public void The_registry_withholds_the_definition_of_a_tool_that_is_unavailable()
    {
        // Availability is presentation, not enforcement - the tool still refuses on its own - but the
        // schema list is what the model is shown, so this is where a switched-off tool disappears.
        var runner = new StubProcessRunner();
        var read = new ReadFileTool(_workspace);

        var off = new AgentToolRegistry([read, Tool(runner)]);
        Assert.Equal(2, off.Definitions.Count);
        Assert.DoesNotContain("run_command", off.Available().Select(definition => definition.Name));

        var on = new AgentToolRegistry([read, Allowing(runner, "dotnet")]);
        Assert.Contains("run_command", on.Available().Select(definition => definition.Name));
    }

    [Fact]
    public void Running_a_program_is_the_one_risk_that_is_never_remembered()
    {
        // Asserted here rather than trusted, because the exemption lives in the agent loop and this is
        // the only tool it applies to: ten commands is ten questions, by design.
        Assert.Equal(AgentToolRisk.Execute, Tool(new StubProcessRunner()).Risk);
    }

    /// <summary>
    /// Builds the tool over a known settings baseline, then whatever the test wants changed.
    /// </summary>
    /// <remarks>
    /// The reset is the point. Every tool here reads the same live settings tree, so a test that only
    /// switched something on would inherit whatever the previous line left behind - and the defaults
    /// ship with a populated allowlist, which is not a neutral starting point for an allowlist test.
    /// </remarks>
    private RunCommandTool Tool(StubProcessRunner runner, Action<AgentSettings>? arrange = null)
    {
        _settings.With<AgentSettings>(agent =>
        {
            agent.AllowCommands = false;
            agent.AllowedCommands = [];
            agent.CommandTimeoutSeconds = 120;
            agent.MaxCommandOutputCharacters = 20_000;

            arrange?.Invoke(agent);
        });

        return new RunCommandTool(_workspace, _settings, runner);
    }

    /// <summary>Running switched on, and exactly these programs allowed.</summary>
    private RunCommandTool Allowing(StubProcessRunner runner, params string[] allowed) =>
        Tool(runner, agent =>
        {
            agent.AllowCommands = true;
            agent.AllowedCommands = [.. allowed];
        });

    /// <summary>A runner that answers every request the same way.</summary>
    private static StubProcessRunner Answering(ProcessRunResult result) => new(_ => result);

    /// <summary>Runs a tool the way a turn does, and asserts it answered.</summary>
    private static async Task<string> Ok(IAgentTool tool, string arguments)
    {
        var result = await Run(tool, arguments);

        Assert.True(result.Success, result.Content);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary));

        return result.Content;
    }

    /// <summary>Runs a tool and asserts it refused, returning the sentence the model is shown.</summary>
    private static async Task<string> Refused(IAgentTool tool, string arguments)
    {
        var result = await Run(tool, arguments);

        Assert.False(result.Success, result.Content);
        Assert.False(string.IsNullOrWhiteSpace(result.Content));

        return result.Content;
    }

    private static async Task<AgentToolResult> Run(IAgentTool tool, string arguments)
    {
        Assert.True(AgentToolArguments.TryParse(arguments, out var parsed, out var error), error);

        return await tool.ExecuteAsync(parsed, Token);
    }

    /// <summary>
    /// Describes a call, flattening the preview so a test can read both halves without a null check.
    /// </summary>
    /// <remarks>
    /// A refused-in-advance call has a summary and no body, which is a legitimate answer rather than a
    /// missing one, so the body comes back empty instead of null.
    /// </remarks>
    private static async Task<(string Summary, string Preview)> Describe(
        IAgentToolPreview tool,
        string arguments)
    {
        Assert.True(AgentToolArguments.TryParse(arguments, out var parsed, out var error), error);

        var preview = await tool.DescribeAsync(parsed, Token);

        Assert.False(string.IsNullOrWhiteSpace(preview.Summary));

        return (preview.Summary!, preview.Preview ?? string.Empty);
    }

    /// <summary>A value as JSON, so a backslash in a test case survives being written into one.</summary>
    private static string Quote(string value) => JsonSerializer.Serialize(value);
}
