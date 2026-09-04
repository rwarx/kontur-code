using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AIClient.Avalonia.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AIClient.Avalonia.Views;

/// <summary>
/// The shell window. Custom chrome means the window buttons and dragging are wired here;
/// everything else belongs to <see cref="ShellViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        DataContext = App.Services.GetRequiredService<ShellViewModel>();

        Opened += (_, _) =>
        {
            _ = ((ShellViewModel)DataContext).Chat.ActivateAsync();
        };

        AddHandler(PointerPressedEvent, OnDragAreaPressed, RoutingStrategies.Tunnel);
    }

    /// <summary>The shell's shortcut map. Pane-local keys live in the panes.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        var shell = (ShellViewModel)DataContext!;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl && shift && e.Key == Key.P)
        {
            shell.TogglePaletteCommand.Execute(null);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.G)
        {
            shell.ShowCanvasCommand.Execute(null);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.B)
        {
            shell.ToggleSidebarCommand.Execute(null);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.OemComma)
        {
            shell.ShowSettingsCommand.Execute(null);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.N)
        {
            shell.Chat.NewChatCommand.Execute(null);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.K)
        {
            shell.ShowChatCommand.Execute(null);
            e.Handled = true;
        }
        else if (!ctrl && e.Key == Key.F && shell.IsCanvasVisible)
        {
            shell.Canvas.FitToContentCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnDragAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        // The drag area is the left part of the title bar; the buttons stop the event before
        // the tunnel reaches them, so a click on a button never drags the window.
        if (e.Source is Visual visual && !IsInDragArea(visual))
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private static bool IsInDragArea(Visual source)
    {
        for (var current = source; current is not null; current = current.Parent as Visual)
        {
            if (current.Name == "DragArea")
            {
                return true;
            }

            if (current is Button)
            {
                return false;
            }
        }

        return false;
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
