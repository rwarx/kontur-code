using AIClient.Application.DTOs;

namespace AIClient.Application.Interfaces;

/// <summary>
/// Asks the user whether the agent may do something it cannot take back.
/// </summary>
/// <remarks>
/// <para>
/// The whole of section 28's safety position rests on this one call. Every effect on the machine
/// above <see cref="AgentToolRisk.Read"/> passes through it, so a host that implements it badly - or
/// not at all - is the difference between an assistant and a program that rewrites a folder while
/// nobody is looking. That is why the default implementation refuses rather than allows.
/// </para>
/// <para>
/// An interface rather than an event, because the loop has to wait for the answer and the answer has
/// to be able to take as long as a person takes. Implementations are free to block on a dialog, and
/// are expected to honour <paramref name="cancellationToken"/> so that pressing Stop closes the
/// question instead of leaving the run wedged behind it.
/// </para>
/// </remarks>
public interface IAgentApproval
{
    /// <summary>
    /// Puts the question, and returns when it has been answered.
    /// </summary>
    /// <remarks>
    /// Called on whatever thread the run happens on, which is never the UI thread. A host with a
    /// dispatcher has to marshal, and this is the one place in the agent where that is expected.
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// The run was cancelled while the question was open. Not the same as a denial: nothing is
    /// reported to the model, because the turn is over.
    /// </exception>
    Task<AgentApprovalDecision> RequestAsync(
        AgentApprovalRequest request,
        CancellationToken cancellationToken = default);
}
