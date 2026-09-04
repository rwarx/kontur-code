using System.Text;
using AIClient.Infrastructure.Configuration;
using AIClient.Infrastructure.SecureStorage;
using AIClient.Tests.Support;

namespace AIClient.Tests;

/// <summary>
/// Section 11 and section 28: where an API key is kept, and everything it must never do.
/// </summary>
/// <remarks>
/// Real DPAPI over a real temporary profile directory. Substituting the encryption would leave the
/// one claim worth making - that the bytes on disk are unreadable ciphertext - asserted against a
/// stub that hands back whatever it was told, so these tests encrypt for the Windows account that
/// runs them. That makes the file Windows-only, which the whole project already is.
/// </remarks>
public sealed class SecureStorageTests : IAsyncLifetime
{
    private const string Key = "openrouter";
    private const string ApiKey = "sk-or-v1-3f9c0a7b8e2d41f6a5c9b0d7e8f1a2b3";

    private readonly RecordingLogger<DpapiSecureStorage> _logger = new();
    private string _root = null!;
    private AppPaths _paths = null!;
    private DpapiSecureStorage _storage = null!;

    public ValueTask InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "aiclient-secrets", Guid.CreateVersion7().ToString("n"));
        _paths = new AppPaths(_root);
        _storage = new DpapiSecureStorage(_paths, _logger);

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Not worth failing a run over a leftover temporary directory.
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_stored_key_comes_back_exactly_as_it_went_in()
    {
        await _storage.SetAsync(Key, ApiKey, Token);

        Assert.Equal(ApiKey, await _storage.GetAsync(Key, Token));
    }

    [Theory]
    [InlineData("nvapi-0123456789abcdef")]
    [InlineData("a")]
    [InlineData("with spaces, = padding == and /slashes/")]
    [InlineData("ключ-с-кириллицей-и-тире")]
    [InlineData("line one\nline two\r\n\ttabbed")]
    public async Task Any_shape_of_secret_survives_the_round_trip(string secret)
    {
        // A key is opaque to this class: whatever the provider hands the user goes in verbatim,
        // and a UTF-8 round trip that mangled a non-ASCII byte would produce a 401 the user
        // could never explain.
        await _storage.SetAsync(Key, secret, Token);

        Assert.Equal(secret, await _storage.GetAsync(Key, Token));
    }

    [Fact]
    public async Task A_secret_far_larger_than_Credential_Manager_allows_still_round_trips()
    {
        // The 2560-byte cap is the reason this class exists instead of a CredRead/CredWrite
        // P/Invoke. A bearer token that long is unusual but a self-hosted gateway can issue one.
        var secret = string.Concat(Enumerable.Repeat("0123456789abcdef", 512));

        await _storage.SetAsync(Key, secret, Token);

        Assert.Equal(secret, await _storage.GetAsync(Key, Token));
    }

    [Fact]
    public async Task The_bytes_on_disk_carry_no_trace_of_the_secret()
    {
        await _storage.SetAsync(Key, ApiKey, Token);

        var bytes = await File.ReadAllBytesAsync(SecretFile(Key), Token);

        // Two decodings, because an implementation that "protected" the value by writing UTF-16
        // would sail past a single ASCII search.
        Assert.DoesNotContain(ApiKey, Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, Encoding.Unicode.GetString(bytes), StringComparison.Ordinal);

        // The provider prefix alone would be enough to tell an attacker which service to try.
        Assert.DoesNotContain("sk-or-v1", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_secret_is_written_under_the_profile_directory_and_nowhere_else()
    {
        await _storage.SetAsync(Key, ApiKey, Token);

        // Everything below the fake profile root, so a stray write to the data or logs folder
        // would show up here as well.
        var written = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);

        Assert.Single(written);
        Assert.Equal(SecretFile(Key), written[0]);
    }

    [Fact]
    public async Task A_completed_write_leaves_no_temporary_file_behind()
    {
        // The write goes to "<key>.dat.tmp" and is moved into place. A leftover .tmp would be an
        // unencrypted-looking artefact in the secrets folder and a second copy of the ciphertext.
        await _storage.SetAsync(Key, ApiKey, Token);

        Assert.Empty(Directory.GetFiles(_paths.SecretsDirectory, "*.tmp"));
    }

    [Fact]
    public async Task No_operation_ever_writes_the_secret_to_the_log()
    {
        // Section 26, stated as an assertion rather than a convention. Every path that touches a
        // value is exercised, including the one that fails to decrypt, since an exception message
        // is output too.
        await _storage.SetAsync(Key, ApiKey, Token);
        await _storage.GetAsync(Key, Token);
        await CorruptAsync(Key);
        await _storage.GetAsync(Key, Token);
        await _storage.DeleteAsync(Key, Token);

        Assert.DoesNotContain(ApiKey, _logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-or-v1", _logger.Text, StringComparison.Ordinal);

        // The log is still worth having: the key name is what makes a support question answerable.
        Assert.Contains(Key, _logger.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_key_does_not_put_the_secret_in_the_exception()
    {
        // An ArgumentException travels through log pipelines and crash reports. The key is
        // deliberately absent from it as well, since a caller can just as easily pass a secret in
        // the wrong argument.
        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => _storage.SetAsync("../escape", ApiKey, Token));

        Assert.DoesNotContain(ApiKey, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_replacement_takes_the_place_of_the_previous_value()
    {
        await _storage.SetAsync(Key, "sk-or-v1-first", Token);
        await _storage.SetAsync(Key, "sk-or-v1-second", Token);

        Assert.Equal("sk-or-v1-second", await _storage.GetAsync(Key, Token));

        // One file, so the old ciphertext is gone rather than sitting beside the new one.
        Assert.Single(Directory.GetFiles(_paths.SecretsDirectory));
    }

    [Fact]
    public async Task A_key_that_was_never_stored_reads_as_null()
    {
        // The registry uses this to decide whether a provider is configured, so "absent" has to
        // be a value rather than an exception.
        Assert.Null(await _storage.GetAsync("never-set", Token));
        Assert.False(await _storage.ContainsAsync("never-set", Token));
    }

    [Fact]
    public async Task Two_providers_keep_their_own_secrets()
    {
        await _storage.SetAsync("openrouter", "sk-or-v1-router", Token);
        await _storage.SetAsync("nvidia", "nvapi-nvidia", Token);

        Assert.Equal("sk-or-v1-router", await _storage.GetAsync("openrouter", Token));
        Assert.Equal("nvapi-nvidia", await _storage.GetAsync("nvidia", Token));
    }

    [Fact]
    public async Task Deleting_removes_the_secret_and_the_file_with_it()
    {
        await _storage.SetAsync(Key, ApiKey, Token);

        await _storage.DeleteAsync(Key, Token);

        Assert.Null(await _storage.GetAsync(Key, Token));
        Assert.False(await _storage.ContainsAsync(Key, Token));

        // Deleted, not merely emptied - a zero-length blob would still read as "configured".
        Assert.Empty(Directory.GetFiles(_paths.SecretsDirectory));
    }

    [Fact]
    public async Task Deleting_a_key_that_was_never_stored_is_not_an_error()
    {
        // The settings screen clears a field whether or not anything was there.
        await _storage.DeleteAsync("never-set", Token);

        Assert.Empty(Directory.GetFiles(_paths.SecretsDirectory));
    }

    [Fact]
    public async Task A_blob_that_cannot_be_decrypted_reads_as_absent_so_the_key_can_be_re_entered()
    {
        // What a profile reset, a restored backup or a file copied from another Windows account
        // looks like from here. Throwing would take the settings screen down with it; reporting
        // "no key" leads the user to the one action that fixes it.
        await _storage.SetAsync(Key, ApiKey, Token);
        await CorruptAsync(Key);

        Assert.Null(await _storage.GetAsync(Key, Token));
        Assert.Contains("could not be decrypted", _logger.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Presence_is_reported_without_decrypting()
    {
        // Every screen that shows a "configured" badge calls this, and it must not cost a DPAPI
        // round trip per provider. A blob that fails to decrypt is still present, which is the
        // observable difference between checking the file and reading the value.
        await _storage.SetAsync(Key, ApiKey, Token);
        await CorruptAsync(Key);

        Assert.True(await _storage.ContainsAsync(Key, Token));
        Assert.Null(await _storage.GetAsync(Key, Token));
    }

    [Fact]
    public async Task Cancelling_propagates_instead_of_reporting_the_secret_as_missing()
    {
        // Section 22. Swallowing cancellation here would surface as "provider not configured" and
        // send the user off to re-enter a key that is already stored.
        await _storage.SetAsync(Key, ApiKey, Token);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _storage.GetAsync(Key, cancellation.Token));
    }

    [Fact]
    public async Task A_null_value_is_refused_rather_than_stored_as_an_empty_secret()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _storage.SetAsync(Key, null!, Token));

        Assert.Empty(Directory.GetFiles(_paths.SecretsDirectory));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("sub/openrouter")]
    [InlineData("sub\\openrouter")]
    [InlineData("..openrouter")]
    [InlineData("open..router")]
    [InlineData("C:openrouter")]
    [InlineData("open router")]
    [InlineData("open*router")]
    [InlineData("роутер")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_key_that_could_reach_outside_the_secrets_directory_is_refused(string key)
    {
        // Keys come from provider ids today, so none of these can currently occur - which is
        // exactly why the guard needs a test. Every entry point validates, not just the write,
        // because a read pointed at "..\\..\\aiclient.db" would hand its bytes to DPAPI.
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.GetAsync(key, Token));
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.SetAsync(key, ApiKey, Token));
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.DeleteAsync(key, Token));
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.ContainsAsync(key, Token));

        // Nothing was created anywhere under the profile root on the way to the exception.
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_null_key_is_refused_as_such()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _storage.GetAsync(null!, Token));
    }

    [Theory]
    [InlineData("openrouter")]
    [InlineData("nvidia-nim")]
    [InlineData("my_provider")]
    [InlineData("provider.v2")]
    [InlineData("Provider42")]
    public async Task A_plausible_provider_id_is_accepted(string key)
    {
        // The complement of the rejection list: the guard has to leave every id the app can
        // actually produce alone, or adding a provider becomes a puzzle.
        await _storage.SetAsync(key, ApiKey, Token);

        Assert.Equal(ApiKey, await _storage.GetAsync(key, Token));
    }

    [Fact]
    public async Task A_read_running_beside_a_write_never_sees_a_half_written_blob()
    {
        // Two windows saving settings while the chat screen resolves a key is the real scenario.
        // Without the mutex and the atomic move, a read would land on a truncated file, DPAPI
        // would refuse it, and the value would come back null - the failure this pair prevents.
        await _storage.SetAsync(Key, ApiKey, Token);

        var writes = Enumerable.Range(0, 20)
            .Select(i => _storage.SetAsync(Key, $"{ApiKey}-{i}", Token))
            .ToList();
        var reads = Enumerable.Range(0, 20)
            .Select(_ => _storage.GetAsync(Key, Token))
            .ToList();

        await Task.WhenAll(writes);
        var values = await Task.WhenAll(reads);

        Assert.All(values, value =>
        {
            Assert.NotNull(value);
            Assert.StartsWith(ApiKey, value, StringComparison.Ordinal);
        });
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string SecretFile(string key) => Path.Combine(_paths.SecretsDirectory, key + ".dat");

    /// <summary>
    /// Replaces the ciphertext with bytes DPAPI cannot unprotect, standing in for a file written
    /// by another Windows account.
    /// </summary>
    private async Task CorruptAsync(string key)
    {
        var junk = new byte[256];
        Random.Shared.NextBytes(junk);

        await File.WriteAllBytesAsync(SecretFile(key), junk, Token);
    }
}
