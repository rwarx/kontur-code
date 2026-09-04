using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;
using AIClient.Application.Configuration;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Application.Services;
using AIClient.Domain.Workspace;
using Microsoft.Extensions.Logging;

namespace AIClient.Infrastructure.Workspace;

/// <summary>
/// The sandbox: one folder, and nothing outside it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WorkspacePath"/> already refuses everything that can be judged from the text of a
/// path. What is left needs the disk, and it is the half that actually leaks: a directory inside
/// the workspace can be a junction to <c>C:\Windows</c>, and then a path that reads as contained
/// is not. So containment is re-established here for every operation - resolve, compare against
/// the root, then walk the ancestors following links - rather than assumed from the parse.
/// </para>
/// <para>
/// Two exclusion lists, deliberately different. The user's <see cref="AgentSettings.IgnoredNames"/>
/// only shapes listings and searches: naming an ignored file explicitly still reads it, because a
/// user may well want the agent to look at a build log. The protected set below is refused even by
/// exact path, because those files hold credentials or version-control internals and a model that
/// reads one has already put it in a chat transcript that this application then writes to disk.
/// </para>
/// </remarks>
public sealed class WorkspaceService : IWorkspaceService
{
    /// <summary>Longest match line reported. A minified bundle must not spend the whole budget.</summary>
    private const int MaxMatchLineLength = 400;

