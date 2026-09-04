using AIClient.App.ViewModels;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace AIClient.App.Services;

/// <summary>
/// The approval gate, as a piece of the window rather than a dialog.
/// </summary>
/// <remarks>
/// <para>
/// Registered over <c>DenyingAgentApproval</c>, which is what the Infrastructure layer installs so that
/// a host which forgets to ask refuses instead of allowing. Section 28's safety position is this class:
/// every effect on the machine above <see cref="AgentToolRisk.Read"/> waits here for a person.
/// </para>
/// <para>
/// It holds the question, not the answer. <see cref="Pending"/> is bound by the chat pane, and the
/// buttons in that card complete the task the agent loop is blocked on. Two rules follow from the loop
/// being blocked. One question at a time, enforced by a semaphore rather than by refusing the second -
/// a denial invented because the UI was busy would be a lie told to the model in the user's name. And
/// every exit path clears <see cref="Pending"/>, because a card left on screen after its run has ended
/// has buttons that answer nothing.
/// </para>
/// </remarks>
public sealed partial class AgentApprovalService : ObservableObject, IAgentApproval
{
    /// <remarks>
    /// A second run's question waits for the first to be answered instead of being turned away. Runs do
    /// not normally overlap - the chat pane starts one turn at a time - but nothing in the contract
    /// promises that, and the failure if it happened would be the worst kind: a question stranded behind
    /// another one, with a run hung on a card that is not showing.
    /// </remarks>
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    private readonly ILogger<AgentApprovalService> _logger;

    /// <summary>The question on screen, or null when the agent is not waiting for anything.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAsking))]
    private AgentApprovalViewModel? _pending;

    public AgentApprovalService(ILogger<AgentApprovalService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
    }

    /// <summary>Whether a question is waiting for an answer, for the view to show the card.</summary>
    public bool IsAsking => Pending is not null;

    /// <inheritdoc />
    /// <remarks>
    /// Arrives on the run's thread, never the dispatcher, so both writes to <see cref="Pending"/> are
    /// posted. The awaited task is completed from a click handler, and the token is the user's Stop:
    /// when it fires the question is abandoned so the loop unwinds through cancellation rather than
    /// being told the user said no.
    /// </remarks>
    public async Task<AgentApprovalDecision> RequestAsync(
        AgentApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Nothing about the call itself is logged: the arguments are the user's files and the model's
        // text, which section 26 keeps out of the log. The tool and the risk are enough to reconstruct
        // what was asked, alongside the transcript the user already has.
        _logger.LogInformation(
            "Asking the user about {Tool} ({Risk}).",
            request.ToolName,
            request.Risk);

        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);

        var question = new AgentApprovalViewModel(request);

        try
        {
            UiThread.Post(() => Pending = question);

            // Awaited disposal on purpose. The synchronous Dispose blocks until a callback that is
            // already running finishes, and this one runs on whatever thread called Stop.
            await using var registration = cancellationToken
                .Register(() => question.Abandon(cancellationToken))
                .ConfigureAwait(false);

            var decision = await question.Answer.ConfigureAwait(false);

            // The reason is not logged. It is free text the user typed for the model, and it routinely
            // names the file they did not want touched.
            _logger.LogInformation(
                "The user answered {Outcome} for {Tool}.",
                decision.Outcome,
                request.ToolName);

            return decision;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "The question about {Tool} was closed unanswered because the run was stopped.",
                request.ToolName);

            throw;
        }
        finally
        {
            // Only if it is still ours. A later question cannot be showing yet - the semaphore is still
            // held - but the identity check costs nothing and makes that reasoning local.
            UiThread.Post(() =>
            {
                if (ReferenceEquals(Pending, question))
                {
                    Pending = null;
                }
            });

            _oneAtATime.Release();
        }
    }
}
