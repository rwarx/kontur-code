using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIClient.App.ViewModels;

namespace AIClient.App.Views;

/// <summary>The outline's view layer: row activation is a click, forwarded to the row itself.</summary>
public partial class GraphOutlineView : UserControl
{
    public GraphOutlineView()
    {
        InitializeComponent();
    }

    private void OnNodeClicked(object sender, MouseButtonEventArgs e)
    {
        // The row is the item; the click is activation, not selection, because what the
        // outline "selects" is the node on the canvas.
        if (((FrameworkElement)sender).DataContext is OutlineNodeViewModel row)
        {
            row.Activate();
        }

        e.Handled = true;
    }
}
