using AIClient.Infrastructure.Git;

namespace AIClient.Tests;

/// <summary>
/// The guard that stands between a JSON body and <c>git</c>'s argument list.
/// </summary>
/// <remarks>
/// <para>
/// These cases are the ones that matter most in the whole suite, because the failure they prevent is
/// not a wrong answer on screen but code running as the user. <c>git fetch "ext::calc.exe"</c> is a
/// documented git feature — the <c>ext::</c> transport runs a local program — so a remote name taken
/// from a request body and passed straight to the process is remote code execution, and the request
/// that does it is one line of JSON.
/// </para>
/// <para>
/// Each refusal is asserted by its <em>reason</em> rather than merely its presence, because the
/// reason is what a maintainer reads when a legitimate remote name is rejected and the reason is
/// supposed to explain why.
/// </para>
/// </remarks>
public sealed class GitArgumentsTests
{
    // ----- remotes -----

    [Theory]
    [InlineData("origin")]
    [InlineData("upstream")]
    [InlineData("fork-2")]
    [InlineData("company_git")]
    [InlineData("a")]
    public void A_remote_name_that_is_just_a_name_is_accepted(string remote) =>
        Assert.Null(GitArguments.ValidateRemote(remote));

    [Theory]
    [InlineData("ext::calc.exe")]
    [InlineData("ext::sh -c whoami")]
    [InlineData("ext:sh -c id")]
    public void A_remote_that_is_a_command_is_refused(string remote)
    {
        var refusal = GitArguments.ValidateRemote(remote);

        Assert.NotNull(refusal);
        Assert.Contains("transport", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ssh://git@github.com/owner/repo.git")]
    [InlineData("git@github.com:owner/repo.git")]
    [InlineData("file:///c:/repo")]
    [InlineData("C:\\repos\\other")]
    [InlineData("/etc/passwd")]
    public void A_remote_that_is_a_url_or_a_path_is_refused(string remote)
    {
        // Every one of these is a legitimate thing to type into a git config by hand and an illegitimate
        // thing to accept from a request body: each is a transport specifier that moves data to or from
        // somewhere the user did not name in this request.
        Assert.NotNull(GitArguments.ValidateRemote(remote));
    }

    [Fact]
    public void A_remote_beginning_with_a_dash_is_refused()
    {
        // git reads this as an option, so `git fetch --upload-pack=…` is the other half of the same
        // attack and does not need the ext:: transport at all.
        var refusal = GitArguments.ValidateRemote("--upload-pack=calc.exe");

        Assert.NotNull(refusal);
        Assert.Contains("dash", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_absent_remote_is_not_a_value_and_is_left_to_git(string? remote) =>
        Assert.Null(GitArguments.ValidateRemote(remote));

    // ----- branches -----

    [Theory]
    [InlineData("main")]
    [InlineData("feature/new-canvas")]
    [InlineData("fix_42")]
    [InlineData("release-1.0")]
    public void A_branch_name_that_is_just_a_name_is_accepted(string branch) =>
        Assert.Null(GitArguments.ValidateBranch(branch));

    [Fact]
    public void A_branch_name_carrying_a_refspec_character_is_refused()
    {
        // `main:refs/heads/x` is refspec syntax, not a name: git reads the colon as "take this ref and
        // write it there", so a branch named that is a request to move a ref somewhere else.
        var refusal = GitArguments.ValidateBranch("main:refs/heads/x");

        Assert.NotNull(refusal);
        Assert.Contains("refspec", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_branch_name_spelling_a_range_is_refused()
    {
        // `a..b` is the range syntax, and a range is not a branch checkout can be handed.
        Assert.NotNull(GitArguments.ValidateBranch("main..HEAD"));
    }

    [Theory]
    [InlineData("--orphan")]
    [InlineData("-b")]
    public void A_branch_beginning_with_a_dash_is_refused(string branch) =>
        Assert.NotNull(GitArguments.ValidateBranch(branch));

    [Theory]
    [InlineData("main.lock")]
    [InlineData("main.")]
    [InlineData("a//b")]
    public void A_branch_name_git_would_not_accept_is_refused(string branch) =>
        Assert.NotNull(GitArguments.ValidateBranch(branch));

    [Fact]
    public void A_branch_containing_a_colon_is_refused()
    {
        // `main:refs/heads/x` is refspec syntax, not a name.
        Assert.NotNull(GitArguments.ValidateBranch("main:refs/heads/x"));
    }

    // ----- revisions -----

    [Theory]
    [InlineData("HEAD")]
    [InlineData("HEAD~2")]
    [InlineData("abc1234")]
    [InlineData("HEAD@{1}")] // braces are not whitespace and not a colon, so this stays legal
    public void A_revision_a_person_would_type_is_accepted(string revision) =>
        Assert.Null(GitArguments.ValidateRevision(revision));

    [Fact]
    public void A_revision_containing_whitespace_is_refused()
    {
        // Whitespace is how `ext:: sh -c …` gets a second word past something that only looked for
        // the transport syntax, and how a second argument gets smuggled past a filter.
        var refusal = GitArguments.ValidateRevision("HEAD --upload-pack=calc");

        Assert.NotNull(refusal);
        Assert.Contains("whitespace", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("--exec=calc")]
    [InlineData("-1")]
    public void A_revision_beginning_with_a_dash_is_refused(string revision) =>
        Assert.NotNull(GitArguments.ValidateRevision(revision));

    [Fact]
    public void A_revision_containing_a_double_colon_is_refused() =>
        Assert.NotNull(GitArguments.ValidateRevision("ext::id"));
}