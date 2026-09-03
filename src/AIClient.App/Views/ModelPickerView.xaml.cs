using System.Windows.Controls;

namespace AIClient.App.Views;

/// <summary>The model selector, hosted in a flyout from the chat header.</summary>
public partial class ModelPickerView : UserControl
{
    public ModelPickerView()
    {
        InitializeComponent();
    }

    /// <summary>Raised when a model is chosen, so the host can close the flyout.</summary>
    public event EventHandler? SelectionCommitted;

    /// <summary>Focuses the filter box, so typing narrows the list immediately on open.</summary>
    public void FocusFilter()
    {
        FilterBox.Focus();
        FilterBox.SelectAll();
    }

    private void OnModelSelected(object sender, SelectionChangedEventArgs e)
    {
        // AddedItems rather than SelectedItem: this also fires when the list is repopulated
        // and the previous selection is restored, which is not a user choice and must not
        // dismiss the flyout.
        if (e.AddedItems.Count > 0)
        {
            SelectionCommitted?.Invoke(this, EventArgs.Empty);
        }
    }
}
