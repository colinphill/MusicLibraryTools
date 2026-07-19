using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

/// <summary>Which result section the Analyze tab is currently showing.</summary>
public enum AnalysisResultView
{
    Findings,
    Duplicates,
    Artists,
    Conflicts,
    Repairs,
    RepresentationRepairs,
    Matrix,
    ItlRepairs,
}

/// <summary>
/// Library-wide analysis. Each analysis type is run by its own button (inconsistencies, lossy files,
/// duplicates, similar artists, cross-set check); typed results are retained for the session.
/// Selecting a finding/track opens that file; similar-artist clusters can be merged in place.
/// Conservative and user-directed tag repairs share a preview/select/apply surface and reject
/// sources changed since preview.
/// </summary>
public partial class AnalyzerViewModel : ViewModelBase
{
    private readonly ILibraryService _library;
    private readonly IArtistReconciler _reconciler;
    private readonly IAnalysisRepairService _repairs;
    private readonly IDecodedAudioVerificationService? _decodedAudio;
    private readonly IRepresentationRepairService? _representationRepairs;
    private readonly IItlMetadataRepairService? _itlMetadataRepairs;
    private readonly IAppSettings _settings;
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

    public string FfmpegPath => _settings.Configuration?.FfmpegPath ?? "ffmpeg";

    public ObservableCollection<AnalysisRunViewModel> Runs { get; } = [];
    public IReadOnlyList<AnalysisProblemGroupViewModel> FindingGroups => SelectedRun?.FindingGroups ?? [];
    public int FindingCount => FindingGroups.Sum(group => group.Count);
    public IReadOnlyList<DuplicateGroup> Duplicates => SelectedRun?.Duplicates ?? [];
    public IReadOnlyList<ArtistGroupViewModel> ArtistGroups => SelectedRun?.ArtistGroups ?? [];
    public IReadOnlyList<AnalysisConflictGroupViewModel> ConflictGroups => SelectedRun?.ConflictGroups ?? [];
    public IReadOnlyList<AnalysisRepairItemViewModel> RepairItems => SelectedRun?.RepairItems ?? [];
    public IReadOnlyList<AnalysisRepairCategoryGroupViewModel> RepairGroups =>
        SelectedRun?.RepairGroups ?? [];
    public IReadOnlyList<RepresentationRepairActionItemViewModel> RepresentationActionItems =>
        SelectedRun?.RepresentationActionItems ?? [];
    public IReadOnlyList<RepresentationRepairCategoryGroupViewModel> RepresentationActionGroups =>
        SelectedRun?.RepresentationActionGroups ?? [];
    public IReadOnlyList<string> RepresentationWarnings =>
        SelectedRun?.RepresentationWarnings ?? [];
    public IReadOnlyList<AlbumMetadataMatrix> Matrices => SelectedRun?.Matrices ?? [];
    public IReadOnlyList<ItlMetadataRepairItemViewModel> ItlRepairItems =>
        SelectedRun?.ItlRepairItems ?? [];
    public IReadOnlyList<ItlMetadataRepairCategoryGroupViewModel> ItlRepairGroups =>
        SelectedRun?.ItlRepairGroups ?? [];
    public IReadOnlyList<string> FilteredPaths => Runs
        .SelectMany(run => run.FilteredPaths)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    public bool HasRuns => Runs.Count > 0;

    private object? _selectedFindingNode;
    private object? _selectedRepairNode;
    private object? _selectedRepresentationNode;
    private object? _selectedItlRepairNode;

    public object? SelectedFindingNode
    {
        get => _selectedFindingNode;
        set
        {
            if (SetProperty(ref _selectedFindingNode, value))
            {
                OnPropertyChanged(nameof(DisplayedFindings));
                OnPropertyChanged(nameof(IsFindingRootSelected));
            }
        }
    }

    public object? SelectedRepairNode
    {
        get => _selectedRepairNode;
        set
        {
            if (SetProperty(ref _selectedRepairNode, value))
                OnPropertyChanged(nameof(DisplayedRepairItems));
        }
    }

    public object? SelectedRepresentationNode
    {
        get => _selectedRepresentationNode;
        set
        {
            if (SetProperty(ref _selectedRepresentationNode, value))
                OnPropertyChanged(nameof(DisplayedRepresentationItems));
        }
    }

    public object? SelectedItlRepairNode
    {
        get => _selectedItlRepairNode;
        set
        {
            if (SetProperty(ref _selectedItlRepairNode, value))
                OnPropertyChanged(nameof(DisplayedItlRepairItems));
        }
    }

