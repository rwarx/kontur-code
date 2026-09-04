using System.Text;
using AIClient.Application.Configuration;
using AIClient.Application.DTOs;
using AIClient.Domain.Workspace;
using AIClient.Infrastructure.Configuration;
using AIClient.Infrastructure.Workspace;
using AIClient.Tests.Support;

namespace AIClient.Tests;

/// <summary>
/// Section 28: the file access an agent is given, and everything it is refused.
/// </summary>
/// <remarks>
/// Over a real temporary directory rather than an abstracted file system. Half of what this class
/// guarantees is only true of a real one - a junction whose target sits outside the root, a
/// byte-order mark, an atomic replace that leaves no temporary file behind - and a substituted file
/// system would assert those against a fake that agrees with whatever the implementation does.
/// </remarks>
public sealed class WorkspaceServiceTests : IAsyncLifetime
{
    private readonly StubSettingsService _settings = new();
    private readonly RecordingLogger<WorkspaceService> _logger = new();

    private string _scratch = null!;
    private string _root = null!;
    private AppPaths _paths = null!;
    private WorkspaceService _service = null!;

    public async ValueTask InitializeAsync()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "aiclient-workspace", Guid.CreateVersion7().ToString("n"));
        _root = Path.Combine(_scratch, "project");

        // The application's own data directory is a sibling of the workspace, never inside it:
        // opening a folder that holds the encrypted API keys is one of the things being refused.
        _paths = new AppPaths(Path.Combine(_scratch, "appdata"));

        Directory.CreateDirectory(_root);
        await SeedAsync();

        _service = new WorkspaceService(_settings, _paths, _logger);

        var opened = await _service.OpenAsync(_root, Token);
        Assert.True(opened.Success, opened.Error);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth failing a run over a leftover temporary directory.
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public void Opening_a_folder_makes_it_the_root_and_remembers_it()
    {
        Assert.True(_service.IsOpen);
        Assert.Equal(_root, _service.Root, ignoreCase: true);
        Assert.Equal(_root, _settings.Current.Agent.WorkspaceRoot, ignoreCase: true);
    }

    [Fact]
    public async Task Opening_a_folder_announces_the_change()
    {
        string? announced = null;
        var second = Path.Combine(_scratch, "second");
        Directory.CreateDirectory(second);

        _service.RootChanged += (_, root) => announced = root;

        await _service.OpenAsync(second, Token);

        Assert.Equal(second, announced, ignoreCase: true);
    }

    [Fact]
    public async Task A_whole_drive_cannot_be_a_workspace()
    {
        var drive = Path.GetPathRoot(_root);
        Assert.NotNull(drive);

        var result = await _service.OpenAsync(drive, Token);

        Assert.False(result.Success);
        Assert.Contains("drive", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_folder_holding_this_applications_own_data_cannot_be_a_workspace()
    {
        var result = await _service.OpenAsync(_paths.DataDirectory, Token);

        Assert.False(result.Success);
        Assert.Contains("API keys", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_folder_that_does_not_exist_cannot_be_a_workspace()
    {
        var result = await _service.OpenAsync(Path.Combine(_scratch, "absent"), Token);

        Assert.False(result.Success);
        Assert.Contains("does not exist", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_can_be_read_before_a_folder_is_opened()
    {
        var closed = new WorkspaceService(new StubSettingsService(), _paths, _logger);

        var result = await closed.ReadAsync(Parse("README.md"), cancellationToken: Token);

        Assert.False(result.Success);
        Assert.Contains("No folder is open", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Closing_the_workspace_stops_every_operation()
    {
        await _service.CloseAsync(Token);

        var result = await _service.ReadAsync(Parse("README.md"), cancellationToken: Token);

        Assert.False(_service.IsOpen);
        Assert.Null(_service.Root);
        Assert.False(result.Success);
        Assert.Null(_settings.Current.Agent.WorkspaceRoot);
    }

    [Fact]
    public void The_folder_from_the_last_run_is_reopened()
    {
        // A second instance over the same saved settings, which is what a restart amounts to.
        var restarted = new WorkspaceService(_settings, _paths, _logger);

        Assert.Equal(_root, restarted.Root, ignoreCase: true);
    }

    [Fact]
    public void A_saved_folder_that_has_since_been_deleted_is_not_reopened()
    {
        var settings = new StubSettingsService()
            .With<AgentSettings>(agent => agent.WorkspaceRoot = Path.Combine(_scratch, "gone"));

        var restarted = new WorkspaceService(settings, _paths, _logger);

        Assert.Null(restarted.Root);
        Assert.False(restarted.IsOpen);
    }

    [Fact]
    public async Task A_listing_names_children_relative_to_the_root()
    {
        var listing = await Listing(WorkspacePath.Root);

        Assert.Contains("README.md", listing);
        Assert.Contains("src", listing);
        Assert.Contains(".gitignore", listing);
        Assert.Contains(".env.example", listing);
    }

    [Fact]
    public async Task A_listing_hides_build_output_and_anything_holding_a_credential()
    {
        var listing = await Listing(WorkspacePath.Root);

        Assert.DoesNotContain("node_modules", listing);
        Assert.DoesNotContain("bin", listing);
        Assert.DoesNotContain(".git", listing);
        Assert.DoesNotContain(".env", listing);
    }

    [Fact]
    public async Task A_recursive_listing_reaches_nested_files()
    {
        var listing = await Listing(WorkspacePath.Root, recursive: true);

        Assert.Contains("src/lib/util.cs", listing);
    }

    [Fact]
    public async Task A_listing_directories_come_before_files()
    {
        var listing = await Listing(Parse("src"));

        Assert.Equal(new[] { "src/lib", "src/Program.cs" }, listing);
    }

    [Fact]
    public async Task A_listing_stops_at_the_cap_and_says_so()
    {
        _settings.With<AgentSettings>(agent => agent.MaxListEntries = 2);

        var result = await _service.ListAsync(WorkspacePath.Root, cancellationToken: Token);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.Value!.Entries.Count);
        Assert.True(result.Value.IsTruncated);
    }

    [Fact]
    public async Task Listing_a_file_says_it_is_a_file()
    {
        var result = await _service.ListAsync(Parse("README.md"), cancellationToken: Token);

        Assert.False(result.Success);
        Assert.Contains("is a file", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reading_a_file_returns_it_whole_with_its_line_count()
    {
        var result = await _service.ReadAsync(Parse("README.md"), cancellationToken: Token);

        Assert.True(result.Success, result.Error);
        Assert.Equal("# Project\nline one\nline two", result.Value!.Content);
        Assert.Equal(3, result.Value.TotalLines);
        Assert.Equal(1, result.Value.FirstLine);
        Assert.False(result.Value.IsTruncated);
    }

    [Fact]
    public async Task Reading_a_window_returns_only_those_lines()
    {
        var result = await _service.ReadAsync(Parse("README.md"), startLine: 2, lineCount: 1, cancellationToken: Token);

        Assert.True(result.Success, result.Error);
        Assert.Equal("line one", result.Value!.Content);
        Assert.Equal(2, result.Value.FirstLine);
        Assert.Equal(1, result.Value.LineCount);

        // The total is still reported, so the caller can tell there is more it has not seen.
        Assert.Equal(3, result.Value.TotalLines);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(1, 0)]
    public async Task A_window_that_cannot_exist_is_refused(int startLine, int? lineCount)
    {
        var result = await _service.ReadAsync(Parse("README.md"), startLine, lineCount, Token);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Reading_past_the_end_of_a_file_is_refused_with_its_length()
    {
        var result = await _service.ReadAsync(Parse("README.md"), startLine: 99, cancellationToken: Token);

        Assert.False(result.Success);
        Assert.Contains("has 3 lines", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reading_a_binary_file_is_refused()
    {
        var result = await _service.ReadAsync(Parse("data.bin"), cancellationToken: Token);

        Assert.False(result.Success);
        Assert.Contains("binary", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reading_a_file_over_the_size_cap_is_refused_and_suggests_a_search()
    {
        _settings.With<AgentSettings>(agent => agent.MaxFileBytes = 8);

        var result = await _service.ReadAsync(Parse("README.md"), cancellationToken: Token);

        Assert.False(result.Success);
        Assert.Contains("Search it", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_stops_at_the_character_cap_and_says_so()
    {
        _settings.With<AgentSettings>(agent => agent.MaxReadCharacters = 4);

        var result = await _service.ReadAsync(Parse("README.md"), cancellationToken: Token);

        Assert.True(result.Success, result.Error);
        Assert.Equal("# Pr", result.Value!.Content);
        Assert.True(result.Value.IsTruncated);
    }

    /// <remarks>
    /// None of these files needs to exist. The refusal is made from the path alone, before anything
    /// is opened, so a name that is off limits stays off limits whether or not it is there.
    /// </remarks>
    [Theory]
    [InlineData(".git/config")]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData("id_rsa")]
    [InlineData("server.pem")]
    [InlineData(".ssh/id_ed25519")]
    [InlineData("secrets.json")]
    [InlineData("config/credentials.json")]
    [InlineData("keys/private.key")]
    [InlineData("certificates/site.pfx")]
    public async Task A_path_holding_a_credential_is_refused_even_when_named_exactly(string path)
    {
        var result = await _service.ReadAsync(Parse(path), cancellationToken: Token);

        Assert.False(result.Success);
        Assert.Contains("off limits", result.Error, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The other half of the same rule, and the reason it is written as exact names rather than
    /// prefixes: these are ordinary files that exist in order to be committed, and an agent that
    /// cannot read them cannot follow a project's own conventions.
    /// </remarks>
    [Theory]
    [InlineData(".gitignore")]
    [InlineData(".env.example")]
    public async Task A_file_that_only_looks_like_a_secret_is_readable(string path)
    {
        var result = await _service.ReadAsync(Parse(path), cancellationToken: Token);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task A_write_cannot_reach_a_protected_path_either()
    {
        var result = await _service.WriteAsync(Parse(".git/config"), "[core]", Token);

        Assert.False(result.Success);
        Assert.Contains("off limits", result.Error, StringComparison.Ordinal);
        Assert.Equal("[remote \"origin\"]\n", await File.ReadAllTextAsync(Path.Combine(_root, ".git", "config"), Token));
    }

    /// <summary>
    /// The check a textual one cannot make: <c>escape/secret.txt</c> is inside the workspace by
    /// every string comparison there is, and outside it on disk.
    /// </summary>
    [Fact]
    public async Task A_path_reached_through_a_link_out_of_the_workspace_is_refused()
    {
        var outside = Path.Combine(_scratch, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "not yours", Token);

        Assert.SkipUnless(
            TryLink(Path.Combine(_root, "escape"), outside),
            "Creating a symbolic link needs Developer Mode or an elevated session.");

        var read = await _service.ReadAsync(Parse("escape/secret.txt"), cancellationToken: Token);
        var write = await _service.WriteAsync(Parse("escape/planted.txt"), "mine now", Token);

        Assert.False(read.Success);
        Assert.Contains("link", read.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(write.Success);
        Assert.False(File.Exists(Path.Combine(outside, "planted.txt")));
    }

    /// <remarks>
    /// A link inside the workspace pointing at a large tree elsewhere would otherwise be walked as
    /// though it were part of the project - one junction to a home directory and a listing costs a
    /// scan of everything the user owns. Enumeration skips reparse points outright, so neither the
    /// link nor anything under it appears.
    /// </remarks>
    [Fact]
    public async Task A_recursive_listing_never_walks_through_a_link()
    {
        var outside = Path.Combine(_scratch, "outside");
        Directory.CreateDirectory(Path.Combine(outside, "nested"));
        await File.WriteAllTextAsync(Path.Combine(outside, "nested", "deep.txt"), "elsewhere", Token);

        Assert.SkipUnless(
            TryLink(Path.Combine(_root, "escape"), outside),
            "Creating a symbolic link needs Developer Mode or an elevated session.");

        var listing = await Listing(WorkspacePath.Root, recursive: true);

        Assert.DoesNotContain("escape", listing);
        Assert.DoesNotContain("escape/nested", listing);
        Assert.DoesNotContain("escape/nested/deep.txt", listing);
    }

    [Fact]
    public async Task A_write_creates_a_file_and_says_that_it_did()
    {
        var result = await _service.WriteAsync(Parse("docs/notes.md"), "first\nsecond\n", Token);

        Assert.True(result.Success, result.Error);
        Assert.True(result.Value!.Created);
        Assert.Equal(0, result.Value.LinesBefore);
        Assert.Equal(2, result.Value.LinesAfter);
        Assert.True(File.Exists(Path.Combine(_root, "docs", "notes.md")));
    }

    [Fact]
    public async Task Replacing_a_file_reports_what_was_there_before()
    {
        var result = await _service.WriteAsync(Parse("crlf.txt"), "only one line\n", Token);

        Assert.True(result.Success, result.Error);
        Assert.False(result.Value!.Created);
        Assert.Equal(2, result.Value.LinesBefore);
        Assert.Equal(1, result.Value.LinesAfter);
    }

    /// <remarks>
    /// The single most important thing a write gets right. A model writes bare line feeds; a file
    /// rewritten with them is a whole-file diff in the user's version control, and a one-line change
    /// nobody can review.
    /// </remarks>
    [Fact]
    public async Task A_write_keeps_the_line_endings_the_file_already_had()
    {
        await _service.WriteAsync(Parse("crlf.txt"), "alpha\nbeta\n", Token);
        await _service.WriteAsync(Parse("lf.txt"), "alpha\r\nbeta\r\n", Token);

        Assert.Equal("alpha\r\nbeta\r\n", await Raw("crlf.txt"));
        Assert.Equal("alpha\nbeta\n", await Raw("lf.txt"));
    }

    /// <remarks>
    /// The other half of the same rule. There is no existing file to take the endings from, so they
    /// come from the content itself rather than from the host: a project full of LF files should not
    /// acquire one CRLF file simply because the agent was the one who added it.
    /// </remarks>
    [Fact]
    public async Task A_new_file_keeps_the_line_endings_it_was_written_with()
    {
        await _service.WriteAsync(Parse("new/lf.txt"), "alpha\nbeta\n", Token);
        await _service.WriteAsync(Parse("new/crlf.txt"), "alpha\r\nbeta\r\n", Token);

        Assert.Equal("alpha\nbeta\n", await Raw("new/lf.txt"));
        Assert.Equal("alpha\r\nbeta\r\n", await Raw("new/crlf.txt"));
    }

    [Fact]
    public async Task A_write_keeps_a_byte_order_mark()
    {
        await _service.WriteAsync(Parse("bom.txt"), "replaced", Token);

        var bytes = await File.ReadAllBytesAsync(Path.Combine(_root, "bom.txt"), Token);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
    }

    [Fact]
    public async Task A_new_file_is_written_without_a_byte_order_mark()
    {
        await _service.WriteAsync(Parse("plain.txt"), "text", Token);

        var bytes = await File.ReadAllBytesAsync(Path.Combine(_root, "plain.txt"), Token);

        Assert.Equal((byte)'t', bytes[0]);
    }

    [Fact]
    public async Task A_write_leaves_no_temporary_file_behind()
    {
        await _service.WriteAsync(Parse("crlf.txt"), "replaced", Token);

        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_directory_cannot_be_written_as_a_file()
    {
        var result = await _service.WriteAsync(Parse("src"), "text", Token);

        Assert.False(result.Success);
        Assert.Contains("is a directory", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overwriting_a_binary_file_is_refused()
    {
        var result = await _service.WriteAsync(Parse("data.bin"), "text", Token);

        Assert.False(result.Success);
        Assert.Contains("binary", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_replace_substitutes_the_one_occurrence()
    {
        var result = await _service.ReplaceAsync(Parse("src/Program.cs"), "class Program", "class Entry", cancellationToken: Token);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.Value!.Replacements);
        Assert.Equal("// Program\nclass Entry { }\n", await Raw("src/Program.cs"));
    }

    [Fact]
    public async Task A_replace_that_matches_nothing_says_to_copy_the_text_exactly()
    {
        var result = await _service.ReplaceAsync(Parse("src/Program.cs"), "class Missing", "x", cancellationToken: Token);

        Assert.False(result.Success);
        Assert.Contains("does not appear", result.Error, StringComparison.Ordinal);
        Assert.Contains("indentation", result.Error, StringComparison.Ordinal);
    }

    /// <remarks>
    /// An ambiguous edit is refused rather than applied to the first match. Landing in the wrong
    /// place costs far more than a retry with more surrounding context, and the model cannot tell
    /// the two outcomes apart afterwards.
    /// </remarks>
    [Fact]
    public async Task A_replace_that_matches_twice_is_refused_and_counts_the_matches()
    {
        var result = await _service.ReplaceAsync(Parse("twice.txt"), "alpha", "beta", cancellationToken: Token);

        Assert.False(result.Success);
        Assert.Contains("appears 2 times", result.Error, StringComparison.Ordinal);
        Assert.Equal("alpha\nalpha\n", await Raw("twice.txt"));
    }

    [Fact]
    public async Task A_replace_can_be_asked_for_every_occurrence()
    {
        var result = await _service.ReplaceAsync(Parse("twice.txt"), "alpha", "beta", replaceAll: true, Token);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.Value!.Replacements);
        Assert.Equal("beta\nbeta\n", await Raw("twice.txt"));
    }

    /// <remarks>
    /// Text a model copied out of one file and into a call against another still matches. Without
    /// this, editing a CRLF file with text quoted from a chat transcript never finds anything, and
    /// the model has no way to see why.
    /// </remarks>
    [Fact]
    public async Task A_replace_matches_across_differing_line_endings()
    {
        var result = await _service.ReplaceAsync(Parse("README.md"), "line one\nline two", "one line", cancellationToken: Token);

        Assert.True(result.Success, result.Error);
        Assert.Equal("# Project\r\none line\r\n", await Raw("README.md"));
    }

    [Fact]
    public async Task An_empty_search_text_is_refused()
    {
        var result = await _service.ReplaceAsync(Parse("README.md"), string.Empty, "x", cancellationToken: Token);

        Assert.False(result.Success);
        Assert.Contains("cannot be empty", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_search_finds_a_literal_match_and_numbers_the_line()
    {
        var result = await Search(new WorkspaceSearchQuery { Query = "class Program" });

        Assert.True(result.Success, result.Error);
        var match = Assert.Single(result.Value!.Matches);
        Assert.Equal("src/Program.cs", match.Path.Value);
        Assert.Equal(2, match.LineNumber);
        Assert.Equal("class Program { }", match.Line);
    }

    [Fact]
    public async Task A_search_ignores_case_unless_asked_not_to()
    {
        var insensitive = await Search(new WorkspaceSearchQuery { Query = "CLASS PROGRAM" });
        var sensitive = await Search(new WorkspaceSearchQuery { Query = "CLASS PROGRAM", MatchCase = true });

        Assert.NotEmpty(insensitive.Value!.Matches);
        Assert.Empty(sensitive.Value!.Matches);
    }

    [Fact]
    public async Task A_search_can_be_narrowed_to_a_file_pattern()
    {
        var result = await Search(new WorkspaceSearchQuery { Query = "line one", FilePattern = "*.cs" });

        Assert.Empty(result.Value!.Matches);
    }

    [Fact]
    public async Task A_search_can_be_narrowed_to_a_subtree()
    {
        var result = await Search(new WorkspaceSearchQuery { Query = "static class", Path = Parse("src/lib") });

        Assert.Single(result.Value!.Matches);
    }

    [Fact]
    public async Task A_search_accepts_a_regular_expression_when_it_is_asked_for()
    {
        var result = await Search(new WorkspaceSearchQuery { Query = @"class\s+\w+", IsRegex = true });

        Assert.NotEmpty(result.Value!.Matches);
    }

    [Fact]
    public async Task A_search_reports_a_regular_expression_it_cannot_parse()
    {
        var result = await Search(new WorkspaceSearchQuery { Query = "(unclosed", IsRegex = true });

        Assert.False(result.Success);
        Assert.Contains("not a valid regular expression", result.Error, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The ignore list is the difference between a search that answers in a moment and one that
    /// reads every dependency the project has ever installed.
    /// </remarks>
    [Fact]
    public async Task A_search_skips_dependencies_and_build_output()
    {
        var result = await Search(new WorkspaceSearchQuery { Query = "needleInModules" });

        Assert.Empty(result.Value!.Matches);
    }

    [Fact]
    public async Task A_search_never_returns_a_line_from_a_protected_file()
    {
        var result = await Search(new WorkspaceSearchQuery { Query = "API_KEY" });

        // .env holds the real one and .env.example holds the placeholder; only the second is here.
        var match = Assert.Single(result.Value!.Matches);
        Assert.Equal(".env.example", match.Path.Value);
    }

    [Fact]
    public async Task A_search_stops_at_the_cap_and_says_so()
    {
        _settings.With<AgentSettings>(agent => agent.MaxSearchResults = 1);

        var result = await Search(new WorkspaceSearchQuery { Query = "alpha" });

        Assert.Single(result.Value!.Matches);
        Assert.True(result.Value.IsTruncated);
    }

    [Fact]
    public async Task An_empty_search_is_refused()
    {
        var result = await Search(new WorkspaceSearchQuery { Query = string.Empty });

        Assert.False(result.Success);
        Assert.Contains("nothing to search for", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Creating_a_directory_creates_the_ones_above_it_too()
    {
        var result = await _service.CreateDirectoryAsync(Parse("a/b/c"), Token);

        Assert.True(result.Success, result.Error);
        Assert.True(result.Value!.IsDirectory);
        Assert.True(Directory.Exists(Path.Combine(_root, "a", "b", "c")));
    }

    [Fact]
    public async Task Deleting_a_file_removes_it()
    {
        var result = await _service.DeleteAsync(Parse("crlf.txt"), Token);

        Assert.True(result.Success, result.Error);
        Assert.False(File.Exists(Path.Combine(_root, "crlf.txt")));
    }

    /// <remarks>
    /// There is no recursive delete, so emptying a tree costs one call and one approval per file.
    /// That friction is the point: it is what stops a single confused step from clearing a repository.
    /// </remarks>
    [Fact]
    public async Task Deleting_a_folder_that_still_has_something_in_it_is_refused()
    {
        var result = await _service.DeleteAsync(Parse("src"), Token);

        Assert.False(result.Success);
        Assert.Contains("no recursive delete", result.Error, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, "src", "Program.cs")));
    }

    [Fact]
    public async Task Deleting_the_workspace_root_is_refused()
    {
        var result = await _service.DeleteAsync(WorkspacePath.Root, Token);

        Assert.False(result.Success);
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public async Task Moving_a_file_renames_it()
    {
        var result = await _service.MoveAsync(Parse("crlf.txt"), Parse("docs/renamed.txt"), Token);

        Assert.True(result.Success, result.Error);
        Assert.False(File.Exists(Path.Combine(_root, "crlf.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "docs", "renamed.txt")));
    }

    [Fact]
    public async Task Moving_onto_something_that_exists_is_refused()
    {
        var result = await _service.MoveAsync(Parse("crlf.txt"), Parse("lf.txt"), Token);

        Assert.False(result.Success);
        Assert.Contains("already exists", result.Error, StringComparison.Ordinal);
        Assert.Equal("one\ntwo\n", await Raw("lf.txt"));
    }

    [Fact]
    public async Task Moving_a_folder_inside_itself_is_refused()
    {
        var result = await _service.MoveAsync(Parse("src"), Parse("src/inner"), Token);

        Assert.False(result.Success);
        Assert.Contains("inside", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stat_describes_an_entry_without_reading_it()
    {
        var file = await _service.StatAsync(Parse("lf.txt"), Token);
        var directory = await _service.StatAsync(Parse("src"), Token);

        Assert.True(file.Success, file.Error);
        Assert.False(file.Value!.IsDirectory);
        Assert.Equal(8, file.Value.Size);

        Assert.True(directory.Success, directory.Error);
        Assert.True(directory.Value!.IsDirectory);
        Assert.Equal(0, directory.Value.Size);
    }

    [Fact]
    public async Task Stat_reports_a_missing_entry_as_missing()
    {
        var result = await _service.StatAsync(Parse("absent.txt"), Token);

        Assert.False(result.Success);
        Assert.Contains("does not exist", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_folder_can_be_resolved_to_the_absolute_path_a_child_process_needs()
    {
        // The one method here that hands an absolute path back, and it exists for exactly one caller:
        // a process has to be started somewhere real. It lives on this interface rather than next to
        // the process code because two path guards that can disagree are worse than one.
        var root = await _service.ResolveDirectoryAsync(WorkspacePath.Root, Token);
        var nested = await _service.ResolveDirectoryAsync(Parse("src"), Token);

        Assert.True(root.Success, root.Error);
        Assert.Equal(_root, root.Value, ignoreCase: true);

        Assert.True(nested.Success, nested.Error);
        Assert.Equal(Absolute("src"), nested.Value, ignoreCase: true);
    }

    [Fact]
    public async Task A_file_named_where_a_folder_belongs_is_told_apart_from_one_that_is_missing()
    {
        // Two different corrections. A model that named a file takes its parent; one that named nothing
        // has to go and look, and collapsing both into "does not exist" sends it looking for the file
        // it just successfully read.
        var file = await _service.ResolveDirectoryAsync(Parse("lf.txt"), Token);
        var absent = await _service.ResolveDirectoryAsync(Parse("nowhere"), Token);

        Assert.False(file.Success);
        Assert.Contains("is a file, not a folder", file.Error, StringComparison.Ordinal);

        Assert.False(absent.Success);
        Assert.Contains("does not exist", absent.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolving_a_folder_refuses_everything_the_rest_of_the_interface_refuses()
    {
        // The whole reason it is here. If this method had its own idea of what is contained, the
        // sandbox would have a second front door with a different lock on it.
        var guarded = await _service.ResolveDirectoryAsync(Parse(".git"), Token);

        Assert.False(guarded.Success);
        Assert.Contains("off limits", guarded.Error, StringComparison.Ordinal);

        await _service.CloseAsync(Token);
        var closed = await _service.ResolveDirectoryAsync(WorkspacePath.Root, Token);

        Assert.False(closed.Success);
        Assert.Contains("No folder is open", closed.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The project a test operates on: ordinary source, the line-ending and byte-order-mark cases,
    /// the names that are off limits, the names that only look like it, and the folders an agent is
    /// meant to walk past.
    /// </summary>
    /// <remarks>
    /// Written with the raw <see cref="File"/> API rather than through the service, so the bytes on
    /// disk are the ones the assertions are about and not ones the service chose.
    /// </remarks>
    private async Task SeedAsync()
    {
        await WriteRaw("README.md", "# Project\r\nline one\r\nline two\r\n");
        await WriteRaw("crlf.txt", "one\r\ntwo\r\n");
        await WriteRaw("lf.txt", "one\ntwo\n");
        await WriteRaw("twice.txt", "alpha\nalpha\n");
        await WriteRaw("src/Program.cs", "// Program\nclass Program { }\n");
        await WriteRaw("src/lib/util.cs", "// util\nstatic class Util { }\n");

        await WriteRaw(".gitignore", "bin/\nobj/\n.env\n");
        await WriteRaw(".env.example", "API_KEY=\n");
        await WriteRaw(".env", "API_KEY=sk-not-a-real-key-0123456789\n");
        await WriteRaw(".git/config", "[remote \"origin\"]\n");

        // Ignored by name, and both holding the same term: a search that returns it has walked into
        // a dependency tree or a build directory.
        await WriteRaw("node_modules/pkg/index.js", "// needleInModules\n");
        await WriteRaw("bin/output.txt", "needleInModules\n");

        await File.WriteAllTextAsync(
            Path.Combine(_root, "bom.txt"),
            "bom\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            Token);

        // A quarter of these bytes are NUL, which is what the binary sniff looks for.
        var binary = new byte[64];

        for (var i = 0; i < binary.Length; i++)
        {
            binary[i] = (byte)(i % 4 == 0 ? 0 : i + 1);
        }

        await File.WriteAllBytesAsync(Path.Combine(_root, "data.bin"), binary, Token);
    }

    private async Task WriteRaw(string relative, string content)
    {
        var full = Absolute(relative);

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        // The default encoding writes no byte-order mark, which is what every file here wants
        // except the one that is explicitly about having one.
        await File.WriteAllTextAsync(full, content, Token);
    }

    /// <summary>Lists a path and projects the entries down to their workspace-relative names.</summary>
    private async Task<string[]> Listing(WorkspacePath path, bool recursive = false)
    {
        var result = await _service.ListAsync(path, recursive, Token);

        Assert.True(result.Success, result.Error);

        return [.. result.Value!.Entries.Select(entry => entry.Path.Value)];
    }

    private Task<WorkspaceResult<WorkspaceSearchResult>> Search(WorkspaceSearchQuery query) =>
        _service.SearchAsync(query, Token);

    /// <summary>
    /// Reads a file straight off the disk, with nothing normalised.
    /// </summary>
    /// <remarks>
    /// Most of the write assertions are about which line endings ended up in the file, so reading it
    /// back through anything that quietly re-casts them would assert nothing at all.
    /// </remarks>
    private Task<string> Raw(string relative) => File.ReadAllTextAsync(Absolute(relative), Token);

    private string Absolute(string relative) =>
        Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static WorkspacePath Parse(string value) => WorkspacePath.Parse(value);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Creates a directory symbolic link, or reports that this machine will not allow one.
    /// </summary>
    /// <remarks>
    /// Creating a link on Windows needs Developer Mode or an elevated session, and a machine with
    /// neither is not a failing test. The two tests that need one cover the half of the containment
    /// rule that no string comparison can check, so they skip out loud rather than pass quietly.
    /// </remarks>
    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
