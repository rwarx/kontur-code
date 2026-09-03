using AIClient.Application.Configuration;

namespace AIClient.App.Services;

/// <summary>
/// Applies the appearance settings to the running application.
/// </summary>
/// <remarks>
/// An interface rather than a static helper so ViewModels can ask for a theme change
/// without referencing WPF-UI, and so a test can substitute a no-op.
/// </remarks>
public interface IAppThemeService
{
    /// <summary>Applies the persisted theme and starts following Windows when set to System.</summary>
    void Initialize();

    /// <summary>Switches theme and persists the choice.</summary>
    Task SetThemeAsync(ThemeMode mode);

    /// <summary>The theme currently on screen. Never <see cref="ThemeMode.System"/>.</summary>
    ThemeMode EffectiveTheme { get; }

    /// <summary>Cycles Light and Dark. Used by the command palette's Toggle Theme entry.</summary>
    Task ToggleAsync();
}
