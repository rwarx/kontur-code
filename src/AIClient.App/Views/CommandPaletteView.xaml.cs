using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIClient.App.ViewModels;

namespace AIClient.App.Views;

/// <summary>
/// The Ctrl+Shift+P palette, hosted as an overlay over the shell.
/// </summary>
/// <remarks>
/// Focus stays in the search box the whole time: the arrow keys move the highlight in the
/// list without moving focus, so the user never has to Tab across to run something. That
/// is the behaviour every palette has, and it is only expressible from the view.
/// </remarks>
public partial class CommandPaletteView : UserControl
{
    public CommandPaletteView()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibleChanged;
    }

    /// <summary>Raised when the palette should close, either by Esc or by a click outside it.</summary>
    public event EventHandler? Dismissed;

    private CommandPaletteViewModel? ViewModel => DataContext as CommandPaletteViewModel;

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            // Dispatcher: the overlay is measured after the visibility change, and focusing
            // an unarranged control silently does nothing.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                QueryBox.Focus();
                QueryBox.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        var viewModel = ViewModel;

        if (viewModel is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                e.Handled = true;
                viewModel.MoveSelection(1);
                ScrollSelectionIntoView();
                break;

            case Key.Up:
                e.Handled = true;
                viewModel.MoveSelection(-1);
                ScrollSelectionIntoView();
                break;

            case Key.Enter:
                e.Handled = true;
                viewModel.InvokeCommand.Execute(null);
                break;

            case Key.Escape:
                e.Handled = true;
                Dismissed?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void OnEntryClicked(object sender, MouseButtonEventArgs e)
    {
        // MouseLeftButtonUp on the list, not SelectionChanged: the highlight also moves
        // from the keyboard, and running a command on every arrow press would be chaos.
        if (ViewModel?.SelectedEntry is not null)
        {
            ViewModel.InvokeCommand.Execute(ViewModel.SelectedEntry);
        }
    }

    private void OnScrimClicked(object sender, MouseButtonEventArgs e) =>
        Dismissed?.Invoke(this, EventArgs.Empty);

    /// <summary>Stops a click on the panel from reaching the scrim and closing the palette.</summary>
    private void OnPanelClicked(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void ScrollSelectionIntoView()
    {
        if (EntryList.SelectedItem is { } selected)
        {
            EntryList.ScrollIntoView(selected);
        }
    }
}
