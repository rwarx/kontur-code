using AIClient.Application.Interfaces;
using AIClient.Application.Services;
using AIClient.Application.Services.Tools;
using AIClient.Infrastructure.Configuration;
using AIClient.Infrastructure.Workspace;
using AIClient.Tests.Support;

namespace AIClient.Tests;

/// <summary>
/// What the user is shown before they approve a change.
/// </summary>
/// <remarks>
/// <para>
/// The approval gate is the whole of section 28's safety position, and a gate is only as good as the
/// question it asks. "write_file wants to change Widget.cs" is not a question anyone can answer, so these
/// assert the two things that make it answerable: that a create is told apart from an overwrite, and that
/// the diff shown is the change that would actually happen.
/// </para>
/// <para>
/// Over the real workspace and a real folder, because a preview is a forecast about a file system and a
/// substituted one would forecast about nothing. The last test is the important one: describing a call
/// must leave the disk exactly as it was, or declining would be as dangerous as accepting.
/// </para>
/// </remarks>
public sealed class AgentToolPreviewTests : IAsyncLifetime
{
    private readonly StubSettingsService _settings = new();
    private readonly RecordingLogger<WorkspaceService> _logger = new();

    private string _scratch = null!;
    private string _root = null!;
    private WorkspaceService _workspace = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "aiclient-preview", Guid.CreateVersion7().ToString("n"));
        _root = Path.Combine(_scratch, "project");

        Directory.CreateDirectory(_root);
        await SeedAsync();

        _workspace = new WorkspaceService(_settings, new AppPaths(Path.Combine(_scratch, "appdata")), _logger);

        var opened = await _workspace.OpenAsync(_root, Token);
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
    public async Task A_file_that_does_not_exist_yet_is_previewed_as_a_creation()
    {
        var preview = await Describe(
            new WriteFileTool(_workspace),
            """
            {"path": "src/New.cs", "content": "namespace New;\nclass New { }\n"}
            """);

        Assert.Equal("Create src/New.cs (2 lines)", preview.Summary);
        Assert.Contains("@@ -0,0 +1,2 @@", preview.Preview, StringComparison.Ordinal);
        Assert.Contains("+namespace New;", preview.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overwriting_a_file_says_so_and_shows_what_would_be_lost()
    {
        var preview = await Describe(
            new WriteFileTool(_workspace),
            """
            {"path": "README.md", "content": "# Project\nline one\n"}
            """);

        Assert.Equal("Overwrite README.md (3 lines become 2 lines)", preview.Summary);
        Assert.Contains("-line two", preview.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_edit_is_previewed_as_the_change_it_would_make()
    {
        var preview = await Describe(
            new EditFileTool(_workspace),
            """
            {"path": "src/Program.cs", "find": "class Program { }", "replace": "class Program { int x; }"}
            """);

        Assert.Equal("Edit src/Program.cs (1 occurrence)", preview.Summary);
        Assert.Contains("-class Program { }", preview.Preview, StringComparison.Ordinal);
        Assert.Contains("+class Program { int x; }", preview.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_edit_that_would_match_nothing_says_so_before_the_user_answers()
    {
        var preview = await Describe(
            new EditFileTool(_workspace),
            """
            {"path": "README.md", "find": "not in there", "replace": "x"}
            """);

        Assert.Contains("not in the file", preview.Summary, StringComparison.Ordinal);
        Assert.Contains("refused", preview.Summary, StringComparison.Ordinal);
        Assert.Null(preview.Preview);
    }

    [Fact]
    public async Task An_ambiguous_edit_says_how_many_times_the_text_appears()
    {
        var preview = await Describe(
            new EditFileTool(_workspace),
            """
            {"path": "twice.txt", "find": "alpha", "replace": "beta"}
            """);

        Assert.Contains("appears 2 times", preview.Summary, StringComparison.Ordinal);
        Assert.Null(preview.Preview);
    }

    /// <summary>
    /// Text copied out of a read matches a file that is stored with CRLF.
    /// </summary>
    /// <remarks>
    /// The workspace levels line endings before it matches, so a preview that did not would announce that
    /// every edit to a Windows-saved file was about to be refused - and be wrong every time.
    /// </remarks>
    [Fact]
    public async Task An_edit_spanning_lines_matches_a_file_saved_with_windows_line_endings()
    {
        var preview = await Describe(
            new EditFileTool(_workspace),
            """
            {"path": "crlf.txt", "find": "one\ntwo", "replace": "one\nTWO"}
            """);

        Assert.Equal("Edit crlf.txt (1 occurrence)", preview.Summary);
        Assert.Contains("+TWO", preview.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_deletion_shows_the_file_that_is_about_to_go()
    {
        var preview = await Describe(
            new DeleteFileTool(_workspace),
            """
            {"path": "README.md"}
            """);

        Assert.StartsWith("Delete README.md (3 lines,", preview.Summary, StringComparison.Ordinal);
        Assert.Contains("-# Project", preview.Preview, StringComparison.Ordinal);
        Assert.Contains("-line two", preview.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_a_folder_with_things_in_it_is_flagged_as_a_refusal()
    {
        var full = await Describe(
            new DeleteFileTool(_workspace),
            """
            {"path": "src"}
            """);

        Assert.Contains("2 entries", full.Summary, StringComparison.Ordinal);
        Assert.Contains("refused", full.Summary, StringComparison.Ordinal);

        var empty = await Describe(
            new DeleteFileTool(_workspace),
            """
            {"path": "empty"}
            """);

        Assert.Equal("Delete the empty folder empty", empty.Summary);
    }

    [Fact]
    public async Task A_move_names_both_ends_and_has_no_diff_to_show()
    {
        var renamed = await Describe(
            new MoveFileTool(_workspace),
            """
            {"from": "src/Program.cs", "to": "src/Main.cs"}
            """);

        Assert.Equal("Rename src/Program.cs to Main.cs", renamed.Summary);
        Assert.Null(renamed.Preview);

        var moved = await Describe(
            new MoveFileTool(_workspace),
            """
            {"from": "src/Program.cs", "to": "src/lib/Program.cs"}
            """);

        Assert.Equal("Move src/Program.cs to src/lib/Program.cs", moved.Summary);
    }

    [Fact]
    public async Task A_move_onto_something_that_already_exists_is_flagged_as_a_refusal()
    {
        var preview = await Describe(
            new MoveFileTool(_workspace),
            """
            {"from": "src/Program.cs", "to": "README.md"}
            """);

        Assert.Contains("already there", preview.Summary, StringComparison.Ordinal);
        Assert.Contains("refused", preview.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Creating_a_folder_that_is_already_there_says_nothing_will_change()
    {
        var existing = await Describe(
            new CreateDirectoryTool(_workspace),
            """
            {"path": "empty"}
            """);

        Assert.Contains("nothing will change", existing.Summary, StringComparison.Ordinal);

        var wanted = await Describe(
            new CreateDirectoryTool(_workspace),
            """
            {"path": "src/deep/deeper"}
            """);

        Assert.Equal("Create the folder src/deep/deeper", wanted.Summary);
    }

    /// <summary>
    /// A call the tool is about to refuse for its arguments alone is described as nothing.
    /// </summary>
    /// <remarks>
    /// The refusal belongs to the execution, which says which argument was wrong in the words the model
    /// reads. A preview repeating it would put a complaint about JSON in front of a user who cannot do
    /// anything about it.
    /// </remarks>
    [Fact]
    public async Task Arguments_that_make_no_sense_are_described_as_nothing()
    {
        var preview = await Describe(new WriteFileTool(_workspace), "{}");

        Assert.Null(preview.Summary);
        Assert.Null(preview.Preview);
    }

    [Fact]
    public void A_tool_that_only_reads_has_no_preview_to_give()
    {
        Assert.IsNotAssignableFrom<IAgentToolPreview>(new ReadFileTool(_workspace));
        Assert.IsNotAssignableFrom<IAgentToolPreview>(new ListFilesTool(_workspace));
        Assert.IsNotAssignableFrom<IAgentToolPreview>(new SearchFilesTool(_workspace));
    }

    /// <summary>Describing a call must change nothing, or declining one would be as dangerous as allowing it.</summary>
    [Fact]
    public async Task Describing_a_call_leaves_the_project_exactly_as_it_was()
    {
        var before = Snapshot();

        await Describe(
            new WriteFileTool(_workspace),
            """
            {"path": "README.md", "content": "gone\n"}
            """);
        await Describe(
            new EditFileTool(_workspace),
            """
            {"path": "README.md", "find": "line one", "replace": "line uno"}
            """);
        await Describe(
            new DeleteFileTool(_workspace),
            """
            {"path": "README.md"}
            """);
        await Describe(
            new MoveFileTool(_workspace),
            """
            {"from": "README.md", "to": "docs/README.md"}
            """);
        await Describe(
            new CreateDirectoryTool(_workspace),
            """
            {"path": "docs"}
            """);

        Assert.Equal(before, Snapshot());
    }

    private static async Task<AgentToolPreview> Describe(IAgentTool tool, string arguments)
    {
        Assert.True(AgentToolArguments.TryParse(arguments, out var parsed, out var error), error);

        var describable = Assert.IsAssignableFrom<IAgentToolPreview>(tool);

        return await describable.DescribeAsync(parsed, Token);
    }

    /// <summary>Every entry under the root, with the two things a write would disturb.</summary>
    private string[] Snapshot() =>
        [.. Directory
            .GetFileSystemEntries(_root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => Directory.Exists(path)
                ? $"{path}|dir"
                : $"{path}|{new FileInfo(path).Length}|{File.GetLastWriteTimeUtc(path):O}")];

    private async Task SeedAsync()
    {
        await WriteRaw("README.md", "# Project\nline one\nline two\n");
        await WriteRaw("twice.txt", "alpha\nalpha\n");
        await WriteRaw("crlf.txt", "one\r\ntwo\r\nthree\r\n");
        await WriteRaw("src/Program.cs", "// Program\nclass Program { }\n");
        await WriteRaw("src/lib/util.cs", "// util\nstatic class Util { }\n");

        Directory.CreateDirectory(Absolute("empty"));
    }

    private string Absolute(string relative) =>
        Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private async Task WriteRaw(string relative, string content)
    {
        var full = Absolute(relative);

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content, Token);
    }
}
