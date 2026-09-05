using AIClient.Application.Configuration;

namespace AIClient.App.Services;

/// <summary>
/// Chooses which language the interface is written in and swaps the string dictionary
/// behind <see cref="System.Windows.Application.Current"/> accordingly, the same way the
/// theme service swaps colours. Consumers read strings through <c>Localization.T</c> or
/// <c>{DynamicResource S.…}</c> and refresh when <see cref="LanguageChanged"/> fires.
/// </summary>
public interface ILocalizationService
{
    /// <summary>Language currently applied.</summary>
    UiLanguage Current { get; }

    /// <summary>Raised after the dictionary has been swapped and the setting saved.</summary>
    event EventHandler? LanguageChanged;

    /// <summary>Looks a string up in the active dictionary; the key itself is the fallback.</summary>
    string T(string key);

    /// <summary>Applies the language stored in settings at startup.</summary>
    void Initialize();

    /// <summary>Switches the language, persists it and raises <see cref="LanguageChanged"/>.</summary>
    Task SetLanguageAsync(UiLanguage language);
}
