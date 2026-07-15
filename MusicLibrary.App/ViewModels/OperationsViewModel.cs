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
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "Scan configured roots or choose a device/folder to discover recovery operations.";

    public ObservableCollection<OperationRunViewModel> Runs { get; } = [];

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