    public IReadOnlyList<AnalysisFindingViewModel> DisplayedFindings =>
        SelectedFindingNode switch
        {
            AnalysisFindingViewModel finding => [finding],
            AnalysisAlbumGroupViewModel album => album.Findings,
            AnalysisArtistGroupViewModel artist => artist.Albums
                .SelectMany(group => group.Findings).ToList(),
            AnalysisProblemGroupViewModel problem => problem.Artists
                .SelectMany(group => group.Albums)
                .SelectMany(group => group.Findings).ToList(),
            _ => FindingGroups.SelectMany(group => group.Artists)
                .SelectMany(group => group.Albums)
                .SelectMany(group => group.Findings).ToList(),
        };
    public bool IsFindingRootSelected => SelectedFindingNode is null;

    public IReadOnlyList<AnalysisRepairItemViewModel> DisplayedRepairItems =>
        SelectedRepairNode switch
        {
            AnalysisRepairItemViewModel item => [item],
            AnalysisRepairAlbumGroupViewModel album => album.Items,
            AnalysisRepairArtistGroupViewModel artist => artist.Albums
                .SelectMany(group => group.Items).ToList(),
            AnalysisRepairCategoryGroupViewModel category => category.Artists
                .SelectMany(group => group.Albums)
                .SelectMany(group => group.Items).ToList(),
            _ => RepairGroups.SelectMany(group => group.Artists)
                .SelectMany(group => group.Albums)
                .SelectMany(group => group.Items).ToList(),
        };

    public IReadOnlyList<RepresentationRepairActionItemViewModel> DisplayedRepresentationItems =>
        SelectedRepresentationNode switch
        {
            RepresentationRepairActionItemViewModel item => [item],
            RepresentationRepairAlbumGroupViewModel album => album.Items,
            RepresentationRepairArtistGroupViewModel artist => artist.Albums
                .SelectMany(group => group.Items).ToList(),
            RepresentationRepairCategoryGroupViewModel category => category.Artists
                .SelectMany(group => group.Albums)
                .SelectMany(group => group.Items).ToList(),
            _ => RepresentationActionGroups.SelectMany(group => group.Artists)
                .SelectMany(group => group.Albums)
                .SelectMany(group => group.Items).ToList(),
        };

    public IReadOnlyList<ItlMetadataRepairItemViewModel> DisplayedItlRepairItems =>
        SelectedItlRepairNode switch
        {
            ItlMetadataRepairItemViewModel item => [item],
            ItlMetadataRepairAlbumGroupViewModel album => album.Items,
            ItlMetadataRepairArtistGroupViewModel artist => artist.Albums
                .SelectMany(group => group.Items).Distinct().ToList(),
            ItlMetadataRepairCategoryGroupViewModel category => category.Artists
                .SelectMany(group => group.Albums)
                .SelectMany(group => group.Items).Distinct().ToList(),
            _ => ItlRepairItems,
        };

    // Section visibility (bound in XAML; ActiveView drives which one shows).
    public bool ShowFindings => ActiveView == AnalysisResultView.Findings;
    public bool ShowDuplicates => ActiveView == AnalysisResultView.Duplicates;
    public bool ShowArtists => ActiveView == AnalysisResultView.Artists;
    public bool ShowConflicts => ActiveView == AnalysisResultView.Conflicts;
    public bool ShowRepairs => ActiveView == AnalysisResultView.Repairs;
    public bool ShowRepresentationRepairs =>
        ActiveView == AnalysisResultView.RepresentationRepairs;
    public bool ShowMatrix => ActiveView == AnalysisResultView.Matrix;
    public int ActiveResultIndex
    {
        get => ActiveView switch
        {
            AnalysisResultView.Findings => 0,
            AnalysisResultView.Duplicates => 1,
            AnalysisResultView.Artists => 2,
            AnalysisResultView.Repairs => 3,
            AnalysisResultView.RepresentationRepairs => 4,
            AnalysisResultView.Conflicts => 5,
            AnalysisResultView.Matrix => 6,
            AnalysisResultView.ItlRepairs => 7,
            _ => 0,
        };
        set
        {
            AnalysisResultView view = value switch
            {
                0 => AnalysisResultView.Findings,
                1 => AnalysisResultView.Duplicates,
                2 => AnalysisResultView.Artists,
                3 => AnalysisResultView.Repairs,
                4 => AnalysisResultView.RepresentationRepairs,
                5 => AnalysisResultView.Conflicts,
                6 => AnalysisResultView.Matrix,
                7 => AnalysisResultView.ItlRepairs,
                _ => AnalysisResultView.Findings,
            };

            if (ActiveView != view)
                ActiveView = view;
        }
    }

    /// <summary>Raised with a file path when the user opens a finding/track.</summary>
    public event Action<string>? OpenRequested;
    public event Action<IReadOnlyList<string>>? RepairsApplied;
    public event Action<IReadOnlyList<string>>? FilterChanged;

