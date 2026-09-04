using System;
using AIClient.Avalonia.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AIClient.Avalonia.Views;

/// <summary>
/// The command palette's view half: typing, arrows, Enter, Escape.
/// </summary>
public partial class CommandPaletteView : UserControl
{
    public CommandPaletteView()
    {
        InitializeComponent();

        QueryBox.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        QueryBox.TextChanged += (_, _) =>
        {
            if (DataContext is ShellViewModel shell)
            {
                shell.Palette.Query = QueryBox.Text ?? string.Empty;
            }
        };
    }

    private ShellViewModel Shell => (ShellViewModel)DataContext!;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                Shell.Palette.MoveSelection(1);
                e.Handled = true;
                break;

            case Key.Up:
                Shell.Palette.MoveSelection(-1);
                e.Handled = true;
                break;

            case Key.Enter:
                e.Handled = true;
                Shell.Palette.ExecuteSelected();
                break;

            case Key.Escape:
                e.Handled = true;
                Shell.ClosePaletteCommand.Execute(null);
                break;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Anything typed while the palette is open belongs in the query box.
        if (e.Key is Key.Down or Key.Up or Key.Enter or Key.Escape)
        {
            return;
        }

        if (!QueryBox.IsFocused)
        {
            e.Handled = true;
            QueryBox.Focus();
        }
    }

    private void OnResultDoubleTapped(object? sender, RoutedEventArgs e) => Shell.Palette.ExecuteSelected();
}
