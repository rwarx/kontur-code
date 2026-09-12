using System.Windows;
using System.Windows.Controls;
using AIClient.App.ViewModels;
using Microsoft.Extensions.Logging;
using Wpf.Ui;

namespace AIClient.App.Views;

/// <summary>
/// The application shell: title bar, sidebar, workspace, context surface and status bar.
/// </summary>
/// <remarks>
/// Constructed by the container, so the ViewModel arrives ready rather than being fetched
/// from a service locator in the constructor.
///
/// The code here is limited to what a binding cannot express: moving keyboard focus,
/// opening and closing the model popup, and collapsing the sidebar into its icon rail -
/// all layout state the view owns on the ViewModel's behalf. Everything else is a command
/// on <see cref="MainViewModel"/>, reached through the ViewModel's events rather than by
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
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        ModelPicker.SelectionCommitted += OnModelSelectionCommitted;
        CommandPalette.Dismissed += OnCommandPaletteDismissed;

        // The panels start expanded; the first sync makes the column widths follow the
        // view model in both directions from here on.
        ApplyPanelLayout();

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
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ModelPicker.SelectionCommitted -= OnModelSelectionCommitted;
        CommandPalette.Dismissed -= OnCommandPaletteDismissed;
    }

    /// <summary>
    /// Panel widths are <see cref="GridLength"/> values, which bindings cannot write as
    /// simple doubles; the shell translates the view model's visibility state here. The
    /// palette's visibility is here for a different reason: it is the one overlay whose
    /// silent failure to appear is indistinguishable from a dead button, so it is set
    /// outright rather than bound through the window's DataContext.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsSidebarCollapsed)
            or nameof(MainViewModel.IsSidebarVisible)
            or nameof(MainViewModel.IsContextPanelVisible))
        {
            ApplyPanelLayout();
        }

        if (e.PropertyName is nameof(MainViewModel.IsCommandPaletteOpen))
        {
            // The palette focuses its own query box when it becomes visible, so setting
            // this is the whole of opening it.
            CommandPalette.Visibility = ViewModel.IsCommandPaletteOpen
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void ApplyPanelLayout()
    {
        var sidebarWidth = !ViewModel.IsSidebarVisible
            ? 0
            : ViewModel.IsSidebarCollapsed ? 48 : 232;

        SidebarColumn.MinWidth = sidebarWidth;
        SidebarColumn.MaxWidth = !ViewModel.IsSidebarVisible
            ? 0
            : ViewModel.IsSidebarCollapsed ? 48 : 420;
        SidebarColumn.Width = new GridLength(sidebarWidth);

        // A hidden panel must give its space back to the editor, not leave an empty strip.
        // The splitter is explicitly zeroed for the same reason.
        var contextWidth = ViewModel.IsContextPanelVisible ? 320 : 0;
        ContextSplitterColumn.Width = ViewModel.IsContextPanelVisible
            ? new GridLength(6)
            : new GridLength(0);
        ContextColumn.MinWidth = ViewModel.IsContextPanelVisible ? 280 : 0;
        ContextColumn.MaxWidth = ViewModel.IsContextPanelVisible ? 480 : 0;
        ContextColumn.Width = new GridLength(contextWidth);
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
}
