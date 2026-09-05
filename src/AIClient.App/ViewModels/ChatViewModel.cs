using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using AIClient.App.Services;
using AIClient.Application.Configuration;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Application.Markdown;
using AIClient.Domain.Enums;
using AIClient.Domain.Graph;
using AIClient.Domain.Models;
using AIClient.Domain.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIClient.App.ViewModels;

/// <summary>
/// The conversation pane: composing, sending, streaming, and the per-message actions.
/// </summary>
/// <remarks>
/// This class talks to <see cref="IChatService"/> and nothing else. It has no idea that
/// OpenRouter or NVIDIA exist, never sees an HTTP client, and never touches the database -
/// which is what section 7 asks for and what makes adding a provider a change in one
/// assembly instead of three.
///
/// Two details that are easy to get wrong and matter a great deal here:
/// <list type="bullet">
/// <item><description>
/// Markdown is re-rendered on a 60 ms timer, not on every token. Re-parsing per token turns
/// a fast model into a slideshow.
/// </description></item>
/// <item><description>
/// The streaming loop runs on the caller's context so <see cref="MessageViewModel"/> updates
/// land on the dispatcher. The work that blocks - HTTP and SQLite - is already async inside
/// the service, so the UI thread is never the thing waiting.
/// </description></item>
/// </list>
/// </remarks>
public sealed partial class ChatViewModel : ObservableObject
{
    /// <summary>
    /// Markdown re-render cadence. Fast enough to look continuous, slow enough that a
    /// 100-tokens-per-second stream costs about 16 parses a second rather than 100.
    /// </summary>
    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(60);

    /// <summary>
    /// Characters a question asked from the canvas may bring with it, across all of its files.
    /// </summary>
    /// <remarks>
    /// About sixteen thousand tokens, and a ceiling rather than a target: one file rarely comes near
    /// it. The number exists because these attachments are not dragged in one at a time by someone
    /// watching the composer fill up - four files arrive from a single click on "Explain", they are
    /// stored with the message, and they are re-sent with every turn after it.
    /// </remarks>
    private const int MaxGraphAttachmentCharacters = 48_000;

    private readonly IChatService _chatService;
    private readonly IAgentService _agent;
    private readonly IWorkspaceService _workspace;
    private readonly IConversationService _conversations;
    private readonly IAttachmentService _attachments;
    private readonly IExportService _exportService;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly IConnectivityMonitor _connectivity;
    private readonly MarkdownParser _markdownParser;
    private readonly ILogger<ChatViewModel> _logger;
    private readonly DispatcherTimer _renderTimer;

    /// <summary>
    /// The cards of the run in progress, by the provider's call id.
    /// </summary>
    /// <remarks>
    /// A call is mentioned by up to three events and each of them has to reach the same card, so the
    /// card cannot be built from whichever event arrives. Ordinal comparison because these ids come
    /// off the wire and are matched, never displayed or sorted.
    /// </remarks>
    private readonly Dictionary<string, AgentToolCallViewModel> _toolCards = new(StringComparer.Ordinal);

    private CancellationTokenSource? _turnCancellation;
    private MessageViewModel? _streamingMessage;

    /// <summary>
    /// The graph selection the next message is about, or null for an ordinary message.
    /// </summary>
    /// <remarks>
    /// Set when a question arrives from the canvas or the inspector, and cleared as soon as a turn
    /// takes it. The context itself is not assembled here: the selection travels on the request and
    /// <c>IGraphContextSource</c> turns it into a context block during the normal build, which is
    /// why the canvas needs no pipeline of its own.
    /// </remarks>
    private GraphSelection? _graphSelection;

    [ObservableProperty]
    private Guid? _conversationId;

    [ObservableProperty]
    private string _title = Services.Localization.T("S.Sidebar.NewChat");

    [ObservableProperty]
    private string _draft = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isGenerating;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private ModelInfo? _selectedModel;

    [ObservableProperty]
    private bool _autoScroll = true;

    /// <summary>Set when a turn fails before any message exists to attach the error to.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBannerOpen))]
    private string? _bannerMessage;

    /// <summary>
    /// Two-way companion to <see cref="BannerMessage"/> for the notice bar.
    /// </summary>
    /// <remarks>
    /// The bar has no closed event: its dismiss button writes <c>IsOpen = false</c> straight
    /// back through the binding. Setting the message to null from here keeps that one gesture
    /// from leaving a dismissed bar that reopens the next time the property is re-read.
    /// </remarks>
    public bool IsBannerOpen
    {
        get => BannerMessage is { Length: > 0 };
        set
        {
            if (!value)
            {
                BannerMessage = null;
            }
        }
    }

    /// <summary>
    /// True when the user has scrolled away from the tail. Auto-scroll is suppressed while
    /// it is set, so reading earlier output is not interrupted by an arriving token.
    /// </summary>
    [ObservableProperty]
    private bool _isScrolledAway;

    /// <summary>Mirrors the chat setting so the view can route Enter without reading settings.</summary>
    [ObservableProperty]
    private bool _sendWithEnter = true;

    /// <summary>Mirrors the chat setting; inherited by every code block in the transcript.</summary>
    [ObservableProperty]
    private bool _highlightCode = true;

    /// <summary>Hint under the composer, which changes with the Enter/Shift+Enter setting.</summary>
    [ObservableProperty]
    private string _inputHint = string.Empty;

    /// <summary>
    /// Whether the next message goes to the agent instead of straight to the model.
    /// </summary>
    /// <remarks>
    /// Deliberately not remembered between sessions. An agent run can edit files and run programs,
    /// and starting an application in a mode that does that - because of a switch flipped days ago -
    /// is not a default anybody should inherit by surprise.
    /// </remarks>
    [ObservableProperty]
    private bool _isAgentMode;

    /// <summary>
    /// Which kind of agent run the next message starts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="IsAgentMode"/> rather than folded into one four-valued enum, because the
    /// two questions are asked at different moments: whether this message is a task at all, and - having
    /// decided it is - whether it should be carried out or thought through. The composer asks them as one
    /// menu, but the mode outlives being switched off, so coming back to the agent returns to the mode
    /// that was last chosen rather than to the default.
    /// </para>
    /// <para>
    /// Defaults to <see cref="AgentMode.Plan"/>, which is deliberate: the first thing agent mode does,
    /// for someone who has just found it, should be to describe what it would do rather than to start
    /// doing it.
    /// </para>
    /// </remarks>
    [ObservableProperty]
    private AgentMode _selectedAgentMode = AgentMode.Plan;

