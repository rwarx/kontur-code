using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Application.Services;
using AIClient.Application.Services.Tools;

namespace AIClient.Tests;

/// <summary>
/// The one tool a planning run delivers anything with: what it accepts, what it refuses, and what the
/// model reads back afterwards.
/// </summary>
/// <remarks>
/// <para>
/// The arguments are most of the subject. A plan is the largest object any tool here is handed - nested
/// arrays of objects, each with optional fields of its own - and it is written by a model a token at a
/// time, so the tests are largely about being forgiving where that costs nothing and firm where it does
/// not.
/// </para>
/// <para>
/// The rendered Markdown is asserted rather than trusted, because it has two readers. It is the
/// persisted tool row, which is the whole of how a plan survives the conversation being closed; and it
/// is what the model reads on the next step, when it puts the plan to the user in prose.
/// </para>
/// </remarks>
public sealed class SubmitPlanToolTests
{
    [Fact]
    public async Task A_plan_comes_back_as_the_markdown_the_model_will_read_out()
    {
        var result = await RunAsync(new RecordingSink(), Sample);

        Assert.True(result.Success);
        Assert.Contains("# Add authentication", result.Content, StringComparison.Ordinal);
        Assert.Contains("Let a returning user stay signed in.", result.Content, StringComparison.Ordinal);

        // A step's detail is folded onto its own line, and its paths are indented under the number so the
        // list survives being rendered as Markdown rather than being ended by them.
        Assert.Contains(
            "1. Read the existing login path — where the cookie is set",
            result.Content,
            StringComparison.Ordinal);

        Assert.Contains("   `src/Auth/Login.cs`, `src/Program.cs`", result.Content, StringComparison.Ordinal);
        Assert.Contains("2. Add a token store", result.Content, StringComparison.Ordinal);

        Assert.Contains(
            "- **AuthService** (service) `src/Auth/AuthService.cs` — Issues and validates tokens",
            result.Content,
            StringComparison.Ordinal);

        Assert.Contains("  needs: TokenStore", result.Content, StringComparison.Ordinal);
        Assert.Contains("## Risks and open questions", result.Content, StringComparison.Ordinal);
        Assert.Contains("- The refresh window is a guess.", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_sink_is_handed_the_plan_itself_rather_than_the_text_of_it()
    {
        // The reason this is a tool and not a request to answer with a plan. Prose can be read by a person
        // and by nothing else; a canvas draws from parts and the dependencies between them, and can only
        // do that if they arrive as parts.
        var sink = new RecordingSink();

        await RunAsync(sink, Sample);

        var plan = Assert.Single(sink.Accepted);

        Assert.Equal("Add authentication", plan.Title);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal(["src/Auth/Login.cs", "src/Program.cs"], plan.Steps[0].Paths);
        Assert.Empty(plan.Steps[1].Paths);

        Assert.Equal(AgentPlanPartKind.Service, plan.Parts[0].Kind);
        Assert.Equal(["TokenStore"], plan.Parts[0].DependsOn);
        Assert.Equal(AgentPlanPartKind.Data, plan.Parts[1].Kind);
        Assert.Single(plan.Risks);
    }

    [Fact]
    public async Task The_summary_counts_what_was_planned()
    {
        // The one line the collapsed card shows, which is the only part of a long plan most of the
        // transcript ever displays.
        var many = await RunAsync(new RecordingSink(), Sample);
        Assert.Equal("Plan: Add authentication (2 steps, 2 parts)", many.Summary);

        var one = await RunAsync(
            new RecordingSink(),
            """{"title":"Rename the widget","steps":[{"title":"Rename it"}]}""");

        // Singular, and no mention of parts when there are none - a count of zero reads as something lost
        // in transit rather than as something not asked for.
        Assert.Equal("Plan: Rename the widget (1 step)", one.Summary);
    }

    [Fact]
    public async Task A_plan_with_no_steps_in_it_is_refused_and_told_why()
    {
        // Refused rather than recorded empty. A model that sends this has misread the schema, and the
        // reply is the only chance to say so before the step budget goes on an empty plan.
        var sink = new RecordingSink();

        var result = await RunAsync(sink, """{"title":"Do the thing","steps":[]}""");

        Assert.False(result.Success);
        Assert.Contains("at least one step", result.Content, StringComparison.Ordinal);
        Assert.Equal("submit_plan: rejected", result.Summary);
        Assert.Empty(sink.Accepted);
    }

    [Fact]
    public async Task A_title_that_was_never_sent_is_refused_with_the_field_named()
    {
        var result = await RunAsync(new RecordingSink(), """{"steps":[{"title":"Rename it"}]}""");

        Assert.False(result.Success);
        Assert.Contains("'title' is required", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_step_without_a_title_names_the_field_and_says_where_it_belongs()
    {
        // The nested error has to say which level it is about. "'title' is required" alone reads as the
        // plan's own title, and the model corrects the wrong field.
        var result = await RunAsync(
            new RecordingSink(),
            """{"title":"Do the thing","steps":[{"detail":"somehow"}]}""");

        Assert.False(result.Success);
        Assert.Contains("Every step needs a 'title'", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plan_the_size_of_a_backlog_is_refused_rather_than_recorded()
    {
        // A cap to catch a model that has misunderstood the granularity, not to discipline a long plan:
        // fifty steps is already more than anyone reviews, and the reply says what to do instead of
        // dropping the tail silently.
        var steps = string.Join(",", Enumerable.Range(1, 51).Select(n => $$"""{"title":"Step {{n}}"}"""));

        var result = await RunAsync(new RecordingSink(), $$"""{"title":"Everything","steps":[{{steps}}]}""");

        Assert.False(result.Success);
        Assert.Contains("That is 51 steps", result.Content, StringComparison.Ordinal);
        Assert.Contains("no more than 50", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_part_that_depends_on_itself_keeps_the_part_and_loses_the_dependency()
    {
        // A slip of the pen rather than a statement, and the loop it draws would be the most prominent
        // thing on the canvas. Matched on the trimmed name and without regard to case, because that is how
        // the same slip is written.
        var sink = new RecordingSink();

        await RunAsync(
            sink,
            """
            {
              "title": "Lay out the API",
              "steps": [{ "title": "Start" }],
              "parts": [
                { "name": "AuthService", "depends_on": ["  authservice ", "TokenStore"] }
              ]
            }
            """);

        var part = Assert.Single(Assert.Single(sink.Accepted).Parts);

        Assert.Equal("AuthService", part.Name);
        Assert.Equal(["TokenStore"], part.DependsOn);
    }

    [Fact]
    public async Task The_kind_a_model_actually_wrote_is_read_as_the_nearest_one()
    {
        // The enum says "module" and models say "class". Refusing that would spend a step of the budget on
        // vocabulary, and the cost of guessing wrong is a node drawn in the wrong colour.
        var sink = new RecordingSink();

        await RunAsync(
            sink,
            """
            {
              "title": "Lay out the app",
              "steps": [{ "title": "Start" }],
              "parts": [
                { "name": "Widget", "kind": "class" },
                { "name": "Settings", "kind": "Screen" },
                { "name": "Whatsit", "kind": "gizmo" }
              ]
            }
            """);

        var parts = Assert.Single(sink.Accepted).Parts;

        Assert.Equal(AgentPlanPartKind.Module, parts[0].Kind);
        Assert.Equal(AgentPlanPartKind.View, parts[1].Kind);
        Assert.Equal(AgentPlanPartKind.Other, parts[2].Kind);
    }

    [Fact]
    public async Task A_kind_nobody_recognised_is_drawn_without_one_rather_than_labelled_other()
    {
        // "Other" is the absence of an answer, and printing it would put a meaningless word next to a
        // third of the boxes.
        var result = await RunAsync(
            new RecordingSink(),
            """
            {
              "title": "Lay out the app",
              "steps": [{ "title": "Start" }],
              "parts": [{ "name": "Whatsit", "kind": "gizmo" }]
            }
            """);

        Assert.Contains("- **Whatsit**", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("(other)", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_step_sent_as_an_object_is_read_as_a_plan_of_one()
    {
        // A model with one item to send frequently sends the item rather than a list holding it. Accepting
        // that costs nothing and saves a step; the alternative is a refusal over a pair of brackets.
        var sink = new RecordingSink();

        var result = await RunAsync(
            sink,
            """{"title":"Rename the widget","steps":{"title":"Rename it","paths":"src/Widget.cs"}}""");

        Assert.True(result.Success);

        var step = Assert.Single(Assert.Single(sink.Accepted).Steps);

        Assert.Equal("Rename it", step.Title);
        Assert.Equal(["src/Widget.cs"], step.Paths);
    }

    [Fact]
    public async Task A_detail_written_as_three_paragraphs_becomes_one_line()
    {
        // A newline inside a list item ends the list, and a model asked for a short explanation will
        // occasionally send several paragraphs. Collapsed rather than truncated: the words are worth
        // keeping, the line breaks are not.
        var result = await RunAsync(
            new RecordingSink(),
            """
            {
              "title": "Tidy up",
              "steps": [{ "title": "Read the file", "detail": "because\n\n  the cookie\nis set there" }]
            }
            """);

        Assert.Contains("1. Read the file — because the cookie is set there", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Blank_risks_are_dropped_and_a_wall_of_them_is_cut_to_what_fits()
    {
        // Unlike the steps, an over-long list of risks is trimmed rather than refused: a plan is still a
        // plan without its twenty-sixth caveat, and refusing here would throw away the whole thing over
        // the least important part of it.
        var risks = string.Join(",", Enumerable.Range(1, 30).Select(n => $"\"Risk {n}\""));

        var result = await RunAsync(
            new RecordingSink(),
            $$"""
            {
              "title": "Take the chance",
              "steps": [{ "title": "Start" }],
              "risks": ["   ", {{risks}}]
            }
            """);

        Assert.True(result.Success);
        Assert.Contains("- Risk 25", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("- Risk 26", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_model_is_told_the_plan_was_kept_and_then_told_to_stop()
    {
        // The last line is the one that earns its place. A model that has just been given its own plan back
        // will otherwise read it as new information and plan again, and the step budget goes on that.
        var result = await RunAsync(new RecordingSink(), Sample);

        Assert.StartsWith("The plan has been recorded.", result.Content, StringComparison.Ordinal);
        Assert.Contains("Nothing was created, changed or run.", result.Content, StringComparison.Ordinal);
        Assert.Contains("tell the user the plan in your own words, and stop", result.Content, StringComparison.Ordinal);
        Assert.Contains("Do not call submit_plan again.", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_canvas_that_drew_the_plan_says_so_and_one_that_could_not_says_that_instead()
    {
        // The first sentence is the model's only source of truth about whether there is a picture to point
        // the user at - and it will point them at one either way if nobody tells it.
        var drawn = await RunAsync(
            new RecordingSink(AgentPlanAcceptance.DrawnOn("It is on the canvas beside the chat.")),
            Sample);

        Assert.StartsWith("The plan has been recorded and drawn.", drawn.Content, StringComparison.Ordinal);
        Assert.Contains("It is on the canvas beside the chat.", drawn.Content, StringComparison.Ordinal);

        var kept = await RunAsync(new RecordingSink(), Sample);

        Assert.StartsWith("The plan has been recorded.", kept.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("drawn", kept.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_build_with_no_canvas_tells_the_model_not_to_point_at_one()
    {
        // The default sink, and the reason Plan + canvas degrades to Plan rather than failing. Without the
        // note the model signs off by telling the user to look at a canvas that is not there, and they
        // reasonably conclude the feature is broken rather than absent.
        var sink = new TranscriptPlanSink();

        var result = await RunAsync(sink, Sample);

        Assert.True(result.Success);
        Assert.False(sink.CanDraw);
        Assert.Contains("There is no canvas in this build", result.Content, StringComparison.Ordinal);
        Assert.Contains("write the plan out for them instead", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Recording_a_plan_is_never_put_to_the_user_and_never_offered_to_a_build()
    {
        // Read, which looks wrong for a tool that plainly records something. The risk levels decide whether
        // the user is asked first and they are about the user's machine: an approval dialog in front of the
        // plan they just asked for would be noise in the one place a run should be quiet.
        var tool = new SubmitPlanTool(new RecordingSink());

        Assert.Equal("submit_plan", tool.Name);
        Assert.Equal(AgentToolRisk.Read, tool.Risk);
        Assert.IsAssignableFrom<IAgentPlanningTool>(tool);

        // Built through the real registry, which is what checks the name and parses the schema. A name no
        // provider would accept, or a schema whose root is not an object, fails here rather than mid-turn
        // against a live endpoint.
        var registry = new AgentToolRegistry([tool]);

        Assert.Contains("submit_plan", registry.Available(AgentMode.Plan).Select(definition => definition.Name));
        Assert.Contains("submit_plan", registry.Available(AgentMode.PlanCanvas).Select(definition => definition.Name));
        Assert.Empty(registry.Available(AgentMode.Build));
    }

    #region Harness

    private static async Task<AgentToolResult> RunAsync(IAgentPlanSink sink, string argumentsJson)
    {
        Assert.True(AgentToolArguments.TryParse(argumentsJson, out var arguments, out var error), error);

        return await new SubmitPlanTool(sink).ExecuteAsync(arguments, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A plan with one of everything in it, so the rendering and the parsing can be asserted against the
    /// same call.
    /// </summary>
    private const string Sample = """
        {
          "title": "Add authentication",
          "goal": "Let a returning user stay signed in.",
          "steps": [
            {
              "title": "Read the existing login path",
              "detail": "where the cookie is set",
              "paths": ["src/Auth/Login.cs", "src/Program.cs"]
            },
            { "title": "Add a token store" }
          ],
          "parts": [
            {
              "name": "AuthService",
              "kind": "service",
              "path": "src/Auth/AuthService.cs",
              "purpose": "Issues and validates tokens",
              "depends_on": ["TokenStore"]
            },
            { "name": "TokenStore", "kind": "data" }
          ],
          "risks": ["The refresh window is a guess."]
        }
        """;

    /// <summary>A sink that keeps what it was handed and answers however the test says.</summary>
    private sealed class RecordingSink : IAgentPlanSink
    {
        private readonly AgentPlanAcceptance _acceptance;

        public RecordingSink(AgentPlanAcceptance? acceptance = null) =>
            _acceptance = acceptance ?? AgentPlanAcceptance.NotDrawn();

        /// <summary>Every plan handed over, in order.</summary>
        public List<AgentPlan> Accepted { get; } = [];

        public bool CanDraw => _acceptance.Drawn;

        public Task<AgentPlanAcceptance> AcceptAsync(
            AgentPlan plan,
            CancellationToken cancellationToken = default)
        {
            Accepted.Add(plan);

            return Task.FromResult(_acceptance);
        }
    }

    #endregion
}
