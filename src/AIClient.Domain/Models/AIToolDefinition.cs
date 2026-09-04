namespace AIClient.Domain.Models;

/// <summary>
/// One capability offered to the model: a name it can call, a sentence telling it when to,
/// and a JSON Schema describing the arguments.
/// </summary>
/// <remarks>
/// <para>
/// The schema is carried as a string of JSON rather than a typed C# model. JSON Schema is
/// what every provider that supports tool calling actually wants, in the same dialect, so a
/// C# schema model would be a translation layer with nothing on the far side of it. The
/// string is parsed once while the payload is built; a tool whose schema does not parse is a
/// defect the suite catches rather than something to discover against a live endpoint.
/// </para>
/// <para>
/// <see cref="Description"/> is prompt text, not documentation. It is the only thing standing
/// between the model and calling the wrong tool, so it says when to reach for this one and
/// what it will refuse - and the schema carries the same care in its per-property
/// descriptions.
/// </para>
/// </remarks>
public sealed record AIToolDefinition
{
    /// <summary>
    /// Stable identifier the model echoes back when it calls. Lower snake case by convention,
    /// because that is what the training data is full of.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>When to call this tool, and what it will not do. Written for the model.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// JSON Schema for the argument object. Must describe an object even when it takes no
    /// arguments - a bare <c>{"type":"object","properties":{}}</c> - because several providers
    /// reject anything else.
    /// </summary>
    public required string ParametersJsonSchema { get; init; }
}
