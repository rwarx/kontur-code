using System.Windows;
using System.Windows.Controls;
using AIClient.App.ViewModels;
using Microsoft.Extensions.Logging;
using Wpf.Ui;

namespace AIClient.App.Views;

/// <summary>
/// The application shell: sidebar, chat header with the model selector, and the panes.
/// </summary>
/// <remarks>
/// Constructed by the container, so the ViewModel arrives ready rather than being fetched
/// from a service locator in the constructor.
///
/// The code here is limited to what a binding cannot express: moving keyboard focus, and
/// opening and closing the model popup. Everything else the shell does is a command on
/// <see cref="MainViewModel"/>, reached through the ViewModel events below rather than by
/// the ViewModel holding a reference to this window.
/// </remarks>
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly ILogger<MainWindow> _logger;

    public MainWindow(
        MainViewModel viewModel,
        IContentDialogService contentDialogService,
        ISnackbarService snackbarService,
        ILogger<MainWindow> logger)
    {
        ViewModel = viewModel;
        _logger = logger;

        InitializeComponent();

        DataContext = viewModel;

        // Both services need a host element from the visual tree, which only exists now.
        contentDialogService.SetDialogHost(DialogHost);
        snackbarService.SetSnackbarPresenter(SnackbarHost);

        viewModel.SearchRequested += OnSearchRequested;
        viewModel.ModelPickerRequested += OnModelPickerRequested;

        ModelPicker.SelectionCommitted += OnModelSelectionCommitted;
        CommandPalette.Dismissed += OnCommandPaletteDismissed;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public MainViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // async void on a routed handler is the one case section 35 allows. The work is
        // deferred to Loaded rather than done in the constructor so the window is on screen
        // while the session list and model catalogue load.
        try
        {
            await ViewModel.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Startup data is recoverable: the user can still open Settings and add a key.
            _logger.LogError(ex, "The shell could not finish loading.");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ViewModel.SearchRequested -= OnSearchRequested;
        ViewModel.ModelPickerRequested -= OnModelPickerRequested;
        ModelPicker.SelectionCommitted -= OnModelSelectionCommitted;
        CommandPalette.Dismissed -= OnCommandPaletteDismissed;
    }

    /// <summary>Ctrl+K, or the palette's Search entry.</summary>
    private void OnSearchRequested(object? sender, EventArgs e)
    {
        // The sidebar may have just been made visible by the same command, and a control
        // that has not been arranged yet cannot take focus.
        Dispatcher.BeginInvoke(Sidebar.FocusSearch, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnModelPickerRequested(object? sender, EventArgs e) => OpenModelPopup();

    private void OnModelButtonClick(object sender, RoutedEventArgs e) => OpenModelPopup();

    private void OpenModelPopup()
    {
        ModelPopup.IsOpen = true;

        // Same reason as above: the popup's content tree is built when it opens.
        Dispatcher.BeginInvoke(ModelPicker.FocusFilter, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnModelSelectionCommitted(object? sender, EventArgs e) => ModelPopup.IsOpen = false;

    private void OnCommandPaletteDismissed(object? sender, EventArgs e) =>
        ViewModel.IsCommandPaletteOpen = false;

    /// <summary>
    /// Opens the export menu on a left click, which a ContextMenu does not do by itself.
    /// </summary>
    private void OnExportButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu })
        {
            return;
        }

        // Without an explicit DataContext the menu, being outside the visual tree, would
        // inherit nothing and every command binding on it would fail silently.
        menu.DataContext = DataContext;
        menu.PlacementTarget = sender as UIElement;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
