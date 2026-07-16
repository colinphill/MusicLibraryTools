using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibrary.App.ViewModels;

/// <summary>Which result section the Analyze tab is currently showing.</summary>
public enum AnalysisResultView { Findings, Duplicates, Artists, Conflicts, Repairs, Matrix }

/// <summary>
/// Library-wide analysis. Each analysis type is run by its own button (inconsistencies, lossy files,
/// duplicates, similar artists, cross-set check); typed results are retained for the session.
/// Selecting a finding/track opens that file; similar-artist clusters can be merged in place.
/// Conservative and user-directed tag repairs share a preview/select/apply surface and reject
/// sources changed since preview.
/// </summary>
public partial class AnalyzerViewModel : ViewModelBase
{
    private const string FfmpegPreference = "Analyzer.FfmpegPath";
    private const string IngestConfigurationPreference = "Ingest.ConfigurationPath";
    private readonly ILibraryService _library;
    private readonly IArtistReconciler _reconciler;
    private readonly IAnalysisRepairService _repairs;
    private readonly IDecodedAudioVerificationService? _decodedAudio;
    private readonly IRepresentationRepairService? _representationRepairs;
    private readonly IAppSettings _settings;
    private bool _settingFfmpegDefault;
    private bool _hasFfmpegOverride;
    private CancellationTokenSource? _cts;
    private IReadOnlyList<TrackRecord> _representationRecords = [];
    private IReadOnlyList<DecodedAudioPair> _decodedAudioPairs = [];

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusText = "Choose an analysis to run.";

    [ObservableProperty]
    private AnalysisResultView _activeView = AnalysisResultView.Findings;

    [ObservableProperty]
    private AnalysisRunViewModel? _selectedRun;

