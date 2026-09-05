using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIClient.App.Markdown;
using AIClient.App.ViewModels;

namespace AIClient.App.Views;

/// <summary>
/// The code view's view layer: rendering the active document through the shared
/// highlighter, and tab strip interaction.
/// </summary>
/// <remarks>
/// The code surface is one <see cref="TextBlock"/> fed by <see cref="MarkdownHost"/>'s
/// code-lines attached property - the same path chat code blocks take, because it is the
/// cheapest retained rendering of many highlighted lines that WPF offers. The gutter is
/// a second TextBlock in the same face and line height, which is what keeps the pair
/// aligned without a per-line ItemsControl.
/// </remarks>
public partial class CodeView : UserControl
{
    private CodeViewModel? _viewModel;

    public CodeView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ActiveTabChanged -= OnActiveTabChanged;
        }

        if (e.NewValue is not CodeViewModel vm)
        {
            _viewModel = null;
            return;
        }

        _viewModel = vm;
        _viewModel.ActiveTabChanged += OnActiveTabChanged;

        RenderActiveTab();
    }

    private void OnActiveTabChanged(object? sender, EventArgs e) => RenderActiveTab();

    /// <summary>
    /// Renders the front document. A failed or loading tab clears the surface rather than
    /// leaving the previous file's lines standing in for the new one.
    /// </summary>
    private void RenderActiveTab()
    {
        var tab = _viewModel?.ActiveTab;

        MarkdownHost.SetCodeLines(CodeText, tab?.State == CodeLoadState.Loaded ? tab?.Lines : null);
        LineNumbers.Text = tab?.State == CodeLoadState.Loaded ? tab?.LineNumbers ?? string.Empty : string.Empty;
    }

    private void OnTabClicked(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is CodeTabViewModel tab && _viewModel is not null)
        {
            _viewModel.ActiveTab = tab;
        }
    }

    private void OnCloseTabClicked(object sender, RoutedEventArgs e)
    {
        // The close button owns the click; the surrounding row's activation must not
        // also fire for the tab it just removed.
        e.Handled = true;

        if (((FrameworkElement)sender).DataContext is CodeTabViewModel tab && _viewModel is not null)
        {
            _viewModel.CloseTabCommand.Execute(tab);
        }
    }
}
