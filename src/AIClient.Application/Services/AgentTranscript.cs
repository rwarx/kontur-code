using System.Text.Json;
using AIClient.Domain.Models;

namespace AIClient.Application.Services;

/// <summary>
/// How a tool call is written into the stored transcript, and read back out of it.
/// </summary>
/// <remarks>
/// <para>
/// One place rather than two, because the writer and the reader have to agree exactly: the call
/// ids in this JSON are what a provider matches a tool result against, and a mismatch is not a
/// wrong answer but a 400 that ends the conversation. The agent loop writes it as a step finishes;
/// the context builder reads it on every later turn.
/// </para>
/// <para>
/// Reading is deliberately forgiving. This text is on disk, it was produced by an earlier version
/// of the app, and the only thing worse than losing a tool exchange is refusing to open the
/// conversation that contains it.
/// </para>
/// </remarks>
public static class AgentTranscript
{
    /// <summary>Web defaults so the stored shape is camelCase, which is what a person reading the row expects.</summary>
    private static readonly JsonSerializerOptions Format = new(JsonSerializerDefaults.Web);

    /// <summary>Renders the calls one assistant step made. Never called with an empty list.</summary>
    public static string Write(IReadOnlyList<AIToolCall> calls) => JsonSerializer.Serialize(calls, Format);

    /// <summary>
    /// Reads the calls back, dropping any that could not be answered.
    /// </summary>
    /// <remarks>
    /// A call with no id or no name is discarded rather than repaired. It cannot be paired with a
    /// result, so sending it would fail the whole request; dropping it costs the model one piece of
    /// its own history and leaves the conversation usable.
    /// </remarks>
    public static IReadOnlyList<AIToolCall> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var calls = JsonSerializer.Deserialize<List<AIToolCall>>(json, Format);

            if (calls is null)
            {
                return [];
            }

            return
            [
                .. calls
                    .Where(call => !string.IsNullOrEmpty(call.Id) && !string.IsNullOrEmpty(call.Name))
                    .Select(call => call with { ArgumentsJson = call.ArgumentsJson ?? string.Empty }),
            ];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
