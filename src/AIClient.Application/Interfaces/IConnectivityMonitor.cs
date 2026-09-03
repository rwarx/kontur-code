namespace AIClient.Application.Interfaces;

/// <summary>
/// Reports whether the machine can currently reach a provider (section 31).
/// </summary>
/// <remarks>
/// Deliberately not a poller. Nothing here sends a request of its own: an app that quietly
/// pings a server every few seconds to colour a strip is both surprising outbound traffic and
/// a thing to explain in a privacy note, and section 28 asks for neither. Instead this
/// combines two signals the machine already produces for free:
///
/// <list type="bullet">
///   <item>the operating system's network-availability notification, which is authoritative
///   when it says there is no adapter at all;</item>
///   <item>the outcome of requests the user has already asked for, reported back through
///   <see cref="ReportUnreachable"/> and <see cref="ReportReachable"/>.</item>
/// </list>
///
/// The second signal is what makes this honest. An adapter being up says nothing about the
/// internet - a captive portal, a router with no upstream, or a DNS outage all present as a
/// perfectly healthy connection - so a failed request is the only evidence that actually
/// distinguishes them, and a later success is the only evidence that clears it.
/// </remarks>
public interface IConnectivityMonitor
{
    /// <summary>False once either signal says a provider cannot be reached.</summary>
    bool IsOnline { get; }

    /// <summary>Raised when <see cref="IsOnline"/> changes. May arrive on any thread.</summary>
    event EventHandler<bool>? ConnectivityChanged;

    /// <summary>
    /// Called when a request failed for a reason that means the network itself is at fault -
    /// DNS, no route, or TLS. An HTTP status code is not one of those: a 401 or a 429 is proof
    /// the connection works.
    /// </summary>
    void ReportUnreachable();

    /// <summary>Called when a request reached the provider, whatever the provider then said.</summary>
    void ReportReachable();
}
