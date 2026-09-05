using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using AIClient.App.ViewModels;

namespace AIClient.App.ViewModels;

/// <summary>
/// A compact, always-current view of what the agent is doing - the surface that makes AI
/// activity legible without opening the conversation.
/// </summary>
/// <remarks>
/// <para>
/// There is no separate task store, and that is the honest architecture: the agent's
/// steps and tool calls are already persisted as messages by <see cref="ChatViewModel"/>
/// and its services. This view model reads that transcript and projects the live shape
/// of it - current run, running tool, finished steps - rather than maintaining a second
/// history that would have to be kept in sync with the first forever.
/// </para>
/// <para>
/// It updates by subscription: property changes on the chat view model and collection
/// changes on its messages and tool call cards all funnel into one rebuild. The rebuild
/// is a projection over the last few dozen items, cheap enough to run on every token.
/// </para>
/// </remarks>
public sealed partial class TasksViewModel : ObservableObject
{
    private const int MaxRows = 40;

    private readonly ChatViewModel _chat;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _stateText = "Idle";

    [ObservableProperty]
    private string _runKind = string.Empty;

    [ObservableProperty]
    private bool _isApprovalPending;

    [ObservableProperty]
    private bool _hasRows;

    public ObservableCollection<TaskRowViewModel> Rows { get; } = [];

    public TasksViewModel(ChatViewModel chat)
    {
        ArgumentNullException.ThrowIfNull(chat);

        _chat = chat;

        _chat.PropertyChanged += OnChatPropertyChanged;

        foreach (var message in _chat.Messages)
        {
            AttachMessage(message);
        }

        _chat.Messages.CollectionChanged += OnMessagesChanged;
        Rebuild();
    }

    private void OnChatPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChatViewModel.IsGenerating):
            case nameof(ChatViewModel.IsAgentMode):
            case nameof(ChatViewModel.SelectedAgentMode):
                Rebuild();
                break;
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
        if (e.PropertyName is nameof(MessageViewModel.Status) or nameof(MessageViewModel.IsStreaming))
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
        if (e.PropertyName is nameof(AgentToolCallViewModel.State) or nameof(AgentToolCallViewModel.Summary))
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        IsRunning = _chat.IsGenerating;
        IsApprovalPending = _chat.Approval.IsAsking;

        RunKind = _chat.IsAgentMode
            ? _chat.SelectedAgentMode switch
            {
                Application.DTOs.AgentMode.Build => "Agent · build",
                Application.DTOs.AgentMode.PlanCanvas => "Agent · plan + canvas",
                Application.DTOs.AgentMode.Plan => "Agent · plan",
                _ => "Agent",
            }
            : "Chat";

        StateText = _chat.IsGenerating
            ? _chat.IsAgentMode ? "Working" : "Answering"
            : IsApprovalPending ? "Waiting for you" : "Idle";

        Rows.Clear();

        var rows = new List<TaskRowViewModel>();

        foreach (var message in _chat.Messages)
        {
            if (message.ToolCalls.Count == 0 && !message.IsAssistant)
            {
                continue;
            }

            foreach (var tool in message.ToolCalls)
            {
                rows.Add(TaskRowViewModel.FromTool(tool));
            }

            if (message.IsStreaming && message.ToolCalls.Count == 0)
            {
                rows.Add(TaskRowViewModel.Streaming(message));
            }
        }

        foreach (var row in rows.TakeLast(MaxRows))
        {
            Rows.Add(row);
        }

        HasRows = Rows.Count > 0;
    }
}

/// <summary>One line of the task surface: a tool call, a streaming step, or a state.</summary>
public sealed partial class TaskRowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private TaskRowState _state;

    private TaskRowViewModel(string title, string detail, TaskRowState state)
    {
        _title = title;
        _detail = detail;
        _state = state;
    }

    public static TaskRowViewModel FromTool(AgentToolCallViewModel tool) => new(
        tool.Headline,
        tool.ToolName,
        tool.State switch
        {
            AgentToolCallState.Proposed => TaskRowState.Waiting,
            AgentToolCallState.Running => TaskRowState.Running,
            AgentToolCallState.Succeeded => TaskRowState.Done,
            AgentToolCallState.Failed => TaskRowState.Failed,
            AgentToolCallState.Denied => TaskRowState.Blocked,
            AgentToolCallState.Abandoned => TaskRowState.Interrupted,
            _ => TaskRowState.Waiting,
        });

    public static TaskRowViewModel Streaming(MessageViewModel message) => new(
        "Writing",
        message.ModelId ?? "assistant",
        TaskRowState.Running);
}

public enum TaskRowState
{
    Waiting,
    Running,
    Done,
    Failed,
    Blocked,
    Interrupted,
}
