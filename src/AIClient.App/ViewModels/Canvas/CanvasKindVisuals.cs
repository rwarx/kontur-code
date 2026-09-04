using System.Collections.Frozen;
using System.Windows.Media;
using AIClient.Domain.Graph;

namespace AIClient.App.ViewModels.Canvas;

/// <summary>
/// Gives every node kind a glyph, a colour and a readable label.
/// </summary>
/// <remarks>
/// <para>
/// Lives in App because it is presentation and nothing else: the graph knows that a node is a
/// <c>service</c>, and only the canvas has an opinion about what a service looks like. Putting
/// the table here also means a new kind needs no code at all - it falls through to the neutral
/// shape rather than throwing or drawing nothing.
/// </para>
/// <para>
/// Shapes are deliberately few. Six geometric glyphs read at a glance and stay legible when the
/// camera is zoomed out, where an icon set turns into grey mush; the finer distinction between
/// kinds is carried by colour, which survives being small. All of them are Geometric Shapes
/// characters, so they render in Segoe UI without a font fallback.
/// </para>
/// </remarks>
internal static class CanvasKindVisuals
{
    private const string Container = "■";  // ■ project, folder, component
    private const string Document = "□";   // □ file, documentation
    private const string Type = "◆";       // ◆ class, service, api
    private const string Idea = "◇";       // ◇ requirement, decision, knowledge
    private const string Run = "▶";        // ▶ agent, execution, artifact
    private const string Alert = "▲";      // ▲ error
    private const string Neutral = "●";    // ● anything unrecognised

    /// <summary>
    /// One entry per known kind. Muted tones on purpose - a canvas of two hundred saturated cards
    /// is unreadable, and the accent colour has to stay the loudest thing on screen.
    /// </summary>
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

    /// <summary>The glyph drawn on the left of a node card.</summary>
    public static string GlyphOf(GraphNodeKind kind) => Resolve(kind).Glyph;

    /// <summary>
    /// The kind's colour, frozen so it can be shared by every card of that kind without the
    /// binding engine copying a brush per node.
    /// </summary>
    public static Brush BrushOf(GraphNodeKind kind) => Resolve(kind).Brush;

    /// <summary>
    /// <c>depends_on</c> read as "Depends On". The wire form is snake_case because it is stored
    /// and sent to models; nobody should have to read it that way.
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

    private static Visual Resolve(GraphNodeKind kind) =>
        Table.TryGetValue(kind.Value, out var visual) ? visual : Fallback;

    private sealed class Visual
    {
        public Visual(string glyph, string colour)
        {
            Glyph = glyph;

            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colour));
            brush.Freeze();
            Brush = brush;
        }

        public string Glyph { get; }

        public Brush Brush { get; }
    }
}
