using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using AIClient.Application.Configuration;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Domain.Enums;
using AIClient.Domain.Interfaces;
using AIClient.Domain.Models;
using Microsoft.Extensions.Logging;

namespace AIClient.Application.Services;

/// <summary>
/// The loop: ask the model, do what it asked for, ask again, stop.
/// </summary>
/// <remarks>
/// <para>
/// The ordering discipline is <see cref="ChatService"/>'s, kept deliberately identical. Every step
/// commits its assistant row and then its tool rows before the next request is built, so the history
/// the next step sees is the history on disk - not a list held in memory that a crash would take with
/// it. It also means the loop has no state worth recovering: what happened is in the transcript.
/// </para>
/// <para>
/// Three things bound a run, and they fail differently, which is why there are three. The step budget
/// stops a model that is making progress too slowly to be worth paying for. The clock stops one step
/// that hangs, which no step count can catch. The repeat check stops the characteristic failure of a
/// tool-using model - the same call forever - which neither of the others catches quickly, because a
/// model can burn twenty-five fast steps reading one file over and over.
/// </para>
/// <para>
/// Nothing here decides what is allowed. <see cref="IAgentApproval"/> answers that, and the loop's
/// only policy is which calls it does not bother asking about (reads), how long an answer is
/// remembered (one run, and never for a command), and that a refusal is reported to the model as an
/// ordinary tool result rather than ending the turn.
/// </para>
/// </remarks>
public sealed class AgentService : IAgentService
{
    /// <summary>
    /// How often streamed text is flushed to the database, as in chat: often enough that a crash
    /// costs a sentence, rarely enough that it is not a write per token.
    /// </summary>
    private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(1);

    private readonly IConversationService _conversations;
    private readonly IProviderRegistry _providers;
    private readonly IContextBuilder _contextBuilder;
    private readonly ISettingsService _settings;
    private readonly IAgentToolRegistry _registry;
    private readonly IAgentApproval _approval;
    private readonly IWorkspaceService _workspace;
    private readonly ILogger<AgentService> _logger;

    public AgentService(
        IConversationService conversations,
        IProviderRegistry providers,
        IContextBuilder contextBuilder,
        ISettingsService settings,
        IAgentToolRegistry registry,
        IAgentApproval approval,
        IWorkspaceService workspace,
        ILogger<AgentService> logger)
    {
        _conversations = conversations;
        _providers = providers;
        _contextBuilder = contextBuilder;
        _settings = settings;
        _registry = registry;
        _approval = approval;
        _workspace = workspace;
        _logger = logger;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var task = await _conversations.AddMessageAsync(
            request.ConversationId,
            new NewMessage
            {
                Role = MessageRole.User,
                Content = request.Content,
                Attachments = request.Attachments,
            },
            cancellationToken).ConfigureAwait(false);

        yield return new AgentEvent.UserMessageSaved(task);

        var title = await TryTitleAsync(request, cancellationToken).ConfigureAwait(false);

        if (title is not null)
        {
            yield return new AgentEvent.TitleGenerated(request.ConversationId, title);
        }

        var run = new RunState(request, _settings.Current.Agent, cancellationToken);

        try
        {
            await foreach (var progress in RunAsync(run).ConfigureAwait(false))
            {
                yield return progress;
            }
        }
        finally
        {
            run.Dispose();
        }
    }

