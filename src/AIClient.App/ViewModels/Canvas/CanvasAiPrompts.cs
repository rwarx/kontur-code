namespace AIClient.App.ViewModels.Canvas;

/// <summary>
/// The five questions the canvas and the inspector can ask on a person's behalf.
/// </summary>
/// <remarks>
/// Shared because both surfaces offer the same actions on the same selection, and two copies of
/// these sentences would drift apart. They are ordinary English questions rather than a prompt
/// template: the graph context is attached separately by <c>IGraphContextSource</c> during the
/// normal context build, and the model receives them through the existing chat path unchanged.
/// </remarks>
internal static class CanvasAiPrompts
{
    /// <summary>
    /// The question behind an action name, or an empty string for "Ask" - where the person types
    /// their own and the selection simply rides along.
    /// </summary>
    public static string For(string? action) => action switch
    {
        "explain" =>
            "Explain this part of the project: what each item is responsible for, and how they fit together.",
        "analyze" =>
            "Analyse this part of the project: responsibilities, coupling, and anything that looks out of place.",
        "refactor" =>
            "Suggest a refactoring for this part of the project. Describe what you would change and why before changing anything.",
        "problems" =>
            "Find the likely problems in this part of the project: bugs, missing error handling, and risky assumptions.",
        "tests" =>
            "Propose tests for this part of the project, and say what each test would prove.",

        _ => string.Empty,
    };
}
