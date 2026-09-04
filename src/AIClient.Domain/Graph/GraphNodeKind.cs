namespace AIClient.Domain.Graph;

/// <summary>
/// What a node is: a file, a class, a requirement, a decision, an execution.
/// </summary>
/// <remarks>
/// <para>
/// Text rather than an enum, deliberately. The domain already has more than twenty kinds and the
/// list is open - anything the user, an agent or a future indexer finds worth naming becomes one -
/// so a new kind must not be a database migration, and it must not be a compile error in every
/// switch that picks an icon. The well-known values below exist so the common ones are
/// discoverable and spelled one way; nothing rejects a kind that is not among them.
/// </para>
/// <para>
/// Where an exhaustive switch genuinely is the point - <see cref="GraphMutation"/>, where a
/// forgotten case would silently drop a change - the type stays a closed record hierarchy instead.
/// </para>
/// </remarks>
public readonly record struct GraphNodeKind
{
    private readonly string? _value;

    private GraphNodeKind(string? value) => _value = value;

    /// <summary>Nothing meaningful was supplied. Equal to <c>default</c>.</summary>
    public static GraphNodeKind Unknown => default;

    // The project itself, and the things a user thinks in.
    public static GraphNodeKind Project { get; } = new("project");
    public static GraphNodeKind Feature { get; } = new("feature");
    public static GraphNodeKind Requirement { get; } = new("requirement");
    public static GraphNodeKind Task { get; } = new("task");
    public static GraphNodeKind Decision { get; } = new("decision");
    public static GraphNodeKind Knowledge { get; } = new("knowledge");
    public static GraphNodeKind Documentation { get; } = new("documentation");

    // Structure on disk.
    public static GraphNodeKind Folder { get; } = new("folder");
    public static GraphNodeKind File { get; } = new("file");

    // Structure in code, filled in by a semantic indexer rather than by the file walk.
    public static GraphNodeKind Class { get; } = new("class");
    public static GraphNodeKind Interface { get; } = new("interface");
    public static GraphNodeKind Method { get; } = new("method");
    public static GraphNodeKind Service { get; } = new("service");
    public static GraphNodeKind Test { get; } = new("test");

    // Architecture, as opposed to code. A component groups things that live in different files.
    public static GraphNodeKind Component { get; } = new("component");
    public static GraphNodeKind Api { get; } = new("api");
    public static GraphNodeKind Database { get; } = new("database");
    public static GraphNodeKind Dependency { get; } = new("dependency");

    // What the agents do and leave behind.
    public static GraphNodeKind Agent { get; } = new("agent");
    public static GraphNodeKind Execution { get; } = new("execution");
    public static GraphNodeKind Artifact { get; } = new("artifact");
    public static GraphNodeKind Error { get; } = new("error");

    /// <summary>Canonical form: lower-case, trimmed. This is what the database stores.</summary>
    public string Value => _value ?? GraphKindText.Unknown;

    public bool IsUnknown => _value is null;

    /// <summary>Accepts any name, canonicalising it so that "File" and " file " are one kind.</summary>
    public static GraphNodeKind From(string? value) => new(GraphKindText.Normalize(value));

    public override string ToString() => Value;
}
