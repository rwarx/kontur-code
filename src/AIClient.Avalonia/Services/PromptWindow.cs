using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AIClient.Avalonia.Services;

/// <summary>
/// The small modal behind ConfirmAsync / ShowErrorAsync while the shell is young. Built in
/// code rather than XAML because it exists in exactly one shape: a message, a confirm and
/// (on errors) a cancel.
/// </summary>
public sealed class PromptWindow : Window
{
    public PromptWindow()
    {
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = BrushFrom("SurfaceBrush");
        SystemDecorations = SystemDecorations.Full;
        TransparencyLevelHint = [];
    }

    public string Message
    {
        set => Build(value);
    }

    public string ConfirmText { get; set; } = "OK";

    public bool IsError { get; set; }

    private IBrush BrushFrom(string key)
    {
        if (TryGetResource(key, ActualThemeVariant, out var value) && value is IBrush brush)
        {
            return brush;
        }

        return Brushes.Gray;
    }

    private void Build(string message)
    {
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 24, 24, 8),
            Foreground = BrushFrom("TextPrimaryBrush"),
        };

        var confirm = new Button
        {
            Content = ConfirmText,
            Padding = new Thickness(16, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        confirm.Click += (_, _) => Close(true);

        var cancel = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(16, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        cancel.Click += (_, _) => Close(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(24, 8, 24, 20),
        };

        if (IsError)
        {
            buttons.Children.Add(cancel);
        }

        buttons.Children.Add(confirm);

        Content = new StackPanel { Children = { text, buttons } };
    }
}
