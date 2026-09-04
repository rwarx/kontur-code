using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIClient.App.ViewModels.Canvas;

namespace AIClient.App.Views.Canvas;

/// <summary>
/// The gestures of the canvas, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place in the canvas where code-behind is the right answer. A pointer gesture is
/// a genuine view concern: it arrives as a screen coordinate, it needs mouse capture to survive
/// leaving the window, and its meaning depends on which button is down. None of that belongs in a
/// view model, and none of it can be expressed as a binding.
/// </para>
/// <para>
/// What this file does not do is decide anything. It converts screen points to world points with
/// the view model's own camera and calls the methods the view model already exposes; every
/// selection rule, every clamp and every save lives there. That is why the file is short.
/// </para>
/// <para>
/// Cards never see the mouse - the world layer is <c>IsHitTestVisible="False"</c> - so hover is
/// set from here after a hit test. It buys a single input path at any zoom, and it costs this one
/// piece of bookkeeping.
/// </para>
/// </remarks>
public partial class CanvasView : UserControl
{
    /// <summary>
    /// How far the pointer must travel before a press on a card becomes a drag.
    /// </summary>
    /// <remarks>
    /// Without it a shaky click would move a card by a pixel and persist that as a deliberate
    /// placement, which is a small thing that makes an application feel careless.
    /// </remarks>
    private const double DragThreshold = 3;

    /// <summary>Roughly a fifth per notch, and smooth for a fast scroll rather than stepped.</summary>
    private const double ZoomPerWheelUnit = 1.0015;

    private Gesture _gesture;

    /// <summary>The last pointer position, in screen coordinates.</summary>
    private Point _last;

    /// <summary>Where the button went down, for the drag threshold.</summary>
    private Point _pressed;

    private CanvasNodeViewModel? _hovered;

    public CanvasView()
    {
        InitializeComponent();

        // Taking focus when the page appears is what makes F, E and Escape work immediately after
        // Ctrl+G, without asking the person to click the canvas first to wake it up.
        IsVisibleChanged += OnIsVisibleChanged;
    }

    /// <summary>
    /// What the pointer is currently doing.
    /// </summary>
    /// <remarks>
    /// <see cref="Press"/> is the undecided state between a click and a drag; it resolves into
    /// <see cref="Drag"/> once the pointer has moved past <see cref="DragThreshold"/>.
    /// </remarks>
    private enum Gesture
    {
        None,
        Press,
        Drag,
        Marquee,
        Pan,
    }

    private CanvasViewModel? ViewModel => DataContext as CanvasViewModel;