    /// <summary>
    /// The composer's agent button, which carries the current mode.
    /// </summary>
    /// <remarks>
    /// A control whose menu is shut has to say what it is set to, and "Agent" alone would not: the
    /// difference between reading a folder and rewriting it is exactly the thing worth reading before
    /// pressing Send.
    /// </remarks>
    public string AgentButtonText =>
        IsAgentMode ? $"{Localization.T("S.Agent.Label")} · {ModeName(SelectedAgentMode)}" : Localization.T("S.Agent.Label");

    /// <summary>A mode's name in the interface language; the Application layer's own names serve the model.</summary>
    private string ModeName(AgentMode mode) => mode switch
    {
        AgentMode.Plan => Localization.T("S.Agent.Mode.Plan"),
        AgentMode.PlanCanvas => Localization.T("S.Agent.Mode.PlanCanvas"),
        AgentMode.Build => Localization.T("S.Agent.Mode.Build"),
        _ => Localization.T("S.Agent.Mode.Off"),
    };

    /// <summary>
    /// Which entry of the agent menu is ticked, as a string.
    /// </summary>
    /// <remarks>
    /// One property compared against four literals, rather than four booleans, because off is a state of
    /// that menu exactly as much as the three modes are - and a single value is the only way to be sure
    /// that precisely one entry is ever ticked.
    /// </remarks>
    public string AgentModeSelection => IsAgentMode ? ModeName(SelectedAgentMode) : Localization.T("S.Agent.Mode.Off");

    /// <summary>
    /// What the agent would do with the next message, shown while agent mode is on.
    /// </summary>
    /// <remarks>
    /// Names the folder, and says whether anything can change. A user about to let a model edit files
    /// is entitled to see which files those are before sending; a user planning is entitled to know
    /// that nothing will be touched, which is the whole reason the planning modes exist.
    /// </remarks>
    public string AgentHint
    {
        get
        {
            var root = _workspace.Root is { Length: > 0 } open ? open : null;

            if (SelectedAgentMode.NeedsWorkspace())
            {
                return root is null
                    ? Localization.T("S.Agent.Hint.Build.NoFolder")
                    : Localization.T("S.Agent.Hint.Build.In", root);
            }

            var name = ModeName(SelectedAgentMode);

            return root is null
                ? Localization.T("S.Agent.Hint.Plan.New", name)
                : Localization.T("S.Agent.Hint.Plan.In", name, root);
        }
    }

