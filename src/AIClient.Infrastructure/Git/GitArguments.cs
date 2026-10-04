using System.Text.RegularExpressions;

namespace AIClient.Infrastructure.Git;

/// <summary>
/// Checks the free-text arguments a caller hands to <c>git</c> before they reach the process.
/// </summary>
/// <remarks>
/// <para>
/// <c>git</c> is invoked with an argument list and no shell, which closes command injection through
/// <c>&amp;&amp;</c>, <c>|</c> and <c>;</c>. It does not close <em>argument</em> injection, and git is
/// unusually good at it: a remote name is not a name, it is a transport specifier. The documented
/// form <c>ext::sh -c '…'</c> tells git to run that string as a local program, so
/// <c>git fetch "ext::calc.exe"</c> executes a binary. The same shape exists for
/// <c>--upload-pack</c>, for the <c>file://</c> transport, and for any argument beginning with
/// <c>-</c>, which git reads as an option rather than as the thing you meant.
/// </para>
/// <para>
/// Nothing here is about what the user is allowed to do - they own the repository and the branches in
/// it are theirs. It is about making sure a <em>transport specifier</em> cannot arrive where a
/// <em>name</em> was expected. When the sidecar is reachable only by this application, that
/// distinction is the difference between a remote name and remote code execution.
/// </para>
/// </remarks>
public static partial class GitArguments
{
    /// <summary>
    /// A remote name: the conservative shape of what people actually call a remote.
    /// </summary>
    /// <remarks>
    /// <c>origin</c>, <c>upstream</c>, <c>fork-2</c>, <c>company_git</c>. Slashes are allowed because
    /// git permits them in a remote name and a handful of setups use them. The colon is not: a colon
    /// is what makes a remote a transport specifier (<c>ext::</c>, <c>ssh://</c>, <c>host:path</c>),
    /// and allowing one would allow the thing this whole class exists to refuse.
    /// </remarks>
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9._/-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex RemoteName();

    /// <summary>
    /// A branch name, per the shape <c>git check-ref-format --branch</c> accepts.
    /// </summary>
    /// <remarks>
    /// Deliberately a subset. A branch name that this accepts is one git will accept; a branch name
    /// git allows and this refuses is a name nobody types by hand, and refusing it is the cheap
    /// direction to be wrong in.
    /// </remarks>
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9._/-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex BranchName();

    /// <summary>
    /// Whether a remote name is safe to pass as a positional argument.
    /// </summary>
    /// <param name="remote">The name, or null to mean "whatever the repository already calls it".</param>
    /// <returns>null when the name is acceptable, or the sentence the caller reports.</returns>
    public static string? ValidateRemote(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote))
        {
            // Null and empty mean "the only remote" and "every remote" respectively to the commands
            // that take one. Neither is a value, so neither needs checking.
            return null;
        }

        if (remote.StartsWith("-", StringComparison.Ordinal))
        {
            return "A remote name cannot start with a dash: git would read it as an option.";
        }

        if (remote.Contains(':', StringComparison.Ordinal))
        {
            // ext::sh -c, ssh://host, C:\path, /absolute/path - all of them are a transport, and
            // ext:: is a command line.
            return "A remote name cannot contain a colon: that makes it a transport specifier rather "
                + "than a name, and git will run it. Use the name git already knows, for example origin.";
        }

        return RemoteName().IsMatch(remote)
            ? null
            : "That remote name contains characters git does not allow in a name. "
                + "Use the name git already knows, for example origin.";
    }

    /// <summary>Whether a branch name is safe to pass as a positional argument.</summary>
    /// <param name="branch">The branch name. Required - a branch is never inferred here.</param>
    /// <returns>null when the name is acceptable, or the sentence the caller reports.</returns>
    public static string? ValidateBranch(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            return null;
        }

        if (branch.StartsWith("-", StringComparison.Ordinal))
        {
            return "A branch name cannot start with a dash: git would read it as an option.";
        }

        // .., ~, ^, :, ?, *, [ and \ are all refspec syntax. A name carrying one of them is asking
        // git to do something other than switch to it.
        foreach (var reserved in (char[])['~', '^', ':', '?', '*', '[', '\\', '\0'])
        {
            if (branch.Contains(reserved))
            {
                return "A branch name cannot contain '~', '^', ':', '?', '*', '[' or a backslash: "
                    + "those are refspec characters, not name characters.";
            }
        }

        if (branch.Contains("..", StringComparison.Ordinal)
            || branch.Contains("//", StringComparison.Ordinal)
            || branch.EndsWith(".", StringComparison.Ordinal)
            || branch.EndsWith("/", StringComparison.Ordinal)
            || branch.EndsWith(".lock", StringComparison.Ordinal))
        {
            return "That is not a usable branch name: it ends with a separator or a dot, or contains "
                + "'..' or '//'.";
        }

        return BranchName().IsMatch(branch)
            ? null
            : "That branch name contains characters git does not allow in a name.";
    }

    /// <summary>
    /// Whether a commit-ish is safe to pass as a positional argument.
    /// </summary>
    /// <remarks>
    /// The loosest of the three, because a revision has genuinely more shapes than a branch does -
    /// <c>HEAD~2</c> and <c>HEAD@{1}</c> are ordinary things to ask for. It still refuses everything
    /// that turns a revision into an option or a second command, which is the whole of the risk.
    /// </remarks>
    public static string? ValidateRevision(string? revision)
    {
        if (string.IsNullOrWhiteSpace(revision))
        {
            return null;
        }

        if (revision.StartsWith("-", StringComparison.Ordinal))
        {
            return "A revision cannot start with a dash: git would read it as an option.";
        }

        // Whitespace is the giveaway for `ext::` spelled with a space, and for any attempt to smuggle
        // a second argument past a filter.
        if (revision.Any(char.IsWhiteSpace))
        {
            return "A revision cannot contain whitespace.";
        }

        if (revision.Contains("::", StringComparison.Ordinal))
        {
            return "A revision cannot contain '::'.";
        }

        return null;
    }
}