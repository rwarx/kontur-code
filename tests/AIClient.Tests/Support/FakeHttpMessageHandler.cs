using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace AIClient.Tests.Support;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers from a script and records what it was asked.
/// </summary>
/// <remarks>
/// This is what lets the provider tests be real tests without a network or a key. They drive
/// the actual <c>OpenRouterProvider</c> and <c>NvidiaProvider</c> - their URL building, their
/// headers, their SSE reading, their error mapping - against canned bytes. Section 36 requires
/// that provider tests not depend on a committed API key; nothing here needs one.
///
/// Requests are recorded with their body read eagerly, because the caller disposes the request
/// message as soon as the response is returned and the content would be unreadable afterwards.
/// </remarks>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();

    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>The most recent request, which is what a single-call test wants.</summary>
    public RecordedRequest LastRequest =>
        Requests.Count > 0 ? Requests[^1] : throw new InvalidOperationException("No request was sent.");

    /// <summary>Queues an arbitrary response.</summary>
    public FakeHttpMessageHandler Respond(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responders.Enqueue(responder);
        return this;
    }

    /// <summary>Queues a JSON response.</summary>
    public FakeHttpMessageHandler RespondJson(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        Respond(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    /// <summary>Queues a <c>text/event-stream</c> response delivered as one buffer.</summary>
    public FakeHttpMessageHandler RespondSse(string body) => RespondSse([body]);

    /// <summary>
    /// Queues a <c>text/event-stream</c> response delivered in the given chunks, one per read.
    /// </summary>
    /// <remarks>
    /// The chunk boundaries are the point. Providers flush mid-line routinely, and a reader
    /// that assumes a read ends on a line boundary drops tokens under exactly that condition.
    /// </remarks>
    public FakeHttpMessageHandler RespondSse(IReadOnlyList<string> chunks) =>
        Respond(_ =>
        {
            var content = new StreamContent(new ChunkedStream(chunks));
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

    /// <summary>Queues a plain-text error response, as a gateway or proxy would send.</summary>
    public FakeHttpMessageHandler RespondError(HttpStatusCode status, string body = "") =>
        Respond(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

    /// <summary>Queues a transport failure, as an offline machine produces.</summary>
    public FakeHttpMessageHandler RespondThrow(Exception exception) =>
        Respond(_ => throw exception);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        Requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase),
            body));

        if (_responders.Count == 0)
        {
            throw new InvalidOperationException(
                $"No response was queued for {request.Method} {request.RequestUri}.");
        }

        return _responders.Dequeue()(request);
    }
}

/// <summary>One request as the handler saw it.</summary>
public sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    IReadOnlyDictionary<string, string> Headers,
    string? Body)
{
    public string? Header(string name) => Headers.GetValueOrDefault(name);
}
