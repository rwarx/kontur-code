using System.Collections.ObjectModel;
using System.Windows.Input;
using AIClient.App.Behaviors;
using AIClient.App.Controls;
using AIClient.App.Services;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Domain.Enums;
using AIClient.Domain.Graph;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIClient.App.ViewModels;

/// <summary>
/// The models surface: the providers the product speaks to and the catalogue each offers,
/// compact enough to read at a glance.
/// </summary>
/// <remarks>
/// <para>
/// The list is the picker's own collection - one catalogue, one selection, one
/// <c>ModelSelected</c> event - so choosing a model from this page and from the top bar
/// are the same action with two entrances. The providers panel adds only what the picker
/// deliberately does not know: connection state and key presence, which are account
/// facts rather than model facts.
/// </para>
/// <para>
/// Providers refresh their own row; models refresh per provider through the registry's
/// <c>ModelsChanged</c> event, which arrives on a background thread and is marshalled
/// here rather than in every subscriber.
/// </para>
/// </remarks>
public sealed partial class ModelsPageViewModel : ObservableObject
{
    private readonly IProviderRegistry _providers;
    private readonly ModelPickerViewModel _picker;
    private readonly ILogger<ModelsPageViewModel> _logger;

    [ObservableProperty]
    private bool _isRefreshing;

    public ObservableCollection<ProviderRowViewModel> Providers { get; } = [];

    /// <summary>The picker's grouped, filterable catalogue - the same collection the top-bar popup shows.</summary>
    public ModelPickerViewModel Picker => _picker;

    public ModelsPageViewModel(
        IProviderRegistry providers,
        ModelPickerViewModel picker,
        ILogger<ModelsPageViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(picker);
        ArgumentNullException.ThrowIfNull(logger);

        _providers = providers;
        _picker = picker;
        _logger = logger;

        _providers.ModelsChanged += OnModelsChanged;
    }

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var infos = await _providers.GetProvidersAsync(cancellationToken).ConfigureAwait(true);

        Providers.Clear();

        foreach (var info in infos.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            Providers.Add(new ProviderRowViewModel(info, _providers, _logger));
        }
    }

    private void OnModelsChanged(object? sender, string providerId)
    {
        // Raised on a worker thread; the rows are UI state, so the reload hops.
        AIClient.App.Services.UiThread.Post(async () =>
        {
            try
            {
                await LoadCommand.ExecuteAsync(null).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Provider row reload after a catalogue change failed.");
            }
        });
    }

    [RelayCommand]
    private async Task RefreshProviderAsync(ProviderRowViewModel? row, CancellationToken cancellationToken)
    {
        if (row is null)
        {
            return;
        }

        row.State = ProviderRowState.Updating;

        _ = await _providers.RefreshModelsAsync(row.Id, cancellationToken).ConfigureAwait(true);

        row.State = ProviderRowState.Connected;

        await LoadAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task TestProviderAsync(ProviderRowViewModel? row, CancellationToken cancellationToken)
    {
        if (row is null)
        {
            return;
        }

        row.State = ProviderRowState.Testing;

        var result = await _providers.TestConnectionAsync(row.Id, cancellationToken).ConfigureAwait(true);

        row.State = result.Success ? ProviderRowState.Connected : ProviderRowState.Failed;
        row.StatusMessage = result.Message;
    }
}

/// <summary>One provider: its name, its state, its key, its catalogue size.</summary>
public sealed partial class ProviderRowViewModel : ObservableObject, IApiKeyEntry
{
    private readonly IProviderRegistry _registry;
    private readonly ILogger _logger;

    [ObservableProperty]
    private ProviderRowState _state;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveApiKeyCommand))]
    private bool _hasApiKey;

    [ObservableProperty]
    private bool _isEditingApiKey;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveApiKeyCommand))]
    private string _apiKeyInput = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveApiKeyCommand))]
    private bool _isBusy;

    public ProviderRowViewModel(
        ProviderInfo info,
        IProviderRegistry registry,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _logger = logger;

        Id = info.Id;
        Name = info.Name;
        HasApiKey = info.HasApiKey;
        CachedModelCount = info.CachedModelCount;
        State = info.ConnectionState switch
        {
            ConnectionState.Connected => ProviderRowState.Connected,
            ConnectionState.Testing => ProviderRowState.Testing,
            ConnectionState.Failed => ProviderRowState.Failed,
            ConnectionState.NotConfigured => ProviderRowState.MissingKey,
            _ => ProviderRowState.Unknown,
        };
        StatusMessage = info.StatusMessage;
    }

    public string Id { get; }

    public string Name { get; }

    public int CachedModelCount { get; }

    /// <summary>A stable glyph per provider; unknown providers get the generic cube.</summary>
    public IconKind Icon => Id.ToLowerInvariant() switch
    {
        "openrouter" => IconKind.Link,
        "nvidia" => IconKind.Package,
        _ => IconKind.Models,
    };

    public string CatalogueLabel => CachedModelCount == 0
        ? "no models cached"
        : $"{CachedModelCount} models";

    public string KeyLabel => HasApiKey ? "key saved" : "no key";

    public bool CanSaveApiKey => ApiKeyInput.Trim().Length > 0 && !IsBusy;

    ICommand IApiKeyEntry.SaveApiKeyCommand => SaveApiKeyCommand;

    ICommand IApiKeyEntry.CancelEditApiKeyCommand => CancelEditApiKeyCommand;

    [RelayCommand(CanExecute = nameof(CanSaveApiKey))]
    private async Task SaveApiKeyAsync()
    {
        var key = ApiKeyInput.Trim();

        ApiKeyInput = string.Empty;
        IsEditingApiKey = false;
        IsBusy = true;
        State = ProviderRowState.Testing;

        try
        {
            await _registry.SetApiKeyAsync(Id, key).ConfigureAwait(true);

            HasApiKey = true;
            StatusMessage = "Saved.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving the API key for {Provider} failed.", Id);
            StatusMessage = "Could not save the API key.";
            State = ProviderRowState.Failed;
        }
        finally
        {
            IsBusy = false;
        }

        // Probe the key straight away so the row turns green or red without another click.
        try
        {
            var result = await _registry.TestConnectionAsync(Id).ConfigureAwait(true);
            State = result.Success ? ProviderRowState.Connected : ProviderRowState.Failed;
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connection test after saving the API key for {Provider} failed.", Id);
            State = ProviderRowState.Failed;
            StatusMessage = "Connection test could not be completed.";
        }
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

    partial void OnHasApiKeyChanged(bool value) => OnPropertyChanged(nameof(KeyLabel));
}

public enum ProviderRowState
{
    Unknown,
    MissingKey,
    Testing,
    Updating,
    Connected,
    Failed,
}
