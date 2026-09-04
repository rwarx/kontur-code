using AIClient.Application.Configuration;
using AIClient.Application.Interfaces;
using AIClient.Application.Services;
using AIClient.Application.Services.Tools;
using AIClient.Domain.Interfaces;
using AIClient.Infrastructure.Configuration;
using AIClient.Infrastructure.Database;
using AIClient.Infrastructure.Graph;
using AIClient.Infrastructure.Http;
using AIClient.Infrastructure.Processes;
using AIClient.Infrastructure.Providers;
using AIClient.Infrastructure.Providers.OpenAiCompatible;
using AIClient.Infrastructure.Repositories;
using AIClient.Infrastructure.SecureStorage;
using AIClient.Infrastructure.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AIClient.Infrastructure;

/// <summary>
/// Composition root for everything that touches the outside world: the database, the
/// filesystem, HTTP and the credential store.
/// </summary>
/// <remarks>
/// This is the only file in Infrastructure the App project calls. Concrete types stay
/// internal to the wiring; the App resolves interfaces defined in Application and Domain,
/// which is what keeps the UI from depending on an implementation.
/// </remarks>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ProviderEndpointOptions>(
            configuration.GetSection(ProviderEndpointOptions.SectionName));

        services.AddSingleton<IAppPaths, AppPaths>();

        AddDatabase(services);
        AddSecureStorage(services);
        AddProviders(services);
        AddApplicationServices(services);

        return services;
    }

    private static void AddDatabase(IServiceCollection services)
    {
        // A factory rather than a scoped DbContext: WPF has no request scope to hang a
        // context off, and a streaming turn writes from a background task while the UI
        // reads on the dispatcher. A DbContext is not thread-safe, so each operation gets
        // its own short-lived instance.
        services.AddDbContextFactory<AIClientDbContext>((provider, options) =>
        {
            var paths = provider.GetRequiredService<IAppPaths>();

            options.UseSqlite(
                $"Data Source={paths.DatabasePath}",
                sqlite => sqlite.MigrationsAssembly(typeof(AIClientDbContext).Assembly.FullName));
        });

        services.AddSingleton<DatabaseInitializer>();
    }

    private static void AddSecureStorage(IServiceCollection services)
    {
        services.AddSingleton<ISecureStorage, DpapiSecureStorage>();
    }

    private static void AddProviders(IServiceCollection services)
    {
        // Each provider gets its own named client so timeouts and headers can diverge
        // without one provider's configuration affecting another.
        services.AddHttpClient(OpenRouterProvider.ProviderId, ConfigureStreamingClient);
        services.AddHttpClient(NvidiaProvider.ProviderId, ConfigureStreamingClient);

        // Registered as IAIProvider so ProviderRegistry receives all of them by injecting
        // IEnumerable<IAIProvider>. Adding a provider is one line here and nothing else.
        services.AddSingleton<IAIProvider, OpenRouterProvider>();
        services.AddSingleton<IAIProvider, NvidiaProvider>();

        services.AddSingleton<IProviderRegistry, ProviderRegistry>();

        // Singleton because it subscribes to OS-wide notifications: one subscription for the
        // process, not one per consumer.
        services.AddSingleton<IConnectivityMonitor, NetworkConnectivityMonitor>();
    }

    private static void ConfigureStreamingClient(IServiceProvider provider, HttpClient client)
    {
        var options = provider.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<ProviderEndpointOptions>>().Value;

        // HttpClient's timeout covers the whole response, including the streamed body, so it
        // has to accommodate a long answer rather than just a slow connection. The user's
        // Stop button and the per-request CancellationToken are the real controls; this is
        // only a backstop against a connection that hangs forever.
        client.Timeout = TimeSpan.FromSeconds(Math.Max(30, options.StreamTimeoutSeconds));

        client.DefaultRequestHeaders.UserAgent.ParseAdd("AIClient/0.1");

        // Never set an Authorization header here: a named client is shared across every
        // request for that provider, and the key is attached per-request instead.
    }

    private static void AddApplicationServices(IServiceCollection services)
    {
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IConversationService, ConversationService>();
        services.AddSingleton<IContextBuilder, ContextBuilder>();
        services.AddSingleton<ITitleGenerator, HeuristicTitleGenerator>();
        services.AddSingleton<IAttachmentService, AttachmentService>();
        services.AddSingleton<IExportService, ExportService>();
        services.AddSingleton<IChatService, ChatService>();

        // Singleton because the open folder is process-wide state: the file tree, the agent and
        // the settings screen all have to agree on which folder that is.
        services.AddSingleton<IWorkspaceService, WorkspaceService>();

        // Singleton for a stronger reason than convenience: the graph is one shared snapshot that
        // readers hold without locking, and a second instance would mean two versions of the truth
        // and two sets of subscribers being told different things.
        services.AddSingleton<IGraphService, GraphService>();

        // Stateless, both of them: an indexing pass owns its own walk and a context build owns its
        // own StringBuilder, so nothing is shared but the graph they read.
        services.AddSingleton<IGraphIndexer, WorkspaceGraphIndexer>();
        services.AddSingleton<IGraphContextSource, GraphContextSource>();

        // The view store is deliberately not a singleton in spirit - it holds nothing - but a
        // singleton in registration, because it is injected into a singleton ViewModel and the
        // context factory it uses creates a fresh connection per call anyway.
        services.AddSingleton<ICanvasViewStore, CanvasViewStore>();

        AddAgentTools(services);

        // Refuses everything, and is meant to be replaced by the host: a console or a test has no
        // way to ask anyone, and the safe answer to a question nobody can hear is no. The WPF app
        // registers its own gate over this one, which is why TryAddSingleton is not used here - the
        // last registration wins, and the host registers last.
        services.AddSingleton<IAgentApproval, DenyingAgentApproval>();

        // Same arrangement, and for the same reason: a plan has to go somewhere, and where it goes
        // depends on what the host can draw on. This one keeps the plan in the conversation, which is
        // the whole of what a build without a canvas can honestly do; the WPF app registers a sink
        // that also draws, over the top of this.
        services.AddSingleton<IAgentPlanSink, TranscriptPlanSink>();

        services.AddSingleton<IAgentService, AgentService>();
    }

    private static void AddAgentTools(IServiceCollection services)
    {
        // Registered as IAgentTool so the registry receives every one of them by injecting
        // IEnumerable<IAgentTool>. This list is therefore the whole of what the model can do:
        // adding a capability is one line here, and withdrawing one is deleting a line, with no
        // second place that has to be kept in step.
        //
        // Listed reading first and writing second, which is the order the registry sorts them into
        // and the order the model is shown them in.
        services.AddSingleton<IAgentTool, ListFilesTool>();
        services.AddSingleton<IAgentTool, ReadFileTool>();
        services.AddSingleton<IAgentTool, SearchFilesTool>();
        services.AddSingleton<IAgentTool, CreateDirectoryTool>();
        services.AddSingleton<IAgentTool, DeleteFileTool>();
        services.AddSingleton<IAgentTool, EditFileTool>();
        services.AddSingleton<IAgentTool, MoveFileTool>();
        services.AddSingleton<IAgentTool, WriteFileTool>();

        // Last, and the only one that can run a program. It is registered unconditionally and refuses
        // its own calls while the setting is off, rather than being registered conditionally: the
        // container is built once at startup, and a tool set that depended on a setting read at that
        // moment would need a restart to reflect the switch the user just turned on.
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IAgentTool, RunCommandTool>();

        // Belongs to the planning modes and is withheld from a build, which AgentModePolicy decides
        // from the marker interface it implements rather than from where it sits in this list.
        services.AddSingleton<IAgentTool, SubmitPlanTool>();

        services.AddSingleton<IAgentToolRegistry, AgentToolRegistry>();
    }
}
