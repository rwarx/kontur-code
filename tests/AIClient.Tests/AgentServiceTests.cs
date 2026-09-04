using System.Runtime.CompilerServices;
using AIClient.Application.Configuration;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Application.Services;
using AIClient.Domain.Enums;
using AIClient.Domain.Interfaces;
using AIClient.Domain.Models;
using AIClient.Infrastructure.Configuration;
using AIClient.Infrastructure.Repositories;
using AIClient.Infrastructure.Workspace;
using AIClient.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIClient.Tests;

/// <summary>
/// The agent loop: how often the model is asked, what it is told between the asks, what is
/// persisted, and what a tool call has to get past before it happens.
/// </summary>
/// <remarks>
/// <para>
/// The conversation store, the context builder, the tool registry and the workspace are the real
/// ones, over real SQLite and a real temporary folder. Three collaborators are scripted, because
/// they are the three whose behaviour a test has to dictate: the provider, which decides what the
/// model asks for; the tools, which decide what comes back; and the approval gate, which decides
/// what the user says. Faking the persistence would leave every ordering guarantee untested, and
/// the order is most of what the class exists to get right.
/// </para>
/// <para>
/// The tools here are probes rather than the real file tools. What a write does to a folder is
/// <see cref="AgentToolTests"/>'s subject; what the loop does with the answer is this one's, and a
/// probe is the only way to script a tool that throws, or one that is asked the same thing twice.
/// </para>
/// </remarks>
public sealed class AgentServiceTests : IAsyncLifetime
{
    private const string ProviderId = "test";
    private const string ModelId = "test/model";

    private readonly StubSettingsService _settings = new();

    private TestDatabase _db = null!;
    private ConversationService _conversations = null!;
    private WorkspaceService _workspace = null!;
    private string _scratch = null!;
    private string _root = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _conversations = _db.Conversations();

        _scratch = Path.Combine(Path.GetTempPath(), "aiclient-agent", Guid.CreateVersion7().ToString("n"));
        _root = Path.Combine(_scratch, "project");

        Directory.CreateDirectory(_root);

        _workspace = new WorkspaceService(
            _settings,
            new AppPaths(Path.Combine(_scratch, "appdata")),
            NullLogger<WorkspaceService>.Instance);

