using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;

namespace AIClient.Application.Services.Tools;

/// <summary>
/// The one thing a planning run does: hand over the plan.
/// </summary>
/// <remarks>
/// <para>
/// A tool rather than a request to "answer with a plan", because the difference between the two is
/// structure. Prose can be read by a person and by nothing else; this arrives as steps and parts, which
/// can be drawn on a canvas, counted, checked off, or handed to a build run. The model still writes the
/// prose afterwards - it is asked to - but from something the application also understands.
/// </para>
/// <para>
/// <see cref="AgentToolRisk.Read"/>, which looks wrong for a tool that plainly records something. The
/// risk levels decide whether the user is asked first, and they are about the user's machine: this
/// writes nothing outside the application's own state, and putting an approval dialog in front of the
/// plan the user just asked for would be noise in the one place a run should be quiet.
/// </para>
/// </remarks>
public sealed class SubmitPlanTool : IAgentTool, IAgentPlanningTool
{
    /// <summary>
    /// Generous, and there to catch a model that has misunderstood the granularity rather than to
    /// discipline a long plan.
    /// </summary>
    private const int MaxSteps = 50;
    private const int MaxParts = 80;
    private const int MaxRisks = 25;

    private readonly IAgentPlanSink _sink;

    public SubmitPlanTool(IAgentPlanSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
    }

    public string Name => "submit_plan";

    public string Description =>
        "Records the plan you have worked out. This is the deliverable of the planning modes: call it once, "
        + "when you have read enough to plan honestly, with the whole plan in one call. 'steps' is the work "
        + "in the order it should happen. 'parts' is the shape of the finished thing - the folders, files, "
        + "services and tests it will consist of - and is what gets drawn, so name each part once and refer "
        + "to that same name in 'depends_on'. Nothing is created, changed or run by this call. After it "
        + "returns, write the plan out for the user in your own words and stop.";

    public string ParametersJsonSchema =>
        """
        {
          "type": "object",
          "properties": {
            "title": {
              "type": "string",
              "description": "One line naming the whole plan, as a heading."
            },
            "goal": {
              "type": "string",
              "description": "What the plan is for, in a sentence or two."
            },
            "steps": {
              "type": "array",
              "description": "The work, in the order it should happen. At least one.",
              "items": {
                "type": "object",
                "properties": {
                  "title": {
                    "type": "string",
                    "description": "What happens, as an instruction: 'Add the settings section'."
                  },
                  "detail": {
                    "type": "string",
                    "description": "Why or how, in one line - whatever the title left out."
                  },
                  "paths": {
                    "type": "array",
                    "description": "Files this step touches, relative to the project folder.",
                    "items": { "type": "string" }
                  }
                },
                "required": ["title"]
              }
            },
            "parts": {
              "type": "array",
              "description": "The pieces the finished project is made of. Required when the plan is to be drawn.",
              "items": {
                "type": "object",
                "properties": {
                  "name": {
                    "type": "string",
                    "description": "Short and unique. What 'depends_on' refers to elsewhere in this plan."
                  },
                  "kind": {
                    "type": "string",
                    "description": "Roughly what it is.",
                    "enum": ["folder", "file", "module", "service", "interface", "data", "view", "test", "external"]
                  },
                  "path": {
                    "type": "string",
                    "description": "Where it will live, when it is something with a path."
                  },
                  "purpose": {
                    "type": "string",
                    "description": "What it is for, in one line."
                  },
                  "depends_on": {
                    "type": "array",
                    "description": "Names of the parts this one needs, exactly as they are spelled above.",
                    "items": { "type": "string" }
                  }
                },
                "required": ["name"]
              }
            },
            "risks": {
              "type": "array",
              "description": "What could go wrong, and what the plan is guessing at. Say these rather than hide them.",
              "items": { "type": "string" }
            }
          },
          "required": ["title", "steps"]
        }
        """;

    public AgentToolRisk Risk => AgentToolRisk.Read;

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolArguments arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (!arguments.TryGetString("title", out var title, out var titleError))
        {
            return Fail(titleError);
        }

        if (!TryReadSteps(arguments, out var steps, out var stepError))
        {
            return Fail(stepError);
        }

        if (!TryReadParts(arguments, out var parts, out var partError))
        {
            return Fail(partError);
        }

        if (!arguments.TryGetStringArray("risks", out var risks, out var riskError))
        {
            return Fail(riskError);
        }

