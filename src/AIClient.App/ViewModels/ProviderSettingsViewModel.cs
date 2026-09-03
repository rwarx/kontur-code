using AIClient.App.Services;
using AIClient.Application.Interfaces;
using AIClient.Domain.Enums;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIClient.App.ViewModels;

/// <summary>
/// One provider row in Settings: key entry, connection test and model refresh.
/// </summary>
/// <remarks>
/// Section 11 forbids showing a key in full. The stored key is never read back for display -
/// there is no code path from <c>ISecureStorage.GetAsync</c> to this class at all. The row
/// knows only whether a key exists, and shows a fixed mask when one does. What the user
/// types stays in <see cref="ApiKeyInput"/> until Save, and is cleared immediately after.
/// </remarks>
public sealed partial class ProviderSettingsViewModel : ObservableObject
{
    private readonly IProviderRegistry _registry;
    private readonly IDialogService _dialogs;
    private readonly ILogger _logger;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _hasApiKey;

    [ObservableProperty]
    private ConnectionState _connectionState;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private int _cachedModelCount;

    [ObservableProperty]
    private DateTimeOffset? _modelsRefreshedAt;

    /// <summary>What the user is typing. Never populated from storage, cleared after Save.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveApiKeyCommand))]
    private string _apiKeyInput = string.Empty;

    [ObservableProperty]
    private bool _isEditingApiKey;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshModelsCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _technicalDetails;

    public ProviderSettingsViewModel(
        ProviderInfo info,
        IProviderRegistry registry,
        IDialogService dialogs,
        ILogger logger)
    {
        _registry = registry;
        _dialogs = dialogs;
        _logger = logger;

        Id = info.Id;
        Name = info.Name;
        ApiKeyUrl = info.ApiKeyUrl;

        _isEnabled = info.IsEnabled;
        _hasApiKey = info.HasApiKey;
        _connectionState = info.ConnectionState;
        _statusMessage = info.StatusMessage;
        _cachedModelCount = info.CachedModelCount;
        _modelsRefreshedAt = info.ModelsRefreshedAt;
    }

    public string Id { get; }
    public string Name { get; }
    public string? ApiKeyUrl { get; }

    /// <summary>
    /// A fixed-length mask. Deliberately not derived from the real key: a mask that matched
    /// the key's length would leak how long it is, and reading it back to count characters
    /// would mean decrypting a secret purely to draw dots.
    /// </summary>
    public string ApiKeyDisplay => HasApiKey ? "••••••••••••••••••••" : "Not configured";

    public bool CanSaveApiKey => ApiKeyInput.Trim().Length > 0 && !IsBusy;

    public bool CanUseProvider => HasApiKey && !IsBusy;

    public string StatusText => ConnectionState switch
    {
        ConnectionState.NotConfigured => "No API key",
        ConnectionState.Testing => "Testing…",
        ConnectionState.Connected => StatusMessage ?? "Connected",
        ConnectionState.Failed => StatusMessage ?? "Connection failed",
        _ => HasApiKey ? "Not tested" : "No API key",
    };

    public string ModelSummary => CachedModelCount switch
    {
        0 => "No models cached",
        1 => "1 model cached",
        _ => $"{CachedModelCount} models cached",
    };

    [RelayCommand(CanExecute = nameof(CanSaveApiKey))]
    private async Task SaveApiKeyAsync()
    {
        var key = ApiKeyInput.Trim();

        // Cleared before anything else can fail, so the plaintext does not linger in a
        // bound TextBox where it would be visible and reachable.
        ApiKeyInput = string.Empty;
        IsEditingApiKey = false;
        IsBusy = true;

        try
        {
            await _registry.SetApiKeyAsync(Id, key).ConfigureAwait(true);

            HasApiKey = true;
            ConnectionState = ConnectionState.Unknown;
            StatusMessage = null;
            TechnicalDetails = null;
        }
        finally
        {
            IsBusy = false;
        }

        // Testing straight away is what the user wants next, and it populates the model
        // list without a second click.
        await TestConnectionAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RemoveApiKeyAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            $"Remove {Name} API key",
            $"The stored key for {Name} will be deleted. Cached models are kept but cannot be used until a new key is added.",
            "Remove").ConfigureAwait(true);

        if (!confirmed)
        {
            return;
        }

        await _registry.DeleteApiKeyAsync(Id).ConfigureAwait(true);

        HasApiKey = false;
        ConnectionState = ConnectionState.NotConfigured;
        StatusMessage = null;
        TechnicalDetails = null;
    }

    [RelayCommand]
    private void BeginEditApiKey()
    {
        ApiKeyInput = string.Empty;
        IsEditingApiKey = true;
    }

    [RelayCommand]
    private void CancelEditApiKey()
    {
        ApiKeyInput = string.Empty;
        IsEditingApiKey = false;
    }

    [RelayCommand(CanExecute = nameof(CanUseProvider))]
    private async Task TestConnectionAsync()
    {
        IsBusy = true;
        ConnectionState = ConnectionState.Testing;
        StatusMessage = "Testing…";
        TechnicalDetails = null;

        try
        {
            var result = await _registry.TestConnectionAsync(Id).ConfigureAwait(true);

            ConnectionState = result.Success ? ConnectionState.Connected : ConnectionState.Failed;
            StatusMessage = result.Message;
            TechnicalDetails = result.TechnicalDetails;

            if (result.Success)
            {
                await RefreshModelsAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connection test for {Provider} failed unexpectedly.", Id);

            ConnectionState = ConnectionState.Failed;
            StatusMessage = "The connection test could not be completed.";
            TechnicalDetails = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseProvider))]
    private async Task RefreshModelsAsync()
    {
        IsBusy = true;

        try
        {
            var models = await _registry.RefreshModelsAsync(Id).ConfigureAwait(true);

            CachedModelCount = models.Count;
            ModelsRefreshedAt = DateTimeOffset.UtcNow;
            ConnectionState = ConnectionState.Connected;
            StatusMessage = $"{models.Count} models available.";
            TechnicalDetails = null;
        }
        catch (Domain.Models.AIProviderException ex)
        {
            ConnectionState = ConnectionState.Failed;
            StatusMessage = ex.UserMessage;
            TechnicalDetails = ex.TechnicalDetails;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refreshing models for {Provider} failed.", Id);

            ConnectionState = ConnectionState.Failed;
            StatusMessage = "The model list could not be refreshed.";
            TechnicalDetails = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenApiKeyPage()
    {
        if (string.IsNullOrEmpty(ApiKeyUrl))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = ApiKeyUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the API key page for {Provider}.", Id);
        }
    }

    partial void OnIsEnabledChanged(bool value) => _ = _registry.SetEnabledAsync(Id, value);

    partial void OnHasApiKeyChanged(bool value)
    {
        OnPropertyChanged(nameof(ApiKeyDisplay));
        OnPropertyChanged(nameof(CanUseProvider));
        TestConnectionCommand.NotifyCanExecuteChanged();
        RefreshModelsCommand.NotifyCanExecuteChanged();
    }

    partial void OnConnectionStateChanged(ConnectionState value) => OnPropertyChanged(nameof(StatusText));

    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(StatusText));

    partial void OnCachedModelCountChanged(int value) => OnPropertyChanged(nameof(ModelSummary));

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSaveApiKey));
        OnPropertyChanged(nameof(CanUseProvider));
    }

    partial void OnApiKeyInputChanged(string value) => OnPropertyChanged(nameof(CanSaveApiKey));
}
