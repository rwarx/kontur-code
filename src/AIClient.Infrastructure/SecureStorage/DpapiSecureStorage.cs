using System.Security.Cryptography;
using System.Text;
using AIClient.Application.Configuration;
using AIClient.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace AIClient.Infrastructure.SecureStorage;

/// <summary>
/// Stores secrets as DPAPI-encrypted files under the user's profile.
/// </summary>
/// <remarks>
/// <para>
/// DPAPI with <see cref="DataProtectionScope.CurrentUser"/> ties the ciphertext to the
/// Windows account: another user on the same machine cannot decrypt it, and a copied file
/// is useless on another machine. No key material is stored by the application, and there
/// is no master password to lose.
/// </para>
/// <para>
/// Chosen over Windows Credential Manager because CredMan requires P/Invoke against
/// <c>CredRead</c>/<c>CredWrite</c> with manual unmanaged-memory handling, is capped at
/// 2560 bytes per blob, and gains nothing here: CredMan itself protects entries with DPAPI.
/// Should that trade-off change, only this class does - callers depend on
/// <see cref="ISecureStorage"/>.
/// </para>
/// <para>
/// Nothing in this class logs, returns, or embeds a secret value in an exception message.
/// </para>
/// </remarks>
public sealed class DpapiSecureStorage : ISecureStorage
{
    /// <summary>
    /// Additional entropy mixed into every operation. Not a secret - it is in the binary -
    /// but it scopes the ciphertext to this application, so a blob taken from another
    /// DPAPI-using program on the same account cannot be decrypted here and vice versa.
    /// </summary>
    private static readonly byte[] Entropy = "AIClient.SecureStorage.v1"u8.ToArray();

    private readonly string _directory;
    private readonly ILogger<DpapiSecureStorage> _logger;

    /// <summary>Serialises access so a concurrent read cannot observe a half-written file.</summary>
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public DpapiSecureStorage(IAppPaths paths, ILogger<DpapiSecureStorage> logger)
    {
        _directory = paths.SecretsDirectory;
        _logger = logger;
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(key);

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var encrypted = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);

            try
            {
                return Encoding.UTF8.GetString(plaintext);
            }
            finally
            {
                // The decrypted bytes are cleared immediately; the string itself is
                // immutable and cannot be, which is a limitation of the platform.
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (CryptographicException ex)
        {
            // Happens when the file was written by another Windows account, or after a
            // profile reset. Treating it as "no secret" lets the user simply re-enter the key.
            _logger.LogWarning(ex, "A stored secret could not be decrypted and will be treated as absent.");
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "A stored secret could not be read.");
            return null;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);

        var path = ResolvePath(key);

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plaintext = Encoding.UTF8.GetBytes(value);
            byte[] encrypted;

            try
            {
                encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            // Write to a temporary file and move it into place, so an interrupted write
            // cannot leave a truncated blob that fails to decrypt afterwards.
            var temporary = path + ".tmp";
            await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);

            _logger.LogInformation("Stored a secret for key '{Key}'.", key);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(key);

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogInformation("Deleted the secret for key '{Key}'.", key);
            }
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "The secret for key '{Key}' could not be deleted.", key);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public Task<bool> ContainsAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(File.Exists(ResolvePath(key)));

    /// <summary>
    /// Maps a logical key to a file name, rejecting anything that could escape the
    /// secrets directory. Keys come from provider ids, but validating here rather than
    /// trusting the caller keeps the guarantee local to this class.
    /// </summary>
    private string ResolvePath(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        foreach (var c in key)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
            {
                throw new ArgumentException(
                    "A secure-storage key may contain only letters, digits, '-', '_' and '.'.",
                    nameof(key));
            }
        }

        if (key is "." or ".." || key.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid secure-storage key.", nameof(key));
        }

        return Path.Combine(_directory, key + ".dat");
    }
}
