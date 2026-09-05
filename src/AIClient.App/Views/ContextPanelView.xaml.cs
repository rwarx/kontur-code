using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIClient.App.ViewModels;

namespace AIClient.App.Views;

/// <summary>
/// The context surface's view layer: routing the relations list's rows to the view model.
/// </summary>
/// <remarks>
/// <para>
/// The relation rows are dense list rows, not template buttons - one quiet
/// <c>MouseLeftButtonUp</c> on the row's hit area is the whole gesture, so a hundred
/// relations stay a hundred borders instead of a hundred themed buttons.
/// </para>
/// <para>
/// The handler routes the row's <see cref="NodeRelationRow"/> to the view model's
/// activation command rather than re-deriving the focus behaviour here: the view layer
/// knows which row was clicked, the view model knows what that means.
/// </para>
/// </remarks>
public partial class ContextPanelView : UserControl
{
    public ContextPanelView()
    {
        InitializeComponent();
    }

    /// <summary>A relation row's click: focus the other node on the canvas.</summary>
    private void OnRelationClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NodeRelationRow row }
            && DataContext is ContextPanelViewModel vm)
        {
            vm.RelationRowActivatedCommand.Execute(row);
        }

        // The row is not draggable, and the click must not fall through to anything below.
        e.Handled = true;
    }
}
