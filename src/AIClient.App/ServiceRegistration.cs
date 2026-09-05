using AIClient.App.Graph;
using AIClient.App.Services;
using AIClient.App.ViewModels;
using AIClient.App.Views;
using AIClient.Application.Interfaces;
using AIClient.Application.Markdown;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui;

namespace AIClient.App;

/// <summary>
/// Registers the presentation layer: ViewModels, Views and the UI-only services.
/// </summary>
/// <remarks>
/// <para>
/// Lifetimes are deliberate. The shell and its panes are singletons because there is one of
/// each and their state - the open conversation, the loaded session list, the canvas's
/// viewport - has to survive navigation. Dialogs are transient.
/// </para>
/// <para>
/// The order inside this method matters only where an interface is registered twice: the
/// canvas plan sink must come after <c>AddInfrastructure</c> (it does - the host calls
/// this after it), and its workspace-root hook is wired in a post-registration pass
/// because the root lives on the workspace service the sink must not hold.
/// </para>
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

        // The approval gate, over the refusing one Infrastructure installs. Registered last wins, and
        // AddInfrastructure runs before this method - so losing that ordering would silently turn every
        // write the agent proposes into a denial, with no error anywhere to explain why.
        services.AddSingleton<AgentApprovalService>();
        services.AddSingleton<IAgentApproval>(provider => provider.GetRequiredService<AgentApprovalService>());

        // The canvas plan sink, over the transcript-only one Infrastructure installs -
        // the same last-wins arrangement, for the same reason: with it missing, PlanCanvas
        // quietly degrades to Plan and the model is told there is no canvas to look at.
        services.AddSingleton<CanvasPlanSink>();
        services.AddSingleton<IAgentPlanSink>(provider => provider.GetRequiredService<CanvasPlanSink>());

        // One parser for the whole app: building the Markdig pipeline is the expensive part
        // and the result is stateless, so a per-message instance would only waste work.
        services.AddSingleton<MarkdownParser>();

        // The workspace composition. Order here is dependency order: the canvas view model
        // exists before the surfaces that inspect it, and the workspace exists before the
        // shell that routes between surfaces.
        services.AddSingleton<CanvasViewModel>();
        services.AddSingleton<GraphOutlineViewModel>();
        services.AddSingleton<FilesViewModel>();
        services.AddSingleton<CodeViewModel>();
        services.AddSingleton<ContextPanelViewModel>();
        services.AddSingleton<IDialogSurface, WorkspaceDialogSurface>();
        services.AddSingleton<WorkspaceViewModel>();
        services.AddSingleton<TasksViewModel>();
        services.AddSingleton<ModelsPageViewModel>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<ChatViewModel>();
        services.AddSingleton<SessionListViewModel>();
        services.AddSingleton<ModelPickerViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<CommandPaletteViewModel>();
        services.AddSingleton<FirstRunViewModel>();

        // The sink's workspace-root probe is wired at startup (see App.OnStartup): a
        // registration-time closure resolving the sink from itself would recurse, and a
        // hosted service for one property assignment is ceremony without honesty.
        services.AddSingleton<MainWindow>();

        return services;
    }
}

/// <summary>
/// Adapts the app's dialog service to the workspace's narrower dialog needs.
/// </summary>
/// <remarks>
/// The workspace view model asks for exactly two things; giving it the full
/// <see cref="IDialogService"/> interface would invite it to grow dialogs it has no
/// business showing, so the adapter exposes a seam, not the service.
/// </remarks>
internal sealed class WorkspaceDialogSurface(IDialogService dialogs) : IDialogSurface
{
    public Task<string?> OpenFolderAsync(string title) =>
        // The picker runs synchronously on the caller's (UI) thread, which is what the
        // dialog service does for every other caller; wrapping it in a completed task
        // keeps the workspace's contract async without pretending the pick is.
        Task.FromResult(dialogs.OpenFolder(title));

    public Task ShowErrorAsync(string title, string message) =>
        dialogs.ShowErrorAsync(title, message);
}
