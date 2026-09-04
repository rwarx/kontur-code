using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;

namespace AIClient.Tests.Support;

/// <summary>
/// Stands in for the real process runner, recording what it was asked to run.
/// </summary>
/// <remarks>
/// <para>
/// The one place in this suite where substituting the implementation is the point rather than a
/// compromise. Everything worth asserting about <c>run_command</c> is what it decides before a program
/// starts - whether the name is allowed, how the arguments were split, which folder was resolved - and
/// a test that actually started a program would assert none of that while depending on which toolchains
/// happen to be installed on the machine running it.
/// </para>
/// <para>
/// It also fails loudly by default rather than returning a blank success, so a test that expected a
/// refusal and got a run can say which command got through.
/// </para>
/// </remarks>
public sealed class StubProcessRunner : IProcessRunner
{
    private readonly Func<ProcessRunRequest, ProcessRunResult> _answer;

    public StubProcessRunner()
        : this(_ => new ProcessRunResult { Started = true, ExitCode = 0, Output = string.Empty })
    {
    }

    public StubProcessRunner(Func<ProcessRunRequest, ProcessRunResult> answer) => _answer = answer;

    /// <summary>Every request, in order. Empty means nothing was run.</summary>
    public List<ProcessRunRequest> Requests { get; } = [];

    /// <summary>The last request, or null when nothing was run.</summary>
    public ProcessRunRequest? Last => Requests.Count == 0 ? null : Requests[^1];

    /// <summary>The command line as it would read, for asserting how arguments were split.</summary>
    public string LastLine => Last is null
        ? string.Empty
        : string.Join(" ", new[] { Last.FileName }.Concat(Last.Arguments));

    public Task<ProcessRunResult> RunAsync(
        ProcessRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);

        return Task.FromResult(_answer(request));
    }
}
