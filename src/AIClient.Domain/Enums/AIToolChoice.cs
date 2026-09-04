namespace AIClient.Domain.Enums;

/// <summary>
/// How hard the model is pushed towards calling a tool. Maps to the OpenAI
/// <c>tool_choice</c> field, which is omitted entirely for <see cref="Auto"/>.
/// </summary>
public enum AIToolChoice
{
    /// <summary>The model decides. The only sane default, and what the field's absence means.</summary>
    Auto = 0,

    /// <summary>
    /// Tools are advertised but must not be called. Used for the final turn of an agent run,
    /// when the step budget is spent and the model has to answer with what it already knows.
    /// </summary>
    None,

    /// <summary>
    /// The model must call something. Not used by the agent loop - a model forced to call a
    /// tool it does not need invents arguments - but part of the protocol and cheap to carry.
    /// </summary>
    Required,
}
