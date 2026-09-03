using System.Net;
using AIClient.Application.Services;
using AIClient.Domain.Enums;
using AIClient.Tests.Support;

namespace AIClient.Tests;

/// <summary>
/// Section 21: every failure the user can hit maps to a stable kind, a sentence a human can
/// act on, and a separate technical block.
/// </summary>
/// <remarks>
/// Centralising this mapping is what lets the UI switch on one enum instead of parsing status
/// codes, so the mapping itself is the thing worth testing. The retryability assertions matter
/// as much as the wording: offering Retry for a revoked key trains the user to ignore the
/// button that does work.
/// </remarks>
public sealed class ErrorMappingTests
{
    private const string Provider = "OpenRouter";
    private const string ProviderId = "openrouter";

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, AIErrorKind.InvalidApiKey)]
    [InlineData(HttpStatusCode.Forbidden, AIErrorKind.PermissionDenied)]
    [InlineData(HttpStatusCode.NotFound, AIErrorKind.NotFound)]
    [InlineData(HttpStatusCode.RequestTimeout, AIErrorKind.Timeout)]
    [InlineData(HttpStatusCode.TooManyRequests, AIErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.BadRequest, AIErrorKind.InvalidRequest)]
    [InlineData(HttpStatusCode.InternalServerError, AIErrorKind.ServerError)]
    [InlineData(HttpStatusCode.BadGateway, AIErrorKind.ServiceUnavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AIErrorKind.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout, AIErrorKind.ServiceUnavailable)]
    public void Each_status_from_the_specification_maps_to_its_kind(HttpStatusCode status, AIErrorKind expected)
    {
        var error = ProviderErrorMapper.FromHttpStatus(status, null, Provider, ProviderId);

        Assert.Equal(expected, error.Kind);
        Assert.Equal(ProviderId, error.ProviderId);
    }

    [Fact]
    public void An_unlisted_server_error_still_classifies_as_a_server_error()
    {
        // 507, 599 and whatever a proxy invents next are all "their side, try later".
        var error = ProviderErrorMapper.FromHttpStatus((HttpStatusCode)507, null, Provider, ProviderId);

        Assert.Equal(AIErrorKind.ServerError, error.Kind);
        Assert.Contains("507", error.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unlisted_client_error_is_unknown_rather_than_guessed_at()
    {
        var error = ProviderErrorMapper.FromHttpStatus((HttpStatusCode)418, null, Provider, ProviderId);

        Assert.Equal(AIErrorKind.Unknown, error.Kind);
    }

    [Fact]
    public void A_context_overflow_is_separated_from_an_ordinary_bad_request()
    {
        // Both arrive as HTTP 400, and the advice is completely different: one means
        // "start a new chat", the other means "the request was malformed".
        var error = ProviderErrorMapper.FromHttpStatus(
            HttpStatusCode.BadRequest, WireFixtures.ContextOverflowBody, Provider, ProviderId);

        Assert.Equal(AIErrorKind.ContextLengthExceeded, error.Kind);
        Assert.Contains("too long", error.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(error.IsRetryable);
    }

    [Theory]
    [InlineData("This model's maximum context length is 8192 tokens")]
    [InlineData("{\"error\":{\"code\":\"context_length_exceeded\"}}")]
    [InlineData("Please reduce the length of the messages")]
    [InlineData("Input is too long for requested model")]
    public void Overflow_is_recognised_from_the_wording_providers_actually_use(string body)
    {
        Assert.True(ProviderErrorMapper.LooksLikeContextOverflow(body));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{\"error\":{\"message\":\"Invalid value for 'temperature'\"}}")]
    public void An_unrelated_body_is_not_mistaken_for_an_overflow(string? body)
    {
        Assert.False(ProviderErrorMapper.LooksLikeContextOverflow(body));
    }

    [Fact]
    public void The_provider_own_wording_is_appended_to_ours()
    {
        // Ours says what to do; theirs says what happened. The user gets both.
        var error = ProviderErrorMapper.FromHttpStatus(
            HttpStatusCode.Unauthorized, WireFixtures.UnauthorizedBody, Provider, ProviderId);

        Assert.Contains("Settings → Providers", error.UserMessage, StringComparison.Ordinal);
        Assert.Contains("No auth credentials found", error.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Technical_details_carry_the_status_and_the_body_separately_from_the_message()
    {
        var error = ProviderErrorMapper.FromHttpStatus(
            HttpStatusCode.TooManyRequests, "{\"error\":{\"message\":\"slow down\"}}", Provider, ProviderId);

        Assert.NotNull(error.TechnicalDetails);
        Assert.Contains("HTTP 429", error.TechnicalDetails, StringComparison.Ordinal);
        Assert.Contains("slow down", error.TechnicalDetails, StringComparison.Ordinal);

        // Section 21 asks for a human description and a separate technical section, not one
        // blob: the status line belongs in the details, not in the sentence.
        Assert.DoesNotContain("HTTP 429", error.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void A_giant_error_body_is_truncated_before_it_reaches_the_UI()
    {
        var error = ProviderErrorMapper.FromHttpStatus(
            HttpStatusCode.InternalServerError, new string('x', 50_000), Provider, ProviderId);

        Assert.NotNull(error.TechnicalDetails);
        Assert.True(
            error.TechnicalDetails.Length < 3_000,
            $"Technical details were {error.TechnicalDetails.Length} characters long.");
        Assert.Contains("truncated", error.TechnicalDetails, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"error\":{\"message\":\"nested shape\"}}", "nested shape")]
    [InlineData("{\"error\":\"string shape\"}", "string shape")]
    [InlineData("{\"detail\":\"NVIDIA shape\"}", "NVIDIA shape")]
    [InlineData("{\"message\":\"bare shape\"}", "bare shape")]
    [InlineData("{\"title\":\"gateway shape\"}", "gateway shape")]
    public void The_message_is_extracted_from_every_envelope_shape_seen_in_the_wild(string body, string expected)
    {
        // Asserted through the public entry point rather than against the extraction helper:
        // what matters is that the provider's wording reaches the user, not how it was found.
        var error = ProviderErrorMapper.FromHttpStatus(HttpStatusCode.BadRequest, body, Provider, ProviderId);

        Assert.EndsWith($"\n\n{expected}", error.UserMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"error\":{\"code\":429}}")]
    [InlineData("")]
    public void A_body_with_nothing_to_extract_leaves_our_own_wording_alone(string body)
    {
        // An HTML error page from a corporate proxy is a routine response, not a bug: the
        // message stays as written and the raw body is still available behind the details.
        var error = ProviderErrorMapper.FromHttpStatus(HttpStatusCode.BadGateway, body, Provider, ProviderId);

        Assert.DoesNotContain("\n\n", error.UserMessage, StringComparison.Ordinal);
        Assert.Equal(AIErrorKind.ServiceUnavailable, error.Kind);
    }

    [Fact]
    public void An_already_classified_failure_passes_through_unchanged()
    {
        var original = new Domain.Models.AIProviderException(
            AIErrorKind.NotConfigured, "No API key.", null, ProviderId);

        Assert.Same(original, ProviderErrorMapper.FromException(original, Provider, ProviderId));
    }

    [Fact]
    public void A_client_side_timeout_is_told_apart_from_the_user_pressing_Stop()
    {
        // Both surface as cancellation. HttpClient signals its own timeout by wrapping a
        // TimeoutException, and that is the only distinguishing feature available.
        var timeout = new TaskCanceledException("timed out", new TimeoutException());
        var stopped = new OperationCanceledException("stopped");

        Assert.Equal(AIErrorKind.Timeout, ProviderErrorMapper.FromException(timeout, Provider, ProviderId).Kind);
        Assert.Equal(AIErrorKind.Cancelled, ProviderErrorMapper.FromException(stopped, Provider, ProviderId).Kind);
    }

    [Fact]
    public void A_stopped_generation_carries_no_technical_details()
    {
        // Pressing Stop is not a fault. A stack trace under it would read like a crash.
        var error = ProviderErrorMapper.FromException(
            new OperationCanceledException(), Provider, ProviderId);

        Assert.Null(error.TechnicalDetails);
        Assert.False(error.IsRetryable);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    [InlineData(HttpRequestError.Unknown)]
    public void Every_transport_failure_becomes_a_network_error_the_user_can_retry(HttpRequestError kind)
    {
        var error = ProviderErrorMapper.FromException(
            new HttpRequestException(kind, "socket said no"), Provider, ProviderId);

        Assert.Equal(AIErrorKind.NetworkError, error.Kind);
        Assert.True(error.IsRetryable);
        Assert.Contains(kind.ToString(), error.TechnicalDetails!, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_a_name_resolution_or_connection_failure_blames_the_connection()
    {
        // Telling someone to check their internet when TLS was intercepted by antivirus
        // sends them looking in the wrong place.
        var tls = ProviderErrorMapper.FromException(
            new HttpRequestException(HttpRequestError.SecureConnectionError, "handshake"), Provider, ProviderId);

        Assert.Contains("proxy or antivirus", tls.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("check your internet", tls.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unrecognised_exception_is_reported_with_its_type_and_not_swallowed()
    {
        var error = ProviderErrorMapper.FromException(
            new InvalidOperationException("something odd"), Provider, ProviderId);

        Assert.Equal(AIErrorKind.Unknown, error.Kind);
        Assert.Contains(nameof(InvalidOperationException), error.TechnicalDetails!, StringComparison.Ordinal);
        Assert.Contains("something odd", error.TechnicalDetails!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AIErrorKind.Timeout, true)]
    [InlineData(AIErrorKind.NetworkError, true)]
    [InlineData(AIErrorKind.ServerError, true)]
    [InlineData(AIErrorKind.ServiceUnavailable, true)]
    [InlineData(AIErrorKind.RateLimited, true)]
    [InlineData(AIErrorKind.ModelUnavailable, true)]
    [InlineData(AIErrorKind.InvalidApiKey, false)]
    [InlineData(AIErrorKind.PermissionDenied, false)]
    [InlineData(AIErrorKind.ContextLengthExceeded, false)]
    [InlineData(AIErrorKind.InvalidRequest, false)]
    [InlineData(AIErrorKind.ContentFiltered, false)]
    [InlineData(AIErrorKind.NotConfigured, false)]
    [InlineData(AIErrorKind.Cancelled, false)]
    public void Retry_is_offered_only_where_an_identical_request_could_succeed(AIErrorKind kind, bool retryable)
    {
        var error = new Domain.Models.AIProviderException(kind, "message");

        Assert.Equal(retryable, error.IsRetryable);
    }

    [Fact]
    public void No_mapped_error_ever_mentions_an_Authorization_header()
    {
        // Section 26. The mapper is the one component that formats a failure for display,
        // so it is the right place to guarantee the header name never leaks into the UI.
        var errors = new[]
        {
            ProviderErrorMapper.FromHttpStatus(HttpStatusCode.Unauthorized, WireFixtures.UnauthorizedBody, Provider, ProviderId),
            ProviderErrorMapper.FromException(new HttpRequestException(HttpRequestError.ConnectionError, "no route"), Provider, ProviderId),
            ProviderErrorMapper.FromException(new InvalidOperationException("odd"), Provider, ProviderId),
        };

        foreach (var error in errors)
        {
            Assert.DoesNotContain("Authorization", error.UserMessage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Bearer", error.UserMessage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Authorization", error.TechnicalDetails ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Bearer", error.TechnicalDetails ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }
}