    public AnalyzerViewModel(ILibraryService library, IArtistReconciler reconciler,
        IAnalysisRepairService repairs, IAppSettings settings,
        IDecodedAudioVerificationService? decodedAudio = null,
        IRepresentationRepairService? representationRepairs = null,
        IItlMetadataRepairService? itlMetadataRepairs = null)
    {
        _library = library;
        _reconciler = reconciler;
        _repairs = repairs;
        _decodedAudio = decodedAudio;
        _representationRepairs = representationRepairs;
        _itlMetadataRepairs = itlMetadataRepairs;
        _settings = settings;
        settings.ConfigurationChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(FfmpegPath));
            ClearRuns();
            _representationRecords = [];
            _decodedAudioPairs = [];
        };
    }

    partial void OnActiveViewChanged(AnalysisResultView value)
    {
        OnPropertyChanged(nameof(ActiveResultIndex));
        OnPropertyChanged(nameof(ShowFindings));
        OnPropertyChanged(nameof(ShowDuplicates));
        OnPropertyChanged(nameof(ShowArtists));
        OnPropertyChanged(nameof(ShowConflicts));
        OnPropertyChanged(nameof(ShowRepairs));
        OnPropertyChanged(nameof(ShowRepresentationRepairs));
        OnPropertyChanged(nameof(ShowMatrix));
    }

    partial void OnSelectedRunChanged(AnalysisRunViewModel? value)
    {
        SelectedFindingNode = null;
        SelectedRepairNode = null;
        SelectedRepresentationNode = null;
        SelectedItlRepairNode = null;
        OnPropertyChanged(nameof(FindingGroups));
        OnPropertyChanged(nameof(FindingCount));
        OnPropertyChanged(nameof(Duplicates));
        OnPropertyChanged(nameof(ArtistGroups));
        OnPropertyChanged(nameof(ConflictGroups));
        OnPropertyChanged(nameof(RepairItems));
        OnPropertyChanged(nameof(RepairGroups));
        OnPropertyChanged(nameof(RepresentationActionItems));
        OnPropertyChanged(nameof(RepresentationActionGroups));
        OnPropertyChanged(nameof(RepresentationWarnings));
        OnPropertyChanged(nameof(Matrices));
        OnPropertyChanged(nameof(ItlRepairItems));
        OnPropertyChanged(nameof(ItlRepairGroups));
        OnPropertyChanged(nameof(DisplayedFindings));
        OnPropertyChanged(nameof(DisplayedRepairItems));
        OnPropertyChanged(nameof(DisplayedRepresentationItems));
        OnPropertyChanged(nameof(DisplayedItlRepairItems));

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
        using var scope = BeginRun(
            "representation repair preview",
            AnalysisResultView.RepresentationRepairs);
        try
        {
            var records = await _library.GetAllRecordsAsync(scope.Token);
            var preview = await _representationRepairs.PreviewAsync(
                records, _settings.Configuration, scope.Token);
            var runs = await Task.Run(() =>
            {
                var projected = new List<AnalysisRunViewModel>(2);
                if (preview.FileActions.Count > 0 || preview.Warnings.Count > 0)
                {
                    string actionStatus = $"Representation file repairs: {preview.FileActions.Count:N0} action(s), " +
                        $"{preview.Warnings.Count:N0} warning(s). No files were changed.";
                    projected.Add(AnalysisRunViewModel.ForRepresentationRepairs(
                        preview.FileActions, preview.Warnings, records, actionStatus));
                }

                if (preview.MetadataCopies.Items.Count > 0)
                {
                    var items = preview.MetadataCopies.Items.Select(CreateRepairItem).ToList();
                    string metadataStatus = $"Representation metadata: {items.Count:N0} copy operation(s). " +
                        "Review the source role in each reason, then apply selected.";
                    projected.Add(AnalysisRunViewModel.ForRepairs(
                        preview.MetadataCopies, items, records, metadataStatus));
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
        finally
        {
            ApplyRepairsCommand.NotifyCanExecuteChanged();
            ApplyRepresentationRepairsCommand.NotifyCanExecuteChanged();
        }
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
                int applicable = plan.Items.Count(item => item.CanApply);
                string status = plan.Items.Count == 0
                    ? "No safely inferable metadata repairs were found."
                    : applicable == 0
                        ? $"Found {plan.Items.Count:N0} metadata repair opportunity(s), but none can be applied. Review the warnings."
                        : $"Previewed {plan.Items.Count:N0} metadata repair(s), {applicable:N0} applicable. Review, then apply active.";
                return AnalysisRunViewModel.ForRepairs(plan, repairItems, records, status);
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
            var records = await _library.GetAllRecordsAsync(scope.Token);
            var run = await Task.Run(() =>
            {
                var plan = _repairs.PreviewConflictRepairs(resolutions);
                var items = plan.Items.Select(CreateRepairItem).ToList();
                string status = plan.Items.Count == 0
                    ? "The selected canonical values already match every file."
                    : $"Previewed {plan.Items.Count:N0} user-directed repair(s). Review, then apply selected.";
                return AnalysisRunViewModel.ForRepairs(plan, items, records, status);
            }, scope.Token);
            AddRun(run);
        }
        catch (OperationCanceledException) { StatusText = "Conflict repair preview cancelled."; }
        catch (Exception ex) { StatusText = $"Conflict repair preview failed: {ex.Message}"; }
        finally { ApplyRepairsCommand.NotifyCanExecuteChanged(); }
    }

    private bool CanApplyRepairs() => !IsBusy && SelectedRun?.RepairPlan is not null &&
        RepairItems.Any(item => item.IsActive);

    private bool CanPreviewItlMetadataRepairs() => CanRun() && _itlMetadataRepairs is not null;

    [RelayCommand(CanExecute = nameof(CanPreviewItlMetadataRepairs))]
    private async Task PreviewItlMetadataRepairs()
    {
        if (_itlMetadataRepairs is null)
            return;
        using var scope = BeginRun("iTunes metadata repair preview", AnalysisResultView.ItlRepairs);
        try
        {
            var progress = new Progress<OperationProgress>(value =>
                StatusText = value.Message ?? "Preparing iTunes metadata repairs…");
            ItlMetadataRepairPlan plan = await _itlMetadataRepairs.PreviewAsync(
                progress: progress, ct: scope.Token);
            var items = plan.Items.Select(item =>
            {
                var viewModel = new ItlMetadataRepairItemViewModel(item);
                viewModel.StateChanged += () => ApplyItlMetadataRepairsCommand.NotifyCanExecuteChanged();
                return viewModel;
            }).ToList();
            string status = items.Count == 0
                ? "The iTunes library already matches the metadata cache."
                : $"Previewed {items.Count:N0} iTunes track repair(s). Review, mark repairs active, then apply.";
            AddRun(AnalysisRunViewModel.ForItlRepairs(plan, items, status));
        }
        catch (OperationCanceledException) { StatusText = "iTunes metadata repair preview cancelled."; }
        catch (Exception ex) { StatusText = $"iTunes metadata repair preview failed: {ex.Message}"; }
        finally { ApplyItlMetadataRepairsCommand.NotifyCanExecuteChanged(); }
    }

    private bool CanApplyItlMetadataRepairs() => !IsBusy &&
        _itlMetadataRepairs is not null && SelectedRun?.ItlRepairPlan is not null &&
        ItlRepairItems.Any(item => item.IsActive);

    [RelayCommand(CanExecute = nameof(CanApplyItlMetadataRepairs))]
    private async Task ApplyItlMetadataRepairs()
    {
        if (_itlMetadataRepairs is null || SelectedRun?.ItlRepairPlan is not { } plan)
            return;
        ItlMetadataRepairItemViewModel[] selected = [.. ItlRepairItems.Where(item => item.IsActive)];
        if (selected.Length == 0)
            return;
        using var scope = BeginRun("Apply iTunes metadata repairs", AnalysisResultView.ItlRepairs);
        try
        {
            var progress = new Progress<int>(done =>
                StatusText = $"Applying iTunes metadata repairs… {done:N0}/{selected.Length:N0}");
            ItlMetadataRepairApplyResult result = await _itlMetadataRepairs.ApplyAsync(
                plan, selected.Select(item => item.Item.Id).ToArray(), progress, scope.Token);
            var byId = result.Items.ToDictionary(item => item.Item.Id);
            foreach (ItlMetadataRepairItemViewModel item in selected)
            {
                if (!byId.TryGetValue(item.Item.Id, out ItlMetadataRepairItemResult? applied))
                    continue;
                item.ResultText = applied.Outcome switch
                {
                    ItlMetadataRepairOutcome.Applied => "Applied",
                    ItlMetadataRepairOutcome.Skipped => "Already correct",
                    _ => applied.Error ?? "Failed",
                };
                if (applied.Outcome is ItlMetadataRepairOutcome.Applied or ItlMetadataRepairOutcome.Skipped)
                {
                    item.IsApplied = true;
                    item.Disposition = AnalysisRepairDisposition.Completed;
                }
            }
            StatusText = $"iTunes metadata repairs: {result.Applied:N0} applied, " +
                $"{result.Skipped:N0} skipped, {result.Failed:N0} failed. A .bak backup was retained.";
        }
        catch (OperationCanceledException) { StatusText = "iTunes metadata repair apply cancelled."; }
        catch (Exception ex) { StatusText = $"iTunes metadata repair apply failed: {ex.Message}"; }
        finally { ApplyItlMetadataRepairsCommand.NotifyCanExecuteChanged(); }
    }

    [RelayCommand(CanExecute = nameof(CanApplyRepairs))]
    private async Task ApplyRepairs()
    {
        if (SelectedRun?.RepairPlan is not { } repairPlan)
            return;
        var selected = RepairItems.Where(item => item.IsActive).ToList();
        if (selected.Count == 0)
            return;
        int selectedRepairs = selected.Count;

        using var scope = BeginRun("Apply metadata repairs", AnalysisResultView.Repairs);
        try
        {
            var selectedPlan = repairPlan with { Items = selected.Select(item => item.Repair).ToList() };
            var progress = new Progress<int>(done =>
                StatusText = $"Applying metadata repairs… {done:N0}/{selectedRepairs:N0} repair(s)");
            AnalysisRepairApplyResult result =
                await _repairs.ApplyReviewedAsync(selectedPlan, progress, scope.Token);
            var byRepair = result.Items.ToDictionary(item => item.Repair);
            foreach (var item in selected)
            {
                if (!byRepair.TryGetValue(item.Repair, out AnalysisRepairItemResult? applied))
                    continue;
                item.ResultText = applied.Outcome switch
                {
                    WriteOutcome.Saved => "Applied",
                    WriteOutcome.Skipped => "Already correct",
                    _ => applied.Error ?? "Failed",
                };
                if (applied.CacheError is not null)
                    item.ResultText += $"; cache refresh failed: {applied.CacheError}";
                item.IsApplied = applied.Outcome is WriteOutcome.Saved or WriteOutcome.Skipped;
                if (item.IsApplied)
                    item.Disposition = AnalysisRepairDisposition.Completed;
            }
            StatusText = $"Metadata repairs: {result.Summary}.";
            var changed = result.Items
                .Where(item => item.Outcome == WriteOutcome.Saved)
                .Select(item => item.AppliedPath ?? item.Repair.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (changed.Count > 0)
                RepairsApplied?.Invoke(changed);
        }
        catch (OperationCanceledException) { StatusText = "Metadata repair apply cancelled."; }
        catch (Exception ex) { StatusText = $"Metadata repair apply failed: {ex.Message}"; }
        finally { ApplyRepairsCommand.NotifyCanExecuteChanged(); }
    }

    private bool CanApplyRepresentationRepairs() =>
        !IsBusy &&
        _representationRepairs is not null &&
        RepresentationActionItems.Any(item => item.IsActive);

    [RelayCommand(CanExecute = nameof(CanApplyRepresentationRepairs))]
    private async Task ApplyRepresentationRepairs()
    {
        if (_representationRepairs is null)
            return;
        var active = RepresentationActionItems.Where(item => item.IsActive).ToList();
        if (active.Count == 0)
            return;

        using var scope = BeginRun(
            "Apply representation file repairs",
            AnalysisResultView.RepresentationRepairs);
        try
        {
            var progress = new Progress<RepresentationRepairProgress>(value =>
                StatusText =
                    $"Applying representation repairsâ€¦ {value.Completed:N0}/{value.Total:N0}: " +
                    Path.GetFileName(value.SourcePath));
            RepresentationRepairApplyResult result =
                await _representationRepairs.ApplyAsync(
                    active.Select(item => item.Action).ToList(),
                    _settings.Configuration,
                    progress,
                    scope.Token);

            var byAction = result.Results
                .GroupBy(item => item.Action)
                .ToDictionary(group => group.Key, group => group.Last());
            foreach (var item in active)
            {
                if (!byAction.TryGetValue(item.Action, out RepresentationRepairActionResult? applied))
                    continue;
                item.ResultText = applied.Outcome switch
                {
                    RepresentationRepairOutcome.Applied => applied.Error ?? "Applied",
                    RepresentationRepairOutcome.Skipped => "Skipped",
                    _ => applied.Error ?? "Failed",
                };
                item.IsApplied = applied.Outcome == RepresentationRepairOutcome.Applied;
                if (item.IsApplied)
                    item.Disposition = AnalysisRepairDisposition.Completed;
            }

            StatusText = result.Cancelled
                ? $"Representation repairs cancelled after {result.Applied:N0} action(s)."
                : $"Representation repairs: {result.Applied:N0} applied, " +
                  $"{result.Failed:N0} failed.";
            if (result.ChangedPaths.Count > 0)
                RepairsApplied?.Invoke(result.ChangedPaths);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Representation repair apply cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Representation repair apply failed: {ex.Message}";
        }
        finally
        {
            ApplyRepresentationRepairsCommand.NotifyCanExecuteChanged();
        }
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
        run.PropertyChanged += RunChanged;
        foreach (var action in run.RepresentationActionItems)
            action.StateChanged += () =>
                ApplyRepresentationRepairsCommand.NotifyCanExecuteChanged();
        Runs.Insert(0, run);
        OnPropertyChanged(nameof(HasRuns));
        SelectedRun = run;
        RemoveRunCommand.NotifyCanExecuteChanged();
        ClearRunsCommand.NotifyCanExecuteChanged();
        PublishFilter();
    }

    private void RunChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AnalysisRunViewModel.FilteredPaths))
            PublishFilter();
    }

    private void PublishFilter()
    {
        IReadOnlyList<string> paths = FilteredPaths;
        OnPropertyChanged(nameof(FilteredPaths));
        FilterChanged?.Invoke(paths);
    }

    private AnalysisRepairItemViewModel CreateRepairItem(AnalysisTagRepair item)
    {
        var viewModel = new AnalysisRepairItemViewModel(item);
        viewModel.StateChanged += () => ApplyRepairsCommand.NotifyCanExecuteChanged();
        return viewModel;
    }

    private bool CanRemoveRun() => !IsBusy && SelectedRun is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveRun))]
    private void RemoveRun()
    {
        if (SelectedRun is null)
            return;
        int index = Runs.IndexOf(SelectedRun);
        SelectedRun.PropertyChanged -= RunChanged;
        Runs.Remove(SelectedRun);
        SelectedRun = Runs.Count == 0 ? null : Runs[Math.Min(index, Runs.Count - 1)];
        OnPropertyChanged(nameof(HasRuns));
        PublishFilter();
        NotifyCommands();
    }

    private bool CanClearRuns() => !IsBusy && Runs.Count > 0;

    [RelayCommand(CanExecute = nameof(CanClearRuns))]
    private void ClearRuns()
    {
        foreach (AnalysisRunViewModel run in Runs)
            run.PropertyChanged -= RunChanged;
        Runs.Clear();
        SelectedRun = null;
        OnPropertyChanged(nameof(HasRuns));
        PublishFilter();
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
        ApplyRepresentationRepairsCommand.NotifyCanExecuteChanged();
        PreviewItlMetadataRepairsCommand.NotifyCanExecuteChanged();
        ApplyItlMetadataRepairsCommand.NotifyCanExecuteChanged();
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

public partial class ItlMetadataRepairItemViewModel : ViewModelBase
{
    private readonly TextDifferenceResult _difference;

    public ItlMetadataRepairItem Item { get; }
    public string Path => Item.Path;
    public string DisplayPath => Item.Path.Replace("\u00A0", "⟦NBSP⟧", StringComparison.Ordinal);
    public string Fields => string.Join(", ", Item.Differences.Select(value => value.Field));
    public string Before { get; }
    public string After { get; }
    public IReadOnlyList<TextDifferenceSegment> BeforeDifference => _difference.Before;
    public IReadOnlyList<TextDifferenceSegment> AfterDifference => _difference.After;
    public string? UnicodeDifferenceDetails => _difference.UnicodeDetails;
    public IReadOnlyList<AnalysisRepairDisposition> Dispositions { get; } =
        Enum.GetValues<AnalysisRepairDisposition>()
            .Where(value => value != AnalysisRepairDisposition.Mixed)
            .ToArray();
    public bool CanChangeDisposition => !IsApplied;
    public bool IsActive => Disposition == AnalysisRepairDisposition.Active && !IsApplied;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    private AnalysisRepairDisposition _disposition = AnalysisRepairDisposition.Ignored;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(CanChangeDisposition))]
    private bool _isApplied;

    [ObservableProperty]
    private string? _resultText;

    public event Action? StateChanged;

    public ItlMetadataRepairItemViewModel(ItlMetadataRepairItem item)
    {
        Item = item;
        Before = Format(item.Differences, difference => difference.Before);
        After = Format(item.Differences, difference => difference.After);
        _difference = TextDifference.Compare(Before, After);
    }

    partial void OnDispositionChanged(AnalysisRepairDisposition value) => StateChanged?.Invoke();
    partial void OnIsAppliedChanged(bool value) => StateChanged?.Invoke();

    private static string Format(
        IEnumerable<ItlMetadataDifference> differences,
        Func<ItlMetadataDifference, string?> select) =>
        string.Join(Environment.NewLine, differences.Select(difference =>
            $"{difference.Field}: {select(difference) ?? "(missing)"}"));
}

