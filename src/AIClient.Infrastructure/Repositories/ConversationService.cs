using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Domain.Entities;
using AIClient.Domain.Enums;
using AIClient.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AIClient.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of conversation and message persistence.
/// </summary>
/// <remarks>
/// Every method opens its own short-lived context from the factory. That is what makes
/// concurrent use safe: a streaming turn writes from a background task while the sidebar
/// reads on the UI thread, and a shared context would fault under exactly that pattern.
/// All reads are projected to DTOs with <c>AsNoTracking</c>, so no entity graph escapes
/// this class and no ViewModel can accidentally hold a live database object.
/// </remarks>
public sealed class ConversationService : IConversationService
{
    /// <summary>Characters of the last message shown as the sidebar preview.</summary>
    private const int PreviewLength = 100;

    private readonly IDbContextFactory<AIClientDbContext> _contextFactory;
    private readonly ITitleGenerator _titleGenerator;
    private readonly ILogger<ConversationService> _logger;

    public ConversationService(
        IDbContextFactory<AIClientDbContext> contextFactory,
        ITitleGenerator titleGenerator,
        ILogger<ConversationService> logger)
    {
        _contextFactory = contextFactory;
        _titleGenerator = titleGenerator;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(
        int skip = 0,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Message bodies are never selected here: the sidebar needs a preview, and pulling
        // whole conversations to build one is what makes naive implementations slow.
        var summaries = await db.Conversations
            .AsNoTracking()
            .OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.UpdatedAt)
            .Skip(skip)
            .Take(take)
            .Select(c => new ConversationSummary
            {
                Id = c.Id,
                Title = c.Title,
                ProviderId = c.ProviderId,
                ModelId = c.ModelId,
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt,
                IsPinned = c.IsPinned,
                MessageCount = c.Messages.Count,
                ProjectId = c.ProjectId,
                Preview = c.Messages
                    .OrderByDescending(m => m.SequenceNumber)
                    .Select(m => m.Content)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Shortening happens after materialisation: it is string work SQLite cannot express,
        // and the row count here is bounded by `take`.
        return summaries.Select(s => s with { Preview = Shorten(s.Preview) }).ToList();
    }

    public async Task<IReadOnlyList<ConversationSummary>> SearchAsync(
        string query,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return await GetSummariesAsync(0, take, cancellationToken).ConfigureAwait(false);
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var term = query.Trim();

        // EF.Functions.Like maps to SQL LIKE, which SQLite evaluates case-insensitively for
        // ASCII. Doing this in SQL rather than in memory keeps search usable with a large history.
        var pattern = $"%{Escape(term)}%";

        var matches = await db.Conversations
            .AsNoTracking()
            .Where(c =>
                EF.Functions.Like(c.Title, pattern, "\\") ||
                c.Messages.Any(m => EF.Functions.Like(m.Content, pattern, "\\")))
            .OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.UpdatedAt)
            .Take(take)
            .Select(c => new ConversationSummary
            {
                Id = c.Id,
                Title = c.Title,
                ProviderId = c.ProviderId,
                ModelId = c.ModelId,
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt,
                IsPinned = c.IsPinned,
                MessageCount = c.Messages.Count,
                ProjectId = c.ProjectId,
                Preview = c.Messages
                    .Where(m => EF.Functions.Like(m.Content, pattern, "\\"))
                    .Select(m => m.Content)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return matches.Select(s => s with { Preview = Shorten(s.Preview) }).ToList();
    }

    public async Task<ConversationDetail?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var conversation = await db.Conversations
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new
            {
                c.Id,
                c.Title,
                c.IsTitleUserDefined,
                c.ProviderId,
                c.ModelId,
                c.SystemPrompt,
                c.CreatedAt,
                c.UpdatedAt,
                c.ProjectId,
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (conversation is null)
        {
            return null;
        }

        var messages = await db.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == id)
            .OrderBy(m => m.SequenceNumber)
            .ThenBy(m => m.CreatedAt)
            .Select(m => new MessageDto
            {
                Id = m.Id,
                ConversationId = m.ConversationId,
                Role = m.Role,
                Content = m.Content,
                Status = m.Status,
                ErrorMessage = m.ErrorMessage,
                ErrorKind = m.ErrorKind,
                SequenceNumber = m.SequenceNumber,
                CreatedAt = m.CreatedAt,
                ProviderId = m.ProviderId,
                ModelId = m.ModelId,
                InputTokens = m.InputTokens,
                OutputTokens = m.OutputTokens,
                ReasoningTokens = m.ReasoningTokens,
                CacheReadTokens = m.CacheReadTokens,
                CacheWriteTokens = m.CacheWriteTokens,
                GenerationTimeMs = m.GenerationTimeMs,
                IsContextSummary = m.IsContextSummary,
                IsCompacted = m.IsCompacted,
                ToolCallsJson = m.ToolCallsJson,
                ToolCallId = m.ToolCallId,
                ToolName = m.ToolName,
                ToolSucceeded = m.ToolSucceeded,
                Attachments = m.Attachments
                    .Select(a => new AttachmentDto
                    {
                        Id = a.Id,
                        FileName = a.FileName,
                        MimeType = a.MimeType,
                        Size = a.Size,
                        IsTruncated = a.IsTruncated,
                        TextContent = a.TextContent,
                    })
                    .ToList(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ConversationDetail
        {
            Id = conversation.Id,
            Title = conversation.Title,
            IsTitleUserDefined = conversation.IsTitleUserDefined,
            ProviderId = conversation.ProviderId,
            ModelId = conversation.ModelId,
            SystemPrompt = conversation.SystemPrompt,
            CreatedAt = conversation.CreatedAt,
            UpdatedAt = conversation.UpdatedAt,
            ProjectId = conversation.ProjectId,
            Messages = messages,
        };
    }

    public async Task<ConversationSummary> CreateAsync(
        string? title = null,
        string? providerId = null,
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var conversation = new Conversation
        {
            Title = string.IsNullOrWhiteSpace(title) ? "New Chat" : title.Trim(),
            IsTitleUserDefined = !string.IsNullOrWhiteSpace(title),
            ProviderId = providerId,
            ModelId = modelId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Created conversation {ConversationId}.", conversation.Id);

        return new ConversationSummary
        {
            Id = conversation.Id,
            Title = conversation.Title,
            ProviderId = conversation.ProviderId,
            ModelId = conversation.ModelId,
            CreatedAt = conversation.CreatedAt,
            UpdatedAt = conversation.UpdatedAt,
            IsPinned = false,
            MessageCount = 0,
        };
    }

    public async Task RenameAsync(Guid id, string title, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // ExecuteUpdate issues a single UPDATE without materialising the entity.
        await db.Conversations
            .Where(c => c.Id == id)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(c => c.Title, title.Trim())
                    // An explicit rename must survive auto-titling on the next message.
                    .SetProperty(c => c.IsTitleUserDefined, true),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Messages and attachments follow through the cascade configured in the model.
        var deleted = await db.Conversations
            .Where(c => c.Id == id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (deleted > 0)
        {
            _logger.LogInformation("Deleted conversation {ConversationId}.", id);
        }
    }

    public async Task SetPinnedAsync(Guid id, bool isPinned, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        await db.Conversations
            .Where(c => c.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsPinned, isPinned), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetModelAsync(
        Guid id,
        string providerId,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        await db.Conversations
            .Where(c => c.Id == id)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(c => c.ProviderId, providerId)
                    .SetProperty(c => c.ModelId, modelId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<MessageDto> AddMessageAsync(
        Guid conversationId,
        NewMessage message,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Read the current maximum rather than counting rows: deletions (from Regenerate)
        // make Count an unreliable source of the next ordinal.
        var nextSequence = await db.Messages
            .Where(m => m.ConversationId == conversationId)
            .MaxAsync(m => (int?)m.SequenceNumber, cancellationToken)
            .ConfigureAwait(false) ?? -1;

        var entity = new Message
        {
            ConversationId = conversationId,
            Role = message.Role,
            Content = message.Content,
            Status = message.Status,
            ProviderId = message.ProviderId,
            ModelId = message.ModelId,
            SequenceNumber = nextSequence + 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ToolCallsJson = message.ToolCallsJson,
            ToolCallId = message.ToolCallId,
            ToolName = message.ToolName,
            ToolSucceeded = message.ToolSucceeded,
            IsContextSummary = message.IsContextSummary,
        };

        foreach (var attachment in message.Attachments)
        {
            entity.Attachments.Add(new Attachment
            {
                MessageId = entity.Id,
                FileName = attachment.FileName,
                MimeType = attachment.MimeType,
                Size = attachment.Size,
                TextContent = attachment.TextContent,
                StoredPath = attachment.StoredPath,
                IsTruncated = attachment.IsTruncated,
            });
        }

        db.Messages.Add(entity);

        await db.Conversations
            .Where(c => c.Id == conversationId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(c => c.UpdatedAt, DateTimeOffset.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToDto(entity);
    }

    public async Task UpdateMessageAsync(MessageUpdate update, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var message = await db.Messages
            .FirstOrDefaultAsync(m => m.Id == update.MessageId, cancellationToken)
            .ConfigureAwait(false);

        if (message is null)
        {
            // The conversation was deleted while a turn was still streaming. Nothing to do.
            _logger.LogDebug("Skipped an update for message {MessageId}, which no longer exists.", update.MessageId);
            return;
        }

        // Null means "leave alone", which is what lets the periodic streaming flush send
        // only the content without restating status and token counts.
        if (update.Content is not null)
        {
            message.Content = update.Content;
        }

        if (update.Status is { } status)
        {
            message.Status = status;
        }

        if (update.ErrorMessage is not null)
        {
            message.ErrorMessage = update.ErrorMessage;
        }

        if (update.ErrorKind is { } errorKind)
        {
            message.ErrorKind = errorKind;
        }

        if (update.InputTokens is { } inputTokens)
        {
            message.InputTokens = inputTokens;
        }

        if (update.OutputTokens is { } outputTokens)
        {
            message.OutputTokens = outputTokens;
        }

        if (update.ReasoningTokens is { } reasoningTokens)
        {
            message.ReasoningTokens = reasoningTokens;
        }

        if (update.CacheReadTokens is { } cacheReadTokens)
        {
            message.CacheReadTokens = cacheReadTokens;
        }

        if (update.CacheWriteTokens is { } cacheWriteTokens)
        {
            message.CacheWriteTokens = cacheWriteTokens;
        }

        if (update.GenerationTimeMs is { } generationTime)
        {
            message.GenerationTimeMs = generationTime;
        }

        if (update.ToolCallsJson is not null)
        {
            message.ToolCallsJson = update.ToolCallsJson;
        }

        // A successful update clears a stale error from a previous failed attempt.
        if (update.Status == MessageStatus.Complete)
        {
            message.ErrorMessage = null;
            message.ErrorKind = null;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteMessageAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        await db.Messages
            .Where(m => m.Id == messageId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeleteFromMessageAsync(
        Guid messageId,
        bool inclusive,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var anchor = await db.Messages
            .AsNoTracking()
            .Where(m => m.Id == messageId)
            .Select(m => new { m.ConversationId, m.SequenceNumber })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (anchor is null)
        {
            return;
        }

        var threshold = inclusive ? anchor.SequenceNumber : anchor.SequenceNumber + 1;

        var deleted = await db.Messages
            .Where(m => m.ConversationId == anchor.ConversationId && m.SequenceNumber >= threshold)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Removed {Count} message(s) from conversation {ConversationId} at sequence {Sequence}.",
            deleted, anchor.ConversationId, threshold);
    }

    public async Task<string?> TryApplyAutoTitleAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var conversation = await db.Conversations
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken)
            .ConfigureAwait(false);

        // A user-chosen title is never overwritten.
        if (conversation is null || conversation.IsTitleUserDefined)
        {
            return null;
        }

        var firstUserMessage = await db.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.Role == MessageRole.User)
            .OrderBy(m => m.SequenceNumber)
            .Select(m => m.Content)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(firstUserMessage))
        {
            return null;
        }

        var title = await _titleGenerator.GenerateAsync(firstUserMessage, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(title) || title == conversation.Title)
        {
            return null;
        }

        conversation.Title = title;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return title;
    }

    public async Task<int> MarkCompactedAsync(
        IReadOnlyCollection<Guid> messageIds,
        CancellationToken cancellationToken = default)
    {
        if (messageIds.Count == 0)
        {
            return 0;
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var ids = messageIds as IList<Guid> ?? [.. messageIds];

        // The `!IsCompacted` term is what makes the return value meaningful: compacting twice
        // over an overlapping range reports only the rows this pass actually folded away.
        var changed = await db.Messages
            .Where(m => ids.Contains(m.Id) && !m.IsCompacted)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsCompacted, true), cancellationToken)
            .ConfigureAwait(false);

        if (changed > 0)
        {
            _logger.LogInformation("Marked {Count} message(s) as compacted.", changed);
        }

        return changed;
    }

    public async Task<IReadOnlyList<ProjectSummary>> GetProjectsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // LastActivityAt comes from a correlated "newest child" subquery rather than an
        // aggregate, the same shape the sidebar preview above already uses. An empty project
        // has no child to read, so the projection yields default and is fixed up below.
        var projects = await db.Projects
            .AsNoTracking()
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Name)
            .Select(p => new ProjectSummary
            {
                Id = p.Id,
                Name = p.Name,
                Description = p.Description,
                WorkspacePath = p.WorkspacePath,
                Accent = p.Accent,
                IsExpanded = p.IsExpanded,
                SortOrder = p.SortOrder,
                CreatedAt = p.CreatedAt,
                UpdatedAt = p.UpdatedAt,
                ConversationCount = p.Conversations.Count,
                LastActivityAt = p.Conversations
                    .OrderByDescending(c => c.UpdatedAt)
                    .Select(c => c.UpdatedAt)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return projects
            .Select(p => p.LastActivityAt == default ? p with { LastActivityAt = p.CreatedAt } : p)
            .ToList();
    }

    public async Task<ProjectSummary> CreateProjectAsync(
        NewProject project,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project.Name);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // New folders go to the top of the list, so the one just created is where the eye is.
        var lowestSortOrder = await db.Projects
            .MinAsync(p => (int?)p.SortOrder, cancellationToken)
            .ConfigureAwait(false) ?? 0;

        var now = DateTimeOffset.UtcNow;
        var entity = new Project
        {
            Name = project.Name.Trim(),
            Description = project.Description,
            WorkspacePath = project.WorkspacePath,
            Accent = project.Accent,
            SortOrder = lowestSortOrder - 1,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Projects.Add(entity);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Created project {ProjectId}.", entity.Id);

        return new ProjectSummary
        {
            Id = entity.Id,
            Name = entity.Name,
            Description = entity.Description,
            WorkspacePath = entity.WorkspacePath,
            Accent = entity.Accent,
            IsExpanded = entity.IsExpanded,
            SortOrder = entity.SortOrder,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
            ConversationCount = 0,
            LastActivityAt = entity.CreatedAt,
        };
    }

    public async Task UpdateProjectAsync(ProjectUpdate update, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var project = await db.Projects
            .FirstOrDefaultAsync(p => p.Id == update.ProjectId, cancellationToken)
            .ConfigureAwait(false);

        if (project is null)
        {
            _logger.LogDebug("Skipped an update for project {ProjectId}, which no longer exists.", update.ProjectId);
            return;
        }

        // Null means "leave alone", so toggling a folder open does not have to restate its name.
        if (!string.IsNullOrWhiteSpace(update.Name))
        {
            project.Name = update.Name.Trim();
        }

        if (update.Description is not null)
        {
            project.Description = update.Description;
        }

        if (update.WorkspacePath is not null)
        {
            project.WorkspacePath = update.WorkspacePath;
        }

        if (update.Accent is not null)
        {
            project.Accent = update.Accent;
        }

        if (update.SortOrder is { } sortOrder)
        {
            project.SortOrder = sortOrder;
        }

        // Expanding a folder is a view preference, not a change to the project, so it is the one
        // field that does not stamp UpdatedAt. Otherwise scrolling the sidebar would rewrite dates.
        if (update.Name is not null ||
            update.Description is not null ||
            update.WorkspacePath is not null ||
            update.Accent is not null ||
            update.SortOrder is not null)
        {
            project.UpdatedAt = DateTimeOffset.UtcNow;
        }

        if (update.IsExpanded is { } isExpanded)
        {
            project.IsExpanded = isExpanded;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Unfiled first, deleted second, rather than relying on the schema's ON DELETE SET NULL.
        // The chats surviving their folder is the whole point of the relationship, and it should
        // not depend on a pragma being on. The two statements are cheap and unambiguous.
        var unfiled = await db.Conversations
            .Where(c => c.ProjectId == projectId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ProjectId, (Guid?)null), cancellationToken)
            .ConfigureAwait(false);

        var deleted = await db.Projects
            .Where(p => p.Id == projectId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (deleted > 0)
        {
            _logger.LogInformation(
                "Deleted project {ProjectId} and unfiled {Count} conversation(s).", projectId, unfiled);
        }
    }

    public async Task SetProjectAsync(
        Guid conversationId,
        Guid? projectId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // UpdatedAt is deliberately left alone: filing a chat is not activity in it, and bumping
        // the date would jump the row to the top of every list the moment it was moved.
        await db.Conversations
            .Where(c => c.Id == conversationId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ProjectId, projectId), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConversationSummary>> GetSummariesByProjectAsync(
        Guid? projectId,
        int take = 200,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Branched rather than written as `c.ProjectId == projectId`: an equality against a null
        // parameter is not the same predicate as IS NULL, and being explicit also lets SQLite use
        // the (ProjectId, UpdatedAt) index for the filed case.
        var scoped = projectId is { } id
            ? db.Conversations.Where(c => c.ProjectId == id)
            : db.Conversations.Where(c => c.ProjectId == null);

        var summaries = await scoped
            .AsNoTracking()
            .OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.UpdatedAt)
            .Take(take)
            .Select(c => new ConversationSummary
            {
                Id = c.Id,
                Title = c.Title,
                ProviderId = c.ProviderId,
                ModelId = c.ModelId,
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt,
                IsPinned = c.IsPinned,
                MessageCount = c.Messages.Count,
                ProjectId = c.ProjectId,
                Preview = c.Messages
                    .OrderByDescending(m => m.SequenceNumber)
                    .Select(m => m.Content)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return summaries.Select(s => s with { Preview = Shorten(s.Preview) }).ToList();
    }


    private static MessageDto ToDto(Message message) => new()
    {
        Id = message.Id,
        ConversationId = message.ConversationId,
        Role = message.Role,
        Content = message.Content,
        Status = message.Status,
        ErrorMessage = message.ErrorMessage,
        ErrorKind = message.ErrorKind,
        SequenceNumber = message.SequenceNumber,
        CreatedAt = message.CreatedAt,
        ProviderId = message.ProviderId,
        ModelId = message.ModelId,
        InputTokens = message.InputTokens,
        OutputTokens = message.OutputTokens,
        ReasoningTokens = message.ReasoningTokens,
        CacheReadTokens = message.CacheReadTokens,
        CacheWriteTokens = message.CacheWriteTokens,
        GenerationTimeMs = message.GenerationTimeMs,
        IsContextSummary = message.IsContextSummary,
        IsCompacted = message.IsCompacted,
        ToolCallsJson = message.ToolCallsJson,
        ToolCallId = message.ToolCallId,
        ToolName = message.ToolName,
        ToolSucceeded = message.ToolSucceeded,
        Attachments = message.Attachments
            .Select(a => new AttachmentDto
            {
                Id = a.Id,
                FileName = a.FileName,
                MimeType = a.MimeType,
                Size = a.Size,
                IsTruncated = a.IsTruncated,
                TextContent = a.TextContent,
            })
            .ToList(),
    };

    /// <summary>Collapses a message to a single-line sidebar preview.</summary>
    private static string? Shorten(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var text = content.AsSpan().Trim();
        var newline = text.IndexOfAny('\n', '\r');
        if (newline > 0)
        {
            text = text[..newline];
        }

        return text.Length <= PreviewLength
            ? text.ToString()
            : string.Concat(text[..PreviewLength].TrimEnd(), "…");
    }

    /// <summary>Escapes LIKE wildcards so a literal <c>%</c> or <c>_</c> in a query is not a wildcard.</summary>
    private static string Escape(string term) => term
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
