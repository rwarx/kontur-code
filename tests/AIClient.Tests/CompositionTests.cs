using AIClient.App;
using AIClient.App.Graph;
using AIClient.App.Services;
using AIClient.Application.Interfaces;
using AIClient.Application.Services;
using AIClient.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AIClient.Tests;

/// <summary>
/// The composition root's deliberate last-wins overrides. <c>AddInfrastructure</c> installs
/// the safe defaults - deny every approval, keep plans in the transcript - and
/// <c>AddAppServices</c> replaces both, purely because the host calls it second.
/// </summary>
/// <remarks>
/// Swapping the call order or switching those registrations to <c>TryAdd</c> would compile,
/// start and run: the agent would simply refuse every write with nothing in any log to
/// explain why, and Plan canvas would degrade to Plan while telling the model there is no
/// canvas. The comments at the registration sites say this; the test makes the comment true.
/// </remarks>
public sealed class CompositionTests
{
    [Fact]
    public void Infrastructure_alone_keeps_the_safe_defaults()
    {
        var services = new ServiceCollection().AddInfrastructure(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        Assert.IsType<DenyingAgentApproval>(provider.GetRequiredService<IAgentApproval>());
        Assert.IsType<TranscriptPlanSink>(provider.GetRequiredService<IAgentPlanSink>());
    }

    [Fact]
    public void AddAppServices_wins_over_the_infrastructure_defaults()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(new ConfigurationBuilder().Build());
        services.AddAppServices();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<AgentApprovalService>(provider.GetRequiredService<IAgentApproval>());
        Assert.IsType<CanvasPlanSink>(provider.GetRequiredService<IAgentPlanSink>());
    }
}
