using System.Collections.ObjectModel;
using AIClient.App.Services;
using AIClient.Application.Interfaces;
using AIClient.Domain.Graph;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIClient.App.ViewModels;

/// <summary>
/// What the runtime did: the conversation's steps and tool calls merged with the
/// graph's timeline into one chronological trace.
/// </summary>
/// <remarks>
/// <para>
/// There is no trajectory store, for the same reason <see cref="TasksViewModel"/> keeps
/// none: the transcript is already persisted as messages by <see cref="ChatViewModel"/>
/// and the graph history by <see cref="IGraphService"/>. This view model projects both
/// into rows rather than maintaining a third history that would have to be kept in sync
/// with the first two forever.
/// </para>
/// <para>
/// Unlike the reference prototype, there are no file-snapshot checkpoints here: nothing
/// below the UI can restore file contents, so the surface shows the graph's own
/// undo/redo steps (which <em>are</em> restorable) instead of pretending otherwise.
/// </para>
/// </remarks>
public sealed partial class TrajectoryViewModel : ObservableObject
{
    private readonly ChatViewModel _chat;
    private readonly IGraphService _graph;
    private readonly ILogger<TrajectoryViewModel> _logger;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private TrajectoryFilter _filter;

    [ObservableProperty]
    private bool _hasRows;

    public ObservableCollection<TrajectoryRowViewModel> Rows { get; } = [];

    public TrajectoryViewModel(ChatViewModel chat, IGraphService graph, ILogger<TrajectoryViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(logger);

        _chat = chat;
        _graph = graph;
        _logger = logger;

        _chat.PropertyChanged += OnChatPropertyChanged;

        foreach (var message in _chat.Messages)
        {
            AttachMessage(message);
        }

        _chat.Messages.CollectionChanged += OnMessagesChanged;

        // Raised on the caller's thread; the rebuild touches bound collections.
        _graph.TimelineChanged += (_, _) => UiThread.Post(Rebuild);

        Rebuild();
    }

    /// <summary>Recomputes the rows after a language switch.</summary>
    public void OnLanguageChanged() => Rebuild();

    /// <summary>Writes the open conversation's transcript via the chat's own export.</summary>
    [RelayCommand]
    private Task ExportTranscriptAsync() => _chat.ExportAsync(ExportFormat.Markdown);

    /// <summary>Selects a timeline category from the filter chips.</summary>
    [RelayCommand]
    private void SetFilter(TrajectoryFilter filter) => Filter = filter;

    partial void OnQueryChanged(string value) => Rebuild();

    partial void OnFilterChanged(TrajectoryFilter value) => Rebuild();