    /// <summary>Fuzzy-distance threshold for the similar-artist check (AnalyzeMetadata's checkartists thresh).</summary>
    [ObservableProperty]
    private double _artistThreshold = 0.2;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VerifyDecodedAudioCommand))]
    private string _ffmpegPath = "ffmpeg";

    public ObservableCollection<AnalysisRunViewModel> Runs { get; } = [];
    public IReadOnlyList<AnalysisProblemGroupViewModel> FindingGroups => SelectedRun?.FindingGroups ?? [];
    public IReadOnlyList<DuplicateGroup> Duplicates => SelectedRun?.Duplicates ?? [];
    public IReadOnlyList<ArtistGroupViewModel> ArtistGroups => SelectedRun?.ArtistGroups ?? [];
    public IReadOnlyList<AnalysisConflictGroupViewModel> ConflictGroups => SelectedRun?.ConflictGroups ?? [];
    public IReadOnlyList<AnalysisRepairItemViewModel> RepairItems => SelectedRun?.RepairItems ?? [];
    public IReadOnlyList<AlbumMetadataMatrix> Matrices => SelectedRun?.Matrices ?? [];
    public bool HasRuns => Runs.Count > 0;

    // Section visibility (bound in XAML; ActiveView drives which one shows).
    public bool ShowFindings => ActiveView == AnalysisResultView.Findings;
    public bool ShowDuplicates => ActiveView == AnalysisResultView.Duplicates;
    public bool ShowArtists => ActiveView == AnalysisResultView.Artists;
    public bool ShowConflicts => ActiveView == AnalysisResultView.Conflicts;
    public bool ShowRepairs => ActiveView == AnalysisResultView.Repairs;
    public bool ShowMatrix => ActiveView == AnalysisResultView.Matrix;

    /// <summary>Raised with a file path when the user opens a finding/track.</summary>
    public event Action<string>? OpenRequested;
    public event Action<IReadOnlyList<string>>? RepairsApplied;

    public AnalyzerViewModel(ILibraryService library, IArtistReconciler reconciler,
        IAnalysisRepairService repairs, IAppSettings settings,
        IDecodedAudioVerificationService? decodedAudio = null,
        IRepresentationRepairService? representationRepairs = null)
    {
        _library = library;
        _reconciler = reconciler;
        _repairs = repairs;
        _decodedAudio = decodedAudio;
        _representationRepairs = representationRepairs;
        _settings = settings;
        ApplyFfmpegDefault();
        settings.ConfigurationChanged += (_, _) =>
        {
            if (!_hasFfmpegOverride)
                ApplyFfmpegDefault();
            ClearRuns();
            _representationRecords = [];
            _decodedAudioPairs = [];
        };
    }

    partial void OnFfmpegPathChanged(string value)
    {
        if (_settingFfmpegDefault)
            return;
        _hasFfmpegOverride = true;
        _settings.SetPreference(FfmpegPreference, string.IsNullOrWhiteSpace(value) ? null : value);
    }

    private void ApplyFfmpegDefault()
    {
        _settingFfmpegDefault = true;
        try
        {
            FfmpegPath = _settings.Configuration?.FfmpegPath ??
                _settings.GetPreference(FfmpegPreference) ??
                (File.Exists(@"C:\ffmpeg\nonfree\ffmpeg.exe")
                    ? @"C:\ffmpeg\nonfree\ffmpeg.exe"
                    : "ffmpeg");
        }
        finally
        {
            _settingFfmpegDefault = false;
        }
    }

    partial void OnActiveViewChanged(AnalysisResultView value)
    {
        OnPropertyChanged(nameof(ShowFindings));
        OnPropertyChanged(nameof(ShowDuplicates));
        OnPropertyChanged(nameof(ShowArtists));
        OnPropertyChanged(nameof(ShowConflicts));
        OnPropertyChanged(nameof(ShowRepairs));
        OnPropertyChanged(nameof(ShowMatrix));
    }

    partial void OnSelectedRunChanged(AnalysisRunViewModel? value)
    {
        OnPropertyChanged(nameof(FindingGroups));
        OnPropertyChanged(nameof(Duplicates));
        OnPropertyChanged(nameof(ArtistGroups));
        OnPropertyChanged(nameof(ConflictGroups));
        OnPropertyChanged(nameof(RepairItems));
        OnPropertyChanged(nameof(Matrices));

        if (value is not null)
        {
            ActiveView = value.View;
            StatusText = value.Summary;
        }
        else
        {
            StatusText = "Choose an analysis to run.";
        }

        NotifyCommands();
    }

    private bool CanRun() => _library.IsReady && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunInconsistencies() => RunOverRecords("Inconsistencies", AnalysisResultView.Findings, (records, ct) =>
    {
        var report = LibraryAnalyzer.Inconsistencies(records);
        string status = report.Count == 0 ? "Inconsistencies: none found." : $"Inconsistencies: {report.Count:N0} finding(s).";
        return (status, AnalysisRunViewModel.ForFindings(report, records, status));
    });

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunLossy() => RunOverRecords("Lossy files", AnalysisResultView.Findings, (records, ct) =>
    {
        var report = LibraryAnalyzer.Lossless(records);
        string status = report.Count == 0 ? "No lossy files." : $"Lossy files: {report.Count:N0}.";
        return (status, AnalysisRunViewModel.ForFindings(report, records, status));
    });

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunDuplicates() => RunOverRecords("Duplicates", AnalysisResultView.Duplicates, (records, ct) =>
    {
        var dupes = DuplicateFinder.Find(records, ct);
        string status = dupes.Count == 0 ? "No duplicates found." : $"{dupes.Count:N0} duplicate group(s).";
        return (status, AnalysisRunViewModel.ForDuplicates("Duplicates", dupes, status));
    });

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunSimilarArtists() => RunOverRecords("Similar artists", AnalysisResultView.Artists, (records, ct) =>
    {
        var groups = _reconciler.FindSimilarArtists(records, ArtistThreshold, ct);
        string status = groups.Count == 0 ? "No similar artist names found." : $"{groups.Count:N0} cluster(s) of similar artist names.";
        return (status, AnalysisRunViewModel.ForArtists(
            "Similar artists",
            groups.Select(group => new ArtistGroupViewModel(_reconciler, group)).ToList(),
            status));
    });

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunAlbumMatrix() => RunOverRecords("Album metadata matrix", AnalysisResultView.Matrix, (records, ct) =>
    {
        var matrices = AlbumMetadataMatrixBuilder.Build(records);
        string status = matrices.Count == 0
            ? "Album metadata matrix: no inconsistent albums found."
            : $"Album metadata matrix: {matrices.Count:N0} album(s), " +
              $"{matrices.Sum(matrix => matrix.InconsistentCellCount):N0} inconsistent cell(s).";
        return (status, AnalysisRunViewModel.ForMatrices(matrices, status));
    });

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunArtworkHealth()
    {
        using var scope = BeginRun("Artwork health", AnalysisResultView.Findings);
        try
        {
            var records = await _library.GetAllRecordsAsync(scope.Token);
            var artwork = await _library.GetArtworkAuditFilesAsync(scope.Token);
            var result = await Task.Run(() =>
            {
                var report = ArtworkHealthAnalyzer.Analyze(records, artwork, scope.Token);
                int deferred = report.Findings.Count(finding => finding.Problem == "Artwork scan deferred");
                int actionable = report.Count - deferred;
                string status = report.Count == 0
                    ? "Artwork health: no cached issues found."
                    : $"Artwork health: {actionable:N0} cached issue(s), {deferred:N0} file(s) still deferred. " +
                      "No image blobs were loaded.";
                return (Status: status, Run: AnalysisRunViewModel.ForFindings(report, records, status));
            }, scope.Token);
            AddRun(result.Run);
        }
        catch (OperationCanceledException) { StatusText = "Artwork health audit cancelled."; }
        catch (Exception ex) { StatusText = $"Artwork health audit failed: {ex.Message}"; }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunRepresentations()
    {
        using var scope = BeginRun("Album representations", AnalysisResultView.Findings);
        try
        {
            var records = await _library.GetAllRecordsAsync(scope.Token);
            var prepared = await Task.Run(() => (
                Pairs: RepresentationAnalyzer.DecodedAudioCandidatePairs(records),
                Report: RepresentationAnalyzer.Compare(records, scope.Token),
                ArtworkPaths: RepresentationAnalyzer.ArtworkCandidatePaths(records)), scope.Token);
            _representationRecords = records;
            _decodedAudioPairs = prepared.Pairs;
            var artworkPaths = prepared.ArtworkPaths;
            IReadOnlyList<AnalysisFinding> artworkFindings = [];
            if (artworkPaths.Count > 0)
            {
                StatusText = $"Comparing embedded artwork for {artworkPaths.Count:N0} matched counterpart file(s)…";
                try
                {
                    var signatures = await _library.GetImageSignaturesAsync(artworkPaths, scope.Token);
                    artworkFindings = await Task.Run(() =>
                    {
                        var signatureMap = artworkPaths.Zip(signatures)
                            .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.OrdinalIgnoreCase);
                        return RepresentationAnalyzer.CompareArtwork(records, signatureMap).Findings;
                    }, scope.Token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    artworkFindings = [new(artworkPaths[0],
                        $"Artwork comparison could not hydrate all matched files: {ex.Message}",
                        "Artwork comparison unavailable")];
                }
            }
            var result = await Task.Run(() =>
            {
                var combined = new AnalysisReport(prepared.Report.Name,
                    [.. prepared.Report.Findings, .. artworkFindings]);
                string status = combined.Count == 0
                    ? "Album representations: no counterpart, metadata, duration, or artwork drift found."
                    : $"Album representations: {combined.Count:N0} finding(s).";
                return (Status: status, Run: AnalysisRunViewModel.ForFindings(combined, records, status));
            }, scope.Token);
            AddRun(result.Run);
        }
        catch (OperationCanceledException) { StatusText = "Album representation comparison cancelled."; }
        catch (Exception ex) { StatusText = $"Album representation comparison failed: {ex.Message}"; }
        finally { VerifyDecodedAudioCommand.NotifyCanExecuteChanged(); }
    }

    private bool CanVerifyDecodedAudio() => !IsBusy && _decodedAudio is not null &&
        _decodedAudioPairs.Count > 0 && !string.IsNullOrWhiteSpace(FfmpegPath);

    [RelayCommand(CanExecute = nameof(CanVerifyDecodedAudio))]
    private async Task VerifyDecodedAudio()
    {
        if (_decodedAudio is null || _decodedAudioPairs.Count == 0)
            return;
        using var scope = BeginRun("decoded-audio verification", AnalysisResultView.Findings);
        try
        {
            var progress = new Progress<DecodedAudioProgress>(item =>
                StatusText = $"Decoding audio… {item.CompletedFiles:N0}/{item.TotalFiles:N0}: {item.Path}");
            var report = await _decodedAudio.VerifyAsync(
                FfmpegPath, _decodedAudioPairs, progress, scope.Token);
            string status = report.Count == 0
                ? $"Decoded-audio verification: {_decodedAudioPairs.Count:N0} compatible pair(s) match."
                : $"Decoded-audio verification: {report.Count:N0} pair(s) differ.";
            var run = await Task.Run(
                () => AnalysisRunViewModel.ForFindings(report, _representationRecords, status), scope.Token);
            AddRun(run);
        }
        catch (OperationCanceledException) { StatusText = "Decoded-audio verification cancelled."; }
        catch (Exception ex) { StatusText = $"Decoded-audio verification failed: {ex.Message}"; }
    }

    private bool CanPreviewRepresentationRepairs() => CanRun() && _representationRepairs is not null;

    [RelayCommand(CanExecute = nameof(CanPreviewRepresentationRepairs))]
    private async Task PreviewRepresentationRepairs()
    {
        if (_representationRepairs is null)
            return;
        using var scope = BeginRun("representation repair preview", AnalysisResultView.Findings);
        try
        {
            var records = await _library.GetAllRecordsAsync(scope.Token);
            var preview = await _representationRepairs.PreviewAsync(
                records, _settings.GetPreference(IngestConfigurationPreference), scope.Token);
            var runs = await Task.Run(() =>
            {
                var projected = new List<AnalysisRunViewModel>(2);
                var findings = preview.FileActions.Select(action => new AnalysisFinding(
                        action.SourcePath,
                        $"{action.Description} Destination: {action.DestinationPath}",
                        action.Kind switch
                        {
                            RepresentationRepairKind.DeriveCdFlac => "Derive missing CD FLAC",
                            RepresentationRepairKind.DeriveAac => "Derive missing AAC",
                            _ => "Organize representation",
                        }))
                    .Concat(preview.Warnings.Select(warning => new AnalysisFinding(
                        records.FirstOrDefault()?.Path ?? "", warning, "Preview unavailable")))
                    .ToList();

                if (findings.Count > 0)
                {
                    string actionStatus = $"Representation file repairs: {preview.FileActions.Count:N0} action(s), " +
                        $"{preview.Warnings.Count:N0} warning(s). No files were changed.";
                    projected.Add(AnalysisRunViewModel.ForFindings(
                        new AnalysisReport("Representation file repairs", findings), records, actionStatus));
                }

                if (preview.MetadataCopies.Items.Count > 0)
                {
                    var items = preview.MetadataCopies.Items.Select(CreateRepairItem).ToList();
                    string metadataStatus = $"Representation metadata: {items.Count:N0} copy operation(s). " +
                        "Review the source role in each reason, then apply selected.";
                    projected.Add(AnalysisRunViewModel.ForRepairs(preview.MetadataCopies, items, metadataStatus));
                }
                return projected;
            }, scope.Token);

            foreach (var run in runs)
                AddRun(run);
            if (runs.Count == 0)
                StatusText = "No representation derivation, metadata-copy, or organization repairs were found.";
        }
        catch (OperationCanceledException) { StatusText = "Representation repair preview cancelled."; }
        catch (Exception ex) { StatusText = $"Representation repair preview failed: {ex.Message}"; }
        finally { ApplyRepairsCommand.NotifyCanExecuteChanged(); }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task PreviewMetadataRepairs()
    {
        using var scope = BeginRun("Metadata repair preview", AnalysisResultView.Repairs);
        try
        {
            var records = await _library.GetAllRecordsAsync(scope.Token);
            var run = await Task.Run(() =>
            {
                var plan = _repairs.PreviewSafeRepairs(records);
                var repairItems = plan.Items.Select(CreateRepairItem).ToList();
                string status = plan.Items.Count == 0
                    ? "No safely inferable metadata repairs were found."
                    : $"Previewed {plan.Items.Count:N0} metadata repair(s). Review, then apply selected.";
                return AnalysisRunViewModel.ForRepairs(plan, repairItems, status);
            }, scope.Token);
            AddRun(run);
        }
        catch (OperationCanceledException) { StatusText = "Metadata repair preview cancelled."; }
        catch (Exception ex) { StatusText = $"Metadata repair preview failed: {ex.Message}"; }
        finally { ApplyRepairsCommand.NotifyCanExecuteChanged(); }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task FindAlbumArtistConflicts()
    {
        using var scope = BeginRun("Album artist conflicts", AnalysisResultView.Conflicts);
        try
        {
            var records = await _library.GetAllRecordsAsync(scope.Token);
            var run = await Task.Run(() =>
            {
                var groups = _repairs.FindAlbumArtistConflicts(records).Select(conflict =>
                {
                    var group = new AnalysisConflictGroupViewModel(conflict);
                    group.SelectionChanged += () => PreviewConflictRepairsCommand.NotifyCanExecuteChanged();
                    return group;
                }).ToList();
                string status = groups.Count == 0
                    ? "No conflicting album artists were found."
                    : $"Found {groups.Count:N0} album(s) with conflicting album artists. Choose canonical values to continue.";
                return AnalysisRunViewModel.ForConflicts(groups, status);
            }, scope.Token);
            AddRun(run);
        }
        catch (OperationCanceledException) { StatusText = "Album artist conflict search cancelled."; }
        catch (Exception ex) { StatusText = $"Album artist conflict search failed: {ex.Message}"; }
        finally { PreviewConflictRepairsCommand.NotifyCanExecuteChanged(); }
    }

    private bool CanPreviewConflictRepairs() => !IsBusy &&
        ConflictGroups.Any(group => group.SelectedOption is not null);

    [RelayCommand(CanExecute = nameof(CanPreviewConflictRepairs))]
    private async Task PreviewConflictRepairs()
    {
        var resolutions = ConflictGroups
            .Where(group => group.SelectedOption is not null)
            .Select(group => new AnalysisConflictResolution(group.Conflict, group.SelectedOption!.Value))
            .ToList();
        if (resolutions.Count == 0)
            return;
        using var scope = BeginRun("conflict repair preview", AnalysisResultView.Repairs);
        try
        {
            var run = await Task.Run(() =>
            {
                var plan = _repairs.PreviewConflictRepairs(resolutions);
                var items = plan.Items.Select(CreateRepairItem).ToList();
                string status = plan.Items.Count == 0
                    ? "The selected canonical values already match every file."
                    : $"Previewed {plan.Items.Count:N0} user-directed repair(s). Review, then apply selected.";
                return AnalysisRunViewModel.ForRepairs(plan, items, status);
            }, scope.Token);
            AddRun(run);
        }
        catch (OperationCanceledException) { StatusText = "Conflict repair preview cancelled."; }
        catch (Exception ex) { StatusText = $"Conflict repair preview failed: {ex.Message}"; }
        finally { ApplyRepairsCommand.NotifyCanExecuteChanged(); }
    }

    private bool CanApplyRepairs() => !IsBusy && SelectedRun?.RepairPlan is not null &&
        RepairItems.Any(item => item.IsSelected && !item.IsApplied);

    [RelayCommand(CanExecute = nameof(CanApplyRepairs))]
    private async Task ApplyRepairs()
    {
        if (SelectedRun?.RepairPlan is not { } repairPlan)
            return;
        var selected = RepairItems.Where(item => item.IsSelected && !item.IsApplied).ToList();
        if (selected.Count == 0)
            return;
        int selectedFiles = selected.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        using var scope = BeginRun("Apply metadata repairs", AnalysisResultView.Repairs);
        try
        {
            var selectedPlan = repairPlan with { Items = selected.Select(item => item.Repair).ToList() };
            var progress = new Progress<int>(done =>
                StatusText = $"Applying metadata repairs… {done:N0}/{selectedFiles:N0} file(s)");
            var result = await _repairs.ApplyAsync(selectedPlan, progress, scope.Token);
            var byPath = result.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var item in selected)
            {
                if (!byPath.TryGetValue(item.Path, out var file))
                    continue;
                item.ResultText = file.Outcome switch
                {
                    WriteOutcome.Saved => "Applied",
                    WriteOutcome.Skipped => "Already correct",
                    _ => file.Error ?? "Failed",
                };
                item.IsApplied = file.Outcome is WriteOutcome.Saved or WriteOutcome.Skipped;
                if (item.IsApplied)
                    item.IsSelected = false;
            }
            StatusText = $"Metadata repairs: {result.Summary}.";
            var changed = result.Files
                .Where(file => file.Outcome == WriteOutcome.Saved)
                .Select(file => file.Path)
                .ToList();
            if (changed.Count > 0)
                RepairsApplied?.Invoke(changed);
        }
        catch (OperationCanceledException) { StatusText = "Metadata repair apply cancelled."; }
        catch (Exception ex) { StatusText = $"Metadata repair apply failed: {ex.Message}"; }
        finally { ApplyRepairsCommand.NotifyCanExecuteChanged(); }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunCheckSets()
    {
        using var scope = BeginRun("Cross-set check", AnalysisResultView.Findings);
        try
        {
            var records = await _library.GetAllRecordsAsync(scope.Token);
            var report = await _library.CheckSetsAsync(scope.Token);
            string status = report.Count == 0
                ? "Cross-set check: no differences (needs 2+ configured sets to compare)."
                : $"Cross-set check: {report.Count:N0} finding(s).";
            var run = await Task.Run(
                () => AnalysisRunViewModel.ForFindings(report, records, status), scope.Token);
            AddRun(run);
        }
        catch (OperationCanceledException) { StatusText = "Cross-set check cancelled."; }
        catch (Exception ex) { StatusText = $"Cross-set check failed: {ex.Message}"; }
    }

    // Shared runner for the analyses that operate on the flat record list: fetch records, run `body`
    // off the UI thread, including result projection, then publish one completed snapshot.
    private async Task RunOverRecords(string label, AnalysisResultView view,
        Func<IReadOnlyList<TrackRecord>, CancellationToken, (string Status, AnalysisRunViewModel Run)> body)
    {
        using var scope = BeginRun(label, view);
        try
        {
            var records = await _library.GetAllRecordsAsync(scope.Token);
            var (status, run) = await Task.Run(() => body(records, scope.Token), scope.Token);
            AddRun(run);
            StatusText = status;
        }
        catch (OperationCanceledException) { StatusText = $"{label} cancelled."; }
        catch (Exception ex) { StatusText = $"{label} failed: {ex.Message}"; }
    }

    // Sets up busy state / cancellation / active view; disposing restores idle state.
    private RunScope BeginRun(string label, AnalysisResultView view)
    {
        IsBusy = true;
        // Keep the selected retained result visible while another analysis runs. With no history
        // yet, show the destination surface so the first run still has a natural empty state.
        if (SelectedRun is null)
            ActiveView = view;
        StatusText = $"Running {label}…";
        _cts = new CancellationTokenSource();
        NotifyCommands();
        return new RunScope(this);
    }

    private sealed class RunScope(AnalyzerViewModel vm) : IDisposable
    {
        public CancellationToken Token => vm._cts!.Token;
        public void Dispose()
        {
            vm.IsBusy = false;
            vm._cts?.Dispose();
            vm._cts = null;
            vm.NotifyCommands();
        }
    }

    private void AddRun(AnalysisRunViewModel run)
    {
        Runs.Insert(0, run);
        OnPropertyChanged(nameof(HasRuns));
        SelectedRun = run;
        RemoveRunCommand.NotifyCanExecuteChanged();
        ClearRunsCommand.NotifyCanExecuteChanged();
    }

    private AnalysisRepairItemViewModel CreateRepairItem(AnalysisTagRepair item)
    {
        var viewModel = new AnalysisRepairItemViewModel(item);
        viewModel.SelectionChanged += () => ApplyRepairsCommand.NotifyCanExecuteChanged();
        return viewModel;
    }

    private bool CanRemoveRun() => !IsBusy && SelectedRun is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveRun))]
    private void RemoveRun()
    {
        if (SelectedRun is null)
            return;
        int index = Runs.IndexOf(SelectedRun);
        Runs.Remove(SelectedRun);
        SelectedRun = Runs.Count == 0 ? null : Runs[Math.Min(index, Runs.Count - 1)];
        OnPropertyChanged(nameof(HasRuns));
        NotifyCommands();
    }

    private bool CanClearRuns() => !IsBusy && Runs.Count > 0;

    [RelayCommand(CanExecute = nameof(CanClearRuns))]
    private void ClearRuns()
    {
        Runs.Clear();
        SelectedRun = null;
        OnPropertyChanged(nameof(HasRuns));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        RunInconsistenciesCommand.NotifyCanExecuteChanged();
        RunLossyCommand.NotifyCanExecuteChanged();
        RunDuplicatesCommand.NotifyCanExecuteChanged();
        RunSimilarArtistsCommand.NotifyCanExecuteChanged();
        FindAlbumArtistConflictsCommand.NotifyCanExecuteChanged();
        PreviewConflictRepairsCommand.NotifyCanExecuteChanged();
        RunAlbumMatrixCommand.NotifyCanExecuteChanged();
        RunArtworkHealthCommand.NotifyCanExecuteChanged();
        RunRepresentationsCommand.NotifyCanExecuteChanged();
        PreviewRepresentationRepairsCommand.NotifyCanExecuteChanged();
        VerifyDecodedAudioCommand.NotifyCanExecuteChanged();
        RunCheckSetsCommand.NotifyCanExecuteChanged();
        PreviewMetadataRepairsCommand.NotifyCanExecuteChanged();
        ApplyRepairsCommand.NotifyCanExecuteChanged();
        RemoveRunCommand.NotifyCanExecuteChanged();
        ClearRunsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void Open(string? path)
    {
        if (!string.IsNullOrEmpty(path))
            OpenRequested?.Invoke(path);
    }
}

