using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIClient.Avalonia.ViewModels;

/// <summary>
/// The command palette's filter and selection.
/// </summary>
/// <remarks>
/// Deliberately dependency-free: the shell hands it commands, it filters and selects, and it
/// raises <see cref="CommandInvoked"/> for the shell to run. Plain contains-matching keeps
/// the behaviour predictable; a fuzzy matcher is a polish-pass decision, not a dependency.
/// </remarks>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private IReadOnlyList<ShellCommand> _commands = [];

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private int _selectedIndex;

    public ObservableCollection<ShellCommand> Filtered { get; } = [];

    /// <summary>Raised for the command the user accepted. The shell runs it.</summary>
    public event EventHandler<ShellCommand>? CommandInvoked;

    /// <summary>Replaces the command list and re-runs the filter.</summary>
    public void SetCommands(IReadOnlyList<ShellCommand> commands)
    {
        _commands = commands ?? [];
        Refilter();
    }

    /// <summary>Opens the palette empty, showing everything, with nothing preselected.</summary>
    public void Reset()
    {
        Query = string.Empty;
        SelectedIndex = 0;
        Refilter();
    }

    partial void OnQueryChanged(string value) => Refilter();

    public void MoveSelection(int delta)
    {
        if (Filtered.Count == 0)
        {
            return;
        }

        SelectedIndex = Math.Clamp(SelectedIndex + delta, 0, Filtered.Count - 1);
    }

    /// <summary>Accepts the current selection, or the only result when one was typed to it.</summary>
    public void ExecuteSelected()
    {
        if (Filtered.Count == 0)
        {
            return;
        }

        var index = Math.Clamp(SelectedIndex, 0, Filtered.Count - 1);
        CommandInvoked?.Invoke(this, Filtered[index]);
    }

    private void Refilter()
    {
        Filtered.Clear();

        foreach (var command in _commands)
        {
            if (Matches(command))
            {
                Filtered.Add(command);
            }
        }

        if (SelectedIndex >= Filtered.Count)
        {
            SelectedIndex = Math.Max(0, Filtered.Count - 1);
        }

        OnPropertyChanged(nameof(Filtered));
    }

    private bool Matches(ShellCommand command)
    {
        if (string.IsNullOrWhiteSpace(Query))
        {
            return true;
        }

        return command.Title.Contains(Query, StringComparison.OrdinalIgnoreCase) ||
               command.Category.Contains(Query, StringComparison.OrdinalIgnoreCase) ||
               command.Keywords.Contains(Query, StringComparison.OrdinalIgnoreCase);
    }
}
