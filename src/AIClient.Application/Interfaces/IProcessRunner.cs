using AIClient.Application.DTOs;

namespace AIClient.Application.Interfaces;

/// <summary>
/// Runs one program and waits for it, capturing what it printed.
/// </summary>
/// <remarks>
/// <para>
/// Declared here and implemented in Infrastructure because starting a process is as much an
/// outside-world concern as opening a socket. The Application layer decides whether a command may run;
/// it should not also own the code that redirects pipes and kills process trees, and a test that wants
/// to check the deciding does not want to start <c>cmd.exe</c> to do it.
/// </para>
/// <para>
/// One method, and it is deliberately blocking. Streaming a program's output as it appears would be a
/// better experience and is a different design: the tool result the model reads is one string, and
/// producing it incrementally would mean a tool that returns before it knows whether it succeeded.
/// </para>
/// <para>
/// Nothing here throws for an ordinary failure. A program that does not exist, a working directory
/// that vanished, a run that hit its timeout - all come back as a
/// <see cref="ProcessRunResult"/>, because the caller's job is to turn them into a sentence for a
/// language model rather than to catch four kinds of exception.
/// </para>
/// </remarks>
public interface IProcessRunner
{
    /// <summary>
    /// Runs the program and returns once it has finished, been killed for running too long, or been
    /// cancelled.
    /// </summary>
    /// <remarks>
    /// Cancellation kills the process and everything it started. A run abandoned with its children
    /// still alive is worse than one that never started: the user pressed Stop and a build carried on
    /// writing to their files.
    /// </remarks>
    Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default);
}
