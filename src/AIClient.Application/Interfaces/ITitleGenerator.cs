namespace AIClient.Application.Interfaces;

/// <summary>
/// Derives a short chat title from the first user message.
/// </summary>
/// <remarks>
/// The MVP uses a local heuristic: no extra API call, no cost, no latency, and it works
/// offline. The interface exists so a model-generated title can be swapped in later
/// without touching the chat pipeline.
/// </remarks>
public interface ITitleGenerator
{
    /// <summary>Returns a title, or null when the text yields nothing useful.</summary>
    Task<string?> GenerateAsync(string firstUserMessage, CancellationToken cancellationToken = default);
}
