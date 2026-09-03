using System.Collections.ObjectModel;
using AIClient.Application.DTOs;
using AIClient.Application.Markdown;
using AIClient.Domain.Enums;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIClient.App.ViewModels;

/// <summary>
/// One message in the transcript.
/// </summary>
/// <remarks>
/// Streaming is the constraint that shapes this class. Tokens arrive dozens of times a
/// second, and re-parsing the whole message and rebuilding its visual tree on each one is
/// what makes naive chat UIs stutter. So:
/// <list type="bullet">
/// <item><description>
/// Text accumulates in a <see cref="System.Text.StringBuilder"/>, not by string
/// concatenation - the latter is quadratic over a long answer.
/// </description></item>
/// <item><description>
/// Markdown is re-parsed on a timer rather than per token, and the resulting blocks are
/// diffed by content hash so unchanged blocks keep their existing visuals.
/// </description></item>
/// </list>
/// </remarks>
public sealed partial class MessageViewModel : ObservableObject
{
    private readonly System.Text.StringBuilder _buffer = new();
    private readonly MarkdownParser _parser;

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private MessageStatus _status;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _errorTechnicalDetails;

    [ObservableProperty]
    private bool _isRetryable;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editDraft = string.Empty;

    [ObservableProperty]
    private int? _inputTokens;

    [ObservableProperty]
    private int? _outputTokens;

    [ObservableProperty]
    private int? _generationTimeMs;

    [ObservableProperty]
    private bool _isRenderingMarkdown = true;

    /// <param name="parser">
    /// Shared with every other message: the Markdig pipeline it wraps is expensive to build
    /// and stateless once built, so one instance serves the whole transcript.
    /// </param>
    public MessageViewModel(MessageDto dto, bool renderMarkdown, MarkdownParser parser)
    {
        _parser = parser;

        Id = dto.Id;
        ConversationId = dto.ConversationId;
        Role = dto.Role;
        CreatedAt = dto.CreatedAt;
        ModelId = dto.ModelId;
        ProviderId = dto.ProviderId;

        _buffer.Append(dto.Content);
        _content = dto.Content;
        _status = dto.Status;
        _errorMessage = dto.ErrorMessage;
        _inputTokens = dto.InputTokens;
        _outputTokens = dto.OutputTokens;
        _generationTimeMs = dto.GenerationTimeMs;
        _isRenderingMarkdown = renderMarkdown;

        Attachments = new ObservableCollection<AttachmentDto>(dto.Attachments);

        if (dto.Content.Length > 0)
        {
            RebuildBlocks();
        }
    }

    public Guid Id { get; }
    public Guid ConversationId { get; }
    public MessageRole Role { get; }
    public DateTimeOffset CreatedAt { get; }
    public string? ModelId { get; private set; }
    public string? ProviderId { get; private set; }

    public ObservableCollection<AttachmentDto> Attachments { get; }

    /// <summary>Rendered markdown. Assistant messages only; user text is shown verbatim.</summary>
    public ObservableCollection<MarkdownBlock> Blocks { get; } = [];

    public bool IsUser => Role == MessageRole.User;
    public bool IsAssistant => Role == MessageRole.Assistant;

    /// <summary>True while tokens are arriving, which drives the caret and the Stop button.</summary>
    public bool IsStreaming => Status == MessageStatus.Streaming;

    public bool HasFailed => Status == MessageStatus.Failed;
    public bool WasCancelled => Status == MessageStatus.Cancelled;
    public bool IsComplete => Status == MessageStatus.Complete;

    /// <summary>An assistant message with nothing in it yet - the "thinking" state.</summary>
    public bool IsAwaitingFirstToken => IsStreaming && _buffer.Length == 0;

    public bool HasUsage => InputTokens is > 0 || OutputTokens is > 0;

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>Appends a streamed chunk. Cheap: no parse, no allocation beyond the buffer.</summary>
    public void AppendDelta(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        _buffer.Append(text);
        Content = _buffer.ToString();
    }

    /// <summary>
    /// Re-parses the buffer and reconciles <see cref="Blocks"/> in place.
    /// Called on a timer during streaming and once when the turn ends.
    /// </summary>
    public void RebuildBlocks()
    {
        if (!IsRenderingMarkdown || !IsAssistant)
        {
            return;
        }

        var parsed = _parser.Parse(_buffer.ToString());
        Reconcile(parsed.Blocks);
    }

    /// <summary>
    /// Replaces only the blocks that actually changed.
    /// </summary>
    /// <remarks>
    /// During streaming, every re-parse produces a block list whose leading entries are
    /// identical to the previous one - only the last block is growing. Comparing content
    /// hashes and touching just the tail means WPF re-renders one paragraph per tick
    /// instead of the entire answer.
    /// </remarks>
    private void Reconcile(IReadOnlyList<MarkdownBlock> parsed)
    {
        var shared = 0;
        var limit = Math.Min(Blocks.Count, parsed.Count);

        while (shared < limit && Blocks[shared].ContentHash == parsed[shared].ContentHash)
        {
            shared++;
        }

        // Everything from the first difference onward is stale.
        while (Blocks.Count > shared)
        {
            Blocks.RemoveAt(Blocks.Count - 1);
        }

        for (var i = shared; i < parsed.Count; i++)
        {
            Blocks.Add(parsed[i]);
        }
    }

    /// <summary>Applies the terminal state of a turn.</summary>
    public void Complete(int? inputTokens, int? outputTokens, int generationTimeMs)
    {
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        GenerationTimeMs = generationTimeMs;
        Status = MessageStatus.Complete;

        RebuildBlocks();
    }

    public void Fail(string userMessage, string? technicalDetails, bool isRetryable)
    {
        ErrorMessage = userMessage;
        ErrorTechnicalDetails = technicalDetails;
        IsRetryable = isRetryable;
        Status = MessageStatus.Failed;

        RebuildBlocks();
    }

    public void Cancel()
    {
        Status = MessageStatus.Cancelled;
        RebuildBlocks();
    }

    /// <summary>Replaces the content wholesale, after an edit.</summary>
    public void ReplaceContent(string content)
    {
        _buffer.Clear();
        _buffer.Append(content);
        Content = content;

        RebuildBlocks();
    }

    /// <summary>Re-renders when the user turns markdown rendering on or off in Settings.</summary>
    public void SetMarkdownRendering(bool enabled)
    {
        if (IsRenderingMarkdown == enabled)
        {
            return;
        }

        IsRenderingMarkdown = enabled;

        if (enabled)
        {
            RebuildBlocks();
        }
        else
        {
            Blocks.Clear();
        }
    }

    // Status drives most of the template, so every derived flag has to be announced with it.
    partial void OnStatusChanged(MessageStatus value)
    {
        OnPropertyChanged(nameof(IsStreaming));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(WasCancelled));
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(IsAwaitingFirstToken));
    }

    partial void OnContentChanged(string value) => OnPropertyChanged(nameof(IsAwaitingFirstToken));

    partial void OnInputTokensChanged(int? value) => OnPropertyChanged(nameof(HasUsage));

    partial void OnOutputTokensChanged(int? value) => OnPropertyChanged(nameof(HasUsage));
}
