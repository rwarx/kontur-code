using System.Text;
using AIClient.Application.Configuration;
using AIClient.Application.Interfaces;
using AIClient.Domain.Models;

namespace AIClient.Server;

/// <summary>
/// Stateless single-shot LLM completion under <c>/api/ai</c>. This is the seam behind the
/// editor's inline-AI edits (Cursor-style Ctrl/Cmd+K) and ghost-text autocompletion: the
/// caller passes a provider, a model and a prompt, and gets one string back — no conversation,
/// no database, no tools.
/// </summary>
/// <remarks>
/// Unlike <c>ChatService</c> this touches no persistence, so it is safe to call at typing speed.
/// It never receives or returns an API key: the provider resolves its own credential internally
/// by its id, exactly as the chat path does. A provider-side refusal (bad key, unsupported
/// region, rate limit) comes back as HTTP 502 with the provider's message so the editor can show
/// it inline rather than silently doing nothing.
/// </remarks>
public static class AiEndpoints
{
    /// <summary>
    /// Ceilings on a request this endpoint will spend money on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The endpoint is the cheapest way to turn a loopback socket into an open tab on someone's
    /// provider credit, and it was reachable by anything that could open a socket. The bearer token
    /// closes that. These bounds close the remaining shape of the problem: even a caller that is
    /// legitimately inside the application - a bug, a runaway retry loop, the ghost-text path firing
    /// on every keystroke - should not be able to ask for a million tokens at temperature 2 with a
    /// prompt the size of a novel.
    /// </para>
    /// <para>
    /// The output ceiling is deliberately generous rather than tied to
    /// <see cref="ChatSettings.ReservedOutputTokens"/>: that figure is the headroom a conversation
    /// turn keeps, and this endpoint answers an edit-in-place or a ghost-text suggestion, where the
    /// whole answer is a few lines. The conversation path still clamps against the model's own window.
    /// </para>
    /// </remarks>
    private const int MaxPromptCharacters = 200_000;
    private const int MaxSystemCharacters = 100_000;
    private const int MaxOutputTokens = 8_192;
    private const double DefaultTemperature = 1.0;

    public static void Map(WebApplication app)
    {
        // An endpoint that spends the user's provider credit is the last thing to leave anonymous.
        app.MapPost("/api/ai/complete", async (
            CompleteRequest req,
            IProviderRegistry registry,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.ProviderId))
            {
                return Results.BadRequest(new { error = "A provider id is required." });
            }

            if (string.IsNullOrWhiteSpace(req.ModelId))
            {
                return Results.BadRequest(new { error = "A model id is required." });
            }

            if (string.IsNullOrWhiteSpace(req.Prompt))
            {
                return Results.BadRequest(new { error = "A prompt is required." });
            }

            // Clamped rather than rejected. A ghost-text request that arrives with the whole visible
            // file as its prompt should get a shortened answer, not an error the editor can only
            // show as "something went wrong" — and a clamped request costs what the clamp says.
            if (req.Prompt.Length > MaxPromptCharacters)
            {
                req = req with { Prompt = req.Prompt[..MaxPromptCharacters] };
            }

            if (req.System is { } system && system.Length > MaxSystemCharacters)
            {
                req = req with { System = system[..MaxSystemCharacters] };
            }

            if (req.Temperature is { } temperature
                && (!double.IsFinite(temperature) || temperature is < 0 or > 2))
            {
                return Results.BadRequest(new { error = "Temperature must be a finite number between 0 and 2." });
            }

            if (req.MaxTokens is { } requested && (requested <= 0 || requested > MaxOutputTokens))
            {
                req = req with { MaxTokens = MaxOutputTokens };
            }

            var provider = registry.GetProvider(req.ProviderId);
            if (provider is null)
            {
                return Results.BadRequest(new { error = $"Unknown provider '{req.ProviderId}'." });
            }

            var messages = new List<AIChatMessage>();
            if (!string.IsNullOrWhiteSpace(req.System))
            {
                messages.Add(AIChatMessage.System(req.System));
            }

            messages.Add(AIChatMessage.User(req.Prompt));

            var request = new AIChatRequest
            {
                ModelId = req.ModelId,
                Messages = messages,
                Temperature = req.Temperature ?? DefaultTemperature,
                MaxTokens = req.MaxTokens ?? MaxOutputTokens,

                // Single-shot: accumulate the whole answer, then return it. The provider still
                // streams under the hood, which is why we fold the deltas here.
                Stream = false,
            };

            var text = new StringBuilder();
            string? finishReason = null;

            try
            {
                await foreach (var evt in provider.StreamChatAsync(request, ct).ConfigureAwait(false))
                {
                    switch (evt)
                    {
                        case AIStreamEvent.ContentDelta delta:
                            text.Append(delta.Text);
                            break;

                        case AIStreamEvent.Completed completed:
                            finishReason = completed.FinishReason;
                            break;

                        case AIStreamEvent.Error error:
                            loggerFactory.CreateLogger("AiComplete").LogWarning(
                                "Completion refused: {Kind} - {Message}", error.Kind, error.Message);
                            return Results.Json(new { error = error.Message }, statusCode: 502);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Ghost-text supersedes its own requests as the user keeps typing, so an aborted
                // completion is routine. The socket is already gone; there is nothing to send.
                return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("AiComplete").LogWarning(ex, "Completion failed.");
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }

            return Results.Ok(new { text = text.ToString(), finishReason });
        }).RequireAuthorization();
    }

    /// <summary>
    /// A completion request. <see cref="System"/> is optional; sampling fields are passed to the
    /// provider only when set. No key field: credentials never travel over this endpoint.
    /// </summary>
    private sealed record CompleteRequest(
        string ProviderId,
        string ModelId,
        string Prompt,
        string? System,
        double? Temperature,
        int? MaxTokens);
}
