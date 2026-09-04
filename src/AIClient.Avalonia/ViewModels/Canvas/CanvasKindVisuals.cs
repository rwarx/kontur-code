using System.Collections.Frozen;
using AIClient.Domain.Graph;

namespace AIClient.Avalonia.ViewModels.Canvas;

/// <summary>
/// Gives every node kind a glyph, a colour and a readable label.
/// </summary>
/// <remarks>
/// Ported from the WPF app with the brush made a hex string: the renderer resolves colours
/// through its own cache, and a string keeps this type free of any UI framework. The
/// palette is deliberately muted - a canvas of two hundred saturated cards is unreadable,
/// and the accent colour has to stay the loudest thing on screen. Unknown kinds fall
/// through to the neutral visual rather than throwing or drawing nothing.
/// </remarks>
public static class CanvasKindVisuals
{
    public sealed record Visual(string Glyph, string Colour);

    private const string Container = "■";  // ■ project, folder, component
    private const string Document = "□";   // □ file, documentation
    private const string Type = "◆";       // ◆ class, service, api
    private const string Idea = "◇";       // ◇ requirement, decision, knowledge
    private const string Run = "▶";        // ▶ agent, execution, artifact
    private const string Alert = "▲";      // ▲ error
    private const string Neutral = "●";    // ● anything unrecognised

    private static readonly FrozenDictionary<string, Visual> Table = new Dictionary<string, Visual>
    {
        ["project"] = new(Container, "#8FA3BF"),
        ["feature"] = new(Container, "#8FA3BF"),
        ["folder"] = new(Container, "#94A3B8"),
        ["component"] = new(Container, "#E0A458"),

        ["file"] = new(Document, "#7AA5D2"),
        ["documentation"] = new(Document, "#A0AEC0"),

        ["class"] = new(Type, "#C08AD8"),
        ["interface"] = new(Type, "#B78BD0"),
        ["method"] = new(Type, "#7FB89A"),
        ["service"] = new(Type, "#E0A458"),
        ["api"] = new(Type, "#E8B563"),
        ["database"] = new(Type, "#6FB3C4"),
        ["dependency"] = new(Type, "#8B93A5"),
        ["test"] = new(Type, "#88B37F"),

        ["requirement"] = new(Idea, "#9BB0D4"),
        ["decision"] = new(Idea, "#C9A227"),
        ["knowledge"] = new(Idea, "#A6ADBB"),
        ["task"] = new(Idea, "#9BB0D4"),

        ["agent"] = new(Run, "#6FB3C4"),
        ["execution"] = new(Run, "#6FB3C4"),
        ["artifact"] = new(Run, "#8AC4B0"),

        ["error"] = new(Alert, "#E06C75"),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly Visual Fallback = new(Neutral, "#8B93A5");

    public static Visual Of(GraphNodeKind kind) =>
        Table.TryGetValue(kind.Value, out var visual) ? visual : Fallback;

    /// <summary>The glyph drawn on the left of a node card.</summary>
    public static string GlyphOf(GraphNodeKind kind) => Of(kind).Glyph;

    /// <summary>The kind's colour, as <c>#RRGGBB</c>. Resolved to a brush by the renderer.</summary>
    public static string ColourOf(GraphNodeKind kind) => Of(kind).Colour;

    /// <summary>
    /// <c>depends_on</c> read as "Depends On". The wire form is snake_case because it is
    /// stored and sent to models; nobody should have to read it that way.
    /// </summary>
    public static string LabelOf(string? wireValue)
    {
        if (string.IsNullOrWhiteSpace(wireValue))
        {
            return "Unknown";
        }

        var parts = wireValue.Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return "Unknown";
        }

        return string.Join(' ', parts.Select(Capitalise));

        static string Capitalise(string word) =>
            word.Length == 1 ? char.ToUpperInvariant(word[0]).ToString()
                : char.ToUpperInvariant(word[0]) + word[1..];
    }
}
