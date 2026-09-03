namespace AIClient.Domain.Enums;

/// <summary>
/// Stable, provider-agnostic classification of every failure the user can hit.
/// The UI switches on this to pick a human sentence and to decide whether a
/// Retry button makes sense; it never parses HTTP status codes itself.
/// </summary>
public enum AIErrorKind
{
    /// <summary>Cause could not be classified. Show the technical details prominently.</summary>
    Unknown = 0,

    /// <summary>HTTP 401. The key is missing, malformed or revoked.</summary>
    InvalidApiKey,

    /// <summary>HTTP 403. The key is valid but not allowed to use this model/endpoint.</summary>
    PermissionDenied,

    /// <summary>HTTP 404. Endpoint or model id does not exist.</summary>
    NotFound,

    /// <summary>HTTP 429. Too many requests, or the account is out of credit.</summary>
    RateLimited,

    /// <summary>HTTP 408, or the client-side timeout elapsed.</summary>
    Timeout,

    /// <summary>HTTP 5xx. The provider is broken on its side; retrying may work.</summary>
    ServerError,

    /// <summary>HTTP 502/503. Provider is up but the upstream model host is not.</summary>
    ServiceUnavailable,

    /// <summary>DNS failure, no route, TLS failure, or the machine is offline.</summary>
    NetworkError,

    /// <summary>The request exceeded the model's context window.</summary>
    ContextLengthExceeded,

    /// <summary>The model id was accepted by the API surface but is not currently servable.</summary>
    ModelUnavailable,

    /// <summary>HTTP 400 for reasons other than context length (bad parameter, unsupported field).</summary>
    InvalidRequest,

    /// <summary>The provider refused the content on policy grounds.</summary>
    ContentFiltered,

    /// <summary>The user pressed Stop. Not an error, but travels the same channel.</summary>
    Cancelled,

    /// <summary>No API key has been configured for this provider yet.</summary>
    NotConfigured,
}
