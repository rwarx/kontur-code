using AIClient.Application.DTOs;
using AIClient.Application.Services;

namespace AIClient.Application.Interfaces;

/// <summary>
/// One thing the model can do, described well enough that it knows when to.
/// </summary>
/// <remarks>
/// <para>
/// A tool is the seam between a sentence in a language model's output and an effect on the user's
/// machine. Everything about this interface is shaped by that: the arguments arrive as JSON the model
/// invented, so they are validated rather than trusted; the result is text the model reads next, so a
/// refusal has to explain itself; and <see cref="Risk"/> is declared by the tool rather than inferred
/// by the caller, because the tool is the only thing that knows whether it writes.
/// </para>
/// <para>
/// Implementations do not enforce their own permission. The agent loop reads <see cref="Risk"/> and
/// gets the user's consent before <see cref="ExecuteAsync"/> is ever called, so that a tool cannot
/// forget to ask and every tool asks in the same voice.
/// </para>
/// </remarks>
public interface IAgentTool
{
    /// <summary>
    /// The name the model calls, in lower snake case.
    /// </summary>
    /// <remarks>
    /// Names read as verbs on a noun - <c>read_file</c>, not <c>file_reader</c> - because that is the
    /// convention every model has seen thousands of, and a name that reads as an action is one it
    /// reaches for correctly without being told to.
    /// </remarks>
    string Name { get; }

    /// <summary>
    /// When to use this tool, what it refuses, and how it differs from its neighbours. Prompt text.
    /// </summary>
    /// <remarks>
    /// This is the whole of what the model knows about the tool before calling it. Saying what a tool
    /// will not do is as valuable as saying what it does: a model that knows <c>edit_file</c> refuses
    /// an ambiguous match will send more surrounding context the first time instead of learning it
    /// from a failure.
    /// </remarks>
    string Description { get; }

    /// <summary>JSON Schema for the argument object. Must describe an object, even when empty.</summary>
    string ParametersJsonSchema { get; }

    /// <summary>What a wrong call costs, and therefore whether the user is asked first.</summary>
    AgentToolRisk Risk { get; }

    /// <summary>
    /// Runs the call, and reports the outcome as text rather than by throwing.
    /// </summary>
    /// <remarks>
    /// Bad arguments, a missing file, a refused path and a failed write are all ordinary outcomes here
    /// and all come back as <see cref="AgentToolResult.Success"/> false with a sentence saying why. An
    /// exception is reserved for a defect, and cancellation for the user pressing Stop.
    /// </remarks>
    Task<AgentToolResult> ExecuteAsync(
        AgentToolArguments arguments,
        CancellationToken cancellationToken = default);
}
