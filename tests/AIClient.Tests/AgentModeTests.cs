using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Application.Services;

namespace AIClient.Tests;

/// <summary>
/// What each mode may call, and what the model is told to do with it.
/// </summary>
/// <remarks>
/// <para>
/// The mode is the one thing the agent does about risk that is not a dialog. Everything else asks the
/// user; a planning run instead never has the tools that would need asking about. So these tests are
/// mostly about absence - what is not in the request, and what does not happen when a call for it
/// arrives anyway.
/// </para>
/// <para>
/// The policy and the registry are tested together on purpose. Either alone would pass while the
/// feature was broken: a policy nobody consults withholds nothing, and a registry filtering by its own
/// copy of the rule would drift from the one the loop enforces.
/// </para>
/// </remarks>
public sealed class AgentModeTests
{
    [Fact]
    public void Planning_offers_the_reading_tools_and_nothing_that_changes_anything()
    {
        // Expressed as risk rather than as a list of names, so a tool written next year is gated
        // correctly by declaring what it costs instead of by being remembered here.
        foreach (var mode in new[] { AgentMode.Plan, AgentMode.PlanCanvas })
        {
            Assert.True(AgentModePolicy.Offers(mode, Probe("read_thing", AgentToolRisk.Read)));
            Assert.False(AgentModePolicy.Offers(mode, Probe("write_thing", AgentToolRisk.Write)));
            Assert.False(AgentModePolicy.Offers(mode, Probe("run_thing", AgentToolRisk.Execute)));

            // The plan tool counts as a read, which is what lets a planning run produce anything at all.
            Assert.True(AgentModePolicy.Offers(mode, new PlanProbe()));
        }
    }

    [Fact]
    public void A_build_offers_everything_except_the_planning_tools()
    {
        Assert.True(AgentModePolicy.Offers(AgentMode.Build, Probe("read_thing", AgentToolRisk.Read)));
        Assert.True(AgentModePolicy.Offers(AgentMode.Build, Probe("write_thing", AgentToolRisk.Write)));
        Assert.True(AgentModePolicy.Offers(AgentMode.Build, Probe("run_thing", AgentToolRisk.Execute)));

        // Withheld for the interface it implements rather than for its name or its place in the
        // registration list, so a second planning tool is gated without the policy being touched.
        Assert.False(AgentModePolicy.Offers(AgentMode.Build, new PlanProbe()));
    }

    [Fact]
    public void Every_declared_mode_gets_an_offer_of_its_own()
    {
        // The registry precomputes one entry per mode by asking the policy. A mode added to the enum and
        // forgotten everywhere else would otherwise either throw on the lookup or quietly inherit Build's
        // tools; this turns the first into a failure and rules out the second.
        var registry = Registry();

        foreach (var mode in Enum.GetValues<AgentMode>())
        {
            var offered = registry.Available(mode).Select(definition => definition.Name).ToArray();

            Assert.Contains("read_thing", offered);
            Assert.Equal(mode == AgentMode.Build, offered.Contains("write_thing"));
            Assert.Equal(mode == AgentMode.Build, offered.Contains("run_thing"));
            Assert.Equal(mode.IsPlanning(), offered.Contains("submit_plan_probe"));
        }
    }

    [Fact]
    public void Available_without_a_mode_named_is_the_build_it_always_was()
    {
        // Callers that predate the modes pass nothing, and there are some. The default has to stay Build
        // or each of them silently starts asserting about a planning run instead.
        var registry = Registry();

        Assert.Equal(
            registry.Available(AgentMode.Build).Select(definition => definition.Name),
            registry.Available().Select(definition => definition.Name));
    }

    [Fact]
    public void A_tool_that_is_switched_off_stays_withheld_in_every_mode()
    {
        // Two filters, and both have to apply. The mode decides what a run of that kind may call at all;
        // availability decides whether a tool could do anything if it were called. A planning run
        // offering a switched-off reader would be the first filter cancelling out the second.
        var registry = new AgentToolRegistry([Probe("read_thing", AgentToolRisk.Read), new SwitchedOff()]);

        Assert.Equal(2, registry.Definitions.Count);

        foreach (var mode in Enum.GetValues<AgentMode>())
        {
            var offered = registry.Available(mode).Select(definition => definition.Name).ToArray();

            Assert.Contains("read_thing", offered);
            Assert.DoesNotContain("off_thing", offered);
        }
    }

