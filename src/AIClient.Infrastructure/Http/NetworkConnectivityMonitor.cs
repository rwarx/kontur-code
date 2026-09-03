using System.Net.NetworkInformation;
using AIClient.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace AIClient.Infrastructure.Http;

/// <summary>
/// <see cref="IConnectivityMonitor"/> over the Windows network-change notifications.
/// </summary>
/// <remarks>
/// The two signals are kept as separate fields rather than collapsed into one bool, because
/// they can disagree and the disagreement is meaningful. Losing the adapter must show the
/// strip immediately; regaining it must clear the strip even if the last request failed,
/// since the failure is now stale evidence about a connection that no longer exists.
/// </remarks>
public sealed class NetworkConnectivityMonitor : IConnectivityMonitor, IDisposable
{
    private readonly ILogger<NetworkConnectivityMonitor> _logger;
    private readonly object _gate = new();

    private bool _hasAdapter;
    private bool _lastRequestReachedProvider = true;
    private bool _isOnline;
    private bool _isDisposed;

    public NetworkConnectivityMonitor(ILogger<NetworkConnectivityMonitor> logger)
    {
        _logger = logger;

        _hasAdapter = SafeGetIsNetworkAvailable();
        _isOnline = _hasAdapter;

        // Both events matter: availability covers the adapter appearing and disappearing,
        // address change covers moving between networks, which is when a stale failure from
        // the previous network should stop being held against the new one.
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
    }

    public bool IsOnline
    {
        get
        {
            lock (_gate)
            {
                return _isOnline;
            }
        }
    }

    public event EventHandler<bool>? ConnectivityChanged;

    public void ReportUnreachable() => Update(requestReachedProvider: false);

    public void ReportReachable() => Update(requestReachedProvider: true);

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
    }

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) =>
        Update(hasAdapter: e.IsAvailable, requestReachedProvider: e.IsAvailable ? true : null);

    private void OnAddressChanged(object? sender, EventArgs e) =>
        Update(
            hasAdapter: SafeGetIsNetworkAvailable(),
            // A new network deserves a clean slate rather than inheriting the old one's verdict.
            requestReachedProvider: true);

    private void Update(bool? hasAdapter = null, bool? requestReachedProvider = null)
    {
        bool changed;
        bool current;

        lock (_gate)
        {
            if (hasAdapter is { } adapter)
            {
                _hasAdapter = adapter;
            }

            if (requestReachedProvider is { } reached)
            {
                _lastRequestReachedProvider = reached;
            }

            // Offline is either signal saying so. Only both agreeing means online.
            current = _hasAdapter && _lastRequestReachedProvider;
            changed = current != _isOnline;
            _isOnline = current;
        }

        if (!changed)
        {
            return;
        }

        _logger.LogInformation("Connectivity changed: {State}.", current ? "online" : "offline");

        ConnectivityChanged?.Invoke(this, current);
    }

    /// <summary>
    /// <see cref="NetworkInterface.GetIsNetworkAvailable"/> reads adapter state through the
    /// OS and can throw while an adapter is being reconfigured. Assuming a connection is the
    /// right guess on failure: a wrongly hidden strip is a smaller error than a permanent one
    /// on a machine that is actually online.
    /// </summary>
    private bool SafeGetIsNetworkAvailable()
    {
        try
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }
        catch (NetworkInformationException ex)
        {
            _logger.LogDebug(ex, "Could not read the current network state.");
            return true;
        }
    }
}
