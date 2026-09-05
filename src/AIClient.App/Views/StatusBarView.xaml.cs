using System.Windows.Controls;

namespace AIClient.App.Views;

/// <summary>
/// The status bar's view layer. Pure XAML: every value it shows is a property on the view
/// models it inherits from the window, so there is nothing to wire here.
/// </summary>
public partial class StatusBarView : UserControl
{
    public StatusBarView()
    {
        InitializeComponent();
    }
}