public partial class AnalysisRepairItemViewModel : ViewModelBase
{
    public AnalysisTagRepair Repair { get; }
    public string Path => Repair.Path;
    public string Field => Repair.Field.ToString();
    public string Before => string.IsNullOrWhiteSpace(Repair.Before) ? "(missing)" : Repair.Before;
    public string After => Repair.After;
    public string Reason => Repair.Reason;

    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private bool _isApplied;
    [ObservableProperty] private string? _resultText;

    public event Action? SelectionChanged;

    public AnalysisRepairItemViewModel(AnalysisTagRepair repair) => Repair = repair;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();
}

public partial class AnalysisConflictGroupViewModel : ViewModelBase
{
    public AnalysisTagConflict Conflict { get; }
    public string Album => Conflict.Album;
    public string Directory => Conflict.Directory;
    public string Field => Conflict.Field.ToString();
    public int FileCount => Conflict.Targets.Count;
    public int MissingCount => Conflict.Targets.Count(target => string.IsNullOrWhiteSpace(target.Before));
    public IReadOnlyList<AnalysisConflictOptionViewModel> Options { get; }

    [ObservableProperty]
    private AnalysisConflictOptionViewModel? _selectedOption;

    public event Action? SelectionChanged;

    public AnalysisConflictGroupViewModel(AnalysisTagConflict conflict)
    {
        Conflict = conflict;
        Options = conflict.Options.Select(option => new AnalysisConflictOptionViewModel(
            option,
            conflict.Targets
                .Where(target => !string.IsNullOrWhiteSpace(target.Before) &&
                    StringComparer.CurrentCultureIgnoreCase.Equals(target.Before.Trim(), option.Value))
                .Select(target => target.Path)
                .ToList()))
            .ToList();
    }

    partial void OnSelectedOptionChanged(AnalysisConflictOptionViewModel? value) => SelectionChanged?.Invoke();
}

public sealed class AnalysisConflictOptionViewModel(
    AnalysisConflictOption option,
    IReadOnlyList<string> paths)
{
    public string Value => option.Value;
    public int FileCount => option.FileCount;
    public IReadOnlyList<string> Paths => paths;
}
