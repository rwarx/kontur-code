namespace AIClient.Domain.Entities;

/// <summary>
/// A named piece of work that several conversations belong to.
/// </summary>
/// <remarks>
/// <para>
/// The sidebar's second axis. A flat list of chats answers "what did I say recently"; it does not
/// answer "what have I been doing about the scheduler", which is the question anyone with more than
/// a week of history actually has. A project is the folder that answers it.
/// </para>
/// <para>
/// Deliberately thin. It owns a name, an optional folder on disk, and nothing else - no settings, no
/// model, no system prompt. Those live on the conversation because they are properties of a
/// conversation, and duplicating them here would create two places to look and one of them wrong.
/// </para>
/// </remarks>
public sealed class Project
{
    /// <summary>UUIDv7, matching every other key in the schema.</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string Name { get; set; }

    /// <summary>One line about the project, shown as the folder's subtitle. Optional.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// The folder this project is about, when it has one.
    /// </summary>
    /// <remarks>
    /// A hint for the agent's workspace prompt, not an authority over it. Nothing here widens what
    /// the agent may touch: the workspace is still chosen and enforced by
    /// <c>IWorkspaceService</c>, and a path recorded on a project that was never nominated as a
    /// workspace grants no access at all.
    /// </remarks>
    public string? WorkspacePath { get; set; }

    /// <summary>
    /// Accent name from the shared palette, so a project is recognisable before it is read.
    /// </summary>
    public string? Accent { get; set; }

    /// <summary>Whether the folder is open in the sidebar. Remembered because collapsing it is a decision.</summary>
    public bool IsExpanded { get; set; } = true;

    /// <summary>Hand-ordered position. Ties are broken by name, so a fresh database still reads sensibly.</summary>
    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Conversation> Conversations { get; set; } = [];
}
