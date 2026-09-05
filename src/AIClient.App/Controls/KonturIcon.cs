using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AIClient.App.Controls;

/// <summary>
/// A semantic icon: <c>&lt;kontur:KonturIcon Kind="Canvas" /&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// The drawing itself lives in the resource dictionary as a frozen
/// <see cref="StreamGeometry"/> under <c>Icon.&lt;Kind&gt;</c>, and this control only finds
/// it and strokes it with <see cref="Foreground"/>. That split is what keeps the icon set a
/// design-system concern rather than a code concern: a designer retunes the drawings without
/// a rebuild's worth of C# churn, and every instance shares the one frozen geometry, so a
/// thousand icons on screen cost a thousand tiny draw calls and nothing else.
/// </para>
/// <para>
/// Colour is deliberately inherited rather than baked in - an icon means the same thing in a
/// nav row (secondary) and in a status strip (muted) and on an accent surface, so the caller
/// tints it. Size travels the same way: the default is 16, and the drawing scales uniformly
/// to whatever <see cref="Width"/>/<see cref="Height"/> the layout gives it, keeping stroke
/// weight proportional.
/// </para>
/// </remarks>
public class KonturIcon : Control
{
    /// <summary>Default stroke weight at the 16px design size; thinner reads faint, heavier reads loud.</summary>
    public const double DefaultGlyphThickness = 1.5;

    /// <summary>Standard rendered size; matches the design grid the drawings were made for.</summary>
    public const double DefaultSize = 16;

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(IconKind),
        typeof(KonturIcon),
        new FrameworkPropertyMetadata(IconKind.Node, OnKindChanged));

    public static readonly DependencyProperty GeometryProperty = DependencyProperty.Register(
        nameof(Geometry),
        typeof(Geometry),
        typeof(KonturIcon),
        new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty GlyphThicknessProperty = DependencyProperty.Register(
        nameof(GlyphThickness),
        typeof(double),
        typeof(KonturIcon),
        new FrameworkPropertyMetadata(DefaultGlyphThickness));

    static KonturIcon()
    {
        // The template lives with the rest of the design system in Controls.xaml; the
        // DefaultStyleKey lookup makes the control meaningless without it, which is the
        // point - an icon without the system's template is not an icon.
        DefaultStyleKeyProperty.OverrideMetadata(typeof(KonturIcon),
            new FrameworkPropertyMetadata(typeof(KonturIcon)));
    }

    /// <summary>Which drawing to show. Changing it re-resolves the geometry resource.</summary>
    public IconKind Kind
    {
        get => (IconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>The resolved drawing; null when the dictionary has no such icon yet, and the control then renders nothing rather than guessing a fallback glyph.</summary>
    public Geometry? Geometry
    {
        get => (Geometry?)GetValue(GeometryProperty);
        private set => SetValue(GeometryProperty, value);
    }

    /// <summary>Stroke weight in design units. The default suits almost everything; smaller rendered sizes sometimes want a nudge.</summary>
    public double GlyphThickness
    {
        get => (double)GetValue(GlyphThicknessProperty);
        set => SetValue(GlyphThicknessProperty, value);
    }

    /// <summary>
    /// Resolves the drawing after the control joins a tree (resources are only reachable
    /// then) and re-resolves on kind changes. An unknown kind leaves the icon blank - visibly
    /// unfinished, which is the correct signal, rather than silently wrong.
    /// </summary>
    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        ResolveGeometry();
    }

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((KonturIcon)d).ResolveGeometry();
    }

    private void ResolveGeometry()
    {
        Geometry = TryFindResource($"Icon.{Kind}") as Geometry;
    }
}
