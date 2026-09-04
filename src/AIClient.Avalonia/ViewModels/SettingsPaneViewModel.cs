using System.Collections.ObjectModel;
using AIClient.Application.Configuration;
using AIClient.Application.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIClient.Avalonia.ViewModels;

/// <summary>
/// The settings surface of the first Avalonia phase: theme and provider keys.
/// </summary>
/// <remarks>
/// Reads and writes through <see cref="ISettingsService"/> and <see cref="IProviderRegistry"/>
/// only - the same two seams the WPF settings pane uses - so keys live in the same DPAPI-
/// protected store whichever shell saved them. Everything else (agent budgets, canvas caps,
/// workspace root) is Phase 5 parity work on the same services.
/// </remarks>
public sealed partial class SettingsPaneViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IProviderRegistry _providers;

    public SettingsPaneViewModel(ISettingsService settings, IProviderRegistry providers)
    {
        _settings = settings;
        _providers = providers;

        Themes =
        [
            new ThemeOption(ThemeMode.System, "Follow the system"),
            new ThemeOption(ThemeMode.Light, "Light"),
            new ThemeOption(ThemeMode.Dark, "Dark"),
        ];

        _selectedTheme = Themes.FirstOrDefault(option =>
            option.Mode == settings.Current.Appearance.Theme) ?? Themes[0];
    }

    public IReadOnlyList<ThemeOption> Themes { get; }

    [ObservableProperty]
    private ThemeOption _selectedTheme;

    public ObservableCollection<ProviderRow> Providers { get; } = [];

    [ObservableProperty]
    private ProviderRow? _selectedProvider;

    /// <summary>The key being typed. Never read back from the store once saved.</summary>
    [ObservableProperty]
    private string _apiKeyInput = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>One line of feedback for a save or a test. Cleared by the next action.</summary>
    [ObservableProperty]
    private string? _notice;

    /// <summary>Applies the theme at once and persists the choice.</summary>
    partial void OnSelectedThemeChanged(ThemeOption value)
    {
        if (value.Mode == _settings.Current.Appearance.Theme)
        {
            return;
        }

        _ = ApplyThemeAsync(value);
    }

    private async Task ApplyThemeAsync(ThemeOption value)
    {
        await _settings.UpdateAsync<AppearanceSettings>(appearance => appearance.Theme = value.Mode);

        App.ApplyThemeFromSettings();
    }

    /// <summary>Loads the provider rows. Called when the pane opens.</summary>
    public async Task ActivateAsync() => await RefreshProvidersAsync();

    [RelayCommand]
    private async Task RefreshProvidersAsync()
    {
        try
        {
            var providers = await _providers.GetProvidersAsync();
            var previous = SelectedProvider?.Id;

            Providers.Clear();

            foreach (var provider in providers)
            {
                Providers.Add(new ProviderRow(provider));
            }

            SelectedProvider =
                Providers.FirstOrDefault(p => p.Id == previous) ??
                Providers.FirstOrDefault();
        }
        catch (Exception)
        {
            Notice = "The provider list could not be read.";
        }
    }

    partial void OnSelectedProviderChanged(ProviderRow? value)
    {
        ApiKeyInput = string.Empty;
        Notice = null;
    }

    [RelayCommand]
    private async Task SaveKeyAsync()
    {
        if (SelectedProvider is not { } provider)
        {
            return;
        }

        IsBusy = true;
        Notice = null;

        try
        {
            await _providers.SetApiKeyAsync(provider.Id, ApiKeyInput.Trim());

            ApiKeyInput = string.Empty;
            await RefreshProvidersAsync();

            Notice = $"Key saved for {provider.Name}. Refreshing the model catalogue…";

            await _providers.RefreshModelsAsync(provider.Id);

            Notice = $"Key saved for {provider.Name}. Models refreshed.";
        }
        catch (Exception)
        {
            Notice = "The key could not be saved.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveKeyAsync()
    {
        if (SelectedProvider is not { } provider || !provider.HasApiKey)
        {
            return;
        }

        IsBusy = true;

        try
        {
            await _providers.DeleteApiKeyAsync(provider.Id);
            await RefreshProvidersAsync();
            Notice = $"Key removed for {provider.Name}.";
        }
        catch (Exception)
        {
            Notice = "The key could not be removed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (SelectedProvider is not { } provider)
        {
            return;
        }

        IsBusy = true;
        Notice = null;

        try
        {
            var result = await _providers.TestConnectionAsync(provider.Id);

            Notice = result.Success
                ? $"{provider.Name}: {result.Message}"
                : $"{provider.Name}: {result.Message}";
        }
        catch (Exception)
        {
            Notice = "The connection could not be tested.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public sealed record ThemeOption(ThemeMode Mode, string Label);

    public sealed partial class ProviderRow : ObservableObject
    {
        public ProviderRow(ProviderInfo info)
        {
            Id = info.Id;
            _name = info.Name;
            _state = Describe(info);
            HasApiKey = info.HasApiKey;
        }

        public string Id { get; }

        [ObservableProperty]
        private string _name;

        [ObservableProperty]
        private string _state;

        public bool HasApiKey { get; }

        private static string Describe(ProviderInfo info)
        {
            if (!info.IsEnabled)
            {
                return "Disabled";
            }

            if (!info.HasApiKey)
            {
                return "No key";
            }

            return info.ConnectionState switch
            {
                Domain.Enums.ConnectionState.Connected => $"Connected · {info.CachedModelCount} models",
                Domain.Enums.ConnectionState.Failed => $"Not reachable · {info.StatusMessage}",
                _ => "Key stored",
            };
        }
    }
}
