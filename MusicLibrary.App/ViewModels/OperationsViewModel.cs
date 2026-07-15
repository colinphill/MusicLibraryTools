using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.App.Services;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibrary.App.ViewModels;

/// <summary>Read-only discovery surface for mutation journals and quarantine runs.</summary>
public partial class OperationsViewModel : ViewModelBase
{
    private const string SearchRootPreference = "Operations.SearchRoot";
    private readonly IOperationJournalService _journals;
    private readonly IFileDialogService _files;
    private readonly IAppSettings _settings;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private string? _searchRoot;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenRunCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "Scan configured roots or choose a device/folder to discover recovery operations.";

    [ObservableProperty]
    private bool _showBrowser;

    [ObservableProperty]
    private OperationRunViewModel? _selectedRun;

    public ObservableCollection<OperationRunViewModel> Runs { get; } = [];
    public ObservableCollection<OperationEntryNodeViewModel> RootNodes { get; } = [];

    public OperationsViewModel(
        IOperationJournalService journals,
        IFileDialogService files,
        IAppSettings settings)
    {
        _journals = journals;
        _files = files;
        _settings = settings;
        SearchRoot = settings.GetPreference(SearchRootPreference);
    }

    partial void OnSearchRootChanged(string? value) =>
        _settings.SetPreference(SearchRootPreference, string.IsNullOrWhiteSpace(value) ? null : value);

    [RelayCommand]
    private async Task BrowseRootAsync()
    {
        string? path = await _files.PickFolderAsync("Select the source, sync, or device root to scan");
        if (path is not null)
            SearchRoot = path;
    }

    private bool CanRefresh() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        var roots = CollectSearchRoots();
        if (roots.Count == 0)
        {
            StatusText = "No configuration or search root is available. Choose a folder to scan.";
            return;
        }

        IsBusy = true;
        _cts = new CancellationTokenSource();
        StatusText = $"Scanning {roots.Count:N0} root(s) for operation journals…";
        try
        {
            var result = await _journals.DiscoverAsync(roots, _cts.Token);
            Runs.Clear();
            RootNodes.Clear();
            SelectedRun = null;
            ShowBrowser = false;
            foreach (var run in result.Runs)
                Runs.Add(new OperationRunViewModel(run));
            int interrupted = result.Runs.Count(run => run.State == OperationJournalState.Interrupted);
            StatusText = $"Found {result.Runs.Count:N0} operation run(s); {interrupted:N0} interrupted"
                + (result.Warnings.Count == 0 ? "." : $"; {result.Warnings.Count:N0} root(s) could not be scanned.");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Operation discovery cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Operation discovery failed: {ex.Message}";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private bool CanOpenRun(OperationRunViewModel? run) => !IsBusy && run is not null;

    [RelayCommand(CanExecute = nameof(CanOpenRun))]
    private async Task OpenRunAsync(OperationRunViewModel? run)
    {
        if (run is null)
            return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        StatusText = $"Opening {run.ToolName} operation…";
        try
        {
            var browse = await _journals.BrowseAsync(run.Summary, _cts.Token);
            RootNodes.Clear();
            RootNodes.Add(OperationEntryNodeViewModel.Build(browse));
            SelectedRun = run;
            ShowBrowser = true;
            StatusText = $"{browse.Entries.Count:N0} operation item(s) in their original hierarchy"
                + (browse.Warnings.Count == 0 ? "." : $"; {browse.Warnings.Count:N0} item(s) could not be read.");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Opening operation cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open operation: {ex.Message}";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CloseBrowser()
    {
        ShowBrowser = false;
        SelectedRun = null;
        RootNodes.Clear();
        StatusText = $"Showing {Runs.Count:N0} discovered operation run(s).";
    }

    internal IReadOnlyList<string> CollectSearchRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
                roots.Add(path);
        }

        Add(SearchRoot);
        Add(_settings.GetPreference("Ingest.SourceDirectory"));
        var snapshot = _settings.GetSnapshot();
        if (snapshot.ConfigPath is not null)
            Add(Path.GetDirectoryName(snapshot.ConfigPath));
        if (snapshot.Configuration is { } configuration)
        {
            foreach (var location in configuration.IndexLocations)
                Add(location.Target);
            foreach (string key in new[] { "SyncTarget", "PlaylistTarget" })
                foreach (string value in configuration[key])
                    Add(value);
        }
        return roots.ToList();
    }
}

public sealed class OperationRunViewModel
{
    public OperationJournalSummary Summary { get; }
    public string ToolName => Summary.ToolName;
    public string Kind => Summary.Kind.ToString();
    public string State => Summary.State switch
    {
        OperationJournalState.Completed => "Completed",
        OperationJournalState.Interrupted => "Interrupted — recovery may be required",
        OperationJournalState.RolledBack => "Rolled back",
        _ => "Quarantine present — terminal state not recorded",
    };
    public bool IsInterrupted => Summary.State == OperationJournalState.Interrupted;
    public string Created => Summary.CreatedAtUtc.ToLocalTime().ToString("g");
    public string RunPath => Summary.RunPath;
    public string Journal => Summary.JournalPath ?? "No journal (folder-only quarantine)";
    public string AffectedItems => Summary.AffectedItemCount is int count
        ? $"{count:N0} recorded item(s)"
        : "Item count available when the run is opened";

