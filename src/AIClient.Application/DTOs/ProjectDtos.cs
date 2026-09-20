namespace AIClient.Application.DTOs;

/// <summary>
/// A project as the sidebar draws it: the folder plus the two numbers that decide whether it
/// is worth opening.
/// </summary>
public sealed record ProjectSummary
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>The folder the project is about, when one was recorded.</summary>
    public string? WorkspacePath { get; init; }

    public string? Accent { get; init; }

    /// <summary>Whether the folder is drawn open. Persisted, because collapsing it is a decision.</summary>
    public bool IsExpanded { get; init; }

    public int SortOrder { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>How many chats are filed here, so an empty folder can say so instead of looking broken.</summary>
    public int ConversationCount { get; init; }

    /// <summary>
    /// When anything in the project last moved, or <see cref="CreatedAt"/> for an empty one.
    /// </summary>
    /// <remarks>
    /// Computed from the conversations rather than stored: a project whose chats are all a month
    /// old is a month old, whatever date renaming it would otherwise have stamped on it.
    /// </remarks>
    public DateTimeOffset LastActivityAt { get; init; }
}

/// <summary>Input for creating a project.</summary>
public sealed record NewProject
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? WorkspacePath { get; init; }
    public string? Accent { get; init; }
}

/// <summary>
/// Partial update of a project. Null leaves the column alone, matching
/// <see cref="MessageUpdate"/>, so toggling a folder open does not have to restate its name.
/// </summary>
public sealed record ProjectUpdate
{
    public required Guid ProjectId { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? WorkspacePath { get; init; }
    public string? Accent { get; init; }
    public bool? IsExpanded { get; init; }
    public int? SortOrder { get; init; }
}
