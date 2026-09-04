using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AIClient.Application.Interfaces;
using AIClient.Domain.Models;

namespace AIClient.Application.Services;

/// <summary>
/// Collects the registered tools, checks them at startup, and offers them to the model.
/// </summary>
/// <remarks>
/// <para>
/// Every tool in the process arrives here by injection, so adding one is a single registration and
/// nothing else - no list to remember to update, and therefore no way to ship a tool that the model
/// is offered but the loop cannot resolve.
/// </para>
/// <para>
/// The checks in the constructor are all of defects rather than of runtime conditions, so they throw.
/// A duplicate name or a schema that is not an object would otherwise surface as a provider rejecting
/// a request mid-turn, which is a long way from the line that caused it; the test suite constructs the
/// real registry, so a mistake here fails a build instead.
/// </para>
/// </remarks>
public sealed class AgentToolRegistry : IAgentToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _byName;

    public AgentToolRegistry(IEnumerable<IAgentTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        // Ordered by risk and then by name, which is also the order the model sees. Reading tools
        // first is not cosmetic: a model scanning a tool list reaches for the first plausible entry,
        // and the first plausible entry should be one that cannot destroy anything.
        Tools = [.. tools.OrderBy(tool => tool.Risk).ThenBy(tool => tool.Name, StringComparer.Ordinal)];
        _byName = new Dictionary<string, IAgentTool>(Tools.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var tool in Tools)
        {
            Validate(tool);

            if (!_byName.TryAdd(tool.Name, tool))
            {
                throw new InvalidOperationException(
                    $"Two tools are registered as '{tool.Name}'. A tool name has to be unique: the model " +
                    "sends back a name, and there would be no way to tell which one it meant.");
            }
        }

        Definitions =
        [
            .. Tools.Select(tool => new AIToolDefinition
            {
                Name = tool.Name,
                Description = tool.Description,
                ParametersJsonSchema = tool.ParametersJsonSchema,
            }),
        ];
    }

    public IReadOnlyList<IAgentTool> Tools { get; }

    public IReadOnlyList<AIToolDefinition> Definitions { get; }

    public bool TryGet(string? name, [NotNullWhen(true)] out IAgentTool? tool)
    {
        tool = null;

        return !string.IsNullOrWhiteSpace(name) && _byName.TryGetValue(name.Trim(), out tool);
    }

    /// <summary>
    /// Rejects a tool that no provider would accept, or that a model would call by the wrong name.
    /// </summary>
    private static void Validate(IAgentTool tool)
    {
        if (!IsCallableName(tool.Name))
        {
            throw new InvalidOperationException(
                $"'{tool.Name}' is not a usable tool name. Use lower snake case, starting with a letter " +
                "and no longer than 64 characters, which is what the providers accept and what the " +
                "models are trained on.");
        }

        if (string.IsNullOrWhiteSpace(tool.Description))
        {
            throw new InvalidOperationException(
                $"'{tool.Name}' has no description. The description is the only thing telling the model " +
                "when to call it.");
        }

        JsonDocument schema;

        try
        {
            schema = JsonDocument.Parse(tool.ParametersJsonSchema);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"The parameter schema for '{tool.Name}' is not valid JSON: {ex.Message}", ex);
        }

        using (schema)
        {
            // Several providers reject a schema whose root is anything else, including the "no
            // arguments" case - which has to be an object with no properties rather than nothing.
            var isObject = schema.RootElement.ValueKind == JsonValueKind.Object
                && schema.RootElement.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.ValueEquals("object");

            if (!isObject)
            {
                throw new InvalidOperationException(
                    $"The parameter schema for '{tool.Name}' has to describe an object. A tool that takes " +
                    "no arguments still declares {\"type\":\"object\",\"properties\":{}}.");
            }
        }
    }

    private static bool IsCallableName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64 || !char.IsAsciiLetterLower(name[0]))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '_')
            {
                return false;
            }
        }

        return true;
    }
}
