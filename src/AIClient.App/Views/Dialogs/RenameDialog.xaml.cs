using System.Windows;
using Wpf.Ui.Controls;

namespace AIClient.App.Views.Dialogs;

/// <summary>
/// A single-field prompt for renaming a chat.
/// </summary>
/// <remarks>
/// A modal window rather than a ContentDialog: the caller is a ViewModel command that wants
/// a value back, and <c>ShowDialog</c> gives it one without the ViewModel having to await a
/// dialog host it would then need injected.
/// </remarks>
public partial class RenameDialog : FluentWindow
{
    public RenameDialog(string currentTitle)
    {
        InitializeComponent();

        TitleBox.Text = currentTitle;

        // Guarded: during a unit test or a very early call there may be no shell yet, and
        // assigning a null owner is fine while assigning a closed one throws.
        var owner = System.Windows.Application.Current?.MainWindow;

        if (owner is not null && !ReferenceEquals(owner, this) && owner.IsLoaded)
        {
            Owner = owner;
        }

        Loaded += (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
        };
    }

    /// <summary>The entered name. Only meaningful when <c>ShowDialog</c> returned true.</summary>
    public string ChatTitle { get; private set; } = string.Empty;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var value = TitleBox.Text.Trim();

        // An empty name would leave an unlabelled row in the sidebar; treat it as "no change"
        // rather than refusing with an error the user has to dismiss.
        if (value.Length == 0)
        {
            DialogResult = false;
            return;
        }

        ChatTitle = value;
        DialogResult = true;
    }
}
