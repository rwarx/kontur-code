using System.IO;
using System.Windows;
using System.Windows.Threading;
using AIClient.App.Infrastructure.Logging;
using AIClient.App.Services;
using AIClient.App.ViewModels;
using AIClient.App.Views;
using AIClient.Application.Configuration;
using AIClient.Application.Interfaces;
using AIClient.Infrastructure;
using AIClient.Infrastructure.Database;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AIClient.App;

/// <summary>
/// Application entry point and composition root.
/// </summary>
/// <remarks>
/// Startup order matters and is deliberate: build the host, apply migrations, load settings,
/// apply the theme, then show a window. Showing the shell before the database is ready would
/// mean every view had to cope with a half-initialised application, which is a cost paid on
/// every screen for the sake of a few hundred milliseconds at launch.
/// </remarks>
public partial class App : System.Windows.Application
{
    private IHost? _host;

    /// <summary>
    /// Service locator of last resort, for the handful of places WPF constructs an object
    /// itself. Views use it to obtain their ViewModel; nothing else should reach for it.
    /// </summary>
    public static IServiceProvider Services =>
        ((App)Current)._host?.Services
        ?? throw new InvalidOperationException("The application host has not been started.");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // An unhandled exception on the dispatcher would otherwise close the app with a
        // Windows crash dialog and no explanation.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            _host = BuildHost();
            await _host.StartAsync().ConfigureAwait(true);

            var logger = _host.Services.GetRequiredService<ILogger<App>>();
            logger.LogInformation("Application starting.");

            // Migrations first: everything below reads from the database.
            await _host.Services.GetRequiredService<DatabaseInitializer>()
                .InitializeAsync().ConfigureAwait(true);

            var settings = _host.Services.GetRequiredService<ISettingsService>();
            await settings.LoadAsync().ConfigureAwait(true);

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;

            // After the window exists, before it is shown: the theme service attaches the
            // backdrop and the system-theme watcher to it, and applying the theme first
            // would show a light frame for one frame on a dark system.
            _host.Services.GetRequiredService<IAppThemeService>().Initialize();

            window.Show();

            logger.LogInformation("Application started.");
        }
        catch (Exception ex)
        {
            // A failure here means the app cannot run at all - a database that cannot be
            // opened, most likely. Say so plainly instead of vanishing.
            ShowFatalError(ex);
            Shutdown(1);
        }
    }

    private static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Configuration
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("AICLIENT_");

        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddAppServices();

        // The file sink needs paths and settings, which only exist once Infrastructure is
        // registered - hence a scratch container rather than a hardcoded path.
        using var probe = builder.Services.BuildServiceProvider();
        var paths = probe.GetRequiredService<IAppPaths>();

        builder.Logging.ClearProviders();
        builder.Logging.AddFileLogger(paths, new StorageSettings());

        // EF Core logs every statement it executes at Information, which on a normal launch
        // buries the application's own diagnostics under a few hundred lines of SQL. Section
        // 26 also asks that user content not be logged without cause, and a query's SQL is one
        // step away from it - so the noisy categories are raised and the app's own are not.
        // DatabaseInitializer already reports which migrations it applies, so nothing of value
        // is lost. Sensitive data logging stays off, which is what keeps parameters as '?'.
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Extensions.Hosting", LogLevel.Warning);
#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            try
            {
                // Bounded: a shutdown that hangs is worse than one that loses a log line.
                await _host.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            }
            catch (Exception)
            {
                // Nothing useful can be done at this point and nowhere left to report it.
            }

            _host.Dispose();
            _host = null;
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        TryLog(e.Exception, "Unhandled exception on the UI thread.");

        // Handled so a single bad interaction does not destroy the session; the user keeps
        // their conversation and can retry or close cleanly.
        e.Handled = true;

        System.Windows.MessageBox.Show(
            $"Something went wrong.\n\n{e.Exception.Message}",
            "AI Client",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        TryLog(e.ExceptionObject as Exception, "Unhandled exception outside the UI thread.");

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        TryLog(e.Exception, "Unobserved task exception.");

        // Observed now, so the finalizer does not tear the process down.
        e.SetObserved();
    }

    private void TryLog(Exception? exception, string message)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            _host?.Services.GetService<ILogger<App>>()?.LogError(exception, "{Message}", message);
        }
        catch (Exception)
        {
            // Logging the failure to log helps nobody.
        }
    }

    /// <summary>Last-resort reporting for a failure that happened before the UI existed.</summary>
    private static void ShowFatalError(Exception exception)
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AIClient",
            "logs");

        System.Windows.MessageBox.Show(
            $"AI Client could not start.\n\n{exception.Message}\n\nLogs: {logPath}",
            "AI Client",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
