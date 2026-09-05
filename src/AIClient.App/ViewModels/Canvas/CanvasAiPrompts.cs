using AIClient.Domain.Enums;
using AIClient.Domain.Graph;
using AIClient.Domain.Workspace;

namespace AIClient.App.ViewModels.Canvas;

/// <summary>
/// The five questions the canvas and the inspector can ask on a person's behalf, and what each one
/// is asked about.
/// </summary>
/// <remarks>
/// <para>
/// Shared because both surfaces offer the same actions on the same selection, and two copies of
/// these sentences would drift apart. They are ordinary English questions rather than a prompt
/// template: the graph context is attached separately by <c>IGraphContextSource</c> during the
/// normal context build, and the model receives them through the existing chat path unchanged.
/// </para>
/// <para>
/// Every question names its subject. "Explain this part of the project" is answerable by the model -
/// it is given the selection - but unreadable to the person who asked it, and worse a day later when
/// the same conversation holds five of them. The path goes in the sentence itself, so the transcript
/// says what was asked without anyone having to remember what was selected at the time.
/// </para>
/// <para>
/// Not localised, alone in this project. These strings are read by a model rather than by a person,
/// and a question asked in Russian pulls the entire answer into Russian whether that was wanted or
/// not; the language of the reply belongs to the system prompt, not to a toolbar button.
/// </para>
/// </remarks>
internal static class CanvasAiPrompts
{
    /// <summary>
    /// Files a question may bring with it as real attachments.
    /// </summary>
    /// <remarks>
    /// Small on purpose. An attachment is stored with the message and re-sent with every later turn,
    /// so a generous cap here quietly makes the rest of the conversation expensive. Asking about more
    /// than a handful of files is asking about a region, and the graph block describes a region
    /// better than several thousand lines of its source would.
    /// </remarks>
    private const int MaxAttachedFiles = 4;

    /// <summary>How many members of a group are named in the question before it becomes a count.</summary>
    /// <remarks>
    /// Three paths still read as a sentence. Eight read as a list, and a list of everything selected
    /// is what the graph block is for.
    /// </remarks>
    private const int MaxNamedInSubject = 3;

    /// <summary>Used when the selection resolves to nothing that can be named.</summary>
    private const string Unnamed = "the selected part of the project";

    /// <summary>The question a toolbar action asks about <paramref name="subject"/>.</summary>
    public static string For(string? action, string subject)
    {
        var about = string.IsNullOrWhiteSpace(subject) ? Unnamed : subject;

        return action switch
        {
            "explain" => $"Explain {about}. What is each part responsible for, and how do they fit together?",
            "analyze" => $"Analyse {about}. Look at responsibilities, coupling, and anything that looks out of place.",
            "refactor" => $"Suggest a refactoring for {about}. Describe what you would change and why before changing anything.",
            "problems" => $"Find the likely problems in {about}: bugs, missing error handling, and risky assumptions.",
            "tests" => $"Propose tests for {about}, and say what each test would prove.",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// What a selection is about: the phrase to put in the question, and the files to attach to it.
    /// </summary>
    /// <remarks>
    /// One walk for both, because they are one decision. The caller resolves the nodes - the canvas
    /// has them on its cards, the inspector reads them from the snapshot - so nothing here touches
    /// the graph or the disk, and the result is ordered rather than in whatever order a hash set
    /// happened to hold: the same selection has to produce the same sentence twice.
    /// </remarks>
    public static CanvasAiTarget Describe(IReadOnlyList<GraphNode> nodes)
    {
        if (nodes.Count == 0)
        {
            return CanvasAiTarget.None;
        }

        var ordered = nodes
            .OrderBy(PathOf, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.Key, StringComparer.Ordinal)
            .ToList();

        var files = ordered
            .Where(node => node.Kind == GraphNodeKind.File && node.Status == GraphNodeStatus.Active)
            .Select(node => node.Source)
            .OfType<WorkspacePath>()
            .Where(path => !path.IsRoot)
            .DistinctBy(path => path.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CanvasAiTarget(
            Subject(ordered),
            files.Count <= MaxAttachedFiles ? files : []);
    }

    /// <summary>Names the selection: by path when there is one of it, by count when there are many.</summary>
    private static string Subject(IReadOnlyList<GraphNode> nodes)
    {
        if (nodes.Count == 1)
        {
            return Name(nodes[0]);
        }

        var noun = nodes.All(node => node.Kind == GraphNodeKind.File)
            ? "files"
            : nodes.All(node => node.Kind == GraphNodeKind.Folder) ? "folders" : "parts of the project";

        return nodes.Count <= MaxNamedInSubject
            ? $"these {nodes.Count} {noun}: {string.Join(", ", nodes.Select(PathOf))}"
            : $"these {nodes.Count} selected {noun}";
    }

    /// <summary>
    /// One node, as unambiguously as a sentence can name it.
    /// </summary>
    /// <remarks>
    /// The kind is spelled out for everything except a file, where the path already says it. A file
    /// is named by its path and nothing else, which is deliberately the same string the attachment
    /// chip and the graph block use - the question, the chip and the context then read as being about
    /// one thing rather than three.
    /// </remarks>
    private static string Name(GraphNode node) =>
        node.Kind == GraphNodeKind.File ? PathOf(node) : $"{PathOf(node)} ({node.Kind.Value})";

    /// <summary>Where the node lives, falling back to its title when nothing does.</summary>
    private static string PathOf(GraphNode node) =>
        node.Source is { IsRoot: false } source ? source.Value : node.Title;
}

/// <summary>What a question from the canvas or the inspector is about.</summary>
/// <remarks>
/// <see cref="Files"/> is what is worth sending whole. Empty is the ordinary answer for a folder, for
/// a group too large to attach, and for anything the indexer has put no file behind: the selection
/// still travels as a <c>GraphSelection</c>, and the graph block describes what the attachments
/// cannot.
/// </remarks>
internal sealed record CanvasAiTarget(string Subject, IReadOnlyList<WorkspacePath> Files)
{
    public static CanvasAiTarget None { get; } = new(string.Empty, []);
}