        // The loop only reads the root, to put it in the system prompt, but it reads it through the
        // real service - so the folder has to be one the workspace rules actually accept.
        var opened = await _workspace.OpenAsync(_root, Token);
        Assert.True(opened.Success, opened.Error);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();

        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth failing a run over a leftover temporary directory.
        }
    }

    [Fact]
    public async Task A_run_is_a_step_then_its_calls_then_another_step_then_an_answer()
    {
        var conversationId = await NewChatAsync();
        var probe = new ProbeTool("read_thing");
        var provider = Stepping(
            Calls(Call("read_thing", """{"path":"a.txt"}""")),
            Answer("It says hello."));

        var events = await CollectAsync(
            Service(provider, [probe]).RunAsync(Ask(conversationId), Token));

        // The card in the transcript is built from this order: the step's own row exists before its
        // calls are mentioned, and every call is proposed before it is started.
        Assert.Collection(
            events,
            e => Assert.Equal(MessageRole.User, Assert.IsType<AgentEvent.UserMessageSaved>(e).Message.Role),
            e => Assert.Equal(1, Assert.IsType<AgentEvent.StepStarted>(e).Step),
            e => Assert.True(Assert.IsType<AgentEvent.StepCompleted>(e).CalledTools),
            e => Assert.Equal("read_thing", Assert.IsType<AgentEvent.ToolCallProposed>(e).Call.Name),
            e => Assert.Equal("read_thing", Assert.IsType<AgentEvent.ToolCallStarted>(e).Call.Name),
            e => Assert.Equal(AgentCallOutcome.Succeeded, Assert.IsType<AgentEvent.ToolCallFinished>(e).Outcome),
            e => Assert.Equal(2, Assert.IsType<AgentEvent.StepStarted>(e).Step),
            e => Assert.Equal("It says hello.", Assert.IsType<AgentEvent.ContentDelta>(e).Text),
            e => Assert.False(Assert.IsType<AgentEvent.StepCompleted>(e).CalledTools),
            e =>
            {
                var completed = Assert.IsType<AgentEvent.Completed>(e);
                Assert.Equal(AgentStopReason.Answered, completed.Reason);
                Assert.Equal(2, completed.Steps);
            });

        Assert.Equal(1, probe.Ran);
    }

    [Fact]
    public async Task The_call_and_its_answer_are_in_the_next_request()
    {
        var conversationId = await NewChatAsync();
        var probe = new ProbeTool(
            "read_thing",
            behaviour: _ => AgentToolResult.Ok("namespace Widgets;"));

        var provider = Stepping(
            Calls(Call("read_thing", """{"path":"a.txt"}""", "call-7")),
            Answer("A namespace declaration."));

        await CollectAsync(Service(provider, [probe]).RunAsync(Ask(conversationId, "What is in a.txt?"), Token));

        // Read from the request rather than the database on purpose: this is the transcript as the
        // model receives it, which is the only form of it that affects the answer. An unanswered call
        // in this list, or an answer whose id does not match, is what makes a provider reject a turn.
        var replayed = provider.Requests[1].Messages;

        Assert.Collection(
            replayed,
            m => Assert.Equal("system", m.Role),
            m => Assert.Equal("What is in a.txt?", m.Content),
            m =>
            {
                Assert.Equal("assistant", m.Role);
                var call = Assert.Single(m.ToolCalls);
                Assert.Equal("call-7", call.Id);
                Assert.Equal("read_thing", call.Name);
            },
            m =>
            {
                Assert.Equal("tool", m.Role);
                Assert.Equal("call-7", m.ToolCallId);
                Assert.Equal("read_thing", m.Name);
                Assert.Equal("namespace Widgets;", m.Content);
            });
    }

    [Fact]
    public async Task A_read_is_never_put_to_the_user()
    {
        var conversationId = await NewChatAsync();
        var probe = new ProbeTool("read_thing");
        var approval = new ScriptedApproval();
        var provider = Stepping(Calls(Call("read_thing")), Answer("Done."));

        await CollectAsync(Service(provider, [probe], approval).RunAsync(Ask(conversationId), Token));

        // A dialog per file read would make the agent unusable, and there is nothing to undo.
        Assert.Empty(approval.Asked);
        Assert.Equal(1, probe.Ran);
    }

    [Fact]
    public async Task A_write_is_put_to_the_user_and_runs_when_they_say_yes()
    {
        var conversationId = await NewChatAsync();
        var probe = new ProbeTool("write_thing", AgentToolRisk.Write);
        var approval = new ScriptedApproval();
        var provider = Stepping(
            Calls(Call("write_thing", """{"path":"a.txt","content":"hi"}""")),
            Answer("Written."));

        var events = await CollectAsync(
            Service(provider, [probe], approval).RunAsync(Ask(conversationId), Token));

        var asked = Assert.Single(approval.Asked);
        Assert.Equal("write_thing", asked.ToolName);
        Assert.Equal(AgentToolRisk.Write, asked.Risk);
        Assert.Equal("""{"path":"a.txt","content":"hi"}""", asked.ArgumentsJson);
        Assert.False(asked.IsRepeat);

        Assert.Equal(1, probe.Ran);
        Assert.Single(events.OfType<AgentEvent.ToolCallStarted>());
    }

    [Fact]
    public async Task A_refusal_is_answered_as_a_tool_result_and_the_run_carries_on()
    {
        var conversationId = await NewChatAsync();
        var probe = new ProbeTool("write_thing", AgentToolRisk.Write);
        var approval = new ScriptedApproval(_ => AgentApprovalDecision.Deny("Not that file."));
        var provider = Stepping(
            Calls(Call("write_thing", """{"path":"a.txt"}""")),
            Answer("Understood - I have not changed it."));

        var events = await CollectAsync(
            Service(provider, [probe], approval).RunAsync(Ask(conversationId), Token));

        Assert.Equal(0, probe.Ran);
        Assert.Empty(events.OfType<AgentEvent.ToolCallStarted>());

        var finished = Assert.Single(events.OfType<AgentEvent.ToolCallFinished>());
        Assert.Equal(AgentCallOutcome.Denied, finished.Outcome);

        // What the model is told matters more than what the UI shows: a refusal it reads as a failure
        // gets retried through another tool, which is the one behaviour the gate cannot allow.
        Assert.Contains("Not that file.", finished.Message.Content);
        Assert.Contains("Do not look for another way", finished.Message.Content);

        // A no ends the call, not the task. The next step still happens.
        var completed = Assert.IsType<AgentEvent.Completed>(events[^1]);
        Assert.Equal(AgentStopReason.Answered, completed.Reason);
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task A_standing_yes_is_not_asked_again()
    {
        var conversationId = await NewChatAsync();
        var probe = new ProbeTool("write_thing", AgentToolRisk.Write);
        var approval = new ScriptedApproval(_ => AgentApprovalDecision.AllowForRun());
        var provider = Stepping(
            Calls(Call("write_thing", """{"path":"a.txt"}""", "call-1")),
            Calls(Call("write_thing", """{"path":"b.txt"}""", "call-2")),
            Answer("Both written."));

        await CollectAsync(Service(provider, [probe], approval).RunAsync(Ask(conversationId), Token));

        Assert.Single(approval.Asked);
        Assert.Equal(2, probe.Ran);
    }

    [Fact]
    public async Task Running_a_program_is_asked_about_every_time_whatever_was_said_before()
    {
        var conversationId = await NewChatAsync();
        var probe = new ProbeTool("run_thing", AgentToolRisk.Execute);
        var approval = new ScriptedApproval(_ => AgentApprovalDecision.AllowForRun());
        var provider = Stepping(
            Calls(Call("run_thing", """{"command":"dotnet build"}""", "call-1")),
            Calls(Call("run_thing", """{"command":"dotnet test"}""", "call-2")),
            Answer("Both ran."));

        await CollectAsync(Service(provider, [probe], approval).RunAsync(Ask(conversationId), Token));

        // "Yes to everything" is a promise about edits, which can be read back and undone. It is not a
        // promise about running programs, and a standing yes must never be stretched into one.
        Assert.Equal(2, approval.Asked.Count);
        Assert.Equal(2, probe.Ran);
    }

    [Fact]
    public async Task Stop_while_the_question_is_open_means_the_call_never_happens()
    {
        var conversationId = await NewChatAsync();
        var probe = new ProbeTool("write_thing", AgentToolRisk.Write);

        using var stop = new CancellationTokenSource();

        var approval = new ScriptedApproval(before: () =>
        {
            stop.Cancel();
            return Task.CompletedTask;
        });

        var provider = Stepping(
            Calls(Call("write_thing", """{"path":"a.txt"}""")),
            Answer("Never reached."));

        var events = await CollectAsync(
            Service(provider, [probe], approval).RunAsync(Ask(conversationId), stop.Token));

        Assert.Equal(0, probe.Ran);
        Assert.Empty(events.OfType<AgentEvent.ToolCallStarted>());
        Assert.IsType<AgentEvent.Cancelled>(events[^1]);
        Assert.Single(provider.Requests);

        // No row for the call, which is what makes this safe to carry on from: a call with no answer
        // is dropped when the history is replayed, so the next turn never sees a half-finished edit.
        var messages = await MessagesAsync(conversationId);
        Assert.DoesNotContain(messages, m => m.Role == MessageRole.Tool);
    }

    [Fact]
    public async Task The_same_call_made_once_too_often_is_refused_instead_of_run()
    {
        var conversationId = await NewChatAsync();
        var probe = new ProbeTool("read_thing");
        var provider = Stepping(
            Calls(Call("read_thing", """{"path":"a.txt"}""", "call-1")),
            Calls(Call("read_thing", """{"path":"a.txt"}""", "call-2")),
            Calls(Call("read_thing", """{"path":"a.txt"}""", "call-3")),
            Answer("It says hello."));

        var events = await CollectAsync(
            Service(provider, [probe]).RunAsync(Ask(conversationId), Token));

        // Read three times, run twice. The third is refused before the tool is touched, which is what
        // breaks the loop a model gets into when it fails to notice the answer it already has.
        Assert.Equal(2, probe.Ran);
        Assert.Equal(2, events.OfType<AgentEvent.ToolCallProposed>().Count());

        var refused = events.OfType<AgentEvent.ToolCallFinished>().Last();
        Assert.Equal(AgentCallOutcome.Failed, refused.Outcome);
        Assert.Equal("read_thing: repeated call refused", refused.Summary);
        Assert.Contains("already made 2 times", refused.Message.Content);
    }

    [Fact]
    public async Task Identical_writes_never_trip_the_repeat_limit()
    {
        var conversationId = await NewChatAsync();
        var probe = new ProbeTool("write_thing", AgentToolRisk.Write);
        var provider = Stepping(
            Calls(Call("write_thing", """{"path":"log.txt","content":"x"}""", "call-1")),
            Calls(Call("write_thing", """{"path":"log.txt","content":"x"}""", "call-2")),
            Calls(Call("write_thing", """{"path":"log.txt","content":"x"}""", "call-3")),
            Answer("Appended three times."));

        await CollectAsync(Service(provider, [probe]).RunAsync(Ask(conversationId), Token));

        // A repeated read is a model going in circles; a repeated write is a model doing the job
        // three times. The counts are forgotten after every successful change, so this is allowed.
        Assert.Equal(3, probe.Ran);
    }

    [Fact]
    public async Task The_last_permitted_step_withholds_the_tools_and_says_why()
    {
        var conversationId = await NewChatAsync();
        _settings.With<AgentSettings>(agent => agent.MaxSteps = 2);

        var probe = new ProbeTool("read_thing");
        var provider = Stepping(
            Calls(Call("read_thing")),
            Answer("I read one file; the rest is undone."));

        var events = await CollectAsync(
            Service(provider, [probe]).RunAsync(Ask(conversationId), Token));

        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(AIToolChoice.Auto, provider.Requests[0].ToolChoice);
        Assert.Equal(AIToolChoice.None, provider.Requests[1].ToolChoice);

        // The definitions stay attached: a provider handed a history full of calls and no tool list
        // rejects the turn outright.
        Assert.NotEmpty(provider.Requests[1].Tools);

        var note = provider.Requests[1].Messages[^1];
        Assert.Equal("system", note.Role);
        Assert.Equal(AgentPrompt.LastStep, note.Content);

        // Answered would be a lie here: the model stopped because it ran out of steps, and the user
        // has to be able to tell that from a task that was actually finished.
        Assert.Equal(AgentStopReason.StepLimit, Assert.IsType<AgentEvent.Completed>(events[^1]).Reason);
    }

    [Fact]
    public async Task A_model_that_cannot_call_tools_is_refused_before_anything_is_sent()
    {
        var conversationId = await NewChatAsync();
        var probe = new ProbeTool("read_thing");
        var provider = Stepping(Answer("Never reached."));

        // A catalogue that listed this model's parameters and left tools out of them. That is a
        // published no, which is the only kind the loop refuses on.
        var registry = new StubProviderRegistry(provider)
            .WithModel(Model(supportsTools: false, "max_tokens", "temperature"));

        var events = await CollectAsync(
            Service(provider, [probe], registry: registry).RunAsync(Ask(conversationId), Token));

        var failed = Assert.IsType<AgentEvent.Failed>(events[^1]);
        Assert.Equal(AIErrorKind.InvalidRequest, failed.Kind);
        Assert.Contains("cannot call tools", failed.UserMessage);

        // Refused up front, not discovered halfway: nothing was asked of the provider at all.
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task A_catalogue_that_says_nothing_about_tools_is_not_read_as_a_refusal()
    {
        // NVIDIA's /v1/models returns an id and an owner per model and no capability flags at all,
        // so every model it serves is cached with SupportsTools false. Refusing on that flag alone
        // meant agent mode failed on the user's entire provider - including Kimi K3 and MiniMax M3,
        // which call tools perfectly well - before a single request went out.
        var conversationId = await NewChatAsync();
        var probe = new ProbeTool("read_thing");
        var provider = Stepping(
            Calls(Call("read_thing", """{"path":"a.txt"}""")),
            Answer("It says hello."));

        var registry = new StubProviderRegistry(provider).WithModel(Model(supportsTools: false));

        var events = await CollectAsync(
            Service(provider, [probe], registry: registry).RunAsync(Ask(conversationId), Token));

        Assert.Empty(events.OfType<AgentEvent.Failed>());

        // The tools were offered rather than quietly withheld, so the model could call one.
        Assert.NotEmpty(provider.Requests[0].Tools);
        Assert.Equal(AIToolChoice.Auto, provider.Requests[0].ToolChoice);
        Assert.Equal(1, probe.Ran);
        Assert.Equal(AgentStopReason.Answered, Assert.IsType<AgentEvent.Completed>(events[^1]).Reason);
    }

    [Fact]
    public async Task A_call_naming_a_tool_that_does_not_exist_is_answered_without_being_proposed()
    {
        var conversationId = await NewChatAsync();
        var probe = new ProbeTool("read_thing");
        var provider = Stepping(
            Calls(Call("delete_everything")),
            Answer("I used the wrong name."));

        var events = await CollectAsync(
            Service(provider, [probe]).RunAsync(Ask(conversationId), Token));

        // There is nothing to propose and nothing to approve, so the user is not shown a dialog about
        // a tool that does not exist. The model is told which ones do.
        Assert.Empty(events.OfType<AgentEvent.ToolCallProposed>());
        Assert.Empty(events.OfType<AgentEvent.ToolCallStarted>());

        var finished = Assert.Single(events.OfType<AgentEvent.ToolCallFinished>());
        Assert.Equal(AgentCallOutcome.Failed, finished.Outcome);
        Assert.Contains("no tool called 'delete_everything'", finished.Message.Content);
        Assert.Contains("read_thing", finished.Message.Content);
    }

    [Fact]
    public async Task Malformed_arguments_are_answered_without_asking_or_running()
    {
        var conversationId = await NewChatAsync();
        var probe = new ProbeTool("write_thing", AgentToolRisk.Write);
        var approval = new ScriptedApproval();
        var provider = Stepping(
            Calls(Call("write_thing", """{"path": """)),
            Answer("I sent broken JSON."));

        var events = await CollectAsync(
            Service(provider, [probe], approval).RunAsync(Ask(conversationId), Token));

        // Nobody should be shown a dialog about a truncated JSON object, and the tool should not be
        // handed arguments it would only reject.
        Assert.Empty(approval.Asked);
        Assert.Empty(events.OfType<AgentEvent.ToolCallProposed>());
        Assert.Equal(0, probe.Ran);

        var finished = Assert.Single(events.OfType<AgentEvent.ToolCallFinished>());
        Assert.Equal("write_thing: malformed arguments", finished.Summary);
    }

    [Fact]
    public async Task A_tool_that_throws_becomes_a_failed_result_and_the_run_carries_on()
    {
        var conversationId = await NewChatAsync();
        var probe = new ProbeTool(
            "read_thing",
            behaviour: _ => throw new InvalidOperationException("The disk caught fire."));

        var provider = Stepping(
            Calls(Call("read_thing")),
            Answer("That tool is broken."));

        var events = await CollectAsync(
            Service(provider, [probe]).RunAsync(Ask(conversationId), Token));

        // A defect in one tool is not a reason to lose the task. The model is told the call failed and
        // gets to decide what to do about it, which is the same thing it would do for a missing file.
        var finished = Assert.Single(events.OfType<AgentEvent.ToolCallFinished>());
        Assert.Equal(AgentCallOutcome.Failed, finished.Outcome);
        Assert.Equal(1, probe.Ran);
        Assert.Equal(AgentStopReason.Answered, Assert.IsType<AgentEvent.Completed>(events[^1]).Reason);
    }

    [Fact]
    public async Task The_system_prompt_keeps_the_users_own_words_and_names_the_project_folder()
    {
        var conversationId = await NewChatAsync();
        _settings.With<ChatSettings>(chat => chat.SystemPrompt = "Answer in Russian.");

        var provider = Stepping(Answer("Готово."));

        await CollectAsync(
            Service(provider, [new ProbeTool("read_thing")]).RunAsync(Ask(conversationId), Token));

        var system = provider.LastRequest.Messages[0];
        Assert.Equal("system", system.Role);

        // The user's instruction comes first and the agent rules follow, rather than replacing it.
        // Someone who asked for Russian meant it during a task as well.
        Assert.StartsWith("Answer in Russian.", system.Content);
        Assert.Contains(_root, system.Content);
        Assert.Contains("Look before you change anything", system.Content);
    }

    [Fact]
    public async Task The_time_budget_stops_the_run_and_keeps_what_had_arrived()
    {
        var conversationId = await NewChatAsync();
        _settings.With<AgentSettings>(agent => agent.MaxDurationSeconds = 1);

        var provider = new ScriptedProvider(ProviderId, (_, ct) => Crawl(ct));

        var events = await CollectAsync(
            Service(provider, [new ProbeTool("read_thing")]).RunAsync(Ask(conversationId), Token));

        // Out of time is not a failure and not a cancellation: nothing went wrong and nobody pressed
        // Stop, so it ends as a completion whose reason says the clock ran out.
        var completed = Assert.IsType<AgentEvent.Completed>(events[^1]);
        Assert.Equal(AgentStopReason.TimeLimit, completed.Reason);

        var assistant = (await MessagesAsync(conversationId)).Last(m => m.Role == MessageRole.Assistant);
        Assert.Equal("Half an ", assistant.Content);

        // Cancelled rather than Complete, so the next turn does not replay half a sentence as if the
        // model had finished saying it.
        Assert.Equal(MessageStatus.Cancelled, assistant.Status);
    }

    [Fact]
    public async Task Time_spent_waiting_for_an_answer_is_not_charged_to_the_budget()
    {
        var conversationId = await NewChatAsync();
        _settings.With<AgentSettings>(agent => agent.MaxDurationSeconds = 1);

        var probe = new ProbeTool("write_thing", AgentToolRisk.Write);

        // Longer than the whole budget, which is the ordinary case: a person reading a diff takes far
        // longer than the model takes to produce it.
        var approval = new ScriptedApproval(before: () => Task.Delay(TimeSpan.FromSeconds(1.4)));

        var provider = Stepping(
            Calls(Call("write_thing", """{"path":"a.txt"}""")),
            Answer("Written."));

        var events = await CollectAsync(
            Service(provider, [probe], approval).RunAsync(Ask(conversationId), Token));

        Assert.Equal(1, probe.Ran);
        Assert.Equal(AgentStopReason.Answered, Assert.IsType<AgentEvent.Completed>(events[^1]).Reason);
    }

    [Fact]
    public async Task Stop_between_steps_ends_the_run_without_another_request()
    {
        var conversationId = await NewChatAsync();
        var probe = new ProbeTool("read_thing");
        var provider = Stepping(
            Calls(Call("read_thing")),
            Answer("Never reached."));

        using var stop = new CancellationTokenSource();

        var events = new List<AgentEvent>();

        await foreach (var evt in Service(provider, [probe]).RunAsync(Ask(conversationId), stop.Token))
        {
            events.Add(evt);

            if (evt is AgentEvent.StepCompleted)
            {
                stop.Cancel();
            }
        }

        Assert.IsType<AgentEvent.Cancelled>(events[^1]);
        Assert.Single(provider.Requests);
        Assert.Equal(0, probe.Ran);
    }

    #region Harness

    private async Task<Guid> NewChatAsync() =>
        (await _conversations.CreateAsync("Chat", cancellationToken: Token)).Id;

    /// <summary>
    /// The real loop over the real store, with the provider, the tools and the gate scripted.
    /// </summary>
    /// <remarks>
    /// The default gate allows everything, because most tests are about something other than the
    /// approval rules and a gate that refused by default would make each of them assert twice.
    /// </remarks>
    private AgentService Service(
        IAIProvider provider,
        IEnumerable<IAgentTool>? tools = null,
        IAgentApproval? approval = null,
        IProviderRegistry? registry = null) =>
        new(_conversations,
            registry ?? new StubProviderRegistry(provider).WithModel(Model()),
            new ContextBuilder(_conversations, NullLogger<ContextBuilder>.Instance),
            _settings,
            new AgentToolRegistry(tools ?? []),
            approval ?? new ScriptedApproval(),
            _workspace,
            NullLogger<AgentService>.Instance);

    /// <summary>
    /// A model that can call tools, which is the only capability the loop insists on.
    /// </summary>
    /// <remarks>
    /// The parameter list is what separates a model that cannot call tools from a catalogue that
    /// never said either way: left empty, <paramref name="supportsTools"/> being false means only
    /// that nobody claimed it.
    /// </remarks>
    private static ModelInfo Model(bool supportsTools = true, params string[] supportedParameters) =>
        new()
        {
            ProviderId = ProviderId,
            ProviderName = ProviderId,
            ModelId = ModelId,
            Name = ModelId,
            SupportsStreaming = true,
            SupportsTools = supportsTools,
            SupportedParameters = supportedParameters,
        };

    private static AgentRunRequest Ask(Guid conversationId, string content = "Rename the widget.") =>
        new()
        {
            ConversationId = conversationId,
            Content = content,
            ProviderId = ProviderId,
            ModelId = ModelId,
        };

    private async Task<IReadOnlyList<MessageDto>> MessagesAsync(Guid conversationId) =>
        (await _conversations.GetAsync(conversationId, Token))!.Messages;

    private static async Task<List<AgentEvent>> CollectAsync(IAsyncEnumerable<AgentEvent> events)
    {
        var collected = new List<AgentEvent>();

        await foreach (var evt in events)
        {
            collected.Add(evt);
        }

        return collected;
    }

    /// <summary>
    /// A provider that answers each successive step from its own script.
    /// </summary>
    /// <remarks>
    /// One script per step is what makes a loop testable at all: the interesting cases are all about
    /// what the second request contains, given what the first one produced. Asking for a step the
    /// script does not have throws rather than repeating the last one, so a loop that fails to stop
    /// fails the test instead of hanging it.
    /// </remarks>
    private static ScriptedProvider Stepping(params AIStreamEvent[][] steps)
    {
        var next = 0;

        return new ScriptedProvider(ProviderId, (_, ct) =>
        {
            var step = next++;

            return step < steps.Length
                ? Replay(steps[step], ct)
                : throw new InvalidOperationException(
                    $"The provider was asked for step {step + 1}, but the script has {steps.Length}.");
        });
    }

    private static AIStreamEvent[] Answer(string text) =>
        [new AIStreamEvent.ContentDelta(text), new AIStreamEvent.Completed("stop")];

    private static AIStreamEvent[] Calls(params AIToolCall[] calls) =>
        [new AIStreamEvent.ToolCalls(calls), new AIStreamEvent.Completed("tool_calls")];

    private static AIToolCall Call(string name, string argumentsJson = "{}", string id = "call-1") =>
        new(id, name, argumentsJson);

    private static async IAsyncEnumerable<AIStreamEvent> Replay(
        AIStreamEvent[] events,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var evt in events)
        {
            // Observed between events, as a real provider does at every network read. This is what
            // makes the cancellation and deadline tests deterministic rather than lucky.
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return evt;
        }
    }

    /// <summary>A stream that produces a few words and then takes longer than any budget allows.</summary>
    private static async IAsyncEnumerable<AIStreamEvent> Crawl(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new AIStreamEvent.ContentDelta("Half an ");
        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        yield return new AIStreamEvent.ContentDelta("answer.");
        yield return new AIStreamEvent.Completed("stop");
    }

    #endregion

    /// <summary>
    /// A tool that records what it was asked to do and answers however the test says.
    /// </summary>
    private sealed class ProbeTool : IAgentTool
    {
        private readonly Func<AgentToolArguments, AgentToolResult> _behaviour;

        public ProbeTool(
            string name,
            AgentToolRisk risk = AgentToolRisk.Read,
            Func<AgentToolArguments, AgentToolResult>? behaviour = null)
        {
            Name = name;
            Risk = risk;
            _behaviour = behaviour ?? (_ => AgentToolResult.Ok($"{name} did it.", $"{name}: ok"));
        }

        public string Name { get; }

        public string Description => $"A {Risk} tool that exists to be called by a test.";

        public string ParametersJsonSchema =>
            """{"type":"object","properties":{"path":{"type":"string"}}}""";

        public AgentToolRisk Risk { get; }

        /// <summary>Every set of arguments the tool was handed, in order.</summary>
        public List<AgentToolArguments> Calls { get; } = [];

        public int Ran => Calls.Count;

        public Task<AgentToolResult> ExecuteAsync(
            AgentToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments);
            return Task.FromResult(_behaviour(arguments));
        }
    }

    /// <summary>
    /// A gate that records what it was asked and answers however the test says.
    /// </summary>
    /// <remarks>
    /// The <c>before</c> hook is what makes the awkward cases reachable: a user who presses Stop with
    /// the dialog open, and one who takes longer to read a diff than the run's whole time budget.
    /// </remarks>
    private sealed class ScriptedApproval : IAgentApproval
    {
        private readonly Func<AgentApprovalRequest, AgentApprovalDecision> _answer;
        private readonly Func<Task>? _before;

        public ScriptedApproval(
            Func<AgentApprovalRequest, AgentApprovalDecision>? answer = null,
            Func<Task>? before = null)
        {
            _answer = answer ?? (_ => AgentApprovalDecision.Allow());
            _before = before;
        }

        /// <summary>Every question put to the user, in order.</summary>
        public List<AgentApprovalRequest> Asked { get; } = [];

        public async Task<AgentApprovalDecision> RequestAsync(
            AgentApprovalRequest request,
            CancellationToken cancellationToken = default)
        {
            Asked.Add(request);

            if (_before is not null)
            {
                await _before().ConfigureAwait(false);
            }

            // A real dialog closes when Stop is pressed, so this one does too.
            cancellationToken.ThrowIfCancellationRequested();

            return _answer(request);
        }
    }
}
