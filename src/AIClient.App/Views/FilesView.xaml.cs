using System.Windows;
using System.Windows.Controls;
using AIClient.App.ViewModels;

namespace AIClient.App.Views;

/// <summary>
/// The files tree's view layer: one event, routed to the view model that owns selection.
/// </summary>
/// <remarks>
/// <see cref="TreeView.SelectedItem"/> is read-only in WPF, so the tree reports selections
/// instead of binding them; nothing needs the reverse direction today, and a behaviour for
/// a feature nobody has is code nobody maintains.
/// </remarks>
public partial class FilesView : UserControl
{
    public FilesView()
    {
        InitializeComponent();
    }

    private void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is FilesViewModel files && e.NewValue is FileNodeViewModel node)
        {
            files.SelectedEntry = node;
        }
    }
}
