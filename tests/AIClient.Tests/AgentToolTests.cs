using System.Reflection;
using System.Text.Json;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Application.Services;
using AIClient.Application.Services.Tools;
using AIClient.Infrastructure;
using AIClient.Infrastructure.Configuration;
using AIClient.Infrastructure.Workspace;
using AIClient.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AIClient.Tests;

/// <summary>
/// The tools the model is given, exercised the way it calls them: a JSON argument object in, a
/// sentence out.
/// </summary>
/// <remarks>
/// <para>
/// Over the real workspace service and a real temporary folder, because a tool is a thin wrapper and
/// almost everything worth asserting about one is whether the wrapper and the thing it wraps agree.
/// A substituted workspace would let a tool claim it had written a file that no file system would
/// have accepted.
/// </para>
/// <para>
/// The refusals are asserted as carefully as the successes. Every one of them is read by a language
/// model as the entire account of what went wrong, and a refusal that does not say which argument
/// was at fault is a step of the budget spent on nothing.
/// </para>
/// </remarks>
public sealed class AgentToolTests : IAsyncLifetime
{
    private readonly StubSettingsService _settings = new();
    private readonly RecordingLogger<WorkspaceService> _logger = new();

    private string _scratch = null!;
    private string _root = null!;
    private WorkspaceService _workspace = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "aiclient-tools", Guid.CreateVersion7().ToString("n"));
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
    public async Task Listing_the_project_names_every_entry_and_marks_the_folders()
    {
        var text = await Ok(new ListFilesTool(_workspace), "{}");

        Assert.StartsWith("5 entries under '.':", text, StringComparison.Ordinal);
        Assert.Contains("src/", text, StringComparison.Ordinal);
        Assert.Contains("README.md  ", text, StringComparison.Ordinal);
        Assert.Contains("data.bin  ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_a_folder_with_nothing_in_it_says_so_rather_than_answering_with_nothing()
    {
        // An empty result and an empty folder look identical to a model that is only shown the
        // absence of lines, and it will conclude the listing failed and call something else.
        var text = await Ok(new ListFilesTool(_workspace), """{"path": "empty"}""");

        Assert.Equal("'empty' is empty.", text);
    }

    [Fact]
    public async Task Reading_a_file_hands_back_exactly_what_is_on_disk()
    {
        // No line numbers down the side, on purpose: edit_file matches literally, and text quoted
        // back from a numbered read produces an edit that can never match.
        var text = await Ok(new ReadFileTool(_workspace), """{"path": "README.md"}""");

        Assert.StartsWith("README.md, 3 lines:", text, StringComparison.Ordinal);
        Assert.EndsWith("# Project\nline one\nline two", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reading_part_of_a_file_says_which_part_it_is()
    {
        var text = await Ok(
            new ReadFileTool(_workspace),
            """{"path": "README.md", "start_line": 2, "line_count": 1}""");

        Assert.StartsWith("README.md, lines 2-2 of 3:", text, StringComparison.Ordinal);
        Assert.EndsWith("line one", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reading_something_that_is_not_text_is_refused()
    {
        var text = await Refused(new ReadFileTool(_workspace), """{"path": "data.bin"}""");

        Assert.Equal("'data.bin' contains binary data, so it cannot be read as text.", text);
    }

    [Fact]
    public async Task A_path_that_climbs_out_of_the_project_never_reaches_the_disk()
    {
        // The guard is in the path type, so it applies to every tool at once rather than to the
        // ones whose author remembered it. Asserted through a tool because that is where a model's
        // text actually arrives.
        var text = await Refused(new ReadFileTool(_workspace), """{"path": "../appdata/keys.dat"}""");

        Assert.Contains("'..' is not allowed", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Searching_says_where_each_match_is()
    {
        var text = await Ok(new SearchFilesTool(_workspace), """{"query": "alpha"}""");

        Assert.StartsWith("2 matches in ", text, StringComparison.Ordinal);
        Assert.Contains("twice.txt:1: alpha", text, StringComparison.Ordinal);
        Assert.Contains("twice.txt:2: alpha", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_search_that_finds_nothing_says_what_to_try_instead()
    {
        var text = await Ok(new SearchFilesTool(_workspace), """{"query": "nothing is written here"}""");

        Assert.StartsWith("No matches for 'nothing is written here'", text, StringComparison.Ordinal);
        Assert.Contains("is_regex", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writing_a_new_file_creates_the_folders_it_needs_and_says_it_is_new()
    {
        var text = await Ok(
            new WriteFileTool(_workspace),
            """{"path": "docs/notes.md", "content": "hello\n"}""");

        Assert.StartsWith("Created 'docs/notes.md' with 1 line", text, StringComparison.Ordinal);

        // A model writes bare LF whatever the platform, and a new file keeps what it was given
        // rather than acquiring the host's line endings.
        Assert.Equal("hello\n", await File.ReadAllTextAsync(Absolute("docs/notes.md"), Token));
    }

    [Fact]
    public async Task Writing_over_a_file_says_that_is_what_happened()
    {
        // The one outcome the user most needs to see in the transcript, because the previous
        // contents are gone and this line is the only sign of it.
        var text = await Ok(
            new WriteFileTool(_workspace),
            """{"path": "README.md", "content": "# Project\n"}""");

        Assert.StartsWith("Replaced the contents of 'README.md': 3 lines became 1 line", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_write_with_no_content_argument_is_refused_by_name()
    {
        var text = await Refused(new WriteFileTool(_workspace), """{"path": "docs/notes.md"}""");

        Assert.Equal("'content' is required.", text);
    }

    [Fact]
    public async Task Emptying_a_file_is_a_write_rather_than_a_missing_argument()
    {
        var text = await Ok(new WriteFileTool(_workspace), """{"path": "twice.txt", "content": ""}""");

        Assert.Contains("Replaced the contents of 'twice.txt'", text, StringComparison.Ordinal);
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(Absolute("twice.txt"), Token));
    }

    [Fact]
    public async Task An_edit_changes_the_one_place_it_named()
    {
        var text = await Ok(
            new EditFileTool(_workspace),
            """{"path": "README.md", "find": "line one", "replace": "line 1"}""");

        Assert.StartsWith("Replaced 1 occurrence in 'README.md'.", text, StringComparison.Ordinal);

        var after = await File.ReadAllTextAsync(Absolute("README.md"), Token);
        Assert.Equal("# Project\nline 1\nline two\n", after);
    }

    [Fact]
    public async Task An_edit_that_matches_twice_is_refused_rather_than_guessed_at()
    {
        // The failure being avoided is silent: an edit that lands on the wrong occurrence looks
        // exactly like one that landed on the right one, and the user finds out much later.
        var text = await Refused(
            new EditFileTool(_workspace),
            """{"path": "twice.txt", "find": "alpha", "replace": "beta"}""");

        Assert.Contains("appears 2 times", text, StringComparison.Ordinal);
        Assert.Equal("alpha\nalpha\n", await File.ReadAllTextAsync(Absolute("twice.txt"), Token));
    }

    [Fact]
    public async Task An_edit_may_change_every_occurrence_when_it_says_so()
    {
        var text = await Ok(
            new EditFileTool(_workspace),
            """{"path": "twice.txt", "find": "alpha", "replace": "beta", "replace_all": true}""");

        Assert.StartsWith("Replaced 2 occurrences in 'twice.txt'.", text, StringComparison.Ordinal);
        Assert.Equal("beta\nbeta\n", await File.ReadAllTextAsync(Absolute("twice.txt"), Token));
    }

    [Fact]
    public async Task An_edit_whose_text_is_not_in_the_file_is_told_how_to_recover()
    {
        var text = await Refused(
            new EditFileTool(_workspace),
            """{"path": "README.md", "find": "line three", "replace": "line 3"}""");

        Assert.Contains("does not appear in 'README.md'", text, StringComparison.Ordinal);
        Assert.Contains("copy the exact text", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_edit_that_would_change_nothing_is_refused_before_the_file_is_touched()
    {
        var text = await Refused(
            new EditFileTool(_workspace),
            """{"path": "README.md", "find": "line one", "replace": "line one"}""");

        Assert.Contains("would change nothing", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Creating_a_folder_makes_the_ones_above_it_too()
    {
        var text = await Ok(
            new CreateDirectoryTool(_workspace),
            """{"path": "src/Domain/Entities"}""");

        Assert.Equal("Created the folder 'src/Domain/Entities'.", text);
        Assert.True(Directory.Exists(Absolute("src/Domain/Entities")));
    }

    [Fact]
    public async Task Creating_a_folder_that_is_already_there_says_nothing_changed()
    {
        // Succeeding silently would leave the model believing it had just made an empty folder,
        // when what is there may be full of files it has not looked at.
        var text = await Ok(new CreateDirectoryTool(_workspace), """{"path": "src"}""");

        Assert.Equal("'src' already exists, so nothing was created.", text);
    }

    [Fact]
    public async Task Deleting_a_file_reports_what_was_removed()
    {
        // Measured before it goes, because afterwards there is nothing left to measure and this
        // line is the only record the user has of it.
        var text = await Ok(new DeleteFileTool(_workspace), """{"path": "twice.txt"}""");

        Assert.Equal("Deleted the file 'twice.txt' (12 B).", text);
        Assert.False(File.Exists(Absolute("twice.txt")));
    }

    [Fact]
    public async Task Deleting_a_folder_that_still_has_something_in_it_is_refused()
    {
        var text = await Refused(new DeleteFileTool(_workspace), """{"path": "src"}""");

        Assert.Contains("no recursive delete", text, StringComparison.Ordinal);
        Assert.True(File.Exists(Absolute("src/Program.cs")));
    }

    [Fact]
    public async Task A_move_inside_one_folder_is_reported_as_the_rename_it_is()
    {
        var text = await Ok(new MoveFileTool(_workspace), """{"from": "README.md", "to": "NOTES.md"}""");

        Assert.Equal("Renamed 'README.md' to 'NOTES.md'.", text);
        Assert.False(File.Exists(Absolute("README.md")));
        Assert.True(File.Exists(Absolute("NOTES.md")));
    }

    [Fact]
    public async Task A_move_somewhere_else_creates_the_folder_it_lands_in()
    {
        var text = await Ok(
            new MoveFileTool(_workspace),
            """{"from": "src/Program.cs", "to": "app/Program.cs"}""");

        Assert.Equal("Moved 'src/Program.cs' to 'app/Program.cs'.", text);
        Assert.True(File.Exists(Absolute("app/Program.cs")));
    }

    [Fact]
    public async Task A_move_onto_something_that_exists_is_refused()
    {
        // The refusal is the point: a rename with a stale destination is how a file is destroyed
        // by a step that looked like housekeeping.
        var text = await Refused(
            new MoveFileTool(_workspace),
            """{"from": "src/Program.cs", "to": "twice.txt"}""");

        Assert.Contains("already exists", text, StringComparison.Ordinal);
        Assert.True(File.Exists(Absolute("src/Program.cs")));
    }

    [Fact]
    public async Task A_move_onto_itself_is_named_for_what_it_is()
    {
        // Left to the workspace this reads as a collision with some other file, which sends the
        // model looking for one.
        var text = await Refused(
            new MoveFileTool(_workspace),
            """{"from": "src/Program.cs", "to": "./src/Program.cs"}""");

        Assert.Equal("'from' and 'to' are both 'src/Program.cs', so there is nothing to move.", text);
    }

    [Fact]
    public void The_registry_publishes_a_usable_definition_for_every_tool()
    {
        // The registry validates on construction, so most of this test is the constructor not
        // throwing: a schema that is not an object, a name a provider would reject, or two tools
        // answering to one name all fail here rather than in the middle of a live turn.
        var registry = new AgentToolRegistry(AllTools());

        Assert.NotEmpty(registry.Tools);
        Assert.Equal(registry.Tools.Count, registry.Definitions.Count);

        foreach (var definition in registry.Definitions)
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.Description), definition.Name);

            using var schema = JsonDocument.Parse(definition.ParametersJsonSchema);
            Assert.Equal(JsonValueKind.Object, schema.RootElement.ValueKind);
            Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
            Assert.True(schema.RootElement.TryGetProperty("properties", out _), definition.Name);
        }
    }

    [Fact]
    public void The_model_is_shown_the_tools_that_cannot_destroy_anything_first()
    {
        // Not cosmetic: a model scanning a tool list reaches for the first plausible entry, and the
        // first plausible entry should be one that only reads.
        var risks = new AgentToolRegistry(AllTools()).Tools.Select(tool => tool.Risk).ToArray();

        Assert.Equal(AgentToolRisk.Read, risks[0]);
        Assert.Equal(risks.OrderBy(risk => risk), risks);
    }

    [Fact]
    public void A_name_the_model_gets_slightly_wrong_still_finds_its_tool()
    {
        var registry = new AgentToolRegistry(AllTools());

        Assert.True(registry.TryGet(" Read_File ", out var tool));
        Assert.Equal("read_file", tool.Name);

        Assert.False(registry.TryGet("read", out _));
        Assert.False(registry.TryGet(null, out _));
    }

    [Fact]
    public void Every_tool_the_application_ships_is_wired_up_for_the_agent()
    {
        // The wiring is the whole of what the model can do, so it is checked against the assembly
        // rather than against a list written out here. A tool that exists and is never registered
        // is a capability nobody notices is missing; a tool registered twice is offered twice.
        var services = new ServiceCollection().AddInfrastructure(new ConfigurationBuilder().Build());

        var registered = services
            .Where(descriptor => descriptor.ServiceType == typeof(IAgentTool))
            .Select(descriptor => descriptor.ImplementationType!)
            .ToArray();

        Assert.Equal(registered, registered.Distinct());
        Assert.Equal(
            ToolTypes().OrderBy(type => type.Name, StringComparer.Ordinal),
            registered.OrderBy(type => type.Name, StringComparer.Ordinal));
    }

    private static Type[] ToolTypes() =>
        [.. typeof(IAgentTool).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false }
                && typeof(IAgentTool).IsAssignableFrom(type))];

    private IAgentTool[] AllTools() => [.. ToolTypes().Select(Activate)];

    /// <summary>
    /// Builds one tool, resolving what its constructor asks for.
    /// </summary>
    /// <remarks>
    /// Deliberately fails loudly on a dependency it has not been taught about, rather than skipping
    /// the tool: a tool this test cannot build is a tool whose schema is never validated.
    /// </remarks>
    private IAgentTool Activate(Type type)
    {
        var arguments = type.GetConstructors().Single().GetParameters().Select(
            parameter => parameter.ParameterType == typeof(IWorkspaceService)
                ? (object)_workspace
                : throw new InvalidOperationException(
                    $"{type.Name} takes a {parameter.ParameterType.Name}, which this test cannot supply."));

        return (IAgentTool)Activator.CreateInstance(type, [.. arguments])!;
    }

    /// <summary>Runs a tool the way a turn does, and asserts it answered.</summary>
    private async Task<string> Ok(IAgentTool tool, string arguments)
    {
        var result = await Run(tool, arguments);

        Assert.True(result.Success, result.Content);

        // Every result carries the one line the transcript shows, or the user sees a card with
        // nothing written on it.
        Assert.False(string.IsNullOrWhiteSpace(result.Summary));

        return result.Content;
    }

    /// <summary>Runs a tool and asserts it refused, returning the sentence the model is shown.</summary>
    private async Task<string> Refused(IAgentTool tool, string arguments)
    {
        var result = await Run(tool, arguments);

        // Content carries the reason on failure too: it is the whole of what the model gets back,
        // and an empty one is a step of the budget spent learning nothing.
        Assert.False(result.Success, result.Content);
        Assert.False(string.IsNullOrWhiteSpace(result.Content));

        return result.Content;
    }

    private static async Task<AgentToolResult> Run(IAgentTool tool, string arguments)
    {
        Assert.True(AgentToolArguments.TryParse(arguments, out var parsed, out var error), error);

        return await tool.ExecuteAsync(parsed, Token);
    }

    private string Absolute(string relative) =>
        Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private async Task SeedAsync()
    {
        await WriteRaw("README.md", "# Project\nline one\nline two\n");
        await WriteRaw("twice.txt", "alpha\nalpha\n");
        await WriteRaw("src/Program.cs", "// Program\nclass Program { }\n");
        await WriteRaw("src/lib/util.cs", "// util\nstatic class Util { }\n");

        Directory.CreateDirectory(Absolute("empty"));

        // A quarter of these bytes are NUL, which is what the binary sniff looks for.
        var binary = new byte[64];

        for (var i = 0; i < binary.Length; i++)
        {
            binary[i] = (byte)(i % 4 == 0 ? 0 : i + 1);
        }

        await File.WriteAllBytesAsync(Absolute("data.bin"), binary, Token);
    }

    private async Task WriteRaw(string relative, string content)
    {
        var full = Absolute(relative);

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content, Token);
    }
}
