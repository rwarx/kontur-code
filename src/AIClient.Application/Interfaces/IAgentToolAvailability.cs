namespace AIClient.Application.Interfaces;

/// <summary>
/// Implemented by a tool that cannot always do anything, so that it is not offered when it cannot.
/// </summary>
/// <remarks>
/// <para>
/// Optional, like <see cref="IAgentToolPreview"/>, and for the same reason: almost every tool is always
/// available, and a property on <see cref="IAgentTool"/> would be eight implementations returning true so
/// that one could return false.
/// </para>
/// <para>
/// This is presentation, not enforcement. A tool still refuses a call it cannot carry out - the setting
/// behind it can change while a request is in flight, and a model can name a tool it was never offered.
/// What this saves is the step and the approval prompt spent finding that out: a model shown a tool
/// assumes it works, tries it, and then reasons about the refusal as though it were a result.
/// </para>
/// </remarks>
public interface IAgentToolAvailability
{
    /// <summary>Whether the tool can currently do anything at all.</summary>
    bool IsAvailable { get; }
}
