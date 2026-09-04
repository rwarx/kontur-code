using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIClient.App.ViewModels;

namespace AIClient.App.Views;

/// <summary>
/// The conversation pane.
/// </summary>
/// <remarks>
/// Everything here is a genuine view concern - scroll position, keyboard routing and
/// drag-and-drop - none of which a ViewModel can express without reaching into WPF.
/// The decisions themselves (whether Enter sends, whether to follow the tail) live in
/// <see cref="ChatViewModel"/>; this file only carries them out.
/// </remarks>
public partial class ChatView : UserControl
{
    /// <summary>
    /// How close to the bottom still counts as "following". Not zero: the scroll extent
    /// changes by fractions of a pixel as text is measured, and an exact comparison would
    /// flicker the jump-to-latest button on and off during streaming.
    /// </summary>
    private const double BottomThreshold = 48;

    private ChatViewModel? _viewModel;

    public ChatView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => DraftBox.Focus();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ScrollToEndRequested -= OnScrollToEndRequested;
            _viewModel.FocusInputRequested -= OnFocusInputRequested;
        }

        _viewModel = e.NewValue as ChatViewModel;

        if (_viewModel is not null)
        {
            _viewModel.ScrollToEndRequested += OnScrollToEndRequested;
            _viewModel.FocusInputRequested += OnFocusInputRequested;
        }
    }

    private void OnScrollToEndRequested(object? sender, EventArgs e)
    {
        // Only follow when the user is already at the tail. Yanking the view back down
        // while someone is reading earlier output is the single most irritating thing a
        // streaming chat UI can do.
        if (_viewModel?.IsScrolledAway != true)
        {
            TranscriptScroller.ScrollToEnd();
        }
    }

    private void OnFocusInputRequested(object? sender, EventArgs e) => DraftBox.Focus();

    private void OnTranscriptScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var distanceFromBottom =
            TranscriptScroller.ExtentHeight - TranscriptScroller.VerticalOffset - TranscriptScroller.ViewportHeight;

        _viewModel.IsScrolledAway = distanceFromBottom > BottomThreshold;
    }

    private void OnJumpToLatest(object sender, RoutedEventArgs e)
    {
        TranscriptScroller.ScrollToEnd();

        if (_viewModel is not null)
        {
            _viewModel.IsScrolledAway = false;
        }
    }

    /// <summary>
    /// Enter and Shift+Enter (section 23), in whichever order the user configured.
    /// </summary>
    private void OnDraftKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null || e.Key != Key.Enter)
        {
            return;
        }

        // IME composition: Enter commits the candidate rather than sending the message.
        if (e.Key == Key.ImeProcessed)
        {
            return;
        }

        var withShift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        var shouldSend = _viewModel.SendWithEnter ? !withShift : withShift;

        if (!shouldSend)
        {
            return;
        }

        e.Handled = true;

        if (_viewModel.SendCommand.CanExecute(null))
        {
            _viewModel.SendCommand.Execute(null);
        }
    }

    private void OnFilesDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFilesDropped(object sender, DragEventArgs e)
    {
        // async void is unavoidable on a routed event handler; the awaited call handles
        // its own failures and reports them through the ViewModel's banner.
        if (_viewModel is null || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        e.Handled = true;

        await _viewModel.AddDroppedFilesAsync(paths).ConfigureAwait(true);
    }

    /// <summary>
    /// Opens the agent-mode menu on a left click, which a <see cref="ContextMenu"/> does not do
    /// by itself.
    /// </summary>
    /// <remarks>
    /// The same handler as the export button in <c>MainWindow</c>, placed above the button rather
    /// than below it: the composer sits at the bottom of the window, where there is nothing under
    /// it to open into.
    /// </remarks>
    private void OnAgentButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu })
        {
            return;
        }

        // Without an explicit DataContext the menu, being outside the visual tree, would inherit
        // nothing and every command binding on it would fail silently.
        menu.DataContext = DataContext;
        menu.PlacementTarget = sender as UIElement;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }
}
