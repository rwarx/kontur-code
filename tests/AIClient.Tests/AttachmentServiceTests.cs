using System.Text;
using AIClient.Application.Configuration;
using AIClient.Application.Services;
using AIClient.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIClient.Tests;

/// <summary>
/// Section 19 attachments, and the part of section 28 that says file handling has to be safe.
/// </summary>
/// <remarks>
/// Real files in a temporary directory: the whole point of this service is what happens when
/// the bytes on disk disagree with the file name, and that cannot be tested through an
/// abstraction over the file system. Every rejection path is covered, because each one is a
/// case where the alternative is inlining something dangerous or unreadable into a prompt.
/// </remarks>
public sealed class AttachmentServiceTests : IAsyncLifetime
{
    private readonly StubSettingsService _settings = new();
    private string _directory = null!;
    private AttachmentService _service = null!;

    public ValueTask InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "aiclient-attachments", Guid.CreateVersion7().ToString("n"));
        Directory.CreateDirectory(_directory);
        _service = new AttachmentService(_settings, NullLogger<AttachmentService>.Instance);

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Not worth failing a run over a leftover temporary directory.
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_text_file_is_read_with_its_name_size_and_content()
    {
        var path = WriteText("Widget.cs", "public sealed class Widget;\n");

        var result = await _service.LoadAsync(path);

        Assert.True(result.Success);
        var attachment = result.Attachment!;
        Assert.Equal("Widget.cs", attachment.FileName);
        Assert.Equal("text/plain", attachment.MimeType);
        Assert.Equal(new FileInfo(path).Length, attachment.Size);
        Assert.Equal("public sealed class Widget;\n", attachment.TextContent);
        Assert.False(attachment.IsTruncated);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task Non_Latin_text_is_read_back_intact()
    {
        // The app is prompted in Russian. A file read with the wrong encoding arrives at the
        // model as mojibake and the answer is nonsense.
        var path = WriteText("заметки.md", "# Привет\n\nЭто заметка — с тире.\n");

        var result = await _service.LoadAsync(path);

        Assert.True(result.Success);
        Assert.Contains("Это заметка — с тире.", result.Attachment!.TextContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("app.exe")]
    [InlineData("archive.zip")]
    [InlineData("library.dll")]
    [InlineData("photo.png")]
    [InlineData("sheet.xlsx")]
    public async Task An_extension_outside_the_allow_list_is_refused(string fileName)
    {
        var path = WriteText(fileName, "harmless looking text");

        var result = await _service.LoadAsync(path);

        Assert.False(result.Success);
        Assert.Null(result.Attachment);
        Assert.Contains(Path.GetExtension(fileName), result.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejection_message_never_repeats_the_path()
    {
        // Paths contain the Windows user name. It has no place in a chat window, and the
        // message the user needs is about the file type, not where it lives.
        var path = WriteText("secret.exe", "text");

        var result = await _service.LoadAsync(path);

        Assert.DoesNotContain(_directory, result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_file_that_no_longer_exists_is_reported_as_such()
    {
        var result = await _service.LoadAsync(Path.Combine(_directory, "gone.txt"));

        Assert.False(result.Success);
        Assert.Contains("no longer exists", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_empty_file_is_refused_rather_than_attached_as_nothing()
    {
        var path = WriteText("blank.txt", string.Empty);

        var result = await _service.LoadAsync(path);

        Assert.False(result.Success);
        Assert.Contains("empty", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_file_over_the_size_cap_is_refused_with_both_numbers()
    {
        _settings.With<StorageSettings>(s => s.MaxAttachmentBytes = 1024);
        var path = WriteText("big.txt", new string('x', 4096));

        var result = await _service.LoadAsync(path);

        Assert.False(result.Success);

        // Both halves matter: how big the file is and what the limit is. "Too large" alone
        // leaves the user guessing what to do next.
        Assert.Contains("4 KB", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Contains("1 KB", result.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_renamed_binary_is_refused_despite_a_text_extension()
    {
        // The extension check alone is not a security boundary: anyone can rename an
        // executable to .txt, and the bytes are what get inlined into the prompt.
        var bytes = new byte[512];
        bytes[0] = 0x4D;
        bytes[1] = 0x5A;
        var path = Path.Combine(_directory, "notreally.txt");
        await File.WriteAllBytesAsync(path, bytes);

        var result = await _service.LoadAsync(path);

        Assert.False(result.Success);
        Assert.Contains("binary", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_utf16_file_is_refused_as_binary()
    {
        // Documenting a real trade-off rather than a bug: UTF-16 is half NUL bytes, and the
        // sniff cannot tell it apart from a renamed executable. "Save as UTF-8" is a fix the
        // user can act on; silently inlining NULs is not.
        var path = Path.Combine(_directory, "utf16.txt");
        await File.WriteAllTextAsync(path, "plain ASCII content", Encoding.Unicode);

        var result = await _service.LoadAsync(path);

        Assert.False(result.Success);
        Assert.Contains("binary", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_stray_nul_does_not_condemn_an_otherwise_textual_file()
    {
        // One accidental NUL in a log file is under the tolerance; rejecting on the first one
        // would make the feature feel broken.
        var content = new StringBuilder(new string('a', 4000)).Append('\0').ToString();
        var path = WriteText("noisy.log", content);

        var result = await _service.LoadAsync(path);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Text_beyond_the_character_cap_is_truncated_and_flagged()
    {
        _settings.With<StorageSettings>(s => s.MaxAttachmentCharacters = 100);
        var path = WriteText("long.md", new string('t', 500));

        var result = await _service.LoadAsync(path);

        Assert.True(result.Success);
        Assert.Equal(100, result.Attachment!.TextContent!.Length);
        Assert.True(result.Attachment.IsTruncated);

        // The full size is still reported, so the UI chip does not claim the file is 100 bytes.
        Assert.Equal(500L, result.Attachment.Size);
    }

    [Fact]
    public async Task Cancelling_while_reading_propagates_instead_of_becoming_a_failed_result()
    {
        // Section 22. The catch-all in the service excludes cancellation on purpose: turning
        // Stop into "that file could not be attached" would be a lie.
        var path = WriteText("slow.txt", new string('c', 200_000));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.LoadAsync(path, cts.Token));
    }

    [Theory]
    [InlineData("data.json", "application/json")]
    [InlineData("notes.md", "text/markdown")]
    [InlineData("config.yml", "application/yaml")]
    [InlineData("page.html", "text/html")]
    [InlineData("style.css", "text/css")]
    [InlineData("query.sql", "application/sql")]
    [InlineData("Program.cs", "text/plain")]
    public async Task The_mime_type_follows_the_extension(string fileName, string expected)
    {
        var path = WriteText(fileName, "content");

        var result = await _service.LoadAsync(path);

        Assert.Equal(expected, result.Attachment!.MimeType);
    }

    [Theory]
    [InlineData("Dockerfile")]
    [InlineData("Makefile")]
    [InlineData("LICENSE")]
    [InlineData("README")]
    [InlineData(".editorconfig")]
    [InlineData(".gitignore")]
    public void A_conventional_extension_less_file_is_supported(string fileName)
    {
        // These come up constantly in code discussions and are always text.
        Assert.True(_service.IsSupported(Path.Combine(_directory, fileName)));
    }

    [Theory]
    [InlineData("mystery")]
    [InlineData("backup~")]
    [InlineData("")]
    [InlineData("   ")]
    public void Anything_else_without_an_extension_is_not_supported(string fileName)
    {
        Assert.False(_service.IsSupported(fileName));
    }

    [Theory]
    [InlineData(".gitignore.exe")]
    [InlineData("Dockerfile.dll")]
    [InlineData("READMEs.zip")]
    public void A_conventional_name_is_matched_exactly_and_never_as_a_prefix(string fileName)
    {
        // A prefix test would wave ".gitignore.exe" past the allow-list, leaving the binary
        // sniff as the only thing between an executable and the prompt. The list has to hold
        // on its own.
        Assert.False(_service.IsSupported(fileName));
    }

    [Theory]
    [InlineData("Program.CS")]
    [InlineData("STYLES.CSS")]
    [InlineData("Notes.MD")]
    public void The_allow_list_is_case_insensitive(string fileName)
    {
        // Windows paths are case-insensitive, and a file saved as .CS is the same file.
        Assert.True(_service.IsSupported(fileName));
    }

    [Fact]
    public void The_dialog_filter_lists_the_allow_list_and_keeps_an_escape_hatch()
    {
        var filter = _service.BuildFileDialogFilter();

        Assert.Contains("*.cs", filter, StringComparison.Ordinal);
        Assert.Contains("*.md", filter, StringComparison.Ordinal);
        Assert.Contains("All files (*.*)|*.*", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_extension_in_the_allow_list_is_lower_case_and_starts_with_a_dot()
    {
        // The lookup is ordinal-ignore-case, but the dialog filter is built from these
        // strings verbatim, and "*.CS" in a filter is a bad look.
        Assert.All(_service.SupportedExtensions, extension =>
        {
            Assert.StartsWith(".", extension, StringComparison.Ordinal);
            Assert.Equal(extension.ToLowerInvariant(), extension);
        });
    }

    [Fact]
    public void No_executable_or_archive_extension_ever_slipped_into_the_allow_list()
    {
        // A regression guard for the one mistake in this file that would actually matter.
        string[] forbidden =
        [
            ".exe", ".dll", ".msi", ".com", ".scr", ".vbs",
            ".zip", ".7z", ".rar", ".tar", ".gz", ".iso", ".lnk", ".pif",
        ];

        Assert.Empty(_service.SupportedExtensions.Intersect(forbidden, StringComparer.OrdinalIgnoreCase));
    }

    private string WriteText(string fileName, string content)
    {
        var path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, content);
        return path;
    }
}
