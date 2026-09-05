using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using AIClient.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIClient.App.ViewModels;

/// <summary>Actions reachable from the command palette.</summary>
public enum PaletteCommand
{
    NewChat,
    SearchChats,
    ChangeModel,
    OpenCanvas,
    OpenSettings,
    ToggleTheme,
    ExportMarkdown,
    ExportJson,
    ExportText,
}

/// <summary>
/// Ctrl+Shift+P: a filterable list of the app's actions.
/// </summary>
/// <remarks>
/// The palette raises an enum rather than executing anything itself. That keeps it a pure
/// menu - it has no dependency on the chat pane, the theme service or settings, and a new
/// entry is one row in <see cref="BuildEntries"/> plus one case in the shell.
/// </remarks>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private readonly ObservableCollection<PaletteEntry> _entries;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private PaletteEntry? _selectedEntry;

    public CommandPaletteViewModel()
    {
        _entries = new ObservableCollection<PaletteEntry>(BuildEntries());

        Entries = CollectionViewSource.GetDefaultView(_entries);
        Entries.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PaletteEntry.Category)));
        Entries.Filter = Matches;

        SelectedEntry = _entries.FirstOrDefault();
    }

    public ICollectionView Entries { get; }

    /// <summary>True when the filter has excluded everything, so the panel can say so.</summary>
    public bool HasNoMatches => Entries.IsEmpty;

    public event EventHandler<PaletteCommand>? CommandInvoked;

    /// <summary>Clears the filter so the palette opens in a known state each time.</summary>
    public void Reset()
    {
        Query = string.Empty;
        SelectedEntry = _entries.FirstOrDefault();
    }

    [RelayCommand]
    private void Invoke(PaletteEntry? entry)
    {
        var target = entry ?? SelectedEntry;

        if (target is not null)
        {
            CommandInvoked?.Invoke(this, target.Command);
        }
    }

    /// <summary>Moves the highlight with the arrow keys while focus stays in the search box.</summary>
    public void MoveSelection(int delta)
    {
        var visible = Entries.Cast<PaletteEntry>().ToList();

        if (visible.Count == 0)
        {
            return;
        }

        var index = SelectedEntry is null ? -1 : visible.IndexOf(SelectedEntry);
        var next = Math.Clamp(index + delta, 0, visible.Count - 1);

        SelectedEntry = visible[next];
    }

    private bool Matches(object item)
    {
        if (item is not PaletteEntry entry)
        {
            return false;
        }

        var query = Query.Trim();

        if (query.Length == 0)
        {
            return true;
        }

        return entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (entry.Keywords?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private IEnumerable<PaletteEntry> BuildEntries() =>
    [
        new(PaletteCommand.NewChat, Localization.T("S.Palette.NewChat"), Localization.T("S.Palette.Category.Chat"), "Ctrl+N", "create start"),
        new(PaletteCommand.SearchChats, Localization.T("S.Palette.SearchChats"), Localization.T("S.Palette.Category.Chat"), "Ctrl+K", "find filter"),
        new(PaletteCommand.ChangeModel, Localization.T("S.Palette.ChangeModel"), Localization.T("S.Palette.Category.Chat"), null, "provider switch llm"),
        new(PaletteCommand.OpenCanvas, Localization.T("S.Palette.OpenCanvas"), Localization.T("S.Palette.Category.Application"), "Ctrl+G", "graph knowledge nodes map spatial"),
        new(PaletteCommand.OpenSettings, Localization.T("S.Palette.OpenSettings"), Localization.T("S.Palette.Category.Application"), "Ctrl+,", "preferences options"),
        new(PaletteCommand.ToggleTheme, Localization.T("S.Palette.ToggleTheme"), Localization.T("S.Palette.Category.Application"), null, "dark light appearance"),
        new(PaletteCommand.ExportMarkdown, Localization.T("S.Palette.ExportMarkdown"), Localization.T("S.Palette.Category.Export"), null, "save md"),
        new(PaletteCommand.ExportJson, Localization.T("S.Palette.ExportJson"), Localization.T("S.Palette.Category.Export"), null, "save json"),
        new(PaletteCommand.ExportText, Localization.T("S.Palette.ExportText"), Localization.T("S.Palette.Category.Export"), null, "save txt plain"),
    ];

    partial void OnQueryChanged(string value)
    {
        Entries.Refresh();
        OnPropertyChanged(nameof(HasNoMatches));

        // Keeps a highlighted row under the cursor as the list narrows, so Enter always
        // does something predictable.
        if (SelectedEntry is null || !Entries.Cast<PaletteEntry>().Contains(SelectedEntry))
        {
            SelectedEntry = Entries.Cast<PaletteEntry>().FirstOrDefault();
        }
    }

    public void OnLanguageChanged()
    {
        _entries.Clear();
        foreach (var entry in BuildEntries())
        {
            _entries.Add(entry);
        }

        Entries.Refresh();
        OnPropertyChanged(nameof(HasNoMatches));
    }
}

/// <summary>One row in the palette.</summary>
/// <param name="Keywords">Extra search terms that are not shown, so "dark" finds Toggle Theme.</param>
public sealed record PaletteEntry(
    PaletteCommand Command,
    string Title,
    string Category,
    string? Shortcut,
    string? Keywords);
