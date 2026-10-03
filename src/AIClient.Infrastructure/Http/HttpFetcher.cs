using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace AIClient.Infrastructure.Http;

/// <summary>
/// Fetches a URL over http or https, refuses any address that is not public, and hands back the
/// response reduced to text.
/// </summary>
/// <remarks>
/// The address guard is the point of the class. A model asked to read a page can be talked into fetching
/// the cloud metadata endpoint or a service on the machine's own network, so the check is against the IP
/// a name resolves to, made at the moment of connecting and against the same addresses that are then
/// connected to - which closes the gap a DNS-rebinding attack lives in. Automatic redirects are off so
/// each hop is re-validated the same way; the body is read only up to a hard ceiling; and HTML is reduced
/// to its text. It does no policy beyond the address: whether the network may be reached at all is the
/// Application layer's to decide, and it decides it before the call arrives here.
/// </remarks>
public sealed class HttpFetcher : IHttpFetcher, IDisposable
{
    /// <summary>Redirects followed before the fetch gives up, each one re-validated.</summary>
    private const int MaxRedirects = 5;

    /// <summary>Bytes read from a response before the rest is dropped, whatever the character cap is.</summary>
    private const long MaxResponseBytes = 5 * 1024 * 1024;

    /// <summary>How long one regular expression may run over a page before it is abandoned.</summary>
    private static readonly TimeSpan RegexBudget = TimeSpan.FromSeconds(2);

    private readonly ILogger<HttpFetcher> _logger;
    private readonly HttpClient _client;