    private void OnChatPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatViewModel.IsGenerating))
        {
            Rebuild();
        }
    }

    private void OnMessagesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is { } added)
        {
            foreach (MessageViewModel message in added)
            {
                AttachMessage(message);
            }
        }

        Rebuild();
    }

    private void AttachMessage(MessageViewModel message)
    {
        message.PropertyChanged += OnMessagePropertyChanged;

        foreach (var tool in message.ToolCalls)
        {
            tool.PropertyChanged += OnToolPropertyChanged;
        }

        message.ToolCalls.CollectionChanged += OnToolCallsChanged;
    }

    private void OnMessagePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MessageViewModel.Status)
            or nameof(MessageViewModel.Content)
            or nameof(MessageViewModel.IsStreaming))
        {
            Rebuild();
        }
    }

    private void OnToolCallsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is { } added)
        {
            foreach (AgentToolCallViewModel tool in added)
            {
                tool.PropertyChanged += OnToolPropertyChanged;
            }
        }

        Rebuild();
    }

    private void OnToolPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AgentToolCallViewModel.State)
            or nameof(AgentToolCallViewModel.Summary))
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        Rows.Clear();

        var staged = new List<(DateTimeOffset At, int Order, TrajectoryRowViewModel Row)>();
        var order = 0;

        foreach (var message in _chat.Messages)
        {
            if (message.IsTool)
            {
                continue;
            }

            staged.Add((message.CreatedAt, order++, TrajectoryRowViewModel.FromMessage(message)));

            // A call belongs to the step that asked for it: staged just after the parent
            // so the chronological sort keeps them together.
            foreach (var tool in message.ToolCalls)
            {
                staged.Add((message.CreatedAt, order++, TrajectoryRowViewModel.FromTool(tool, message.CreatedAt)));
            }
        }

        foreach (var entry in _graph.Timeline)
        {
            staged.Add((entry.At, order++, TrajectoryRowViewModel.FromGraph(entry)));
        }

        foreach (var (_, _, row) in staged
            .OrderBy(s => s.At)
            .ThenBy(s => s.Order)
            .Where(s => MatchesFilter(s.Row))
            .TakeLast(200))
        {
            Rows.Add(row);
        }

        HasRows = Rows.Count > 0;
    }

    private bool MatchesFilter(TrajectoryRowViewModel row)
    {
        var categoryOk = Filter switch
        {
            TrajectoryFilter.Tools => row.Kind == TrajectoryRowKind.Tool,
            TrajectoryFilter.Agent => row.Kind is TrajectoryRowKind.Assistant or TrajectoryRowKind.Tool,
            TrajectoryFilter.Graph => row.Kind == TrajectoryRowKind.Graph,
            TrajectoryFilter.Issues => row.State is TrajectoryRowState.Failed
                or TrajectoryRowState.Rejected
                or TrajectoryRowState.Blocked,
            _ => true,
        };

        if (!categoryOk)
        {
            return false;
        }

        var query = Query.Trim();

        if (query.Length == 0)
        {
            return true;
        }

        return row.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Detail.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The timeline's category filter. Mirrors the reference UI's filter chips.</summary>
public enum TrajectoryFilter
{
    All,
    Tools,
    Agent,
    Graph,
    Issues,
}

/// <summary>One timeline row: a message, a tool call, or a graph step.</summary>
public sealed partial class TrajectoryRowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private DateTimeOffset _at;

    [ObservableProperty]
    private TrajectoryRowKind _kind;

    [ObservableProperty]
    private TrajectoryRowState _state;

    private TrajectoryRowViewModel(string title, string detail, DateTimeOffset at, TrajectoryRowKind kind, TrajectoryRowState state)
    {
        _title = title;
        _detail = detail;
        _at = at;
        _kind = kind;
        _state = state;
    }

    public static TrajectoryRowViewModel FromMessage(MessageViewModel message)
    {
        var state = message.Status switch
        {
            Domain.Enums.MessageStatus.Streaming => TrajectoryRowState.Running,
            Domain.Enums.MessageStatus.Failed => TrajectoryRowState.Failed,
            Domain.Enums.MessageStatus.Cancelled => TrajectoryRowState.Blocked,
            _ => TrajectoryRowState.Done,
        };

        var kind = message.IsUser ? TrajectoryRowKind.User : TrajectoryRowKind.Assistant;
        var title = message.IsUser
            ? Localization.T("S.Trajectory.Row.User")
            : string.IsNullOrWhiteSpace(message.ModelId)
                ? Localization.T("S.Trajectory.Row.Assistant")
                : message.ModelId;

        return new TrajectoryRowViewModel(title, message.Content, message.CreatedAt, kind, state);
    }

    public static TrajectoryRowViewModel FromTool(AgentToolCallViewModel tool, DateTimeOffset at)
    {
        var state = tool.State switch
        {
            AgentToolCallState.Proposed => TrajectoryRowState.Idle,
            AgentToolCallState.Running => TrajectoryRowState.Running,
            AgentToolCallState.Succeeded => TrajectoryRowState.Done,
            AgentToolCallState.Failed => TrajectoryRowState.Failed,
            AgentToolCallState.Denied => TrajectoryRowState.Blocked,
            AgentToolCallState.Abandoned => TrajectoryRowState.Blocked,
            _ => TrajectoryRowState.Idle,
        };

        return new TrajectoryRowViewModel(tool.Headline, tool.ToolName, at, TrajectoryRowKind.Tool, state);
    }

    public static TrajectoryRowViewModel FromGraph(GraphTimelineEntry entry)
    {
        var state = entry.WasRejected ? TrajectoryRowState.Rejected : TrajectoryRowState.Done;
        var title = entry.Title.StartsWith("S.", StringComparison.Ordinal)
            ? Localization.T(entry.Title)
            : entry.Title;

        return new TrajectoryRowViewModel(
            title,
            entry.Description ?? string.Empty,
            entry.At,
            TrajectoryRowKind.Graph,
            state);
    }
}

public enum TrajectoryRowKind
{
    User,
    Assistant,
    Tool,
    Graph,
}

public enum TrajectoryRowState
{
    Idle,
    Running,
    Done,
    Failed,
    Blocked,
    Rejected,
}
