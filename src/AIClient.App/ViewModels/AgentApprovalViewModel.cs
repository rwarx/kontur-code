using AIClient.Application.DTOs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIClient.App.ViewModels;

/// <summary>
/// One question put to the user: the agent wants to do something it cannot take back.
/// </summary>
/// <remarks>
/// <para>
/// A view model rather than a dialog class, because the question is not modal. The transcript
/// behind it is what the answer depends on - which file the model said it was editing, what it
/// claimed to have read - and a window centred over that text would hide the evidence.
/// </para>
/// <para>
/// It owns the answer as a task. The agent loop is blocked inside
/// <see cref="Application.Interfaces.IAgentApproval.RequestAsync"/> while this object exists, so
/// exactly one of the three commands must complete <see cref="Answer"/>, or the run waits for a
/// button nobody is going to press. <see cref="Abandon"/> is the fourth way out, for a Stop that
/// arrives while the question is still on screen.
/// </para>
/// </remarks>
public sealed partial class AgentApprovalViewModel : ObservableObject
{
    /// <remarks>
    /// Continuations run asynchronously on purpose. The Deny path completes this from a click
    /// handler on the UI thread, and the agent loop resumes by writing a message and asking the
    /// provider for another step - work that must not run inline on the dispatcher.
    /// </remarks>
    private readonly TaskCompletionSource<AgentApprovalDecision> _answer =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// What the model is told when the answer is no. Optional, and worth typing.
    /// </summary>
    /// <remarks>
    /// "Not that file, the one in src" turns a denial into a correction the model can act on in
    /// one step. Left empty it still denies; the loop supplies a plain sentence.
    /// </remarks>
    [ObservableProperty]
    private string _denyReason = string.Empty;

    /// <summary>Whether the raw arguments are showing. Collapsed by default.</summary>
    [ObservableProperty]
    private bool _isArgumentsExpanded;

    public AgentApprovalViewModel(AgentApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        Request = request;
        PreviewLines = DiffLines.Split(request.Preview);
    }

    /// <summary>The request as the loop built it, for anything the properties below do not cover.</summary>
    public AgentApprovalRequest Request { get; }

    public string ToolName => Request.ToolName;

    /// <summary>
    /// The one line the decision is usually made on.
    /// </summary>
    /// <remarks>
    /// Falls back to the tool's name when no tool could describe the call. That is a worse
    /// question, but a question with the raw arguments underneath it is still answerable, and
    /// hiding the gate because the summary was missing would not be.
    /// </remarks>
    public string Headline => Request.Summary is { Length: > 0 } summary
        ? summary
        : $"Run {Request.ToolName} with the arguments below";

    /// <summary>What is at stake, in the plainest words available.</summary>
    public string Consequence => Request.Risk switch
    {
        AgentToolRisk.Execute => "Runs a program on this computer.",
        AgentToolRisk.Write => "Changes files in the folder you opened.",
        _ => "Reads from the folder you opened.",
    };

    public string ArgumentsJson => Request.ArgumentsJson;

    public bool HasArguments => Request.ArgumentsJson.Length > 0;

    /// <summary>
    /// Set when this exact call has already been approved once in the same run.
    /// </summary>
    /// <remarks>
    /// Shown so the user is not left wondering whether they misread the last question. It happens
    /// for the risk levels whose answers are deliberately never remembered.
    /// </remarks>
    public bool IsRepeat => Request.IsRepeat;

    /// <summary>The diff, split so the view can colour each line by what it is.</summary>
    /// <remarks>
    /// A preview is always <see cref="Application.Services.TextDiff"/> output, so it is coloured
    /// unconditionally - unlike a tool result, which is often ordinary text and has to be tested first.
    /// </remarks>
    public IReadOnlyList<DiffLine> PreviewLines { get; }

    public bool HasPreview => PreviewLines.Count > 0;

    /// <summary>
    /// Whether the "don't ask again" button is offered.
    /// </summary>
    /// <remarks>
    /// Withheld for anything that runs a program, because the loop refuses to remember that answer
    /// however it is given. A button that silently means the same as Allow would teach the user
    /// something untrue about what they had consented to.
    /// </remarks>
    public bool CanAllowForRun => Request.Risk != AgentToolRisk.Execute;

    /// <summary>Completes when one of the commands below runs, or when the run is stopped.</summary>
    public Task<AgentApprovalDecision> Answer => _answer.Task;

    /// <summary>
    /// Closes the question without answering it, because the run is over.
    /// </summary>
    /// <remarks>
    /// Cancellation rather than a denial: nothing is reported to the model, because there is no
    /// next step to report it to. <see cref="IAgentApproval"/> documents this as the difference
    /// between Stop and No.
    /// </remarks>
    public void Abandon(CancellationToken cancellationToken) => _answer.TrySetCanceled(cancellationToken);

    [RelayCommand]
    private void Allow() => _answer.TrySetResult(AgentApprovalDecision.Allow());

    [RelayCommand]
    private void AllowForRun() => _answer.TrySetResult(AgentApprovalDecision.AllowForRun());

    [RelayCommand]
    private void Deny()
    {
        var reason = DenyReason.Trim();

        _answer.TrySetResult(AgentApprovalDecision.Deny(reason.Length > 0 ? reason : null));
    }
}
