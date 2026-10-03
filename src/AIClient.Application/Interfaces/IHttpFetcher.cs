using AIClient.Application.DTOs;

namespace AIClient.Application.Interfaces;

/// <summary>
/// Fetches one URL and returns its content, reduced to text.
/// </summary>
/// <remarks>
/// <para>
/// Declared here and implemented in Infrastructure because opening a socket is an outside-world concern
/// the same way starting a process is. The Application layer decides whether the network may be reached;
/// it does not own the code that resolves a hostname, checks the address it resolved to, follows a
/// redirect and drains a response stream.
/// </para>
/// <para>
/// Nothing here throws for an ordinary failure. A URL that is refused for pointing at a private address,
/// a host that cannot be reached, a request that hit its timeout - all come back as a
/// <see cref="HttpFetchResult"/>, because the caller's job is to turn them into a sentence for a language
/// model rather than to catch several kinds of exception. Cancellation - the user pressing Stop - is the
/// one thing that is allowed out.
/// </para>
/// </remarks>
public interface IHttpFetcher
{
    /// <summary>
    /// Fetches the URL and returns once the response has been read, the fetch has timed out, or it has
    /// been refused.
    /// </summary>
    Task<HttpFetchResult> FetchAsync(HttpFetchRequest request, CancellationToken cancellationToken = default);
}
