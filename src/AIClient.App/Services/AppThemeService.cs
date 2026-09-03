using System.Windows;
using System.Windows.Media;
using AIClient.Application.Configuration;
using AIClient.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

// .NET 10 introduced System.Windows.ThemeMode for WPF's own light/dark switch. It is not the
// same thing as ours - it has no System option and no persistence - so the alias keeps this
// file talking about the application's setting rather than the framework's.
using ThemeMode = AIClient.Application.Configuration.ThemeMode;

namespace AIClient.App.Services;

/// <summary>
/// Theme switching on top of WPF-UI's <see cref="ApplicationThemeManager"/>.
/// </summary>
/// <remarks>
/// System mode is a live subscription, not a one-off read at startup: Windows can flip to
/// dark at sunset while the app is open, and an app that only matched at launch would then
/// be the one bright window on the desktop.
/// </remarks>
public sealed class AppThemeService : IAppThemeService
{
    private readonly ISettingsService _settings;
    private readonly ILogger<AppThemeService> _logger;

    private bool _isFollowingSystem;

    public AppThemeService(ISettingsService settings, ILogger<AppThemeService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public ThemeMode EffectiveTheme =>
        ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light
            ? ThemeMode.Light
            : ThemeMode.Dark;

    public void Initialize() => Apply(_settings.Current.Appearance.Theme);

    public async Task SetThemeAsync(ThemeMode mode)
    {
        Apply(mode);

        await _settings.UpdateAsync<AppearanceSettings>(a => a.Theme = mode).ConfigureAwait(false);
    }

    public Task ToggleAsync() =>
        SetThemeAsync(EffectiveTheme == ThemeMode.Dark ? ThemeMode.Light : ThemeMode.Dark);

    private void Apply(ThemeMode mode)
    {
        var appearance = _settings.Current.Appearance;

        // Accent has to be set before the theme: WPF-UI derives its tint palette from the
        // accent when it builds the theme resources.
        ApplyAccent(appearance.AccentColor);

        // Null during a unit test, and briefly at startup if this is called before the shell
        // is constructed. The theme still applies; only the per-window backdrop is skipped.
        var shell = System.Windows.Application.Current?.MainWindow;

        if (mode == ThemeMode.System)
        {
            if (shell is not null)
            {
                SystemThemeWatcher.Watch(
                    shell,
                    ResolveBackdrop(appearance.UseMicaBackdrop),
                    updateAccents: appearance.AccentColor is null);

                _isFollowingSystem = true;
            }

            ApplicationThemeManager.ApplySystemTheme();
        }
        else
        {
            if (_isFollowingSystem && shell is not null)
            {
                SystemThemeWatcher.UnWatch(shell);
                _isFollowingSystem = false;
            }

            ApplicationThemeManager.Apply(
                mode == ThemeMode.Light ? ApplicationTheme.Light : ApplicationTheme.Dark,
                ResolveBackdrop(appearance.UseMicaBackdrop),
                updateAccent: appearance.AccentColor is null);
        }

        _logger.LogInformation("Theme set to {Mode} (rendering {Effective}).", mode, EffectiveTheme);
    }

    private void ApplyAccent(string? accentColor)
    {
        if (string.IsNullOrWhiteSpace(accentColor))
        {
            return;
        }

        try
        {
            if (ColorConverter.ConvertFromString(accentColor) is Color color)
            {
                ApplicationAccentColorManager.Apply(color, ApplicationThemeManager.GetAppTheme());
            }
        }
        catch (FormatException)
        {
            // A malformed colour in settings falls back to the Windows accent rather than
            // stopping the theme from being applied at all.
            _logger.LogWarning("Accent colour '{Accent}' is not a valid colour and was ignored.", accentColor);
        }
    }

    /// <summary>
    /// Mica needs Windows 11 build 22000+. WPF-UI degrades gracefully, but asking for
    /// <see cref="WindowBackdropType.None"/> on Windows 10 avoids a pointless composition pass.
    /// </summary>
    private static WindowBackdropType ResolveBackdrop(bool useMica) =>
        useMica && Environment.OSVersion.Version.Build >= 22000
            ? WindowBackdropType.Mica
            : WindowBackdropType.None;
}
