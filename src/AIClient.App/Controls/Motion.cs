using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AIClient.App.Controls;

/// <summary>
/// The product's motion vocabulary, as attached properties.
/// </summary>
/// <remarks>
/// <para>
/// WPF has no implicit animations: a state change lands on the next frame and nothing
/// in between. Scattering Storyboards through every template to fix that is how a
/// codebase ends up with eleven different easing curves and no idea which surfaces
/// move at all. This class is the one place that knows how the product moves, and the
/// attached properties are the one line a view pays to use it:
/// <c>motion:Motion.Show="Rise"</c>.
/// </para>
/// <para>
/// Motion is spent only where attention is meant to move: an overlay announcing
/// itself, a panel arriving, an activity indicator being alive. Content the user is
/// reading never animates, and every run here is short (90-260ms) and decelerating -
/// the interface ends still, it does not bounce.
/// </para>
/// <para>
/// The system animation preference (<see cref="SystemParameters.ClientAreaAnimation"/>,
/// off when Windows is set to reduce motion or on low-power plans) is honoured by
/// every behaviour here: when it is off, states still change instantly - motion is a
/// clarification, never a requirement.
/// </para>
/// </remarks>
public static class Motion
{
    // -------------------------------------------------------------- Show

    /// <summary>How an element announces itself when it becomes visible.</summary>
    public enum Entrance
    {
        /// <summary>No entrance: the element is simply there. The default for content.</summary>
        None,

        /// <summary>A quiet 150ms fade. For surfaces appearing inside established layout.</summary>
        Fade,

        /// <summary>A fade plus an 9px rise. For cards and panels that arrive as a unit.</summary>
        Rise,

        /// <summary>A fade plus a 2% settle from slightly large. For modal overlays.</summary>
        Settle,
    }

    public static readonly DependencyProperty ShowProperty = DependencyProperty.RegisterAttached(
        "Show",
        typeof(Entrance),
        typeof(Motion),
        new FrameworkPropertyMetadata(Entrance.None, OnShowChanged));

    /// <summary>Set how the element animates in each time it becomes visible.</summary>
    public static void SetShow(DependencyObject element, Entrance value) => element.SetValue(ShowProperty, value);

    public static Entrance GetShow(DependencyObject element) => (Entrance)element.GetValue(ShowProperty);

