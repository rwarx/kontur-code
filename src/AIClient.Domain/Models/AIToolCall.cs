namespace AIClient.Domain.Models;

/// <summary>
/// The model's decision to call one tool, with the arguments it chose.
/// </summary>
/// <remarks>
/// <para>
/// <paramref name="ArgumentsJson"/> stays as text on purpose. The model produces characters,
/// not an object graph, and it produces malformed JSON often enough that parsing has to be a
/// step that can fail and be reported - back to the model, as a tool result it can correct on
/// the next turn - rather than an exception thrown while decoding the stream. Deserialising
/// here would turn a recoverable mistake into a dead turn.
/// </para>
/// <para>
/// <paramref name="Id"/> is provider-issued and is the only thing correlating a result with
/// the call that asked for it. A <c>tool</c> message sent back without the matching id is a
/// 400 from every provider, and one sent with the wrong id silently answers the wrong
/// question.
/// </para>
/// </remarks>
/// <param name="Id">Provider-issued correlation id, echoed on the <c>tool</c> result message.</param>
/// <param name="Name">Tool name, matching an <see cref="AIToolDefinition.Name"/> that was offered.</param>
/// <param name="ArgumentsJson">Raw JSON object text. May be empty, and may not parse.</param>
public sealed record AIToolCall(string Id, string Name, string ArgumentsJson);