        var plan = new AgentPlan
        {
            Title = title,
            Goal = arguments.GetString("goal"),
            Steps = steps,
            Parts = parts,
            Risks = [.. risks.Where(risk => !string.IsNullOrWhiteSpace(risk)).Take(MaxRisks)],
        };

        var acceptance = await _sink.AcceptAsync(plan, cancellationToken).ConfigureAwait(false);

        return AgentToolResult.Ok(Report(plan, acceptance), AgentPlanFormatter.Summarise(plan));
    }

    /// <summary>
    /// What the model reads back, which is the plan itself.
    /// </summary>
    /// <remarks>
    /// Echoing a plan the model just wrote costs context, and buys two things worth more than it. The
    /// tool row is persisted, so the plan survives the conversation being reopened without a table of its
    /// own; and the model is about to put the plan to the user in prose, which it does from this rather
    /// than from memory of what it sent.
    /// </remarks>
    private static string Report(AgentPlan plan, AgentPlanAcceptance acceptance)
    {
        var recorded = acceptance.Drawn
            ? "The plan has been recorded and drawn."
            : "The plan has been recorded.";

        var note = string.IsNullOrWhiteSpace(acceptance.Note) ? string.Empty : $" {acceptance.Note.Trim()}";

        return $"""
            {recorded}{note} Nothing was created, changed or run.

            {AgentPlanFormatter.Render(plan)}

            Now tell the user the plan in your own words, and stop. Do not call submit_plan again.
            """;
    }

    private static bool TryReadSteps(
        AgentToolArguments arguments,
        out IReadOnlyList<AgentPlanStep> steps,
        out string? error)
    {
        steps = [];

        if (!arguments.TryGetObjectArray("steps", out var raw, out error))
        {
            return false;
        }

        if (raw.Count == 0)
        {
            error = "'steps' is required and has to hold at least one step. A plan with no steps in it is "
                + "not a plan - say what would happen first.";

            return false;
        }

        if (raw.Count > MaxSteps)
        {
            error = $"That is {raw.Count} steps, which is more than a plan can usefully hold. Group them into "
                + $"no more than {MaxSteps} and put the finer detail in each step's 'detail'.";

            return false;
        }

        var read = new List<AgentPlanStep>(raw.Count);

        foreach (var item in raw)
        {
            if (!item.TryGetString("title", out var title, out var titleError))
            {
                error = $"Every step needs a 'title': {titleError}";
                return false;
            }

            if (!item.TryGetStringArray("paths", out var paths, out var pathError))
            {
                error = $"A step's 'paths' is wrong: {pathError}";
                return false;
            }

            read.Add(new AgentPlanStep
            {
                Title = title,
                Detail = item.GetString("detail"),
                Paths = [.. paths.Where(path => !string.IsNullOrWhiteSpace(path))],
            });
        }

        steps = read;
        return true;
    }

    private static bool TryReadParts(
        AgentToolArguments arguments,
        out IReadOnlyList<AgentPlanPart> parts,
        out string? error)
    {
        parts = [];

        if (!arguments.TryGetObjectArray("parts", out var raw, out error))
        {
            return false;
        }

        if (raw.Count > MaxParts)
        {
            error = $"That is {raw.Count} parts, which is more than can be drawn or read. Keep the ones that "
                + $"matter to the shape of the project - no more than {MaxParts} - and leave the rest to the steps.";

            return false;
        }

        var read = new List<AgentPlanPart>(raw.Count);

        foreach (var item in raw)
        {
            if (!item.TryGetString("name", out var name, out var nameError))
            {
                error = $"Every part needs a 'name': {nameError}";
                return false;
            }

            if (!item.TryGetStringArray("depends_on", out var dependencies, out var dependencyError))
            {
                error = $"A part's 'depends_on' is wrong: {dependencyError}";
                return false;
            }

            read.Add(new AgentPlanPart
            {
                Name = name,
                Kind = AgentPlanPartKinds.Parse(item.GetString("kind")),
                Path = item.GetString("path"),
                Purpose = item.GetString("purpose"),

                // A part that depends on itself is a slip of the pen rather than a statement, and drawing
                // the loop it makes would be the most visible thing on the canvas.
                DependsOn =
                [
                    .. dependencies
                        .Where(dependency => !string.IsNullOrWhiteSpace(dependency))
                        .Where(dependency => !string.Equals(dependency.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)),
                ],
            });
        }

        parts = read;
        return true;
    }

    private AgentToolResult Fail(string? error) =>
        AgentToolResult.Fail(error ?? "The plan could not be read.", $"{Name}: rejected");
}