public sealed class ItlMetadataRepairCategoryGroupViewModel : ViewModelBase
{
    private AnalysisRepairDisposition _disposition;
    private bool _propagating;

    public string Category { get; }
    public IReadOnlyList<ItlMetadataRepairArtistGroupViewModel> Artists { get; }
    public int Count => Artists.Sum(artist => artist.Count);
    public int ActiveCount => Artists.Sum(artist => artist.ActiveCount);
    public IReadOnlyList<AnalysisRepairDisposition> Dispositions { get; } =
        Enum.GetValues<AnalysisRepairDisposition>();

    public AnalysisRepairDisposition Disposition
    {
        get => _disposition;
        set
        {
            if (value == AnalysisRepairDisposition.Mixed || _propagating)
                return;
            _propagating = true;
            foreach (ItlMetadataRepairArtistGroupViewModel artist in Artists)
                artist.Disposition = value;
            _propagating = false;
            RefreshState();
        }
    }

    private ItlMetadataRepairCategoryGroupViewModel(
        string category,
        IReadOnlyList<ItlMetadataRepairArtistGroupViewModel> artists)
    {
        Category = category;
        Artists = artists;
        foreach (ItlMetadataRepairArtistGroupViewModel artist in Artists)
            artist.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(ItlMetadataRepairArtistGroupViewModel.ActiveCount) or
                    nameof(ItlMetadataRepairArtistGroupViewModel.Disposition))
                    RefreshState();
            };
        _disposition = Aggregate();
    }

    public static IReadOnlyList<ItlMetadataRepairCategoryGroupViewModel> Build(
        IReadOnlyList<ItlMetadataRepairItemViewModel> items) =>
        items.Select(item => new
            {
                Item = item,
                Category = "Cached metadata",
                Artist = item.Item.Metadata.AlbumArtist ?? item.Item.Metadata.Artist ?? "Unknown Artist",
                Album = item.Item.Metadata.Album ?? AlbumFromPath(item.Path),
            })
            .GroupBy(entry => entry.Category, StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(category => new ItlMetadataRepairCategoryGroupViewModel(
                category.Key,
                category.GroupBy(entry => entry.Artist, StringComparer.CurrentCultureIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
                    .Select(artist => new ItlMetadataRepairArtistGroupViewModel(
                        artist.Key,
                        artist.GroupBy(entry => entry.Album, StringComparer.CurrentCultureIgnoreCase)
                            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
                            .Select(album => new ItlMetadataRepairAlbumGroupViewModel(
                                album.Key,
                                album.Select(entry => entry.Item)
                                    .Distinct()
                                    .OrderBy(item => item.Path, StringComparer.CurrentCultureIgnoreCase)
                                    .ToList()))
                            .ToList()))
                    .ToList()))
            .ToList();

    private static string AlbumFromPath(string path) =>
        Path.GetFileName(Path.GetDirectoryName(path)) is { Length: > 0 } value
            ? value
            : "Unknown Album";

    private AnalysisRepairDisposition Aggregate() =>
        AnalysisRepairCategoryGroupViewModel.Aggregate(
            Artists.Select(artist => artist.Disposition));

    private void RefreshState()
    {
        SetProperty(ref _disposition, Aggregate(), nameof(Disposition));
        OnPropertyChanged(nameof(ActiveCount));
    }
}

public sealed class ItlMetadataRepairArtistGroupViewModel : ViewModelBase
{
    private AnalysisRepairDisposition _disposition;
    private bool _propagating;
    public string Artist { get; }
    public IReadOnlyList<ItlMetadataRepairAlbumGroupViewModel> Albums { get; }
    public int Count => Albums.Sum(album => album.Count);
    public int ActiveCount => Albums.Sum(album => album.ActiveCount);
    public IReadOnlyList<AnalysisRepairDisposition> Dispositions { get; } =
        Enum.GetValues<AnalysisRepairDisposition>();
    public AnalysisRepairDisposition Disposition
    {
        get => _disposition;
        set
        {
            if (value == AnalysisRepairDisposition.Mixed || _propagating)
                return;
            _propagating = true;
            foreach (ItlMetadataRepairAlbumGroupViewModel album in Albums)
                album.Disposition = value;
            _propagating = false;
            RefreshState();
        }
    }

    public ItlMetadataRepairArtistGroupViewModel(
        string artist,
        IReadOnlyList<ItlMetadataRepairAlbumGroupViewModel> albums)
    {
        Artist = artist;
        Albums = albums;
        foreach (ItlMetadataRepairAlbumGroupViewModel album in Albums)
            album.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(ItlMetadataRepairAlbumGroupViewModel.ActiveCount) or
                    nameof(ItlMetadataRepairAlbumGroupViewModel.Disposition))
                    RefreshState();
            };
        _disposition = Aggregate();
    }

    private AnalysisRepairDisposition Aggregate() =>
        AnalysisRepairCategoryGroupViewModel.Aggregate(
            Albums.Select(album => album.Disposition));

    private void RefreshState()
    {
        SetProperty(ref _disposition, Aggregate(), nameof(Disposition));
        OnPropertyChanged(nameof(ActiveCount));
    }
}

