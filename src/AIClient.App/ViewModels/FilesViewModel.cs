using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AIClient.App.Controls;
using AIClient.Application.DTOs;
using AIClient.Application.Interfaces;
using AIClient.Domain.Workspace;

namespace AIClient.App.ViewModels;

/// <summary>
/// The workspace's file tree: what the agent may read and edit, shown as the user browses
/// it - lazily, one folder at a time, exactly how <see cref="IWorkspaceService.ListAsync"/>
/// is shaped to serve it.
/// </summary>
/// <remarks>
/// <para>
/// The tree does not mirror the whole workspace eagerly: listing a large repository into a
/// WPF tree is thousands of elements the user will never open. A folder's children are
/// listed when it expands, and a folder shows its spinner while that happens - honest
/// laziness, visible as such.
/// </para>
/// <para>
/// Nodes carry the same kind inference the graph indexer uses, so a file is a Test here
/// and a Test-shaped node on the canvas: one vocabulary across surfaces, and no second
/// classifier to drift out of sync.
/// </para>
/// </remarks>
public sealed partial class FilesViewModel : ObservableObject
{
    private readonly IWorkspaceService _workspace;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasRoot;

    [ObservableProperty]
    private string _rootName = string.Empty;

    [ObservableProperty]
    private string _rootPath = string.Empty;

    [ObservableProperty]
    private string _emptyMessage = "No workspace is open.";

    public ObservableCollection<FileNodeViewModel> Root { get; } = [];

    /// <summary>Raised when a file is chosen: the workspace opens it in the code view.</summary>
    public event EventHandler<string>? FileActivated;

    /// <summary>Raised when a file is selected: the context surface shows its inspector.</summary>
    public event EventHandler<FileNodeViewModel>? SelectedFileChanged;

    public FilesViewModel(IWorkspaceService workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        _workspace = workspace;
        _workspace.RootChanged += OnRootChanged;
        SyncRoot();
    }

    private FileNodeViewModel? _selectedEntry;

    public FileNodeViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetProperty(ref _selectedEntry, value) && value is not null)
            {
                SelectedFileChanged?.Invoke(this, value);
            }
        }
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!_workspace.IsOpen)
        {
            Root.Clear();
            SyncRoot();
            return;
        }

        IsLoading = true;

        try
        {
            var listing = await _workspace.ListAsync(WorkspacePath.Root, recursive: false, cancellationToken)
                .ConfigureAwait(true);

            RebuildRoot(listing);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void OnRootChanged(object? sender, string? root)
    {
        SyncRoot();

        if (root is null)
        {
            Root.Clear();
            return;
        }

        _ = RefreshCommand.ExecuteAsync(null);
    }

    private void SyncRoot()
    {
        HasRoot = _workspace.IsOpen;
        RootPath = _workspace.Root ?? string.Empty;
        RootName = HasRoot && RootPath.Length > 0
            ? System.IO.Path.GetFileName(RootPath.TrimEnd(System.IO.Path.DirectorySeparatorChar))
            : string.Empty;

        EmptyMessage = HasRoot
            ? "This folder is empty (or everything in it is ignored)."
            : "No workspace is open.\nOpen a folder to see it here.";
    }

    private void RebuildRoot(WorkspaceResult<WorkspaceListing> result)
    {
        Root.Clear();

        if (!result.Success || result.Value is null)
        {
            return;
        }

        foreach (var entry in OrderEntries(result.Value.Entries))
        {
            Root.Add(FileNodeViewModel.FromEntry(entry, parentPath: null, this));
        }
    }

    internal Task<WorkspaceResult<WorkspaceListing>> ListDirectoryAsync(string path, CancellationToken cancellationToken)
        => _workspace.ListAsync(WorkspacePath.Parse(path), recursive: false, cancellationToken);

    internal void ActivateFile(FileNodeViewModel node) => FileActivated?.Invoke(this, node.Path);

    /// <summary>Folders first, then files, both alphabetical - the order every file browser has taught since forever.</summary>
    private static IEnumerable<WorkspaceEntry> OrderEntries(IEnumerable<WorkspaceEntry> entries) =>
        entries
            .OrderBy(entry => entry.IsDirectory ? 0 : 1)
            .ThenBy(entry => entry.Path.Name, StringComparer.OrdinalIgnoreCase);

    internal void RaiseSelectedChanged(FileNodeViewModel node) => SelectedFileChanged?.Invoke(this, node);
}

/// <summary>
/// One row of the file tree: a real entry on disk, listed on demand, expanded on demand.
/// </summary>
public sealed partial class FileNodeViewModel : ObservableObject
{
    private readonly FilesViewModel? _owner;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasLoaded;

    private FileNodeViewModel(FilesViewModel? owner, string path, string name, bool isDirectory, IconKind kind, long size)
    {
        _owner = owner;
        Path = path;
        Name = name;
        IsDirectory = isDirectory;
        Kind = kind;
        Size = size;
    }

    public static FileNodeViewModel FromEntry(WorkspaceEntry entry, string? parentPath, FilesViewModel owner)
    {
        return new FileNodeViewModel(
            owner,
            entry.Path.Value,
            entry.Path.Name,
            entry.IsDirectory,
            entry.IsDirectory ? IconKind.Folder : WorkspaceIcons.ForFile(entry.Path.Name),
            entry.Size);
    }

    public string Path { get; }

    public string Name { get; }

    public bool IsDirectory { get; }

    public IconKind Kind { get; }

    public long Size { get; }

    /// <summary>Human-readable size for files; blank for folders, which do not have one.</summary>
    public string DisplaySize => IsDirectory ? string.Empty : FormatSize(Size);

    public ObservableCollection<FileNodeViewModel> Children { get; } = [];

    public bool HasChildren => IsDirectory && !HasLoaded || Children.Count > 0;

    /// <summary>The workspace-relative path, shown small under the name and copied by the inspector.</summary>
    public string RelativePath => Path;

    [RelayCommand]
    private void Open()
    {
        if (!IsDirectory)
        {
            _owner?.ActivateFile(this);
        }
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_hasLoaded && IsDirectory)
        {
            _ = LoadChildrenAsync();
        }
    }

    private async Task LoadChildrenAsync()
    {
        if (_owner is null)
        {
            return;
        }

        IsLoading = true;

        try
        {
            var listing = await _owner.ListDirectoryAsync(Path, CancellationToken.None).ConfigureAwait(true);

            if (listing.Success && listing.Value is not null)
            {
                Children.Clear();

                foreach (var entry in listing.Value.Entries)
                {
                    Children.Add(FromEntry(entry, Path, _owner));
                }
            }
        }
        finally
        {
            IsLoading = false;
            HasLoaded = true;
            OnPropertyChanged(nameof(HasChildren));
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };
}
