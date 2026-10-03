using System.Collections.ObjectModel;
using AIClient.App.Services;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIClient.App.ViewModels;

/// <summary>
/// Runnable recipes and the tool catalogue: the automation hub.
/// </summary>
/// <remarks>
/// <para>
/// A recipe is a prompt preset, not a script: running one writes it into the composer's
/// draft with the matching agent mode selected and switches to Chat, exactly like the
/// workspace's own "ask the AI" path. The user reads what the model will read and sends
/// it themselves - a preset that edited files on one click would be a surprise nobody
/// asked for.
/// </para>
/// <para>
/// The tool list is a live projection of <see cref="IAgentToolRegistry"/>, read-only:
/// there is no backend for enabling or disabling individual tools, so the surface shows
/// what the agent can do (names, descriptions, risk levels) instead of switches that
/// would change nothing.
/// </para>
/// </remarks>
public sealed partial class WorkflowsViewModel : ObservableObject
{
    private readonly ChatViewModel _chat;
    private readonly WorkspaceViewModel _workspace;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isGenerating;

    public IReadOnlyList<WorkflowRecipeRow> Recipes { get; }

    public ObservableCollection<ToolCatalogRow> Tools { get; } = [];

    public WorkflowsViewModel(ChatViewModel chat, WorkspaceViewModel workspace, IAgentToolRegistry tools)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(tools);

        _chat = chat;
        _workspace = workspace;

        Recipes =
        [
            new("fix-test", "S.Workflows.Recipe.FixTest.Name", "S.Workflows.Recipe.FixTest.Description",
                AgentMode.Build,
                "Run the full agent loop to fix the failing test in the open workspace: reproduce it, diagnose the root cause, apply the minimal fix, then verify with build and tests."),
            new("draft-readme", "S.Workflows.Recipe.Readme.Name", "S.Workflows.Recipe.Readme.Description",
                AgentMode.Plan,
                "Draft a README.md for the open workspace: inspect the projects, outline structure, build and test instructions, then review for accuracy."),
            new("refactor-service", "S.Workflows.Recipe.Refactor.Name", "S.Workflows.Recipe.Refactor.Description",
                AgentMode.Build,
                "Refactor the selected service for clarity: map its callers, propose the refactor plan, apply edits with diff approvals, and keep tests green."),
            new("review-changes", "S.Workflows.Recipe.Review.Name", "S.Workflows.Recipe.Review.Description",
                AgentMode.Plan,
                "Review the recent changes in the workspace: read the git status and diff, inspect the touched files, and report findings with severity levels."),
        ];

        foreach (var tool in tools.Tools)
        {
            Tools.Add(new ToolCatalogRow(tool.Name, tool.Description, tool.Risk.ToString()));
        }

        _chat.PropertyChanged += OnChatPropertyChanged;
        IsGenerating = _chat.IsGenerating;
    }

    /// <summary>True while a turn is running; recipes wait rather than queue.</summary>
    public bool IsBusy => IsGenerating;

    /// <summary>Rebuilds the code-computed recipe words after a language switch.</summary>
    public void OnLanguageChanged()
    {
        foreach (var recipe in Recipes)
        {
            recipe.RefreshLanguage();
        }
    }

    /// <summary>
    /// Stages a recipe in the composer with its agent mode and shows Chat.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private void RunRecipe(WorkflowRecipeRow? recipe)
    {
        if (recipe is null || !CanRun())
        {
            return;
        }

        _chat.IsAgentMode = true;
        _chat.SelectedAgentMode = recipe.Mode;
        _chat.Draft = recipe.Prompt;

        _workspace.SwitchModeCommand.Execute(WorkspaceMode.Chat);
        _chat.FocusInput();
    }

    private bool CanRun() => !IsGenerating;

    private void OnChatPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.IsGenerating))
        {
            IsGenerating = _chat.IsGenerating;
            RunRecipeCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>The agent mode's name in the interface language.</summary>
    public static string ModeName(AgentMode mode) => mode switch
    {
        AgentMode.Plan => Localization.T("S.Agent.Mode.Plan"),
        AgentMode.PlanCanvas => Localization.T("S.Agent.Mode.PlanCanvas"),
        AgentMode.Build => Localization.T("S.Agent.Mode.Build"),
        _ => Localization.T("S.Agent.Mode.Off"),
    };
}

/// <summary>One runnable recipe: a named prompt preset bound to an agent mode.</summary>
/// <remarks>
/// Names resolve through the string table in code, like the sidebar's own nav rows:
/// a <c>DynamicResource</c> cannot be bound per item, so the row owns the lookup.
/// </remarks>
/// <param name="Prompt">Model-facing text; stays English like the chat's own suggestions.</param>
public sealed partial class WorkflowRecipeRow : ObservableObject
{
    private readonly string _nameKey;
    private readonly string _descriptionKey;

    public WorkflowRecipeRow(string id, string nameKey, string descriptionKey, AgentMode mode, string prompt)
    {
        Id = id;
        _nameKey = nameKey;
        _descriptionKey = descriptionKey;
        Mode = mode;
        Prompt = prompt;
    }

    public string Id { get; }

    public AgentMode Mode { get; }

    public string Prompt { get; }

    public string Name => Localization.T(_nameKey);

    public string Description => Localization.T(_descriptionKey);

    public string ModeName => WorkflowsViewModel.ModeName(Mode);

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(ModeName));
    }
}

/// <summary>One row of the tool catalogue, projected from the live registry.</summary>
public sealed record ToolCatalogRow(string Name, string Description, string Risk);
