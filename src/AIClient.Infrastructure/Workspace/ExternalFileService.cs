using System.Text;
using AIClient.Application.Configuration;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Application.Services;
using AIClient.Domain.Workspace;
using Microsoft.Extensions.Logging;

namespace AIClient.Infrastructure.Workspace;

/// <summary>
/// Reads and writes files outside the workspace, rebuilding the guard from scratch because none of
/// the workspace's relative-path guarantees apply to an absolute path.
/// </summary>
/// <remarks>
/// <para>
/// The workspace service refuses any path outside the open folder, and that refusal is not weakened
/// here - it is left exactly as it is, and this is a separate door with its own lock. Two targets
/// are refused whatever the user has approved: the application's own data directory, where the
/// encrypted keys and the conversation database live, and any credential-shaped name, resolved the
/// same way <see cref="SensitiveFiles"/> does inside the project. Writes have a third refusal on top,
/// the operating-system folders, which the agent may read with approval but never change.
/// </para>
/// <para>
/// The feature switch is not checked here. As with the fetch tool and its fetcher, the tools own the
/// on/off decision and the approval gate; this service is only the mechanism, and it enforces the
/// refusals that must hold no matter what the switch says.
/// </para>
/// </remarks>
public sealed class ExternalFileService : IExternalFileService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: false);

    private readonly ISettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly ILogger<ExternalFileService> _logger;

    public ExternalFileService(ISettingsService settings, IAppPaths paths, ILogger<ExternalFileService> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _paths = paths;
        _logger = logger;
    }

    private AgentSettings Settings => _settings.Current.Agent;

    public Task<WorkspaceResult<ExternalFile>> ReadAsync(
        string path,
        int startLine = 1,
        int? lineCount = null,
        CancellationToken cancellationToken = default)
    {
        if (!Validate(path, forWrite: false, out var full, out var error))
        {
            return Task.FromResult(WorkspaceResult<ExternalFile>.Fail(error!));
        }

        return GuardAsync(full, () => ReadCoreAsync(full, startLine, lineCount, cancellationToken));
    }

    /// <summary>
    /// Turns the expected failures into results and the rest into a worded refusal, never a leaked
    /// exception message. Cancellation is a stop, not a failure, and is allowed to propagate.
    /// </summary>
    private async Task<WorkspaceResult<T>> GuardAsync<T>(string full, Func<Task<WorkspaceResult<T>>> operation)
        where T : class
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return WorkspaceResult<T>.Fail($"Access to '{full}' was denied by the operating system.");
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "An external file operation failed.");
            return WorkspaceResult<T>.Fail($"'{full}' could not be read or written: an I/O error occurred.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "An external file operation failed unexpectedly.");
            return WorkspaceResult<T>.Fail($"'{full}' could not be processed.");
        }
    }

    private async Task<WorkspaceResult<ExternalFile>> ReadCoreAsync(
        string full,
        int startLine,
        int? lineCount,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(full))
        {
            return WorkspaceResult<ExternalFile>.Fail($"'{full}' is a directory, not a file.");
        }

        var info = new FileInfo(full);

        if (!info.Exists)
        {
            return WorkspaceResult<ExternalFile>.Fail($"'{full}' does not exist.");
        }

        var settings = Settings;

        if (info.Length > settings.MaxFileBytes)
        {
            return WorkspaceResult<ExternalFile>.Fail(
                $"'{full}' is {FormatSize(info.Length)}, over the {FormatSize(settings.MaxFileBytes)} limit. "
                + "Read a narrower window, or ask the user.");
        }

        if (await TextContent.IsBinaryAsync(full, cancellationToken).ConfigureAwait(false))
        {
            return WorkspaceResult<ExternalFile>.Fail($"'{full}' contains binary data, so it cannot be read as text.");
        }

        var text = await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
        var lines = SplitLines(text);
        var total = lines.Length;
        var first = Math.Max(1, startLine);

        if (first > total)
        {
            return WorkspaceResult<ExternalFile>.Ok(Slice(full, string.Empty, total, 0, total, info.Length, truncated: false));
        }

        var wanted = lineCount is { } requested && requested > 0 ? requested : total - first + 1;
        var take = Math.Min(wanted, total - first + 1);
        var window = string.Join('\n', lines.Skip(first - 1).Take(take));
        var cap = Math.Max(1_000, settings.MaxReadCharacters);
        var truncated = window.Length > cap;

        if (truncated)
        {
            window = window[..cap];
        }

        return WorkspaceResult<ExternalFile>.Ok(Slice(full, window, first, take, total, info.Length, truncated));
    }

    private static ExternalFile Slice(string path, string content, int first, int count, int total, long size, bool truncated) =>
        new()
        {
            Path = path,
            Content = content,
            FirstLine = first,
            LineCount = count,
            TotalLines = total,
            Size = size,
            IsTruncated = truncated,
        };

    public Task<WorkspaceResult<ExternalListing>> ListAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!Validate(path, forWrite: false, out var full, out var error))
        {
            return Task.FromResult(WorkspaceResult<ExternalListing>.Fail(error!));
        }

        return GuardAsync(full, () => ListCoreAsync(full, cancellationToken));
    }

    private Task<WorkspaceResult<ExternalListing>> ListCoreAsync(string full, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(full))
        {
            return Task.FromResult(WorkspaceResult<ExternalListing>.Fail(
                File.Exists(full) ? $"'{full}' is a file, not a directory." : $"'{full}' does not exist."));
        }

        var settings = Settings;
        var cap = Math.Max(1, settings.MaxListEntries);
        var ignored = settings.IgnoredNames;
        var entries = new List<ExternalEntry>();
        var truncated = false;

        var infos = new DirectoryInfo(full)
            .EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true })
            .OrderBy(entry => entry is DirectoryInfo ? 0 : 1)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var info in infos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ignored.Contains(info.Name, StringComparer.OrdinalIgnoreCase) || SensitiveFiles.IsProtectedSegment(info.Name))
            {
                continue;
            }

            if (entries.Count >= cap)
            {
                truncated = true;
                break;
            }

            entries.Add(new ExternalEntry
            {
                Name = info.Name,
                IsDirectory = info is DirectoryInfo,
                Size = info is FileInfo file ? file.Length : 0,
            });
        }

        return Task.FromResult(WorkspaceResult<ExternalListing>.Ok(
            new ExternalListing { Path = full, Entries = entries, IsTruncated = truncated }));
    }

    public Task<WorkspaceResult<ExternalWrite>> WriteAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!Validate(path, forWrite: true, out var full, out var error))
        {
            return Task.FromResult(WorkspaceResult<ExternalWrite>.Fail(error!));
        }

        return GuardAsync(full, () => WriteCoreAsync(full, content, cancellationToken));
    }

    private async Task<WorkspaceResult<ExternalWrite>> WriteCoreAsync(string full, string content, CancellationToken cancellationToken)
    {
        if (Directory.Exists(full))
        {
            return WorkspaceResult<ExternalWrite>.Fail($"'{full}' is a directory, not a file.");
        }

        var info = new FileInfo(full);
        var existed = info.Exists;
        string newline;
        var bom = false;
        var linesBefore = 0;

        if (existed)
        {
            if (info.Length > Settings.MaxFileBytes)
            {
                return WorkspaceResult<ExternalWrite>.Fail(
                    $"'{full}' is {FormatSize(info.Length)}, over the {FormatSize(Settings.MaxFileBytes)} limit, so it will not be overwritten.");
            }

            if (await TextContent.IsBinaryAsync(full, cancellationToken).ConfigureAwait(false))
            {
                return WorkspaceResult<ExternalWrite>.Fail($"'{full}' contains binary data, so it will not be overwritten as text.");
            }

            var existing = await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
            newline = TextContent.DominantNewline(existing);
            bom = await HasBomAsync(full, cancellationToken).ConfigureAwait(false);
            linesBefore = SplitLines(existing).Length;
        }
        else
        {
            newline = TextContent.DominantNewline(content);
        }

        return await CommitAsync(full, content, newline, bom, existed, linesBefore, 0, cancellationToken).ConfigureAwait(false);
    }

    public Task<WorkspaceResult<ExternalWrite>> ReplaceAsync(
        string path,
        string find,
        string replace,
        bool replaceAll,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(find);
        ArgumentNullException.ThrowIfNull(replace);

        if (!Validate(path, forWrite: true, out var full, out var error))
        {
            return Task.FromResult(WorkspaceResult<ExternalWrite>.Fail(error!));
        }

        if (find.Length == 0)
        {
            return Task.FromResult(WorkspaceResult<ExternalWrite>.Fail(
                "The text to find is empty. Give the exact text to replace."));
        }

        if (string.Equals(find, replace, StringComparison.Ordinal))
        {
            return Task.FromResult(WorkspaceResult<ExternalWrite>.Fail(
                "The text to find and the replacement are identical, so nothing would change."));
        }

        return GuardAsync(full, () => ReplaceCoreAsync(full, find, replace, replaceAll, cancellationToken));
    }

    private async Task<WorkspaceResult<ExternalWrite>> ReplaceCoreAsync(
        string full,
        string find,
        string replace,
        bool replaceAll,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(full))
        {
            return WorkspaceResult<ExternalWrite>.Fail($"'{full}' is a directory, not a file.");
        }

        var info = new FileInfo(full);

        if (!info.Exists)
        {
            return WorkspaceResult<ExternalWrite>.Fail($"'{full}' does not exist, so there is nothing to edit.");
        }

        if (info.Length > Settings.MaxFileBytes)
        {
            return WorkspaceResult<ExternalWrite>.Fail(
                $"'{full}' is {FormatSize(info.Length)}, over the {FormatSize(Settings.MaxFileBytes)} limit.");
        }

        if (await TextContent.IsBinaryAsync(full, cancellationToken).ConfigureAwait(false))
        {
            return WorkspaceResult<ExternalWrite>.Fail($"'{full}' contains binary data, so it cannot be edited as text.");
        }

        var existing = await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
        var newline = TextContent.DominantNewline(existing);
        var bom = await HasBomAsync(full, cancellationToken).ConfigureAwait(false);

        // Match on newline-flattened text so a find that spans lines is not defeated by CRLF vs LF.
        var haystack = Level(existing);
        var needle = Level(find);
        var occurrences = CountOccurrences(haystack, needle);

        if (occurrences == 0)
        {
            return WorkspaceResult<ExternalWrite>.Fail(
                "That text was not found in the file. Read it and copy the exact text, including whitespace.");
        }

        if (occurrences > 1 && !replaceAll)
        {
            return WorkspaceResult<ExternalWrite>.Fail(
                $"That text appears {occurrences} times. Pass replace_all to change every one, or extend it so it matches only the place you mean.");
        }

        var updated = replaceAll ? haystack.Replace(needle, Level(replace)) : ReplaceFirst(haystack, needle, Level(replace));
        var linesBefore = SplitLines(existing).Length;

        return await CommitAsync(full, updated, newline, bom, existed: true, linesBefore, replaceAll ? occurrences : 1, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<WorkspaceResult<ExternalWrite>> CommitAsync(
        string full,
        string content,
        string newline,
        bool bom,
        bool existed,
        int linesBefore,
        int replacements,
        CancellationToken cancellationToken)
    {
        var body = TextContent.NormalizeNewlines(content, newline);
        var cap = Settings.MaxFileBytes;
        var size = Utf8NoBom.GetByteCount(body) + (bom ? 3 : 0);

        if (size > cap)
        {
            return WorkspaceResult<ExternalWrite>.Fail(
                $"That content is {FormatSize(size)}, over the {FormatSize(cap)} limit for one file.");
        }

        await WriteAtomicAsync(full, body, bom, cancellationToken).ConfigureAwait(false);

        return WorkspaceResult<ExternalWrite>.Ok(new ExternalWrite
        {
            Path = full,
            Created = !existed,
            LinesBefore = linesBefore,
            LinesAfter = SplitLines(body).Length,
            Size = size,
            Replacements = replacements,
        });
    }

    /// <summary>Writes through a sibling temp file and a move, so a failure leaves the original whole.</summary>
    private static async Task WriteAtomicAsync(string full, string body, bool bom, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(full);

        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var temp = full + ".aiclient.tmp";

        try
        {
            await File.WriteAllTextAsync(temp, body, bom ? Utf8WithBom : Utf8NoBom, cancellationToken).ConfigureAwait(false);
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The original failure is the one the caller needs to see.
            }

            throw;
        }
    }

    /// <summary>
    /// Resolves an absolute path and refuses the ones that are off limits whatever the user approved.
    /// </summary>
    /// <remarks>
    /// The whole of the guard. It requires a fully-qualified path so nothing is resolved against a
    /// working directory the model cannot see, refuses an alternate-data-stream name, runs every
    /// segment through <see cref="SensitiveFiles"/>, and refuses the application's own data directory.
    /// A write refuses the operating-system folders on top of that: the agent may read them with
    /// approval, but there is no version control undoing a change to System32.
    /// </remarks>
    private bool Validate(string? raw, bool forWrite, out string full, out string? error)
    {
        full = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Give an absolute path to a file, such as 'C:\\Users\\me\\notes.txt'.";
            return false;
        }

        var trimmed = raw.Trim();

        if (trimmed.Any(char.IsControl) || !Path.IsPathFullyQualified(trimmed))
        {
            error = $"'{trimmed}' is not an absolute path. Give a fully-qualified path such as 'C:\\Users\\me\\notes.txt'.";
            return false;
        }

        string resolved;

        try
        {
            resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"'{trimmed}' is not a valid path.";
            return false;
        }

        if (Path.GetFileName(resolved).Contains(':'))
        {
            error = "That path names an alternate data stream, which is refused.";
            return false;
        }

        foreach (var segment in resolved.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (SensitiveFiles.IsProtectedSegment(segment))
            {
                error = $"'{segment}' holds credentials or version-control internals, and is off limits even "
                    + "outside the project. Ask the user to do anything that needs it.";
                return false;
            }
        }

        if (IsAtOrUnder(resolved, _paths.DataDirectory))
        {
            error = "That path is inside this application's own data (encrypted keys and conversations), "
                + "which is never opened this way.";
            return false;
        }

        if (forWrite && IsSystemFolder(resolved))
        {
            error = "That path is inside an operating-system folder. The agent may read there with your "
                + "approval, but never write to it.";
            return false;
        }

        full = resolved;
        return true;
    }

    /// <summary>
    /// Whether one path is the same as another or sits below it, with the separator part of the test
    /// so that <c>C:\data-notes</c> is not treated as inside <c>C:\data</c>. Case-insensitive, as the
    /// file system is.
    /// </summary>
    private static bool IsAtOrUnder(string candidate, string ancestor)
    {
        if (string.IsNullOrEmpty(ancestor))
        {
            return false;
        }

        var a = Path.TrimEndingDirectorySeparator(candidate);
        var b = Path.TrimEndingDirectorySeparator(ancestor);

        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return a.Length > b.Length
            && (a[b.Length] == Path.DirectorySeparatorChar || a[b.Length] == Path.AltDirectorySeparatorChar)
            && a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSystemFolder(string full)
    {
        foreach (var folder in SystemFolders)
        {
            if (IsAtOrUnder(full, folder))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly string[] SystemFolders =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
    ];

    /// <summary>Splits text into lines the way an editor counts them, dropping one trailing empty.</summary>
    private static string[] SplitLines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var lines = TextContent.NormalizeNewlines(text, "\n").Split('\n');

        return lines.Length > 1 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private static string Level(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string ReplaceFirst(string text, string needle, string value)
    {
        var index = text.IndexOf(needle, StringComparison.Ordinal);

        return index < 0
            ? text
            : string.Concat(text.AsSpan(0, index), value, text.AsSpan(index + needle.Length));
    }

    private static async Task<bool> HasBomAsync(string full, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);

        var head = new byte[3];
        var read = await stream
            .ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);

        return read == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };
}
