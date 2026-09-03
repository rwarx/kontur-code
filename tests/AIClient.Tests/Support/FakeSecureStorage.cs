using AIClient.Domain.Interfaces;

namespace AIClient.Tests.Support;

/// <summary>
/// A dictionary-backed <see cref="ISecureStorage"/>.
/// </summary>
/// <remarks>
/// Providers only ask this whether a key exists and what it is, so a dictionary is a faithful
/// stand-in. The DPAPI implementation is tested separately, against the real Windows API - see
/// <c>SecureStorageTests</c>. Values here are throwaway strings, never a real key: section 36
/// requires the suite to run without one.
/// </remarks>
public sealed class FakeSecureStorage : ISecureStorage
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <summary>Seeds a placeholder key so a provider will authorise a request.</summary>
    public static FakeSecureStorage With(string key, string value = "test-key-not-a-real-credential")
    {
        var storage = new FakeSecureStorage();
        storage._values[key] = value;
        return storage;
    }

    public List<string> Reads { get; } = [];

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        Reads.Add(key);
        return Task.FromResult(_values.GetValueOrDefault(key));
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _values.Remove(key);
        return Task.CompletedTask;
    }

    public Task<bool> ContainsAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_values.ContainsKey(key));
}
