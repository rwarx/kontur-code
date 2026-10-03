using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;

namespace AIClient.Server;

/// <summary>
/// The one credential the sidecar has, and the reason it is not the open port it used to be.
/// </summary>
/// <remarks>
/// <para>
/// The server binds a plain-HTTP loopback socket, which every process on the machine and every web
/// page in the user's browser can reach. Being on loopback is not an authorisation boundary: a page
/// the user has open sends same-machine requests to <c>127.0.0.1</c> freely, and reads the responses
/// back under permissive CORS. So every route except the readiness probe requires a bearer token
/// that only this process and its launcher know.
/// </para>
/// <para>
/// The token is supplied by the launcher through <c>AICLIENT_AUTHTOKEN</c> and, when it is absent -
/// which is what happens if someone starts the sidecar by hand - one is generated here. Generating
/// rather than trusting a default matters: a hardcoded fallback token is a published constant, and a
/// published constant is not a credential at all.
/// </para>
/// <para>
/// This defends against a browser page, which is the realistic remote attacker for a desktop
/// application. It deliberately does not defend against another process running as the same Windows
/// user - that process can read the conversation database and the DPAPI-protected keys directly, and
/// DPAPI's whole purpose is that only that user can.
/// </para>
/// </remarks>
public sealed class SidecarToken
{
    /// <summary>The configuration key the launcher sets.</summary>
    public const string EnvironmentVariable = "AICLIENT_AUTHTOKEN";

    private const int GeneratedBytes = 32;

    public SidecarToken()
        : this(Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } supplied
            ? supplied
            : Generate())
    {
        // The value is never logged. The whole claim of this class is that the token is a secret, and
        // a launcher that prints its own environment has already given it away.
    }

    public SidecarToken(string value) => Value = value;

    /// <summary>The expected bearer token.</summary>
    public string Value { get; }

    /// <summary>
    /// Compares a presented token with this one without leaking where they first differ.
    /// </summary>
    /// <remarks>
    /// Both sides are hashed before comparison rather than compared as bytes directly. Comparing
    /// variable-length strings byte by byte returns early on the length, which tells a caller the
    /// token's length before it has earned any of it; hashing first makes the comparison fixed-width
    /// and therefore genuinely constant-time. SHA-256 is used as a comparator here, not as a
    /// protection: there is no secret to protect at rest, only a comparison to make uniform.
    /// </remarks>
    public bool Matches(string presented) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(Value)));

    private static string Generate() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(GeneratedBytes));
}

/// <summary>
/// Validates the single bearer token <see cref="SidecarToken"/> defines.
/// </summary>
public sealed class SidecarTokenHandler(
    SidecarToken token,
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "SidecarToken";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // A missing header is not a failed authentication, it is an absent one: the two produce
        // different statuses downstream, and 401 tells a caller what to do where 403 would not.
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)
            || !token.Matches(header["Bearer ".Length..]))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid sidecar token."));
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "kontur-code")],
            SchemeName);

        return Task.FromResult(
            AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"{SchemeName} realm=\"kontur-code-sidecar\"";
        return Task.CompletedTask;
    }
}

/// <summary>
/// Which browser origins the sidecar will answer, and which addresses it will bind.
/// </summary>
/// <remarks>
/// <para>
/// CORS is the second of the two controls and the weaker one. The token is what stops a web page
/// from doing anything: a page cannot read this application's environment, so it cannot produce a
/// valid <c>Authorization</c> header, and a preflight that passes CORS still fails authentication.
/// CORS is here so that such a page fails <em>early</em> and visibly, instead of being allowed to
/// attempt the request and learn only that it was rejected.
/// </para>
/// <para>
/// The three allowed origins are the ones this application legitimately produces. A packaged build
/// loads the renderer from <c>file://</c>, whose origin a browser reports as the literal string
/// <c>null</c>. A development build loads it from the Vite server on loopback. A non-browser client -
/// curl, a test, a readiness probe - sends no <c>Origin</c> header at all, which reaches the policy
/// as an empty string. Everything else is refused.
/// </para>
/// </remarks>
public static class SidecarCors
{
    /// <summary>The policy name, referenced once at registration and once at use.</summary>
    public const string PolicyName = "sidecar-loopback";

    /// <summary>The only address the sidecar will listen on.</summary>
    public const string LoopbackUrl = "http://127.0.0.1:45631";

    public static void Configure(CorsOptions options) =>
        options.AddPolicy(
            PolicyName,
            policy => policy
                .SetIsOriginAllowed(IsAllowedOrigin)
                .AllowAnyMethod()
                .AllowAnyHeader());

    /// <summary>Whether a browser origin is one of ours.</summary>
    public static bool IsAllowedOrigin(string origin)
    {
        if (origin.Length == 0)
        {
            // No Origin header: a native client rather than a page. The token still applies.
            return true;
        }

        if (string.Equals(origin, "null", StringComparison.Ordinal))
        {
            // file:// and sandboxed frames. The packaged renderer is one of these.
            return true;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && uri.IsLoopback;
    }

    /// <summary>
    /// Forces the listener onto loopback, whatever the command line asked for.
    /// </summary>
    /// <remarks>
    /// A host that can be pointed at <c>0.0.0.0</c> is a host on the network, and every guarantee
    /// this class offers assumes it is not. The launcher's <c>--urls</c> argument is therefore
    /// validated rather than honoured: a non-loopback request is refused at startup, loudly, instead
    /// of quietly opening a port.
    /// </remarks>
    public static void Bind(WebApplication app, ILogger logger)
    {
        if (app.Urls.Count == 0)
        {
            logger.LogInformation("No URL was supplied; binding {Url}.", LoopbackUrl);
            app.Urls.Clear();
            app.Urls.Add(LoopbackUrl);
            return;
        }

        foreach (var url in app.Urls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || !uri.IsLoopback)
            {
                throw new InvalidOperationException(
                    $"The sidecar refuses to bind {url}. It serves conversation content and makes "
                    + "provider calls on the user's key, so it listens on loopback only.");
            }
        }

        logger.LogInformation("Binding {Url}.", string.Join(", ", app.Urls));
    }

    /// <summary>A short, non-sensitive label for the token's source, for the startup log.</summary>
    public static string DescribeTokenSource() =>
        Environment.GetEnvironmentVariable(SidecarToken.EnvironmentVariable) is { Length: > 0 }
            ? "supplied by the launcher"
            : "generated for this process";
}