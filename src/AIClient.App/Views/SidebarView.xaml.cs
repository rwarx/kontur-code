using System.Windows.Controls;
using AIClient.App.ViewModels;

namespace AIClient.App.Views;

/// <summary>
/// The sidebar: New Chat, search, session list and the footer actions.
/// </summary>
/// <remarks>
/// The code-behind covers two view concerns only: giving the search box focus when the shell
/// asks for it, and turning "the list has been scrolled near its end" into a request for the
/// next page. Neither is expressible from a ViewModel without it knowing about scroll bars.
/// </remarks>
public partial class SidebarView : UserControl
{
    /// <summary>
    /// How close to the bottom triggers the next page. A screenful of lead time, so the
    /// rows are already there by the time the user reaches them.
    /// </summary>
    private const double LoadMoreThreshold = 240;

    public SidebarView()
    {
        InitializeComponent();
    }

    /// <summary>Focuses and selects the search box. Called by the shell for Ctrl+K.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnSessionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel main &&
            SessionList.SelectedItem is Application.DTOs.ConversationSummary session)
        {
            main.Sessions.OpenCommand.Execute(session);
        }
    }

    private async void OnSessionScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // async void on a routed event handler; LoadMoreAsync swallows its own failures.
        if (e.OriginalSource is not ScrollViewer viewer || DataContext is not MainViewModel main)
        {
            return;
        }

        var distanceFromBottom = viewer.ExtentHeight - viewer.VerticalOffset - viewer.ViewportHeight;

        if (distanceFromBottom <= LoadMoreThreshold && viewer.ExtentHeight > viewer.ViewportHeight)
        {
            await main.Sessions.LoadMoreCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
    }
}
