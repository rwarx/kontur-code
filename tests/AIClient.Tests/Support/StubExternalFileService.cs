using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;

namespace AIClient.Tests.Support;

/// <summary>
/// Stands in for the out-of-project file service while a tool's own schema and refusal wording are
/// under test.
/// </summary>
/// <remarks>
/// <para>
/// Every member refuses. The tests that use this are checking what a tool publishes and what it says
/// when it cannot act, and a stub that quietly succeeded would let a broken tool pass: an external
/// tool that forgot to gate on the setting would look identical to a correct one.
/// </para>
/// <para>
/// Anything that genuinely exercises a read, a write or a replace belongs in
/// <c>ExternalFileServiceTests</c> against the real implementation over a real temporary tree, for the
/// same reason the workspace tests do not substitute this service either.
/// </para>
/// </remarks>
public sealed class StubExternalFileService : IExternalFileService
{
    public Task<WorkspaceResult<ExternalFile>> ReadAsync(
        string path,
        int startLine = 1,
        int? lineCount = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Fail<ExternalFile>($"stub cannot read {path}"));

    public Task<WorkspaceResult<ExternalWrite>> WriteAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Fail<ExternalWrite>($"stub cannot write {path}"));

    public Task<WorkspaceResult<ExternalWrite>> ReplaceAsync(
        string path,
        string find,
        string replace,
        bool replaceAll,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Fail<ExternalWrite>($"stub cannot edit {path}"));

    public Task<WorkspaceResult<ExternalListing>> ListAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Fail<ExternalListing>($"stub cannot list {path}"));

    private static WorkspaceResult<T> Fail<T>(string error)
        where T : class =>
        new(false, null, error);
}