using AIClient.Domain.Interfaces;

namespace AIClient.Server;

/// <summary>
/// Git panel endpoints: they expose the existing <see cref="IGitService"/> — the same
/// first-class git module the agent's tools use — over HTTP under <c>/api/git</c>. No shell,
/// no mocks: every call runs git through the process runner against the open workspace root.
/// </summary>
/// <remarks>
/// Read endpoints (status/diff/branches/history) always answer 200 with data; when the
/// workspace is not a repository the service returns empty results and <c>/repo</c> reports
/// it. Mutating endpoints answer 200 with the <see cref="GitResult"/> as-is — a rejected push
/// or an empty commit is a normal outcome the panel renders, not an HTTP error — so the client
/// reads <c>success</c>/<c>error</c> rather than catching a throw.
/// </remarks>
public static class GitEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/git").RequireAuthorization();

        // ----- reads -----
        g.MapGet("/repo", async (IGitService git, CancellationToken ct) =>
            Results.Ok(new { isRepository = await git.IsRepositoryAsync(ct).ConfigureAwait(false) }));

        g.MapGet("/status", async (IGitService git, CancellationToken ct) =>
            Results.Ok(await git.GetStatusAsync(ct).ConfigureAwait(false)));

        g.MapGet("/branch", async (IGitService git, CancellationToken ct) =>
            Results.Ok(await git.GetBranchInfoAsync(ct).ConfigureAwait(false)));

        g.MapGet("/branches", async (IGitService git, CancellationToken ct) =>
            Results.Ok(await git.GetBranchesAsync(ct).ConfigureAwait(false)));

        g.MapGet("/diff", async (IGitService git, CancellationToken ct) =>
            Results.Ok(await git.GetDiffAsync(ct).ConfigureAwait(false)));

        g.MapGet("/diff/file", async (string path, IGitService git, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(path)
                ? Results.BadRequest(new { error = "A file path is required." })
                : Results.Ok(await git.GetFileDiffAsync(path, ct).ConfigureAwait(false)));

        g.MapGet("/history", async (int? maxCount, IGitService git, CancellationToken ct) =>
            Results.Ok(await git.GetHistoryAsync(Math.Clamp(maxCount ?? 50, 1, 500), ct).ConfigureAwait(false)));

        g.MapGet("/commit/{sha}", async (string sha, IGitService git, CancellationToken ct) =>
            await git.GetCommitAsync(sha, ct).ConfigureAwait(false) is { } c ? Results.Ok(c) : Results.NotFound());

        // ----- staging & commit -----
        g.MapPost("/stage", async (GitPathsRequest? req, IGitService git, CancellationToken ct) =>
            Results.Ok(await git.StageAsync(req?.Paths, ct).ConfigureAwait(false)));

        g.MapPost("/unstage", async (GitPathsRequest? req, IGitService git, CancellationToken ct) =>
            Results.Ok(await git.UnstageAsync(req?.Paths, ct).ConfigureAwait(false)));

        g.MapPost("/commit", async (GitCommitRequest req, IGitService git, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(req.Message)
                ? Results.BadRequest(new { error = "A commit message is required." })
                : Results.Ok(await git.CommitAsync(req.Message, ct).ConfigureAwait(false)));

        // ----- branches -----
        g.MapPost("/branch/create", async (GitBranchRequest req, IGitService git, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(req.Name)
                ? Results.BadRequest(new { error = "A branch name is required." })
                : Results.Ok(await git.CreateBranchAsync(req.Name, ct).ConfigureAwait(false)));

        g.MapPost("/checkout", async (GitBranchRequest req, IGitService git, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(req.Name)
                ? Results.BadRequest(new { error = "A branch name is required." })
                : Results.Ok(await git.CheckoutAsync(req.Name, ct).ConfigureAwait(false)));

        // ----- remote -----
        g.MapPost("/push", async (GitPushRequest? req, IGitService git, CancellationToken ct) =>
            Results.Ok(await git.PushAsync(req?.Remote, req?.Branch, req?.SetUpstream ?? false, ct).ConfigureAwait(false)));

        g.MapPost("/pull", async (GitPullRequest? req, IGitService git, CancellationToken ct) =>
            Results.Ok(await git.PullAsync(req?.Remote, req?.Branch, ct).ConfigureAwait(false)));

        g.MapPost("/fetch", async (GitFetchRequest? req, IGitService git, CancellationToken ct) =>
            Results.Ok(await git.FetchAsync(req?.Remote, ct).ConfigureAwait(false)));
    }

    // ----- request shapes -----
    private sealed record GitPathsRequest(IReadOnlyList<string>? Paths);
    private sealed record GitCommitRequest(string Message);
    private sealed record GitBranchRequest(string Name);
    private sealed record GitPushRequest(string? Remote, string? Branch, bool? SetUpstream);
    private sealed record GitPullRequest(string? Remote, string? Branch);
    private sealed record GitFetchRequest(string? Remote);
}
