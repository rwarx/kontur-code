using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;

namespace AIClient.Application.Services;

/// <summary>
/// Which tools a mode offers, and what the model is told when it asks for one of the others anyway.
/// </summary>
/// <remarks>
/// <para>
/// One place, consulted twice. The registry reads it to decide what to put in the request, and the loop
/// reads it again when a call arrives, because the first is a courtesy and the second is the rule: a
/// provider can hand back a call for a tool that was never offered - from a cached turn, from a model
/// that remembers the last request, from a bug - and a mode that only filtered the offer would carry it
/// out.
/// </para>
/// <para>
/// The rule is expressed in terms of <see cref="AgentToolRisk"/> rather than tool names, so a tool added
/// next year is gated correctly by declaring what it costs. That is the same property that made the risk
/// levels worth having: a new tool cannot forget to be dangerous.
/// </para>
/// </remarks>
public static class AgentModePolicy
{
    /// <summary>Whether this mode offers this tool.</summary>
    public static bool Offers(AgentMode mode, IAgentTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        return mode.IsPlanning()
            ? tool.Risk == AgentToolRisk.Read
            : tool is not IAgentPlanningTool;
    }

    /// <summary>
    /// What to hand back for a call the mode does not allow.
    /// </summary>
    /// <remarks>
    /// A tool result rather than an exception, like every other refusal in the loop, and it says what to
    /// do instead. A model told only "not available in this mode" tries the next tool along the list
    /// until it finds one that writes; a model told to record the plan and stop, stops.
    /// </remarks>
    public static AgentToolResult Refuse(AgentMode mode, IAgentTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (mode.IsPlanning())
        {
            return AgentToolResult.Fail(
                $"'{tool.Name}' is not available while planning, and neither is anything else that changes "
                + "a file or runs a program. Nothing has been done. Finish the plan with submit_plan and "
                + "say what you would have done - the user switches to Build when they want it carried out.",
                $"{tool.Name}: not available in {mode.DisplayName()}");
        }

        return AgentToolResult.Fail(
            $"'{tool.Name}' belongs to the planning modes, and this is a build. Nothing has been recorded. "
            + "Make the change with the file tools instead, and say what you are doing as you go.",
            $"{tool.Name}: not available in {mode.DisplayName()}");
    }
}