    public HttpFetcher(ILogger<HttpFetcher> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;

        var handler = new SocketsHttpHandler
        {
            // Redirects by hand, so each hop's address is re-validated by the connect guard below.
            AllowAutoRedirect = false,

            // No ambient state between requests: a cookie set by one fetch has no business travelling to
            // the next, and the agent is not a browser session.
            UseCookies = false,

            AutomaticDecompression = DecompressionMethods.All,

            // Refreshes pooled connections periodically, so a long-lived singleton does not pin a stale
            // DNS answer - which here is also how a once-validated address could quietly go bad.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(15),

            // The load-bearing line: every connection goes through here, where the address it would open a
            // socket to is checked before the socket exists.
            ConnectCallback = SafeConnectAsync,
        };

        _client = new HttpClient(handler, disposeHandler: true);

        // The whole fetch - redirects and body read included - shares one budget, managed per request
        // through a linked token, which HttpClient.Timeout does not model.
        _client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;

        _client.DefaultRequestHeaders.UserAgent.ParseAdd("AIClient/0.1");
        _client.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/xml;q=0.9,text/plain;q=0.8,*/*;q=0.5");
    }

    public async Task<HttpFetchResult> FetchAsync(
        HttpFetchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Uri.TryCreate(request.Url?.Trim(), UriKind.Absolute, out var uri))
        {
            return HttpFetchResult.Failed(
                "That is not a URL I can fetch. Send an absolute http or https address, such as "
                + "https://example.com/page.");
        }

        if (!IsFetchableScheme(uri))
        {
            return HttpFetchResult.Failed(
                $"Only http and https addresses can be fetched, and this one is '{uri.Scheme}'.");
        }

        var clock = Stopwatch.StartNew();

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(request.Timeout);

        try
        {
            return await FollowAsync(uri, request, clock, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The user pressed Stop. That ends the run and is told to nobody; let it out.
            throw;
        }
        catch (OperationCanceledException)
        {
            return HttpFetchResult.Failed(
                $"The fetch took longer than {request.Timeout.TotalSeconds:0}s and was stopped.",
                timedOut: true);
        }
        catch (HttpRequestException ex)
        {
            // Never a stack, and never the resolved IP: the message a model reads is the reason we
            // composed for a refusal, or a short description of a connection that failed.
            _logger.LogInformation(ex, "Fetch of {Host} failed.", uri.Host);
            return HttpFetchResult.Failed(Describe(ex));
        }
    }

    /// <summary>
    /// Follows the URL, and any redirects it returns, until it has a response that is not a redirect.
    /// </summary>
    /// <remarks>
    /// Each hop is a fresh request rather than a handler-driven redirect, so the scheme is re-checked here
    /// and the address re-validated by the connect guard. A relative Location is resolved against the URL
    /// it came from, which is how a redirect to "/login" is meant to be read.
    /// </remarks>
    private async Task<HttpFetchResult> FollowAsync(
        Uri start,
        HttpFetchRequest request,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        var current = start;

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!IsFetchableScheme(current))
            {
                return HttpFetchResult.Failed(
                    $"A redirect pointed at a '{current.Scheme}' address, which cannot be fetched.");
            }

            using var message = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await _client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
                {
                    current = new Uri(current, location);
                    continue;
                }

                return await ReadAsync(current, response, request, clock, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                response.Dispose();
            }
        }

        return HttpFetchResult.Failed(
            $"The address redirected more than {MaxRedirects} times without settling, so it was not read.");
    }

    /// <summary>
    /// Reads the response body up to the byte ceiling, decodes it, reduces HTML to text and caps it.
    /// </summary>
    private async Task<HttpFetchResult> ReadAsync(
        Uri final,
        HttpResponseMessage response,
        HttpFetchRequest request,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        var (bytes, truncatedBytes) = await DrainAsync(response, cancellationToken).ConfigureAwait(false);

        var contentType = response.Content.Headers.ContentType;
        var text = Decode(bytes, contentType);

        if (LooksLikeHtml(contentType, text))
        {
            text = ReduceHtml(text);
        }

        var cap = Math.Max(1_000, request.MaxResponseCharacters);
        var truncatedChars = text.Length > cap;

        if (truncatedChars)
        {
            // The head is kept, not the tail: a document says what it is at the top.
            text = text[..cap];
        }

        clock.Stop();

        return new HttpFetchResult
        {
            Started = true,
            StatusCode = (int)response.StatusCode,
            ReasonPhrase = response.ReasonPhrase,
            ContentType = contentType?.MediaType,
            FinalUrl = final.ToString(),
            Body = text,
            Bytes = bytes.Length,
            Truncated = truncatedBytes || truncatedChars,
            Duration = clock.Elapsed,
        };
    }

    /// <summary>Reads the body into memory, stopping at the byte ceiling.</summary>
    private static async Task<(byte[] Bytes, bool Truncated)> DrainAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        using var buffer = new MemoryStream();
        var rented = ArrayPool<byte>.Shared.Rent(81_920);
        var truncated = false;

        try
        {
            int read;
            while ((read = await stream.ReadAsync(rented, cancellationToken).ConfigureAwait(false)) > 0)
            {
                var room = MaxResponseBytes - buffer.Length;

                if (read >= room)
                {
                    buffer.Write(rented, 0, (int)room);
                    truncated = true;
                    break;
                }

                buffer.Write(rented, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return (buffer.ToArray(), truncated);
    }

    private static bool IsFetchableScheme(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    /// <summary>
    /// Turns bytes into text, using the charset the response declared and falling back to UTF-8.
    /// </summary>
    /// <remarks>
    /// A declared charset no encoding matches is not worth failing the whole fetch for: UTF-8 reads most
    /// of the modern web, and a page in an exotic legacy encoding read as UTF-8 loses its accents but
    /// keeps its structure - the half a model can still use.
    /// </remarks>
    private static string Decode(byte[] bytes, MediaTypeHeaderValue? contentType)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        var encoding = Encoding.UTF8;

        if (!string.IsNullOrWhiteSpace(contentType?.CharSet))
        {
            try
            {
                encoding = Encoding.GetEncoding(contentType.CharSet.Trim('"', '\''));
            }
            catch (ArgumentException)
            {
                encoding = Encoding.UTF8;
            }
        }

        return encoding.GetString(bytes);
    }

    private static bool LooksLikeHtml(MediaTypeHeaderValue? contentType, string text)
    {
        var media = contentType?.MediaType;

        if (media is not null)
        {
            if (media.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // A declared non-HTML type is believed: JSON that happens to contain "<html>" in a string is
            // not markup, and reducing it would corrupt it.
            if (media.Contains("json", StringComparison.OrdinalIgnoreCase)
                || media.Contains("xml", StringComparison.OrdinalIgnoreCase)
                || media.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // No usable type: sniff the first stretch for a doctype or an html/head/body tag.
        var head = text.Length > 1_000 ? text[..1_000] : text;

        return head.Contains("<!doctype html", StringComparison.OrdinalIgnoreCase)
            || head.Contains("<html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reduces HTML to the text a reader would see: script and style gone, tags stripped, entities
    /// decoded, whitespace collapsed.
    /// </summary>
    /// <remarks>
    /// Regular expressions rather than a parser, so there is no new dependency for what is a lossy
    /// reduction anyway. Each pass gets a time budget: the patterns are simple, but the input is a page a
    /// stranger wrote, and a pathological one should cost a fetch, not the process. A pass that times out
    /// leaves the text as it was - degraded rather than wrong.
    /// </remarks>
    private static string ReduceHtml(string html)
    {
        try
        {
            html = Regex.Replace(html, "<script[\\s\\S]*?</script>", " ", RegexOptions.IgnoreCase, RegexBudget);
            html = Regex.Replace(html, "<style[\\s\\S]*?</style>", " ", RegexOptions.IgnoreCase, RegexBudget);
            html = Regex.Replace(html, "<!--[\\s\\S]*?-->", " ", RegexOptions.None, RegexBudget);

            // Block-level ends become newlines, so the text keeps the shape a reader would see.
            html = Regex.Replace(html, "<(br|/p|/div|/li|/tr|/h[1-6]|/section|/article)\\s*/?>", "\n",
                RegexOptions.IgnoreCase, RegexBudget);

