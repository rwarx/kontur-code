using AIClient.Application.Configuration;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace AIClient.Application.Services;

/// <summary>
/// Reads user-selected files for inlining into a prompt.
/// </summary>
/// <remarks>
/// This is a security boundary and is written as one. Three independent checks apply:
/// an extension allow-list (no executables, no archives), a size cap enforced before the
/// file is opened, and a binary sniff that rejects a file whose bytes contradict its
/// extension. Nothing here executes an attachment or follows a path the user did not choose.
/// </remarks>
public sealed class AttachmentService : IAttachmentService
{
    /// <summary>
    /// Allowed extensions. An allow-list rather than a deny-list: the failure mode of
    /// missing an entry is a rejected file the user can rename, while the failure mode of
    /// missing a dangerous extension is far worse.
    /// </summary>
    private static readonly string[] Extensions =
    [
        // Documents and data
        ".txt", ".md", ".markdown", ".rst", ".log", ".csv", ".tsv",
        ".json", ".jsonc", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf",
        // .NET
        ".cs", ".vb", ".fs", ".fsx", ".csproj", ".fsproj", ".vbproj", ".sln", ".props", ".targets", ".razor", ".cshtml", ".xaml",
        // C family
        ".c", ".h", ".cpp", ".cxx", ".cc", ".hpp", ".hxx", ".m", ".mm",
        // JVM and friends
        ".java", ".kt", ".kts", ".scala", ".groovy", ".gradle",
        // Scripting
        ".py", ".pyi", ".rb", ".php", ".pl", ".lua", ".r",
        // Web
        ".js", ".mjs", ".cjs", ".jsx", ".ts", ".tsx", ".vue", ".svelte",
        ".html", ".htm", ".css", ".scss", ".sass", ".less",
        // Systems
        ".go", ".rs", ".swift", ".zig", ".dart",
        // Shell and infra
        ".sh", ".bash", ".zsh", ".ps1", ".psm1", ".bat", ".cmd",
        ".dockerfile", ".tf", ".tfvars",
        // Query
        ".sql", ".graphql", ".gql", ".prisma",
        // Diff
        ".patch", ".diff",
    ];

    /// <summary>
    /// Files that are conventionally plain text but carry no usable extension, matched by
    /// exact name. Leading-dot names live here rather than in <see cref="Extensions"/>
    /// because <c>Path.GetExtension(".gitignore")</c> returns <c>".gitignore"</c> - the whole
    /// name - so such a file can never match an extension lookup.
    /// </summary>
    /// <remarks>
    /// Matched exactly and never by prefix: a prefix test would wave ".gitignore.exe"
    /// through, and this list has to stand on its own rather than lean on the binary sniff.
    /// </remarks>
    private static readonly string[] TextFileNames =
    [
        "Dockerfile", "Makefile", "Rakefile", "Gemfile", "Procfile",
        "LICENSE", "LICENCE", "README", "CHANGELOG", "AUTHORS", "NOTICE", "CONTRIBUTING",
        ".gitignore", ".gitattributes", ".gitmodules", ".dockerignore",
        ".editorconfig", ".npmrc", ".nvmrc", ".prettierrc", ".eslintrc", ".env.example",
    ];

    /// <summary>
    /// Bytes inspected for the binary check. A UTF-8 BOM plus a header is well inside this,
    /// and reading more would not improve the verdict.
    /// </summary>
    private const int SniffLength = 8192;
    /// <summary>
    /// Share of NUL bytes above which a file is called binary. Text files contain none;
    /// a small tolerance covers UTF-16 content that slipped past encoding detection.
    /// </summary>
    private const double BinaryNulThreshold = 0.01;

    private readonly ISettingsService _settings;
    private readonly ILogger<AttachmentService> _logger;

