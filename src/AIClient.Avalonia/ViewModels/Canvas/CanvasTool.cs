namespace AIClient.Avalonia.ViewModels.Canvas;

/// <summary>
/// What the left mouse button does on the surface. A mode a person stays in, chosen in the
/// toolbar, never inferred from a modifier and never persisted.
/// </summary>
public enum CanvasTool
{
    /// <summary>Click picks a card, drag moves it, drag on empty space is a rubber band.</summary>
    Select,

    /// <summary>Drag anywhere moves the camera and does nothing else.</summary>
    Pan,
}
