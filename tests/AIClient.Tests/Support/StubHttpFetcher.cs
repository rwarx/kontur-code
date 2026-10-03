using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;

namespace AIClient.Tests.Support;

/// <summary>
/// Stands in for the network fetcher while a tool's own schema and refusal wording are under test.
/// </summary>
/// <remarks>
/// <para>
/// Always returns the "not started" failure rather than pretending a fetch happened. A tool that
/// forgot to check <c>AllowNetwork</c>, or that reported success it never earned, would otherwise be
/// indistinguishable from a correct one.
/// </para>
/// <para>
/// The address guard itself - private and link-local ranges, redirect schemes, DNS rebinding - lives
/// in <c>HttpFetcher</c> and is asserted against the real implementation, not against this.
/// </para>
/// </remarks>
public sealed class StubHttpFetcher : IHttpFetcher
{
    public Task<HttpFetchResult> FetchAsync(
        HttpFetchRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new HttpFetchResult { Started = false, Error = "stub opens no sockets" });
}