public sealed class ItlMetadataRepairAlbumGroupViewModel : ViewModelBase
{
    private AnalysisRepairDisposition _disposition;
    private bool _propagating;
    public string Album { get; }
    public IReadOnlyList<ItlMetadataRepairItemViewModel> Items { get; }
    public int Count => Items.Count;
    public int ActiveCount => Items.Count(item => item.IsActive);
    public IReadOnlyList<AnalysisRepairDisposition> Dispositions { get; } =
        Enum.GetValues<AnalysisRepairDisposition>();
    public AnalysisRepairDisposition Disposition
    {
        get => _disposition;
        set
        {
            if (value == AnalysisRepairDisposition.Mixed || _propagating)
                return;
            _propagating = true;
            foreach (ItlMetadataRepairItemViewModel item in Items.Where(item => item.CanChangeDisposition))
                item.Disposition = value;
            _propagating = false;
            RefreshState();
        }
    }

    public ItlMetadataRepairAlbumGroupViewModel(
        string album,
        IReadOnlyList<ItlMetadataRepairItemViewModel> items)
    {
        Album = album;
        Items = items;
        foreach (ItlMetadataRepairItemViewModel item in Items)
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(ItlMetadataRepairItemViewModel.Disposition) or
                    nameof(ItlMetadataRepairItemViewModel.IsApplied) or
                    nameof(ItlMetadataRepairItemViewModel.IsActive))
                    RefreshState();
            };
        _disposition = Aggregate();
    }

    private AnalysisRepairDisposition Aggregate() =>
        AnalysisRepairCategoryGroupViewModel.Aggregate(
            Items.Select(item => item.Disposition));

    private void RefreshState()
    {
        SetProperty(ref _disposition, Aggregate(), nameof(Disposition));
        OnPropertyChanged(nameof(ActiveCount));
    }
}

