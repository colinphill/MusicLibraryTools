using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.App.Services;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibrary.App.ViewModels;

public partial class IngestViewModel : ViewModelBase
{
    private const string SourcePreference = "Ingest.SourceDirectory";
    private const string ConfigPreference = "Ingest.ConfigurationPath";
    private readonly IIngestMusicService _service;
    private readonly IFileDialogService _files;
    private readonly IDialogService _dialogs;
    private readonly IAppSettings _settings;
    private readonly ILibraryService _library;
    private CancellationTokenSource? _cts;
    private IngestPlan? _plan;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    private string? _sourceDirectory;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(PreviewCommand)), NotifyCanExecuteChangedFor(nameof(EditConfigurationCommand))]
    private string? _configurationPath;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(PreviewCommand)), NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
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

    public ObservableCollection<IngestFileItemViewModel> Files { get; } = [];
    public ObservableCollection<IngestConflict> Conflicts { get; } = [];
    public event Action? IngestCompleted;

    public IngestViewModel(IIngestMusicService service, IFileDialogService files, IDialogService dialogs,
        IAppSettings settings, ILibraryService library)
    {
        _service = service; _files = files; _dialogs = dialogs; _settings = settings; _library = library;
        SourceDirectory = settings.GetPreference(SourcePreference);
        ConfigurationPath = settings.GetPreference(ConfigPreference);
    }

    partial void OnSourceDirectoryChanged(string? value)
    {
        _settings.SetPreference(SourcePreference, string.IsNullOrWhiteSpace(value) ? null : value);
        InvalidatePreview();
    }

    partial void OnConfigurationPathChanged(string? value)
    {
        _settings.SetPreference(ConfigPreference, string.IsNullOrWhiteSpace(value) ? null : value);
        InvalidatePreview();
    }

    private void InvalidatePreview()
    {
        if (_plan is null) return;
        _plan = null;
        HasApplicablePreview = false;
        StatusText = "Inputs changed. Preview again before applying.";
    }

    [RelayCommand]
    private async Task BrowseSourceAsync()
    {
        string? path = await _files.PickFolderAsync("Select incoming music directory");
        if (path is not null) SourceDirectory = path;
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

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewAsync()
    {
        IsBusy = true; IsPreviewing = true; HasApplicablePreview = false; _plan = null; Files.Clear(); Conflicts.Clear();
        _cts = new CancellationTokenSource();
        try
        {
            StatusText = "Scanning and planning…";
            var plan = await _service.PreviewAsync(new IngestRequest(SourceDirectory!, ConfigurationPath!), _cts.Token);
            _plan = plan;
            foreach (var file in plan.Files) Files.Add(new IngestFileItemViewModel(file));
            foreach (var conflict in plan.Conflicts) Conflicts.Add(conflict);
            HasApplicablePreview = plan.CanApply;
            _settings.SetPreference(SourcePreference, plan.Request.SourceDirectory);
            _settings.SetPreference(ConfigPreference, plan.Request.ConfigurationPath);
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
        foreach (var file in Files) file.ResetProgress();
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
                    Files.FirstOrDefault(file => string.Equals(file.Source, p.SourcePath, StringComparison.OrdinalIgnoreCase))
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
        PreviewCommand.NotifyCanExecuteChanged(); ApplyCommand.NotifyCanExecuteChanged();
    }
}

public partial class IngestFileItemViewModel(IngestFileSummary file) : ViewModelBase
{
    public string Source => file.Source;
    public string SourceType => file.SourceType;
    public string Summary => file.Summary;

    [ObservableProperty]
    private bool _isComplete;
    [ObservableProperty]
    private string? _progressText;

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