    public OperationRunViewModel(OperationJournalSummary summary) => Summary = summary;
}

public sealed class OperationEntryNodeViewModel
{
    public string Name { get; }
    public string OriginalPath { get; }
    public string? CurrentPath { get; private set; }
    public OperationEntryKind? Kind { get; private set; }
    public bool Exists { get; private set; }
    public bool IsDirectory { get; private set; }
    public List<OperationEntryNodeViewModel> Children { get; } = [];
    public bool HasEntry => Kind is not null;
    public string StateText => Kind switch
    {
        OperationEntryKind.Quarantined => Exists ? "Quarantined" : "Quarantine copy missing",
        OperationEntryKind.Moved => Exists ? "Moved" : "Move destination missing",
        OperationEntryKind.Created => Exists ? "Created" : "Created item missing",
        OperationEntryKind.Deleted => "Deleted",
        OperationEntryKind.Planned => "Planned; completion not recorded",
        OperationEntryKind.Unknown => "Unknown operation",
        _ => "",
    };

    private OperationEntryNodeViewModel(string name, string originalPath)
    {
        Name = name;
        OriginalPath = originalPath;
    }

    public static OperationEntryNodeViewModel Build(OperationBrowseResult browse)
    {
        var root = new OperationEntryNodeViewModel(browse.OriginalRoot, browse.OriginalRoot)
        {
            IsDirectory = true,
        };
        foreach (var entry in browse.Entries)
            root.Add(entry);
        root.SortChildren();
        return root;
    }

    private void Add(OperationFileEntry entry)
    {
        string relative = entry.RelativePath;
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            relative = Path.GetFileName(entry.OriginalPath);
        string[] parts = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            parts = [Path.GetFileName(entry.OriginalPath)];

        var parent = this;
        string path = OriginalPath;
        foreach (string part in parts)
        {
            path = Path.Combine(path, part);
            var child = parent.Children.FirstOrDefault(candidate =>
                StringComparer.OrdinalIgnoreCase.Equals(candidate.Name, part));
            if (child is null)
            {
                child = new OperationEntryNodeViewModel(part, path) { IsDirectory = true };
                parent.Children.Add(child);
            }
            parent = child;
        }
        parent.CurrentPath = entry.CurrentPath;
        parent.Kind = entry.Kind;
        parent.Exists = entry.Exists;
        parent.IsDirectory = entry.IsDirectory;
    }

    private void SortChildren()
    {
        Children.Sort((left, right) =>
        {
            int directories = right.IsDirectory.CompareTo(left.IsDirectory);
            return directories != 0 ? directories :
                StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
        });
        foreach (var child in Children)
            child.SortChildren();
    }
}
