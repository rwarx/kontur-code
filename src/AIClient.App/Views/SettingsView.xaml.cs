using System.Windows.Controls;

namespace AIClient.App.Views;

/// <summary>
/// The settings pane: General, Appearance, Providers, Chat, Storage, Shortcuts, About.
/// </summary>
/// <remarks>
/// No code-behind logic. The one thing that could not be done declaratively - keeping a
/// PasswordBox in step with the ViewModel without binding the key - lives in
/// <see cref="Behaviors.ApiKeyBox"/>, because the first-run wizard needs the same rule.
/// </remarks>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }
}
