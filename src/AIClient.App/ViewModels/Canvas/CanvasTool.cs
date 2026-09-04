namespace AIClient.App.ViewModels.Canvas;

/// <summary>
/// What the left mouse button does on the surface.
/// </summary>
/// <remarks>
/// <para>
/// Two modes rather than one, because the two gestures want the same button and cannot share it.
/// Dragging empty space is either a rubber band or a pan, and every canvas that has ever existed
/// settles this the same way: a tool the person chooses, so the surface behaves the way they just
/// asked it to instead of guessing from a modifier they have to remember.
/// </para>
/// <para>
/// Middle and right drag pan in either mode. The tool decides what the <em>left</em> button means;
/// it never takes a way of moving around away from anyone.
/// </para>
/// <para>
/// Not persisted. <see cref="Select"/> is what a canvas should be on open - the mode in which
/// clicking a card does the obvious thing - and remembering that somebody once left it on
/// <see cref="Pan"/> would be a settings row that only ever surprises them.
/// </para>
/// </remarks>
public enum CanvasTool
{
    /// <summary>Click picks a card, drag moves it, drag on empty space is a rubber band.</summary>
    Select = 0,

    /// <summary>Drag anywhere moves the camera, and nothing is selected or moved.</summary>
    Pan = 1,
}
