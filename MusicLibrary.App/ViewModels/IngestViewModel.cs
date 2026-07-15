using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.App.Services;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibrary.App.ViewModels;

public sealed record IngestPreset(string Name, string SourceDirectory, string ConfigurationPath);
public enum IngestPreviewFilter { All, Albums, Outputs, Conflicts, Cleanup }

public partial class IngestViewModel : ViewModelBase
{
    private const string SourcePreference = "Ingest.SourceDirectory";
    private const string ConfigPreference = "Ingest.ConfigurationPath";
    private const string PresetsPreference = "Ingest.Presets";
    private const string RecentSourcesPreference = "Ingest.RecentSources";
    private const int RecentSourceLimit = 12;
    private readonly IIngestMusicService _service;
    private readonly IFileDialogService _files;
    private readonly IDialogService _dialogs;
    private readonly IAppSettings _settings;
    private readonly ILibraryService _library;
    private readonly IIngestPreflightService? _preflight;
    private readonly IOperationJournalService? _journals;
    private CancellationTokenSource? _cts;
    private IngestPlan? _plan;
    private bool _applyingPreset;
    private readonly List<IngestFileItemViewModel> _allFiles = [];

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(PreviewCommand)),
     NotifyCanExecuteChangedFor(nameof(PreflightCommand)), NotifyCanExecuteChangedFor(nameof(SavePresetCommand))]
    private string? _sourceDirectory;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(PreviewCommand)), NotifyCanExecuteChangedFor(nameof(EditConfigurationCommand)),
     NotifyCanExecuteChangedFor(nameof(PreflightCommand)), NotifyCanExecuteChangedFor(nameof(SavePresetCommand))]
    private string? _configurationPath;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(PreviewCommand)),
     NotifyCanExecuteChangedFor(nameof(PreflightCommand)), NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _isBusy;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _hasApplicablePreview;
    [ObservableProperty]
    private string _statusText = "Choose an incoming folder and IngestMusic configuration, then Preview.";
    [ObservableProperty]
    private bool _isPreviewing;
    [ObservableProperty]
    private bool _isApplying;
    [ObservableProperty]
    private int _applyProgress;
    [ObservableProperty]
    private int _applyProgressMaximum = 1;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SavePresetCommand))]
    private string? _presetName;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(DeletePresetCommand))]
    private IngestPreset? _selectedPreset;
    [ObservableProperty]
    private string? _selectedRecentSource;
    [ObservableProperty]
    private IngestPreviewFilter _selectedPreviewFilter;
    [ObservableProperty]
    private bool _hasPreviewSummary;
    [ObservableProperty]
    private int _albumCount;
    [ObservableProperty]
    private int _outputCount;
    [ObservableProperty]
    private int _conflictCount;
    [ObservableProperty]
    private int _cleanupCount;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshHistoryCommand))]
    private bool _isHistoryBusy;
    [ObservableProperty]
    private string _historyStatus = "Refresh to discover persistent ingest journals and interrupted runs.";

    public ObservableCollection<IngestFileItemViewModel> Files { get; } = [];
    public ObservableCollection<IngestConflict> Conflicts { get; } = [];
    public ObservableCollection<IngestPreset> Presets { get; } = [];
    public ObservableCollection<string> RecentSources { get; } = [];
    public ObservableCollection<IngestPreflightCheck> PreflightChecks { get; } = [];
    public ObservableCollection<IngestHistoryItemViewModel> History { get; } = [];
    public IReadOnlyList<IngestPreviewFilter> PreviewFilters { get; } = Enum.GetValues<IngestPreviewFilter>();
    public bool HasPreflightChecks => PreflightChecks.Count > 0;
    public bool HasHistory => History.Count > 0;
    public int InterruptedHistoryCount => History.Count(item => item.IsInterrupted);
    public event Action? IngestCompleted;
    public event Action<OperationJournalSummary>? RecoveryRequested;

    public IngestViewModel(IIngestMusicService service, IFileDialogService files, IDialogService dialogs,
        IAppSettings settings, ILibraryService library, IIngestPreflightService? preflight = null,
        IOperationJournalService? journals = null)
    {
        _service = service; _files = files; _dialogs = dialogs; _settings = settings; _library = library;
        _preflight = preflight;
        _journals = journals;
        LoadPresets();
        LoadRecentSources();
        SourceDirectory = settings.GetPreference(SourcePreference);
        ConfigurationPath = settings.GetPreference(ConfigPreference);
    }

    partial void OnSourceDirectoryChanged(string? value)
    {
        _settings.SetPreference(SourcePreference, string.IsNullOrWhiteSpace(value) ? null : value);
        MarkPresetCustomized();
        InvalidatePreview();
    }

    partial void OnConfigurationPathChanged(string? value)
    {
        _settings.SetPreference(ConfigPreference, string.IsNullOrWhiteSpace(value) ? null : value);
        MarkPresetCustomized();
        InvalidatePreview();
    }

    partial void OnSelectedPresetChanged(IngestPreset? value)
    {
        if (value is null || _applyingPreset)
            return;
        _applyingPreset = true;
        try
        {
            PresetName = value.Name;
            SourceDirectory = value.SourceDirectory;
            ConfigurationPath = value.ConfigurationPath;
        }
        finally { _applyingPreset = false; }
        StatusText = $"Loaded ingest preset '{value.Name}'. Run Preflight or Preview.";
    }

    partial void OnSelectedRecentSourceChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            SourceDirectory = value;
    }

    partial void OnSelectedPreviewFilterChanged(IngestPreviewFilter value) => RefilterFiles();

    private void InvalidatePreview()
    {
        if (PreflightChecks.Count > 0)
        {
            PreflightChecks.Clear();
            OnPropertyChanged(nameof(HasPreflightChecks));
        }
        if (_plan is null) return;
        _plan = null;
        HasApplicablePreview = false;
        StatusText = "Inputs changed. Preview again before applying.";
    }

    [RelayCommand]
    private async Task BrowseSourceAsync()
    {
        string? path = await _files.PickFolderAsync("Select incoming music directory");
        if (path is not null)
        {
            SourceDirectory = path;
            AddRecentSource(path);
        }
    }

    [RelayCommand]
    private async Task BrowseConfigurationAsync()
    {
        string? path = await _files.PickOpenFileAsync("Select IngestMusic configuration",
            [new FilePickerFilter("XML configuration", ["*.xml"])]);
        if (path is not null) ConfigurationPath = path;
    }

    [RelayCommand]
    private async Task NewConfigurationAsync()
    {
        string? path = await _dialogs.ShowIngestConfigEditorAsync(null);
        if (path is not null)
        {
            ConfigurationPath = path;
            _settings.SetPreference(ConfigPreference, path);
        }
    }

    private bool CanEditConfiguration() => !string.IsNullOrWhiteSpace(ConfigurationPath) && File.Exists(ConfigurationPath);

    [RelayCommand(CanExecute = nameof(CanEditConfiguration))]
    private async Task EditConfigurationAsync()
    {
        string? path = await _dialogs.ShowIngestConfigEditorAsync(ConfigurationPath);
        if (path is not null)
        {
            ConfigurationPath = path;
            _settings.SetPreference(ConfigPreference, path);
            HasApplicablePreview = false;
            _plan = null;
            StatusText = "Configuration saved. Preview again before applying.";
        }
    }

    private bool CanPreview() => !IsBusy && !string.IsNullOrWhiteSpace(SourceDirectory) && !string.IsNullOrWhiteSpace(ConfigurationPath);

    public void SetDroppedSource(string path)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;
        SourceDirectory = Path.GetFullPath(path);
        AddRecentSource(SourceDirectory);
        StatusText = "Source folder selected from drop. Run Preflight or Preview.";
    }

    private bool CanPreflight() => _preflight is not null && CanPreview();

    [RelayCommand(CanExecute = nameof(CanPreflight))]
    private async Task PreflightAsync()
    {
        if (_preflight is null)
            return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        PreflightChecks.Clear();
        OnPropertyChanged(nameof(HasPreflightChecks));
        try
        {
            StatusText = "Checking ingest configuration and external toolsâ€¦";
            var result = await _preflight.CheckAsync(
                new IngestRequest(SourceDirectory!, ConfigurationPath!), _cts.Token);
            foreach (var check in result.Checks)
                PreflightChecks.Add(check);
            OnPropertyChanged(nameof(HasPreflightChecks));
            StatusText = result.CanProceed
                ? result.WarningCount == 0
                    ? "Preflight passed. The source is ready to scan."
                    : $"Preflight passed with {result.WarningCount:N0} warning(s). Review before previewing."
                : $"Preflight found {result.ErrorCount:N0} blocking error(s).";
        }
        catch (OperationCanceledException) { StatusText = "Preflight cancelled."; }
        catch (Exception ex) { StatusText = $"Preflight failed: {ex.Message}"; }
        finally { FinishBusy(); }
    }

    private bool CanRefreshHistory() => _journals is not null && !IsHistoryBusy;

    [RelayCommand(CanExecute = nameof(CanRefreshHistory))]
    private async Task RefreshHistoryAsync()
    {
        if (_journals is null)
            return;
        var roots = RecentSources.Append(SourceDirectory)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (roots.Count == 0)
        {
            HistoryStatus = "Choose a source folder before refreshing ingest history.";
            return;
        }
        IsHistoryBusy = true;
        try
        {
            HistoryStatus = $"Searching {roots.Count:N0} source root(s) for ingest journalsâ€¦";
            var result = await _journals.DiscoverAsync(roots);
            History.Clear();
            foreach (var run in result.Runs.Where(run => run.Kind == OperationJournalKind.Ingest).Take(50))
                History.Add(new IngestHistoryItemViewModel(run));
            OnPropertyChanged(nameof(HasHistory));
            OnPropertyChanged(nameof(InterruptedHistoryCount));
            HistoryStatus = $"{History.Count:N0} ingest run(s); {InterruptedHistoryCount:N0} interrupted"
                + (result.Warnings.Count == 0 ? "." : $"; {result.Warnings.Count:N0} root warning(s).");
        }
        catch (Exception ex)
        {
            HistoryStatus = $"Ingest history refresh failed: {ex.Message}";
        }
        finally { IsHistoryBusy = false; }
    }

    [RelayCommand]
    private void OpenHistory(IngestHistoryItemViewModel? item)
    {
        if (item is not null)
            RecoveryRequested?.Invoke(item.Summary);
    }

    private bool CanSavePreset() => !string.IsNullOrWhiteSpace(PresetName) &&
        !string.IsNullOrWhiteSpace(SourceDirectory) && !string.IsNullOrWhiteSpace(ConfigurationPath);

    [RelayCommand(CanExecute = nameof(CanSavePreset))]
    private void SavePreset()
    {
        var preset = new IngestPreset(PresetName!.Trim(), Path.GetFullPath(SourceDirectory!),
            Path.GetFullPath(ConfigurationPath!));
        int existing = Presets.Select((item, index) => (item, index))
            .FirstOrDefault(pair => pair.item.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase)).index;
        bool found = Presets.Any(item => item.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase));
        if (found) Presets[existing] = preset;
        else Presets.Add(preset);
        PersistPresets();
        _applyingPreset = true;
        try { SelectedPreset = preset; }
        finally { _applyingPreset = false; }
        StatusText = $"Saved ingest preset '{preset.Name}'.";
    }

    [RelayCommand(CanExecute = nameof(CanDeletePreset))]
    private void DeletePreset()
    {
        if (SelectedPreset is not { } selected)
            return;
        Presets.Remove(selected);
        SelectedPreset = null;
        PersistPresets();
        StatusText = $"Deleted ingest preset '{selected.Name}'.";
    }

    private bool CanDeletePreset() => SelectedPreset is not null;

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewAsync()
    {
        IsBusy = true; IsPreviewing = true; HasApplicablePreview = false; _plan = null;
        _allFiles.Clear(); Files.Clear(); Conflicts.Clear(); HasPreviewSummary = false;
        _cts = new CancellationTokenSource();
        try
        {
            StatusText = "Scanning and planning…";
            var plan = await _service.PreviewAsync(new IngestRequest(SourceDirectory!, ConfigurationPath!), _cts.Token);
            _plan = plan;
            foreach (var file in plan.Files)
            {
                bool cleanup = file.SourceType.Equals("Unsupported/non-audio", StringComparison.OrdinalIgnoreCase);
                _allFiles.Add(new IngestFileItemViewModel(file, isAlbum: !cleanup,
                    isCleanup: cleanup));
            }
            foreach (var output in plan.Albums.SelectMany(album => album.Outputs))
                _allFiles.Add(IngestFileItemViewModel.ForOutput(output));
            foreach (var conflict in plan.Conflicts)
            {
                Conflicts.Add(conflict);
                _allFiles.Add(IngestFileItemViewModel.ForConflict(conflict));
            }
            AlbumCount = plan.Albums.Count;
            OutputCount = plan.Albums.Sum(album => album.Outputs.Count);
            ConflictCount = plan.Conflicts.Count;
            CleanupCount = plan.IgnoredFileSnapshots.Count + plan.SourceDirectories.Count;
            HasPreviewSummary = true;
            RefilterFiles();
            HasApplicablePreview = plan.CanApply;
            _settings.SetPreference(SourcePreference, plan.Request.SourceDirectory);
            _settings.SetPreference(ConfigPreference, plan.Request.ConfigurationPath);
            AddRecentSource(plan.Request.SourceDirectory);
            StatusText = plan.CanApply
                ? plan.Albums.Count == 0
                    ? $"No music albums found. {plan.IgnoredFileSnapshots.Count} non-music files and "
                      + $"{plan.SourceDirectories.Count} source folders are ready for cleanup. Review, then Apply."
                    : $"{plan.Albums.Count} albums, {plan.Files.Count} source files, {plan.RequiredApprovals.Count} derivation approvals. Review, then Apply."
                : plan.Conflicts.Count > 0
                    ? $"Preview has {plan.Conflicts.Count} conflicts and cannot be applied."
                    : "No importable music albums or enabled non-music cleanup items were found.";
        }
        catch (OperationCanceledException) { StatusText = "Preview cancelled."; }
        catch (Exception ex) { StatusText = $"Preview failed: {ex.Message}"; }
        finally { FinishBusy(); }
    }

    private bool CanApply() => !IsBusy && HasApplicablePreview && _plan is not null;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (_plan is null) return;
        IsBusy = true; IsApplying = true; ApplyProgress = 0; ApplyProgressMaximum = 1; _cts = new CancellationTokenSource();
        foreach (var file in _allFiles) file.ResetProgress();
        try
        {
            var decisions = new List<IngestApprovalDecision>();
            foreach (var item in _plan.RequiredApprovals)
            {
                bool approved = await _dialogs.ConfirmCdDerivationAsync(item);
                decisions.Add(new IngestApprovalDecision(item.AlbumKey, approved));
                if (!approved)
                {
                    StatusText = "Derivation declined; the entire run was cancelled and nothing was changed.";
                    return;
                }
            }
            var progress = new Progress<IngestProgress>(p =>
            {
                ApplyProgressMaximum = Math.Max(1, p.TotalItems);
                ApplyProgress = p.CompletedItems;
                StatusText = $"{p.Operation}: {p.Album} ({p.CompletedItems}/{p.TotalItems})";
                if (p.SourcePath is not null && p.FileState is { } state)
                    _allFiles.FirstOrDefault(file => !file.IsConflict &&
                        string.Equals(file.Source, p.SourcePath, StringComparison.OrdinalIgnoreCase))
                        ?.SetProgress(state, p.Operation);
            });
            var result = await _service.ApplyAsync(_plan, decisions, progress, _cts.Token);
            if (!result.Cancelled && result.Albums.Any(a => a.Success) && _library.IsReady)
            {
                // Once ingestion commits files, finish the cache refresh even if the user presses
                // Cancel; otherwise disk and the library view would knowingly diverge.
                StatusText = "Ingestion committed. Re-indexing the library…";
                var indexed = await _library.IndexAsync(ct: CancellationToken.None);
                StatusText = $"Installed {result.Installed} files; {result.Failed} albums failed. "
                    + $"Index: {indexed.Added} added, {indexed.Modified} modified, {indexed.Removed} removed.";
                IngestCompleted?.Invoke();
            }
            else
            {
                StatusText = result.Cancelled ? result.Message ?? "Cancelled."
                    : _plan.Albums.Count == 0
                        ? "Non-music cleanup completed."
                        : $"Installed {result.Installed} files; {result.Failed} albums failed."
                          + (!_library.IsReady && result.Albums.Any(a => a.Success)
                              ? " No library configuration is loaded, so re-indexing was skipped." : "");
                if (!result.Cancelled) IngestCompleted?.Invoke();
            }
            HasApplicablePreview = false; _plan = null;
            if (!result.Cancelled && _journals is not null)
                await RefreshHistoryAsync();
        }
        catch (OperationCanceledException) { StatusText = "Cancelled; any album already committed remains safely journaled."; }
        catch (Exception ex) { StatusText = $"Apply failed: {ex.Message}"; }
        finally { FinishBusy(); }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private void FinishBusy()
    {
        _cts?.Dispose(); _cts = null; IsBusy = false; IsPreviewing = false; IsApplying = false;
        PreviewCommand.NotifyCanExecuteChanged(); PreflightCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private void MarkPresetCustomized()
    {
        if (_applyingPreset || SelectedPreset is null)
            return;
        if (!StringComparer.OrdinalIgnoreCase.Equals(SourceDirectory, SelectedPreset.SourceDirectory) ||
            !StringComparer.OrdinalIgnoreCase.Equals(ConfigurationPath, SelectedPreset.ConfigurationPath))
            SelectedPreset = null;
    }

    private void RefilterFiles()
    {
        Files.Clear();
        foreach (var item in _allFiles.Where(item => SelectedPreviewFilter switch
                 {
                     IngestPreviewFilter.Albums => item.IsAlbum,
                     IngestPreviewFilter.Outputs => item.IsOutput,
                     IngestPreviewFilter.Conflicts => item.IsConflict,
                     IngestPreviewFilter.Cleanup => item.IsCleanup,
                     _ => true,
                 }))
            Files.Add(item);
    }

    private void LoadPresets()
    {
        try
        {
            var presets = JsonSerializer.Deserialize<List<IngestPreset>>(
                _settings.GetPreference(PresetsPreference) ?? "[]") ?? [];
            foreach (var preset in presets.Where(preset => !string.IsNullOrWhiteSpace(preset.Name) &&
                         !string.IsNullOrWhiteSpace(preset.SourceDirectory) &&
                         !string.IsNullOrWhiteSpace(preset.ConfigurationPath)))
                Presets.Add(preset);
        }
        catch { }
    }

    private void PersistPresets() =>
        _settings.SetPreference(PresetsPreference, JsonSerializer.Serialize(Presets));

    private void LoadRecentSources()
    {
        try
        {
            var sources = JsonSerializer.Deserialize<List<string>>(
                _settings.GetPreference(RecentSourcesPreference) ?? "[]") ?? [];
            foreach (string source in sources.Where(source => !string.IsNullOrWhiteSpace(source))
                         .Distinct(StringComparer.OrdinalIgnoreCase).Take(RecentSourceLimit))
                RecentSources.Add(source);
        }
        catch { }
    }

    private void AddRecentSource(string source)
    {
        string fullPath = Path.GetFullPath(source);
        var existing = RecentSources.FirstOrDefault(item =>
            StringComparer.OrdinalIgnoreCase.Equals(item, fullPath));
        if (existing is not null)
            RecentSources.Remove(existing);
        RecentSources.Insert(0, fullPath);
        while (RecentSources.Count > RecentSourceLimit)
            RecentSources.RemoveAt(RecentSources.Count - 1);
        _settings.SetPreference(RecentSourcesPreference, JsonSerializer.Serialize(RecentSources));
        SelectedRecentSource = fullPath;
    }
}