public partial class AnalysisRepairItemViewModel : ViewModelBase
{
    private readonly TextDifferenceResult _difference;

    public AnalysisTagRepair Repair { get; }
    public string Path => Repair.Path;
    public string DisplayPath => ShowWhitespace(Repair.Path);
    public string Field => Repair.Kind == AnalysisRepairKind.Path
        ? "Path"
        : Repair.TargetId3Version is not null
            ? "ID3 tag version"
            : Repair.Field.ToString();
    public string Before => string.IsNullOrEmpty(Repair.Before)
        ? "(missing)"
        : ShowWhitespace(Repair.Before);
    public string After => ShowWhitespace(Repair.After);
    public IReadOnlyList<TextDifferenceSegment> BeforeDifference => _difference.Before;
    public IReadOnlyList<TextDifferenceSegment> AfterDifference => _difference.After;
    public string? UnicodeDifferenceDetails => _difference.UnicodeDetails;
    public string Reason => Repair.Reason;
    public string? BlockingReason => Repair.BlockingReason;
    public bool CanChangeDisposition => !IsApplied;
    public IReadOnlyList<AnalysisRepairDisposition> Dispositions { get; }
    public bool IsActive =>
        Repair.CanApply && Disposition == AnalysisRepairDisposition.Active && !IsApplied;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    private AnalysisRepairDisposition _disposition = AnalysisRepairDisposition.Ignored;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(CanChangeDisposition))]
    private bool _isApplied;

    [ObservableProperty] private string? _resultText;

    public event Action? StateChanged;

    public AnalysisRepairItemViewModel(AnalysisTagRepair repair)
    {
        Repair = repair;
        Dispositions = Enum.GetValues<AnalysisRepairDisposition>()
            .Where(value => value != AnalysisRepairDisposition.Mixed &&
                (repair.CanApply || value is not (AnalysisRepairDisposition.Active or
                    AnalysisRepairDisposition.Completed)))
            .ToArray();
        _difference = TextDifference.Compare(repair.Before, repair.After);
    }

    partial void OnDispositionChanged(AnalysisRepairDisposition value) => StateChanged?.Invoke();
    partial void OnIsAppliedChanged(bool value) => StateChanged?.Invoke();

    private static string ShowWhitespace(string value) =>
        value.Replace("\u00A0", "⟦NBSP⟧", StringComparison.Ordinal);
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
