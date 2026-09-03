using System.Net;
using AIClient.Domain.Enums;
using AIClient.Domain.Models;

namespace AIClient.Application.Services;

/// <summary>
/// Converts transport and provider failures into a classified
/// <see cref="AIProviderException"/> carrying a sentence fit to show a human.
/// </summary>
/// <remarks>
/// Centralised so every provider produces identical wording for identical failures,
/// and so the mapping is unit-testable without a network. Nothing here ever includes
/// a request header or an API key in its output.
/// </remarks>
public static class ProviderErrorMapper
{
    /// <summary>Maps an HTTP status plus response body to a classified exception.</summary>
    /// <param name="statusCode">Status returned by the provider.</param>
    /// <param name="responseBody">Response body, already truncated by the caller.</param>
    /// <param name="providerName">Display name used in the message, e.g. "OpenRouter".</param>
    /// <param name="providerId">Provider id recorded on the exception.</param>
    public static AIProviderException FromHttpStatus(
        HttpStatusCode statusCode,
        string? responseBody,
        string providerName,
        string providerId)
    {
        var detail = ExtractProviderMessage(responseBody);
        var technical = BuildTechnicalDetails(statusCode, responseBody);

        // A 400 that mentions context length is a different problem from a 400 that
        // rejects a parameter, and the two need different advice.
        if (statusCode == HttpStatusCode.BadRequest && LooksLikeContextOverflow(responseBody))
        {
            return new AIProviderException(
                AIErrorKind.ContextLengthExceeded,
                "This conversation is too long for the selected model. Start a new chat, or switch to a model with a larger context window.",
                technical,
                providerId);
        }

        var (kind, message) = statusCode switch
        {
            HttpStatusCode.Unauthorized => (
                AIErrorKind.InvalidApiKey,
                $"{providerName} rejected the API key. Check it in Settings → Providers."),

            HttpStatusCode.Forbidden => (
                AIErrorKind.PermissionDenied,
                $"{providerName} refused this request. The key may lack access to this model, or the account may need billing enabled."),

            HttpStatusCode.NotFound => (
                AIErrorKind.NotFound,
                $"{providerName} does not recognise this model or endpoint. Refresh the model list in Settings."),

            HttpStatusCode.RequestTimeout => (
                AIErrorKind.Timeout,
                $"{providerName} took too long to respond."),

            HttpStatusCode.TooManyRequests => (
                AIErrorKind.RateLimited,
                $"{providerName} is rate-limiting this key, or the account is out of credit. Wait a moment and try again."),

            HttpStatusCode.BadRequest => (
                AIErrorKind.InvalidRequest,
                $"{providerName} rejected the request as invalid."),

            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout => (
                AIErrorKind.ServiceUnavailable,
                $"{providerName} is temporarily unavailable. This is usually brief - try again shortly."),

            HttpStatusCode.InternalServerError => (
                AIErrorKind.ServerError,
                $"{providerName} hit an internal error."),

            _ when (int)statusCode >= 500 => (
                AIErrorKind.ServerError,
                $"{providerName} returned an error ({(int)statusCode})."),

            _ => (
                AIErrorKind.Unknown,
                $"{providerName} returned an unexpected response ({(int)statusCode})."),
        };

        // The provider's own message is usually more specific than ours; append it.
        var finalMessage = string.IsNullOrWhiteSpace(detail) ? message : $"{message}\n\n{detail}";
        return new AIProviderException(kind, finalMessage, technical, providerId);
    }

    /// <summary>Maps a client-side transport exception.</summary>
    public static AIProviderException FromException(Exception exception, string providerName, string providerId)
    {
        return exception switch
        {
            AIProviderException known => known,

            // HttpClient reports its own timeout as a TaskCanceledException wrapping a
            // TimeoutException. That is the only way to tell "the request timed out" from
            // "the user pressed Stop", since both surface as cancellation.
            TaskCanceledException { InnerException: TimeoutException } => new AIProviderException(
                AIErrorKind.Timeout,
                $"The request to {providerName} timed out.",
                exception.Message,
                providerId,
                exception),

            OperationCanceledException => new AIProviderException(
                AIErrorKind.Cancelled,
                "Generation was stopped.",
                null,
                providerId,
                exception),

            HttpRequestException http => FromHttpRequestException(http, providerName, providerId),

            _ => new AIProviderException(
                AIErrorKind.Unknown,
                $"Something went wrong talking to {providerName}.",
                $"{exception.GetType().Name}: {exception.Message}",
                providerId,
                exception),
        };
    }

    private static AIProviderException FromHttpRequestException(
        HttpRequestException exception,
        string providerName,
        string providerId)
    {
        // HttpRequestError distinguishes "the machine is offline" from "the host said no",
        // which matters because only the former should tell the user to check their connection.
        var (kind, message) = exception.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => (
                AIErrorKind.NetworkError,
                $"Could not resolve {providerName}'s address. Check your internet connection."),

            HttpRequestError.ConnectionError => (
                AIErrorKind.NetworkError,
                $"Could not connect to {providerName}. Check your internet connection."),

            HttpRequestError.SecureConnectionError => (
                AIErrorKind.NetworkError,
                $"The secure connection to {providerName} failed. A proxy or antivirus may be intercepting HTTPS."),

            _ => (
                AIErrorKind.NetworkError,
                $"Could not reach {providerName}. Check your internet connection."),
        };

        return new AIProviderException(
            kind,
            message,
            $"{exception.GetType().Name} ({exception.HttpRequestError}): {exception.Message}",
            providerId,
            exception);
    }

    /// <summary>
    /// Pulls the human-readable text out of an OpenAI-style error envelope
    /// (<c>{"error":{"message":"..."}}</c>), tolerating providers that deviate.
    /// </summary>
    internal static string? ExtractProviderMessage(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(responseBody);
            var root = document.RootElement;

            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return error.GetString();
                }

                if (error.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    error.TryGetProperty("message", out var errorMessage) &&
                    errorMessage.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return errorMessage.GetString();
                }
            }

            // NVIDIA and some gateways return a bare {"detail": "..."} or {"message": "..."}.
            foreach (var name in (string[])["detail", "message", "title"])
            {
                if (root.TryGetProperty(name, out var value) &&
                    value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return value.GetString();
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON - an HTML error page from a proxy, most likely. Nothing to extract.
        }

        return null;
    }

    /// <summary>
    /// Detects a context-window overflow. Providers signal it in prose rather than with a
    /// distinct status code, so matching on wording is the only option available.
    /// </summary>
    public static bool LooksLikeContextOverflow(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        ReadOnlySpan<string> markers =
        [
            "context_length_exceeded",
            "context length",
            "maximum context",
            "too many tokens",
            "reduce the length",
            "input is too long",
            "prompt is too long",
            "exceeds the maximum",
        ];

        foreach (var marker in markers)
        {
            if (responseBody.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildTechnicalDetails(HttpStatusCode statusCode, string? responseBody)
    {
        var status = $"HTTP {(int)statusCode} {statusCode}";
        return string.IsNullOrWhiteSpace(responseBody)
            ? status
            : $"{status}\n\n{Truncate(responseBody, 2000)}";
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "\n… (truncated)";
}