            html = Regex.Replace(html, "<[^>]+>", " ", RegexOptions.None, RegexBudget);
            html = WebUtility.HtmlDecode(html);

            html = Regex.Replace(html, "[ \\t\\f\\v\\r]+", " ", RegexOptions.None, RegexBudget);
            html = Regex.Replace(html, " *\\n *", "\n", RegexOptions.None, RegexBudget);
            html = Regex.Replace(html, "\\n{3,}", "\n\n", RegexOptions.None, RegexBudget);
        }
        catch (RegexMatchTimeoutException)
        {
            // Left as far as it got. Degraded text beats no fetch.
        }

        return html.Trim();
    }

    /// <summary>
    /// The connect guard: resolves the host, refuses the connection if any address it resolves to is
    /// not public, and otherwise opens the socket to exactly those addresses.
    /// </summary>
    /// <remarks>
    /// Wired as the handler's <see cref="SocketsHttpHandler.ConnectCallback"/>, so it runs for every
    /// connection the client opens, including each redirect hop. The name is resolved here and the socket
    /// is told to connect to the resolved addresses rather than the name, so the addresses that were
    /// checked are the addresses that are used - there is no second resolution for a rebind to slip
    /// through. Returning a raw <see cref="NetworkStream"/> is enough: the handler layers TLS over it for
    /// an https URL.
    /// </remarks>
    private static async ValueTask<Stream> SafeConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        IPAddress[] addresses;

        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }

        if (addresses.Length == 0)
        {
            throw new HttpRequestException($"The host '{host}' did not resolve to any address.");
        }

        foreach (var address in addresses)
        {
            if (IsBlocked(address))
            {
                // This wording is the sentinel Describe looks for; it never names the address itself.
                throw new HttpRequestException(
                    $"Refusing to connect to '{host}': it resolves to an address that is not public.");
            }
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            // Connects to exactly the addresses just validated, in order, with no fresh resolution.
            await socket.ConnectAsync(addresses, port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Decides whether an address is one the fetcher must not open a socket to: anything that is not a
    /// public, routable unicast address.
    /// </summary>
    /// <remarks>
    /// This is the whole of the SSRF defence, and it works on the address rather than the name because the
    /// name is the attacker's to choose. The cloud metadata endpoint (169.254.169.254) falls under
    /// link-local; loopback, the RFC 1918 private ranges, carrier-grade NAT and the benchmarking block are
    /// each named below. An IPv4-mapped IPv6 address is unwrapped first, so <c>::ffff:127.0.0.1</c> is
    /// judged as the loopback it is, and an address family that is neither v4 nor v6 is refused outright.
    /// </remarks>
    private static bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var octets = address.GetAddressBytes();

            return octets[0] switch
            {
                0 => true,                                  // 0.0.0.0/8, "this network"
                10 => true,                                 // 10.0.0.0/8 private
                127 => true,                                // 127.0.0.0/8 loopback
                100 => octets[1] >= 64 && octets[1] <= 127, // 100.64.0.0/10 carrier-grade NAT
                169 => octets[1] == 254,                    // 169.254.0.0/16 link-local (cloud metadata)
                172 => octets[1] >= 16 && octets[1] <= 31,  // 172.16.0.0/12 private
                192 => octets[1] == 168,                    // 192.168.0.0/16 private
                198 => octets[1] is 18 or 19,               // 198.18.0.0/15 benchmarking
                >= 224 => true,                             // 224.0.0.0/4 multicast and reserved above it
                _ => false,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return IPAddress.IsLoopback(address)
                || address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
                || address.IsIPv6UniqueLocal
                || address.Equals(IPAddress.IPv6Any)
                || address.Equals(IPAddress.IPv6None);
        }

        return true;
    }

    /// <summary>
    /// Turns an <see cref="HttpRequestException"/> into a sentence safe to hand to a model.
    /// </summary>
    /// <remarks>
    /// The refusals this class raises carry a message written to be read: it names the host, which the
    /// model already has, and never the resolved IP. Those pass straight through, found by walking the
    /// inner-exception chain the handler wraps them in. Anything else is a real transport failure whose
    /// message can carry a raw socket error, so it is replaced with one plain line - the model needs to
    /// know the page could not be reached, not how the socket failed.
    /// </remarks>
    private static string Describe(HttpRequestException exception)
    {
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            if (e.Message.Contains("Refusing to connect", StringComparison.Ordinal)
                || e.Message.Contains("did not resolve", StringComparison.Ordinal))
            {
                return e.Message;
            }
        }

        return "The address could not be reached.";
    }

    public void Dispose() => _client.Dispose();
}
