using AIClient.Application.DTOs;
using AIClient.Application.Services;
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
        PreviewLines = SplitPreview(request.Preview);
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
    public IReadOnlyList<ApprovalPreviewLine> PreviewLines { get; }

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

    /// <summary>
    /// Splits a preview into tagged lines.
    /// </summary>
    /// <remarks>
    /// Done once, here, rather than by a converter per line: the list is bound directly and a
    /// preview never changes after the question is asked.
    /// </remarks>
    private static IReadOnlyList<ApprovalPreviewLine> SplitPreview(string? preview)
    {
        if (preview is not { Length: > 0 })
        {
            return [];
        }

        var lines = preview.ReplaceLineEndings("\n").Split('\n');
        var tagged = new List<ApprovalPreviewLine>(lines.Length);

        foreach (var line in lines)
        {
            tagged.Add(new ApprovalPreviewLine(line, Classify(line)));
        }

        return tagged;
    }

    /// <summary>
    /// What a line of a unified diff is.
    /// </summary>
    /// <remarks>
    /// The file headers start with the same characters as an added and a removed line, so they are
    /// tested first. Getting that order wrong paints <c>+++ b/Program.cs</c> green as though the
    /// path itself were being inserted.
    /// </remarks>
    private static ApprovalLineKind Classify(string line)
    {
        if (line == TextDiff.TruncationNotice)
        {
            return ApprovalLineKind.Notice;
        }

        if (line.StartsWith("+++", StringComparison.Ordinal)
            || line.StartsWith("---", StringComparison.Ordinal)
            || line.StartsWith("@@", StringComparison.Ordinal))
        {
            return ApprovalLineKind.Header;
        }

        return line.Length == 0 ? ApprovalLineKind.Context : line[0] switch
        {
            '+' => ApprovalLineKind.Added,
            '-' => ApprovalLineKind.Removed,
            _ => ApprovalLineKind.Context,
        };
    }
}

/// <summary>One line of a preview, and what it is, so the view can colour it.</summary>
public sealed record ApprovalPreviewLine(string Text, ApprovalLineKind Kind);

/// <summary>The kinds of line a preview contains.</summary>
public enum ApprovalLineKind
{
    /// <summary>Unchanged, shown for context.</summary>
    Context,

    /// <summary>A line the change would add.</summary>
    Added,

    /// <summary>A line the change would remove.</summary>
    Removed,

    /// <summary>A file or hunk header.</summary>
    Header,

    /// <summary>The note saying the diff was cut short.</summary>
    Notice,
}
