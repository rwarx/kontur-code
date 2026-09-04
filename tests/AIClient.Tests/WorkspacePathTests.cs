using AIClient.Domain.Workspace;

namespace AIClient.Tests;

/// <summary>
/// The path guard between a language model and the user's disk.
/// </summary>
/// <remarks>
/// <para>
/// Every file the agent touches is named by generated text, so this type is the whole of the
/// static half of the sandbox. It is pure, which is the point: the rules can be pinned by a
/// table instead of by a scenario, and a rule that regresses fails here rather than at the far
/// end of a tool call.
/// </para>
/// <para>
/// The rejection table below is written the way <c>SecureStorageTests</c> writes its key guard -
/// one row per way out of the sandbox, each named - because a guard with an untested branch is
/// a guard with a hole in it.
/// </para>
/// </remarks>
public sealed class WorkspacePathTests
{
    [Theory]
    // Nothing to validate.
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // Rooted: the path names a location instead of one inside the workspace.
    [InlineData("/etc/passwd")]
    [InlineData("\\Windows\\System32\\config")]
    [InlineData("\\\\server\\share\\secrets.txt")]
    [InlineData("//server/share/secrets.txt")]
    // Drive-qualified, both the rooted and the relative-to-drive forms.
    [InlineData("C:\\Windows\\System32")]
    [InlineData("c:/Windows")]
    [InlineData("C:notes.txt")]
    // Traversal, in every spelling. Refused rather than resolved.
    [InlineData("..")]
    [InlineData("../secrets.txt")]
    [InlineData("..\\secrets.txt")]
    [InlineData("src/../../etc/passwd")]
    [InlineData("src\\..\\..\\etc")]
    [InlineData("a/../b")]
    [InlineData("src/..")]
    [InlineData("...")]
    // Windows strips a trailing dot or space, so these are aliases of another file.
    [InlineData("notes.txt.")]
    [InlineData("dir /notes.txt")]
    [InlineData("dir./notes.txt")]
    // DOS devices, which resolve at any depth and with any extension.
    [InlineData("nul")]
    [InlineData("NUL")]
    [InlineData("nul.txt")]
    [InlineData("src/con")]
    [InlineData("COM1")]
    [InlineData("src/lpt9.log")]
    [InlineData("src/aux.cs")]
    [InlineData("conin$")]
    // Characters no file name may contain. ':' also opens an NTFS alternate data stream.
    [InlineData("notes.txt:hidden")]
    [InlineData("a<b.txt")]
    [InlineData("a>b.txt")]
    [InlineData("a\"b.txt")]
    [InlineData("a|b.txt")]
    [InlineData("a?b.txt")]
    [InlineData("a*b.txt")]
    [InlineData("a\tb.txt")]
    [InlineData("a\u0000b.txt")]
    [InlineData("\\\\?\\C:\\Windows")]
    public void A_path_that_could_reach_outside_the_workspace_is_refused(string? raw)
    {
        Assert.False(WorkspacePath.TryParse(raw, out var path, out var error));
        Assert.Null(path);

        // The refusal is handed back to the model as a tool result, so it has to say something.
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void A_path_longer_than_the_cap_is_refused()
    {
        Assert.False(WorkspacePath.TryParse(new string('a', WorkspacePath.MaxLength + 1), out _, out var error));
        Assert.Contains(WorkspacePath.MaxLength.ToString(), error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_nested_deeper_than_the_cap_is_refused()
    {
        var deep = string.Join('/', Enumerable.Repeat("d", WorkspacePath.MaxSegments + 1));

        Assert.False(WorkspacePath.TryParse(deep, out _, out var error));
        Assert.Contains(WorkspacePath.MaxSegments.ToString(), error, StringComparison.Ordinal);
    }

    [Theory]
    // The everyday cases.
    [InlineData("notes.txt", "notes.txt")]
    [InlineData("src/App.xaml.cs", "src/App.xaml.cs")]
    // Both separators arrive from models; one form comes back out.
    [InlineData("src\\App.xaml.cs", "src/App.xaml.cs")]
    [InlineData("src\\sub/deep\\file.cs", "src/sub/deep/file.cs")]
    // Noise that means nothing and is dropped rather than refused.
    [InlineData("./src/a.cs", "src/a.cs")]
    [InlineData(".\\src\\a.cs", "src/a.cs")]
    [InlineData("src//a.cs", "src/a.cs")]
    [InlineData("src/./a.cs", "src/a.cs")]
    [InlineData("src/", "src")]
    [InlineData("  src/a.cs  ", "src/a.cs")]
    // A leading-dot name reports its whole self as its extension, so its stem is empty and it
    // must not collide with the reserved-name check.
    [InlineData(".gitignore", ".gitignore")]
    [InlineData(".github/workflows/ci.yml", ".github/workflows/ci.yml")]
    // Reserved names are matched whole. A file that merely starts with one is an ordinary file.
    [InlineData("nulls.txt", "nulls.txt")]
    [InlineData("connection.cs", "connection.cs")]
    [InlineData("src/console.log", "src/console.log")]
    [InlineData("auxiliary/prnt.cs", "auxiliary/prnt.cs")]
    // Dots inside a name are ordinary, and so is a space.
    [InlineData("a.b.c.txt", "a.b.c.txt")]
    [InlineData("My Documents/read me.md", "My Documents/read me.md")]
    public void A_relative_path_is_accepted_in_one_canonical_form(string raw, string expected)
    {
        Assert.True(WorkspacePath.TryParse(raw, out var path, out var error));
        Assert.Null(error);
        Assert.Equal(expected, path.Value);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData(".\\")]
    public void The_root_is_named_by_a_single_dot(string raw)
    {
        Assert.True(WorkspacePath.TryParse(raw, out var path, out _));

        Assert.True(path.IsRoot);
        Assert.Equal(WorkspacePath.Root, path);
        Assert.Empty(path.Value);
        Assert.Empty(path.Segments);
    }

    [Fact]
    public void Nesting_up_to_the_cap_is_still_accepted()
    {
        var deep = string.Join('/', Enumerable.Repeat("d", WorkspacePath.MaxSegments));

        Assert.True(WorkspacePath.TryParse(deep, out var path, out _));
        Assert.Equal(WorkspacePath.MaxSegments, path.Segments.Count);
    }

    [Fact]
    public void Two_spellings_of_the_same_path_are_the_same_value()
    {
        // Equality is on the canonical form, which is what lets the agent notice it has already
        // read a file rather than reading it twice under two names.
        Assert.Equal(WorkspacePath.Parse("src/a.cs"), WorkspacePath.Parse(".\\src\\a.cs"));
        Assert.Equal(
            WorkspacePath.Parse("src/a.cs").GetHashCode(),
            WorkspacePath.Parse("src//a.cs").GetHashCode());
    }

    [Fact]
    public void A_path_knows_its_name_and_its_parent()
    {
        var nested = WorkspacePath.Parse("src/Views/MainWindow.xaml");

        Assert.Equal("MainWindow.xaml", nested.Name);
        Assert.Equal("src/Views", nested.Parent?.Value);
        Assert.Equal(["src", "Views", "MainWindow.xaml"], nested.Segments);

        // A top-level entry's parent is the root, and the root has none.
        Assert.Equal(WorkspacePath.Root, WorkspacePath.Parse("a.txt").Parent);
        Assert.Null(WorkspacePath.Root.Parent);
        Assert.Empty(WorkspacePath.Root.Name);
    }

    [Fact]
    public void A_child_name_is_validated_like_any_other_input()
    {
        var directory = WorkspacePath.Parse("src");

        Assert.True(directory.TryAppend("a.cs", out var child, out _));
        Assert.Equal("src/a.cs", child.Value);

        Assert.True(WorkspacePath.Root.TryAppend("a.cs", out var top, out _));
        Assert.Equal("a.cs", top.Value);

        // The names come off the disk during a walk, and a name we would refuse as input must
        // not be handed out as a path either.
        Assert.False(directory.TryAppend("..", out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.False(directory.TryAppend("nul", out _, out _));
    }

    [Fact]
    public void Resolving_against_a_root_uses_the_platform_separator()
    {
        var root = Path.Combine(Path.GetTempPath(), "ws");

        Assert.Equal(
            Path.Combine(root, "src", "a.cs"),
            WorkspacePath.Parse("src/a.cs").ResolveAgainst(root));

        // The root resolves to the root directory itself, with nothing appended.
        Assert.Equal(root, WorkspacePath.Root.ResolveAgainst(root));
    }

    [Fact]
    public void The_throwing_parse_names_the_rule_that_was_broken()
    {
        var error = Assert.Throws<ArgumentException>(() => WorkspacePath.Parse("../etc/passwd"));

        Assert.Contains("..", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_prints_as_the_model_would_write_it()
    {
        Assert.Equal("src/a.cs", WorkspacePath.Parse("src\\a.cs").ToString());
        Assert.Equal(".", WorkspacePath.Root.ToString());
    }
}
