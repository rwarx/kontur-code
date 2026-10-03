using AIClient.Server;

namespace AIClient.Tests;

/// <summary>
/// The two controls that turn a loopback socket from "reachable by anything on the machine" into
/// "reachable only by this application".
/// </summary>
/// <remarks>
/// Both are asserted here rather than only in the wiring, because the failure mode is silence: a
/// rejected token and a refused origin both produce a blank panel and an error toast, and nothing
/// about them is visible in a build. A regression should fail a test, not a support request.
/// </remarks>
public sealed class SidecarSecurityTests
{
    // ----- the token -----

    [Fact]
    public void A_token_presented_exactly_is_accepted()
    {
        var token = new SidecarToken("s3cret-token-value");

        Assert.True(token.Matches("s3cret-token-value"));
    }

    [Theory]
    [InlineData("s3cret-token-valu")]
    [InlineData("s3cret-token-valueX")]
    [InlineData("S3CRET-TOKEN-VALUE")]
    [InlineData("")]
    [InlineData("Bearer s3cret-token-value")]
    public void Anything_that_is_not_the_token_is_refused(string presented)
    {
        var token = new SidecarToken("s3cret-token-value");

        // The case test is the one worth having: a comparison that folds case would accept a token the
        // sidecar never issued, and `Bearer` is already stripped before this point.
        Assert.False(token.Matches(presented));
    }

    [Fact]
    public void A_generated_token_is_not_a_constant()
    {
        // Two sidecars started in the same second must not share a credential. A hardcoded fallback
        // would make this trivially true to fail, which is why there is no fallback.
        var first = new SidecarToken();
        var second = new SidecarToken();

        Assert.NotEqual(first.Value, second.Value);
        Assert.True(first.Value.Length >= 40, "32 random bytes in base64 is at least 43 characters.");
    }

    [Fact]
    public void A_token_is_long_enough_to_not_be_guessable()
    {
        // 32 bytes of CSPRNG output. Anything shorter and the brute-force cost stops being a real
        // answer to "can a web page guess this".
        var token = new SidecarToken();

        Assert.Equal(44, token.Value.Length);
    }

    // ----- CORS -----

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("http://127.0.0.1:5173")]
    [InlineData("http://localhost:5173")]
    public void An_origin_this_application_produces_is_allowed(string origin) =>
        Assert.True(SidecarCors.IsAllowedOrigin(origin));

    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("http://evil.example.com")]
    [InlineData("http://192.168.1.10:8080")]
    [InlineData("http://10.0.0.5")]
    [InlineData("file:///C:/Users/someone/secret.html")]
    public void An_origin_a_web_page_could_carry_is_refused(string origin)
    {
        // This is the list the previous AllowAnyOrigin() permitted. A page served from any of these
        // origins can reach 127.0.0.1 and read the response, which is what made the missing token
        // matter.
        Assert.False(SidecarCors.IsAllowedOrigin(origin));
    }

    [Fact]
    public void A_non_loopback_http_origin_is_refused_even_on_its_own_host()
    {
        // 127.0.0.1.nip.io resolves to loopback but is not loopback as far as the origin string goes,
        // and an origin check that only compared hostnames would wave it through.
        Assert.False(SidecarCors.IsAllowedOrigin("http://127.0.0.1.nip.io"));
    }

    [Fact]
    public void An_https_loopback_origin_is_refused()
    {
        // The sidecar speaks plain HTTP on one address. An https origin is never ours, and allowing
        // "loopback on any scheme" would be a broader rule than the one the app needs.
        Assert.False(SidecarCors.IsAllowedOrigin("https://127.0.0.1:5173"));
    }
}