    private static bool Additive =>
        (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            Focus();
        }
    }

    /// <summary>The view model culls to the visible rectangle, so it has to be told what that is.</summary>
    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e) =>
        ViewModel?.SetSurfaceSize(e.NewSize.Width, e.NewSize.Height);

    private void OnSurfaceMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        Focus();

        _pressed = e.GetPosition(Surface);
        _last = _pressed;

        if (vm.Tool == CanvasTool.Pan)
        {
            // No hit test at all in this mode. The left button moves the camera and does nothing
            // else, which is the whole reason the tool exists - a person who chose the hand does not
            // want a card to follow the pointer because they started the drag a few pixels too far
            // to the left.
            _gesture = Gesture.Pan;
            Cursor = Cursors.SizeAll;

            Surface.CaptureMouse();
            e.Handled = true;
            return;
        }

        var card = vm.HitTest(vm.ToWorldX(_pressed.X), vm.ToWorldY(_pressed.Y));

        if (card is null)
        {
            _gesture = Gesture.Marquee;
            vm.BeginMarquee(_pressed.X, _pressed.Y);
        }
        else if (e.ClickCount == 2)
        {
            // The second click of a double-click. The first one already made this card the selection,
            // so this one only opens its file - and deliberately starts no drag, or the card would
            // creep across the canvas while the panel was opening.
            _gesture = Gesture.None;

            // Not awaited: reading a file is not something the pointer should wait on, and the panel
            // reports its own refusals.
            _ = vm.OpenCodeAsync(card);

            e.Handled = true;
            return;
        }
        else
        {
            // Selecting before beginning the drag is deliberate: a press on an unselected card
            // makes it the selection, and a press on one of several drags all of them.
            _gesture = Gesture.Press;
            vm.Click(card, Additive);
            vm.BeginDrag(card);
        }

        Surface.CaptureMouse();
        e.Handled = true;
    }

    private void OnSurfaceMouseMove(object sender, MouseEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var at = e.GetPosition(Surface);

        if (_gesture == Gesture.Press &&
            (Math.Abs(at.X - _pressed.X) >= DragThreshold || Math.Abs(at.Y - _pressed.Y) >= DragThreshold))
        {
            _gesture = Gesture.Drag;
        }

        switch (_gesture)
        {
            case Gesture.Drag:
                // Divided by the zoom, so a card stays under the pointer instead of drifting away
                // from it at anything other than 100%.
                vm.DragBy((at.X - _last.X) / vm.Zoom, (at.Y - _last.Y) / vm.Zoom);
                break;

            case Gesture.Marquee:
                vm.UpdateMarquee(at.X, at.Y);
                break;

            case Gesture.Pan:
                vm.Pan(at.X - _last.X, at.Y - _last.Y);
                break;

            case Gesture.Press:
                break;

            default:
                UpdateHover(vm, at);
                break;
        }

        _last = at;
    }

    private void OnSurfaceMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var gesture = _gesture;
        _gesture = Gesture.None;
        Surface.ReleaseMouseCapture();

        if (ViewModel is not { } vm)
        {
            return;
        }

        switch (gesture)
        {
            case Gesture.Marquee:
                vm.EndMarquee(Additive);
                break;

            case Gesture.Press:
            case Gesture.Drag:
                // Not awaited: the save writes placements through the store, which reports its own
                // failures, and the pointer must not wait on a database to be released.
                _ = vm.EndDragAsync();
                break;

            default:
                break;
        }

        // The pointer may have ended up over a different card than it started on.
        UpdateHover(vm, e.GetPosition(Surface));
    }

    /// <summary>
    /// Middle or right drag pans.
    /// </summary>
    /// <remarks>
    /// Both, because middle drag is what people who use design tools reach for and right drag is
    /// what people who use game editors reach for, and neither costs anything. There is no context
    /// menu on the surface for the right button to compete with.
    /// </remarks>
    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_gesture != Gesture.None || e.ChangedButton is not (MouseButton.Middle or MouseButton.Right))
        {
            return;
        }

        Focus();

        _last = e.GetPosition(Surface);
        _gesture = Gesture.Pan;
        Cursor = Cursors.SizeAll;

        Surface.CaptureMouse();
        e.Handled = true;
    }

    private void OnSurfaceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_gesture != Gesture.Pan || e.ChangedButton is not (MouseButton.Middle or MouseButton.Right))
        {
            return;
        }

        _gesture = Gesture.None;
        Cursor = Cursors.Arrow;
        Surface.ReleaseMouseCapture();
    }

    /// <summary>
    /// Zooms about the pointer.
    /// </summary>
    /// <remarks>
    /// About the pointer rather than the centre of the surface: the thing a person wants a closer
    /// look at is the thing they are pointing at, and keeping it still under the cursor is what
    /// makes a large graph navigable.
    /// </remarks>
    private void OnSurfaceMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var at = e.GetPosition(Surface);

        vm.ZoomAt(Math.Pow(ZoomPerWheelUnit, e.Delta), at.X, at.Y);
        UpdateHover(vm, at);

        e.Handled = true;
    }

    private void OnSurfaceMouseLeave(object sender, MouseEventArgs e) => ClearHover();

    private void UpdateHover(CanvasViewModel vm, Point at)
    {
        if (vm.Tool == CanvasTool.Pan)
        {
            // Nothing on the surface answers a left click in this mode, so nothing should light up as
            // though it would. The pan cursor is set on the surface itself, in markup, which leaves
            // the toolbar and the rest of the chrome with an ordinary arrow.
            ClearHover();
            return;
        }

        var card = vm.HitTest(vm.ToWorldX(at.X), vm.ToWorldY(at.Y));

        if (ReferenceEquals(card, _hovered))
        {
            return;
        }

        if (_hovered is not null)
        {
            _hovered.IsHovered = false;
        }

        _hovered = card;

        if (card is not null)
        {
            card.IsHovered = true;
        }

        Cursor = card is null ? Cursors.Arrow : Cursors.Hand;
    }

    private void ClearHover()
    {
        if (_hovered is not null)
        {
            _hovered.IsHovered = false;
            _hovered = null;
        }

        if (_gesture == Gesture.None)
        {
            Cursor = Cursors.Arrow;
        }
    }
}