    [Fact]
    public void A_refusal_names_the_tool_and_says_what_to_do_instead()
    {
        // This sentence is the whole of what the model learns about the refusal. "Not available in this
        // mode" sends it down the tool list looking for another way to write; being told what the mode is
        // for, and what ends it, stops it.
        var planning = AgentModePolicy.Refuse(AgentMode.Plan, Probe("write_thing", AgentToolRisk.Write));

        Assert.False(planning.Success);
        Assert.Contains("'write_thing' is not available while planning", planning.Content, StringComparison.Ordinal);
        Assert.Contains("Nothing has been done", planning.Content, StringComparison.Ordinal);
        Assert.Contains("submit_plan", planning.Content, StringComparison.Ordinal);
        Assert.Equal("write_thing: not available in Plan", planning.Summary);

        // The opposite mistake gets the opposite instruction, rather than one sentence covering both.
        var building = AgentModePolicy.Refuse(AgentMode.Build, new PlanProbe());

        Assert.False(building.Success);
        Assert.Contains("belongs to the planning modes", building.Content, StringComparison.Ordinal);
        Assert.Contains("Nothing has been recorded", building.Content, StringComparison.Ordinal);
        Assert.Equal("submit_plan_probe: not available in Build", building.Summary);
    }

    [Fact]
    public void The_planning_prompt_replaces_the_build_discipline_rather_than_qualifying_it()
    {
        var build = AgentPrompt.Compose(null, Root);
        var plan = AgentPrompt.Compose(null, Root, mode: AgentMode.Plan);

        // Telling a model both disciplines leaves it following neither: "look before you change anything"
        // is advice for a run that can change something.
        Assert.Contains("Look before you change anything", build, StringComparison.Ordinal);
        Assert.DoesNotContain("Look before you change anything", plan, StringComparison.Ordinal);

        Assert.Contains("You are planning, not building", plan, StringComparison.Ordinal);
        Assert.Contains("submit_plan", plan, StringComparison.Ordinal);
        Assert.Contains(Root, plan, StringComparison.Ordinal);

        // The line that stops a planning run from ending in an offer to do the work and a wait for an
        // answer that is never coming.
        Assert.Contains("do not offer to and do not ask to be let", plan, StringComparison.Ordinal);
    }

    [Fact]
    public void A_build_prompt_reads_the_same_as_it_did_before_there_were_modes()
    {
        // The mode arrived as a new parameter with a default. This is the guarantee that the default
        // changed the composed text for nobody who never passes it.
        Assert.Equal(
            AgentPrompt.Compose("Answer in Russian.", Root, canRunCommands: true),
            AgentPrompt.Compose("Answer in Russian.", Root, canRunCommands: true, AgentMode.Build));
    }

    [Fact]
    public void Only_the_canvas_mode_is_told_the_plan_gets_drawn()
    {
        var plan = AgentPrompt.Compose(null, Root, mode: AgentMode.Plan);
        var canvas = AgentPrompt.Compose(null, Root, mode: AgentMode.PlanCanvas);

        // The extra block is entirely about 'parts', because that is the field a drawing is made from and
        // the one a model left to itself fills with restatements of the steps.
        Assert.DoesNotContain("gets drawn as a diagram", plan, StringComparison.Ordinal);
        Assert.Contains("gets drawn as a diagram", canvas, StringComparison.Ordinal);
        Assert.Contains("not the steps that create them", canvas, StringComparison.Ordinal);

        // Added, not substituted: the permissions and the rest of the instructions are the same ones.
        Assert.Contains("You are planning, not building", canvas, StringComparison.Ordinal);
    }

    [Fact]
    public void Planning_with_no_folder_open_says_the_opposite_of_building_with_none()
    {
        var build = AgentPrompt.Compose(null, null);
        var plan = AgentPrompt.Compose(null, null, mode: AgentMode.PlanCanvas);

        // The ordinary case for these modes rather than a degraded one: a project that does not exist yet
        // has nothing to read. "Open a folder first" is the least useful thing that could be said to
        // somebody asking how to start one.
        Assert.Contains("open a folder first", build, StringComparison.Ordinal);
        Assert.DoesNotContain("open a folder first", plan, StringComparison.Ordinal);
        Assert.Contains("for planning that is fine", plan, StringComparison.Ordinal);
        Assert.Contains("that is for building", plan, StringComparison.Ordinal);
    }

