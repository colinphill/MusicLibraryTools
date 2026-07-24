using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;
using MusicFileUtilities;

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
    ArtworkRepairs,
}

/// <summary>
/// Library-wide analysis. Each analysis type is run by its own button (inconsistencies, lossy files,
/// duplicates, similar artists, cross-set check); typed results are retained for the session.
/// Selecting a finding/track opens that file; similar-artist variants use review/apply dispositions.
/// Conservative and user-directed tag repairs share a preview/select/apply surface and reject
/// sources changed since preview.
/// </summary>
public partial class AnalyzerViewModel : ViewModelBase
{
    private const string ArtistThresholdPreference = "manager.health.artistSimilarityThreshold.v1";

    private readonly ILibraryService _library;
    private readonly IArtistReconciler _reconciler;
    private readonly IAnalysisRepairService _repairs;
    private readonly IDecodedAudioVerificationService? _decodedAudio;
    private readonly IRepresentationRepairService? _representationRepairs;
    private readonly IItlMetadataRepairService? _itlMetadataRepairs;
    private readonly IAppSettings _settings;
    private readonly IDialogCoordinator? _dialogs;
    private readonly IArtworkService? _artwork;
    private readonly IThumbnailService? _thumbnails;
    private readonly ILocalizationService? _localization;
    private CancellationTokenSource? _cts;
    private IReadOnlyList<TrackRecord> _representationRecords = [];
    private IReadOnlyList<DecodedAudioPair> _decodedAudioPairs = [];
    private bool _clearingFilterDispositions;
    private Stopwatch? _analysisProgressClock;
    private string? _analysisProgressStage;
    private string? _analysisProgressUnit;
    private long _analysisProgressTotal;
    private long _analysisProgressOrigin;
    private long _analysisProgressCompleted;
    private AnalysisProgress? _lastAnalysisProgress;
    private string? _statusTextKey;
    private object?[] _statusTextArguments = [];
    private long? _statusTextCount;
    private bool _statusUsesSelectedRun;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeterminateAnalysisProgress))]
    [NotifyPropertyChangedFor(nameof(HasIndeterminateAnalysisProgress))]
    private bool _isBusy;

    [ObservableProperty]
    private double _analysisProgressFraction;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeterminateAnalysisProgress))]
    [NotifyPropertyChangedFor(nameof(HasIndeterminateAnalysisProgress))]
    private bool _isAnalysisProgressIndeterminate = true;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusDiagnosticDetail))]
    private string? _statusDiagnosticDetail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusInfo))]
    [NotifyPropertyChangedFor(nameof(IsStatusSuccess))]
    [NotifyPropertyChangedFor(nameof(IsStatusWarning))]
    [NotifyPropertyChangedFor(nameof(IsStatusError))]
    [NotifyPropertyChangedFor(nameof(StatusIcon))]
    private MessageTone _statusTone = MessageTone.Info;

    [ObservableProperty]
    private AppActivityState _lastActivityState = AppActivityState.Completed;

    [ObservableProperty]
    private AnalysisResultView _activeView = AnalysisResultView.Findings;

    [ObservableProperty]
    private AnalysisRunViewModel? _selectedRun;

    /// <summary>Fuzzy-distance threshold for the similar-artist check (AnalyzeMetadata's checkartists thresh).</summary>
    [ObservableProperty]
    private double _artistThreshold = 0.2;

    private string _artistThresholdText = "0.20";

    /// <summary>
    /// Editable threshold text. An empty value intentionally means zero; incomplete or invalid
    /// numeric input leaves the last usable threshold in place until it becomes valid.
    /// </summary>
    public string ArtistThresholdText
    {
        get => _artistThresholdText;
        set
        {
            if (!SetProperty(ref _artistThresholdText, value))
                return;

            if (string.IsNullOrWhiteSpace(value))
                ArtistThreshold = 0;
            else if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture,
                         out double threshold))
                ArtistThreshold = Math.Clamp(threshold, 0, 1);
        }
    }

    partial void OnArtistThresholdChanged(double value)
    {
        _settings.SetLibraryPreference(ArtistThresholdPreference,
            Math.Clamp(value, 0, 1).ToString("R", CultureInfo.InvariantCulture));

        if (string.IsNullOrWhiteSpace(_artistThresholdText))
            return;
        if (double.TryParse(_artistThresholdText, NumberStyles.Float,
                CultureInfo.CurrentCulture, out double current) &&
            Math.Abs(current - value) < 0.0000001)
            return;

        SetProperty(ref _artistThresholdText,
            Math.Clamp(value, 0, 1).ToString("0.##", CultureInfo.CurrentCulture),
            nameof(ArtistThresholdText));
    }

    public string FfmpegPath => _settings.Configuration?.FfmpegPath ?? "ffmpeg";

    public ObservableCollection<AnalysisRunViewModel> Runs { get; } = [];
    public IReadOnlyList<LocalizedChoice<AnalysisFindingDisposition>>
        FindingDispositionChoices =>
            HealthLocalizedChoices.AllFindingDispositions;
    public IReadOnlyList<LocalizedChoice<AnalysisRepairDisposition>>
        RepairDispositionChoices =>
            HealthLocalizedChoices.AllRepairDispositions;
    public IReadOnlyList<AnalysisProblemGroupViewModel> FindingGroups => SelectedRun?.FindingGroups ?? [];
    public int FindingCount => FindingGroups.Sum(group => group.Count);
    public IReadOnlyList<DuplicateGroup> Duplicates => SelectedRun?.Duplicates ?? [];
    public IReadOnlyList<ArtistGroupViewModel> ArtistGroups => SelectedRun?.ArtistGroups ?? [];
    public int ActiveArtistVariantCount => ArtistGroups.Sum(group => group.ActiveCount);
    public int ActiveArtistTrackCount => ArtistGroups.SelectMany(group => group.Variants)
        .Where(variant => variant.IsActive)
        .SelectMany(variant => variant.Files)
        .DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase).Count();
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
    public IReadOnlyList<ArtworkRepairItemViewModel> ArtworkRepairItems =>
        SelectedRun?.ArtworkRepairItems ?? [];
    public IReadOnlyList<ArtworkRepairCategoryGroupViewModel> ArtworkRepairGroups =>
        SelectedRun?.ArtworkRepairGroups ?? [];
    public IReadOnlyList<ItlMetadataRepairItemViewModel> ItlRepairItems =>
        SelectedRun?.ItlRepairItems ?? [];
    public IReadOnlyList<ItlMetadataRepairCategoryGroupViewModel> ItlRepairGroups =>
        SelectedRun?.ItlRepairGroups ?? [];
    public IReadOnlyList<string> FilteredPaths => Runs
        .SelectMany(run => run.FilteredPaths)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    public bool HasRuns => Runs.Count > 0;
    public bool HasDeterminateAnalysisProgress => IsBusy && !IsAnalysisProgressIndeterminate;
    public bool HasIndeterminateAnalysisProgress => IsBusy && IsAnalysisProgressIndeterminate;
    public bool IsStatusInfo => StatusTone == MessageTone.Info;
    public bool IsStatusSuccess => StatusTone == MessageTone.Success;
    public bool IsStatusWarning => StatusTone == MessageTone.Warning;
    public bool IsStatusError => StatusTone == MessageTone.Error;
    public bool HasStatusDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(StatusDiagnosticDetail);
    public string StatusIcon => StatusTone switch
    {
        MessageTone.Success => "✓",
        MessageTone.Warning => "⚠",
        MessageTone.Error => "!",
        _ => "i",
    };
    public bool HasDuplicateResults => Duplicates.Count > 0;
    public bool HasArtistResults => ArtistGroups.Count > 0;
    public bool HasConflictResults => ConflictGroups.Count > 0;
    public bool HasMatrixResults => Matrices.Count > 0;
    public bool HasArtworkRepairResults => ArtworkRepairItems.Count > 0;
    public int ActiveArtworkRepairCount => ArtworkRepairItems.Count(item => item.IsActive);
    public bool IsArtworkHealthRun =>
        SelectedRun?.Kind == AnalysisRunKind.ArtworkHealth;
    public IReadOnlyList<string> DeferredArtworkPaths => IsArtworkHealthRun
        ? FindingGroups.Where(group => group.Problem == "Artwork scan deferred")
            .SelectMany(group => group.Artists)
            .SelectMany(group => group.Albums)
            .SelectMany(group => group.Findings)
            .Select(finding => finding.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
        : [];
    public int DeferredArtworkCount => DeferredArtworkPaths.Count;
    public bool HasDeferredArtwork => DeferredArtworkCount > 0;
    public string FindingsEmptyText => FindingCount == 0
        ? L("Health.Findings.Empty.NoMatches")
        : L("Health.Findings.Empty.SelectBranch");

    private object? _selectedFindingNode;
    private object? _selectedRepairNode;
    private object? _selectedRepresentationNode;
    private object? _selectedItlRepairNode;
    private object? _selectedArtworkRepairNode;

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

    public object? SelectedArtworkRepairNode
    {
        get => _selectedArtworkRepairNode;
        set
        {
            if (SetProperty(ref _selectedArtworkRepairNode, value))
                OnPropertyChanged(nameof(DisplayedArtworkRepairItems));
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

    public IReadOnlyList<ArtworkRepairItemViewModel> DisplayedArtworkRepairItems =>
        SelectedArtworkRepairNode switch
        {
            ArtworkRepairItemViewModel item => [item],
            ArtworkRepairGroupViewModel group => group.DescendantItems,
            _ => ArtworkRepairItems,
        };

    public bool CanAutomaticallySelectMixedArtwork(object? node)
    {
        IReadOnlyList<ArtworkRepairItemViewModel> items = ArtworkItemsForNode(node);
        return items.Count > 0 && items.All(item => item.Kind == ArtworkRepairKind.NormalizeAlbum);
    }

    public int AutomaticallySelectMixedArtwork(
        object node,
        ArtworkCandidateSelectionRule rule)
    {
        if (!CanAutomaticallySelectMixedArtwork(node))
            return 0;
        int activated = ArtworkItemsForNode(node)
            .Count(item => item.SelectCandidateAndActivate(rule));
        OnPropertyChanged(nameof(ActiveArtworkRepairCount));
        ApplyArtworkRepairsCommand.NotifyCanExecuteChanged();
        return activated;
    }

    public void ReportAutomaticArtworkSelection(int activated)
    {
        if (activated == 0)
            SetStatusText(
                "Health.Artwork.Selection.NoneChanged");
        else
            SetCountStatusText(
                "Health.Artwork.Selection.Changed",
                activated);
    }

    private static IReadOnlyList<ArtworkRepairItemViewModel> ArtworkItemsForNode(object? node) =>
        node switch
        {
            ArtworkRepairItemViewModel item => [item],
            ArtworkRepairGroupViewModel group => group.DescendantItems,
            _ => [],
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
    public bool ShowArtworkRepairs => ActiveView == AnalysisResultView.ArtworkRepairs;
    public bool HasDuplicateSection => ShowDuplicates || Duplicates.Count > 0;
    public bool HasArtistSection => ShowArtists || ArtistGroups.Count > 0;
    public bool HasRepairSection => ShowRepairs || RepairItems.Count > 0;
    public bool HasRepresentationSection =>
        ShowRepresentationRepairs || RepresentationActionItems.Count > 0;
    public bool HasConflictSection => ShowConflicts || ConflictGroups.Count > 0;
    public bool HasMatrixSection => ShowMatrix || Matrices.Count > 0;
    public bool HasArtworkRepairSection =>
        ShowArtworkRepairs || ArtworkRepairItems.Count > 0;
    public bool HasItlRepairSection =>
        ActiveView == AnalysisResultView.ItlRepairs || ItlRepairItems.Count > 0;
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
            AnalysisResultView.ArtworkRepairs => 8,
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
                8 => AnalysisResultView.ArtworkRepairs,
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
        IItlMetadataRepairService? itlMetadataRepairs = null,
        IDialogCoordinator? dialogs = null,
        IArtworkService? artwork = null,
        IThumbnailService? thumbnails = null,
        ILocalizationService? localization = null)
    {
        _library = library;
        _reconciler = reconciler;
        _repairs = repairs;
        _decodedAudio = decodedAudio;
        _representationRepairs = representationRepairs;
        _itlMetadataRepairs = itlMetadataRepairs;
        _settings = settings;
        _dialogs = dialogs;
        _artwork = artwork;
        _thumbnails = thumbnails;
        _localization = localization;
        HealthLocalizedChoices.Refresh(L);
        _localization?.CultureChanged += OnLocalizationCultureChanged;
        if (double.TryParse(settings.GetLibraryPreference(ArtistThresholdPreference),
                NumberStyles.Float, CultureInfo.InvariantCulture, out double storedThreshold))
        {
            _artistThreshold = Math.Clamp(storedThreshold, 0, 1);
            _artistThresholdText = _artistThreshold.ToString("0.##", CultureInfo.CurrentCulture);
        }
        SetStatusText(settings.Configuration is null
            ? "Health.Status.ChooseConfiguration"
            : "Health.Status.ChooseAnalysis");
        settings.ConfigurationChanged += (_, _) =>
        {
            if (double.TryParse(settings.GetLibraryPreference(ArtistThresholdPreference),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double threshold))
                ArtistThreshold = Math.Clamp(threshold, 0, 1);
            else
                ArtistThreshold = 0.2;
            OnPropertyChanged(nameof(FfmpegPath));
            ClearRuns();
            _representationRecords = [];
            _decodedAudioPairs = [];
            SetStatusText(settings.Configuration is null
                ? "Health.Status.ChooseConfiguration"
                : "Health.Status.ChooseAnalysis");
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
        OnPropertyChanged(nameof(ShowArtworkRepairs));
        NotifySectionVisibility();
    }

    partial void OnSelectedRunChanged(AnalysisRunViewModel? value)
    {
        SelectedFindingNode = null;
        SelectedRepairNode = null;
        SelectedRepresentationNode = null;
        SelectedItlRepairNode = null;
        SelectedArtworkRepairNode = null;
        OnPropertyChanged(nameof(FindingGroups));
        OnPropertyChanged(nameof(FindingCount));
        OnPropertyChanged(nameof(FindingsEmptyText));
        OnPropertyChanged(nameof(Duplicates));
        OnPropertyChanged(nameof(HasDuplicateResults));
        OnPropertyChanged(nameof(ArtistGroups));
        OnPropertyChanged(nameof(HasArtistResults));
        OnPropertyChanged(nameof(ActiveArtistVariantCount));
        OnPropertyChanged(nameof(ActiveArtistTrackCount));
        OnPropertyChanged(nameof(ConflictGroups));
        OnPropertyChanged(nameof(HasConflictResults));
        OnPropertyChanged(nameof(RepairItems));
        OnPropertyChanged(nameof(RepairGroups));
        OnPropertyChanged(nameof(RepresentationActionItems));
        OnPropertyChanged(nameof(RepresentationActionGroups));
        OnPropertyChanged(nameof(RepresentationWarnings));
        OnPropertyChanged(nameof(Matrices));
        OnPropertyChanged(nameof(HasMatrixResults));
        OnPropertyChanged(nameof(ArtworkRepairItems));
        OnPropertyChanged(nameof(ArtworkRepairGroups));
        OnPropertyChanged(nameof(DisplayedArtworkRepairItems));
        OnPropertyChanged(nameof(HasArtworkRepairResults));
        OnPropertyChanged(nameof(ActiveArtworkRepairCount));
        OnPropertyChanged(nameof(IsArtworkHealthRun));
        OnPropertyChanged(nameof(DeferredArtworkPaths));
        OnPropertyChanged(nameof(DeferredArtworkCount));
        OnPropertyChanged(nameof(HasDeferredArtwork));
        OnPropertyChanged(nameof(ItlRepairItems));
        OnPropertyChanged(nameof(ItlRepairGroups));
        OnPropertyChanged(nameof(DisplayedFindings));
        OnPropertyChanged(nameof(DisplayedRepairItems));
        OnPropertyChanged(nameof(DisplayedRepresentationItems));
        OnPropertyChanged(nameof(DisplayedItlRepairItems));
        NotifySectionVisibility();

        if (value is not null)
        {
            ActiveView = value.View;
            SetStatusFromRun(value);
        }
        else
        {
            SetStatusText("Health.Status.ChooseAnalysis");
        }

        NotifyCommands();
    }

    private void NotifySectionVisibility()
    {
        OnPropertyChanged(nameof(HasDuplicateSection));
        OnPropertyChanged(nameof(HasArtistSection));
        OnPropertyChanged(nameof(HasRepairSection));
        OnPropertyChanged(nameof(HasRepresentationSection));
        OnPropertyChanged(nameof(HasConflictSection));
        OnPropertyChanged(nameof(HasMatrixSection));
        OnPropertyChanged(nameof(HasArtworkRepairSection));
        OnPropertyChanged(nameof(HasItlRepairSection));
    }

    private bool CanRun() => _library.IsReady && !IsBusy;

    private AnalysisReport ApplyCurrentHealthPolicy(AnalysisReport report) =>
        _settings.Configuration is { } configuration
            ? LibraryHealthPolicyService.Default.ApplyToReport(report, configuration)
            : LibraryHealthPolicyService.Default.ApplyToReport(
                report,
                LibraryProfilePresets.Create(
                    LibraryProfilePreset.LegacyMusicLibraryTools).Health);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunInconsistencies() => RunOverRecords(
        "Health.Run.Name.Inconsistencies",
        AnalysisResultView.Findings,
        (records, progress, ct) =>
    {
        var report = ApplyCurrentHealthPolicy(LibraryAnalyzer.Inconsistencies(records, progress, ct));
        HealthRunText text = report.Count == 0
            ? RunText(
                AnalysisRunKind.Inconsistencies,
                "Health.Run.Name.Inconsistencies",
                "Health.Status.Inconsistencies.None")
            : RunText(
                AnalysisRunKind.Inconsistencies,
                "Health.Run.Name.Inconsistencies",
                "Health.Status.Inconsistencies.Findings",
                report.Count);
        string status = text.ResolveSummary(_localization);
        return (status, AnalysisRunViewModel.ForFindings(
            report,
            records,
            status,
            text,
            _localization));
    });

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunLossy() => RunOverRecords(
        "Health.Run.Name.LossyFiles",
        AnalysisResultView.Findings,
        (records, progress, ct) =>
    {
        var report = ApplyCurrentHealthPolicy(LibraryAnalyzer.Lossless(records, progress, ct));
        HealthRunText text = report.Count == 0
            ? RunText(
                AnalysisRunKind.LossyFiles,
                "Health.Run.Name.LossyFiles",
                "Health.Status.LossyFiles.None")
            : RunText(
                AnalysisRunKind.LossyFiles,
                "Health.Run.Name.LossyFiles",
                "Health.Status.LossyFiles.Count",
                report.Count);
        string status = text.ResolveSummary(_localization);
        return (status, AnalysisRunViewModel.ForFindings(
            report,
            records,
            status,
            text,
            _localization));
    });

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunDuplicates() => RunOverRecords(
        "Health.Run.Name.Duplicates",
        AnalysisResultView.Duplicates,
        (records, progress, ct) =>
    {
        var dupes = _settings.Configuration is { } configuration
            ? DuplicateFinder.Find(records, configuration, progress, ct)
            : DuplicateFinder.Find(records, progress, ct);
        HealthRunText text = dupes.Count == 0
            ? RunText(
                AnalysisRunKind.Duplicates,
                "Health.Run.Name.Duplicates",
                "Health.Status.Duplicates.None")
            : RunText(
                AnalysisRunKind.Duplicates,
                "Health.Run.Name.Duplicates",
                "Health.Status.Duplicates.Groups",
                dupes.Count);
        string status = text.ResolveSummary(_localization);
        return (status, AnalysisRunViewModel.ForDuplicates(
            text.ResolveName(_localization),
            dupes,
            status,
            text,
            _localization));
    });

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunSimilarArtists() => RunOverRecords(
        "Health.Run.Name.SimilarArtists",
        AnalysisResultView.Artists,
        (records, progress, ct) =>
    {
        var groups = _reconciler.FindSimilarArtists(records, ArtistThreshold, progress, ct);
        HealthRunText text = groups.Count == 0
            ? RunText(
                AnalysisRunKind.SimilarArtists,
                "Health.Run.Name.SimilarArtists",
                "Health.Status.SimilarArtists.None",
                null,
                ArtistThreshold)
            : RunText(
                AnalysisRunKind.SimilarArtists,
                "Health.Run.Name.SimilarArtists",
                "Health.Status.SimilarArtists.Clusters",
                groups.Count,
                ArtistThreshold);
        string status = text.ResolveSummary(_localization);
        return (status, AnalysisRunViewModel.ForArtists(
            text.ResolveName(_localization),
            groups.Select(group => new ArtistGroupViewModel(group)).ToList(),
            status,
            text,
            _localization));
    });

    private bool CanApplySimilarArtists() => !IsBusy && ArtistGroups.Any(group =>
        group.HasCanonicalName && group.Variants.Any(variant => variant.IsActive));

    [RelayCommand(CanExecute = nameof(CanApplySimilarArtists))]
    private async Task ApplySimilarArtists()
    {
        var selected = ArtistGroups
            .Where(group => group.HasCanonicalName)
            .SelectMany(group => group.Variants
                .Where(variant => variant.IsActive)
                .Select(variant => (Group: group, Variant: variant)))
            .ToList();
        if (selected.Count == 0)
            return;

        int selectedTracks = selected.SelectMany(action => action.Variant.Files)
            .DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase).Count();
        int selectedGroups = selected.Select(action => action.Group).Distinct().Count();
        if (_dialogs is not null && !await _dialogs.ConfirmAsync(
                L("Health.Dialog.ArtistMerge.Title"),
                LF(
                    "Health.Dialog.ArtistMerge.Message",
                    selected.Count,
                    selectedTracks,
                    selectedGroups),
                L("Health.Dialog.ArtistMerge.Confirm")))
            return;

        using var scope = BeginRun(
            "Health.Operation.ApplySimilarArtists",
            AnalysisResultView.Artists);
        var changedPaths = new List<string>();
        int changed = 0;
        int failed = 0;
        int processed = 0;
        try
        {
            foreach (var action in selected)
            {
                scope.Token.ThrowIfCancellationRequested();
                string canonical = action.Group.CanonicalName.Trim();
                SetStatusText(
                    "Health.Progress.ApplyingSimilarArtists",
                    processed,
                    selected.Count);
                try
                {
                    int variantChanged = await _reconciler.RenameArtistAsync(
                        action.Variant.Files.Select(file => file.Path).ToArray(),
                        action.Variant.Name,
                        canonical,
                        ct: scope.Token);
                    changed += variantChanged;
                    if (variantChanged > 0)
                        changedPaths.AddRange(action.Variant.Files.Select(file => file.Path));
                    action.Variant.ResultText = variantChanged == 0
                        ? L("Health.Result.AlreadyCorrect")
                        : LC(
                            "Health.Result.FilesRenamed",
                            variantChanged);
                    action.Variant.ResultDiagnosticDetail = null;
                    action.Variant.IsApplied = true;
                    action.Variant.Disposition = AnalysisRepairDisposition.Completed;
                }
                catch (OperationCanceledException) when (scope.Token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    action.Variant.ResultText = L(
                        "Health.Result.Failed");
                    action.Variant.ResultDiagnosticDetail =
                        ex.Message;
                }
                processed++;
            }

            SetStatusText(
                "Health.Status.ArtistMerge.Completed",
                changed,
                selected.Count - failed,
                failed);
            scope.Complete(failed > 0 ? MessageTone.Warning : MessageTone.Success);
            if (changedPaths.Count > 0)
                RepairsApplied?.Invoke(changedPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }
        catch (OperationCanceledException)
        {
            scope.Cancel();
            SetStatusText(
                "Health.Status.ArtistMerge.Cancelled");
        }
        finally
        {
            ApplySimilarArtistsCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunAlbumMatrix() => RunOverRecords(
        "Health.Run.Name.AlbumMetadataMatrix",
        AnalysisResultView.Matrix,
        (records, progress, ct) =>
    {
        var matrices = _settings.Configuration is { } configuration
            ? AlbumMetadataMatrixBuilder.Build(records, configuration, progress, ct)
            : AlbumMetadataMatrixBuilder.Build(records, progress, ct);
        HealthRunText text = matrices.Count == 0
            ? RunText(
                AnalysisRunKind.AlbumMetadataMatrix,
                "Health.Run.Name.AlbumMetadataMatrix",
                "Health.Status.AlbumMatrix.None")
            : RunText(
                AnalysisRunKind.AlbumMetadataMatrix,
                "Health.Run.Name.AlbumMetadataMatrix",
                "Health.Status.AlbumMatrix.Results",
                matrices.Count,
                matrices.Sum(matrix => matrix.InconsistentCellCount));
        string status = text.ResolveSummary(_localization);
        return (status, AnalysisRunViewModel.ForMatrices(
            matrices,
            status,
            text,
            _localization));
    });

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunArtworkHealth()
    {
        using var scope = BeginRun(
            "Health.Run.Name.ArtworkHealth",
            AnalysisResultView.ArtworkRepairs);
        try
        {
            var records = await _library.GetAllRecordsAsync(scope.Token);
            var artwork = await _library.GetArtworkAuditFilesAsync(scope.Token);
            AnalysisRunViewModel run = await BuildArtworkHealthRunAsync(
                records, artwork, GetArtworkHealthSettings(), CreateAnalysisProgress(), scope.Token);
            AddRun(run);
        }
        catch (OperationCanceledException)
        {
            scope.Cancel();
            SetStatusText("Health.Status.ArtworkHealth.Cancelled");
        }
        catch (Exception ex)
        {
            scope.Fail();
            SetStatusFailure(
                "Health.Status.ArtworkHealth.Failed",
                ex.Message);
        }
    }

    private LibraryArtworkHealthSettings GetArtworkHealthSettings() =>
        _settings.GetSnapshot().Configuration?.ArtworkHealthSettings ??
        new LibraryArtworkHealthSettings(
            LibraryArtworkHealthSettings.DefaultOversizedByteThreshold,
            LibraryArtworkHealthSettings.DefaultOversizedDimensionThreshold);

    private async Task<AnalysisRunViewModel> BuildArtworkHealthRunAsync(
        IReadOnlyList<TrackRecord> records,
        IReadOnlyList<ArtworkAuditFile> artwork,
        LibraryArtworkHealthSettings settings,
        IProgress<AnalysisProgress> progress,
        CancellationToken ct)
    {
        LibraryConfiguration? configuration = _settings.GetSnapshot().Configuration;
        AnalysisReport report = await Task.Run(() => ArtworkHealthAnalyzer.Analyze(
            records, artwork, configuration, settings.OversizedByteThreshold,
            settings.OversizedDimensionThreshold, progress, ct), ct);
        IReadOnlyList<ArtworkRepairItemViewModel> repairs = _artwork is null
            ? []
            : await Task.Run(() => ArtworkRepairPlanner.BuildAsync(
                records, artwork, settings, _library, _artwork, _thumbnails,
                configuration, progress, ct), ct);
        int deferred = report.Findings.Count(finding =>
            finding.Problem == "Artwork scan deferred");
        int actionable = report.Count - deferred;
        HealthRunText text = report.Count == 0
            ? RunText(
                AnalysisRunKind.ArtworkHealth,
                "Health.Run.Name.ArtworkHealth",
                "Health.Status.ArtworkHealth.None")
            : RunText(
                AnalysisRunKind.ArtworkHealth,
                "Health.Run.Name.ArtworkHealth",
                "Health.Status.ArtworkHealth.Results",
                null,
                actionable,
                repairs.Count,
                deferred);
        string status = text.ResolveSummary(_localization);
        return await Task.Run(
            () => AnalysisRunViewModel.ForArtwork(
                report,
                records,
                repairs,
                status,
                progress,
                ct,
                text,
                _localization),
            ct);
    }

    private bool CanForceScanDeferredArtwork() =>
        !IsBusy && _library.IsReady && DeferredArtworkCount > 0;

    [RelayCommand(CanExecute = nameof(CanForceScanDeferredArtwork))]
    private async Task ForceScanDeferredArtwork()
    {
        string[] paths = DeferredArtworkPaths.ToArray();
        if (paths.Length == 0)
            return;

        using var scope = BeginRun(
            "Health.Operation.ForceScanDeferredArtwork",
            AnalysisResultView.ArtworkRepairs);
        try
        {
            SetCountStatusText(
                "Health.Progress.ReadingDeferredArtwork",
                paths.Length);
            _ = await _library.GetImageSignaturesAsync(paths, scope.Token);
            var records = await _library.GetAllRecordsAsync(scope.Token);
            var artwork = await _library.GetArtworkAuditFilesAsync(scope.Token);
            AnalysisRunViewModel run = await BuildArtworkHealthRunAsync(
                records, artwork, GetArtworkHealthSettings(), CreateAnalysisProgress(), scope.Token);
            AddRun(run);
            SetStatusFromRun(run);
        }
        catch (OperationCanceledException)
        {
            scope.Cancel();
            SetStatusText(
                "Health.Status.DeferredArtwork.Cancelled");
        }
        catch (Exception ex)
        {
            scope.Fail();
            SetStatusFailure(
                "Health.Status.DeferredArtwork.Failed",
                ex.Message);
        }
    }

    private bool CanApplyArtworkRepairs() =>
        !IsBusy && _artwork is not null && ArtworkRepairItems.Any(item => item.IsActive);

    [RelayCommand(CanExecute = nameof(CanApplyArtworkRepairs))]
    private async Task ApplyArtworkRepairs()
    {
        if (_artwork is null)
            return;
        ArtworkRepairItemViewModel[] selected = ArtworkRepairItems
            .Where(item => item.IsActive).ToArray();
        if (selected.Length == 0)
            return;
        int fileCount = selected.SelectMany(item => item.AffectedPaths)
            .Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (_dialogs is not null && !await _dialogs.ConfirmAsync(
                L("Health.Dialog.ArtworkRepair.Title"),
                LF(
                    "Health.Dialog.ArtworkRepair.Message",
                    fileCount,
                    selected.Length),
                L("Health.Dialog.ArtworkRepair.Confirm")))
            return;

        using var scope = BeginRun(
            "Health.Operation.ApplyArtworkRepairs",
            AnalysisResultView.ArtworkRepairs);
        var changedPaths = new List<string>();
        int completed = 0;
        int failed = 0;
        try
        {
            foreach (ArtworkRepairItemViewModel item in selected)
            {
                scope.Token.ThrowIfCancellationRequested();
                SetStatusText(
                    "Health.Progress.ApplyingArtworkRepairs",
                    completed + failed,
                    selected.Length);
                PreparedImage? prepared = await PrepareArtworkWithinLimitsAsync(item, scope.Token);
                if (prepared is null)
                {
                    failed++;
                    item.ResultText = L(
                        "Health.Result.ArtworkEncodingFailed");
                    item.ResultDiagnosticDetail = null;
                    continue;
                }

                var itemFailures = new List<string>();
                foreach (ArtistPathViewModel target in item.AffectedPaths)
                {
                    scope.Token.ThrowIfCancellationRequested();
                    ArtworkOpResult result = await _artwork.SaveImagesAsync(target.Path,
                        [new ArtworkInput(ID3v2Util.APICType.FrontCover,
                            prepared.MimeType, prepared.Data)], scope.Token);
                    if (result.Success)
                        changedPaths.Add(target.Path);
                    else
                        itemFailures.Add(LF(
                            "Health.Diagnostic.FileError",
                            Path.GetFileName(target.Path),
                            result.Error ??
                            L("Health.Common.UnknownError")));
                }

                if (itemFailures.Count == 0)
                {
                    completed++;
                    item.IsApplied = true;
                    item.Disposition = AnalysisRepairDisposition.Completed;
                    item.ResultText = LF(
                        "Health.Result.ArtworkCompleted",
                        item.FileCount,
                        prepared.Width,
                        prepared.Height,
                        prepared.Data.Length / 1024d);
                    item.ResultDiagnosticDetail = null;
                }
                else
                {
                    failed++;
                    item.ResultText = LC(
                        "Health.Result.ArtworkFilesFailed",
                        itemFailures.Count);
                    item.ResultDiagnosticDetail =
                        string.Join("; ", itemFailures.Take(3));
                }
            }

            SetStatusText(
                "Health.Status.ArtworkRepair.Completed",
                completed,
                failed);
            scope.Complete(failed > 0 ? MessageTone.Warning : MessageTone.Success);
            if (changedPaths.Count > 0)
                RepairsApplied?.Invoke(changedPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }
        catch (OperationCanceledException)
        {
            scope.Cancel();
            SetStatusText(
                "Health.Status.ArtworkRepair.Cancelled");
        }
        finally
        {
            OnPropertyChanged(nameof(ActiveArtworkRepairCount));
            ApplyArtworkRepairsCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task<PreparedImage?> PrepareArtworkWithinLimitsAsync(
        ArtworkRepairItemViewModel item, CancellationToken ct)
    {
        if (_artwork is null || item.SelectedCandidate is null)
            return null;
        byte[]? sourceData = await item.SelectedCandidate.EnsureDataAsync(ct);
        if (sourceData is null || sourceData.Length == 0)
            return null;
        int dimension = item.MaximumDimension;
        while (dimension >= 64)
        {
            foreach (int quality in new[] { 90, 80, 70, 60, 50, 40, 30 })
            {
                PreparedImage? prepared = await _artwork.PrepareFromBytesAsync(
                    sourceData, dimension, quality, ct);
                if (prepared is not null && prepared.Data.Length <= item.MaximumBytes &&
                    prepared.Width <= item.MaximumDimension &&
                    prepared.Height <= item.MaximumDimension)
                    return prepared;
            }
            dimension = (int)Math.Floor(dimension * 0.8);
        }
        return null;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunRepresentations()
    {
        using var scope = BeginRun(
            "Health.Run.Name.AlbumRepresentations",
            AnalysisResultView.Findings);
        try
        {
            var records = await _library.GetAllRecordsAsync(scope.Token);
            LibraryConfiguration? configuration = _settings.Configuration;
            IProgress<AnalysisProgress> progress = CreateAnalysisProgress();
            var prepared = await Task.Run(() => (
                Pairs: configuration is null
                    ? RepresentationAnalyzer.DecodedAudioCandidatePairs(records)
                    : RepresentationAnalyzer.DecodedAudioCandidatePairs(records, configuration),
                Report: configuration is null
                    ? RepresentationAnalyzer.Compare(records, progress, scope.Token)
                    : RepresentationAnalyzer.Compare(records, configuration, progress, scope.Token),
                ArtworkPaths: configuration is null
                    ? RepresentationAnalyzer.ArtworkCandidatePaths(records)
                    : RepresentationAnalyzer.ArtworkCandidatePaths(records, configuration)),
                scope.Token);
            _representationRecords = records;
            _decodedAudioPairs = prepared.Pairs;
            var artworkPaths = prepared.ArtworkPaths;
            IReadOnlyList<AnalysisFinding> artworkFindings = [];
            string? artworkDiagnostic = null;
            if (artworkPaths.Count > 0)
            {
                SetCountStatusText(
                    "Health.Progress.ComparingEmbeddedArtwork",
                    artworkPaths.Count);
                try
                {
                    var signatures = await _library.GetImageSignaturesAsync(artworkPaths, scope.Token);
                    artworkFindings = await Task.Run(() =>
                    {
                        var signatureMap = artworkPaths.Zip(signatures)
                            .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.OrdinalIgnoreCase);
                        return (configuration is null
                            ? RepresentationAnalyzer.CompareArtwork(records, signatureMap)
                            : RepresentationAnalyzer.CompareArtwork(
                                records, signatureMap, configuration)).Findings;
                    }, scope.Token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    artworkDiagnostic = ex.Message;
                    artworkFindings = [new(artworkPaths[0],
                        L("Health.Finding.ArtworkComparisonUnavailable.Description"),
                        L("Health.Finding.ArtworkComparisonUnavailable.Problem"))];
                }
            }
            var result = await Task.Run(() =>
            {
                var combined = new AnalysisReport(prepared.Report.Name,
                    [.. prepared.Report.Findings, .. artworkFindings]);
                HealthRunText text = combined.Count == 0
                    ? RunText(
                        AnalysisRunKind.AlbumRepresentations,
                        "Health.Run.Name.AlbumRepresentations",
                        "Health.Status.AlbumRepresentations.None")
                    : RunText(
                        AnalysisRunKind.AlbumRepresentations,
                        "Health.Run.Name.AlbumRepresentations",
                        "Health.Status.AlbumRepresentations.Findings",
                        combined.Count);
                string status = text.ResolveSummary(_localization);
                return (
                    Status: status,
                    Run: AnalysisRunViewModel.ForFindings(
                        combined,
                        records,
                        status,
                        text,
                        _localization,
                        artworkDiagnostic));
            }, scope.Token);
            AddRun(result.Run);
        }
        catch (OperationCanceledException)
        {
            scope.Cancel();
            SetStatusText(
                "Health.Status.AlbumRepresentations.Cancelled");
        }
        catch (Exception ex)
        {
            scope.Fail();
            SetStatusFailure(
                "Health.Status.AlbumRepresentations.Failed",
                ex.Message);
        }
        finally { VerifyDecodedAudioCommand.NotifyCanExecuteChanged(); }
    }

    private bool CanVerifyDecodedAudio() => !IsBusy && _decodedAudio is not null &&
        _decodedAudioPairs.Count > 0 && !string.IsNullOrWhiteSpace(FfmpegPath);

    [RelayCommand(CanExecute = nameof(CanVerifyDecodedAudio))]
    private async Task VerifyDecodedAudio()
    {
        if (_decodedAudio is null || _decodedAudioPairs.Count == 0)
            return;
        using var scope = BeginRun(
            "Health.Run.Name.DecodedAudioVerification",
            AnalysisResultView.Findings);
        try
        {
            var progress = new Progress<DecodedAudioProgress>(item =>
                ReportAnalysisProgress(new(
                    item.CompletedFiles,
                    item.TotalFiles,
                    "Health.Progress.Unit.Files",
                    "Health.Progress.Stage.DecodingAudio",
                    item.Path)));
            var report = await _decodedAudio.VerifyAsync(
                FfmpegPath, _decodedAudioPairs, progress, scope.Token);
            HealthRunText text = report.Count == 0
                ? RunText(
                    AnalysisRunKind.DecodedAudioVerification,
                    "Health.Run.Name.DecodedAudioVerification",
                    "Health.Status.DecodedAudioVerification.Match",
                    _decodedAudioPairs.Count)
                : RunText(
                    AnalysisRunKind.DecodedAudioVerification,
                    "Health.Run.Name.DecodedAudioVerification",
                    "Health.Status.DecodedAudioVerification.Differ",
                    report.Count);
            string status = text.ResolveSummary(_localization);
            var run = await Task.Run(
                () => AnalysisRunViewModel.ForFindings(
                    report,
                    _representationRecords,
                    status,
                    text,
                    _localization),
                scope.Token);
            AddRun(run);
        }
        catch (OperationCanceledException)
        {
            scope.Cancel();
            SetStatusText(
                "Health.Status.DecodedAudioVerification.Cancelled");
        }
        catch (Exception ex)
        {
            scope.Fail();
            SetStatusFailure(
                "Health.Status.DecodedAudioVerification.Failed",
                ex.Message);
        }
    }

    private bool CanPreviewRepresentationRepairs() => CanRun() && _representationRepairs is not null;

    [RelayCommand(CanExecute = nameof(CanPreviewRepresentationRepairs))]
    private async Task PreviewRepresentationRepairs()
    {
        if (_representationRepairs is null)
            return;
        using var scope = BeginRun(
            "Health.Operation.PreviewRepresentationRepairs",
            AnalysisResultView.RepresentationRepairs);
        try
        {
            var records = await _library.GetAllRecordsAsync(scope.Token);
            IProgress<AnalysisProgress> progress = CreateAnalysisProgress();
            var preview = await Task.Run(
                () => _representationRepairs.PreviewAsync(
                    records, _settings.Configuration, progress, scope.Token),
                scope.Token);
            progress.Report(new(
                0,
                0,
                "Health.Progress.Unit.Results",
                "Health.Progress.Stage.PreparingRepresentationRepairResults"));
            var runs = await Task.Run(() =>
            {
                var projected = new List<AnalysisRunViewModel>(2);
                if (preview.FileActions.Count > 0 || preview.Warnings.Count > 0)
                {
                    HealthRunText actionText = RunText(
                        AnalysisRunKind.RepresentationFileRepairs,
                        "Health.Run.Name.RepresentationFileRepairs",
                        "Health.Status.RepresentationRepairPreview.FileActions",
                        null,
                        preview.FileActions.Count,
                        preview.Warnings.Count);
                    string actionStatus =
                        actionText.ResolveSummary(_localization);
                    projected.Add(AnalysisRunViewModel.ForRepresentationRepairs(
                        preview.FileActions,
                        preview.Warnings,
                        records,
                        actionStatus,
                        actionText,
                        _localization));
                }

                if (preview.MetadataCopies.Items.Count > 0)
                {
                    var items = preview.MetadataCopies.Items.Select(CreateRepairItem).ToList();
                    HealthRunText metadataText = RunText(
                        AnalysisRunKind.RepresentationMetadataRepairs,
                        "Health.Run.Name.RepresentationMetadataRepairs",
                        "Health.Status.RepresentationRepairPreview.MetadataCopies",
                        items.Count);
                    string metadataStatus =
                        metadataText.ResolveSummary(_localization);
                    projected.Add(AnalysisRunViewModel.ForRepairs(
                        preview.MetadataCopies,
                        items,
                        records,
                        metadataStatus,
                        metadataText,
                        _localization));
                }
                return projected;
            }, scope.Token);

            foreach (var run in runs)
                AddRun(run);
            if (runs.Count == 0)
                SetStatusText(
                    "Health.Status.RepresentationRepairPreview.None");
        }
        catch (OperationCanceledException)
        {
            scope.Cancel();
            SetStatusText(
                "Health.Status.RepresentationRepairPreview.Cancelled");
        }
        catch (Exception ex)
        {
            scope.Fail();
            SetStatusFailure(
                "Health.Status.RepresentationRepairPreview.Failed",
                ex.Message);
        }
        finally
        {
            ApplyRepairsCommand.NotifyCanExecuteChanged();
            ApplyRepresentationRepairsCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task PreviewMetadataRepairs()
    {
        using var scope = BeginRun(
            "Health.Operation.PreviewMetadataRepairs",
            AnalysisResultView.Repairs);
        try
        {
            var records = await _library.GetAllRecordsAsync(scope.Token);
            var run = await Task.Run(() =>
            {
                AnalysisRepairPlan preview = _settings.Configuration is { } configuration
                    ? _repairs.PreviewSafeRepairs(records, configuration)
                    : _repairs.PreviewSafeRepairs(
                        records,
                        LibraryProfilePresets.Create(
                            LibraryProfilePreset.LegacyMusicLibraryTools).Health);
                AnalysisRepairPlan plan = StampPolicy(preview);
                var repairItems = plan.Items.Select(CreateRepairItem).ToList();
                int applicable = plan.Items.Count(item => item.CanApply);
                HealthRunText text = plan.Items.Count == 0
                    ? RunText(
                        AnalysisRunKind.MetadataRepairs,
                        "Health.Run.Name.MetadataRepairs",
                        "Health.Status.MetadataRepairPreview.None")
                    : applicable == 0
                        ? RunText(
                            AnalysisRunKind.MetadataRepairs,
                            "Health.Run.Name.MetadataRepairs",
                            "Health.Status.MetadataRepairPreview.Blocked",
                            plan.Items.Count)
                        : RunText(
                            AnalysisRunKind.MetadataRepairs,
                            "Health.Run.Name.MetadataRepairs",
                            "Health.Status.MetadataRepairPreview.Results",
                            plan.Items.Count,
                            applicable);
                string status = text.ResolveSummary(_localization);
                return AnalysisRunViewModel.ForRepairs(
                    plan,
                    repairItems,
                    records,
                    status,
                    text,
                    _localization);
            }, scope.Token);
            AddRun(run);
        }
        catch (OperationCanceledException)
        {
            scope.Cancel();
            SetStatusText(
                "Health.Status.MetadataRepairPreview.Cancelled");
        }
        catch (Exception ex)
        {
            scope.Fail();
            SetStatusFailure(
                "Health.Status.MetadataRepairPreview.Failed",
                ex.Message);
        }
        finally { ApplyRepairsCommand.NotifyCanExecuteChanged(); }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task FindAlbumArtistConflicts()
    {
        using var scope = BeginRun(
            "Health.Run.Name.AlbumArtistConflicts",
            AnalysisResultView.Conflicts);
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
                HealthRunText text = groups.Count == 0
                    ? RunText(
                        AnalysisRunKind.AlbumArtistConflicts,
                        "Health.Run.Name.AlbumArtistConflicts",
                        "Health.Status.AlbumArtistConflicts.None")
                    : RunText(
                        AnalysisRunKind.AlbumArtistConflicts,
                        "Health.Run.Name.AlbumArtistConflicts",
                        "Health.Status.AlbumArtistConflicts.Results",
                        groups.Count);
                string status = text.ResolveSummary(_localization);
                return AnalysisRunViewModel.ForConflicts(
                    groups,
                    status,
                    text,
                    _localization);
            }, scope.Token);
            AddRun(run);
        }
        catch (OperationCanceledException)
        {
            scope.Cancel();
            SetStatusText(
                "Health.Status.AlbumArtistConflicts.Cancelled");
        }
        catch (Exception ex)
        {
            scope.Fail();
            SetStatusFailure(
                "Health.Status.AlbumArtistConflicts.Failed",
                ex.Message);
        }
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
        using var scope = BeginRun(
            "Health.Operation.PreviewConflictRepairs",
            AnalysisResultView.Repairs);
        try
        {
            var records = await _library.GetAllRecordsAsync(scope.Token);
            var run = await Task.Run(() =>
            {
                AnalysisRepairPlan plan = StampPolicy(
                    _repairs.PreviewConflictRepairs(resolutions));
                var items = plan.Items.Select(CreateRepairItem).ToList();
                HealthRunText text = plan.Items.Count == 0
                    ? RunText(
                        AnalysisRunKind.ConflictRepairs,
                        "Health.Run.Name.ConflictRepairs",
                        "Health.Status.ConflictRepairPreview.None")
                    : RunText(
                        AnalysisRunKind.ConflictRepairs,
                        "Health.Run.Name.ConflictRepairs",
                        "Health.Status.ConflictRepairPreview.Results",
                        plan.Items.Count);
                string status = text.ResolveSummary(_localization);
                return AnalysisRunViewModel.ForRepairs(
                    plan,
                    items,
                    records,
                    status,
                    text,
                    _localization);
            }, scope.Token);
            AddRun(run);
        }
        catch (OperationCanceledException)
        {
            scope.Cancel();
            SetStatusText(
                "Health.Status.ConflictRepairPreview.Cancelled");
        }
        catch (Exception ex)
        {
            scope.Fail();
            SetStatusFailure(
                "Health.Status.ConflictRepairPreview.Failed",
                ex.Message);
        }
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
        using var scope = BeginRun(
            "Health.Operation.PreviewItlMetadataRepairs",
            AnalysisResultView.ItlRepairs);
        try
        {
            var progress = new Progress<OperationProgress>(value =>
                SetProviderProgressStatus(
                    value,
                    "Health.Progress.PreparingItlMetadataRepairs"));
            ItlMetadataRepairPlan plan = await _itlMetadataRepairs.PreviewAsync(
                progress: progress, ct: scope.Token);
            var items = plan.Items.Select(item =>
            {
                var viewModel = new ItlMetadataRepairItemViewModel(item);
                viewModel.StateChanged += () => ApplyItlMetadataRepairsCommand.NotifyCanExecuteChanged();
                return viewModel;
            }).ToList();
            HealthRunText text = items.Count == 0
                ? RunText(
                    AnalysisRunKind.ItlMetadataRepairs,
                    "Health.Run.Name.ItlMetadataRepairs",
                    "Health.Status.ItlMetadataRepairPreview.None")
                : RunText(
                    AnalysisRunKind.ItlMetadataRepairs,
                    "Health.Run.Name.ItlMetadataRepairs",
                    "Health.Status.ItlMetadataRepairPreview.Results",
                    items.Count);
            string status = text.ResolveSummary(_localization);
            AddRun(AnalysisRunViewModel.ForItlRepairs(
                plan,
                items,
                status,
                text,
                _localization));
        }
        catch (OperationCanceledException)
        {
            scope.Cancel();
            SetStatusText(
                "Health.Status.ItlMetadataRepairPreview.Cancelled");
        }
        catch (Exception ex)
        {
            scope.Fail();
            SetStatusFailure(
                "Health.Status.ItlMetadataRepairPreview.Failed",
                ex.Message);
        }
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
        if (_dialogs is not null && !await _dialogs.ConfirmAsync(
                L("Health.Dialog.ItlRepair.Title"),
                LF(
                    "Health.Dialog.ItlRepair.Message",
                    selected.Length),
                L("Health.Dialog.ApplyRepairs")))
            return;
        using var scope = BeginRun(
            "Health.Operation.ApplyItlMetadataRepairs",
            AnalysisResultView.ItlRepairs);
        try
        {
            var progress = new Progress<int>(done =>
                SetStatusText(
                    "Health.Progress.ApplyingItlMetadataRepairs",
                    done,
                    selected.Length));
            ItlMetadataRepairApplyResult result = await _itlMetadataRepairs.ApplyAsync(
                plan, selected.Select(item => item.Item.Id).ToArray(), progress, scope.Token);
            var byId = result.Items.ToDictionary(item => item.Item.Id);
            foreach (ItlMetadataRepairItemViewModel item in selected)
            {
                if (!byId.TryGetValue(item.Item.Id, out ItlMetadataRepairItemResult? applied))
                    continue;
                item.ResultText = applied.Outcome switch
                {
                    ItlMetadataRepairOutcome.Applied =>
                        L("Health.Result.Applied"),
                    ItlMetadataRepairOutcome.Skipped =>
                        L("Health.Result.AlreadyCorrect"),
                    _ => L("Health.Result.Failed"),
                };
                item.ResultDiagnosticDetail =
                    applied.Outcome == ItlMetadataRepairOutcome.Failed
                        ? applied.Error
                        : null;
                if (applied.Outcome is ItlMetadataRepairOutcome.Applied or ItlMetadataRepairOutcome.Skipped)
                {
                    item.IsApplied = true;
                    item.Disposition = AnalysisRepairDisposition.Completed;
                }
            }
            SetStatusText(
                "Health.Status.ItlMetadataRepair.Completed",
                result.Applied,
                result.Skipped,
                result.Failed);
            scope.Complete(result.Failed > 0 ? MessageTone.Warning : MessageTone.Success);
        }
        catch (OperationCanceledException)
        {
            scope.Cancel();
            SetStatusText(
                "Health.Status.ItlMetadataRepair.Cancelled");
        }
        catch (Exception ex)
        {
            scope.Fail();
            SetStatusFailure(
                "Health.Status.ItlMetadataRepair.Failed",
                ex.Message);
        }
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
        if (_dialogs is not null && !await _dialogs.ConfirmAsync(
                L("Health.Dialog.MetadataRepair.Title"),
                LF(
                    "Health.Dialog.MetadataRepair.Message",
                    selectedRepairs),
                L("Health.Dialog.ApplyRepairs")))
            return;

        using var scope = BeginRun(
            "Health.Operation.ApplyMetadataRepairs",
            AnalysisResultView.Repairs);
        try
        {
            var selectedPlan = repairPlan with { Items = selected.Select(item => item.Repair).ToList() };
            var progress = new Progress<int>(done =>
                SetStatusText(
                    "Health.Progress.ApplyingMetadataRepairs",
                    done,
                    selectedRepairs));
            AnalysisRepairApplyResult result = _settings.Configuration is { } configuration
                ? await _repairs.ApplyReviewedAsync(
                    selectedPlan, configuration, progress, scope.Token)
                : await _repairs.ApplyReviewedAsync(
                    selectedPlan,
                    LibraryProfilePresets.Create(
                        LibraryProfilePreset.LegacyMusicLibraryTools).Health,
                    progress,
                    scope.Token);
            var byRepair = result.Items.ToDictionary(item => item.Repair);
            foreach (var item in selected)
            {
                if (!byRepair.TryGetValue(item.Repair, out AnalysisRepairItemResult? applied))
                    continue;
                item.ResultText = applied.Outcome switch
                {
                    WriteOutcome.Saved =>
                        L("Health.Result.Applied"),
                    WriteOutcome.Skipped =>
                        L("Health.Result.AlreadyCorrect"),
                    _ => L("Health.Result.Failed"),
                };
                item.ResultDiagnosticDetail =
                    string.Join(
                        Environment.NewLine,
                        new[]
                        {
                            applied.Outcome == WriteOutcome.Failed
                                ? applied.Error
                                : null,
                            applied.CacheError is null
                                ? null
                                : LF(
                                    "Health.Diagnostic.CacheRefreshFailed",
                                    applied.CacheError),
                        }.Where(value =>
                            !string.IsNullOrWhiteSpace(value)));
                item.IsApplied = applied.Outcome is WriteOutcome.Saved or WriteOutcome.Skipped;
                if (item.IsApplied)
                    item.Disposition = AnalysisRepairDisposition.Completed;
            }
            SetStatusText(
                "Health.Status.MetadataRepair.Completed",
                result.SavedCount,
                result.SkippedCount,
                result.FailedCount,
                result.CacheFailedCount);
            scope.Complete(result.FailedCount > 0 || result.CacheFailedCount > 0
                ? MessageTone.Warning
                : MessageTone.Success);
            var changed = result.Items
                .Where(item => item.Outcome == WriteOutcome.Saved)
                .Select(item => item.AppliedPath ?? item.Repair.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (changed.Count > 0)
                RepairsApplied?.Invoke(changed);
        }
        catch (OperationCanceledException)
        {
            scope.Cancel();
            SetStatusText(
                "Health.Status.MetadataRepair.Cancelled");
        }
        catch (Exception ex)
        {
            scope.Fail();
            SetStatusFailure(
                "Health.Status.MetadataRepair.Failed",
                ex.Message);
        }
        finally { ApplyRepairsCommand.NotifyCanExecuteChanged(); }
    }

    private AnalysisRepairPlan StampPolicy(AnalysisRepairPlan plan) =>
        _settings.Configuration is { } configuration
            ? plan with
            {
                PolicyFingerprint = configuration.PolicySnapshot.Fingerprint,
                LibraryId = configuration.LibraryId,
            }
            : plan;

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
        if (_dialogs is not null && !await _dialogs.ConfirmAsync(
                L("Health.Dialog.FileRepair.Title"),
                LF(
                    "Health.Dialog.FileRepair.Message",
                    active.Count),
                L("Health.Dialog.FileRepair.Confirm")))
            return;

        using var scope = BeginRun(
            "Health.Operation.ApplyRepresentationFileRepairs",
            AnalysisResultView.RepresentationRepairs);
        try
        {
            var progress = new Progress<RepresentationRepairProgress>(value =>
                SetStatusText(
                    "Health.Progress.ApplyingRepresentationRepairs",
                    value.Completed,
                    value.Total,
                    Path.GetFileName(value.SourcePath)));
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
                    RepresentationRepairOutcome.Applied =>
                        L("Health.Result.Applied"),
                    RepresentationRepairOutcome.Skipped =>
                        L("Health.Result.Skipped"),
                    _ => L("Health.Result.Failed"),
                };
                item.ResultDiagnosticDetail = applied.Error;
                item.IsApplied = applied.Outcome == RepresentationRepairOutcome.Applied;
                if (item.IsApplied)
                    item.Disposition = AnalysisRepairDisposition.Completed;
            }

            if (result.Cancelled)
                SetCountStatusText(
                    "Health.Status.RepresentationRepair.CancelledAfter",
                    result.Applied);
            else
                SetStatusText(
                    "Health.Status.RepresentationRepair.Completed",
                    result.Applied,
                    result.Failed);
            if (result.Cancelled)
                scope.Cancel();
            else
                scope.Complete(result.Failed > 0 ? MessageTone.Warning : MessageTone.Success);
            if (result.ChangedPaths.Count > 0)
                RepairsApplied?.Invoke(result.ChangedPaths);
        }
        catch (OperationCanceledException)
        {
            scope.Cancel();
            SetStatusText(
                "Health.Status.RepresentationRepair.Cancelled");
        }
        catch (Exception ex)
        {
            scope.Fail();
            SetStatusFailure(
                "Health.Status.RepresentationRepair.Failed",
                ex.Message);
        }
        finally
        {
            ApplyRepresentationRepairsCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunCheckSets()
    {
        using var scope = BeginRun(
            "Health.Run.Name.CrossSetCheck",
            AnalysisResultView.Findings);
        try
        {
            var records = await _library.GetAllRecordsAsync(scope.Token);
            var report = await _library.CheckSetsAsync(scope.Token);
            HealthRunText text = report.Count == 0
                ? RunText(
                    AnalysisRunKind.CrossSetCheck,
                    "Health.Run.Name.CrossSetCheck",
                    "Health.Status.CrossSetCheck.None")
                : RunText(
                    AnalysisRunKind.CrossSetCheck,
                    "Health.Run.Name.CrossSetCheck",
                    "Health.Status.CrossSetCheck.Findings",
                    report.Count);
            string status = text.ResolveSummary(_localization);
            var run = await Task.Run(
                () => AnalysisRunViewModel.ForFindings(
                    report,
                    records,
                    status,
                    text,
                    _localization),
                scope.Token);
            AddRun(run);
        }
        catch (OperationCanceledException)
        {
            scope.Cancel();
            SetStatusText(
                "Health.Status.CrossSetCheck.Cancelled");
        }
        catch (Exception ex)
        {
            scope.Fail();
            SetStatusFailure(
                "Health.Status.CrossSetCheck.Failed",
                ex.Message);
        }
    }

    // Shared runner for the analyses that operate on the flat record list: fetch records, run `body`
    // off the UI thread, including result projection, then publish one completed snapshot.
    private async Task RunOverRecords(string labelResourceKey, AnalysisResultView view,
        Func<IReadOnlyList<TrackRecord>, IProgress<AnalysisProgress>, CancellationToken,
            (string Status, AnalysisRunViewModel Run)> body)
    {
        using var scope = BeginRun(labelResourceKey, view);
        try
        {
            var records = await _library.GetAllRecordsAsync(scope.Token);
            IProgress<AnalysisProgress> progress = CreateAnalysisProgress();
            var (_, run) = await Task.Run(
                () => body(records, progress, scope.Token), scope.Token);
            AddRun(run);
            SetStatusFromRun(run);
        }
        catch (OperationCanceledException)
        {
            scope.Cancel();
            SetStatusText(
                "Health.Status.Run.Cancelled",
                L(labelResourceKey));
        }
        catch (Exception ex)
        {
            scope.Fail();
            SetStatusFailure(
                "Health.Status.Run.Failed",
                ex.Message,
                L(labelResourceKey));
        }
    }

    // Sets up busy state / cancellation / active view; disposing restores idle state.
    private RunScope BeginRun(string labelResourceKey, AnalysisResultView view)
    {
        IsBusy = true;
        AnalysisProgressFraction = 0;
        IsAnalysisProgressIndeterminate = true;
        _analysisProgressClock = null;
        _analysisProgressStage = null;
        _analysisProgressUnit = null;
        _analysisProgressTotal = 0;
        _analysisProgressOrigin = 0;
        _analysisProgressCompleted = 0;
        _lastAnalysisProgress = null;
        // Keep the selected retained result visible while another analysis runs. With no history
        // yet, show the destination surface so the first run still has a natural empty state.
        if (SelectedRun is null)
            ActiveView = view;
        SetStatusText(
            "Health.Progress.Running",
            L(labelResourceKey));
        StatusTone = MessageTone.Info;
        LastActivityState = AppActivityState.Running;
        _cts = new CancellationTokenSource();
        NotifyCommands();
        return new RunScope(this);
    }

    private IProgress<AnalysisProgress> CreateAnalysisProgress() =>
        new Progress<AnalysisProgress>(ReportAnalysisProgress);

    private void ReportAnalysisProgress(AnalysisProgress value)
    {
        if (!IsBusy || LastActivityState != AppActivityState.Running)
            return;
        _lastAnalysisProgress = value;

        long completed = Math.Clamp(value.Completed, 0, Math.Max(0, value.Total));
        long total = Math.Max(0, value.Total);
        if (total == 0)
        {
            IsAnalysisProgressIndeterminate = true;
            SetStatusText(
                "Health.Progress.Indeterminate",
                ProgressStage(value.Stage));
            return;
        }

        bool newPhase = !string.Equals(_analysisProgressStage, value.Stage, StringComparison.Ordinal) ||
            !string.Equals(_analysisProgressUnit, value.Unit, StringComparison.Ordinal) ||
            _analysisProgressTotal != total || completed < _analysisProgressCompleted;
        if (newPhase)
        {
            _analysisProgressStage = value.Stage;
            _analysisProgressUnit = value.Unit;
            _analysisProgressTotal = total;
            _analysisProgressOrigin = completed;
            _analysisProgressClock = Stopwatch.StartNew();
        }
        _analysisProgressCompleted = completed;
        IsAnalysisProgressIndeterminate = false;
        AnalysisProgressFraction = Math.Clamp((double)completed / total, 0, 1);

        long phaseCompleted = completed - _analysisProgressOrigin;
        double elapsedSeconds = _analysisProgressClock?.Elapsed.TotalSeconds ?? 0;
        string estimate;
        if (phaseCompleted <= 0 || elapsedSeconds < 0.5)
        {
            estimate = L(
                "Health.Progress.EtaCalculating");
        }
        else
        {
            double rate = phaseCompleted / elapsedSeconds;
            double remainingSeconds = (total - completed) / rate;
            estimate = LF(
                "Health.Progress.RateAndEta",
                FormatRate(rate),
                ProgressUnit(value.Unit),
                FormatDuration(remainingSeconds));
        }

        SetStatusText(
            "Health.Progress.Determinate",
            ProgressStage(value.Stage),
            completed,
            total,
            ProgressUnit(value.Unit),
            AnalysisProgressFraction,
            estimate);
    }

    private static string FormatRate(double rate) => rate switch
    {
        >= 100 => rate.ToString("N0", CultureInfo.CurrentCulture),
        >= 10 => rate.ToString("N1", CultureInfo.CurrentCulture),
        _ => rate.ToString("N2", CultureInfo.CurrentCulture),
    };

    private string FormatDuration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            return L(
                "Health.Progress.Calculating");
        TimeSpan duration = TimeSpan.FromSeconds(Math.Ceiling(seconds));
        if (duration.TotalHours >= 1)
            return LF(
                "Health.Duration.HoursMinutes",
                (int)duration.TotalHours,
                duration.Minutes);
        if (duration.TotalMinutes >= 1)
            return LF(
                "Health.Duration.MinutesSeconds",
                duration.Minutes,
                duration.Seconds);
        return LF(
            "Health.Duration.Seconds",
            Math.Max(0, duration.Seconds));
    }

    private sealed class RunScope(AnalyzerViewModel vm) : IDisposable
    {
        public CancellationToken Token => vm._cts!.Token;

        public void Complete(MessageTone tone = MessageTone.Success)
        {
            vm.LastActivityState = AppActivityState.Completed;
            vm.StatusTone = tone;
        }

        public void Cancel()
        {
            vm.LastActivityState = AppActivityState.Cancelled;
            vm.StatusTone = MessageTone.Warning;
        }

        public void Fail()
        {
            vm.LastActivityState = AppActivityState.Failed;
            vm.StatusTone = MessageTone.Error;
        }

        public void Dispose()
        {
            if (vm.LastActivityState == AppActivityState.Running)
                Complete();
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
        foreach (ArtworkRepairItemViewModel item in run.ArtworkRepairItems)
            item.StateChanged += ArtworkRepairStateChanged;
        foreach (ArtistGroupViewModel group in run.ArtistGroups)
            group.StateChanged += ArtistGroupStateChanged;
        Runs.Insert(0, run);
        OnPropertyChanged(nameof(HasRuns));
        SelectedRun = run;
        RemoveRunCommand.NotifyCanExecuteChanged();
        ClearRunsCommand.NotifyCanExecuteChanged();
        PublishFilter();
    }

    private void RunChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AnalysisRunViewModel.FilteredPaths) &&
            !_clearingFilterDispositions)
            PublishFilter();
    }

    private void ArtistGroupStateChanged()
    {
        OnPropertyChanged(nameof(ActiveArtistVariantCount));
        OnPropertyChanged(nameof(ActiveArtistTrackCount));
        ApplySimilarArtistsCommand.NotifyCanExecuteChanged();
    }

    private void ArtworkRepairStateChanged()
    {
        OnPropertyChanged(nameof(ActiveArtworkRepairCount));
        ApplyArtworkRepairsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Clears every disposition represented by the Library's Health-results chip.</summary>
    public void ClearFilterDispositions()
    {
        bool changed = false;
        _clearingFilterDispositions = true;
        try
        {
            foreach (AnalysisRunViewModel run in Runs)
                changed |= run.ClearFilterDispositions();
        }
        finally
        {
            _clearingFilterDispositions = false;
        }

        if (changed)
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
        ForceScanDeferredArtworkCommand.NotifyCanExecuteChanged();
        ApplyArtworkRepairsCommand.NotifyCanExecuteChanged();
        RunRepresentationsCommand.NotifyCanExecuteChanged();
        PreviewRepresentationRepairsCommand.NotifyCanExecuteChanged();
        VerifyDecodedAudioCommand.NotifyCanExecuteChanged();
        RunCheckSetsCommand.NotifyCanExecuteChanged();
        PreviewMetadataRepairsCommand.NotifyCanExecuteChanged();
        ApplyRepairsCommand.NotifyCanExecuteChanged();
        ApplySimilarArtistsCommand.NotifyCanExecuteChanged();
        ApplyRepresentationRepairsCommand.NotifyCanExecuteChanged();
        PreviewItlMetadataRepairsCommand.NotifyCanExecuteChanged();
        ApplyItlMetadataRepairsCommand.NotifyCanExecuteChanged();
        RemoveRunCommand.NotifyCanExecuteChanged();
        ClearRunsCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void Open(string? path)
    {
        if (!string.IsNullOrEmpty(path))
            OpenRequested?.Invoke(path);
    }

    private string L(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);

    private string LF(
        string key,
        params object?[] arguments) =>
        _localization?.Format(key, arguments) ??
        LocalizedText.Format(key, arguments);

    private string LC(
        string key,
        long count,
        params object?[] arguments) =>
        _localization?.FormatCount(key, count, arguments) ??
        LocalizedText.FormatCount(key, count, arguments);

    private void SetStatusText(
        string key,
        params object?[] arguments)
    {
        _statusTextKey = key;
        _statusTextArguments = arguments;
        _statusTextCount = null;
        _statusUsesSelectedRun = false;
        StatusText = LF(key, arguments);
        StatusDiagnosticDetail = null;
    }

    private void SetCountStatusText(
        string key,
        long count,
        params object?[] arguments)
    {
        _statusTextKey = key;
        _statusTextArguments = arguments;
        _statusTextCount = count;
        _statusUsesSelectedRun = false;
        StatusText = LC(key, count, arguments);
        StatusDiagnosticDetail = null;
    }

    private void SetStatusFailure(
        string key,
        string? diagnosticDetail,
        params object?[] arguments)
    {
        SetStatusText(key, arguments);
        StatusDiagnosticDetail = diagnosticDetail;
    }

    private void SetStatusFromRun(AnalysisRunViewModel run)
    {
        _statusTextKey = null;
        _statusTextArguments = [];
        _statusTextCount = null;
        _statusUsesSelectedRun = true;
        StatusText = run.Summary;
        StatusDiagnosticDetail = run.DiagnosticDetail;
    }

    private HealthRunText RunText(
        AnalysisRunKind kind,
        string nameResourceKey,
        string summaryResourceKey,
        long? summaryCount = null,
        params object?[] summaryArguments) =>
        new(
            kind,
            nameResourceKey,
            summaryResourceKey,
            summaryCount,
            summaryArguments);

    private string ProgressStage(string stage) =>
        stage switch
        {
            "Analyzing duplicate candidates" =>
                L("Health.Progress.Stage.AnalyzingDuplicateCandidates"),
            "Building album metadata matrix" =>
                L("Health.Progress.Stage.BuildingAlbumMetadataMatrix"),
            "Checking artwork metadata" =>
                L("Health.Progress.Stage.CheckingArtworkMetadata"),
            "Checking audio formats" =>
                L("Health.Progress.Stage.CheckingAudioFormats"),
            "Checking metadata consistency" =>
                L("Health.Progress.Stage.CheckingMetadataConsistency"),
            "Comparing album artwork" =>
                L("Health.Progress.Stage.ComparingAlbumArtwork"),
            "Comparing album representations" =>
                L("Health.Progress.Stage.ComparingAlbumRepresentations"),
            "Reading artist names" =>
                L("Health.Progress.Stage.ReadingArtistNames"),
            "Comparing artist names" =>
                L("Health.Progress.Stage.ComparingArtistNames"),
            "Grouping artwork repairs" =>
                L("Health.Progress.Stage.GroupingArtworkRepairs"),
            "Indexing artwork audit" =>
                L("Health.Progress.Stage.IndexingArtworkAudit"),
            "Indexing tracks for artwork repairs" =>
                L("Health.Progress.Stage.IndexingTracksForArtworkRepairs"),
            "Indexing tracks for artwork results" =>
                L("Health.Progress.Stage.IndexingTracksForArtworkResults"),
            "Planning album artwork repairs" =>
                L("Health.Progress.Stage.PlanningAlbumArtworkRepairs"),
            "Planning file artwork repairs" =>
                L("Health.Progress.Stage.PlanningFileArtworkRepairs"),
            "Preparing analysis findings" =>
                L("Health.Progress.Stage.PreparingAnalysisFindings"),
            "Preparing artwork findings" =>
                L("Health.Progress.Stage.PreparingArtworkFindings"),
            "Preparing artwork repair choices" =>
                L("Health.Progress.Stage.PreparingArtworkRepairChoices"),
            "Preparing representation repair results" =>
                L("Health.Progress.Stage.PreparingRepresentationRepairResults"),
            "Previewing representation derivations" =>
                L("Health.Progress.Stage.PreviewingRepresentationDerivations"),
            "Previewing representation metadata copies" =>
                L("Health.Progress.Stage.PreviewingRepresentationMetadataCopies"),
            "Previewing organization moves" =>
                L("Health.Progress.Stage.PreviewingOrganizationMoves"),
            "Selecting organization repairs" =>
                L("Health.Progress.Stage.SelectingOrganizationRepairs"),
            _ when stage.StartsWith("Health.", StringComparison.Ordinal) =>
                L(stage),
            _ => stage,
        };

    private string ProgressUnit(string unit) =>
        unit switch
        {
            "artist-name comparisons" =>
                L("Health.Progress.Unit.ArtistNameComparisons"),
            "files" => L("Health.Progress.Unit.Files"),
            "findings" => L("Health.Progress.Unit.Findings"),
            "moves" => L("Health.Progress.Unit.Moves"),
            "repair actions" => L("Health.Progress.Unit.RepairActions"),
            "results" => L("Health.Progress.Unit.Results"),
            "tracks" => L("Health.Progress.Unit.Tracks"),
            _ when unit.StartsWith("Health.", StringComparison.Ordinal) =>
                L(unit),
            _ => unit,
        };

    private void SetProviderProgressStatus(
        OperationProgress progress,
        string fallbackResourceKey)
    {
        string key = progress.Phase switch
        {
            OperationPhase.LoadingConfiguration =>
                "Health.Progress.OperationPhase.LoadingConfiguration",
            OperationPhase.LoadingLibrary =>
                "Health.Progress.OperationPhase.LoadingLibrary",
            OperationPhase.IndexingSources =>
                "Health.Progress.OperationPhase.IndexingSources",
            OperationPhase.InventoryingDestination =>
                "Health.Progress.OperationPhase.InventoryingDestination",
            OperationPhase.Planning =>
                "Health.Progress.OperationPhase.Planning",
            OperationPhase.Validating =>
                "Health.Progress.OperationPhase.Validating",
            OperationPhase.Applying =>
                "Health.Progress.OperationPhase.Applying",
            OperationPhase.RollingBack =>
                "Health.Progress.OperationPhase.RollingBack",
            OperationPhase.Completed =>
                "Health.Progress.OperationPhase.Completed",
            _ => fallbackResourceKey,
        };
        SetStatusText(key);
    }

    private void OnLocalizationCultureChanged(
        object? sender,
        EventArgs e)
    {
        HealthLocalizedChoices.Refresh(L);
        foreach (AnalysisRunViewModel run in Runs)
            run.RefreshLocalizedText();
        OnPropertyChanged(nameof(FindingsEmptyText));

        if (_statusUsesSelectedRun && SelectedRun is not null)
            StatusText = SelectedRun.Summary;
        else if (_statusTextKey is not null)
            StatusText = _statusTextCount is { } count
                ? LC(_statusTextKey, count, _statusTextArguments)
                : LF(_statusTextKey, _statusTextArguments);

        if (!string.IsNullOrWhiteSpace(ArtistThresholdText))
            SetProperty(
                ref _artistThresholdText,
                ArtistThreshold.ToString(
                    "0.##",
                CultureInfo.CurrentCulture),
                nameof(ArtistThresholdText));
        if (IsBusy && _lastAnalysisProgress is { } progress)
            ReportAnalysisProgress(progress);
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
    public IReadOnlyList<LocalizedChoice<AnalysisRepairDisposition>>
        DispositionChoices => HealthLocalizedChoices.RepairDispositions;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultDiagnosticDetail))]
    private string? _resultDiagnosticDetail;

    public bool HasResultDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(ResultDiagnosticDetail);

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
            LocalizedText.Format(
                "Health.Itl.DifferenceFormat",
                difference.Field,
                select(difference) ??
                LocalizedText.Get("Health.Common.Missing"))));
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
    public IReadOnlyList<LocalizedChoice<AnalysisRepairDisposition>>
        DispositionChoices => HealthLocalizedChoices.AllRepairDispositions;

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
                Category = LocalizedText.Get(
                    "Health.Itl.Category.CachedMetadata"),
                Artist = item.Item.Metadata.AlbumArtist ??
                         item.Item.Metadata.Artist ??
                         LocalizedText.Get(
                             "Health.Common.UnknownArtist"),
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
            : LocalizedText.Get(
                "Health.Common.UnknownAlbum");

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
    public IReadOnlyList<LocalizedChoice<AnalysisRepairDisposition>>
        DispositionChoices => HealthLocalizedChoices.AllRepairDispositions;
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
    public IReadOnlyList<LocalizedChoice<AnalysisRepairDisposition>>
        DispositionChoices => HealthLocalizedChoices.AllRepairDispositions;
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
        ? LocalizedText.Get("Health.Field.Path")
        : Repair.TargetId3Version is not null
            ? LocalizedText.Get("Health.Field.Id3TagVersion")
            : LocalizedText.Get(
                $"Settings.Choice.TagFields.{Repair.Field}");
    public string Before => string.IsNullOrEmpty(Repair.Before)
        ? LocalizedText.Get("Health.Common.Missing")
        : ShowWhitespace(Repair.Before);
    public string After => ShowWhitespace(Repair.After);
    public IReadOnlyList<TextDifferenceSegment> BeforeDifference => _difference.Before;
    public IReadOnlyList<TextDifferenceSegment> AfterDifference => _difference.After;
    public string? UnicodeDifferenceDetails => _difference.UnicodeDetails;
    public string Reason => Repair.Reason;
    public string? BlockingReason => Repair.BlockingReason;
    public bool CanChangeDisposition => !IsApplied;
    public IReadOnlyList<AnalysisRepairDisposition> Dispositions { get; }
    public IReadOnlyList<LocalizedChoice<AnalysisRepairDisposition>>
        DispositionChoices => Repair.CanApply
            ? HealthLocalizedChoices.RepairDispositions
            : HealthLocalizedChoices.BlockedRepairDispositions;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultDiagnosticDetail))]
    private string? _resultDiagnosticDetail;

    public bool HasResultDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(ResultDiagnosticDetail);

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
