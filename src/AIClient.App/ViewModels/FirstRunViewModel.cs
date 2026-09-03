using AIClient.Application.Configuration;
using AIClient.Application.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIClient.App.ViewModels;

/// <summary>Where the first-run wizard has got to.</summary>
public enum FirstRunStep
{
    Welcome = 0,
    ConnectProvider = 1,
    ChooseModel = 2,
}

/// <summary>
/// The first-run wizard: Welcome, connect a provider, choose a default model (section 32).
/// </summary>
/// <remarks>
/// The provider rows are the same <see cref="ProviderSettingsViewModel"/> instances the
/// Settings pane uses, borrowed rather than duplicated. Two sets of rows over one registry
/// would mean the wizard could hold a stale "no key" state for a provider the user has just
/// configured on the other screen.
///
/// Every step is skippable. A wizard that will not let go until a key is entered is a wall
/// in front of an app the user may only want to look at.
/// </remarks>
public sealed partial class FirstRunViewModel : ObservableObject
{
    private readonly SettingsViewModel _settingsViewModel;
    private readonly ModelPickerViewModel _modelPicker;
    private readonly ISettingsService _settings;
    private readonly ILogger<FirstRunViewModel> _logger;

    [ObservableProperty]
    private FirstRunStep _step = FirstRunStep.Welcome;

    public FirstRunViewModel(
        SettingsViewModel settingsViewModel,
        ModelPickerViewModel modelPicker,
        ISettingsService settings,
        ILogger<FirstRunViewModel> logger)
    {
        _settingsViewModel = settingsViewModel;
        _modelPicker = modelPicker;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>The provider rows from Settings, so key entry behaves identically in both places.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ProviderSettingsViewModel> Providers =>
        _settingsViewModel.Providers;

    public ModelPickerViewModel ModelPicker => _modelPicker;

    public bool IsWelcome => Step == FirstRunStep.Welcome;
    public bool IsConnect => Step == FirstRunStep.ConnectProvider;
    public bool IsChooseModel => Step == FirstRunStep.ChooseModel;

    /// <summary>One-based, for "Step 2 of 3".</summary>
    public int StepNumber => (int)Step + 1;

    public bool CanGoBack => Step != FirstRunStep.Welcome;

    /// <summary>True once any provider has a key, which is what makes the model step useful.</summary>
    public bool HasConfiguredProvider => Providers.Any(p => p.HasApiKey);

    public string PrimaryButtonText => Step == FirstRunStep.ChooseModel ? "Start chatting" : "Continue";

    /// <summary>Raised when the wizard is done or skipped, so the shell can dismiss it.</summary>
    public event EventHandler? Finished;

    [RelayCommand]
    private async Task NextAsync()
    {
        switch (Step)
        {
            case FirstRunStep.Welcome:
                Step = FirstRunStep.ConnectProvider;
                break;

            case FirstRunStep.ConnectProvider:
                // Whatever keys were entered have already refreshed the catalogue; reload so
                // the model step opens with a populated list rather than an empty one.
                await _modelPicker.LoadAsync().ConfigureAwait(true);
                Step = FirstRunStep.ChooseModel;
                break;

            case FirstRunStep.ChooseModel:
                await FinishAsync().ConfigureAwait(true);
                break;
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (Step != FirstRunStep.Welcome)
        {
            Step -= 1;
        }
    }

    /// <summary>Leaves the wizard without configuring anything. The app still works, with no models.</summary>
    [RelayCommand]
    private async Task SkipAsync() => await FinishAsync().ConfigureAwait(true);

    private async Task FinishAsync()
    {
        try
        {
            // Remember the chosen model as the default so the next new chat starts on it.
            if (_modelPicker.SelectedModel is { } model)
            {
                await _settings.UpdateAsync<ChatSettings>(chat =>
                {
                    chat.DefaultProviderId = model.ProviderId;
                    chat.DefaultModelId = model.ModelId;
                }).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            // Not worth blocking the user on: the picker keeps the selection for this session
            // either way, and Settings can set it later.
            _logger.LogWarning(ex, "Could not save the default model chosen during first run.");
        }

        Finished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-evaluates the step-dependent flags the view binds to.</summary>
    partial void OnStepChanged(FirstRunStep value)
    {
        OnPropertyChanged(nameof(IsWelcome));
        OnPropertyChanged(nameof(IsConnect));
        OnPropertyChanged(nameof(IsChooseModel));
        OnPropertyChanged(nameof(StepNumber));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(HasConfiguredProvider));
        OnPropertyChanged(nameof(PrimaryButtonText));
    }
}
