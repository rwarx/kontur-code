using Avalonia.Controls;

namespace AIClient.Avalonia.Views;

/// <summary>
/// The canvas page. Everything interactive happens in
/// <see cref="Rendering.CanvasRenderSurface"/>; this class exists so the page has a type.
/// </summary>
public partial class CanvasView : UserControl
{
    public CanvasView()
    {
        InitializeComponent();
    }
}
