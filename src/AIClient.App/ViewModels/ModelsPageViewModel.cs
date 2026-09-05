using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AIClient.App.Controls;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Domain.Enums;
using AIClient.Domain.Graph;
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
            Providers.Add(new ProviderRowViewModel(info));
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
public sealed partial class ProviderRowViewModel : ObservableObject
{
    [ObservableProperty]
    private ProviderRowState _state;

    [ObservableProperty]
    private string? _statusMessage;

    public ProviderRowViewModel(ProviderInfo info)
    {
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

    public bool HasApiKey { get; }

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
