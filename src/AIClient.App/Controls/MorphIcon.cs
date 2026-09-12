using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace AIClient.App.Controls;

/// <summary>
/// An icon that morphs between two of the set's drawings instead of swapping.
/// </summary>
/// <remarks>
/// <para>
/// Inspired by the morphicons approach (morphicons.com): when two icons are congruent
/// under rotation, the transition is a rotation; when they are not, the outgoing drawing
/// still turns part-way toward the incoming one while the two crossfade, so the eye reads
/// one shape folding into another rather than two shapes swapping. The runs end with a
/// small overshoot - a spring settling - and they are interruptible: flipping the state
/// mid-flight retargets from wherever the transforms actually are, which is what makes a
/// toggle feel physical when clicked quickly.
/// </para>
/// <para>
/// Rotation pairs are declared, not solved. WPF cannot re-derive path correspondence the
/// way the browser library does at runtime, but the set only contains a handful of
/// genuinely congruent pairs (plus/close at 45°, chevrons, panel toggles at 180°,
/// sun/moon at 90°), and those are exactly the pairs a state toggle uses. Every other
/// pair gets the crossfade-with-turn fallback.
/// </para>
/// <para>
/// Colour and size inherit like <see cref="KonturIcon"/>: the caller tints via
/// <see cref="Control.Foreground"/> and sizes via Width/Height, and both drawings share
/// one stroke weight.
/// </para>
/// </remarks>
public class MorphIcon : Control
{
    private const double GlyphThickness = 2.4;
    private const double DefaultIconSize = 16;

    private static readonly Duration SpringIn = new(TimeSpan.FromMilliseconds(340));
    private static readonly Duration SpringOut = new(TimeSpan.FromMilliseconds(240));

    private readonly Path _slotA;
    private readonly Path _slotB;

    private Path _current;
    private Path _other;

    private IconKind _shown = IconKind.Node;
    private bool _templateReady;

    public static readonly DependencyProperty FirstProperty = DependencyProperty.Register(
        nameof(First),
        typeof(IconKind),
        typeof(MorphIcon),
        new FrameworkPropertyMetadata(IconKind.Node, OnPairChanged));

    public static readonly DependencyProperty SecondProperty = DependencyProperty.Register(
        nameof(Second),
        typeof(IconKind),
        typeof(MorphIcon),
        new FrameworkPropertyMetadata(IconKind.Node, OnPairChanged));

    public static readonly DependencyProperty IsSecondProperty = DependencyProperty.Register(
        nameof(IsSecond),
        typeof(bool),
        typeof(MorphIcon),
        new FrameworkPropertyMetadata(false, OnIsSecondChanged));