public sealed class IngestHistoryItemViewModel(OperationJournalSummary summary)
{
    public OperationJournalSummary Summary { get; } = summary;
    public string Created => Summary.CreatedAtUtc.ToLocalTime().ToString("g");
    public string State => Summary.State switch
    {
        OperationJournalState.Completed => "Completed",
        OperationJournalState.Interrupted => "Interrupted â€” recovery available",
        OperationJournalState.RolledBack => "Rolled back",
        _ => "Quarantine present",
    };
    public bool IsInterrupted => Summary.State == OperationJournalState.Interrupted;
    public string AffectedItems => Summary.AffectedItemCount is int count
        ? $"{count:N0} item(s)" : "Open for item details";
    public string RunPath => Summary.RunPath;
}

public partial class IngestFileItemViewModel : ViewModelBase
{
    private readonly IngestFileSummary _file;
    public string Source => _file.Source;
    public string SourceType => _file.SourceType;
    public string Summary => _file.Summary;
    public bool IsAlbum { get; }
    public bool IsOutput { get; }
    public bool IsCleanup { get; }
    public bool IsConflict { get; }

    [ObservableProperty]
    private bool _isComplete;
    [ObservableProperty]
    private string? _progressText;

    public IngestFileItemViewModel(IngestFileSummary file, bool isAlbum = false,
        bool isOutput = false, bool isCleanup = false, bool isConflict = false)
    {
        _file = file;
        IsAlbum = isAlbum;
        IsOutput = isOutput;
        IsCleanup = isCleanup;
        IsConflict = isConflict;
    }

    public static IngestFileItemViewModel ForConflict(IngestConflict conflict) => new(
        new IngestFileSummary(conflict.Path, "Conflict", conflict.Message), isConflict: true);

    public static IngestFileItemViewModel ForOutput(IngestOutputPlan output) => new(
        new IngestFileSummary(output.SourcePath, $"{output.Kind} output",
            $"Destination â†’ {output.DestinationPath}"), isOutput: true);

    public void ResetProgress()
    {
        IsComplete = false;
        ProgressText = null;
    }

    public void SetProgress(IngestFileProgressState state, string operation)
    {
        if (IsComplete && state == IngestFileProgressState.InProgress)
            return;
        IsComplete = state == IngestFileProgressState.Completed;
        ProgressText = state switch
        {
            IngestFileProgressState.InProgress => $"● In progress — {operation}",
            IngestFileProgressState.Completed => "✓ Complete",
            IngestFileProgressState.Failed => $"✕ Not completed — {operation}",
            _ => null,
        };
    }
}
