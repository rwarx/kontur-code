using AIClient.Application.DTOs;
using AIClient.Infrastructure;
using AIClient.Infrastructure.Configuration;
using AIClient.Infrastructure.Workspace;
using AIClient.Tests.Support;

namespace AIClient.Tests;

/// <summary>
/// The second door to the file system, exercised over a real temporary tree.
/// </summary>
/// <remarks>
/// <para>
/// Over a real directory rather than a substituted interface, for the same reason the workspace tests
/// are: a stubbed file system would have accepted a junction, and a junction is the entire subject of
/// the link-escape cases below.
/// </para>
/// <para>
/// Two of these need a directory symlink and skip out loud without Developer Mode or elevation,
/// rather than passing quietly. They cover the half of the containment rule no string comparison can
/// check, so skipping them silently would be the one outcome that makes the suite worse than useless.
/// </para>
/// </remarks>
public sealed class ExternalFileServiceTests : IAsyncLifetime
{
    private readonly RecordingLogger<ExternalFileService> _logger = new();

    private string _scratch = null!;
    private string _appData = null!;
    private string _outside = null!;
    private ExternalFileService _service = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "aiclient-external", Guid.CreateVersion7().ToString("n"));
        _outside = Path.Combine(_scratch, "outside");
        _appData = Path.Combine(_scratch, "appdata");

        Directory.CreateDirectory(Path.Combine(_outside, "notes"));
        Directory.CreateDirectory(Path.Combine(_outside, ".ssh"));
        Directory.CreateDirectory(_appData);

        File.WriteAllText(Path.Combine(_outside, "notes", "plain.txt"), "hello");
        File.WriteAllText(Path.Combine(_outside, ".ssh", "id_rsa"), "PRIVATE");
        File.WriteAllText(Path.Combine(_outside, "top.pem"), "PRIVATE");

        _service = new ExternalFileService(
            new StubSettingsService(),
            new AppPaths(_appData),
            _logger);

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var junction in new[] { "escape", "escapefile" })
            {
                var path = Path.Combine(_outside, junction);
                if (Directory.Exists(path))
                {
                    // Windows refuses to remove a junction with Remove-Directory's own logic in some
                    // states; Delete works because it does not follow the link.
                    new DirectoryInfo(path).Delete();
                }
            }

            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
            // A locked handle is not worth failing a run over; %TEMP% is disposable.
        }
    }

    // ----- the shape of the guard -----

    [Fact]
    public async Task An_ordinary_file_outside_the_project_is_read()
    {
        var result = await _service.ReadAsync(Path.Combine(_outside, "notes", "plain.txt"), cancellationToken: Token);

        Assert.True(result.Success, result.Error);
        Assert.Equal("hello", result.Value!.Content);
    }

    [Theory]
    [InlineData("")]                       // empty
    [InlineData("   ")]
    [InlineData("relative\\path.txt")]      // not absolute
    [InlineData("C:relative.txt")]          // drive-relative, not fully qualified
    public async Task A_path_that_is_not_fully_qualified_is_refused(string path)
    {
        var result = await _service.ReadAsync(path, cancellationToken: Token);

        Assert.False(result.Success);
        Assert.Contains("absolute", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_credential_named_file_is_refused()
    {
        var result = await _service.ReadAsync(Path.Combine(_outside, ".ssh", "id_rsa"), cancellationToken: Token);

        Assert.False(result.Success);
        Assert.Contains("credentials", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_credential_named_file_is_refused_by_extension_too()
    {
        var result = await _service.ReadAsync(Path.Combine(_outside, "top.pem"), cancellationToken: Token);

        Assert.False(result.Success);
        Assert.Contains("credentials", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_alternate_data_stream_is_refused()
    {
        // `plain.txt:hidden` is a second, unlisted stream on an otherwise innocent file.
        var result = await _service.ReadAsync(
            Path.Combine(_outside, "notes", "plain.txt") + ":hidden",
            cancellationToken: Token);

        Assert.False(result.Success);
        Assert.Contains("alternate data stream", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task This_applications_own_data_directory_is_refused()
    {
        var paths = new AppPaths(_appData);
        var target = Path.Combine(paths.DataDirectory, "aiclient.db");
        Directory.CreateDirectory(paths.DataDirectory);
        await File.WriteAllTextAsync(target, "not really a database", Token);

        var result = await _service.ReadAsync(target, cancellationToken: Token);

        Assert.False(result.Success);
        Assert.Contains("own data", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_write_into_a_system_folder_is_refused()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(system) || !Directory.Exists(system))
        {
            return;
        }

        var result = await _service.WriteAsync(
            Path.Combine(system, "kontur-code-should-not-exist.txt"),
            "x",
            Token);

        Assert.False(result.Success);
        Assert.Contains("operating-system folder", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_write_into_the_machine_wide_program_data_folder_is_refused()
    {
        // C:\ProgramData. This one was missing from the refusal list while Program Files was in it,
        // which meant the guard refused System32 and permitted ProgramData.
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrEmpty(programData) || !Directory.Exists(programData))
        {
            return;
        }

        var result = await _service.WriteAsync(
            Path.Combine(programData, "kontur-code-should-not-exist.txt"),
            "x",
            Token);

        Assert.False(result.Success);
        Assert.Contains("operating-system folder", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_read_inside_a_system_folder_is_allowed_because_the_user_approved_it()
    {
        // The asymmetry is deliberate: there is no version-control undo for a change to System32,
        // but refusing to read a system file would make the agent useless for diagnosing a machine.
        var system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(system))
        {
            return;
        }

        // Only the refusal path is under test; whether such a file exists is not this test's business.
        var result = await _service.ReadAsync(
            Path.Combine(system, "kontur-code-absent-probe.txt"),
            cancellationToken: Token);

        Assert.DoesNotContain("operating-system folder", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    // ----- link escape: the case the text-only check cannot see -----

    [Fact]
    public async Task A_junction_cannot_reach_a_file_protected_by_its_own_directory()
    {
        var junction = Path.Combine(_outside, "escape");

        CreateJunctionOrSkip(junction, Path.Combine(_outside, ".ssh"));

        // This is the bypass. `.ssh\config` is protected because its *parent* is `.ssh`, not because
        // `config` is on the list. Reached through a junction called `escape`, every textual segment of
        // the request is innocent - `escape`, `config` - so a check over the path's own text passes and
        // the read lands on a real SSH config file.
        //
        // Asking for `escape\id_rsa` instead would pass with or without link resolution, because
        // `id_rsa` is refused by name and the junction would be irrelevant. The case has to be one where
        // the protection comes from the directory, because that is the only shape on which the textual
        // check and the resolved check disagree.
        var result = await _service.ReadAsync(Path.Combine(junction, "config"), cancellationToken: Token);

        Assert.False(result.Success);
        Assert.Contains("credentials", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_file_symlink_with_an_innocent_name_cannot_reach_a_protected_file()
    {
        // The other shape: one file rather than a folder. Nothing in `plain\notes.txt` is a protected
        // name, and the private key is what comes back if links are not resolved.
        Directory.CreateDirectory(Path.Combine(_outside, "plain"));
        var link = Path.Combine(_outside, "plain", "notes.txt");

        CreateFileSymlinkOrSkip(link, Path.Combine(_outside, ".ssh", "id_rsa"));

        var result = await _service.ReadAsync(link, cancellationToken: Token);

        Assert.False(result.Success);
        Assert.Contains("credentials", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_junction_to_an_ordinary_folder_still_works()
    {
        // The fix must not refuse links wholesale, or the feature becomes useless. Resolution
        // rewrites the path and then the ordinary checks run against the destination.
        var junction = Path.Combine(_outside, "harmless");

        CreateJunctionOrSkip(junction, Path.Combine(_outside, "notes"));

        var result = await _service.ReadAsync(Path.Combine(junction, "plain.txt"), cancellationToken: Token);

        Assert.True(result.Success, result.Error);
        Assert.Equal("hello", result.Value!.Content);
    }

    [Fact]
    public async Task A_dangling_link_is_resolved_to_its_destination_rather_than_read_as_its_own_path()
    {
        var link = Path.Combine(_outside, "dangling", "target.txt");

        Directory.CreateDirectory(Path.Combine(_outside, "dangling"));
        CreateFileSymlinkOrSkip(link, Path.Combine(_outside, "gone.txt"));

        var result = await _service.ReadAsync(link, cancellationToken: Token);

        // The assertion is on *which path* failed, not on the wording. A dangling link resolves to its
        // destination, and the destination is what gets checked and read. So the error names the file
        // the link points at, and this test fails if anyone reintroduces the lexical fallback — under
        // which the link's own name would be reported and, worse, a link whose destination is a
        // protected file would be read through.
        Assert.False(result.Success);
        Assert.Contains("gone.txt", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("target.txt", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_dangling_link_to_a_protected_destination_is_refused_by_name()
    {
        // The combination that matters: a link, and a destination the name list protects. Resolution
        // happens first, so the refusal is about the destination rather than about the innocent-looking
        // name of the link.
        var link = Path.Combine(_outside, "dangling", "innocent.txt");

        Directory.CreateDirectory(Path.Combine(_outside, "dangling"));
        CreateFileSymlinkOrSkip(link, Path.Combine(_outside, ".ssh", "id_rsa"));

        var result = await _service.ReadAsync(link, cancellationToken: Token);

        Assert.False(result.Success);
        Assert.Contains("credentials", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    // ----- writes, replacement and listing -----

    [Fact]
    public async Task A_write_outside_the_project_lands_where_it_was_asked_to()
    {
        var target = Path.Combine(_outside, "notes", "written.txt");

        var result = await _service.WriteAsync(target, "content", Token);

        Assert.True(result.Success, result.Error);
        Assert.True(result.Value!.Created);
        Assert.Equal("content", await File.ReadAllTextAsync(target, Token));
    }

    [Fact]
    public async Task A_replace_reports_how_many_occurrences_it_changed()
    {
        var target = Path.Combine(_outside, "notes", "pairs.txt");
        await File.WriteAllTextAsync(target, "a X b X c", Token);

        var result = await _service.ReplaceAsync(target, "X", "Y", replaceAll: true, Token);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.Value!.Replacements);
        Assert.Equal("a Y b Y c", await File.ReadAllTextAsync(target, Token));
    }

    [Fact]
    public async Task A_listing_separates_directories_from_files()
    {
        var result = await _service.ListAsync(Path.Combine(_outside, "notes"), Token);

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Value!.Entries, entry => entry.Name == "plain.txt" && !entry.IsDirectory);
    }

    [Fact]
    public async Task A_listing_of_a_protected_folder_is_refused()
    {
        var result = await _service.ListAsync(Path.Combine(_outside, ".ssh"), Token);

        Assert.False(result.Success);
        Assert.Contains("credentials", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ----- helpers -----

    /// <summary>
    /// Creates a directory junction, or skips the test saying why it could not.
    /// </summary>
    /// <remarks>
    /// Creating a junction needs no elevation on Windows 10 1809+, but a hardened or older
    /// configuration can still refuse. <see cref="Assert.Skip(string)"/> rather than a bare
    /// <c>return</c>: a test that quietly returns passes, and a passing test that never ran is worse
    /// than a missing one, because it removes the case from view.
    /// </remarks>
    private static void CreateJunctionOrSkip(string junction, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(junction, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip($"This machine will not create a directory junction ({ex.GetType().Name}).");
        }

        AssertLink(junction);
    }

    /// <summary>
    /// Creates a file symlink, or skips the test saying why it could not.
    /// </summary>
    /// <remarks>
    /// The target is deliberately allowed not to exist: a dangling link is one of the cases under
    /// test, and a helper that insisted the target existed would quietly remove it.
    /// </remarks>
    private static void CreateFileSymlinkOrSkip(string link, string target)
    {
        // Deliberately not catching DirectoryNotFoundException: a missing parent directory is a bug in
        // the test, not a machine that refuses links, and skipping on it hid exactly that.
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip($"This machine will not create a file symlink ({ex.GetType().Name}).");
        }

        AssertLink(link);
    }

    /// <summary>
    /// Asserts the link really is one, so a helper that reported success without creating anything
    /// becomes a visible failure rather than a test that quietly stops testing.
    /// </summary>
    private static void AssertLink(string path)
    {
        var attributes = File.GetAttributes(path);

        Assert.True(
            (attributes & FileAttributes.ReparsePoint) != 0,
            $"{path} was created but is not a reparse point, so this test would pass without exercising anything.");
    }
}