    static MorphIcon()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(MorphIcon),
            new FrameworkPropertyMetadata(typeof(MorphIcon)));
    }

    public MorphIcon()
    {
        SnapsToDevicePixels = true;
        Focusable = false;
        IsTabStop = false;

        _slotA = CreateSlot();
        _slotB = CreateSlot();

        _current = _slotA;
        _other = _slotB;

        var grid = new Grid();
        grid.Children.Add(_slotA);
        grid.Children.Add(_slotB);
        VisualChild = grid;

        // The override alone is not enough: without AddVisualChild the parent-child
        // connection is never established, and WPF renders nothing where the control
        // sits - the empty-button bug this fix closes.
        AddVisualChild(VisualChild);

        Loaded += (_, _) =>
        {
            _templateReady = true;
            ShowSteady(IsSecond ? Second : First);
        };
    }

    /// <summary>The icon shown while <see cref="IsSecond"/> is false.</summary>
    public IconKind First
    {
        get => (IconKind)GetValue(FirstProperty);
        set => SetValue(FirstProperty, value);
    }

    /// <summary>The icon shown while <see cref="IsSecond"/> is true.</summary>
    public IconKind Second
    {
        get => (IconKind)GetValue(SecondProperty);
        set => SetValue(SecondProperty, value);
    }

    /// <summary>Which of the pair is shown. Flipping it runs the morph.</summary>
    public bool IsSecond
    {
        get => (bool)GetValue(IsSecondProperty);
        set => SetValue(IsSecondProperty, value);
    }

    private UIElement VisualChild { get; }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => VisualChild;

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(
            double.IsNaN(Width) ? DefaultIconSize : Width,
            double.IsNaN(Height) ? DefaultIconSize : Height);

        _slotA.Measure(size);
        _slotB.Measure(size);

        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _slotA.Arrange(new Rect(finalSize));
        _slotB.Arrange(new Rect(finalSize));
        return finalSize;
    }

    private static Path CreateSlot()
    {
        var slot = new Path
        {
            StrokeThickness = GlyphThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.Uniform,
            Fill = Brushes.Transparent,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(0),
            IsHitTestVisible = false,
            Opacity = 0,
        };

        return slot;
    }

    private static void OnPairChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var icon = (MorphIcon)d;

        // A pair changed underneath a shown state: re-resolve whichever half is on
        // screen. Mid-morph this snaps the tail of the run, which is the honest outcome
        // of rewriting the pair while it plays.
        icon.ShowSteady(icon.IsSecond ? icon.Second : icon.First);
    }

    private static void OnIsSecondChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MorphIcon)d).TransitionTo((bool)e.NewValue);

    /// <summary>
    /// The rotation, in degrees, that carries the first icon onto the second's silhouette.
    /// Congruent pairs turn the whole way; everything else turns a quarter-turn, which is
    /// enough to sell the fold without pretending the shapes correspond.
    /// </summary>
    private static double RotationBetween(IconKind from, IconKind to) => (from, to) switch
    {
        (IconKind.Plus, IconKind.Close) or (IconKind.Close, IconKind.Plus) => 45,
        (IconKind.ChevronDown, IconKind.ChevronUp) or (IconKind.ChevronUp, IconKind.ChevronDown) => 180,
        (IconKind.ChevronLeft, IconKind.ChevronRight) or (IconKind.ChevronRight, IconKind.ChevronLeft) => 180,
        (IconKind.ChevronDown, IconKind.ChevronRight) or (IconKind.ChevronRight, IconKind.ChevronDown) => 90,
        (IconKind.ChevronDown, IconKind.ChevronLeft) or (IconKind.ChevronLeft, IconKind.ChevronDown) => 90,
        (IconKind.ChevronUp, IconKind.ChevronRight) or (IconKind.ChevronRight, IconKind.ChevronUp) => 90,
        (IconKind.ChevronUp, IconKind.ChevronLeft) or (IconKind.ChevronLeft, IconKind.ChevronUp) => 90,
        (IconKind.ExpandPanel, IconKind.CollapsePanel) or (IconKind.CollapsePanel, IconKind.ExpandPanel) => 180,
        (IconKind.Sun, IconKind.Moon) or (IconKind.Moon, IconKind.Sun) => 90,
        (IconKind.Eye, IconKind.EyeOff) or (IconKind.EyeOff, IconKind.Eye) => 0,
        (IconKind.Send, IconKind.Stop) or (IconKind.Stop, IconKind.Send) => 90,
        (IconKind.Pointer, IconKind.Hand) or (IconKind.Hand, IconKind.Pointer) => 30,
        _ => 90,
    };

    /// <summary>Puts one icon on screen with no transform, and empties the other slot.</summary>
    private void ShowSteady(IconKind kind)
    {
        // A held animation overrides every base value set below, so a pair rewritten
        // mid-morph or after one must first stop whatever is still holding a slot.
        StopAll();

        _shown = kind;

        _current.Data = TryFindResource($"Icon.{kind}") as Geometry;
        ((RotateTransform)_current.RenderTransform).Angle = 0;
        _current.Opacity = 1;

        _other.Data = null;
        ((RotateTransform)_other.RenderTransform).Angle = 0;
        _other.Opacity = 0;
    }

    private void TransitionTo(bool showSecond)
    {
        var target = showSecond ? Second : First;

        if (target == _shown)
        {
            return;
        }

        var outgoingKind = _shown;
        _shown = target;

        if (!_templateReady)
        {
            ShowSteady(target);
            return;
        }

        var angle = RotationBetween(outgoingKind, target);
        var ease = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };

        // Stop whatever the previous run left running: the From values below are read
        // after the stop, so a rapid double-click retargets from the mid-flight pose
        // rather than snapping back to where an earlier run began.
        StopAll();

        var outgoing = _current;
        var incoming = _other;

        incoming.Data = TryFindResource($"Icon.{target}") as Geometry;

        var outRotation = (RotateTransform)outgoing.RenderTransform;
        var inRotation = (RotateTransform)incoming.RenderTransform;

        outgoing.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, SpringOut));
        outRotation.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(outRotation.Angle, angle, SpringOut)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });

        incoming.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, SpringIn) { EasingFunction = ease });
        inRotation.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(angle - 90, 0, SpringIn) { EasingFunction = ease });

        // The roles swap for the next run: the icon now on screen is the one that will
        // turn away from it.
        (_current, _other) = (_other, _current);
    }

    private void StopAll()
    {
        foreach (var slot in new[] { _slotA, _slotB })
        {
            slot.BeginAnimation(OpacityProperty, null);
            ((RotateTransform)slot.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }
}
