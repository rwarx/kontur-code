using System.Text;
using System.Text.RegularExpressions;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace AIClient.Infrastructure.Git;

/// <summary>
/// Git operations through the process runner. Every command runs git directly with an
/// argument list — no shell, no shell operators, no environment inheritance.
/// </summary>
public sealed class GitService : IGitService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly Regex DiffStatRegex = new(
        @"(\d+) files? changed(?:, (\d+) insertions?\(\+\))?(?:, (\d+) deletions?\(-\))?",
        RegexOptions.Compiled);

    private readonly IProcessRunner _runner;
    private readonly IWorkspaceService _workspace;
    private readonly ILogger<GitService> _logger;

    public GitService(IProcessRunner runner, IWorkspaceService workspace, ILogger<GitService> logger)
    {
        _runner = runner;
        _workspace = workspace;
        _logger = logger;
    }

    public async Task<bool> IsRepositoryAsync(CancellationToken cancellationToken)
    {
        if (!_workspace.IsOpen)
        {
            return false;
        }

        var result = await GitAsync(["rev-parse", "--git-dir"], cancellationToken).ConfigureAwait(false);
        return result.Success;
    }

    public async Task<GitStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var branch = await GetBranchNameAsync(cancellationToken).ConfigureAwait(false);
        var upstream = await GetUpstreamAsync(cancellationToken).ConfigureAwait(false);

        var result = await GitAsync(["status", "--porcelain=v1", "--branch"], cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            return new GitStatus { Branch = branch, IsClean = true };
        }

        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var files = new List<GitFileStatus>();

        // First line is the branch info: ## main...origin/main [ahead 1, behind 2]
        var ahead = 0;
        var behind = 0;
        if (lines.Length > 0 && lines[0].StartsWith("## "))
        {
            var tracking = lines[0][3..];
            if (tracking.Contains("ahead", StringComparison.Ordinal) &&
                Regex.Match(tracking, @"ahead (\d+)") is { Success: true } aheadMatch)
            {
                ahead = int.Parse(aheadMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }

            if (tracking.Contains("behind", StringComparison.Ordinal) &&
                Regex.Match(tracking, @"behind (\d+)") is { Success: true } behindMatch)
            {
                behind = int.Parse(behindMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length < 4)
            {
                continue;
            }

            var stagedChar = line[0];
            var unstagedChar = line[1];

            files.Add(new GitFileStatus
            {
                Path = line[3..],
                Staged = MapStatus(stagedChar),
                Unstaged = MapStatus(unstagedChar),
            });
        }

        return new GitStatus
        {
            Branch = branch,
            UpstreamBranch = upstream,
            IsClean = files.Count == 0,
            Files = files,
        };
    }

    public async Task<GitDiff> GetDiffAsync(CancellationToken cancellationToken)
    {
        // Check if anything is staged.
        var staged = await GitAsync(["diff", "--cached", "--stat"], cancellationToken).ConfigureAwait(false);

        if (staged.Success && !string.IsNullOrWhiteSpace(staged.Output))
        {
            return await ParseDiffAsync(["diff", "--cached"], cancellationToken).ConfigureAwait(false);
        }

        // No staged changes — show all working tree changes.
        return await ParseDiffAsync(["diff"], cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitDiff> GetDiffAsync(string fromRef, string toRef, CancellationToken cancellationToken)
    {
        return await ParseDiffAsync(["diff", "--stat", fromRef, toRef], cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitDiff> GetFileDiffAsync(string filePath, CancellationToken cancellationToken)
    {
        return await ParseDiffAsync(["diff", "--", filePath], cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GitCommit>> GetFileHistoryAsync(string filePath, int maxCount, CancellationToken cancellationToken)
    {
        var result = await GitAsync(
            ["log", $"--max-count={maxCount}", "--format=%H%n%h%n%s%n%an%n%aI%n%P", "--", filePath],
            cancellationToken).ConfigureAwait(false);

        return ParseCommits(result.Output);
    }

    public async Task<IReadOnlyList<GitCommit>> GetHistoryAsync(int maxCount, CancellationToken cancellationToken)
    {
        var result = await GitAsync(
            ["log", $"--max-count={maxCount}", "--format=%H%n%h%n%s%n%an%n%aI%n%P"],
            cancellationToken).ConfigureAwait(false);

        return ParseCommits(result.Output);
    }

    public async Task<GitBranchInfo> GetBranchInfoAsync(CancellationToken cancellationToken)
    {
        var branch = await GetBranchNameAsync(cancellationToken).ConfigureAwait(false);
        var upstream = await GetUpstreamAsync(cancellationToken).ConfigureAwait(false);

        var ahead = 0;
        var behind = 0;

        if (upstream is not null)
        {
            var result = await GitAsync(
                ["rev-list", "--left-right", "--count", $"{upstream}...HEAD"],
                cancellationToken).ConfigureAwait(false);

            if (result.Success)
            {
                var parts = result.Output.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    behind = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                    ahead = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                }
            }
        }

        return new GitBranchInfo
        {
            Name = branch,
            IsCurrent = true,
            TrackingBranch = upstream,
            Ahead = ahead,
            Behind = behind,
        };
    }

    public async Task<IReadOnlyList<GitBranchInfo>> GetBranchesAsync(CancellationToken cancellationToken)
    {
        var result = await GitAsync(
            ["branch", "--format=%(refname:short)|%(HEAD)|%(upstream:short)|%(objectname:short)"],
            cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return [];
        }

        var branches = new List<GitBranchInfo>();
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|', StringSplitOptions.None);
            if (parts.Length < 2)
            {
                continue;
            }

            branches.Add(new GitBranchInfo
            {
                Name = parts[0],
                IsCurrent = parts[1] == "*",
                TrackingBranch = parts.Length > 2 ? parts[2] : null,
            });
        }

        return branches;
    }

    public async Task<GitResult> CreateBranchAsync(string branchName, CancellationToken cancellationToken)
    {
        var result = await GitAsync(["checkout", "-b", branchName], cancellationToken).ConfigureAwait(false);
        return result.Success ? GitResult.Ok(result.Output) : GitResult.Fail(result.Error ?? result.Output);
    }

    public async Task<GitResult> CheckoutAsync(string branchName, CancellationToken cancellationToken)
    {
        var result = await GitAsync(["checkout", branchName], cancellationToken).ConfigureAwait(false);
        return result.Success ? GitResult.Ok(result.Output) : GitResult.Fail(result.Error ?? result.Output);
    }

    public async Task<GitResult> StageAsync(IReadOnlyList<string>? filePaths, CancellationToken cancellationToken)
    {
        var args = new List<string> { "add" };

        if (filePaths is { Count: > 0 })
        {
            args.AddRange(filePaths);
        }
        else
        {
            args.Add("-A");
        }

        var result = await GitAsync(args.ToArray(), cancellationToken).ConfigureAwait(false);
        return result.Success ? GitResult.Ok() : GitResult.Fail(result.Error ?? result.Output);
    }

    public async Task<GitResult> CommitAsync(string message, CancellationToken cancellationToken)
    {
        var result = await GitAsync(["commit", "-m", message], cancellationToken).ConfigureAwait(false);
        return result.Success ? GitResult.Ok(result.Output) : GitResult.Fail(result.Error ?? result.Output);
    }

    public async Task<GitResult> RevertLastAsync(CancellationToken cancellationToken)
    {
        var result = await GitAsync(["revert", "--no-commit", "HEAD"], cancellationToken).ConfigureAwait(false);
        return result.Success ? GitResult.Ok(result.Output) : GitResult.Fail(result.Error ?? result.Output);
    }

    public async Task<GitResult> RevertAsync(string commitSha, CancellationToken cancellationToken)
    {
        var result = await GitAsync(["revert", "--no-commit", commitSha], cancellationToken).ConfigureAwait(false);
        return result.Success ? GitResult.Ok(result.Output) : GitResult.Fail(result.Error ?? result.Output);
    }

    public async Task<GitCommit?> GetCommitAsync(string commitSha, CancellationToken cancellationToken)
    {
        var result = await GitAsync(
            ["show", $"--format=%H%n%h%n%s%n%an%n%aI%n%P", "--no-patch", commitSha],
            cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return null;
        }

        var commits = ParseCommits(result.Output);
        return commits.Count > 0 ? commits[0] : null;
    }

    private async Task<GitDiff> ParseDiffAsync(string[] args, CancellationToken cancellationToken)
    {
        var result = await GitAsync(args, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return new GitDiff();
        }

        var statResult = await GitAsync(
            [.. args, "--stat"], cancellationToken).ConfigureAwait(false);

        var files = new List<GitFileDiff>();

        if (statResult.Success)
        {
            foreach (var line in statResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // Format: " file.cs | 5 +++--" or " 2 files changed, 10 insertions(+), 5 deletions(-)"
                if (line.StartsWith(" ") && line.Contains('|'))
                {
                    var parts = line.Split('|', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        files.Add(new GitFileDiff
                        {
                            Path = parts[0].Trim(),
                            LinesAdded = 0,
                            LinesRemoved = 0,
                        });
                    }
                }
            }
        }

        return new GitDiff
        {
            RawDiff = result.Output,
            Files = files,
        };
    }

    private static IReadOnlyList<GitCommit> ParseCommits(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var commits = new List<GitCommit>();

        // Format: sha\nshortSha\nmessage\nauthor\ndate\nparents
        const int fieldsPerCommit = 6;

        for (var i = 0; i + fieldsPerCommit - 1 < lines.Length; i += fieldsPerCommit)
        {
            commits.Add(new GitCommit
            {
                Sha = lines[i],
                ShortSha = lines[i + 1],
                Message = lines[i + 2],
                Author = lines[i + 3],
                Date = DateTimeOffset.Parse(lines[i + 4], System.Globalization.CultureInfo.InvariantCulture),
                ParentShas = lines[i + 5].Split(' ', StringSplitOptions.RemoveEmptyEntries),
            });
        }

        return commits;
    }

    private async Task<string> GetBranchNameAsync(CancellationToken cancellationToken)
    {
        var result = await GitAsync(["branch", "--show-current"], cancellationToken).ConfigureAwait(false);
        return result.Success ? result.Output.Trim() : string.Empty;
    }

    private async Task<string?> GetUpstreamAsync(CancellationToken cancellationToken)
    {
        var result = await GitAsync(
            ["rev-parse", "--abbrev-ref", "@{upstream}"],
            cancellationToken).ConfigureAwait(false);
        return result.Success ? result.Output.Trim() : null;
    }

    private async Task<(bool Success, string Output, string? Error)> GitAsync(
        string[] args, CancellationToken cancellationToken)
    {
        if (!_workspace.IsOpen || _workspace.Root is null)
        {
            return (false, string.Empty, "No workspace is open.");
        }

        try
        {
            var result = await _runner.RunAsync(
                new ProcessRunRequest
                {
                    FileName = "git",
                    Arguments = args,
                    WorkingDirectory = _workspace.Root,
                    Timeout = CommandTimeout,
                    MaxOutputCharacters = 200_000,
                },
                cancellationToken).ConfigureAwait(false);

            if (result.TimedOut)
            {
                return (false, string.Empty, "The git command timed out.");
            }

            if (!result.Started)
            {
                return (false, string.Empty, "Git could not be started. Is it installed?");
            }

            var output = result.Output.Trim();
            var error = result.ExitCode != 0 ? output : null;

            return (result.ExitCode == 0, output, error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Git command failed: {Args}", string.Join(" ", args));
            return (false, string.Empty, $"Git command failed: {ex.Message}");
        }
    }

    private static GitFileState MapStatus(char c) => c switch
    {
        'A' => GitFileState.Added,
        'M' => GitFileState.Modified,
        'D' => GitFileState.Deleted,
        'R' => GitFileState.Renamed,
        'C' => GitFileState.Copied,
        '?' => GitFileState.Untracked,
        'U' => GitFileState.Conflicted,
        _ => GitFileState.None,
    };
}
