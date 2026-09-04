using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AIClient.Application.DTOs;
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
    private readonly Dictionary<AgentMode, ModeOffer> _offers;
    private readonly bool _conditional;

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

        _conditional = Tools.Any(tool => tool is IAgentToolAvailability);
        _offers = BuildOffers(Tools, Definitions);
    }

    public IReadOnlyList<IAgentTool> Tools { get; }

    public IReadOnlyList<AIToolDefinition> Definitions { get; }

    public IReadOnlyList<AIToolDefinition> Available(AgentMode mode = AgentMode.Build)
    {
        var offer = _offers[mode];

        // The fast path is the only path most of the time, and it hands back a list built in the
        // constructor rather than a copy of it: a tool that is always available is the rule, and a step
        // of a run should not allocate a list to say so.
        if (!_conditional)
        {
            return offer.Definitions;
        }

        var available = new List<AIToolDefinition>(offer.Definitions.Count);

        for (var index = 0; index < offer.Tools.Count; index++)
        {
            if (offer.Tools[index] is not IAgentToolAvailability { IsAvailable: false })
            {
                available.Add(offer.Definitions[index]);
            }
        }

        // Indexes line up because both lists in an offer are built from Tools in one pass, and neither
        // is filtered afterwards. If that ever stops being true this loop silently offers the wrong
        // schemas, which is why the two are built together below rather than in separate passes.
        return available;
    }

    public bool TryGet(string? name, [NotNullWhen(true)] out IAgentTool? tool)
    {
        tool = null;

        return !string.IsNullOrWhiteSpace(name) && _byName.TryGetValue(name.Trim(), out tool);
    }

    /// <summary>
    /// Works out once, per mode, which tools that mode offers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every declared mode gets an entry, built by asking <see cref="AgentModePolicy"/> rather than by
    /// restating what it says. Two modes offering the same set - which Plan and Plan + canvas do, since
    /// they differ in what becomes of the plan and not in what may be called - costs two small lists at
    /// startup and buys the guarantee that a mode added later cannot quietly inherit Build's tools.
    /// </para>
    /// <para>
    /// Precomputed because the answer cannot change: a tool's risk and its interfaces are fixed at
    /// construction, so the only part of availability that varies at runtime is
    /// <see cref="IAgentToolAvailability"/>, which <see cref="Available"/> applies on top of this.
    /// </para>
    /// </remarks>
    private static Dictionary<AgentMode, ModeOffer> BuildOffers(
        IReadOnlyList<IAgentTool> tools,
        IReadOnlyList<AIToolDefinition> definitions)
    {
        var modes = Enum.GetValues<AgentMode>();
        var offers = new Dictionary<AgentMode, ModeOffer>(modes.Length);

        foreach (var mode in modes)
        {
            var offered = new List<IAgentTool>(tools.Count);
            var schemas = new List<AIToolDefinition>(tools.Count);

            for (var index = 0; index < tools.Count; index++)
            {
                if (!AgentModePolicy.Offers(mode, tools[index]))
                {
                    continue;
                }

                offered.Add(tools[index]);
                schemas.Add(definitions[index]);
            }

            offers[mode] = new ModeOffer(offered, schemas);
        }

        return offers;
    }

    /// <summary>The tools one mode offers, and their schemas in the same order.</summary>
    private readonly record struct ModeOffer(
        IReadOnlyList<IAgentTool> Tools,
        IReadOnlyList<AIToolDefinition> Definitions);

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
