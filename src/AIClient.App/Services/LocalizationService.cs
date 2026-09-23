using System.Windows;
using AIClient.Application.Configuration;
using AIClient.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace AIClient.App.Services;

/// <summary>
/// Reads strings out of the merged string dictionary so every caller sees the same
/// translations whether it is markup (<c>{DynamicResource}</c>) or code.
/// </summary>
public static class Localization
{
    /// <summary>Looks a string up in the active dictionary; the key itself is the fallback.</summary>
    public static string T(string key)
    {
        var resources = System.Windows.Application.Current?.Resources;
        return resources is not null && resources[key] is string value ? value : key;
    }

    /// <summary>Convenience for formatted strings: <see cref="T"/> plus <see cref="string.Format"/>.</summary>
    public static string T(string key, params object[] args)
    {
        var pattern = T(key);
        return args.Length == 0 ? pattern : string.Format(CultureInfoCurrent, pattern, args);
    }

    /// <summary>Current culture of the UI thread, so numbers and plurals format as usual.</summary>
    private static System.IFormatProvider CultureInfoCurrent => System.Globalization.CultureInfo.CurrentUICulture;
}

/// <inheritdoc/>
public sealed class LocalizationService : ILocalizationService
{
    private const string DictionaryUri = "pack://application:,,,/Localization/Strings.{0}.xaml";

    private readonly ISettingsService _settings;
    private readonly ILogger<LocalizationService> _logger;
    private ResourceDictionary? _current;

    public LocalizationService(ISettingsService settings, ILogger<LocalizationService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc/>
    public UiLanguage Current { get; private set; } = UiLanguage.English;

    /// <inheritdoc/>
    public event EventHandler? LanguageChanged;

    /// <inheritdoc/>
    public string T(string key) => Localization.T(key);

    /// <inheritdoc/>
    public void Initialize()
    {
        Apply(_settings.Current.General.Language, logDrift: true);
    }

    /// <inheritdoc/>
    public async Task SetLanguageAsync(UiLanguage language)
    {
        if (language == Current)
        {
            return;
        }

        Apply(language, logDrift: false);

        try
        {
            await _settings.UpdateAsync<GeneralSettings>(g => g.Language = language);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The language could not be saved.");
        }

        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Apply(UiLanguage language, bool logDrift)
    {
        var dictionary = Load(language);
        if (dictionary is null)
        {
            return;
        }

        var app = System.Windows.Application.Current;
        if (app is not null)
        {
            if (_current is not null)
            {
                app.Resources.MergedDictionaries.Remove(_current);
            }

            // Inserted at the top so a drifted key in another dictionary can never shadow it.
            app.Resources.MergedDictionaries.Insert(0, dictionary);
        }

        _current = dictionary;
        Current = language;

        if (logDrift)
        {
            LogDrift(language, dictionary);
        }
    }

    private ResourceDictionary? Load(UiLanguage language)
    {
        try
        {
            return new ResourceDictionary
            {
                Source = new Uri(string.Format(DictionaryUri, CodeOf(language))),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The string table for {Language} could not be loaded.", language);
            return null;
        }
    }

    private void LogDrift(UiLanguage language, ResourceDictionary dictionary)
    {
        if (language == UiLanguage.English || System.Windows.Application.Current?.Resources is not { } resources)
        {
            return;
        }

        var canonical = resources.Keys.OfType<string>().Where(k => k.StartsWith("S.", StringComparison.Ordinal)).ToHashSet();
        var offered = dictionary.Keys.OfType<string>().ToHashSet();

        var missing = canonical.Where(k => !offered.Contains(k)).OrderBy(k => k).ToList();
        var extra = offered.Where(k => !canonical.Contains(k)).OrderBy(k => k).ToList();

        if (missing.Count > 0 || extra.Count > 0)
        {
            _logger.LogWarning(
                "The {Language} string table drifted from English: missing {Missing}, unexpected {Extra}.",
                language,
                string.Join(", ", missing),
                string.Join(", ", extra));
        }
    }

    private static string CodeOf(UiLanguage language) => language switch
    {
        UiLanguage.Russian => "ru",
        UiLanguage.German => "de",
        _ => "en",
    };
}