    public ChatViewModel(
        IChatService chatService,
        IAgentService agent,
        IWorkspaceService workspace,
        IConversationService conversations,
        IAttachmentService attachments,
        IExportService exportService,
        ISettingsService settings,
        IDialogService dialogs,
        IConnectivityMonitor connectivity,
        AgentApprovalService approval,
        MarkdownParser markdownParser,
        ILogger<ChatViewModel> logger)
    {
        _chatService = chatService;
        _agent = agent;
        _workspace = workspace;
        _conversations = conversations;
        _attachments = attachments;
        _exportService = exportService;
        _settings = settings;
        _dialogs = dialogs;
        _connectivity = connectivity;
        _markdownParser = markdownParser;
        _logger = logger;

        Approval = approval;

        _renderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RenderInterval };
        _renderTimer.Tick += OnRenderTick;

        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));

        // The folder is chosen in Settings, so the hint under the composer would otherwise go on
        // naming a folder the agent is no longer working in.
        _workspace.RootChanged += (_, _) => OnPropertyChanged(nameof(AgentHint));

        ApplyRenderingSettings();
    }

    /// <summary>
    /// Rebuilds the words this pane computes in code, after a language switch.
    /// </summary>
    public void OnLanguageChanged()
    {
        Suggestions = BuildSuggestions();
        OnPropertyChanged(nameof(Suggestions));
        OnPropertyChanged(nameof(AgentHint));
        OnPropertyChanged(nameof(AgentButtonText));
        OnPropertyChanged(nameof(AgentModeSelection));

        // The hint depends on the setting and the sentence; both parts are re-read.
        ApplyRenderingSettings();

        // An unsaved chat carries the default title; a saved one carries the user's or the
        // generated one, which is translated for nobody.
        if (ConversationId is null)
        {
            Title = Localization.T("S.Sidebar.NewChat");
        }
    }

    /// <summary>
    /// The agent's approval gate, bound by the card above the composer.
    /// </summary>
    /// <remarks>
    /// Held as the concrete service rather than <see cref="IAgentApproval"/>, because what the view
    /// needs is the question currently pending - which is a property of this host's implementation, not
    /// something the Application layer knows or should know about.
    /// </remarks>
    public AgentApprovalService Approval { get; }

    public ObservableCollection<MessageViewModel> Messages { get; } = [];

    /// <summary>Files staged for the next message. Shown as removable chips above the input.</summary>
    public ObservableCollection<PendingAttachment> PendingAttachments { get; } = [];

    /// <summary>
    /// Starter prompts for the empty state (section 33). Fixed rather than generated: four
    /// predictable entries are more useful than a rotating set nobody can rely on. The titles and
    /// descriptions follow the language; the prompts stay English so the model understands them.
    /// </summary>
    public IReadOnlyList<ChatSuggestion> Suggestions { get; private set; } = BuildSuggestions();

    private static IReadOnlyList<ChatSuggestion> BuildSuggestions() =>
    [
        new(Localization.T("S.Suggest.Explain.Title"), Localization.T("S.Suggest.Explain.Description"), "Explain what this code does, step by step:\n\n"),
        new(Localization.T("S.Suggest.Function.Title"), Localization.T("S.Suggest.Function.Description"), "Write a function that "),
        new(Localization.T("S.Suggest.Analyse.Title"), Localization.T("S.Suggest.Analyse.Description"), "Review the attached file and summarise what it does."),
        new(Localization.T("S.Suggest.Debug.Title"), Localization.T("S.Suggest.Debug.Description"), "I'm getting this error and I can't work out why:\n\n"),
    ];

    /// <summary>Drives the "What can I help you with?" state instead of an empty grey panel.</summary>
    public bool IsEmpty => Messages.Count == 0;

    public bool CanSend => !IsGenerating && (Draft.Trim().Length > 0 || PendingAttachments.Count > 0);

    /// <summary>Raised when the transcript grows, so the view can scroll to the end.</summary>
    public event EventHandler? ScrollToEndRequested;

    /// <summary>Raised when the composer should take focus, after a new chat or a suggestion.</summary>
    public event EventHandler? FocusInputRequested;

    /// <summary>Raised when a title is generated, so the sidebar can update its row.</summary>
    public event EventHandler<ConversationTitleChangedEventArgs>? TitleChanged;

    /// <summary>Asks the view to put the caret in the composer.</summary>
    public void FocusInput() => FocusInputRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Loads a stored conversation into the pane.</summary>
    public async Task LoadConversationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        IsLoading = true;

        try
        {
            var detail = await _conversations.GetAsync(id, cancellationToken).ConfigureAwait(true);

            if (detail is null)
            {
                _logger.LogWarning("Conversation {Id} no longer exists.", id);
                StartNewConversation();
                return;
            }

            ConversationId = detail.Id;
            Title = detail.Title;
            BannerMessage = null;

            var renderMarkdown = _settings.Current.Chat.RenderMarkdown;

            Messages.Clear();
            foreach (var message in detail.Messages)
            {
                var view = new MessageViewModel(message, renderMarkdown, _markdownParser);

                // A tool row is an answer to the step above it, not a message to the user. Folding it
                // into that step is what makes a reopened conversation look like the one that was
                // closed, rather than a transcript with the agent's working out pasted into it.
                if (view.IsTool)
                {
                    LastAssistant()?.ToolCalls.Add(AgentToolCallViewModel.Restored(message));
                    continue;
                }

                Messages.Add(view);
            }

            ClearPendingAttachments();
            RequestScrollToEnd();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Resets the pane to an unsaved new chat. The row is created on first send.</summary>
    public void StartNewConversation()
    {
        CancelTurn();

        ConversationId = null;
        Title = Localization.T("S.Sidebar.NewChat");
        BannerMessage = null;

        Messages.Clear();
        ClearPendingAttachments();
        Draft = string.Empty;
        IsScrolledAway = false;
        _graphSelection = null;

        FocusInputRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops a starter prompt into the composer so the user can finish the sentence.</summary>
    [RelayCommand]
    private void UseSuggestion(ChatSuggestion? suggestion)
    {
        if (suggestion is null)
        {
            return;
        }

        Draft = suggestion.Prompt;
        FocusInputRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Asks about a graph selection, through the ordinary conversation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The canvas and the inspector both arrive here. There is no second chat: the question is a
    /// message like any other, and the only difference is that the request carries the selection so
    /// the context build can describe it.
    /// </para>
    /// <para>
    /// An empty <paramref name="prompt"/> means the person chose "Ask Kontur AI…" and wants to write
    /// their own question - the selection is held, the composer is focused, and nothing is sent. A
    /// question that came from a button is sent straight away, because clicking "Explain" is already
    /// the decision to ask.
    /// </para>
    /// <para>
    /// Agent mode is deliberately left alone: an agent run has its own context assembly, and giving
    /// it a selection is a change to the runner rather than to the composer.
    /// </para>
    /// </remarks>
    /// <param name="files">
    /// The selection's files, staged as ordinary attachments. This is the visible half of asking from
    /// the canvas: the chip says which file the question is about, the file travels with the message
    /// into the stored conversation, and a follow-up question therefore still has it - whereas the
    /// graph block is rebuilt per request and describes only whatever is selected at that moment.
    /// </param>
    public async Task AskAboutGraphAsync(
        GraphSelection selection,
        string prompt,
        string label,
        IReadOnlyList<WorkspacePath> files)
    {
        _graphSelection = selection.IsEmpty ? null : selection;

        await AttachFromWorkspaceAsync(files).ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(prompt))
        {
            _logger.LogInformation("A question about {Label} is waiting on the composer.", label);
            FocusInputRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        Draft = prompt;

        if (!CanSend)
        {
            // Mid-generation, or no model chosen yet. The question stays in the box with the
            // selection still attached, so pressing send later still asks about the same thing.
            FocusInputRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        await SendAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Stages files named by the graph, read through the workspace sandbox.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not <see cref="IAttachmentService"/>, which takes an absolute path: these paths come from a
    /// graph the user did not type, and the sandbox is what makes a path from a machine harmless -
    /// containment, protected names, and the size cap are all on that side. The chip and the wire
    /// format are the same either way, so a reader of the transcript cannot tell which door a file
    /// came through.
    /// </para>
    /// <para>
    /// A file that cannot be read is skipped in silence. The sandbox has already logged why, and a
    /// banner about one file of four - after a click that was about a selection rather than about
    /// that file - would be noise in front of the answer the person is waiting for.
    /// </para>
    /// </remarks>
    private async Task AttachFromWorkspaceAsync(IReadOnlyList<WorkspacePath> files)
    {
        if (files.Count == 0 || !_workspace.IsOpen)
        {
            return;
        }

        var staged = 0;
        var remaining = MaxGraphAttachmentCharacters;

        foreach (var path in files)
        {
            if (remaining <= 0)
            {
                break;
            }

            // Asking twice about the same file should not send it twice.
            if (PendingAttachments.Any(a => string.Equals(a.FileName, path.Value, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var read = await _workspace.ReadAsync(path).ConfigureAwait(true);

            if (read is not { Success: true, Value: { } file } || file.Content.Length == 0)
            {
                continue;
            }

            var content = Clip(file.Content, remaining);
            remaining -= content.Length;
            staged++;

            PendingAttachments.Add(new PendingAttachment(new NewAttachment
            {
                // The workspace-relative path, not the bare file name: it is what the graph block and
                // the question both say, and one project holds several Program.cs.
                FileName = path.Value,
                MimeType = "text/plain",
                Size = file.Size,
                TextContent = content,
                IsTruncated = file.IsTruncated || content.Length < file.Content.Length,
            }));
        }

        if (staged == 0)
        {
            return;
        }

        OnPropertyChanged(nameof(CanSend));
        SendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Cuts text to a length on a line boundary, so an excerpt never ends mid-token.
    /// </summary>
    /// <remarks>
    /// Half of a line of code invites the model to guess how it finished. The whole-line version costs
    /// a few characters and cannot be misread; <c>ContextBuilder</c> marks the result as truncated when
    /// it inlines it, so nothing pretends the file ended there.
    /// </remarks>
    private static string Clip(string content, int limit)
    {
        if (content.Length <= limit)
        {
            return content;
        }

        var head = content[..limit];
        var lastBreak = head.LastIndexOf('\n');

        return lastBreak > 0 ? head[..lastBreak] : head;
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Draft.Trim();

        if (text.Length == 0 && PendingAttachments.Count == 0)
        {
            return;
        }

        if (SelectedModel is not { } model)
        {
            BannerMessage = Localization.T("S.Banner.ChooseModel");
            return;
        }

        // Checked here rather than after the draft is cleared: a refusal that also loses what the
        // user typed is two problems where there was one.
        //
        // Only Build needs one. A plan for a project that does not exist yet has nothing to read, and
        // refusing it for want of a folder would refuse the case those modes were added for.
        if (IsAgentMode && SelectedAgentMode.NeedsWorkspace() && !_workspace.IsOpen)
        {
            BannerMessage = Localization.T("S.Banner.BuildNeedsFolder");

            return;
        }

        // Cleared immediately: the message is already committed from the user's point of
        // view, and leaving it in the box invites an accidental double send.
        Draft = string.Empty;
        BannerMessage = null;

        // Taken in one step, so whichever branch this turn goes down the selection belongs to the
        // message that was just asked and to nothing after it.
        var selection = _graphSelection;
        _graphSelection = null;

        var attachments = PendingAttachments.Select(a => a.Attachment).ToList();
        ClearPendingAttachments();

        var conversationId = await EnsureConversationAsync(model).ConfigureAwait(true);

        if (IsAgentMode)
        {
            await RunAgentAsync(new AgentRunRequest
            {
                ConversationId = conversationId,
                Content = text,
                ProviderId = model.ProviderId,
                ModelId = model.ModelId,
                Attachments = attachments,

                // Taken at Send, not read again while the run is going. Switching the picker
                // mid-run chooses what the next task will be.
                Mode = SelectedAgentMode,
            }).ConfigureAwait(true);

            return;
        }

        var request = new SendMessageRequest
        {
            ConversationId = conversationId,
            Content = text,
            ProviderId = model.ProviderId,
            ModelId = model.ModelId,
            Attachments = attachments,
            Selection = selection,
        };

        await RunTurnAsync(token => _chatService.SendMessageAsync(request, token)).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(IsGenerating))]
    private void Stop()
    {
        _logger.LogInformation("Generation stopped by the user.");
        CancelTurn();
    }

    /// <summary>Re-answers an assistant message, discarding it and anything after it.</summary>
    [RelayCommand]
    private async Task RegenerateAsync(MessageViewModel? message)
    {
        if (message is null || IsGenerating || ConversationId is not { } conversationId)
        {
            return;
        }

        if (SelectedModel is not { } model)
        {
            BannerMessage = Localization.T("S.Banner.ChooseModel.Regenerate");
            return;
        }

        // The service deletes from this message onward; the UI has to match that view of
        // history or the two will disagree about what the model was shown.
        var index = Messages.IndexOf(message);
        if (index >= 0)
        {
            while (Messages.Count > index)
            {
                Messages.RemoveAt(Messages.Count - 1);
            }
        }

        var request = new RegenerateRequest
        {
            ConversationId = conversationId,
            AssistantMessageId = message.Id,
            ProviderId = model.ProviderId,
            ModelId = model.ModelId,
        };

        await RunTurnAsync(token => _chatService.RegenerateAsync(request, token)).ConfigureAwait(true);
    }

    /// <summary>Retries the failed turn that produced this message.</summary>
    [RelayCommand]
    private Task RetryAsync(MessageViewModel? message) => RegenerateAsync(message);

    [RelayCommand]
    private void CopyMessage(MessageViewModel? message)
    {
        if (message is not null)
        {
            _dialogs.CopyToClipboard(message.Content);
        }
    }

    [RelayCommand]
    private static void CopyText(string? text)
    {
        // Static because the code-block Copy button passes its own text; nothing else is needed.
        if (!string.IsNullOrEmpty(text))
        {
            System.Windows.Clipboard.SetText(text);
        }
    }

    [RelayCommand]
    private static void BeginEdit(MessageViewModel? message)
    {
        if (message is { IsUser: true })
        {
            message.EditDraft = message.Content;
            message.IsEditing = true;
        }
    }

    [RelayCommand]
    private static void CancelEdit(MessageViewModel? message)
    {
        if (message is not null)
        {
            message.IsEditing = false;
            message.EditDraft = string.Empty;
        }
    }

    /// <summary>
    /// Saves an edited user message and re-answers from that point.
    /// </summary>
    /// <remarks>
    /// Everything after the edited message is discarded. Keeping the old answers would leave
    /// a transcript where the model appears to have replied to a question it never saw.
    /// </remarks>
    [RelayCommand]
    private async Task SaveEditAsync(MessageViewModel? message)
    {
        if (message is not { IsUser: true } || IsGenerating || ConversationId is not { } conversationId)
        {
            return;
        }

        var edited = message.EditDraft.Trim();

        if (edited.Length == 0 || edited == message.Content)
        {
            message.IsEditing = false;
            return;
        }

        if (SelectedModel is not { } model)
        {
            BannerMessage = Localization.T("S.Banner.ChooseModel.Edit");
            return;
        }

        message.IsEditing = false;

        await _conversations.UpdateMessageAsync(new MessageUpdate
        {
            MessageId = message.Id,
            Content = edited,
        }).ConfigureAwait(true);

        message.ReplaceContent(edited);

        // Drop the replies that followed the old wording, in the UI and in the database.
        await _conversations.DeleteFromMessageAsync(message.Id, inclusive: false).ConfigureAwait(true);

        var index = Messages.IndexOf(message);
        while (index >= 0 && Messages.Count > index + 1)
        {
            Messages.RemoveAt(Messages.Count - 1);
        }

        var detail = await _conversations.GetAsync(conversationId).ConfigureAwait(true);
        var lastAssistant = detail?.Messages.LastOrDefault(m => m.Role == MessageRole.Assistant);

        var request = new RegenerateRequest
        {
            ConversationId = conversationId,
            AssistantMessageId = lastAssistant?.Id ?? Guid.Empty,
            ProviderId = model.ProviderId,
            ModelId = model.ModelId,
        };

        await RunTurnAsync(token => _chatService.RegenerateAsync(request, token)).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteMessageAsync(MessageViewModel? message)
    {
        if (message is null || IsGenerating)
        {
            return;
        }

        if (_settings.Current.General.ConfirmBeforeDelete)
        {
            var confirmed = await _dialogs.ConfirmAsync(
                Localization.T("S.Dialog.DeleteMessage.Title"),
                Localization.T("S.Dialog.DeleteMessage.Message")).ConfigureAwait(true);

            if (!confirmed)
            {
                return;
            }
        }

        await _conversations.DeleteMessageAsync(message.Id).ConfigureAwait(true);
        Messages.Remove(message);
    }

    [RelayCommand]
    private async Task AttachFilesAsync()
    {
        var paths = _dialogs.OpenFiles(_attachments.BuildFileDialogFilter());

        foreach (var path in paths)
        {
            var result = await _attachments.LoadAsync(path).ConfigureAwait(true);

            if (!result.Success || result.Attachment is null)
            {
                BannerMessage = result.ErrorMessage;
                continue;
            }

            PendingAttachments.Add(new PendingAttachment(result.Attachment));
        }

        OnPropertyChanged(nameof(CanSend));
        SendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Accepts files dropped onto the input area.</summary>
    public async Task AddDroppedFilesAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!_attachments.IsSupported(path))
            {
                BannerMessage = Localization.T("S.Banner.UnsupportedFile", Path.GetFileName(path));
                continue;
            }

            var result = await _attachments.LoadAsync(path).ConfigureAwait(true);

            if (!result.Success || result.Attachment is null)
            {
                BannerMessage = result.ErrorMessage;
                continue;
            }

            PendingAttachments.Add(new PendingAttachment(result.Attachment));
        }

        OnPropertyChanged(nameof(CanSend));
        SendCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RemoveAttachment(PendingAttachment? attachment)
    {
        if (attachment is not null)
        {
            PendingAttachments.Remove(attachment);
            OnPropertyChanged(nameof(CanSend));
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Writes the conversation to disk in the chosen format.</summary>
    public async Task ExportAsync(ExportFormat format)
    {
        if (ConversationId is not { } id)
        {
            return;
        }

        var detail = await _conversations.GetAsync(id).ConfigureAwait(true);

        if (detail is null)
        {
            return;
        }

        var path = _dialogs.SaveFile(
            _exportService.GetFileDialogFilter(format),
            _exportService.SuggestFileName(detail, format));

        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, _exportService.Export(detail, format)).ConfigureAwait(true);
            BannerMessage = Localization.T("S.Banner.Exported", Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Export failed.");
            await _dialogs.ShowErrorAsync(Localization.T("S.Banner.ExportFailed"), ex.Message).ConfigureAwait(true);
        }
    }

    /// <summary>Re-reads the chat settings that affect how the pane behaves and renders.</summary>
    public void ApplyRenderingSettings()
    {
        var chat = _settings.Current.Chat;

        foreach (var message in Messages)
        {
            message.SetMarkdownRendering(chat.RenderMarkdown);
        }

        AutoScroll = chat.AutoScroll;
        HighlightCode = chat.HighlightCode;
        SendWithEnter = chat.SendWithEnter;

        InputHint = chat.SendWithEnter
            ? Localization.T("S.Hint.EnterSend")
            : Localization.T("S.Hint.ShiftEnterSend");
    }

    /// <summary>
    /// Runs one turn, translating <see cref="ChatTurnEvent"/>s into UI state.
    /// </summary>
    /// <remarks>
    /// Shared by Send, Regenerate and Retry: all three differ only in which service call
    /// produces the event stream, and duplicating this loop three times would mean three
    /// places to get cancellation and error handling subtly wrong.
    /// </remarks>
    private async Task RunTurnAsync(Func<CancellationToken, IAsyncEnumerable<ChatTurnEvent>> start)
    {
        CancelTurn();

        _turnCancellation = new CancellationTokenSource();
        var token = _turnCancellation.Token;

        IsGenerating = true;
        _renderTimer.Start();

        var renderMarkdown = _settings.Current.Chat.RenderMarkdown;

        try
        {
            await foreach (var evt in start(token).WithCancellation(token).ConfigureAwait(true))
            {
                switch (evt)
                {
                    case ChatTurnEvent.UserMessageSaved(var dto):
                        Messages.Add(new MessageViewModel(dto, renderMarkdown, _markdownParser));
                        RequestScrollToEnd();
                        break;

                    case ChatTurnEvent.AssistantMessageStarted(var dto):
                        _streamingMessage = new MessageViewModel(dto, renderMarkdown, _markdownParser);
                        Messages.Add(_streamingMessage);
                        RequestScrollToEnd();
                        break;

                    case ChatTurnEvent.ContentDelta(_, var text):
                        _streamingMessage?.AppendDelta(text);
                        break;

                    // Named rather than deconstructed: the event carries reasoning and cache counts
                    // this pane does not show yet, and a positional pattern would have to be widened
                    // every time one is added.
                    case ChatTurnEvent.Completed completed:
                        _streamingMessage?.Complete(
                            completed.InputTokens,
                            completed.OutputTokens,
                            completed.GenerationTimeMs);

                        // A finished answer is the strongest proof there is that the provider
                        // is reachable, which clears the offline strip on a network the OS
                        // considered healthy all along (a captive portal, most often).
                        _connectivity.ReportReachable();
                        break;

                    case ChatTurnEvent.Failed(_, var kind, var userMessage, var details, var retryable):
                        HandleFailure(kind, userMessage, details, retryable);
                        break;

                    case ChatTurnEvent.Cancelled:
                        _streamingMessage?.Cancel();
                        break;

                    case ChatTurnEvent.TitleGenerated(var id, var title):
                        Title = title;
                        TitleChanged?.Invoke(this, new ConversationTitleChangedEventArgs(id, title));
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the user pressed Stop. The service has already persisted whatever
            // text arrived before the cancellation.
            _streamingMessage?.Cancel();
        }
        catch (Exception ex)
        {
            // A bug rather than a provider failure - provider failures arrive as Failed events.
            _logger.LogError(ex, "Unexpected failure during a chat turn.");
            HandleFailure(AIErrorKind.Unknown, Localization.T("S.Error.Unknown.Generation"), ex.Message, true);
        }
        finally
        {
            _renderTimer.Stop();

            // One final parse so the last tokens are rendered rather than left as raw text.
            _streamingMessage?.RebuildBlocks();
            _streamingMessage = null;

            IsGenerating = false;

            _turnCancellation?.Dispose();
            _turnCancellation = null;

            RequestScrollToEnd();
        }
    }

    /// <summary>
    /// Runs one agent turn, translating <see cref="AgentEvent"/>s into UI state.
    /// </summary>
    /// <remarks>
    /// Deliberately the same shape as <see cref="RunTurnAsync"/> - same cancellation source, same
    /// render timer, same finally - because the two differ only in what the events mean. What is new
    /// is that a turn is now several messages instead of one, and that part of what happens in it is
    /// not text. The tool cards hang off the step that asked for them, so nothing the agent did to the
    /// user's files is shown apart from the words that led to it.
    /// </remarks>
    private async Task RunAgentAsync(AgentRunRequest request)
    {
        CancelTurn();

        _turnCancellation = new CancellationTokenSource();
        var token = _turnCancellation.Token;

        IsGenerating = true;
        _renderTimer.Start();

        var renderMarkdown = _settings.Current.Chat.RenderMarkdown;

        _toolCards.Clear();

        try
        {
            await foreach (var evt in _agent.RunAsync(request, token).WithCancellation(token).ConfigureAwait(true))
            {
                switch (evt)
                {
                    case AgentEvent.UserMessageSaved(var dto):
                        Messages.Add(new MessageViewModel(dto, renderMarkdown, _markdownParser));
                        RequestScrollToEnd();
                        break;

                    case AgentEvent.TitleGenerated(var id, var title):
                        Title = title;
                        TitleChanged?.Invoke(this, new ConversationTitleChangedEventArgs(id, title));
                        break;

                    case AgentEvent.StepStarted(_, var dto):
                        // One last parse of the step that is ending, while it is still the message the
                        // render timer is pointed at.
                        _streamingMessage?.RebuildBlocks();

                        _streamingMessage = new MessageViewModel(dto, renderMarkdown, _markdownParser);
                        Messages.Add(_streamingMessage);
                        RequestScrollToEnd();
                        break;

                    case AgentEvent.ContentDelta(_, var text):
                        _streamingMessage?.AppendDelta(text);
                        break;

                    case AgentEvent.ReasoningDelta(_, var text):
                        _streamingMessage?.AppendReasoning(text);
                        break;

                    case AgentEvent.ToolCallProposed(var messageId, var call, var risk):
                        Card(messageId, call, risk);
                        RequestScrollToEnd();
                        break;

                    case AgentEvent.ToolCallStarted(var messageId, var call):
                        Card(messageId, call, risk: null).Start();
                        break;

                    // No step id on this one, so it lands on the step still in hand - which is the step
                    // that asked, because the loop finishes a call before it starts another.
                    case AgentEvent.ToolCallFinished(var call, var outcome, var row, var summary, var detail):
                        Card(messageId: null, call, risk: null).Finish(outcome, row.Content, summary, detail);
                        RequestScrollToEnd();
                        break;

                    // Tokens, but no elapsed time: see MessageViewModel.Complete.
                    case AgentEvent.StepCompleted(_, _, var input, var output, _):
                        _streamingMessage?.Complete(input, output, generationTimeMs: null);
                        break;

                    case AgentEvent.Completed(_, var steps, var reason, _):
                        NoteRunEnd(steps, reason);

                        // A finished run is the strongest proof there is that the provider is
                        // reachable, which clears the offline strip on a network the OS considered
                        // healthy all along (a captive portal, most often).
                        _connectivity.ReportReachable();
                        break;

                    case AgentEvent.Failed(_, var kind, var userMessage, var details, var retryable):
                        HandleFailure(kind, userMessage, details, retryable);
                        break;

                    case AgentEvent.Cancelled(_, var steps):
                        NoteStopped(steps);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the user pressed Stop. Everything the run had already done is on disk,
            // including the tool rows - the loop writes those with CancellationToken.None so that a
            // file which was written is never left unrecorded.
            NoteStopped(steps: null);
        }
        catch (Exception ex)
        {
            // A bug rather than a provider failure - provider failures arrive as Failed events.
            _logger.LogError(ex, "Unexpected failure during an agent run.");
            HandleFailure(AIErrorKind.Unknown, Localization.T("S.Error.Unknown.Agent"), ex.Message, true);
        }
        finally
        {
            _renderTimer.Stop();

            // A card still running is one whose tool was interrupted, and saying so is exactly what
            // this transcript is for: a write that was cut off may or may not have happened, and
            // leaving the card spinning would claim otherwise.
            foreach (var card in _toolCards.Values)
            {
                card.Abandon();
            }

            _toolCards.Clear();

            // One final parse so the last tokens are rendered rather than left as raw text.
            _streamingMessage?.RebuildBlocks();
            _streamingMessage = null;

            IsGenerating = false;

            _turnCancellation?.Dispose();
            _turnCancellation = null;

            RequestScrollToEnd();
        }
    }

    /// <summary>
    /// Finds or creates the card for one call.
    /// </summary>
    /// <remarks>
    /// Creating it here rather than only on the proposal is what keeps the transcript honest about
    /// calls that never got that far: a tool the model invented, or arguments that were not valid
    /// JSON, go straight to their answer without ever being proposed.
    /// </remarks>
    private AgentToolCallViewModel Card(Guid? messageId, AIToolCall call, AgentToolRisk? risk)
    {
        if (_toolCards.TryGetValue(call.Id, out var existing))
        {
            return existing;
        }

        var card = AgentToolCallViewModel.Live(call, risk);
        _toolCards[call.Id] = card;

        StepMessage(messageId)?.ToolCalls.Add(card);

        return card;
    }

    /// <summary>The message a card belongs under: the step named by the event, or the one in hand.</summary>
    private MessageViewModel? StepMessage(Guid? messageId)
    {
        if (messageId is { } id)
        {
            // From the end: the step being answered is the last thing in the transcript.
            for (var i = Messages.Count - 1; i >= 0; i--)
            {
                if (Messages[i].Id == id)
                {
                    return Messages[i];
                }
            }
        }

        return _streamingMessage;
    }

    /// <summary>The step a restored tool row answers, which is the last one loaded before it.</summary>
    private MessageViewModel? LastAssistant()
    {
        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            if (Messages[i].IsAssistant)
            {
                return Messages[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Says what a stopped run leaves behind, wherever that can honestly be said.
    /// </summary>
    /// <remarks>
    /// Stop during a step marks that step cancelled, which is what the user is looking at. Stop
    /// between steps - while a tool was running, or while its question was on screen - leaves nothing
    /// mid-stream to mark, and silently doing nothing would look like the button had not worked.
    /// </remarks>
    private void NoteStopped(int? steps)
    {
        if (_streamingMessage is { IsStreaming: true } message)
        {
            message.Cancel();
            return;
        }

        BannerMessage = steps is { } count and > 0
            ? Localization.T("S.Stop.AfterSteps", count)
            : Localization.T("S.Stop.Plain");
    }

    /// <summary>
    /// Says why a run ended, when the reason is not simply that the agent had finished.
    /// </summary>
    /// <remarks>
    /// A budget that ran out is not a failure and not an answer, and it is the one thing about a run
    /// the transcript cannot show on its own: the last step reads like any other. The elapsed time is
    /// left out of the messages on purpose - it belongs to the run, and pinning it on the last step
    /// would be a number that means nothing.
    /// </remarks>
    private void NoteRunEnd(int steps, AgentStopReason reason)
    {
        BannerMessage = reason switch
        {
            AgentStopReason.StepLimit =>
                Localization.T("S.RunEnd.StepLimit", steps),

            AgentStopReason.TimeLimit =>
                Localization.T("S.RunEnd.TimeLimit", steps),

            _ => BannerMessage,
        };

        _logger.LogInformation("Agent run ended after {Steps} step(s): {Reason}.", steps, reason);
    }

    private void HandleFailure(AIErrorKind kind, string userMessage, string? details, bool isRetryable)
    {
        // The mapper's sentence is written in English in the Application layer; the interface's own
        // words for the kind take precedence, and the mapper's technical detail stays as the part
        // that is folded away. A kind with no sentence of its own keeps the original message.
        var sentence = kind switch
        {
            AIErrorKind.InvalidApiKey => "S.Error.InvalidApiKey",
            AIErrorKind.PermissionDenied => "S.Error.PermissionDenied",
            AIErrorKind.NotFound => "S.Error.NotFound",
            AIErrorKind.RateLimited => "S.Error.RateLimited",
            AIErrorKind.Timeout => "S.Error.Timeout",
            AIErrorKind.ServerError => "S.Error.ServerError",
            AIErrorKind.ServiceUnavailable => "S.Error.ServiceUnavailable",
            AIErrorKind.NetworkError => "S.Error.NetworkError",
            AIErrorKind.ContextLengthExceeded => "S.Error.ContextLengthExceeded",
            AIErrorKind.ModelUnavailable => "S.Error.ModelUnavailable",
            AIErrorKind.InvalidRequest => "S.Error.InvalidRequest",
            AIErrorKind.ContentFiltered => "S.Error.ContentFiltered",
            AIErrorKind.NotConfigured => "S.Error.NotConfigured",
            _ => null,
        };

        var message = sentence is { } key ? Localization.T(key) : userMessage;

        if (_streamingMessage is not null)
        {
            _streamingMessage.Fail(message, details, isRetryable);
        }
        else
        {
            // No message to attach the error to - a failure before the turn even started.
            BannerMessage = message;
        }

        switch (ReachabilityEvidence(kind))
        {
            case true:
                _connectivity.ReportReachable();
                break;

            case false:
                _connectivity.ReportUnreachable();
                break;
        }

        _logger.LogWarning("Chat turn failed: {Kind}.", kind);
    }

    /// <summary>
    /// What a failure says about the connection, as opposed to what it says about the request.
    /// </summary>
    /// <returns>
    /// Null where the failure proves nothing either way, so the offline strip keeps whatever
    /// state it already had rather than guessing from ambiguous evidence.
    /// </returns>
    private static bool? ReachabilityEvidence(AIErrorKind kind) => kind switch
    {
        AIErrorKind.NetworkError => false,

        // The provider answered. It refused, but it answered, so the connection works.
        AIErrorKind.InvalidApiKey
            or AIErrorKind.PermissionDenied
            or AIErrorKind.NotFound
            or AIErrorKind.RateLimited
            or AIErrorKind.ServerError
            or AIErrorKind.ServiceUnavailable
            or AIErrorKind.ContextLengthExceeded
            or AIErrorKind.ModelUnavailable
            or AIErrorKind.InvalidRequest
            or AIErrorKind.ContentFiltered => true,

        // Timeout is genuinely ambiguous - a dead link and a slow model are indistinguishable
        // from here. NotConfigured and Cancelled never put a packet on the wire. Unknown is a
        // bug in this application, not a statement about the network.
        _ => null,
    };

    /// <summary>Creates the conversation row on first send, so an abandoned draft leaves nothing behind.</summary>
    private async Task<Guid> EnsureConversationAsync(ModelInfo model)
    {
        if (ConversationId is { } existing)
        {
            await _conversations.SetModelAsync(existing, model.ProviderId, model.ModelId).ConfigureAwait(true);
            return existing;
        }

        var created = await _conversations
            .CreateAsync(providerId: model.ProviderId, modelId: model.ModelId)
            .ConfigureAwait(true);

        ConversationId = created.Id;
        Title = created.Title;

        return created.Id;
    }

    private void OnRenderTick(object? sender, EventArgs e) => _streamingMessage?.RebuildBlocks();

    private void CancelTurn()
    {
        if (_turnCancellation is { IsCancellationRequested: false })
        {
            _turnCancellation.Cancel();
        }
    }

    private void ClearPendingAttachments()
    {
        PendingAttachments.Clear();
        OnPropertyChanged(nameof(CanSend));
        SendCommand.NotifyCanExecuteChanged();
    }

    private void RequestScrollToEnd()
    {
        if (AutoScroll)
        {
            ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    partial void OnDraftChanged(string value)
    {
        OnPropertyChanged(nameof(CanSend));
        SendCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedModelChanged(ModelInfo? value)
    {
        if (value is not null && ConversationId is { } id)
        {
            // Fire-and-forget is acceptable here: it records a preference, and a failure
            // costs the user nothing beyond re-picking the model next time.
            _ = _conversations.SetModelAsync(id, value.ProviderId, value.ModelId);
        }
    }

    /// <summary>
    /// Asks for a folder the moment a mode that needs one is turned on without one.
    /// </summary>
    /// <remarks>
    /// The alternative is a menu entry that appears to work and then refuses the first message, with
    /// the fix on another screen. Turning the mode on is the point at which the user has said what they
    /// want, so it is the point at which to ask where.
    /// </remarks>
    partial void OnIsAgentModeChanged(bool value)
    {
        OnPropertyChanged(nameof(AgentHint));
        OnPropertyChanged(nameof(AgentButtonText));
        OnPropertyChanged(nameof(AgentModeSelection));

        if (value && SelectedAgentMode.NeedsWorkspace() && !_workspace.IsOpen)
        {
            // Fire-and-forget, as elsewhere in this class: the picker is modal, and the method
            // below reports its own failures rather than letting one escape.
            _ = PromptForWorkspaceAsync();
        }
    }

    /// <summary>
    /// Asks the same question when the mode changes while the agent is already on.
    /// </summary>
    /// <remarks>
    /// Someone who has been planning and switches to Build has just said they want the work done, and
    /// wanting it done in a folder nobody has chosen is not a thing they can have meant.
    /// </remarks>
    partial void OnSelectedAgentModeChanged(AgentMode value)
    {
        OnPropertyChanged(nameof(AgentHint));
        OnPropertyChanged(nameof(AgentButtonText));
        OnPropertyChanged(nameof(AgentModeSelection));

        if (IsAgentMode && value.NeedsWorkspace() && !_workspace.IsOpen)
        {
            _ = PromptForWorkspaceAsync();
        }
    }

    /// <summary>
    /// Turns the agent on in one of its modes, from the composer's menu.
    /// </summary>
    /// <remarks>
    /// The mode is set before the agent is turned on, and that order matters: both of the handlers
    /// above ask for a folder when a build has none, and assigning the mode while the agent is still
    /// off leaves only one of them able to ask. Turning it on first would put the same question twice,
    /// once about the mode being left behind.
    /// </remarks>
    [RelayCommand]
    private void SetAgentMode(AgentMode mode)
    {
        SelectedAgentMode = mode;
        IsAgentMode = true;
    }

    /// <summary>
    /// Puts the composer back to a plain message.
    /// </summary>
    /// <remarks>
    /// The mode is deliberately left as it was. Coming back to the agent an hour later returns to the
    /// mode last chosen rather than to the default, which is what "off" means for every other control
    /// that remembers where it was.
    /// </remarks>
    [RelayCommand]
    private void TurnAgentOff() => IsAgentMode = false;

    private async Task PromptForWorkspaceAsync()
    {
        try
        {
            var chosen = _dialogs.OpenFolder(Localization.T("S.Dialog.ChooseWorkspace"));

            // Cancelling falls back to planning rather than turning the agent off. The user has said
            // they want the agent; only the part of it that needs a folder is unavailable, and Plan is
            // the mode that works without one.
            if (chosen is not { Length: > 0 })
            {
                SelectedAgentMode = AgentMode.Plan;
                return;
            }

            var result = await _workspace.OpenAsync(chosen).ConfigureAwait(true);

            if (result is { Success: true, Value: { Length: > 0 } })
            {
                // RootChanged refreshes AgentHint, so there is nothing to raise here.
                BannerMessage = null;
                return;
            }

            // The workspace's own words: it refuses folders a picker allows - a drive root, a system
            // folder, this application's data - and only it knows which of those this was.
            SelectedAgentMode = AgentMode.Plan;
            BannerMessage = result.Error ?? Localization.T("S.Chat.NotWorkspace");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open a workspace folder from the composer.");

            SelectedAgentMode = AgentMode.Plan;
            BannerMessage = Localization.T("S.Chat.NotOpened");
        }
    }
}

/// <summary>A starter prompt on the empty-state screen.</summary>
/// <param name="Prompt">Text placed in the composer, deliberately unfinished so the user continues it.</param>
public sealed record ChatSuggestion(string Title, string Description, string Prompt);

/// <summary>A file staged for the next message, with a display size for its chip.</summary>
public sealed class PendingAttachment
{
    public PendingAttachment(NewAttachment attachment)
    {
        Attachment = attachment;
    }

    public NewAttachment Attachment { get; }

    public string FileName => Attachment.FileName;

    public bool IsTruncated => Attachment.IsTruncated;

    /// <summary>Human-readable size for the chip label.</summary>
    public string DisplaySize => Attachment.Size switch
    {
        < 1024 => $"{Attachment.Size} B",
        < 1024 * 1024 => $"{Attachment.Size / 1024.0:0.#} KB",
        _ => $"{Attachment.Size / (1024.0 * 1024.0):0.#} MB",
    };
}

/// <summary>Announces an auto-generated title so the sidebar row can be updated in place.</summary>
public sealed class ConversationTitleChangedEventArgs : EventArgs
{
    public ConversationTitleChangedEventArgs(Guid conversationId, string title)
    {
        ConversationId = conversationId;
        Title = title;
    }

    public Guid ConversationId { get; }
    public string Title { get; }
}
