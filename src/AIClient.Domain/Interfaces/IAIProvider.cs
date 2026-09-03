using AIClient.Domain.Models;

namespace AIClient.Domain.Interfaces;

/// <summary>
/// The contract every AI backend implements. This is the seam that keeps the chat UI
/// ignorant of OpenRouter, NVIDIA, or anything added later.
/// </summary>
/// <remarks>
/// Implementations must be stateless with respect to a conversation and safe to call
/// concurrently: one instance is registered as a singleton and shared by all chats.
/// </remarks>
public interface IAIProvider
{
    /// <summary>Matches <see cref="Entities.Provider.Id"/>, e.g. <c>openrouter</c>.</summary>
    string Id { get; }

    /// <summary>Display name for Settings and the model picker.</summary>
    string DisplayName { get; }

    /// <summary>Fetches the catalogue. Throws <see cref="AIProviderException"/> on failure.</summary>
    Task<IReadOnlyList<AIModelDescriptor>> GetModelsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Streams a completion. The sequence must end with either
    /// <see cref="AIStreamEvent.Completed"/> or <see cref="AIStreamEvent.Error"/>.
    /// Cancelling the token must abort the underlying HTTP request, not just stop reading it.
    /// </summary>
    IAsyncEnumerable<AIStreamEvent> StreamChatAsync(AIChatRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Verifies the stored credentials with the cheapest call the provider offers.
    /// Returns a failure result rather than throwing, since "it failed" is the expected outcome here.
    /// </summary>
    Task<ProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken);
}

/// <summary>Outcome of <see cref="IAIProvider.TestConnectionAsync"/>.</summary>
/// <param name="Success">True when the credentials were accepted.</param>
/// <param name="Message">Sentence to show next to the status dot.</param>
/// <param name="ModelCount">Models visible to these credentials, when the probe could tell.</param>
/// <param name="TechnicalDetails">Diagnostics for the expandable section. Never contains the key.</param>
public sealed record ProviderTestResult(
    bool Success,
    string Message,
    int? ModelCount = null,
    string? TechnicalDetails = null);
