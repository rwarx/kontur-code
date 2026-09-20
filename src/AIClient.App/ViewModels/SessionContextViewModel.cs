using AIClient.App.Services;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIClient.App.ViewModels;

/// <summary>
/// The context panel: what the model is holding for the open chat, and the button that folds it.
/// </summary>
/// <remarks>
/// <para>
/// Assembled on open rather than kept in step with the transcript. The report is a dozen derived
/// numbers over every message in the conversation, and recomputing it on each arriving token would
/// put a full table scan inside the streaming loop to update a panel that is usually closed.
/// </para>
/// <para>
/// The report itself is exposed as one immutable record rather than unpacked into twenty observable
/// properties. Replacing it raises a single notification and every bound field re-reads, so there is
/// no second copy of the numbers here to drift from the ones the service computed.
/// </para>
/// </remarks>
public sealed partial class SessionContextViewModel : ObservableObject
{
    private readonly ISessionContextService _context;
    private readonly ICompactionService _compaction;
    private readonly ILogger<SessionContextViewModel> _logger;

    /// <summary>
    /// What the last manual fold achieved, kept so the notice can be re-worded in a new language.
    /// </summary>
    private CompactionResult? _lastCompaction;

    private Guid? _conversationId;
    private string? _providerId;
    private string? _modelId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    [NotifyCanExecuteChangedFor(nameof(CompactCommand))]
    private SessionContextReport? _report;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Set while the summariser is running, which is a provider call and not instant.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompactCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isCompacting;

    /// <summary>Outcome of the last fold, shown under the button until the panel is reopened.</summary>
    [ObservableProperty]
    private string? _notice;

    /// <summary>
    /// The service's own words for why a fold did nothing, for the notice's tooltip.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="Notice"/> because it is not translated: the reasons are written in
    /// the Application layer, which has no string table, and several of them quote a provider id.
    /// Localising the line the user reads and hovering the exact cause behind it beats printing an
    /// English sentence into a Russian panel.
    /// </remarks>
    [ObservableProperty]
    private string? _noticeDetail;

    public SessionContextViewModel(
        ISessionContextService context,
        ICompactionService compaction,
        ILogger<SessionContextViewModel> logger)
    {
        _context = context;
        _compaction = compaction;
        _logger = logger;
    }

    /// <summary>True once a report has been built, which is what the panel draws instead of a spinner.</summary>
    public bool HasReport => Report is not null;

    /// <summary>
    /// Raised after a fold so the transcript can be reloaded.
    /// </summary>
    /// <remarks>
    /// Compaction rewrites which rows the model can see, and the summary is a new message. A panel
    /// that folded twelve turns while the transcript still showed them unchanged would leave the
    /// user with no way to tell whether the button had done anything.
    /// </remarks>
    public event EventHandler<Guid>? Compacted;

    /// <summary>
    /// Points the panel at a conversation and the model that will answer next.
    /// </summary>
    /// <remarks>
    /// The provider and model are the ones selected in the picker, not the pair the conversation
    /// last recorded: the question the panel answers is how much room there is for the next message.
    /// </remarks>
    public async Task LoadAsync(
        Guid? conversationId,
        string? providerId,
        string? modelId,
        CancellationToken cancellationToken = default)
    {
        _conversationId = conversationId;
        _providerId = providerId;
        _modelId = modelId;

        // A fold from a previous chat says nothing about this one.
        _lastCompaction = null;
        Notice = null;
        NoticeDetail = null;

        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Rebuilds the report for the conversation the panel is already pointed at.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_conversationId is not { } id)
        {
            Report = null;
            return;
        }

        IsLoading = true;

        try
        {
            Report = await _context
                .GetReportAsync(id, _providerId, _modelId, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The panel was closed, or another chat was opened, while this was in flight.
        }
        catch (Exception ex)
        {
            // A panel is not worth failing an application over: an empty one is a small loss
            // next to a crash while the user was only curious about their token count.
            _logger.LogError(ex, "The context report for conversation {ConversationId} could not be built.", id);
            Report = null;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Re-words the strings this pane computes in C# after the language changed.</summary>
    public void OnLanguageChanged() => Notice = Describe(_lastCompaction);

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task Refresh() => RefreshAsync();

    private bool CanRefresh() => !IsCompacting;

    /// <summary>
    /// Folds the older history of the open chat, on the user's say-so.
    /// </summary>
    /// <remarks>
    /// Forced, because pressing the button is a decision rather than a heuristic: refusing it
    /// because the window is only half full would leave the button doing nothing with no
    /// explanation. The threshold still governs the automatic pass.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanCompact))]
    private async Task CompactAsync()
    {
        if (_conversationId is not { } id || Report is null)
        {
            return;
        }

        // The report resolved these against the conversation's own pair, so it is the one place
        // that knows which provider a forced fold should be asked of.
        if (_providerId is not { Length: > 0 } providerId || _modelId is not { Length: > 0 } modelId)
        {
            Notice = Localization.T("S.Context.Compact.NoModel");
            return;
        }

        IsCompacting = true;
        Notice = null;
        NoticeDetail = null;

        try
        {
            var result = await _compaction
                .CompactAsync(
                    new CompactionRequest
                    {
                        ConversationId = id,
                        ProviderId = providerId,
                        ModelId = modelId,
                        Force = true,
                    })
                .ConfigureAwait(true);

            _lastCompaction = result;
            Notice = Describe(result);
            NoticeDetail = result.SkippedReason;

            if (result.MessagesFolded > 0)
            {
                Compacted?.Invoke(this, id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Conversation {ConversationId} could not be compacted.", id);

            _lastCompaction = null;
            Notice = Localization.T("S.Context.Compact.Failed");
            NoticeDetail = ex.Message;
        }
        finally
        {
            IsCompacting = false;

            // Whatever happened, the numbers on screen describe the prompt as it was before it.
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Enabled whenever there is a chat to fold, not only when it is nearly full.
    /// </summary>
    /// <remarks>
    /// A chat with room left is refused by the service with a reason the notice then shows, which
    /// is more useful than a disabled button that cannot say why it is disabled.
    /// </remarks>
    private bool CanCompact() => Report is not null && !IsCompacting;

    /// <summary>Turns a fold's outcome into the one line shown under the button.</summary>
    private static string? Describe(CompactionResult? result) => result switch
    {
        null => null,
        { MessagesFolded: > 0 } folded => Localization.T(
            "S.Context.Compact.Done",
            folded.MessagesFolded,
            folded.TokensSaved),
        _ => Localization.T("S.Context.Compact.Nothing"),
    };
}
