using AIClient.Avalonia.Services;
using AIClient.Avalonia.ViewModels;
using AIClient.Avalonia.ViewModels.Canvas;
using AIClient.Avalonia.Views;
using Microsoft.Extensions.DependencyInjection;

namespace AIClient.Avalonia;

/// <summary>
/// The UI-side registrations. Same shape as the WPF app's <c>AddAppServices</c>: everything
/// below Application is already registered by <c>AddInfrastructure</c>; this adds only what
/// a view layer needs - dialogs, the shell, and the screen view models as singletons, so
/// canvas state, selection and the open chat outlive a page switch.
/// </summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddAppServices(this IServiceCollection services)
    {
        services.AddSingleton<IDialogService, DialogService>();

        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<ChatPaneViewModel>();
        services.AddSingleton<SettingsPaneViewModel>();
        services.AddSingleton<CommandPaletteViewModel>();

        services.AddSingleton<CanvasCodeViewModel>();
        services.AddSingleton<CanvasViewModel>();
        services.AddSingleton<InspectorViewModel>();

        services.AddSingleton<MainWindow>();

        return services;
    }
}
