using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace AIClient.Infrastructure.Processes;

/// <summary>
/// Starts a program, reads what it printed, and makes sure it is dead before returning.
/// </summary>
/// <remarks>
/// <para>
/// The whole of this class is the four ways running a child process goes wrong in practice, and none of
/// them is the launching:
/// </para>
/// <list type="bullet">
/// <item><b>The full pipe.</b> A program that prints more than the pipe buffer holds blocks forever if
/// nobody drains it, and a caller that waits for exit before reading has deadlocked. Both streams are
/// drained by event as they arrive, which is why the output is collected under a lock instead of being
/// read after the fact.</item>
/// <item><b>The orphan.</b> Killing a process does not kill what it started. <c>dotnet test</c> is a
/// launcher: kill it alone and the test host keeps running, keeps the files locked, and keeps writing.
/// Everything here kills the tree.</item>
/// <item><b>The unbounded output.</b> A restore prints tens of thousands of lines, and all of them would
/// otherwise end up in a language model's context. The cap keeps the tail, because that is where the
/// error is.</item>
/// <item><b>The inherited environment.</b> A child gets the parent's environment, and this parent holds
/// the user's API keys in its own. §26 of the specification forbids a key reaching a log; a program that
/// prints its environment is a log, so the secret-looking variables are removed before the child is
/// started.</item>
/// </list>
/// <para>
/// It deliberately does no policy. Whether this program may run at all, in this folder, was decided in
/// the Application layer before the call arrived; duplicating any of that here would produce two rules
/// that can disagree, and the one that disagrees quietly is the one that is wrong.
/// </para>
/// </remarks>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>
    /// How long to wait for a killed process to actually go away.
    /// </summary>
    /// <remarks>
    /// A kill is asynchronous, and the output handlers are still attached until the process object says
    /// it has exited. Waiting briefly lets the last lines arrive - which for a timed-out build are the
    /// most interesting ones - and giving up after a moment means an unkillable process cannot hang the
    /// run in its turn.
    /// </remarks>
    private static readonly TimeSpan Reaping = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Fragments that mark an environment variable as carrying a secret.
    /// </summary>
    /// <remarks>
    /// Matched as substrings, case-insensitively, which is deliberately over-eager: removing
    /// <c>SSH_AUTH_SOCK</c> or <c>KEYBOARD_LAYOUT</c> from a build's environment costs nothing worth
    /// having, and leaving <c>OPENROUTER_API_KEY</c> in it costs the user their key the first time a
    /// script prints <c>env</c>. Erring the other way would mean deciding, for every variable on an
    /// unknown machine, that it is safe to hand to an unknown program.
    /// </remarks>
    private static readonly string[] SecretMarkers =
    [
        "KEY", "TOKEN", "SECRET", "PASSWORD", "PASSWD", "CREDENTIAL", "AUTH", "SESSION", "COOKIE",
        "PRIVATE", "SIGNATURE", "LICENSE", "APIKEY", "ACCESS_ID", "CLIENT_ID", "CONNECTIONSTRING",
    ];

    /// <summary>
    /// Variables removed by name rather than by pattern.
    /// </summary>
    /// <remarks>
    /// This application's own configuration prefix, because <c>AICLIENT_Providers__Nvidia</c> matches
    /// nothing above and a future setting under it may well be a secret - and because a child process has
    /// no business reading the configuration of the program that started it.
    /// </remarks>
    private const string OwnPrefix = "AICLIENT_";

    private readonly ILogger<ProcessRunner> _logger;

    public ProcessRunner(ILogger<ProcessRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public async Task<ProcessRunResult> RunAsync(
        ProcessRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Directory.Exists(request.WorkingDirectory))
        {
            return ProcessRunResult.Failed("The folder to run in no longer exists.");
        }

        var output = new OutputBuffer(request.MaxOutputCharacters);
        using var process = new Process { StartInfo = Describe(request) };

        process.OutputDataReceived += (_, args) => output.Add(args.Data);
        process.ErrorDataReceived += (_, args) => output.Add(args.Data);

        var clock = Stopwatch.StartNew();

        try
        {
            if (!process.Start())
            {
                // Documented as possible when an existing process was reused, which cannot happen
                // without UseShellExecute. Handled rather than asserted, because the alternative is a
                // NullReferenceException in a code path nobody can reproduce.
                return ProcessRunResult.Failed($"'{request.FileName}' did not start.");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // The program name only. An argument list can hold a token a user pasted into a chat, and a
            // failure to start is exactly the moment a naive log would write the whole command line.
            _logger.LogWarning(ex, "The program {Program} could not be started.", request.FileName);

            return ProcessRunResult.Failed(
                $"'{request.FileName}' could not be started. It may not be installed, or not be on PATH. "
                + "Ask the user rather than guessing at another name for it.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Closed immediately, which is the point of having redirected it. A program that prompts for
        // input - a package manager asking to confirm, a tool asking for a password - reads end of file
        // and gives up, instead of blocking until the timeout kills it and reporting nothing useful.
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The program exited before the handle could be closed. Nothing to close, nothing to do.
        }

        var timedOut = false;

        try
        {
            timedOut = !await WaitAsync(process, request.Timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Killed before the exception is allowed out, so that Stop leaves nothing behind. The
            // caller sees cancellation, which is what it asked for; the machine sees no orphan.
            Kill(process, request.FileName);
            throw;
        }

        if (timedOut)
        {
            Kill(process, request.FileName);
        }

        clock.Stop();

        _logger.LogInformation(
            "Ran {Program} for {Elapsed} ms, exit {Exit}.",
            request.FileName,
            (long)clock.Elapsed.TotalMilliseconds,
            timedOut ? "killed" : process.HasExited ? process.ExitCode.ToString() : "unknown");

        return new ProcessRunResult
        {
            Started = true,
            ExitCode = timedOut || !process.HasExited ? null : process.ExitCode,
            Output = output.ToString(),
            TimedOut = timedOut,
            Truncated = output.Truncated,
            Duration = clock.Elapsed,
        };
    }

    /// <summary>
    /// Builds the start info, and it is the absence of things that matters here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>UseShellExecute = false</c> is the load-bearing line: with it true, Windows hands the string to
    /// the shell and every operator in an argument becomes syntax. False also makes redirection possible,
    /// makes <see cref="ProcessStartInfo.ArgumentList"/> the way arguments are passed, and means the
    /// program is looked up rather than a file association being consulted - so a call naming a
    /// <c>.txt</c> cannot open an editor.
    /// </para>
    /// <para>
    /// The output encoding is forced to UTF-8 rather than left to the console's code page. Every modern
    /// toolchain writes UTF-8; the alternative default on a non-English Windows is a legacy OEM page, and
    /// a build error in Russian read as UTF-8 arrives as line noise. The cost is the reverse case - a
    /// legacy tool's non-ASCII output shows replacement characters - which is the rarer half by a wide
    /// margin and does not lose the ASCII around it.
    /// </para>
    /// </remarks>
    private static ProcessStartInfo Describe(ProcessRunRequest request)
    {
        var info = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // Closed rather than left inherited. A program that asks a question gets an immediate end of
            // input and exits, instead of waiting for a keystroke nobody is there to send until the
            // timeout kills it.
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in request.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        Sanitise(info.Environment);

        return info;
    }

    /// <summary>
    /// Removes the variables a child process should not be told about.
    /// </summary>
    /// <remarks>
    /// The dictionary starts as a copy of this process's environment, so this is a removal pass rather
    /// than a construction. Building the child's environment from nothing would be stricter and is the
    /// wrong trade: PATH, SystemRoot, TEMP and a dozen others are what make a toolchain work at all, and
    /// a list of those maintained by hand would be wrong on the first machine that differs.
    /// </remarks>
    private static void Sanitise(IDictionary<string, string?> environment)
    {
        var doomed = environment.Keys
            .Where(key => key.StartsWith(OwnPrefix, StringComparison.OrdinalIgnoreCase) || IsSecret(key))
            .ToArray();

        foreach (var key in doomed)
        {
            environment.Remove(key);
        }
    }

    private static bool IsSecret(string key)
    {
        foreach (var marker in SecretMarkers)
        {
            if (key.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Waits for exit. False means the time ran out; an exception means the caller cancelled.
    /// </summary>
    /// <remarks>
    /// The two are separated because they are answered differently: a timeout is reported to the model as
    /// a result it can act on, and a cancellation is the user having pressed Stop, which ends the run and
    /// is told to nobody. A single linked token would have collapsed them into one indistinguishable
    /// <see cref="OperationCanceledException"/>.
    /// </remarks>
    private static async Task<bool> WaitAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Kills the process and everything it started, and never throws for doing so.</summary>
    private void Kill(Process process, string program)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception
            or NotSupportedException or AggregateException)
        {
            // Exited between the check and the kill, or a child that cannot be killed. Nothing useful
            // to do either way, and throwing here would replace a timeout the model can read with a
            // failure it cannot.
            _logger.LogWarning(ex, "Could not stop {Program} cleanly.", program);
            return;
        }

        try
        {
            // Blocking, briefly, and on purpose: the output handlers are still attached, and the last
            // lines a killed build wrote are the ones worth having.
            process.WaitForExit((int)Reaping.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            _logger.LogWarning(ex, "Could not wait for {Program} to stop.", program);
        }
    }

    /// <summary>
    /// Collects both streams into one capped buffer, keeping the end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Locked because the two redirection callbacks arrive on thread-pool threads and there is no
    /// ordering between them. The lock is held for an append, which is short enough that a chatty build
    /// does not contend on it meaningfully.
    /// </para>
    /// <para>
    /// The cap is enforced by dropping from the front once the buffer is over it, which is what makes the
    /// tail the part that survives. The alternative - stopping at the cap - keeps a restore's download
    /// log and throws away the compiler error that follows it.
    /// </para>
    /// </remarks>
    private sealed class OutputBuffer
    {
        private readonly StringBuilder _text = new();
        private readonly object _gate = new();
        private readonly int _max;

        public OutputBuffer(int max) => _max = Math.Max(1, max);

        public bool Truncated { get; private set; }

        public void Add(string? line)
        {
            // Null is the end of the stream rather than a blank line, and appending it would put a
            // trailing newline on every command's output.
            if (line is null)
            {
                return;
            }

            lock (_gate)
            {
                _text.AppendLine(line);

                if (_text.Length <= _max)
                {
                    return;
                }

                // Trimmed in one step to a margin below the cap rather than exactly to it, so that a
                // program printing a million short lines does not pay for a shift per line.
                var target = Math.Max(1, _max - (_max / 4));
                _text.Remove(0, _text.Length - target);
                Truncated = true;
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                return _text.ToString();
            }
        }
    }
}
