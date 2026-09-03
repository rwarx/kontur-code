using System.Windows.Controls;

namespace AIClient.App.Views;

/// <summary>
/// The first-run wizard (section 32), shown as an overlay over the shell.
/// </summary>
/// <remarks>
/// All three steps live in one control with their panels toggled by the ViewModel's step
/// flags. A NavigationView or a frame per step would give each one its own DataContext and
/// lifetime for a flow the user walks through once.
/// </remarks>
public partial class FirstRunView : UserControl
{
    public FirstRunView()
    {
        InitializeComponent();
    }
}
