namespace AIClient.Application.DTOs;

/// <summary>
/// What a planning run produces: what it intends to do, and what the finished thing is made of.
/// </summary>
/// <remarks>
/// <para>
/// Structured rather than prose, and that is the whole point of the mode. A plan written as a
/// paragraph can be read by a person and by nothing else; a plan with steps and parts can be drawn on
/// a canvas, handed to a build run, or checked off. The model is perfectly capable of writing the
/// paragraph as well, and is asked to - but afterwards, in its own answer, from this.
/// </para>
/// <para>
/// Every list is optional and empty by default. A model that fills in half of this has still said
/// something useful, and refusing a plan for a missing field would spend a step of the budget arguing
/// about shape.
/// </para>
/// </remarks>
public sealed record AgentPlan
{
    /// <summary>One line naming the whole plan, as a heading.</summary>
    public required string Title { get; init; }

    /// <summary>What the plan is trying to achieve, in a sentence or two.</summary>
    public string? Goal { get; init; }

    /// <summary>The work, in the order it should happen.</summary>
    public IReadOnlyList<AgentPlanStep> Steps { get; init; } = [];

    /// <summary>
    /// The pieces the finished project is made of. What a canvas draws.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="Steps"/> because they answer different questions and a plan needs
    /// both: the steps are the order of the work, the parts are the shape of the result. One step can
    /// touch four parts, and one part can take four steps to finish.
    /// </remarks>
    public IReadOnlyList<AgentPlanPart> Parts { get; init; } = [];

    /// <summary>
    /// What could go wrong, or what the plan is guessing at.
    /// </summary>
    /// <remarks>
    /// Asked for explicitly because a model that is not asked will not volunteer it, and the one thing
    /// a person reviewing a plan most needs is the list of places it might be wrong.
    /// </remarks>
    public IReadOnlyList<string> Risks { get; init; } = [];
}

/// <summary>One unit of the work, as the plan intends to do it.</summary>
public sealed record AgentPlanStep
{
    /// <summary>What happens, as an instruction: "Add the settings section", not "Settings".</summary>
    public required string Title { get; init; }

    /// <summary>Why, or how - whatever the title left out.</summary>
    public string? Detail { get; init; }

    /// <summary>The files this step touches, relative to the project folder.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];
}

/// <summary>
/// One piece of the finished project, and what it leans on.
/// </summary>
/// <remarks>
/// <see cref="DependsOn"/> holds <see cref="Name"/>s rather than ids, because a model writing a plan
/// invents both and only remembers one of them. Names that match nothing are kept as written rather
/// than dropped: a dangling dependency is a thing worth seeing on a canvas, and silently discarding it
/// would hide a plan that has not been thought through.
/// </remarks>
public sealed record AgentPlanPart
{
    public required string Name { get; init; }

    public AgentPlanPartKind Kind { get; init; } = AgentPlanPartKind.Other;

    /// <summary>Where it will live, when the part is something with a path.</summary>
    public string? Path { get; init; }

    /// <summary>What it is for, in one line.</summary>
    public string? Purpose { get; init; }

    /// <summary>The names of the parts this one needs.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];
}

/// <summary>
/// Roughly what a part is, to the extent that changes how it should be drawn.
/// </summary>
/// <remarks>
/// Coarse on purpose. This exists so a canvas can pick a shape and a colour, not to classify software:
/// a finer list would only give a model more ways to disagree with itself about which one a thing is.
/// </remarks>
public enum AgentPlanPartKind
{
    /// <summary>Anything the list below does not cover, and the answer when the model said nothing.</summary>
    Other,

    Folder,
    File,

    /// <summary>A unit of code with a name: a class, a project, a package.</summary>
    Module,

    /// <summary>Something long-lived that other parts call.</summary>
    Service,

    /// <summary>A contract rather than an implementation.</summary>
    Interface,

    /// <summary>A table, an entity, a schema, a file format.</summary>
    Data,

    /// <summary>A screen, a page, a component the user sees.</summary>
    View,

    Test,

    /// <summary>Something the project depends on but does not contain.</summary>
    External,
}

/// <summary>Reads the kind a model wrote, which is rarely one of the words on the list.</summary>
public static class AgentPlanPartKinds
{
    /// <summary>
    /// Maps whatever was written to the nearest kind, and to <see cref="AgentPlanPartKind.Other"/>
    /// when there is no near one.
    /// </summary>
    /// <remarks>
    /// The synonyms are the words models actually use. Refusing "class" because the enum says "module"
    /// would cost a step of the budget and teach the model nothing worth knowing, and the consequence
    /// of guessing wrong here is a node drawn in the wrong colour.
    /// </remarks>
    public static AgentPlanPartKind Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return AgentPlanPartKind.Other;
        }

        var word = raw.Trim().ToLowerInvariant();

        return word switch
        {
            "folder" or "directory" or "dir" or "namespace" or "package" => AgentPlanPartKind.Folder,
            "file" or "document" or "script" or "config" or "configuration" => AgentPlanPartKind.File,
            "module" or "class" or "project" or "assembly" or "library" or "component" or "record"
                or "struct" or "enum" => AgentPlanPartKind.Module,
            "service" or "handler" or "worker" or "job" or "api" or "endpoint" or "controller"
                or "repository" => AgentPlanPartKind.Service,
            "interface" or "contract" or "abstraction" or "protocol" => AgentPlanPartKind.Interface,
            "data" or "entity" or "model" or "table" or "schema" or "database" or "dto"
                or "migration" => AgentPlanPartKind.Data,
            "view" or "screen" or "page" or "window" or "ui" or "control" or "viewmodel"
                => AgentPlanPartKind.View,
            "test" or "tests" or "spec" or "suite" => AgentPlanPartKind.Test,
            "external" or "dependency" or "nuget" or "package_reference" or "third_party"
                => AgentPlanPartKind.External,
            _ => AgentPlanPartKind.Other,
        };
    }
}

/// <summary>What became of a plan that was handed over.</summary>
/// <param name="Drawn">True when it reached a canvas, rather than only the transcript.</param>
/// <param name="Note">
/// One line to pass on to the model, when there is something it should know - that the canvas is not
/// there, for instance, so it does not tell the user to go and look at it.
/// </param>
public sealed record AgentPlanAcceptance(bool Drawn, string? Note = null)
{
    public static AgentPlanAcceptance NotDrawn(string? note = null) => new(false, note);

    public static AgentPlanAcceptance DrawnOn(string note) => new(true, note);
}
