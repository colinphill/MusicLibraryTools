using CommunityToolkit.Mvvm.ComponentModel;
using MusicLibrary.Core.Models;

namespace MusicLibraryManager.Presentation;

/// <summary>
/// One cluster of similar artist spellings. Each non-canonical spelling is an independently
/// reviewable rename action; the cluster disposition propagates to those actions.
/// </summary>
public partial class ArtistGroupViewModel : ViewModelBase
{
    private AnalysisRepairDisposition _disposition;
    private bool _propagating;

    public IReadOnlyList<ArtistVariantViewModel> Variants { get; }
    public int TrackCount => Variants.SelectMany(variant => variant.Files)
        .DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase).Count();
    public int ActiveCount => Variants.Count(variant => variant.IsActive);
    public int ActiveTrackCount => Variants.Where(variant => variant.IsActive)
        .SelectMany(variant => variant.Files)
        .DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase).Count();
    public bool HasCanonicalName => !string.IsNullOrWhiteSpace(CanonicalName);
    public bool CanEditCanonical => Variants.All(variant => !variant.IsApplied);
    public IReadOnlyList<AnalysisRepairDisposition> Dispositions { get; } =
        Enum.GetValues<AnalysisRepairDisposition>();
    public IReadOnlyList<LocalizedChoice<AnalysisRepairDisposition>>
        DispositionChoices => HealthLocalizedChoices.AllRepairDispositions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCanonicalName))]
    private string _canonicalName;

    public AnalysisRepairDisposition Disposition
    {
        get => _disposition;
        set
        {
            if (value == AnalysisRepairDisposition.Mixed || _propagating)
                return;
            _propagating = true;
            foreach (ArtistVariantViewModel variant in Variants.Where(item => !item.IsCanonical &&
                         item.CanChangeDisposition && item.Dispositions.Contains(value)))
                variant.Disposition = value;
            _propagating = false;
            RefreshState();
        }
    }

    public event Action? StateChanged;

    public ArtistGroupViewModel(SimilarArtistGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        _canonicalName = group.Suggested;
        Variants = group.Variants.Select(variant => new ArtistVariantViewModel(variant)).ToList();
        foreach (ArtistVariantViewModel variant in Variants)
            variant.StateChanged += VariantChanged;
        RefreshCanonical();
        _disposition = Aggregate();
    }

    partial void OnCanonicalNameChanged(string value)
    {
        RefreshCanonical();
        StateChanged?.Invoke();
    }

    private void VariantChanged()
    {
        RefreshState();
        StateChanged?.Invoke();
    }

    private void RefreshCanonical()
    {
        string canonical = CanonicalName.Trim();
        foreach (ArtistVariantViewModel variant in Variants)
            variant.SetCanonical(string.Equals(variant.Name, canonical, StringComparison.Ordinal));
        RefreshState();
    }

    private AnalysisRepairDisposition Aggregate() =>
        AnalysisRepairCategoryGroupViewModel.Aggregate(
            Variants.Where(variant => !variant.IsCanonical)
                .Select(variant => variant.Disposition));

    private void RefreshState()
    {
        SetProperty(ref _disposition, Aggregate(), nameof(Disposition));
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(ActiveTrackCount));
        OnPropertyChanged(nameof(CanEditCanonical));
    }
}

/// <summary>A single spelling and the files/folders that currently use it.</summary>
public partial class ArtistVariantViewModel : ViewModelBase
{
    public string Name { get; }
    public IReadOnlyList<ArtistPathViewModel> Files { get; }
    public IReadOnlyList<ArtistPathViewModel> Folders { get; }
    public int TrackCount => Files.Count;
    public int FolderCount => Folders.Count;
    public bool CanChangeDisposition => !IsCanonical && !IsApplied;
    public bool IsActive => CanChangeDisposition &&
        Disposition == AnalysisRepairDisposition.Active;
    public IReadOnlyList<AnalysisRepairDisposition> Dispositions { get; } =
        Enum.GetValues<AnalysisRepairDisposition>()
            .Where(value => value != AnalysisRepairDisposition.Mixed)
            .ToArray();
    public IReadOnlyList<LocalizedChoice<AnalysisRepairDisposition>>
        DispositionChoices => HealthLocalizedChoices.RepairDispositions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    private AnalysisRepairDisposition _disposition = AnalysisRepairDisposition.Ignored;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeDisposition))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    private bool _isCanonical;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeDisposition))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    private bool _isApplied;

    [ObservableProperty]
    private string? _resultText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultDiagnosticDetail))]
    private string? _resultDiagnosticDetail;

    public bool HasResultDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(ResultDiagnosticDetail);

    public event Action? StateChanged;

    public ArtistVariantViewModel(ArtistVariant variant)
    {
        Name = variant.Name;
        Files = variant.Paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
            .Select(path => new ArtistPathViewModel(path))
            .ToList();
        Folders = Files.Select(file => Path.GetDirectoryName(file.Path) ?? string.Empty)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
            .Select(path => new ArtistPathViewModel(path))
            .ToList();
    }

    internal void SetCanonical(bool value)
    {
        if (IsCanonical == value)
            return;
        IsCanonical = value;
        if (value && !IsApplied)
            Disposition = AnalysisRepairDisposition.Ignored;
    }

    partial void OnDispositionChanged(AnalysisRepairDisposition value) => StateChanged?.Invoke();
    partial void OnIsAppliedChanged(bool value) => StateChanged?.Invoke();
}

/// <summary>A concrete file or folder shown beneath an artist spelling.</summary>
public sealed record ArtistPathViewModel(string Path)
{
    public string Name => string.IsNullOrWhiteSpace(Path)
        ? LocalizedText.Get("Health.Common.NoFolder")
        : System.IO.Path.GetFileName(Path);
}