    public AttachmentService(ISettingsService settings, ILogger<AttachmentService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public IReadOnlyList<string> SupportedExtensions => Extensions;

    public bool IsSupported(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        // Checked before the extension, not only when there is none: a dotfile reports its
        // whole name as its extension, so the name is the only thing there is to match on.
        if (TextFileNames.Contains(Path.GetFileName(filePath), StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var extension = Path.GetExtension(filePath);

        return !string.IsNullOrEmpty(extension)
            && Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<AttachmentResult> LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return AttachmentResult.Fail("That file no longer exists.");
            }

            if (!IsSupported(filePath))
            {
                var extension = Path.GetExtension(filePath);
                return AttachmentResult.Fail(
                    string.IsNullOrEmpty(extension)
                        ? "Only text files can be attached."
                        : $"{extension} files cannot be attached. Only text files are supported.");
            }

            var info = new FileInfo(filePath);
            var storage = _settings.Current.Storage;

            // Checked before opening: refusing a 4 GB file must not require reading it.
            if (info.Length > storage.MaxAttachmentBytes)
            {
                return AttachmentResult.Fail(
                    $"That file is {FormatSize(info.Length)}. The limit is {FormatSize(storage.MaxAttachmentBytes)}.");
            }

            if (info.Length == 0)
            {
                return AttachmentResult.Fail("That file is empty.");
            }

            if (await IsBinaryAsync(filePath, cancellationToken).ConfigureAwait(false))
            {
                return AttachmentResult.Fail("That file contains binary data and cannot be attached.");
            }

            var text = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);

            var truncated = false;
            if (text.Length > storage.MaxAttachmentCharacters)
            {
                text = text[..storage.MaxAttachmentCharacters];
                truncated = true;

                _logger.LogInformation(
                    "Attachment {FileName} was truncated to {Characters} characters.",
                    info.Name, storage.MaxAttachmentCharacters);
            }

            return AttachmentResult.Ok(new NewAttachment
            {
                FileName = info.Name,
                MimeType = ResolveMimeType(info.Extension),
                Size = info.Length,
                TextContent = text,
                IsTruncated = truncated,
            });
        }
        catch (UnauthorizedAccessException)
        {
            return AttachmentResult.Fail("Access to that file was denied.");
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read the attachment at the selected path.");
            return AttachmentResult.Fail("That file could not be read. It may be open in another program.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The path is deliberately kept out of the message: it can contain a user name.
            _logger.LogError(ex, "Unexpected failure while reading an attachment.");
            return AttachmentResult.Fail("That file could not be attached.");
        }
    }

    public string BuildFileDialogFilter()
    {
        // The conventional names are listed as well as the extensions: they are exactly the
        // files the dialog would otherwise hide behind "All files".
        var patterns = string.Join(';', Extensions.Select(e => $"*{e}").Concat(TextFileNames));
        return $"Text and code files|{patterns}|All files (*.*)|*.*";
    }

    /// <summary>
    /// Rejects files whose contents are binary regardless of extension - a renamed
    /// executable must not be inlined into a prompt.
    /// </summary>
    private static async Task<bool> IsBinaryAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, SniffLength, useAsync: true);

        var buffer = new byte[Math.Min(SniffLength, (int)Math.Min(stream.Length, SniffLength))];
        var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

        if (read == 0)
        {
            return false;
        }

        var nulCount = 0;
        for (var i = 0; i < read; i++)
        {
            if (buffer[i] == 0)
            {
                nulCount++;
            }
        }

        return (double)nulCount / read > BinaryNulThreshold;
    }

    private static string ResolveMimeType(string extension) => extension.ToLowerInvariant() switch
    {
        ".json" or ".jsonc" => "application/json",
        ".xml" or ".xaml" or ".csproj" or ".props" or ".targets" => "application/xml",
        ".html" or ".htm" => "text/html",
        ".css" or ".scss" or ".sass" or ".less" => "text/css",
        ".csv" => "text/csv",
        ".md" or ".markdown" => "text/markdown",
        ".yaml" or ".yml" => "application/yaml",
        ".js" or ".mjs" or ".cjs" => "text/javascript",
        ".sql" => "application/sql",
        _ => "text/plain",
    };

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };
}
