namespace AIClient.Domain.Graph;

/// <summary>
/// What one node has to do with another.
/// </summary>
/// <remarks>
/// Text for the same reasons as <see cref="GraphNodeKind"/>, and with one extra consequence worth
/// stating: an edge kind is part of an edge's identity. <c>A depends_on B</c> and <c>A calls B</c>
/// are two different facts about the same pair, and both are allowed to exist at once.
///
/// Every edge here is directed. Where a relationship is genuinely symmetric, <see cref="RelatesTo"/>
/// is the one to use, and readers should treat it as unordered.
/// </remarks>
public readonly record struct GraphEdgeKind
{
    private readonly string? _value;

    private GraphEdgeKind(string? value) => _value = value;

    /// <summary>Nothing meaningful was supplied. Equal to <c>default</c>.</summary>
    public static GraphEdgeKind Unknown => default;

    /// <summary>Structural containment: a folder holds a file, a class holds a method.</summary>
    public static GraphEdgeKind Contains { get; } = new("contains");

    /// <summary>
    /// Membership of an architectural grouping, which is not containment: a component groups
    /// things that physically live in different files and folders.
    /// </summary>
    public static GraphEdgeKind Groups { get; } = new("groups");

    public static GraphEdgeKind DependsOn { get; } = new("depends_on");
    public static GraphEdgeKind Implements { get; } = new("implements");
    public static GraphEdgeKind Extends { get; } = new("extends");
    public static GraphEdgeKind Calls { get; } = new("calls");
    public static GraphEdgeKind References { get; } = new("references");
    public static GraphEdgeKind Imports { get; } = new("imports");

    public static GraphEdgeKind TestedBy { get; } = new("tested_by");
    public static GraphEdgeKind Documents { get; } = new("documents");

    /// <summary>A decision or a requirement bearing on something.</summary>
    public static GraphEdgeKind Decides { get; } = new("decides");

    /// <summary>An execution and what it left behind.</summary>
    public static GraphEdgeKind Produces { get; } = new("produces");

    /// <summary>Deliberately vague, for a link a person drew because it was useful.</summary>
    public static GraphEdgeKind RelatesTo { get; } = new("relates_to");

    /// <summary>Canonical form: lower-case, trimmed. This is what the database stores.</summary>
    public string Value => _value ?? GraphKindText.Unknown;

    public bool IsUnknown => _value is null;

    public static GraphEdgeKind From(string? value) => new(GraphKindText.Normalize(value));

    public override string ToString() => Value;
}
