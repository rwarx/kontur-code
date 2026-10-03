namespace AIClient.Application.DTOs;

/// <summary>
/// One page to fetch: where from, and the two bounds on what it may cost.
/// </summary>
/// <remarks>
/// The URL is a string rather than a <see cref="Uri"/> because it arrived from a language model and has
/// to be validated before it is trusted as one - the same reason a command's arguments arrive as text.
/// This is a request object, not a gate; whether the network may be reached at all was decided in the
/// Application layer before the call arrived, and whether the address is one that may be connected to is
/// decided by the fetcher itself, at connect time, against the IP rather than the name.
/// </remarks>
public sealed record HttpFetchRequest
{
    /// <summary>The absolute http or https URL to fetch.</summary>
    public required string Url { get; init; }

    /// <summary>How long the whole fetch may take, including redirects and reading the body.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Characters of the reduced page text kept before the rest is dropped.</summary>
    public int MaxResponseCharacters { get; init; } = 20_000;
}

/// <summary>
/// What fetching a page produced.
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <see cref="ProcessRunResult"/>, and for the same reasons. Nothing here is thrown:
/// a refused address, a dead host and a timeout are all ordinary outcomes the caller turns into a
/// sentence for a model, not exceptions for it to catch.
/// </para>
/// <para>
/// <see cref="Started"/> is false when there is no response to report at all - the URL was refused, the
/// host could not be reached, the connection timed out. An HTTP error such as 404 or 500 is a response
/// and so has <see cref="Started"/> true and a <see cref="StatusCode"/>: "the page said no" and "there
/// was no page" call for different next moves from whoever reads this.
/// </para>
/// </remarks>
public sealed record HttpFetchResult
{
    /// <summary>Whether a response was received at all.</summary>
    public required bool Started { get; init; }

    /// <summary>The HTTP status code, or null when no response was received.</summary>
    public int? StatusCode { get; init; }

    /// <summary>The status reason phrase, when the response carried one.</summary>
    public string? ReasonPhrase { get; init; }

    /// <summary>The response's content type, without its parameters.</summary>
    public string? ContentType { get; init; }

    /// <summary>The URL the body actually came from, which differs from the request after a redirect.</summary>
    public string? FinalUrl { get; init; }

    /// <summary>The response body, reduced to text and capped.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Bytes read from the network before any text reduction.</summary>
    public long Bytes { get; init; }

    /// <summary>Set when the body was cut short at a cap, whether the byte ceiling or the character one.</summary>
    public bool Truncated { get; init; }

    /// <summary>Set when the fetch was abandoned for exceeding its time limit.</summary>
    public bool TimedOut { get; init; }

    /// <summary>How long the fetch took.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Why nothing was fetched, when nothing was. A sentence safe to hand to a model: it names what went
    /// wrong without the internals of how it was attempted.
    /// </summary>
    public string? Error { get; init; }

    public static HttpFetchResult Failed(string error, bool timedOut = false) =>
        new() { Started = false, Error = error, TimedOut = timedOut };
}