    [Fact]
    public void Planning_never_mentions_running_programs_however_the_setting_is_left()
    {
        // The command block is written for a run that checks its own work by building it. A planning run
        // cannot run anything, so including it would advertise a tool that was never offered.
        Assert.DoesNotContain(
            "You can also run programs",
            AgentPrompt.Compose(null, Root, canRunCommands: true, AgentMode.Plan),
            StringComparison.Ordinal);

        Assert.Contains(
            "You can also run programs",
            AgentPrompt.Compose(null, Root, canRunCommands: true),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_users_own_words_come_first_in_every_mode()
    {
        // The configured system prompt is the user's, not the feature's. Someone who wrote "answer in
        // Russian" in Settings meant it while planning as well.
        foreach (var mode in Enum.GetValues<AgentMode>())
        {
            Assert.StartsWith(
                "Answer in Russian.",
                AgentPrompt.Compose("  Answer in Russian.  ", Root, mode: mode),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_mode_knows_whether_it_needs_a_folder_and_what_it_is_called()
    {
        // Read in four places - the tools offered, the prompt, the Send refusal and the hint under the
        // composer - so the answer is written once. Build is the only mode that changes anything, and so
        // the only one that needs somewhere to change it.
        Assert.True(AgentMode.Build.NeedsWorkspace());
        Assert.False(AgentMode.Plan.NeedsWorkspace());
        Assert.False(AgentMode.PlanCanvas.NeedsWorkspace());

        Assert.False(AgentMode.Build.IsPlanning());
        Assert.True(AgentMode.Plan.IsPlanning());
        Assert.True(AgentMode.PlanCanvas.IsPlanning());

        Assert.Equal("Build", AgentMode.Build.DisplayName());
        Assert.Equal("Plan", AgentMode.Plan.DisplayName());
        Assert.Equal("Plan + canvas", AgentMode.PlanCanvas.DisplayName());
    }

    [Fact]
    public void A_request_that_says_nothing_about_its_mode_is_a_build()
    {
        // What every caller meant before there were modes, so it is what they still get. Build being the
        // zero value is part of that and not an accident: a default-constructed enum has to agree.
        Assert.Equal(AgentMode.Build, default);

        var request = new AgentRunRequest
        {
            ConversationId = Guid.CreateVersion7(),
            Content = "Rename the widget.",
            ProviderId = "test",
            ModelId = "test/model",
        };

        Assert.Equal(AgentMode.Build, request.Mode);
    }

    #region Harness

    private const string Root = @"C:\Projects\Widgets";

    /// <summary>One tool of each risk, plus one that belongs to the planning modes.</summary>
    private static AgentToolRegistry Registry() =>
        new([
            Probe("read_thing", AgentToolRisk.Read),
            Probe("write_thing", AgentToolRisk.Write),
            Probe("run_thing", AgentToolRisk.Execute),
            new PlanProbe(),
        ]);

    private static IAgentTool Probe(string name, AgentToolRisk risk) => new ModeProbe(name, risk);

    /// <summary>A tool that exists to be offered or withheld, and does nothing if it is called.</summary>
    private class ModeProbe : IAgentTool
    {
        public ModeProbe(string name, AgentToolRisk risk)
        {
            Name = name;
            Risk = risk;
        }

        public string Name { get; }

        public string Description => $"A {Risk} tool that exists to be offered or withheld.";

        public string ParametersJsonSchema => """{"type":"object","properties":{}}""";

        public AgentToolRisk Risk { get; }

        public Task<AgentToolResult> ExecuteAsync(
            AgentToolArguments arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AgentToolResult.Ok($"{Name} did nothing."));
    }

    /// <summary>Belongs to the planning modes, and says so the way the real one does.</summary>
    private sealed class PlanProbe : ModeProbe, IAgentPlanningTool
    {
        public PlanProbe()
            : base("submit_plan_probe", AgentToolRisk.Read)
        {
        }
    }

    /// <summary>A reading tool that could do nothing if it were called.</summary>
    private sealed class SwitchedOff : ModeProbe, IAgentToolAvailability
    {
        public SwitchedOff()
            : base("off_thing", AgentToolRisk.Read)
        {
        }

        public bool IsAvailable => false;
    }

    #endregion
}
