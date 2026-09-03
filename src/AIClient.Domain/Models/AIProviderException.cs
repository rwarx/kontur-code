using AIClient.Domain.Enums;

namespace AIClient.Domain.Models;

/// <summary>
/// Every provider failure, already classified and already carrying a sentence fit to
/// show a human. Providers translate their native errors into this once, so the UI and
/// the retry logic never inspect status codes or provider JSON.
/// </summary>
public sealed class AIProviderException : Exception
{
    public AIProviderException(
        AIErrorKind kind,
        string userMessage,
        string? technicalDetails = null,
        string? providerId = null,
        Exception? innerException = null)
        : base(userMessage, innerException)
    {
        Kind = kind;
        UserMessage = userMessage;
        TechnicalDetails = technicalDetails;
        ProviderId = providerId;
    }

    public AIErrorKind Kind { get; }

    /// <summary>Plain sentence for the error card. Contains no credentials and no stack trace.</summary>
    public string UserMessage { get; }

    /// <summary>Status code, response body excerpt and exception type, shown behind "Technical details".</summary>
    public string? TechnicalDetails { get; }

    public string? ProviderId { get; }

    /// <summary>
    /// True when trying again unchanged could plausibly succeed. A bad key or a
    /// context overflow will fail identically on retry, so those return false.
    /// </summary>
    public bool IsRetryable => Kind is
        AIErrorKind.Timeout or
        AIErrorKind.NetworkError or
        AIErrorKind.ServerError or
        AIErrorKind.ServiceUnavailable or
        AIErrorKind.RateLimited or
        AIErrorKind.ModelUnavailable;
}
