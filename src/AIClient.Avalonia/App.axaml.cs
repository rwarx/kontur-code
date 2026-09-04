using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AIClient.Avalonia.Services;
using AIClient.Avalonia.ViewModels;
using AIClient.Avalonia.Views;
using AIClient.Application.Configuration;
using AIClient.Application.Interfaces;
using AIClient.Infrastructure;
using AIClient.Infrastructure.Database;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AIClient.Avalonia;

/// <summary>
/// Composition root for the Avalonia shell. Startup order mirrors the WPF app on purpose:
/// container, migrations, settings, then the window - everything below reads the database,
/// so showing a shell before it is ready would put "cope with a half-initialised app" into
/// every view. <c>global::</c> below because the sibling namespace <c>AIClient.Application</c>
/// would otherwise shadow the Avalonia <c>Application</c> type.
/// </summary>
public class App : global::Avalonia.Application
{
    private ServiceProvider? _services;

    /// <summary>
    /// Service locator of last resort, for the places Avalonia constructs an object itself.
    /// Views use it to obtain their view model; nothing else reaches for it.
    /// </summary>
    public static IServiceProvider Services =>
        ((App?)global::Avalonia.Application.Current)?._services
        ?? throw new InvalidOperationException("The application host has not been started.");

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Keep the shutdown in the framework's hands so SaveAllAsync can run on close.
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.ShutdownRequested += OnShutdownRequested;

            try
            {
                _services = BuildServices();

                await _services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
                await _services.GetRequiredService<ISettingsService>().LoadAsync();

                ApplyThemeFromSettings();

                var window = _services.GetRequiredService<MainWindow>();
                window.Closed += OnMainWindowClosed;
                desktop.MainWindow = window;
                window.Show();
            }
            catch (Exception ex)
            {
                // A failure here means the app cannot run at all - a database that cannot be
                // opened, most likely. Say so plainly instead of vanishing.
                Console.Error.WriteLine(ex);
                _services?.Dispose();
                desktop.Shutdown(1);
                return;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Applies the persisted theme. Called at startup and by the settings pane.</summary>
    public static void ApplyThemeFromSettings()
    {
        var mode = Services.GetRequiredService<ISettingsService>().Current.Appearance.Theme;
        Current!.RequestedThemeVariant = mode switch
        {
            ThemeMode.Light => global::Avalonia.Styling.ThemeVariant.Light,
            ThemeMode.Dark => global::Avalonia.Styling.ThemeVariant.Dark,
            _ => global::Avalonia.Styling.ThemeVariant.Default,
        };
    }

    private static ServiceProvider BuildServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("AICLIENT_")
            .Build();

        var services = new ServiceCollection();

        services.AddInfrastructure(configuration);
        services.AddAppServices();

        // EF Core logging drowns the application's own diagnostics; the WPF shell filters it
        // the same way. A full file logger arrives with the settings polish pass.
#if DEBUG
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        });
#else
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Warning);
            logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        });
#endif

        return services.BuildServiceProvider();
    }

    private async void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (_services is null)
        {
            return;
        }

        try
        {
            await _services.GetRequiredService<ISettingsService>().SaveAllAsync();
        }
        catch (Exception)
        {
            // A shutdown that hangs is worse than one that loses a settings row.
        }

        // Camera state is written by a debounced save; give it a moment to land before the
        // container goes away.
        await Task.Delay(900);
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        // Nothing to veto yet; the hook exists so a future unsaved-changes check has its place.
    }
}