    /// <summary>Wall clock a single search may spend before it reports itself truncated.</summary>
    private static readonly TimeSpan SearchBudget = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Cap on one regular-expression match. The pattern arrives from a model, and a few
    /// characters of nested quantifier are enough to hang a scan on one line forever.
    /// </summary>
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromSeconds(1);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>
    /// How a directory is enumerated.
    /// </summary>
    /// <remarks>
    /// <see cref="FileAttributes.ReparsePoint"/> is skipped so a recursive walk can never descend
    /// through a link - a junction to a large tree costs one entry rather than a traversal of it.
    /// Hidden and system files are deliberately left visible: the default would skip them, and in
    /// a code tree the interesting files are frequently the ones whose name starts with a dot.
    /// </remarks>
    private static readonly EnumerationOptions EnumerationRules = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        MatchType = MatchType.Simple,
    };

    /// <summary>
    /// Names the agent may not touch at all, whether or not they were named explicitly.
    /// </summary>
    /// <remarks>
    /// Not the same question as "can the user attach this file". A user picking a file in a dialog
    /// is consent; a model naming one is not, which is why <c>.npmrc</c> is an attachable text
    /// file and an unreadable workspace entry at the same time.
    /// </remarks>
    private static readonly string[] ProtectedNames =
    [
        // Version-control internals: the config alone can carry a remote URL with a token in it,
        // and the object store carries every version of every file ever committed.
        ".git", ".svn", ".hg",
        // Key material, and the tools that keep it.
        ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker",
        "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519",
        // Files whose whole purpose is to hold a credential.
        ".netrc", "_netrc", ".npmrc", ".pypirc", ".git-credentials", ".htpasswd",
        "credentials", "credentials.json", "secrets.json", "secrets.yaml", "secrets.yml",
        "appsettings.secrets.json", "serviceaccount.json",
    ];

    /// <summary>Extensions that only ever carry a private key, a certificate or a password store.</summary>
    private static readonly string[] ProtectedExtensions =
    [
        ".pem", ".key", ".pfx", ".p12", ".jks", ".keystore", ".ppk", ".kdbx", ".gpg", ".asc",
    ];

    /// <summary>
    /// The <c>.env</c> variants that exist in order to be committed, and are therefore ordinary
    /// files rather than secrets.
    /// </summary>
    private static readonly string[] EnvTemplateSuffixes = [".example", ".sample", ".template", ".dist"];

    /// <summary>
    /// Folders that cannot be opened as a workspace, nor contain one.
    /// </summary>
    /// <remarks>
    /// <see cref="Environment.SpecialFolder.ApplicationData"/> and its local twin are deliberately
    /// absent. They are where per-user application state lives, including this application's own -
    /// which is refused separately and by its real path - and other applications' data is not this
    /// one's business to police. Listing them would also refuse every temporary directory, because
    /// <see cref="Path.GetTempPath"/> resolves under the local one.
    /// </remarks>
    private static readonly Environment.SpecialFolder[] SystemFolders =
    [
        Environment.SpecialFolder.Windows,
        Environment.SpecialFolder.System,
        Environment.SpecialFolder.SystemX86,
        Environment.SpecialFolder.ProgramFiles,
        Environment.SpecialFolder.ProgramFilesX86,
        Environment.SpecialFolder.CommonApplicationData,
    ];

    private readonly ISettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly ILogger<WorkspaceService> _logger;
    private readonly object _gate = new();

    private string? _root;
    private bool _restored;

    public WorkspaceService(
        ISettingsService settings,
        IAppPaths paths,
        ILogger<WorkspaceService> logger)
    {
        _settings = settings;
        _paths = paths;
        _logger = logger;
    }

    public string? Root
    {
        get
        {
            EnsureRestored();

            // Read under the same lock the writers take: the agent loop resolves paths on worker
            // threads while the window can close the folder, and a torn read here would be a write
            // aimed at a folder that is no longer open.
            lock (_gate)
            {
                return _root;
            }
        }
    }

    public bool IsOpen => Root is not null;

    public event EventHandler<string?>? RootChanged;

    public async Task<WorkspaceResult<string>> OpenAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return WorkspaceResult<string>.Fail("No folder was given.");
        }

        if (!TryFullPath(directory.Trim(), out var full))
        {
            return WorkspaceResult<string>.Fail("That is not a usable folder path.");
        }

        if (!Directory.Exists(full))
        {
            return WorkspaceResult<string>.Fail("That folder does not exist.");
        }

        if (RefuseAsRoot(full) is { } refusal)
        {
            return WorkspaceResult<string>.Fail(refusal);
        }

        lock (_gate)
        {
            _root = full;
            _restored = true;
        }

        await _settings.UpdateAsync<AgentSettings>(s => s.WorkspaceRoot = full, cancellationToken)
            .ConfigureAwait(false);

        // The path itself stays out of the log: it contains the user's name.
        _logger.LogInformation("Workspace opened.");

        RootChanged?.Invoke(this, full);
        return WorkspaceResult<string>.Ok(full);
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _root = null;
            _restored = true;
        }

        await _settings.UpdateAsync<AgentSettings>(s => s.WorkspaceRoot = null, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Workspace closed.");

        RootChanged?.Invoke(this, null);
    }

    public Task<WorkspaceResult<WorkspaceListing>> ListAsync(
        WorkspacePath path,
        bool recursive = false,
        CancellationToken cancellationToken = default) =>
        GuardAsync<WorkspaceListing>(path, () =>
        {
            if (!TryResolve(path, out var full, out var error))
            {
                return Task.FromResult(WorkspaceResult<WorkspaceListing>.Fail(error));
            }

            if (!Directory.Exists(full))
            {
                return Task.FromResult(WorkspaceResult<WorkspaceListing>.Fail(
                    File.Exists(full)
                        ? $"'{path}' is a file, not a directory."
                        : $"'{path}' does not exist."));
            }

            var agent = _settings.Current.Agent;
            var limit = Math.Max(1, agent.MaxListEntries);
            var ignored = agent.IgnoredNames;

            // Off the calling thread: a recursive walk of a large tree is long enough to freeze a
            // window, and the file tree in the UI calls this too.
            return Task.Run(
                () =>
                {
                    var entries = new List<WorkspaceEntry>(Math.Min(limit, 128));
                    var truncated = false;

                    foreach (var (child, info) in Walk(path, full, recursive, ignored, cancellationToken))
                    {
                        if (entries.Count == limit)
                        {
                            truncated = true;
                            break;
                        }

                        entries.Add(ToEntry(child, info));
                    }

                    return WorkspaceResult<WorkspaceListing>.Ok(new WorkspaceListing
                    {
                        Path = path,
                        Entries = entries,
                        IsTruncated = truncated,
                    });
                },
                cancellationToken);
        });

    public Task<WorkspaceResult<WorkspaceFile>> ReadAsync(
        WorkspacePath path,
        int startLine = 1,
        int? lineCount = null,
        CancellationToken cancellationToken = default) =>
        GuardAsync<WorkspaceFile>(path, async () =>
        {
            if (!TryResolve(path, out var full, out var error))
            {
                return WorkspaceResult<WorkspaceFile>.Fail(error);
            }

            if (startLine < 1)
            {
                return WorkspaceResult<WorkspaceFile>.Fail("Lines are numbered from 1.");
            }

            if (lineCount is < 1)
            {
                return WorkspaceResult<WorkspaceFile>.Fail("Ask for at least one line.");
            }

            var (text, readError) = await ReadTextAsync(path, full, cancellationToken).ConfigureAwait(false);

            if (readError is not null)
            {
                return WorkspaceResult<WorkspaceFile>.Fail(readError);
            }

            var lines = SplitLines(text!);

            if (startLine > lines.Length && text!.Length > 0)
            {
                return WorkspaceResult<WorkspaceFile>.Fail(
                    $"'{path}' has {lines.Length} lines, so line {startLine} does not exist.");
            }

            var window = lines.Skip(startLine - 1).Take(lineCount ?? lines.Length).ToArray();
            var content = string.Join('\n', window);
            var cap = Math.Max(1, _settings.Current.Agent.MaxReadCharacters);
            var truncated = content.Length > cap;

            if (truncated)
            {
                content = content[..cap];
            }

            return WorkspaceResult<WorkspaceFile>.Ok(new WorkspaceFile
            {
                Path = path,
                Content = content,
                FirstLine = startLine,
                LineCount = window.Length,
                TotalLines = lines.Length,
                Size = new FileInfo(full).Length,
                IsTruncated = truncated,
            });
        });

    public Task<WorkspaceResult<WorkspaceEntry>> StatAsync(
        WorkspacePath path,
        CancellationToken cancellationToken = default) =>
        GuardAsync<WorkspaceEntry>(path, () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryResolve(path, out var full, out var error))
            {
                return Task.FromResult(WorkspaceResult<WorkspaceEntry>.Fail(error));
            }

            FileSystemInfo? info = Directory.Exists(full)
                ? new DirectoryInfo(full)
                : File.Exists(full) ? new FileInfo(full) : null;

            return Task.FromResult(info is null
                ? WorkspaceResult<WorkspaceEntry>.Fail($"'{path}' does not exist.")
                : WorkspaceResult<WorkspaceEntry>.Ok(ToEntry(path, info)));
        });

    public Task<WorkspaceResult<WorkspaceWrite>> WriteAsync(
        WorkspacePath path,
        string content,
        CancellationToken cancellationToken = default) =>
        GuardAsync<WorkspaceWrite>(path, async () =>
        {
            ArgumentNullException.ThrowIfNull(content);

            if (!TryResolve(path, out var full, out var error))
            {
                return WorkspaceResult<WorkspaceWrite>.Fail(error);
            }

            if (path.IsRoot)
            {
                return WorkspaceResult<WorkspaceWrite>.Fail("The workspace root is a folder, not a file.");
            }

            if (Directory.Exists(full))
            {
                return WorkspaceResult<WorkspaceWrite>.Fail($"'{path}' is a directory, not a file.");
            }

            // A new file keeps the line endings its own content was written with, falling back to the
            // platform's only when there are none to read. Models write bare line feeds, and
            // re-casting those to CRLF makes every file the agent adds to an LF project the one file
            // that shows up as a whole-file diff the first time anything touches it.
            var newline = TextContent.DominantNewline(content);
            var bom = false;
            var linesBefore = 0;
            var existed = File.Exists(full);

            if (existed)
            {
                // An overwrite has to be able to read what is there. That is what preserves the
                // file's line endings and byte-order mark, so a one-line change stays a one-line
                // diff - and it is why a binary or over-sized file cannot be overwritten either.
                var (previous, readError) = await ReadTextAsync(path, full, cancellationToken)
                    .ConfigureAwait(false);

                if (readError is not null)
                {
                    return WorkspaceResult<WorkspaceWrite>.Fail(readError);
                }

                newline = TextContent.DominantNewline(previous!);
                linesBefore = SplitLines(previous!).Length;
                bom = await HasBomAsync(full, cancellationToken).ConfigureAwait(false);
            }

            return await CommitAsync(path, full, content, newline, bom, existed, linesBefore, 0, cancellationToken)
                .ConfigureAwait(false);
        });

    public Task<WorkspaceResult<WorkspaceWrite>> ReplaceAsync(
        WorkspacePath path,
        string find,
        string replacement,
        bool replaceAll = false,
        CancellationToken cancellationToken = default) =>
        GuardAsync<WorkspaceWrite>(path, async () =>
        {
            ArgumentNullException.ThrowIfNull(replacement);

            if (!TryResolve(path, out var full, out var error))
            {
                return WorkspaceResult<WorkspaceWrite>.Fail(error);
            }

            if (string.IsNullOrEmpty(find))
            {
                return WorkspaceResult<WorkspaceWrite>.Fail(
                    "The text to find cannot be empty. Write the file whole instead.");
            }

            var (previous, readError) = await ReadTextAsync(path, full, cancellationToken).ConfigureAwait(false);

            if (readError is not null)
            {
                return WorkspaceResult<WorkspaceWrite>.Fail(readError);
            }

            // Both sides are re-cast to the file's own line endings before matching, so text
            // copied out of one file and pasted into a call against another still matches.
            var newline = TextContent.DominantNewline(previous!);
            var needle = TextContent.NormalizeNewlines(find, newline);
            var value = TextContent.NormalizeNewlines(replacement, newline);
            var occurrences = CountOccurrences(previous!, needle);

            if (occurrences == 0)
            {
                return WorkspaceResult<WorkspaceWrite>.Fail(
                    $"That text does not appear in '{path}'. Read the file and copy the exact text, indentation included.");
            }

            if (occurrences > 1 && !replaceAll)
            {
                return WorkspaceResult<WorkspaceWrite>.Fail(
                    $"That text appears {occurrences} times in '{path}'. Include more surrounding lines to make it unique, or ask for every occurrence to be replaced.");
            }

            var body = replaceAll
                ? previous!.Replace(needle, value, StringComparison.Ordinal)
                : ReplaceFirst(previous!, needle, value);

            var bom = await HasBomAsync(full, cancellationToken).ConfigureAwait(false);
            var replacements = replaceAll ? occurrences : 1;

            return await CommitAsync(
                    path, full, body, newline, bom,
                    existed: true, SplitLines(previous!).Length, replacements, cancellationToken)
                .ConfigureAwait(false);
        });

    public Task<WorkspaceResult<WorkspaceEntry>> CreateDirectoryAsync(
        WorkspacePath path,
        CancellationToken cancellationToken = default) =>
        GuardAsync<WorkspaceEntry>(path, () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryResolve(path, out var full, out var error))
            {
                return Task.FromResult(WorkspaceResult<WorkspaceEntry>.Fail(error));
            }

            if (path.IsRoot)
            {
                return Task.FromResult(WorkspaceResult<WorkspaceEntry>.Fail("The workspace root already exists."));
            }

            if (File.Exists(full))
            {
                return Task.FromResult(WorkspaceResult<WorkspaceEntry>.Fail($"'{path}' is already a file."));
            }

            var info = Directory.CreateDirectory(full);
            return Task.FromResult(WorkspaceResult<WorkspaceEntry>.Ok(ToEntry(path, info)));
        });

    public Task<WorkspaceResult<WorkspacePath>> DeleteAsync(
        WorkspacePath path,
        CancellationToken cancellationToken = default) =>
        GuardAsync<WorkspacePath>(path, () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryResolve(path, out var full, out var error))
            {
                return Task.FromResult(WorkspaceResult<WorkspacePath>.Fail(error));
            }

            if (path.IsRoot)
            {
                return Task.FromResult(WorkspaceResult<WorkspacePath>.Fail("The workspace root cannot be deleted."));
            }

            if (Directory.Exists(full))
            {
                if (Directory.EnumerateFileSystemEntries(full).Any())
                {
                    return Task.FromResult(WorkspaceResult<WorkspacePath>.Fail(
                        $"'{path}' is not empty. Delete what is inside it first - there is no recursive delete."));
                }

                Directory.Delete(full);
            }
            else if (File.Exists(full))
            {
                File.Delete(full);
            }
            else
            {
                return Task.FromResult(WorkspaceResult<WorkspacePath>.Fail($"'{path}' does not exist."));
            }

            return Task.FromResult(WorkspaceResult<WorkspacePath>.Ok(path));
        });

    public Task<WorkspaceResult<WorkspacePath>> MoveAsync(
        WorkspacePath from,
        WorkspacePath to,
        CancellationToken cancellationToken = default) =>
        GuardAsync<WorkspacePath>(from, () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryResolve(from, out var source, out var sourceError))
            {
                return Task.FromResult(WorkspaceResult<WorkspacePath>.Fail(sourceError));
            }

            if (!TryResolve(to, out var target, out var targetError))
            {
                return Task.FromResult(WorkspaceResult<WorkspacePath>.Fail(targetError));
            }

            if (from.IsRoot || to.IsRoot)
            {
                return Task.FromResult(WorkspaceResult<WorkspacePath>.Fail(
                    "The workspace root cannot be moved or replaced."));
            }

            if (Directory.Exists(target) || File.Exists(target))
            {
                return Task.FromResult(WorkspaceResult<WorkspacePath>.Fail(
                    $"'{to}' already exists. Delete it first if it is meant to be replaced."));
            }

            var isDirectory = Directory.Exists(source);

            if (!isDirectory && !File.Exists(source))
            {
                return Task.FromResult(WorkspaceResult<WorkspacePath>.Fail($"'{from}' does not exist."));
            }

            if (isDirectory && IsUnder(WithoutTrailingSeparator(target), WithoutTrailingSeparator(source)))
            {
                return Task.FromResult(WorkspaceResult<WorkspacePath>.Fail(
                    $"'{to}' is inside '{from}', so the move would put the folder inside itself."));
            }

            var parent = Path.GetDirectoryName(target);

            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (isDirectory)
            {
                Directory.Move(source, target);
            }
            else
            {
                File.Move(source, target, overwrite: false);
            }

            return Task.FromResult(WorkspaceResult<WorkspacePath>.Ok(to));
        });

    public Task<WorkspaceResult<WorkspaceSearchResult>> SearchAsync(
        WorkspaceSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var path = query.Path ?? WorkspacePath.Root;

        return GuardAsync<WorkspaceSearchResult>(path, () =>
        {
            if (string.IsNullOrEmpty(query.Query))
            {
                return Task.FromResult(WorkspaceResult<WorkspaceSearchResult>.Fail("There is nothing to search for."));
            }

            if (!TryResolve(path, out var full, out var error))
            {
                return Task.FromResult(WorkspaceResult<WorkspaceSearchResult>.Fail(error));
            }

            if (!Directory.Exists(full))
            {
                return Task.FromResult(WorkspaceResult<WorkspaceSearchResult>.Fail(
                    $"'{path}' is not a directory, so there is nothing to search under it."));
            }

            Regex? pattern = null;

            if (query.IsRegex)
            {
                try
                {
                    pattern = new Regex(
                        query.Query,
                        query.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase,
                        RegexMatchTimeout);
                }
                catch (ArgumentException ex)
                {
                    return Task.FromResult(WorkspaceResult<WorkspaceSearchResult>.Fail(
                        $"That is not a valid regular expression: {ex.Message}"));
                }
            }

            return Task.Run(() => SearchCore(query, path, full, pattern, cancellationToken), cancellationToken);
        });
    }

    /// <summary>
    /// The scan itself, on a worker thread because it is file-bound and long.
    /// </summary>
    /// <remarks>
    /// Three independent things end it early - the match cap, the time budget, and a pattern that
    /// times out on one line - and all three report <see cref="WorkspaceSearchResult.IsTruncated"/>
    /// rather than pretending the answer is complete. A caller told "no matches" when the truth is
    /// "stopped looking" will delete code it thinks is unreferenced.
    /// </remarks>
    private WorkspaceResult<WorkspaceSearchResult> SearchCore(
        WorkspaceSearchQuery query,
        WorkspacePath path,
        string full,
        Regex? pattern,
        CancellationToken cancellationToken)
    {
        var agent = _settings.Current.Agent;
        var limit = Math.Max(1, agent.MaxSearchResults);
        var comparison = query.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        var matches = new List<WorkspaceMatch>();
        var clock = Stopwatch.StartNew();
        var scanned = 0;
        var truncated = false;

        foreach (var (child, info) in Walk(path, full, recursive: true, agent.IgnoredNames, cancellationToken))
        {
            if (info is not FileInfo file)
            {
                continue;
            }

            if (matches.Count >= limit || clock.Elapsed > SearchBudget)
            {
                truncated = true;
                break;
            }

            // The size cap, the glob and the emptiness test all come before the read: skipping a
            // file has to be cheaper than opening it, or a repository full of build output costs
            // as much as one full of source.
            if (file.Length == 0 || file.Length > agent.MaxFileBytes)
            {
                continue;
            }

            if (query.FilePattern is { Length: > 0 } glob
                && !FileSystemName.MatchesSimpleExpression(glob, file.Name, ignoreCase: true))
            {
                continue;
            }

            byte[] bytes;

            try
            {
                bytes = File.ReadAllBytes(file.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One unreadable file - locked by a build, or permission-denied - is not a reason
                // to abandon a search across thousands of others.
                continue;
            }

            if (TextContent.LooksBinary(bytes.AsSpan(0, Math.Min(bytes.Length, TextContent.SniffLength))))
            {
                continue;
            }

            scanned++;

            var lineNumber = 0;

            foreach (var line in SplitLines(Decode(bytes)))
            {
                lineNumber++;

                bool hit;

                try
                {
                    hit = pattern is null
                        ? line.Contains(query.Query, comparison)
                        : pattern.IsMatch(line);
                }
                catch (RegexMatchTimeoutException)
                {
                    // The expression came from a model. A few characters of nested quantifier are
                    // enough to hang on one long line, so the line is abandoned, not the search.
                    truncated = true;
                    break;
                }

                if (!hit)
                {
                    continue;
                }

                matches.Add(new WorkspaceMatch
                {
                    Path = child,
                    LineNumber = lineNumber,
                    Line = Clip(line),
                });

                if (matches.Count >= limit)
                {
                    truncated = true;
                    break;
                }
            }
        }

        return WorkspaceResult<WorkspaceSearchResult>.Ok(new WorkspaceSearchResult
        {
            Matches = matches,
            FilesScanned = scanned,
            IsTruncated = truncated,
        });
    }

    /// <summary>
    /// Runs one operation and turns anything the file system throws into a refusal.
    /// </summary>
    /// <remarks>
    /// Every method here funnels through this, because the result text is read by a language model
    /// and an escaped <see cref="IOException"/> would surface as an agent-loop defect instead of a
    /// step the model can retry. Two things never reach the returned message: the absolute path,
    /// which contains the user's name, and <c>ex.Message</c>, which contains it as well.
    /// </remarks>
    private async Task<WorkspaceResult<T>> GuardAsync<T>(
        WorkspacePath path,
        Func<Task<WorkspaceResult<T>>> operation)
        where T : class
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller's own cancellation, and not a failure of this operation.
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return WorkspaceResult<T>.Fail($"Access to '{path}' was denied by the operating system.");
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "A workspace operation failed with an I/O error.");
            return WorkspaceResult<T>.Fail(
                $"'{path}' could not be used. It may be open in another program, or on a drive that went away.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A workspace operation failed unexpectedly.");
            return WorkspaceResult<T>.Fail($"'{path}' could not be used.");
        }
    }

    /// <summary>
    /// Turns a relative path into an absolute one, or explains why it will not be touched.
    /// </summary>
    /// <remarks>
    /// The four checks are ordered by cost, and each one is load-bearing on its own:
    /// a workspace has to be open; the name must not be one of the protected ones; the combined
    /// path has to still sit inside the root once <see cref="Path.GetFullPath(string)"/> has
    /// collapsed it; and no directory on the way down may be a link that leaves the root. The last
    /// one cannot be folded into the third - <c>src/x.cs</c> is textually inside the workspace even
    /// when <c>src</c> is a junction to somewhere else entirely.
    /// </remarks>
    private bool TryResolve(
        WorkspacePath path,
        [NotNullWhen(true)] out string? fullPath,
        [NotNullWhen(false)] out string? error)
    {
        fullPath = null;

        var root = Root;

        if (root is null)
        {
            error = "No folder is open. Ask the user to open a project folder first.";
            return false;
        }

        if (ProtectedReason(path) is { } refusal)
        {
            error = refusal;
            return false;
        }

        if (!TryFullPath(path.ResolveAgainst(root), out var candidate))
        {
            error = $"'{path}' is not a usable path.";
            return false;
        }

        if (!IsInside(candidate, root))
        {
            // Unreachable through WorkspacePath's own parse, and checked anyway: this is the last
            // line before a write, and it costs one string comparison.
            error = $"'{path}' resolves outside the open folder, and was refused.";
            return false;
        }

        if (LinkEscape(candidate, root) is { } escape)
        {
            error = escape;
            return false;
        }

        fullPath = candidate;
        error = null;
        return true;
    }

    /// <summary>
    /// Reopens the folder from the last session, once, on first use.
    /// </summary>
    /// <remarks>
    /// Lazy rather than done in the constructor, because nothing orders dependency injection
    /// against <see cref="ISettingsService.LoadAsync"/> - a constructor that read the setting would
    /// read whatever the defaults were. The saved path is re-validated rather than trusted: a
    /// folder can be deleted, renamed, or on a drive that is not mounted this time, and the file
    /// holding it is editable by hand.
    /// </remarks>
    private void EnsureRestored()
    {
        if (Volatile.Read(ref _restored))
        {
            return;
        }

        lock (_gate)
        {
            if (_restored)
            {
                return;
            }

            _restored = true;

            var saved = _settings.Current.Agent.WorkspaceRoot;

            if (string.IsNullOrWhiteSpace(saved))
            {
                return;
            }

            if (TryFullPath(saved, out var full) && Directory.Exists(full) && RefuseAsRoot(full) is null)
            {
                _root = full;
                return;
            }

            _logger.LogInformation("The saved workspace folder is no longer usable and was not reopened.");
        }
    }

    /// <summary>
    /// Whether a folder is unfit to be the workspace root, and why.
    /// </summary>
    /// <remarks>
    /// The root is the user's own choice, made in a folder dialog, so this list is short and blunt:
    /// it exists to stop a mis-click from handing an agent a drive, a home directory, or the folder
    /// holding this application's encrypted API keys. Ordered from the cheapest test to the most,
    /// and phrased for the user rather than for the model - the model never sees these.
    /// </remarks>
    private string? RefuseAsRoot(string full)
    {
        var drive = Path.GetPathRoot(full);

        if (drive is not null && string.Equals(WithoutTrailingSeparator(drive), full, StringComparison.OrdinalIgnoreCase))
        {
            return "A whole drive cannot be a workspace. Open the project folder itself.";
        }

        if (KnownFolder(Environment.SpecialFolder.UserProfile) is { } profile
            && string.Equals(full, profile, StringComparison.OrdinalIgnoreCase))
        {
            return "Your whole user folder is too broad for a workspace. Open the project folder itself.";
        }

        // Checked by its real path rather than by a known-folder constant, because this is the
        // directory that holds the encrypted API keys and the conversation database.
        if (Overlaps(full, _paths.DataDirectory))
        {
            return "That folder holds this application's own data, including the encrypted API keys "
                + "and the conversation history, so it cannot be opened as a workspace.";
        }

        foreach (var folder in SystemFolders)
        {
            if (KnownFolder(folder) is { } resolved && Overlaps(full, resolved))
            {
                return "That is a system folder, and cannot be opened as a workspace.";
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves one of Windows' known folders, or null when the platform does not define it.
    /// </summary>
    /// <remarks>
    /// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> answers with an empty
    /// string rather than throwing when a folder has no meaning here - which every comparison below
    /// has to treat as "no such folder", since an empty prefix matches every path there is.
    /// </remarks>
    private static string? KnownFolder(Environment.SpecialFolder folder)
    {
        var raw = Environment.GetFolderPath(folder);

        return !string.IsNullOrWhiteSpace(raw) && TryFullPath(raw, out var full) ? full : null;
    }

    /// <summary>
    /// Canonicalises an absolute path, or reports that the operating system will not have it.
    /// </summary>
    /// <remarks>
    /// The trailing separator is dropped so every path in this class has one spelling. Keeping it
    /// would make <c>C:\proj</c> and <c>C:\proj\</c> two different roots, and the prefix test that
    /// decides containment would then depend on which one was stored.
    /// </remarks>
    private static bool TryFullPath(string raw, [NotNullWhen(true)] out string? full)
    {
        try
        {
            full = WithoutTrailingSeparator(Path.GetFullPath(raw));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            full = null;
            return false;
        }
    }

    /// <summary>
    /// Strips trailing separators, leaving at least one character.
    /// </summary>
    private static string WithoutTrailingSeparator(string path)
    {
        var end = path.Length;

        while (end > 1 && (path[end - 1] == Path.DirectorySeparatorChar || path[end - 1] == Path.AltDirectorySeparatorChar))
        {
            end--;
        }

        return end == path.Length ? path : path[..end];
    }

    /// <summary>
    /// Whether <paramref name="child"/> sits strictly below <paramref name="parent"/>.
    /// </summary>
    /// <remarks>
    /// The separator is part of the comparison on purpose: a bare prefix test makes
    /// <c>C:\project-notes</c> look like it is inside <c>C:\project</c>. Both sides are compared
    /// case-insensitively, because Windows paths are, and a case-sensitive test would refuse a
    /// perfectly valid root the user re-typed with different capitalisation.
    /// </remarks>
    private static bool IsUnder(string child, string parent) =>
        child.Length > parent.Length
        && (child[parent.Length] == Path.DirectorySeparatorChar
            || child[parent.Length] == Path.AltDirectorySeparatorChar)
        && child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a path is the root itself or something below it.</summary>
    private static bool IsInside(string candidate, string root)
    {
        var normalized = WithoutTrailingSeparator(root);

        return string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase)
            || IsUnder(candidate, normalized);
    }

    /// <summary>Whether two paths are the same, or either contains the other.</summary>
    private static bool Overlaps(string a, string b)
    {
        a = WithoutTrailingSeparator(a);
        b = WithoutTrailingSeparator(b);

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || IsUnder(a, b) || IsUnder(b, a);
    }

    /// <summary>
    /// Follows every link on the way down to a path and refuses one that leads out of the root.
    /// </summary>
    /// <remarks>
    /// The check that matters, and the one a prefix comparison cannot make. <c>src/main.cs</c> is
    /// textually inside the workspace no matter what <c>src</c> is; if <c>src</c> is a junction to
    /// <c>C:\Windows\System32</c> then the write lands there. So every existing level is resolved to
    /// its final target - <c>returnFinalTarget</c>, since a link may point at another link - and the
    /// target is measured against the root. Levels that do not exist yet are skipped: a file about
    /// to be created cannot be a link, and its parents were checked on the way past.
    /// </remarks>
    private static string? LinkEscape(string candidate, string root)
    {
        var normalizedRoot = WithoutTrailingSeparator(root);

        foreach (var level in Ancestors(candidate, normalizedRoot))
        {
            FileSystemInfo info;

            if (Directory.Exists(level))
            {
                info = new DirectoryInfo(level);
            }
            else if (File.Exists(level))
            {
                info = new FileInfo(level);
            }
            else
            {
                continue;
            }

            string? target;

            try
            {
                target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            }
            catch (IOException)
            {
                // A broken link, or a cycle the operating system gave up on. Either way this is not
                // a path anything should be written through.
                return "That path leads through a link that could not be resolved, and was refused.";
            }

            if (target is not null && !IsInside(WithoutTrailingSeparator(target), normalizedRoot))
            {
                return "That path leads through a link that leaves the workspace, and was refused.";
            }
        }

        return null;
    }

    /// <summary>
    /// Every level from the root's first child down to <paramref name="candidate"/> itself.
    /// </summary>
    /// <remarks>
    /// Outermost first, so the shallowest link is the one that decides. The root is not included:
    /// it may well be a link, and the user chose it, so containment is measured against the root as
    /// it was given rather than against wherever it points.
    /// </remarks>
    private static IEnumerable<string> Ancestors(string candidate, string root)
    {
        var levels = new List<string>();
        var current = candidate;

        while (IsUnder(current, root))
        {
            levels.Add(current);

            var parent = Path.GetDirectoryName(current);

            if (string.IsNullOrEmpty(parent))
            {
                break;
            }

            current = parent;
        }

        levels.Reverse();
        return levels;
    }

    /// <summary>
    /// Whether any segment of a path names something the agent may not touch, and why.
    /// </summary>
    /// <remarks>
    /// Every segment, not just the last: <c>.git/config</c> is refused because <c>.git</c> is, and
    /// so is <c>.ssh/id_rsa.pub</c>. The wording names the segment rather than the whole path so the
    /// model can see which part of its request was the problem.
    /// </remarks>
    private static string? ProtectedReason(WorkspacePath path)
    {
        foreach (var segment in path.Segments)
        {
            if (IsProtectedName(segment))
            {
                return $"'{segment}' holds credentials or version-control internals, and is off limits. "
                    + "Ask the user to do anything that needs it.";
            }
        }

        return null;
    }

    /// <summary>
    /// Whether one path segment is protected.
    /// </summary>
    /// <remarks>
    /// Exact names, never prefixes - a prefix test would refuse <c>.gitignore</c> along with
    /// <c>.git</c>, and the agent has every reason to read the first. The one deliberate family
    /// match is <c>.env</c>: the real file is <c>.env</c>, <c>.env.local</c> or
    /// <c>.env.production</c> and they all hold secrets, while <c>.env.example</c> exists in order
    /// to be committed and is an ordinary file.
    /// </remarks>
    private static bool IsProtectedName(string segment)
    {
        if (ProtectedNames.Contains(segment, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (segment.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            && !EnvTemplateSuffixes.Any(suffix => segment.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var extension = Path.GetExtension(segment);

        return extension.Length > 0
            && ProtectedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Walks one directory, or a subtree, yielding the entries a caller is allowed to see.
    /// </summary>
    /// <remarks>
    /// Directories first and then by name, so a listing reads the way a file tree does and two calls
    /// against an unchanged folder return the same order. Recursion is explicit rather than
    /// <see cref="EnumerationOptions.RecurseSubdirectories"/> so that the ignore list prunes whole
    /// subtrees: <c>node_modules</c> costs one skipped name instead of a walk of everything under it.
    /// </remarks>
    private static IEnumerable<(WorkspacePath Path, FileSystemInfo Info)> Walk(
        WorkspacePath parent,
        string parentFull,
        bool recursive,
        IReadOnlyCollection<string> ignored,
        CancellationToken cancellationToken)
    {
        var entries = Enumerate(parentFull);

        if (entries is null)
        {
            yield break;
        }

        var subdirectories = new List<(WorkspacePath Path, string Full)>();

        foreach (var info in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ignored.Contains(info.Name, StringComparer.OrdinalIgnoreCase) || IsProtectedName(info.Name))
            {
                continue;
            }

            // A name the path type would refuse as input is not handed out as output either -
            // otherwise the model sees an entry in a listing that every later call rejects.
            if (!parent.TryAppend(info.Name, out var child, out _))
            {
                continue;
            }

            yield return (child, info);

            if (recursive && info is DirectoryInfo)
            {
                subdirectories.Add((child, info.FullName));
            }
        }

        foreach (var (childPath, childFull) in subdirectories)
        {
            foreach (var descendant in Walk(childPath, childFull, recursive, ignored, cancellationToken))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// Reads one directory, or answers null when it cannot be read.
    /// </summary>
    /// <remarks>
    /// Materialised inside the <c>try</c>, because a deferred enumeration would throw at the caller's
    /// <c>foreach</c> instead. A folder that vanished mid-walk or refused a handle skips rather than
    /// ending the listing: a build running in the background deletes directories constantly, and one
    /// of them must not cost the agent its whole view of the tree.
    /// </remarks>
    private static FileSystemInfo[]? Enumerate(string directory)
    {
        try
        {
            return new DirectoryInfo(directory)
                .EnumerateFileSystemInfos("*", EnumerationRules)
                .OrderBy(entry => entry is DirectoryInfo ? 0 : 1)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static WorkspaceEntry ToEntry(WorkspacePath path, FileSystemInfo info) => new()
    {
        Path = path,
        IsDirectory = info is DirectoryInfo,
        Size = info is FileInfo file ? file.Length : 0,
        ModifiedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
    };

    /// <summary>
    /// Reads a whole file as text, or explains why it will not be read.
    /// </summary>
    /// <remarks>
    /// The gate in front of every read and every overwrite, which is why the size cap and the binary
    /// sniff live here rather than in each caller. The refusals are worded as instructions: a model
    /// told a file is too large is expected to search it instead, and a model told a file is binary
    /// is expected to stop trying.
    /// </remarks>
    private async Task<(string? Text, string? Error)> ReadTextAsync(
        WorkspacePath path,
        string full,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(full))
        {
            return (null, $"'{path}' is a directory, not a file.");
        }

        var info = new FileInfo(full);

        if (!info.Exists)
        {
            return (null, $"'{path}' does not exist.");
        }

        var cap = _settings.Current.Agent.MaxFileBytes;

        if (info.Length > cap)
        {
            return (null, $"'{path}' is {FormatSize(info.Length)}, over the {FormatSize(cap)} limit. "
                + "Search it for what you need instead of reading it whole.");
        }

        if (await TextContent.IsBinaryAsync(full, cancellationToken).ConfigureAwait(false))
        {
            return (null, $"'{path}' contains binary data, so it cannot be read as text.");
        }

        // Strips a byte-order mark if there is one; whether to write it back is answered separately.
        var text = await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);

        return (text, null);
    }

    /// <summary>Whether a file starts with a UTF-8 byte-order mark, so a rewrite can keep it.</summary>
    private static async Task<bool> HasBomAsync(string full, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);

        var head = new byte[3];
        var read = await stream
            .ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);

        return read == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
    }

    /// <summary>
    /// The one place bytes reach a file: normalise, check the size, write atomically, report.
    /// </summary>
    /// <remarks>
    /// Both writing paths end here so neither can forget the newline that was found or the mark that
    /// was there. The size is counted before anything is written, from the encoded bytes rather than
    /// the character count, because a file of CJK text is three times its length in bytes.
    /// </remarks>
    private async Task<WorkspaceResult<WorkspaceWrite>> CommitAsync(
        WorkspacePath path,
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
        var cap = _settings.Current.Agent.MaxFileBytes;
        var size = Utf8NoBom.GetByteCount(body) + (bom ? 3 : 0);

        if (size > cap)
        {
            return WorkspaceResult<WorkspaceWrite>.Fail(
                $"That content is {FormatSize(size)}, over the {FormatSize(cap)} limit for one file.");
        }

        await WriteAtomicAsync(full, body, bom, cancellationToken).ConfigureAwait(false);

        return WorkspaceResult<WorkspaceWrite>.Ok(new WorkspaceWrite
        {
            Path = path,
            Created = !existed,
            LinesBefore = linesBefore,
            LinesAfter = SplitLines(body).Length,
            Size = size,
            Replacements = replacements,
        });
    }

    /// <summary>
    /// Writes a file whole, leaving either the old contents or the new ones and never half of each.
    /// </summary>
    /// <remarks>
    /// A sibling temporary file and a move, because the file being replaced is usually source code
    /// the user has open. Truncating it in place and then failing - a full disk, a cancelled step, a
    /// process killed mid-write - loses work that was not this application's to lose. The temporary
    /// file is a sibling rather than in the system temp folder so the move stays on one volume, which
    /// is what makes it a rename instead of a copy.
    /// </remarks>
    private static async Task WriteAtomicAsync(
        string full,
        string body,
        bool bom,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(full);

        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var temp = full + ".aiclient.tmp";

        try
        {
            await File.WriteAllTextAsync(temp, body, bom ? Utf8WithBom : Utf8NoBom, cancellationToken)
                .ConfigureAwait(false);

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
                // Nothing useful is left to do about it, and the original failure is the one the
                // caller needs to see.
            }

            throw;
        }
    }

    /// <summary>
    /// Splits text into lines, counting the way an editor does.
    /// </summary>
    /// <remarks>
    /// A file ending in a newline has that many lines, not one more. Getting this wrong is not
    /// cosmetic: the count is what a model uses to ask for the next window, and an invented final
    /// line makes every read past the end of a file look like a real one.
    /// </remarks>
    private static string[] SplitLines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var lines = TextContent.NormalizeNewlines(text, "\n").Split('\n');

        return lines.Length > 1 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    /// <summary>
    /// Counts non-overlapping occurrences, which is what <see cref="string.Replace(string, string)"/>
    /// substitutes - so the number reported back is the number of edits actually made.
    /// </summary>
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

    /// <summary>
    /// Decodes bytes already in hand as UTF-8, dropping a byte-order mark.
    /// </summary>
    /// <remarks>
    /// A search reads each file once and needs both the raw bytes, for the binary check, and the
    /// text. Left in place the mark becomes a zero-width character on the first line, which is
    /// enough to stop a match on a term at the very start of a file.
    /// </remarks>
    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bytes = bytes[3..];
        }

        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Trims and shortens a matched line so one minified file cannot fill the result.</summary>
    private static string Clip(string line)
    {
        var trimmed = line.Trim();

        return trimmed.Length <= MaxMatchLineLength ? trimmed : trimmed[..MaxMatchLineLength] + "…";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };
}