    /// <summary>
    /// Every step of one run, from the first request to the single terminal event.
    /// </summary>
    /// <remarks>
    /// The budgets are checked here, at the top of a step, and never between "the model asked for
    /// something" and "the something happened". A limit that could fire in that gap would leave a
    /// call with no result in the transcript, which is the one shape the next request cannot use.
    /// </remarks>
    private async IAsyncEnumerable<AgentEvent> RunAsync(RunState run)
    {
        while (true)
        {
            if (run.UserToken.IsCancellationRequested)
            {
                yield return new AgentEvent.Cancelled(run.LastMessageId, run.Step);
                yield break;
            }

            if (run.Step > 0 && run.OutOfTime)
            {
                yield return Finish(run, AgentStopReason.TimeLimit);
                yield break;
            }

            if (run.Step >= run.MaxSteps)
            {
                // Reached when the step whose tools were withheld asked for one anyway, which some
                // providers allow through. Its calls were carried out; there is no further step.
                yield return Finish(run, AgentStopReason.StepLimit);
                yield break;
            }

            run.Step++;

            // On the last permitted step the tools are withheld, so the run ends in a sentence
            // rather than on a file listing. See AgentStopReason.StepLimit.
            var lastStep = run.Step >= run.MaxSteps;

            var placeholder = await _conversations.AddMessageAsync(
                run.ConversationId,
                new NewMessage
                {
                    Role = MessageRole.Assistant,
                    Content = string.Empty,
                    Status = MessageStatus.Streaming,
                    ProviderId = run.ProviderId,
                    ModelId = run.ModelId,
                },
                CancellationToken.None).ConfigureAwait(false);

            run.LastMessageId = placeholder.Id;
            yield return new AgentEvent.StepStarted(run.Step, placeholder);

            var preparation = await PrepareAsync(run, offerTools: !lastStep).ConfigureAwait(false);

            if (preparation.Failure is { } early)
            {
                await PersistFailureAsync(placeholder.Id, string.Empty, early).ConfigureAwait(false);
                yield return Failure(placeholder.Id, early);
                yield break;
            }

            var provider = preparation.Provider!;

            var buffer = new StringBuilder();
            var clock = Stopwatch.StartNew();
            var lastPersist = clock.Elapsed;
            int? inputTokens = null;
            int? outputTokens = null;
            IReadOnlyList<AIToolCall> calls = [];

            // Stepped by hand rather than with `await foreach`, as in chat, so that a mid-stream
            // exception can be caught without a try/catch wrapped around a yield.
            var stream = provider.StreamChatAsync(preparation.Request!, run.RunToken)
                .GetAsyncEnumerator(run.RunToken);

            try
            {
                while (true)
                {
                    AIStreamEvent? current;
                    AIProviderException? failure = null;
                    var halted = false;

                    try
                    {
                        current = await stream.MoveNextAsync().ConfigureAwait(false) ? stream.Current : null;
                    }
                    catch (OperationCanceledException) when (run.RunToken.IsCancellationRequested)
                    {
                        current = null;
                        halted = true;
                    }
                    catch (Exception ex)
                    {
                        current = null;
                        failure = ProviderErrorMapper.FromException(ex, provider.DisplayName, provider.Id);
                    }

                    if (halted)
                    {
                        clock.Stop();
                        await PersistCancellationAsync(placeholder.Id, buffer.ToString(), clock).ConfigureAwait(false);

                        // Which of the two tokens fired decides what the user is told: they pressed
                        // Stop, or the run ran out of time on its own.
                        yield return run.UserToken.IsCancellationRequested
                            ? new AgentEvent.Cancelled(placeholder.Id, run.Step)
                            : Finish(run, AgentStopReason.TimeLimit);

                        yield break;
                    }

                    if (failure is not null)
                    {
                        clock.Stop();
                        await PersistFailureAsync(placeholder.Id, buffer.ToString(), failure).ConfigureAwait(false);
                        yield return Failure(placeholder.Id, failure);
                        yield break;
                    }

                    if (current is null)
                    {
                        // The provider ended without a terminal event. Take what arrived.
                        break;
                    }

                    switch (current)
                    {
                        case AIStreamEvent.ContentDelta delta:
                            buffer.Append(delta.Text);
                            yield return new AgentEvent.ContentDelta(placeholder.Id, delta.Text);

                            if (clock.Elapsed - lastPersist > PersistInterval)
                            {
                                lastPersist = clock.Elapsed;
                                await _conversations.UpdateMessageAsync(
                                    new MessageUpdate { MessageId = placeholder.Id, Content = buffer.ToString() },
                                    CancellationToken.None).ConfigureAwait(false);
                            }

                            break;

                        case AIStreamEvent.ReasoningDelta reasoning:
                            // Surfaced here, unlike in chat: a step that spends half a minute
                            // deciding which file to open is otherwise half a minute of nothing.
                            yield return new AgentEvent.ReasoningDelta(placeholder.Id, reasoning.Text);
                            break;

                        case AIStreamEvent.ToolCalls requested:
                            // The provider has reassembled the whole set. This, not finish_reason,
                            // is what decides whether the run continues.
                            calls = requested.Calls;
                            break;

                        case AIStreamEvent.Usage usage:
                            inputTokens = usage.InputTokens ?? inputTokens;
                            outputTokens = usage.OutputTokens ?? outputTokens;
                            break;

                        case AIStreamEvent.Error error:
                            clock.Stop();
                            var reported = new AIProviderException(
                                error.Kind, error.Message, error.TechnicalDetails, provider.Id);

                            await PersistFailureAsync(placeholder.Id, buffer.ToString(), reported)
                                .ConfigureAwait(false);

                            yield return Failure(placeholder.Id, reported);
                            yield break;

                        case AIStreamEvent.Completed:
                            goto streamed;

                        case AIStreamEvent.ToolCallDelta:
                            // Progress only, and the arguments arrive in fragments that are not
                            // valid JSON on their own. Dropped; the reassembled set is what counts.
                            break;
                    }
                }

                streamed:
                clock.Stop();

                var content = buffer.ToString();

                // Neither words nor a call is nothing to continue from, and another step would send
                // the same request and get the same nothing.
                if (calls.Count == 0 && string.IsNullOrWhiteSpace(content))
                {
                    var empty = new AIProviderException(
                        AIErrorKind.Unknown, "The model returned an empty response.", null, provider.Id);

                    await PersistFailureAsync(placeholder.Id, string.Empty, empty).ConfigureAwait(false);
                    yield return Failure(placeholder.Id, empty);
                    yield break;
                }

                // The step's own row goes in before its tool rows, and both before the next request
                // is built. The transcript is the loop's memory, so it is written in the order it
                // will be read.
                await _conversations.UpdateMessageAsync(
                    new MessageUpdate
                    {
                        MessageId = placeholder.Id,
                        Content = content,
                        Status = MessageStatus.Complete,
                        InputTokens = inputTokens ?? preparation.EstimatedInputTokens,
                        OutputTokens = outputTokens,
                        GenerationTimeMs = (int)clock.ElapsedMilliseconds,
                        ToolCallsJson = calls.Count > 0 ? AgentTranscript.Write(calls) : null,
                    },
                    CancellationToken.None).ConfigureAwait(false);

                yield return new AgentEvent.StepCompleted(
                    run.Step,
                    placeholder.Id,
                    inputTokens ?? preparation.EstimatedInputTokens,
                    outputTokens,
                    calls.Count > 0);
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            if (calls.Count == 0)
            {
                // The model answered with words. Reported as StepLimit when this was the step whose
                // tools were withheld: the run did stop because the budget ran out, and saying it
                // answered would hide that there may be more to do.
                _logger.LogInformation(
                    "Agent run finished for conversation {ConversationId} after {Steps} step(s) in {ElapsedMs} ms.",
                    run.ConversationId, run.Step, (int)run.Elapsed.TotalMilliseconds);

                yield return Finish(run, lastStep ? AgentStopReason.StepLimit : AgentStopReason.Answered);
                yield break;
            }

            await foreach (var acted in ActAsync(run, placeholder.Id, calls).ConfigureAwait(false))
            {
                yield return acted;
            }

            if (run.Halted)
            {
                yield return run.UserToken.IsCancellationRequested
                    ? new AgentEvent.Cancelled(placeholder.Id, run.Step)
                    : Finish(run, AgentStopReason.TimeLimit);

                yield break;
            }
        }
    }

    /// <summary>
    /// Puts every call of one step through the gate, runs what survives it, and records an answer
    /// for each - including for the ones that never ran.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every call gets a row, whatever became of it. A call with no answer is not a smaller version
    /// of a call that failed: it is a hole in the next request, which providers reject outright. The
    /// one exception is a run that stops mid-way, where nothing is fabricated on the model's behalf
    /// and the replay drops the unanswered calls instead.
    /// </para>
    /// <para>
    /// The order within a call is deliberate. Arguments are parsed and the repeat check runs before
    /// the user is asked anything, so nobody is shown a dialog about malformed JSON and a model
    /// stuck in a loop cannot turn the approval prompt into the loop.
    /// </para>
    /// </remarks>
    private async IAsyncEnumerable<AgentEvent> ActAsync(
        RunState run,
        Guid stepMessageId,
        IReadOnlyList<AIToolCall> calls)
    {
        foreach (var call in calls)
        {
            if (run.UserToken.IsCancellationRequested)
            {
                run.Halted = true;
                yield break;
            }

            if (!_registry.TryGet(call.Name, out var tool))
            {
                // Naming a tool that does not exist is not something to ask the user about, so this
                // call is never proposed - it goes straight to its answer.
                yield return await RecordAsync(
                    run,
                    call,
                    AgentToolResult.Fail(
                        $"There is no tool called '{call.Name}'. The tools you have are: {ToolNames()}.",
                        $"Unknown tool '{call.Name}'"),
                    AgentCallOutcome.Failed).ConfigureAwait(false);

                continue;
            }

            if (!AgentToolArguments.TryParse(call.ArgumentsJson, out var arguments, out var parseError))
            {
                yield return await RecordAsync(
                    run,
                    call,
                    AgentToolResult.Fail(parseError, $"{tool.Name}: malformed arguments"),
                    AgentCallOutcome.Failed).ConfigureAwait(false);

                continue;
            }

            if (run.TooManyAttempts(call, out var attempts))
            {
                yield return await RecordAsync(
                    run,
                    call,
                    RepeatRefusal(tool, attempts),
                    AgentCallOutcome.Failed).ConfigureAwait(false);

                continue;
            }

            yield return new AgentEvent.ToolCallProposed(stepMessageId, call, tool.Risk);

            var decision = await DecideAsync(run, call, tool, arguments!).ConfigureAwait(false);

            if (decision is null)
            {
                // Stopped while the question was open. The call simply never happened.
                run.Halted = true;
                yield break;
            }

            if (!decision.IsAllowed)
            {
                yield return await RecordAsync(
                    run, call, Denial(tool, decision), AgentCallOutcome.Denied).ConfigureAwait(false);

                continue;
            }

            run.Remember(call, tool, decision);

            yield return new AgentEvent.ToolCallStarted(stepMessageId, call);

            var result = await ExecuteAsync(run, tool, arguments!).ConfigureAwait(false);

            if (result is null)
            {
                run.Halted = true;
                yield break;
            }

            if (result.Success && tool.Risk != AgentToolRisk.Read)
            {
                // Something in the workspace changed, so a call that was a pointless repeat a
                // moment ago may now be exactly the right thing to do. See MaxIdenticalCalls.
                run.ForgetAttempts();
            }

            yield return await RecordAsync(
                run,
                call,
                result,
                result.Success ? AgentCallOutcome.Succeeded : AgentCallOutcome.Failed).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes one call's answer into the transcript and reports it.
    /// </summary>
    /// <remarks>
    /// <see cref="CancellationToken.None"/>, always. By the time this runs the call has either
    /// happened or been decided against, and dropping the record because Stop was pressed a moment
    /// later would leave a file that was written and a model that will never be told.
    /// </remarks>
    private async Task<AgentEvent> RecordAsync(
        RunState run,
        AIToolCall call,
        AgentToolResult result,
        AgentCallOutcome outcome)
    {
        var message = await _conversations.AddMessageAsync(
            run.ConversationId,
            new NewMessage
            {
                Role = MessageRole.Tool,
                Content = result.Content,
                ToolCallId = call.Id,
                ToolName = call.Name,
                ToolSucceeded = result.Success,
            },
            CancellationToken.None).ConfigureAwait(false);

        return new AgentEvent.ToolCallFinished(call, outcome, message, result.Summary, result.Detail);
    }

    /// <summary>
    /// Decides whether one call may be made. Null means the run was stopped instead of answered.
    /// </summary>
    /// <remarks>
    /// The only policy the loop keeps to itself: a read is never put to the user, and a standing yes
    /// covers a tool for the rest of the run unless it runs programs.
    /// </remarks>
    private async Task<AgentApprovalDecision?> DecideAsync(
        RunState run,
        AIToolCall call,
        IAgentTool tool,
        AgentToolArguments arguments)
    {
        if (tool.Risk == AgentToolRisk.Read || run.IsAllowedForRun(tool))
        {
            return AgentApprovalDecision.Allow();
        }

        var described = await DescribeAsync(run, tool, arguments).ConfigureAwait(false);

        var request = new AgentApprovalRequest
        {
            ConversationId = run.ConversationId,
            ToolName = tool.Name,
            Risk = tool.Risk,
            ArgumentsJson = call.ArgumentsJson ?? string.Empty,
            Summary = described.Summary,
            Preview = described.Preview,
            IsRepeat = run.HasApproved(call),
        };

        var asked = run.Elapsed;

        // Suspended for the duration of the question, not merely subtracted afterwards. A deadline
        // that fires while the dialog is open cancels the run for good - CancelAfter cannot take a
        // cancellation back - so the time given back below would arrive too late to matter.
        run.HoldDeadline();

        try
        {
            // The user's token rather than the run's: a question is closed by Stop, but never by the
            // deadline. The time budget is a ceiling on the work, and a person reading a diff is not
            // the work - which is why the wait is given back below.
            return await _approval.RequestAsync(request, run.UserToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // A gate that throws has not said yes. Treated as a denial so the run continues and the
            // model is told, rather than the whole task dying on a broken dialog.
            _logger.LogError(ex, "The approval gate failed while asking about {Tool}.", tool.Name);
            return AgentApprovalDecision.Deny("The approval prompt could not be shown, so nothing was changed.");
        }
        finally
        {
            run.GiveBack(run.Elapsed - asked);
        }
    }

    /// <summary>Asks a tool what a call would do, when it can say.</summary>
    private async Task<AgentToolPreview> DescribeAsync(RunState run, IAgentTool tool, AgentToolArguments arguments)
    {
        if (tool is not IAgentToolPreview describable)
        {
            return AgentToolPreview.None;
        }

        try
        {
            return await describable.DescribeAsync(arguments, run.UserToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A preview is a courtesy. Failing to compute one must never be the reason a call is not
            // put to the user, so the question is asked without it.
            _logger.LogWarning(ex, "Could not describe a {Tool} call.", tool.Name);
            return AgentToolPreview.None;
        }
    }

    /// <summary>
    /// Runs one tool. Null means the run was stopped while it was running.
    /// </summary>
    private async Task<AgentToolResult?> ExecuteAsync(RunState run, IAgentTool tool, AgentToolArguments arguments)
    {
        try
        {
            return await tool.ExecuteAsync(arguments, run.RunToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (run.RunToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            // A tool is contracted to report failure as a result rather than by throwing, so this is
            // a defect in the tool. It still may not end the run: the model is told the call failed
            // and gets to try something else, and the stack trace goes where stack traces belong.
            _logger.LogError(ex, "The tool {Tool} threw instead of reporting a failure.", tool.Name);

            return AgentToolResult.Fail(
                $"The tool '{tool.Name}' failed unexpectedly and nothing was changed: {ex.Message}",
                $"{tool.Name}: failed");
        }
    }

    /// <summary>
    /// Resolves the provider, builds the history and assembles one step's request. Returns a failure
    /// rather than throwing, so the caller can persist it against the step's own row.
    /// </summary>
    private async Task<Preparation> PrepareAsync(RunState run, bool offerTools)
    {
        try
        {
            var provider = _providers.GetProvider(run.ProviderId);

            if (provider is null)
            {
                return Preparation.Failed(new AIProviderException(
                    AIErrorKind.NotConfigured,
                    $"The provider '{run.ProviderId}' is not available. Configure it in Settings → Providers.",
                    null,
                    run.ProviderId));
            }

            if (!run.ModelResolved)
            {
                // Once per run. The catalogue does not change while a task is running, and a lookup
                // per step would be twenty-five of them for one answer.
                run.Model = await _providers
                    .GetModelAsync(run.ProviderId, run.ModelId, run.RunToken)
                    .ConfigureAwait(false);

                run.ModelResolved = true;
            }

            var model = run.Model;

            // Refused outright rather than discovered halfway through. A model that cannot call
            // tools answers the agent prompt with a description of what it would have done, which
            // looks exactly like an agent that did nothing and is far harder to understand.
            //
            // Only a catalogue that positively excluded tools counts as a no. A catalogue that
            // says nothing about capabilities - NVIDIA's says nothing about any of them - would
            // otherwise take agent mode away from the whole provider.
            if (model is { ToolsRuledOut: true })
            {
                return Preparation.Failed(new AIProviderException(
                    AIErrorKind.InvalidRequest,
                    $"The model '{model.Name}' cannot call tools, so it cannot carry out a task. "
                    + "Pick a model that supports tool calling, or send this as an ordinary message.",
                    null,
                    run.ProviderId));
            }

            var chat = _settings.Current.Chat;
            var conversation = await _conversations.GetAsync(run.ConversationId, run.RunToken).ConfigureAwait(false);

            var context = await _contextBuilder.BuildAsync(
                new ContextBuildRequest
                {
                    ConversationId = run.ConversationId,
                    SystemPrompt = AgentPrompt.Compose(conversation?.SystemPrompt ?? chat.SystemPrompt, _workspace.Root),
                    ContextWindow = model?.ContextWindow,
                    ReservedOutputTokens = chat.ReservedOutputTokens,
                },
                run.RunToken).ConfigureAwait(false);

            var messages = context.Messages;

            if (!offerTools)
            {
                // Said in words as well as by tool_choice. A model that is simply given no tools
                // tends to narrate the call it was about to make instead of summarising what it did.
                messages = [.. messages, AIChatMessage.System(AgentPrompt.LastStep)];
            }

            var request = new AIChatRequest
            {
                ModelId = run.ModelId,
                Messages = messages,
                Temperature = Allow(model, "temperature") ? chat.Temperature : null,
                TopP = Allow(model, "top_p") ? chat.TopP : null,
                MaxTokens = Allow(model, "max_tokens") ? ClampMaxTokens(chat.MaxTokens, model) : null,
                Stream = model?.SupportsStreaming ?? true,
                Tools = _registry.Definitions,

                // The definitions stay attached on the final step. Withdrawing them entirely makes
                // some providers forget the calls already in the history, which invalidates the very
                // transcript the model is being asked to summarise.
                ToolChoice = offerTools ? AIToolChoice.Auto : AIToolChoice.None,
            };

            return Preparation.Ready(provider, request, context.EstimatedTokens);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to prepare step {Step} of an agent run.", run.Step);
            return Preparation.Failed(ProviderErrorMapper.FromException(ex, run.ProviderId, run.ProviderId));
        }
    }

    private static bool Allow(ModelInfo? model, string parameter) => model?.Supports(parameter) ?? true;

    /// <summary>Keeps a global max-tokens setting from exceeding what a given model allows.</summary>
    private static int? ClampMaxTokens(int? configured, ModelInfo? model)
    {
        if (configured is not { } value || value <= 0)
        {
            return null;
        }

        return model?.MaxOutputTokens is { } limit && value > limit ? limit : value;
    }

    /// <summary>What the model is told when the user says no.</summary>
    /// <remarks>
    /// The last sentence is the important one. A model told only "denied" reliably tries the same
    /// change through a different tool, which is precisely what the gate exists to prevent.
    /// </remarks>
    private static AgentToolResult Denial(IAgentTool tool, AgentApprovalDecision decision)
    {
        var reason = string.IsNullOrWhiteSpace(decision.Reason)
            ? "The user declined this call."
            : $"The user declined this call: {decision.Reason.Trim()}";

        return AgentToolResult.Fail(
            $"{reason} Nothing was changed. Do not look for another way to make the same change - say "
            + "what you were going to do and ask, or carry on with the rest of the task.",
            $"{tool.Name}: declined");
    }

    /// <summary>What the model is told when it makes the same call once too often.</summary>
    private static AgentToolResult RepeatRefusal(IAgentTool tool, int attempts)
    {
        var already = attempts - 1;

        return AgentToolResult.Fail(
            $"This is the same '{tool.Name}' call, with the same arguments, that you have already made "
            + $"{already} time{(already == 1 ? string.Empty : "s")} in this task, so it was not run again. "
            + "The answer will not change until something else does: read the result you already have, "
            + "then do something different or say what is blocking you.",
            $"{tool.Name}: repeated call refused");
    }

    private string ToolNames() => string.Join(", ", _registry.Tools.Select(tool => tool.Name));

    private static AgentEvent Finish(RunState run, AgentStopReason reason) =>
        new AgentEvent.Completed(run.LastMessageId, run.Step, reason, (int)run.Elapsed.TotalMilliseconds);

    private static AgentEvent Failure(Guid messageId, AIProviderException failure) =>
        new AgentEvent.Failed(
            messageId, failure.Kind, failure.UserMessage, failure.TechnicalDetails, failure.IsRetryable);

    private async Task PersistCancellationAsync(Guid messageId, string content, Stopwatch clock)
    {
        // The step keeps whatever text it managed to produce, marked for what it is. Cancelled rather
        // than Complete matters to the next turn: a half-sentence must not be replayed as an answer.
        await _conversations.UpdateMessageAsync(
            new MessageUpdate
            {
                MessageId = messageId,
                Content = content,
                Status = MessageStatus.Cancelled,
                GenerationTimeMs = (int)clock.ElapsedMilliseconds,
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task PersistFailureAsync(Guid messageId, string partialContent, AIProviderException failure)
    {
        _logger.LogWarning(
            "Agent step failed: {Kind} from provider {ProviderId}.",
            failure.Kind, failure.ProviderId ?? "unknown");

        await _conversations.UpdateMessageAsync(
            new MessageUpdate
            {
                MessageId = messageId,
                Content = partialContent,
                Status = MessageStatus.Failed,
                ErrorKind = failure.Kind,
                ErrorMessage = failure.UserMessage,
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<string?> TryTitleAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        if (!_settings.Current.General.AutoGenerateTitles)
        {
            return null;
        }

        try
        {
            return await _conversations
                .TryApplyAutoTitleAsync(request.ConversationId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A title is cosmetic; never let it break the run.
            _logger.LogWarning(ex, "Auto-titling failed for conversation {ConversationId}.", request.ConversationId);
            return null;
        }
    }

    /// <summary>Outcome of one step's setup: either a ready request or a failure to report.</summary>
    private sealed record Preparation(
        IAIProvider? Provider,
        AIChatRequest? Request,
        int EstimatedInputTokens,
        AIProviderException? Failure)
    {
        public static Preparation Ready(IAIProvider provider, AIChatRequest request, int estimatedTokens)
            => new(provider, request, estimatedTokens, null);

        public static Preparation Failed(AIProviderException failure)
            => new(null, null, 0, failure);
    }

    /// <summary>
    /// One run's mutable state: where it has got to, what it has spent, and what has already been
    /// allowed.
    /// </summary>
    /// <remarks>
    /// A class rather than a handful of locals because the loop is split across iterator methods,
    /// and an iterator cannot take state by reference. Everything here is touched by the one thread
    /// draining the sequence, so none of it is synchronised.
    /// </remarks>
    private sealed class RunState : IDisposable
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly CancellationTokenSource? _deadline;
        private readonly TimeSpan _budget;
        private readonly int _maxIdentical;
        private readonly HashSet<string> _allowedForRun = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _approved = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);
        private TimeSpan _waited;

        public RunState(AgentRunRequest request, AgentSettings settings, CancellationToken userToken)
        {
            ConversationId = request.ConversationId;
            ProviderId = request.ProviderId;
            ModelId = request.ModelId;
            UserToken = userToken;
            RunToken = userToken;

            MaxSteps = Math.Max(1, settings.MaxSteps);

            // Below two, the rule could only refuse a call the model has not made yet, which would
            // break every tool. Read as "no limit" rather than honoured literally.
            _maxIdentical = settings.MaxIdenticalCalls >= 2 ? settings.MaxIdenticalCalls : int.MaxValue;

            if (settings.MaxDurationSeconds > 0)
            {
                _budget = TimeSpan.FromSeconds(settings.MaxDurationSeconds);
                _deadline = CancellationTokenSource.CreateLinkedTokenSource(userToken);
                _deadline.CancelAfter(_budget);
                RunToken = _deadline.Token;
            }
        }

        public Guid ConversationId { get; }

        public string ProviderId { get; }

        public string ModelId { get; }

        public int MaxSteps { get; }

        /// <summary>The Stop button. Ends the run and closes an open question.</summary>
        public CancellationToken UserToken { get; }

        /// <summary>Stop, plus the deadline. What the provider and the tools are given.</summary>
        public CancellationToken RunToken { get; }

        public int Step { get; set; }

        /// <summary>The row the last step wrote into, and what the terminal event points at.</summary>
        public Guid LastMessageId { get; set; }

        /// <summary>Set when a step ended because the run stopped, rather than because it finished.</summary>
        public bool Halted { get; set; }

        public ModelInfo? Model { get; set; }

        public bool ModelResolved { get; set; }

        public TimeSpan Elapsed => _clock.Elapsed;

        /// <summary>
        /// Whether the run has spent its time. Measured on the work, which is the elapsed time less
        /// whatever the user spent deciding.
        /// </summary>
        public bool OutOfTime => _budget > TimeSpan.Zero && _clock.Elapsed - _waited >= _budget;

        /// <summary>
        /// Stops the deadline while a question is open, and is undone by <see cref="GiveBack"/>.
        /// </summary>
        /// <remarks>
        /// The two have to be paired in one try/finally. A hold that is never given back would leave
        /// the run with no time limit at all, which is the failure this whole mechanism exists to
        /// prevent.
        /// </remarks>
        public void HoldDeadline() => _deadline?.CancelAfter(Timeout.InfiniteTimeSpan);

        /// <summary>
        /// Gives back the time an approval question was open, so that a person taking a minute to
        /// read a diff never costs the run its remaining budget.
        /// </summary>
        public void GiveBack(TimeSpan waited)
        {
            if (waited > TimeSpan.Zero)
            {
                _waited += waited;
            }

            if (_budget <= TimeSpan.Zero)
            {
                return;
            }

            // Re-armed unconditionally, even for a question answered in no measurable time: the
            // deadline is suspended before every question, and only this restarts it.
            var left = _budget - (_clock.Elapsed - _waited);
            _deadline?.CancelAfter(left > TimeSpan.Zero ? left : TimeSpan.Zero);
        }

        /// <summary>
        /// Counts this attempt and says whether it is one repeat too many.
        /// </summary>
        /// <remarks>
        /// Counts refused and denied attempts too. A model that keeps proposing something the user
        /// keeps declining is in the same loop as one re-reading a file, and gets the same answer.
        /// </remarks>
        public bool TooManyAttempts(AIToolCall call, out int attempts)
        {
            var key = Key(call);
            attempts = _attempts.TryGetValue(key, out var seen) ? seen + 1 : 1;
            _attempts[key] = attempts;

            return attempts >= _maxIdentical;
        }

        /// <summary>Forgets the repeat counts, because the workspace is no longer what it was.</summary>
        public void ForgetAttempts() => _attempts.Clear();

        /// <summary>
        /// Whether a standing yes covers this tool. Never true for one that runs programs, however
        /// the gate answered.
        /// </summary>
        public bool IsAllowedForRun(IAgentTool tool) =>
            tool.Risk != AgentToolRisk.Execute && _allowedForRun.Contains(tool.Name);

        public bool HasApproved(AIToolCall call) => _approved.Contains(Key(call));

        public void Remember(AIToolCall call, IAgentTool tool, AgentApprovalDecision decision)
        {
            _approved.Add(Key(call));

            if (decision.Outcome == AgentApprovalOutcome.AllowedForRun && tool.Risk != AgentToolRisk.Execute)
            {
                _allowedForRun.Add(tool.Name);
            }
        }

        public void Dispose() => _deadline?.Dispose();

        /// <summary>
        /// What counts as the same call: the tool and the arguments exactly as they were sent.
        /// </summary>
        /// <remarks>
        /// Textual rather than semantic, so two calls differing only in whitespace read as different
        /// calls. That errs towards letting a call through, which is the right direction for a rule
        /// whose only power is to refuse.
        /// </remarks>
        private static string Key(AIToolCall call) =>
            string.Concat(call.Name, "\0", (call.ArgumentsJson ?? string.Empty).Trim());
    }
}
