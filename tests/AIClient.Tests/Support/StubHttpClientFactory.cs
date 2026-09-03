namespace AIClient.Tests.Support;

/// <summary>
/// Hands out clients over one shared <see cref="FakeHttpMessageHandler"/>.
/// </summary>
/// <remarks>
/// Providers resolve their client by name through the factory, which is the seam that makes
/// them testable without a network. The handler is shared so a test can queue responses and
/// read back requests regardless of how many clients the provider asked for, and is not
/// disposed with the client for the same reason.
/// </remarks>
public sealed class StubHttpClientFactory(FakeHttpMessageHandler handler) : IHttpClientFactory
{
    public List<string> RequestedNames { get; } = [];

    public HttpClient CreateClient(string name)
    {
        RequestedNames.Add(name);
        return new HttpClient(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }
}