    private static void OnShowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        // IsVisibleChanged survives retargeting of the property; the hook is registered
        // once and re-fires on every show, which is exactly the lifetime an entrance has.
        element.IsVisibleChanged -= OnElementVisible;
        if ((Entrance)e.NewValue != Entrance.None)
        {
            element.IsVisibleChanged += OnElementVisible;
        }
    }

    private static void OnElementVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || sender is not FrameworkElement element)
        {
            return;
        }

        RunEntrance(element, GetShow(element));
    }

    private static void RunEntrance(FrameworkElement element, Entrance kind)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(kind == Entrance.Fade ? 150 : 210));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // FillBehavior.Stop: when a run completes the property returns to its base value,
        // so every base here is deliberately the END state (opacity 1, offset 0, scale 1)
        // and each animation carries its own From. The element rests where the storyboard
        // leaves it, not where it started.
        var storyboard = new Storyboard { FillBehavior = FillBehavior.Stop };

        var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
        Storyboard.SetTargetProperty(fade, new PropertyPath(UIElement.OpacityProperty));
        storyboard.Children.Add(fade);

        if (kind is Entrance.Rise or Entrance.Settle)
        {
            var translate = new TranslateTransform();
            element.RenderTransformOrigin = new Point(0.5, 0);
            element.RenderTransform = kind == Entrance.Rise
                ? translate
                : new TransformGroup { Children = { new ScaleTransform(), translate } };

            var rise = new DoubleAnimation(kind == Entrance.Rise ? 9 : 0, 0, duration) { EasingFunction = ease };
            Storyboard.SetTargetProperty(rise, new PropertyPath(kind == Entrance.Rise
                ? "RenderTransform.Y"
                : "RenderTransform.Children[1].Y"));
            storyboard.Children.Add(rise);

            if (kind == Entrance.Settle)
            {
                var scaleX = new DoubleAnimation(1.02, 1, duration) { EasingFunction = ease };
                Storyboard.SetTargetProperty(scaleX, new PropertyPath("RenderTransform.Children[0].ScaleX"));
                storyboard.Children.Add(scaleX);

                var scaleY = scaleX.Clone();
                Storyboard.SetTargetProperty(scaleY, new PropertyPath("RenderTransform.Children[0].ScaleY"));
                storyboard.Children.Add(scaleY);
            }
        }

        storyboard.Begin(element, isControllable: false);
    }

    // -------------------------------------------------------------- Spin

    public static readonly DependencyProperty SpinProperty = DependencyProperty.RegisterAttached(
        "Spin",
        typeof(bool),
        typeof(Motion),
        new FrameworkPropertyMetadata(false, OnSpinChanged));

    /// <summary>Rotates the element continuously - the working state of an icon.</summary>
    public static void SetSpin(DependencyObject element, bool value) => element.SetValue(SpinProperty, value);

    public static bool GetSpin(DependencyObject element) => (bool)element.GetValue(SpinProperty);

    private static void OnSpinChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = new RotateTransform();

            // A hidden spinner keeps its clock running; pausing while collapsed keeps a
            // dozen buried spinners from costing frames no one is watching.
            element.IsVisibleChanged -= OnSpinVisibility;
            element.IsVisibleChanged += OnSpinVisibility;
            StartSpin(element);
        }
        else
        {
            element.IsVisibleChanged -= OnSpinVisibility;
            if (element.RenderTransform is RotateTransform rotate)
            {
                rotate.BeginAnimation(RotateTransform.AngleProperty, null);
            }

            element.RenderTransform = Transform.Identity;
        }
    }

    private static void OnSpinVisibility(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        if (e.NewValue is true)
        {
            StartSpin(element);
        }
        else if (element.RenderTransform is RotateTransform rotate)
        {
            rotate.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    private static void StartSpin(FrameworkElement element)
    {
        if (element.RenderTransform is not RotateTransform rotate || !SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        // Restart from the transform's current resting angle so a paused-then-resumed
        // spinner does not snap backwards a quarter turn.
        var from = rotate.Angle % 360;
        var spin = new DoubleAnimation(from, from + 360, new Duration(TimeSpan.FromMilliseconds(1100)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        rotate.BeginAnimation(RotateTransform.AngleProperty, spin);
    }

    // -------------------------------------------------------------- Pulse

    public static readonly DependencyProperty PulseProperty = DependencyProperty.RegisterAttached(
        "Pulse",
        typeof(bool),
        typeof(Motion),
        new FrameworkPropertyMetadata(false, OnPulseChanged));

    /// <summary>Breathes the element's opacity while it is attached - the alive state of a status dot.</summary>
    public static void SetPulse(DependencyObject element, bool value) => element.SetValue(PulseProperty, value);

    public static bool GetPulse(DependencyObject element) => (bool)element.GetValue(PulseProperty);

    private static void OnPulseChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.IsVisibleChanged -= OnPulseVisibility;

        if ((bool)e.NewValue)
        {
            element.IsVisibleChanged += OnPulseVisibility;
            StartPulse(element);
        }
        else
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
        }
    }

    private static void OnPulseVisibility(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        if (e.NewValue is true && GetPulse(element))
        {
            StartPulse(element);
        }
        else
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
        }
    }

    private static void StartPulse(FrameworkElement element)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        var breathe = new DoubleAnimation(1, 0.35, new Duration(TimeSpan.FromMilliseconds(900)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        element.BeginAnimation(UIElement.OpacityProperty, breathe);
    }
}
