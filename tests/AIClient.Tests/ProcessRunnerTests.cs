using AIClient.Application.DTOs;
using AIClient.Infrastructure.Processes;
using AIClient.Tests.Support;

namespace AIClient.Tests;

/// <summary>
/// The part that actually starts a program, against programs that really run.
/// </summary>
/// <remarks>
/// <para>
/// The only place in this suite where a real child process is worth the flakiness. Everything asserted
/// here is a property of the operating system rather than of the code's intentions - a full pipe
/// deadlocks, a killed launcher leaves orphans, a chatty build fills memory, and a child inherits the
/// parent's environment - and a substituted runner would agree with whatever the implementation did.
/// </para>
/// <para>
/// Kept to programs that must exist for this suite to be running at all: the .NET CLI, and on Windows
/// the two system tools used to sleep and to print an environment. A machine without them skips out
/// loud rather than passing quietly.
/// </para>
/// </remarks>
public sealed class ProcessRunnerTests
{
    private readonly ProcessRunner _runner = new(new RecordingLogger<ProcessRunner>());

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Scratch => Path.GetTempPath();

    [Fact]
    public async Task A_program_that_ran_reports_its_exit_code_and_what_it_printed()
    {
        // The one piece of feedback in the whole tool set that does not come from the model's own
        // reasoning, so both halves have to arrive: the code the loop acts on, and the text it reads.
        var result = await Run("dotnet", ["--version"]);

        Assert.True(result.Started, result.Error);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.False(result.Truncated);
        Assert.Matches(@"^\d+\.\d+", result.Output.Trim());
    }

    [Fact]
    public async Task A_program_that_failed_still_counts_as_having_run()
    {
        // Started and failed is not the same state as never started, and the model is told which:
        // one is something to fix in the project, the other something to tell the user about the machine.
        var result = await Run("dotnet", ["--this-flag-does-not-exist"]);

        Assert.True(result.Started, result.Error);
        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEmpty(result.Output);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task A_program_that_is_not_installed_is_reported_without_a_stack_or_a_path()
    {
        // Section 26 reaches even here. The refusal names the program and says where to look, and it
        // carries neither the Win32 message nor the working directory - which on this platform holds
        // the user's account name.
        var result = await Run($"not-a-real-program-{Guid.CreateVersion7():n}", []);

        Assert.False(result.Started);
        Assert.NotNull(result.Error);
        Assert.Contains("PATH", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(Scratch, result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.ExitCode);
    }

    [Fact]
    public async Task A_working_directory_that_has_gone_is_refused_before_anything_starts()
    {
        // The folder is resolved by the workspace and then handed over, so it can be deleted in the
        // gap - and starting a process on a missing directory throws where nobody is catching.
        var result = await Run("dotnet", ["--version"], Path.Combine(Scratch, Guid.CreateVersion7().ToString("n")));

        Assert.False(result.Started);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Output_past_the_cap_keeps_the_end_and_drops_the_beginning()
    {
        // A build that fails prints its errors last, and a restore that succeeds prints thousands of
        // lines first. Keeping the head would throw away the only part worth reading.
        var full = await Run("dotnet", ["--help"]);
        Assert.True(full.Started, full.Error);

        var capped = await Run("dotnet", ["--help"], cap: 400);
        Assert.True(capped.Started, capped.Error);

        Assert.True(capped.Truncated);
        Assert.True(capped.Output.Length <= 400, $"kept {capped.Output.Length} characters");
        Assert.EndsWith(
            capped.Output.ReplaceLineEndings("\n"),
            full.Output.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_program_that_will_not_finish_is_stopped_and_says_so()
    {
        // The gate that keeps a hung test suite from holding the run open until the user gives up. What
        // it printed before it was killed still comes back: a hung build has usually said why.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Needs a sleeper this test can name without a shell.");
        }

        var result = await Run("ping", ["-n", "30", "127.0.0.1"], timeout: TimeSpan.FromSeconds(2));

        Assert.True(result.Started, result.Error);
        Assert.True(result.TimedOut);
        Assert.True(result.Duration < TimeSpan.FromSeconds(20), $"took {result.Duration}");
    }

    [Fact]
    public async Task Cancelling_a_run_kills_the_program_rather_than_letting_it_finish()
    {
        // The Stop button, and the reason it has to reach the child: a run cancelled while a build is
        // half done would otherwise leave the build holding its lock files after the turn had ended.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Needs a sleeper this test can name without a shell.");
        }

        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _runner.RunAsync(
            new ProcessRunRequest
            {
                FileName = "ping",
                Arguments = ["-n", "30", "127.0.0.1"],
                WorkingDirectory = Scratch,
                Timeout = TimeSpan.FromMinutes(5),
            },
            stopping.Token));
    }

    [Fact]
    public async Task A_child_process_is_not_given_this_applications_secrets()
    {
        // Section 26 says a key must never reach a log. A child that prints its environment - which is
        // one line of a build script - is a log, so the variables go rather than the printing being
        // trusted not to happen. Deliberately over-eager: a build that needs a variable called
        // LICENSE_KEY is a smaller problem than one that echoes an API key into a transcript.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Needs a way to print an environment that this test can name without a shell.");
        }

        var marker = $"AICLIENT_MARKER_{Guid.CreateVersion7():n}";
        var secret = $"MARKER_{Guid.CreateVersion7():n}_TOKEN";
        var innocent = $"MARKER_{Guid.CreateVersion7():n}_PLAIN";

        Environment.SetEnvironmentVariable(marker, "own-configuration");
        Environment.SetEnvironmentVariable(secret, "sk-must-not-appear");
        Environment.SetEnvironmentVariable(innocent, "ordinary-value");

        try
        {
            // 'cmd /c set' is the shell this application refuses the model, used here deliberately:
            // the runner is allowed to start anything, and the containment being tested is what the
            // child can read rather than what it can be asked to do.
            var result = await Run("cmd", ["/c", "set"], cap: 200_000);

            Assert.True(result.Started, result.Error);
            Assert.DoesNotContain(marker, result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sk-must-not-appear", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, result.Output, StringComparison.OrdinalIgnoreCase);

            // The pass is a removal, not a rebuild. Starting from nothing would break every toolchain
            // on the machine, so what is left has to be everything else.
            Assert.Contains(innocent, result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("PATH=", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(marker, null);
            Environment.SetEnvironmentVariable(secret, null);
            Environment.SetEnvironmentVariable(innocent, null);
        }
    }

    [Fact]
    public async Task A_program_waiting_to_be_asked_something_sees_the_end_of_its_input()
    {
        // Redirected and then closed. Left open, a program that prompts blocks on a pipe nobody will
        // ever write to and burns the whole timeout for an answer it could have defaulted.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Needs a program that reads standard input that this test can name.");
        }

        var result = await Run("cmd", ["/c", "set", "/p", "ANSWER=Continue? "], timeout: TimeSpan.FromSeconds(10));

        Assert.True(result.Started, result.Error);
        Assert.False(result.TimedOut, "the program was still waiting for input");
    }

    private Task<ProcessRunResult> Run(
        string program,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        int cap = 20_000) =>
        _runner.RunAsync(
            new ProcessRunRequest
            {
                FileName = program,
                Arguments = arguments,
                WorkingDirectory = workingDirectory ?? Scratch,
                Timeout = timeout ?? TimeSpan.FromSeconds(60),
                MaxOutputCharacters = cap,
            },
            Token);
}
