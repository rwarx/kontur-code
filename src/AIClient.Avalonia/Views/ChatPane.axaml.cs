using System;
using System.Windows.Input;
using AIClient.Avalonia.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AIClient.Avalonia.Views;

/// <summary>
/// The chat surface's view half. Enter sends; the view model owns everything after that.
/// </summary>
public partial class ChatPane : UserControl
{
    public ChatPane()
    {
        InitializeComponent();

        Composer.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);

        // The view model arrives after construction through the binding, so the focus
        // handshake is wired when it lands.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ChatPaneViewModel chat)
            {
                chat.FocusInputRequested += (_, _) => Composer.Focus();
            }
        };
    }

    private TextBox Composer => this.GetControl<TextBox>("ComposerBox");

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter sends; Shift+Enter makes a newline. Handled in the tunnel so the TextBox
        // never inserts the newline first.
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;

            if (((ChatPaneViewModel)DataContext!).SendCommand is ICommand send && send.CanExecute(null))
            {
                send.Execute(null);
            }
        }
    }
}
