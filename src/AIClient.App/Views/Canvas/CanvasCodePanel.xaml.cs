using System.Windows.Controls;

namespace AIClient.App.Views.Canvas;

/// <summary>
/// The file behind a card, read-only.
/// </summary>
/// <remarks>
/// One handler of code-behind, for one reason: a second box holding line numbers lines up with the
/// code only while the two agree on their vertical offset, and no binding can express "follow that
/// scroll". The code is what a person scrolls; the gutter follows it and cannot be scrolled by hand.
/// </remarks>
public partial class CanvasCodePanel : UserControl
{
    public CanvasCodePanel() => InitializeComponent();

    private void OnCodeScrollChanged(object sender, ScrollChangedEventArgs e) =>
        Gutter.ScrollToVerticalOffset(e.VerticalOffset);
}
