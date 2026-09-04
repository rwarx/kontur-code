using AIClient.Domain.Graph;
using AIClient.Domain.Models;

namespace AIClient.Domain.Interfaces;

/// <summary>
/// Turns a stored conversation into the exact message list sent to a model.
/// </summary>
/// <remarks>
/// This is the extension point the roadmap depends on. Today it inlines the system
/// prompt, the history and any attachments, and trims to fit the context window.
/// Tomorrow the same seam is where project files, retrieved memory, tool definitions
/// and MCP resources get folded in - without the chat pipeline changing shape.
/// </remarks>
public interface IContextBuilder
{
    Task<ContextBuildResult> BuildAsync(ContextBuildRequest request, CancellationToken cancellationToken);
}

/// <summary>Input for <see cref="IContextBuilder.BuildAsync"/>.</summary>
public sealed record ContextBuildRequest
{
    public required Guid ConversationId { get; init; }

    /// <summary>System prompt, or null to omit the system turn entirely.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Context window of the target model, when known. The builder trims oldest-first
    /// to stay under it; null disables trimming.
    /// </summary>
    public int? ContextWindow { get; init; }

    /// <summary>Tokens to keep free for the answer, so a long history cannot squeeze the reply to nothing.</summary>
    public int ReservedOutputTokens { get; init; } = 1024;

    /// <summary>
    /// Stop before this message id, exclusive. Used by Regenerate and by Edit, which both
    /// need the history as it stood before a given turn.
    /// </summary>
    public Guid? UpToMessageId { get; init; }

    /// <summary>
    /// What the user had picked out on the Canvas when they asked, if anything.
    /// </summary>
    /// <remarks>
    /// Ids and a depth, never coordinates: see <see cref="GraphSelection"/>. Null is the
    /// ordinary case and leaves the built prompt exactly as it was before this property existed,
    /// which is what keeps a plain chat message unaffected by the graph existing at all.
    /// </remarks>
    public GraphSelection? Selection { get; init; }
}

/// <summary>Result of a context build, including what had to be dropped.</summary>
/// <param name="Messages">Wire-ready messages, system turn first.</param>
/// <param name="EstimatedTokens">Approximate prompt size. Heuristic, not a tokenizer.</param>
/// <param name="DroppedMessageCount">How many old turns were trimmed to fit.</param>
public sealed record ContextBuildResult(
    IReadOnlyList<AIChatMessage> Messages,
    int EstimatedTokens,
    int DroppedMessageCount);
