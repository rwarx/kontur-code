using System.Text;
using AIClient.Application.DTOs;

namespace AIClient.Application.Services;

/// <summary>
/// Writes a plan out as Markdown, for the two readers that need it in that form.
/// </summary>
/// <remarks>
/// <para>
/// The transcript is one: a tool result is a persisted row, so rendering the plan into it is what makes
/// the plan survive the conversation being closed - there is no plan table and no migration behind this
/// feature, deliberately. The model is the other: it reads its own plan back on the next step and is
/// asked to put it to the user in prose, and Markdown is the shape it will keep.
/// </para>
/// <para>
/// Nothing is invented and nothing is omitted. An empty section is left out rather than printed with a
/// note, because a heading over nothing reads as something lost in transit.
/// </para>
/// </remarks>
public static class AgentPlanFormatter
{
    /// <summary>The whole plan, as Markdown.</summary>
    public static string Render(AgentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var text = new StringBuilder();

        text.Append("# ").AppendLine(plan.Title.Trim());

        if (!string.IsNullOrWhiteSpace(plan.Goal))
        {
            text.AppendLine().AppendLine(plan.Goal.Trim());
        }

        AppendSteps(text, plan.Steps);
        AppendParts(text, plan.Parts);
        AppendRisks(text, plan.Risks);

        return text.ToString().TrimEnd();
    }

    /// <summary>The one line the collapsed card in the transcript shows.</summary>
    public static string Summarise(AgentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var counts = new List<string>(2);

        if (plan.Steps.Count > 0)
        {
            counts.Add($"{plan.Steps.Count} step{Suffix(plan.Steps.Count)}");
        }

        if (plan.Parts.Count > 0)
        {
            counts.Add($"{plan.Parts.Count} part{Suffix(plan.Parts.Count)}");
        }

        var title = plan.Title.Trim();

        return counts.Count == 0 ? $"Plan: {title}" : $"Plan: {title} ({string.Join(", ", counts)})";
    }

    private static void AppendSteps(StringBuilder text, IReadOnlyList<AgentPlanStep> steps)
    {
        if (steps.Count == 0)
        {
            return;
        }

        text.AppendLine().AppendLine("## Steps").AppendLine();

        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];

            text.Append(index + 1).Append(". ").Append(step.Title.Trim());

            if (!string.IsNullOrWhiteSpace(step.Detail))
            {
                text.Append(" — ").Append(Flatten(step.Detail));
            }

            text.AppendLine();

            if (step.Paths.Count > 0)
            {
                // Indented under the numbered item so the list survives being rendered as Markdown,
                // where a paragraph at the left margin would end the list instead of continuing it.
                text.Append("   ").AppendLine(string.Join(", ", step.Paths.Select(path => $"`{path.Trim()}`")));
            }
        }
    }

    private static void AppendParts(StringBuilder text, IReadOnlyList<AgentPlanPart> parts)
    {
        if (parts.Count == 0)
        {
            return;
        }

        text.AppendLine().AppendLine("## Parts").AppendLine();

        foreach (var part in parts)
        {
            text.Append("- **").Append(part.Name.Trim()).Append("**");

            if (part.Kind != AgentPlanPartKind.Other)
            {
                text.Append(" (").Append(part.Kind.ToString().ToLowerInvariant()).Append(')');
            }

            if (!string.IsNullOrWhiteSpace(part.Path))
            {
                text.Append(" `").Append(part.Path.Trim()).Append('`');
            }

            if (!string.IsNullOrWhiteSpace(part.Purpose))
            {
                text.Append(" — ").Append(Flatten(part.Purpose));
            }

            text.AppendLine();

            if (part.DependsOn.Count > 0)
            {
                text.Append("  needs: ").AppendLine(string.Join(", ", part.DependsOn.Select(name => name.Trim())));
            }
        }
    }

    private static void AppendRisks(StringBuilder text, IReadOnlyList<string> risks)
    {
        if (risks.Count == 0)
        {
            return;
        }

        text.AppendLine().AppendLine("## Risks and open questions").AppendLine();

        foreach (var risk in risks)
        {
            text.Append("- ").AppendLine(Flatten(risk));
        }
    }

    /// <summary>
    /// One line, whatever arrived.
    /// </summary>
    /// <remarks>
    /// A newline inside a list item breaks the list, and a model asked for a short explanation will
    /// occasionally send three paragraphs. Collapsed rather than truncated: the text is worth keeping,
    /// its line breaks are not.
    /// </remarks>
    private static string Flatten(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Suffix(int count) => count == 1 ? string.Empty : "s";
}
