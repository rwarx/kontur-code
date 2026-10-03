using System.Text.Json;
using System.Text.Json.Serialization;
using AIClient.Application.Graph;
using AIClient.Application.Interfaces;
using AIClient.Infrastructure;
using AIClient.Infrastructure.Database;
using AIClient.Server;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Cors.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// WebApplication.CreateBuilder has already read appsettings.json and the ambient environment.
// Only the prefixed variables are added, because an unqualified one would let the machine's
// environment redirect a provider endpoint; the AICLIENT_ prefix is the contract.
builder.Configuration.AddEnvironmentVariables("AICLIENT_");

builder.Services.AddInfrastructure(builder.Configuration);

// The HTTP approval gate wins over Infrastructure's refusing default, exactly
// like the WPF host's inline card does: last registration wins.
builder.Services.AddSingleton<RunRegistry>();
builder.Services.AddSingleton<JsonCheckpointStore>();
builder.Services.AddSingleton<GraphPersistence>();
builder.Services.AddSingleton<HttpAgentApproval>();
builder.Services.AddSingleton<IAgentApproval>(p => p.GetRequiredService<HttpAgentApproval>());

// PlanCanvas actually draws in this build: override Infrastructure's text-only
// TranscriptPlanSink so submit_plan lands on the shared graph the canvas reads.
builder.Services.AddSingleton<IAgentPlanSink, ServerCanvasPlanSink>();

// The bearer token every route but the readiness probe requires. See SidecarAuthentication.cs for
// why a loopback socket is not an authorisation boundary on its own.
builder.Services.AddSingleton<SidecarToken>();
builder.Services
    .AddAuthentication(SidecarTokenHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, SidecarTokenHandler>(
        SidecarTokenHandler.SchemeName,
        _ => { });
builder.Services.AddAuthorization();

builder.Services.Configure<CorsOptions>(SidecarCors.Configure);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

// Localhost only, enforced rather than requested: see SidecarCors.Bind.
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
SidecarCors.Bind(app, startupLogger);

app.UseCors(SidecarCors.PolicyName);

// Authentication before authorization, and both before the endpoints. Anything mapped without
// RequireAuthorization is reachable without the token, which is why there is exactly one such
// endpoint and it is named in a comment on both sides.
app.UseAuthentication();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    logger.LogInformation("Kontur Code sidecar starting.");

    logger.LogInformation(
        "Sidecar authentication token {Source}.",
        SidecarCors.DescribeTokenSource());

    await services.GetRequiredService<DatabaseInitializer>().InitializeAsync().ConfigureAwait(false);

    var settings = services.GetRequiredService<ISettingsService>();
    await settings.LoadAsync().ConfigureAwait(false);

    await services.GetRequiredService<IProviderRegistry>().LoadCustomProvidersAsync().ConfigureAwait(false);

    // The indexer is resolved once so misconfiguration fails here, at startup.
    _ = services.GetRequiredService<WorkspaceGraphIndexer>();
    await services.GetRequiredService<GraphPersistence>().InitializeAsync().ConfigureAwait(false);

    logger.LogInformation("Kontur Code sidecar ready.");
}

RunEndpoints.Map(app);
DataEndpoints.Map(app);
GitEndpoints.Map(app);
AiEndpoints.Map(app);

app.Run();

/// <summary>
/// Named so <c>WebApplicationFactory</c> and the readiness probe have a type to refer to.
/// </summary>
public partial class Program
{
    /// <summary>Present so the type is not empty; nothing is constructed from it.</summary>
    protected Program()
    {
    }
}