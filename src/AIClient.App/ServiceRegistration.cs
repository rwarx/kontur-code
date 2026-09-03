using AIClient.App.Services;
using AIClient.App.ViewModels;
using AIClient.App.Views;
using AIClient.Application.Markdown;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui;

namespace AIClient.App;

/// <summary>
/// Registers the presentation layer: ViewModels, Views and the UI-only services.
/// </summary>
/// <remarks>
/// Lifetimes are deliberate. The shell and its panes are singletons because there is one of
/// each and their state - the open conversation, the loaded session list - has to survive
/// navigation. Dialogs are transient.
/// </remarks>
public static class ServiceRegistration
{
    public static IServiceCollection AddAppServices(this IServiceCollection services)
    {
        // WPF-UI infrastructure.
        services.AddSingleton<IContentDialogService, ContentDialogService>();
        services.AddSingleton<ISnackbarService, SnackbarService>();

        services.AddSingleton<IAppThemeService, AppThemeService>();
        services.AddSingleton<IDialogService, DialogService>();

        // One parser for the whole app: building the Markdig pipeline is the expensive part
        // and the result is stateless, so a per-message instance would only waste work.
        services.AddSingleton<MarkdownParser>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<ChatViewModel>();
        services.AddSingleton<SessionListViewModel>();
        services.AddSingleton<ModelPickerViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<CommandPaletteViewModel>();
        services.AddSingleton<FirstRunViewModel>();

        services.AddSingleton<MainWindow>();

        return services;
    }
}
