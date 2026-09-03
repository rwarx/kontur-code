using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using AIClient.App.Services;
using AIClient.Application.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIClient.App.ViewModels;

/// <summary>
/// The model selector: every model from every configured provider, grouped and searchable.
/// </summary>
/// <remarks>
/// OpenRouter alone lists several hundred models, so a flat combo box is unusable - hence
/// grouping by provider and a filter box. Reads come from the registry's cache, never from
/// the network, so opening the picker is instant and works offline.
/// </remarks>
public sealed partial class ModelPickerViewModel : ObservableObject
{
    private readonly IProviderRegistry _registry;
    private readonly ISettingsService _settings;
    private readonly ILogger<ModelPickerViewModel> _logger;
    private readonly ObservableCollection<ModelInfo> _allModels = [];

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    private ModelInfo? _selectedModel;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Shown in place of the list when no provider has a key yet.</summary>
    [ObservableProperty]
    private bool _hasNoModels;

    public ModelPickerViewModel(
        IProviderRegistry registry,
        ISettingsService settings,
        ILogger<ModelPickerViewModel> logger)
    {
        _registry = registry;
        _settings = settings;
        _logger = logger;

        Models = CollectionViewSource.GetDefaultView(_allModels);
        Models.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ModelInfo.ProviderName)));
        Models.Filter = MatchesFilter;

        _registry.ModelsChanged += OnModelsChanged;
    }

    /// <summary>Grouped, filtered view bound by the picker.</summary>
    public ICollectionView Models { get; }

    /// <summary>Raised when the user picks a model, so the chat pane can adopt it.</summary>
    public event EventHandler<ModelInfo>? ModelSelected;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;

        try
        {
            var models = await _registry.GetAllModelsAsync(cancellationToken).ConfigureAwait(true);

            _allModels.Clear();
            foreach (var model in models)
            {
                _allModels.Add(model);
            }

            HasNoModels = _allModels.Count == 0;

            RestoreSelection();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Restores the model from settings, or from the conversation being opened.
    /// </summary>
    /// <remarks>
    /// A model can vanish between sessions - removed upstream, or the provider disabled.
    /// Falling back to the first available model is better than leaving the picker empty
    /// and the Send button dead with no explanation.
    /// </remarks>
    public void SelectModel(string? providerId, string? modelId)
    {
        if (providerId is not null && modelId is not null)
        {
            var match = _allModels.FirstOrDefault(m =>
                m.ProviderId == providerId && m.ModelId == modelId);

            if (match is not null)
            {
                SelectedModel = match;
                return;
            }

            _logger.LogInformation(
                "Model {Model} from {Provider} is no longer available; falling back.",
                modelId,
                providerId);
        }

        RestoreSelection();
    }

    private void RestoreSelection()
    {
        if (SelectedModel is not null && _allModels.Contains(SelectedModel))
        {
            return;
        }

        var chat = _settings.Current.Chat;

        SelectedModel =
            _allModels.FirstOrDefault(m => m.ProviderId == chat.DefaultProviderId && m.ModelId == chat.DefaultModelId)
            ?? _allModels.FirstOrDefault();
    }

    [RelayCommand]
    private void ClearFilter() => Filter = string.Empty;

    private bool MatchesFilter(object item)
    {
        if (item is not ModelInfo model)
        {
            return false;
        }

        var query = Filter.Trim();

        if (query.Length == 0)
        {
            return true;
        }

        // Both name and id: users search for "sonnet" as often as "anthropic/claude".
        return model.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || model.ModelId.Contains(query, StringComparison.OrdinalIgnoreCase)
            || model.ProviderName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private async void OnModelsChanged(object? sender, string providerId)
    {
        // async void is unavoidable on an event handler; the body cannot throw because
        // everything is inside the try.
        try
        {
            // The registry raises this from whichever thread finished refreshing, which is a
            // background one after a key is saved. _allModels sits behind a CollectionView,
            // and that throws when mutated off the dispatcher rather than coping, so the hop
            // has to happen here - out here, around LoadAsync, not inside it.
            await UiThread.RunAsync(() => LoadAsync()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not refresh the model list after {Provider} changed.", providerId);
        }
    }

    partial void OnFilterChanged(string value) => Models.Refresh();

    partial void OnSelectedModelChanged(ModelInfo? value)
    {
        if (value is not null)
        {
            ModelSelected?.Invoke(this, value);
        }
    }
}
