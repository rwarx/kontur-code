using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Domain.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIClient.App.ViewModels;

/// <summary>
/// One tool call, shown as a card under the step that asked for it.
/// </summary>
/// <remarks>
/// <para>
/// A card exists from the moment a call is mentioned until the run ends, and every event about that
/// call updates this one object. That is what makes a call the user refused still visible: the
/// alternative - a card built when the call finishes - would silently drop the interesting half of
/// what the agent tried to do.
/// </para>
/// <para>
/// The body is set once, when the call ends, and can be either a diff or ordinary text. Which one it
/// is decides how it is painted, so it is decided here rather than guessed at by the template.
/// </para>
/// </remarks>
public sealed partial class AgentToolCallViewModel : ObservableObject
{
    /// <summary>
    /// Lines of a result a card will show.
    /// </summary>
    /// <remarks>
    /// A diff arrives already capped, but a file the agent read does not, and a transcript that has to
    /// lay out ten thousand lines to scroll is a transcript that stutters for the rest of the session.
    /// </remarks>
    private const int MaxBodyLines = 200;

    private const string BodyTruncationNotice = "... the rest of this result is not shown.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(HasSucceeded))]
    [NotifyPropertyChangedFor(nameof(HasFailed))]
    [NotifyPropertyChangedFor(nameof(WasDenied))]
    [NotifyPropertyChangedFor(nameof(WasAbandoned))]
    [NotifyPropertyChangedFor(nameof(IsFinished))]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private AgentToolCallState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Headline))]
    private string? _summary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBody))]
    [NotifyPropertyChangedFor(nameof(IsBodyDiff))]
    [NotifyPropertyChangedFor(nameof(IsBodyText))]
    private string? _body;

    [ObservableProperty]
    private IReadOnlyList<DiffLine> _bodyLines = [];

    /// <summary>Whether the result is showing. Collapsed by default, in every state.</summary>
    /// <remarks>
    /// Expanding by default would push the answer the user is reading off the screen every time the
    /// agent opens a file, which is several times a step.
    /// </remarks>
    [ObservableProperty]
    private bool _isExpanded;

    private AgentToolCallViewModel(string callId, string toolName, AgentToolRisk? risk)
    {
        CallId = callId;
        ToolName = toolName;
        Risk = risk;
    }

    /// <summary>The provider's id for this call, which is how every later event finds this card.</summary>
    public string CallId { get; }

    public string ToolName { get; }

    /// <summary>
    /// What the call was allowed to do, when that is known.
    /// </summary>
    /// <remarks>
    /// Null for a card rebuilt from the database: a stored tool row records what happened, not what
    /// the tool was permitted to do, and the registry it would have to be looked up in belongs to a
    /// run that ended.
    /// </remarks>
    public AgentToolRisk? Risk { get; }

    public bool HasRisk => Risk is not null;

    /// <summary>The line the card is read by.</summary>
    /// <remarks>
    /// Falls back to the tool's name, which is also what a card restored from the transcript shows:
    /// the one-line summary is built by the tool as it runs and is not worth a column in the schema.
    /// </remarks>
    public string Headline => Summary is { Length: > 0 } summary ? summary : ToolName;

    public bool IsWaiting => State == AgentToolCallState.Proposed;
    public bool IsRunning => State == AgentToolCallState.Running;
    public bool HasSucceeded => State == AgentToolCallState.Succeeded;
    public bool HasFailed => State == AgentToolCallState.Failed;
    public bool WasDenied => State == AgentToolCallState.Denied;
    public bool WasAbandoned => State == AgentToolCallState.Abandoned;

    public bool IsFinished => State is not (AgentToolCallState.Proposed or AgentToolCallState.Running);

    /// <summary>The state in the words a user would use for it.</summary>
    public string StateText => State switch
    {
        AgentToolCallState.Proposed => "Waiting",
        AgentToolCallState.Running => "Running",
        AgentToolCallState.Succeeded => "Done",
        AgentToolCallState.Denied => "Not allowed",
        AgentToolCallState.Abandoned => "Interrupted",
        _ => "Failed",
    };

    public bool HasBody => Body is { Length: > 0 };

    public bool IsBodyDiff => HasBody && BodyLines.Count > 0;

    public bool IsBodyText => HasBody && BodyLines.Count == 0;

    /// <summary>A card for a call the loop has just told us about.</summary>
    public static AgentToolCallViewModel Live(AIToolCall call, AgentToolRisk? risk)
    {
        ArgumentNullException.ThrowIfNull(call);

        return new AgentToolCallViewModel(call.Id, call.Name, risk);
    }

    /// <summary>
    /// A card for a call from an earlier session, built from its stored row.
    /// </summary>
    /// <remarks>
    /// Everything comes from first-class columns on the row. The provider-shaped JSON on the
    /// assistant message would say more - the arguments, for one - but parsing it here would put
    /// knowledge of a provider's wire format in the view, which is the one thing this layer must not
    /// know. A denial is stored as an unsuccessful call, so it comes back as a failure; the text the
    /// model was given says which it was, and that text is the body of the card.
    /// </remarks>
    public static AgentToolCallViewModel Restored(MessageDto row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var card = new AgentToolCallViewModel(
            row.ToolCallId ?? row.Id.ToString(),
            row.ToolName is { Length: > 0 } name ? name : "tool",
            risk: null)
        {
            State = row.ToolSucceeded == false ? AgentToolCallState.Failed : AgentToolCallState.Succeeded,
        };

        card.SetBody(row.Content);

        return card;
    }

    /// <summary>The call was allowed and has begun.</summary>
    public void Start() => State = AgentToolCallState.Running;

    /// <summary>The call is over, whatever the outcome.</summary>
    /// <param name="content">What the model was told, used when there is nothing richer to show.</param>
    /// <param name="detail">The fuller version for a person - a diff, usually.</param>
    public void Finish(AgentCallOutcome outcome, string content, string? summary, string? detail)
    {
        if (summary is { Length: > 0 })
        {
            Summary = summary;
        }

        SetBody(detail is { Length: > 0 } ? detail : content);

        State = outcome switch
        {
            AgentCallOutcome.Succeeded => AgentToolCallState.Succeeded,
            AgentCallOutcome.Denied => AgentToolCallState.Denied,
            _ => AgentToolCallState.Failed,
        };
    }

    /// <summary>
    /// The run ended before this call did.
    /// </summary>
    /// <remarks>
    /// Not the same as a failure. Stop while a tool is running says nothing about whether the tool got
    /// as far as writing anything, and a card that kept spinning - or claimed to have failed - would
    /// answer a question nobody can answer from here.
    /// </remarks>
    public void Abandon()
    {
        if (!IsFinished)
        {
            State = AgentToolCallState.Abandoned;
        }
    }

    /// <summary>
    /// Stores the result, capped, and decides whether it is a diff.
    /// </summary>
    /// <remarks>
    /// The diff test happens once here because getting it wrong is not a cosmetic mistake. Ordinary
    /// text - a listing, a refusal, a file with a markdown bullet in it - has lines starting with a
    /// hyphen, and tinting one red says a line of the user's file is being deleted when nothing of
    /// the kind is happening.
    /// </remarks>
    private void SetBody(string? text)
    {
        var trimmed = Cap(text);

        Body = trimmed;
        BodyLines = DiffLines.LooksLikeDiff(trimmed) ? DiffLines.Split(trimmed) : [];
    }

    private static string? Cap(string? text)
    {
        if (text is not { Length: > 0 })
        {
            return null;
        }

        var normalised = text.ReplaceLineEndings("\n");
        var lines = normalised.Split('\n');

        return lines.Length <= MaxBodyLines
            ? normalised
            : string.Join('\n', lines.Take(MaxBodyLines).Append(BodyTruncationNotice));
    }
}

/// <summary>Where a tool call has got to.</summary>
public enum AgentToolCallState
{
    /// <summary>Named by the model; the loop has not decided about it yet.</summary>
    Proposed,

    /// <summary>Allowed, and running now.</summary>
    Running,

    /// <summary>Did what it said it would.</summary>
    Succeeded,

    /// <summary>Tried and did not work.</summary>
    Failed,

    /// <summary>The user said no. Nothing was changed.</summary>
    Denied,

    /// <summary>The run ended while this call was still open, so what it did is not known.</summary>
    Abandoned,
}
