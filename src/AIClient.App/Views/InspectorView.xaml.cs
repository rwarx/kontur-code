using System.Windows.Controls;

namespace AIClient.App.Views;

/// <summary>
/// Code-behind for the inspector.
/// </summary>
/// <remarks>
/// Empty on purpose. The panel is a projection of the graph: everything it shows arrives through
/// bindings, and its two actions - ask the AI, reveal the file - are commands on the view model.
/// There is no scroll position to restore and no keyboard routing to arrange, so there is nothing
/// for a view to do here beyond existing.
/// </remarks>
public partial class InspectorView : UserControl
{
    public InspectorView()
    {
        InitializeComponent();
    }
}
