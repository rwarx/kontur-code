namespace AIClient.Domain.Interfaces;

/// <summary>
/// Credential storage. The only place in the application allowed to hold an API key.
/// </summary>
/// <remarks>
/// The default implementation encrypts with Windows DPAPI scoped to the current user.
/// The abstraction exists so the backing store can become Credential Manager, a hardware
/// token, or a per-machine scope without touching a single caller.
/// Implementations must never log a value, and must never include one in an exception message.
/// </remarks>
public interface ISecureStorage
{
    /// <summary>Returns the stored secret, or null when the key is absent or undecryptable.</summary>
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Stores or replaces a secret.</summary>
    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>Removes a secret. Absent keys are not an error.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>True when a value exists, without decrypting it.</summary>
    Task<bool> ContainsAsync(string key, CancellationToken cancellationToken = default);
}
