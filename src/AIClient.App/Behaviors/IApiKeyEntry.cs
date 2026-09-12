namespace AIClient.App.Behaviors;

/// <summary>
/// What <see cref="ApiKeyBox"/> needs from its row: a one-way pipe for the typed key and
/// the two commands that consume it.
/// </summary>
/// <remarks>
/// Lives next to the behavior so any view-model that wants inline key entry can satisfy it
/// without picking up the heavier <c>ProviderSettingsViewModel</c>. Settings and the Models
/// page both implement it.
/// </remarks>
public interface IApiKeyEntry : System.ComponentModel.INotifyPropertyChanged
{
    string ApiKeyInput { get; set; }

    System.Windows.Input.ICommand SaveApiKeyCommand { get; }

    System.Windows.Input.ICommand CancelEditApiKeyCommand { get; }
}
