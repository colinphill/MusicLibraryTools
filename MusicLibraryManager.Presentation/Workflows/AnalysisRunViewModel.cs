using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

/// <summary>What the user has decided to do with an analysis finding.</summary>
public enum AnalysisFindingDisposition
{
    None,
    Filter,
    Mixed,
}

/// <summary>
/// Review state for an applicable repair. Mixed is calculated for tree branches and is never
/// assigned to a leaf.
/// </summary>
public enum AnalysisRepairDisposition
{
    Active,
    Completed,
    Ignored,
    Filter,
    Mixed,
}

/// <summary>
/// An immutable analysis result snapshot. The contained finding and action view models remain
/// mutable so review state survives while the user navigates between runs.
/// </summary>
public sealed class AnalysisRunViewModel : ViewModelBase
{
    private bool _clearingFilterDispositions;

    public string Name { get; }
    public string Summary { get; }
    public DateTimeOffset CreatedAt { get; }
    public AnalysisResultView View { get; }
    public IReadOnlyList<AnalysisProblemGroupViewModel> FindingGroups { get; }
    public IReadOnlyList<DuplicateGroup> Duplicates { get; }
    public IReadOnlyList<ArtistGroupViewModel> ArtistGroups { get; }
    public IReadOnlyList<AnalysisConflictGroupViewModel> ConflictGroups { get; }
    public IReadOnlyList<AnalysisRepairItemViewModel> RepairItems { get; }
    public IReadOnlyList<AnalysisRepairCategoryGroupViewModel> RepairGroups { get; }
    public IReadOnlyList<RepresentationRepairActionItemViewModel> RepresentationActionItems { get; }
    public IReadOnlyList<RepresentationRepairCategoryGroupViewModel> RepresentationActionGroups { get; }
    public IReadOnlyList<string> RepresentationWarnings { get; }
    public IReadOnlyList<AlbumMetadataMatrix> Matrices { get; }
    public IReadOnlyList<ArtworkRepairItemViewModel> ArtworkRepairItems { get; }
    public IReadOnlyList<ArtworkRepairCategoryGroupViewModel> ArtworkRepairGroups { get; }
    public AnalysisRepairPlan? RepairPlan { get; }
    public IReadOnlyList<ItlMetadataRepairItemViewModel> ItlRepairItems { get; }
    public IReadOnlyList<ItlMetadataRepairCategoryGroupViewModel> ItlRepairGroups { get; }
    public ItlMetadataRepairPlan? ItlRepairPlan { get; }
    public int Count { get; }

    public int ActiveFindingCount => FindingGroups.Sum(group => group.ActiveCount);
    public IReadOnlyList<string> FilteredPaths => FindingGroups
        .SelectMany(group => group.Artists)
        .SelectMany(group => group.Albums)
        .SelectMany(group => group.Findings)
        .Where(finding => finding.Disposition == AnalysisFindingDisposition.Filter)
        .Select(finding => finding.Path)
        .Concat(RepairItems
            .Where(item => item.Disposition == AnalysisRepairDisposition.Filter)
            .Select(item => item.Path))
        .Concat(ArtistGroups
            .SelectMany(group => group.Variants)
            .Where(variant => variant.Disposition == AnalysisRepairDisposition.Filter)
            .SelectMany(variant => variant.Files)
            .Select(file => file.Path))
        .Concat(RepresentationActionItems
            .Where(item => item.Disposition == AnalysisRepairDisposition.Filter)
            .Select(item => item.SourcePath))
        .Concat(ItlRepairItems
            .Where(item => item.Disposition == AnalysisRepairDisposition.Filter)
            .Select(item => item.Path))
        .Concat(ArtworkRepairItems
            .Where(item => item.Disposition == AnalysisRepairDisposition.Filter)
            .SelectMany(item => item.AffectedPaths)
            .Select(item => item.Path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    public string DisplayLabel => $"{Name} · {Count:N0} · {CreatedAt:HH:mm:ss}";

    private AnalysisRunViewModel(
        string name,
        string summary,
        AnalysisResultView view,
        int count,
        IReadOnlyList<AnalysisProblemGroupViewModel>? findingGroups = null,
        IReadOnlyList<DuplicateGroup>? duplicates = null,
        IReadOnlyList<ArtistGroupViewModel>? artistGroups = null,
        IReadOnlyList<AnalysisConflictGroupViewModel>? conflictGroups = null,
        IReadOnlyList<AnalysisRepairItemViewModel>? repairItems = null,
        IReadOnlyList<AnalysisRepairCategoryGroupViewModel>? repairGroups = null,
        IReadOnlyList<RepresentationRepairActionItemViewModel>? representationActionItems = null,
        IReadOnlyList<RepresentationRepairCategoryGroupViewModel>? representationActionGroups = null,
        IReadOnlyList<string>? representationWarnings = null,
        AnalysisRepairPlan? repairPlan = null,
        IReadOnlyList<AlbumMetadataMatrix>? matrices = null,
        IReadOnlyList<ArtworkRepairItemViewModel>? artworkRepairItems = null,
        IReadOnlyList<ArtworkRepairCategoryGroupViewModel>? artworkRepairGroups = null,
        IReadOnlyList<ItlMetadataRepairItemViewModel>? itlRepairItems = null,
        ItlMetadataRepairPlan? itlRepairPlan = null)
    {
        Name = name;
        Summary = summary;
        View = view;
        Count = count;
        CreatedAt = DateTimeOffset.Now;
        FindingGroups = findingGroups ?? [];
        Duplicates = duplicates ?? [];
        ArtistGroups = artistGroups ?? [];
        ConflictGroups = conflictGroups ?? [];
        RepairItems = repairItems ?? [];
        RepairGroups = repairGroups ?? [];
        RepresentationActionItems = representationActionItems ?? [];
        RepresentationActionGroups = representationActionGroups ?? [];
        RepresentationWarnings = representationWarnings ?? [];
        RepairPlan = repairPlan;
        Matrices = matrices ?? [];
        ArtworkRepairItems = artworkRepairItems ?? [];
        ArtworkRepairGroups = artworkRepairGroups ??
            ArtworkRepairCategoryGroupViewModel.Build(ArtworkRepairItems);
        ItlRepairItems = itlRepairItems ?? [];
        ItlRepairGroups = ItlMetadataRepairCategoryGroupViewModel.Build(ItlRepairItems);
        ItlRepairPlan = itlRepairPlan;

        foreach (var group in FindingGroups)
            group.PropertyChanged += FindingGroupChanged;
        foreach (AnalysisFindingViewModel finding in FindingGroups
                     .SelectMany(group => group.Artists)
                     .SelectMany(group => group.Albums)
                     .SelectMany(group => group.Findings))
            finding.PropertyChanged += FilterDispositionChanged;
        foreach (AnalysisRepairItemViewModel item in RepairItems)
            item.PropertyChanged += FilterDispositionChanged;
        foreach (ArtistVariantViewModel variant in ArtistGroups.SelectMany(group => group.Variants))
            variant.PropertyChanged += FilterDispositionChanged;
        foreach (RepresentationRepairActionItemViewModel item in RepresentationActionItems)
            item.PropertyChanged += FilterDispositionChanged;
        foreach (ItlMetadataRepairItemViewModel item in ItlRepairItems)
            item.PropertyChanged += FilterDispositionChanged;
        foreach (ArtworkRepairItemViewModel item in ArtworkRepairItems)
            item.PropertyChanged += FilterDispositionChanged;
    }

    public static AnalysisRunViewModel ForFindings(
        AnalysisReport report,
        IReadOnlyList<TrackRecord> records,
        string summary) =>
        new(report.Name, summary, AnalysisResultView.Findings, report.Count,
            findingGroups: AnalysisProblemGroupViewModel.Build(report.Findings, records));

    public static AnalysisRunViewModel ForDuplicates(
        string name,
        IReadOnlyList<DuplicateGroup> groups,
        string summary) =>
        new(name, summary, AnalysisResultView.Duplicates, groups.Count, duplicates: groups);

    public static AnalysisRunViewModel ForArtists(
        string name,
        IReadOnlyList<ArtistGroupViewModel> groups,
        string summary) =>
        new(name, summary, AnalysisResultView.Artists, groups.Count, artistGroups: groups);

    public static AnalysisRunViewModel ForConflicts(
        IReadOnlyList<AnalysisConflictGroupViewModel> groups,
        string summary) =>
        new("Album artist conflicts", summary, AnalysisResultView.Conflicts, groups.Count,
            conflictGroups: groups);

    public static AnalysisRunViewModel ForRepairs(
        AnalysisRepairPlan plan,
        IReadOnlyList<AnalysisRepairItemViewModel> items,
        IReadOnlyList<TrackRecord> records,
        string summary) =>
        new(plan.Name, summary, AnalysisResultView.Repairs, items.Count,
            repairItems: items,
            repairGroups: AnalysisRepairCategoryGroupViewModel.Build(items, records),
            repairPlan: plan);

    public static AnalysisRunViewModel ForRepresentationRepairs(
        IReadOnlyList<RepresentationRepairAction> actions,
        IReadOnlyList<string> warnings,
        IReadOnlyList<TrackRecord> records,
        string summary)
    {
        var items = actions.Select(action =>
            new RepresentationRepairActionItemViewModel(action)).ToList();
        return new("Representation file repairs", summary,
            AnalysisResultView.RepresentationRepairs, actions.Count,
            representationActionItems: items,
            representationActionGroups:
                RepresentationRepairCategoryGroupViewModel.Build(items, records),
            representationWarnings: warnings);
    }

    public static AnalysisRunViewModel ForMatrices(
        IReadOnlyList<AlbumMetadataMatrix> matrices,
        string summary) =>
        new("Album metadata matrix", summary, AnalysisResultView.Matrix, matrices.Count,
            matrices: matrices);

    public static AnalysisRunViewModel ForArtwork(
        AnalysisReport report,
        IReadOnlyList<TrackRecord> records,
        IReadOnlyList<ArtworkRepairItemViewModel> repairs,
        string summary) =>
        ForArtwork(report, records, repairs, summary, null, default);

    public static AnalysisRunViewModel ForArtwork(
        AnalysisReport report,
        IReadOnlyList<TrackRecord> records,
        IReadOnlyList<ArtworkRepairItemViewModel> repairs,
        string summary,
        IProgress<AnalysisProgress>? progress,
        CancellationToken ct)
    {
        IReadOnlyList<AnalysisProblemGroupViewModel> findingGroups =
            AnalysisProblemGroupViewModel.Build(report.Findings, records, progress, ct,
                "Preparing artwork findings");
        IReadOnlyList<ArtworkRepairCategoryGroupViewModel> repairGroups =
            ArtworkRepairCategoryGroupViewModel.Build(repairs, progress, ct);
        return new("Artwork health", summary, AnalysisResultView.ArtworkRepairs,
            report.Count, findingGroups: findingGroups,
            artworkRepairItems: repairs, artworkRepairGroups: repairGroups);
    }

    public static AnalysisRunViewModel ForItlRepairs(
        ItlMetadataRepairPlan plan,
        IReadOnlyList<ItlMetadataRepairItemViewModel> items,
        string summary) =>
        new("iTunes library metadata repairs", summary, AnalysisResultView.ItlRepairs,
            items.Count, itlRepairItems: items, itlRepairPlan: plan);

    private void FindingGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AnalysisProblemGroupViewModel.ActiveCount))
            OnPropertyChanged(nameof(ActiveFindingCount));
    }

    private void FilterDispositionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Disposition" && !_clearingFilterDispositions)
            OnPropertyChanged(nameof(FilteredPaths));
    }

    /// <summary>
    /// Clears the Library-filter choice without disturbing repair choices that are active,
    /// ignored, completed, or otherwise unrelated to the filter chip.
    /// </summary>
    public bool ClearFilterDispositions()
    {
        if (FilteredPaths.Count == 0)
            return false;

        _clearingFilterDispositions = true;
        try
        {
            foreach (AnalysisFindingViewModel finding in FindingGroups
                         .SelectMany(group => group.Artists)
                         .SelectMany(group => group.Albums)
                         .SelectMany(group => group.Findings)
                         .Where(finding =>
                             finding.Disposition == AnalysisFindingDisposition.Filter))
                finding.Disposition = AnalysisFindingDisposition.None;

            foreach (AnalysisRepairItemViewModel item in RepairItems.Where(item =>
                         item.Disposition == AnalysisRepairDisposition.Filter))
                item.Disposition = AnalysisRepairDisposition.Ignored;

            foreach (ArtistVariantViewModel variant in ArtistGroups
                         .SelectMany(group => group.Variants)
                         .Where(variant =>
                             variant.Disposition == AnalysisRepairDisposition.Filter))
                variant.Disposition = AnalysisRepairDisposition.Ignored;

            foreach (RepresentationRepairActionItemViewModel item in
                     RepresentationActionItems.Where(item =>
                         item.Disposition == AnalysisRepairDisposition.Filter))
                item.Disposition = AnalysisRepairDisposition.Ignored;

            foreach (ItlMetadataRepairItemViewModel item in ItlRepairItems.Where(item =>
                         item.Disposition == AnalysisRepairDisposition.Filter))
                item.Disposition = AnalysisRepairDisposition.Ignored;

            foreach (ArtworkRepairItemViewModel item in ArtworkRepairItems.Where(item =>
                         item.Disposition == AnalysisRepairDisposition.Filter))
                item.Disposition = AnalysisRepairDisposition.Ignored;
        }
        finally
        {
            _clearingFilterDispositions = false;
        }

        OnPropertyChanged(nameof(FilteredPaths));
        return true;
    }
}

/// <summary>Metadata repairs for one field, divided into artists and albums.</summary>
public sealed class AnalysisRepairCategoryGroupViewModel : ViewModelBase
{
    private AnalysisRepairDisposition _disposition;
    private bool _propagating;

    public string Category { get; }
    public IReadOnlyList<AnalysisRepairArtistGroupViewModel> Artists { get; }
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
            foreach (var artist in Artists)
                artist.Disposition = value;
            _propagating = false;
            RefreshState();
        }
    }

    private AnalysisRepairCategoryGroupViewModel(
        string category,
        IReadOnlyList<AnalysisRepairArtistGroupViewModel> artists)
    {
        Category = category;
        Artists = artists;
        foreach (var artist in Artists)
            artist.PropertyChanged += ArtistChanged;
        _disposition = Aggregate(Artists.Select(artist => artist.Disposition));
    }

    public static IReadOnlyList<AnalysisRepairCategoryGroupViewModel> Build(
        IReadOnlyList<AnalysisRepairItemViewModel> items,
        IReadOnlyList<TrackRecord> records)
    {
        var recordsByPath = records
            .GroupBy(record => record.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return items
            .Select(item =>
            {
                TrackRecord? record = recordsByPath.GetValueOrDefault(item.Path);
                return new
                {
                    Item = item,
                    Artist = record?.EffectiveAlbumArtist ?? "Unknown Artist",
                    Album = AlbumLabel(item.Path, record),
                };
            })
            .GroupBy(entry => entry.Item.Field, StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(category => new AnalysisRepairCategoryGroupViewModel(
                category.Key,
                category.GroupBy(entry => entry.Artist, StringComparer.CurrentCultureIgnoreCase)
                    .OrderBy(artist => artist.Key, StringComparer.CurrentCultureIgnoreCase)
                    .Select(artist => new AnalysisRepairArtistGroupViewModel(
                        artist.Key,
                        artist.GroupBy(entry => entry.Album, StringComparer.CurrentCultureIgnoreCase)
                            .OrderBy(album => album.Key, StringComparer.CurrentCultureIgnoreCase)
                            .Select(album => new AnalysisRepairAlbumGroupViewModel(
                                album.Key,
                                album.Select(entry => entry.Item)
                                    .OrderBy(item => item.Path, StringComparer.CurrentCultureIgnoreCase)
                                    .ThenBy(item => item.Field, StringComparer.CurrentCultureIgnoreCase)
                                    .ToList()))
                            .ToList()))
                    .ToList()))
            .ToList();
    }

    private static string AlbumLabel(string path, TrackRecord? record)
    {
        if (record is not null)
        {
            string? album = !string.IsNullOrWhiteSpace(record.StrippedAlbum)
                ? record.StrippedAlbum
                : record.Album;
            return string.IsNullOrWhiteSpace(album) ? "Unknown Album" : album;
        }

        string? directory = Path.GetDirectoryName(path);
        string? folder = string.IsNullOrWhiteSpace(directory) ? null : Path.GetFileName(directory);
        return string.IsNullOrWhiteSpace(folder) ? "Unknown Album" : folder;
    }

    private void ArtistChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AnalysisRepairArtistGroupViewModel.ActiveCount) or
            nameof(AnalysisRepairArtistGroupViewModel.Disposition))
            RefreshState();
    }

    private void RefreshState()
    {
        SetProperty(ref _disposition,
            Aggregate(Artists.Select(artist => artist.Disposition)),
            nameof(Disposition));
        OnPropertyChanged(nameof(ActiveCount));
    }

    internal static AnalysisRepairDisposition Aggregate(
        IEnumerable<AnalysisRepairDisposition> dispositions)
    {
        AnalysisRepairDisposition[] values = dispositions.Distinct().ToArray();
        return values.Length == 0
            ? AnalysisRepairDisposition.Ignored
            : values.Length == 1 ? values[0] : AnalysisRepairDisposition.Mixed;
    }
}

/// <summary>Metadata repairs for one artist within a field category.</summary>
public sealed class AnalysisRepairArtistGroupViewModel : ViewModelBase
{
    private AnalysisRepairDisposition _disposition;
    private bool _propagating;

    public string Artist { get; }
    public IReadOnlyList<AnalysisRepairAlbumGroupViewModel> Albums { get; }
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
            foreach (var album in Albums)
                album.Disposition = value;
            _propagating = false;
            RefreshState();
        }
    }

    public AnalysisRepairArtistGroupViewModel(
        string artist,
        IReadOnlyList<AnalysisRepairAlbumGroupViewModel> albums)
    {
        Artist = artist;
        Albums = albums;
        foreach (var album in Albums)
            album.PropertyChanged += AlbumChanged;
        _disposition = AnalysisRepairCategoryGroupViewModel.Aggregate(
            Albums.Select(album => album.Disposition));
    }

    private void AlbumChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AnalysisRepairAlbumGroupViewModel.ActiveCount) or
            nameof(AnalysisRepairAlbumGroupViewModel.Disposition))
            RefreshState();
    }

    private void RefreshState()
    {
        SetProperty(ref _disposition,
            AnalysisRepairCategoryGroupViewModel.Aggregate(
                Albums.Select(album => album.Disposition)),
            nameof(Disposition));
        OnPropertyChanged(nameof(ActiveCount));
    }
}

/// <summary>Metadata repairs for one album within an artist and field category.</summary>
public sealed class AnalysisRepairAlbumGroupViewModel : ViewModelBase
{
    private AnalysisRepairDisposition _disposition;
    private bool _propagating;

    public string Album { get; }
    public IReadOnlyList<AnalysisRepairItemViewModel> Items { get; }
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
            foreach (var item in Items.Where(item =>
                         item.CanChangeDisposition && item.Dispositions.Contains(value)))
                item.Disposition = value;
            _propagating = false;
            RefreshState();
        }
    }

    public AnalysisRepairAlbumGroupViewModel(
        string album,
        IReadOnlyList<AnalysisRepairItemViewModel> items)
    {
        Album = album;
        Items = items;
        foreach (var item in Items)
            item.PropertyChanged += ItemChanged;
        _disposition = AnalysisRepairCategoryGroupViewModel.Aggregate(
            Items.Select(item => item.Disposition));
    }

    private void ItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AnalysisRepairItemViewModel.Disposition) or
            nameof(AnalysisRepairItemViewModel.IsApplied) or
            nameof(AnalysisRepairItemViewModel.IsActive))
            RefreshState();
    }

    private void RefreshState()
    {
        SetProperty(ref _disposition,
            AnalysisRepairCategoryGroupViewModel.Aggregate(
                Items.Select(item => item.Disposition)),
            nameof(Disposition));
        OnPropertyChanged(nameof(ActiveCount));
    }
}

public sealed class RepresentationRepairCategoryGroupViewModel : ViewModelBase
{
    private AnalysisRepairDisposition _disposition;
    private bool _propagating;

    public string Category { get; }
    public IReadOnlyList<RepresentationRepairArtistGroupViewModel> Artists { get; }
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
            foreach (var artist in Artists)
                artist.Disposition = value;
            _propagating = false;
            RefreshState();
        }
    }

    private RepresentationRepairCategoryGroupViewModel(
        string category,
        IReadOnlyList<RepresentationRepairArtistGroupViewModel> artists)
    {
        Category = category;
        Artists = artists;
        foreach (var artist in Artists)
            artist.PropertyChanged += ArtistChanged;
        _disposition = AnalysisRepairCategoryGroupViewModel.Aggregate(
            Artists.Select(artist => artist.Disposition));
    }

    public static IReadOnlyList<RepresentationRepairCategoryGroupViewModel> Build(
        IReadOnlyList<RepresentationRepairActionItemViewModel> items,
        IReadOnlyList<TrackRecord> records)
    {
        var recordsByPath = records
            .GroupBy(record => record.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(),
                StringComparer.OrdinalIgnoreCase);

        return items
            .Select(item =>
            {
                TrackRecord? record = recordsByPath.GetValueOrDefault(item.SourcePath);
                return new
                {
                    Item = item,
                    Artist = record?.EffectiveAlbumArtist ?? "Unknown Artist",
                    Album = AlbumLabel(item.SourcePath, record),
                };
            })
            .GroupBy(entry => entry.Item.Category, StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(category => new RepresentationRepairCategoryGroupViewModel(
                category.Key,
                category.GroupBy(entry => entry.Artist, StringComparer.CurrentCultureIgnoreCase)
                    .OrderBy(artist => artist.Key, StringComparer.CurrentCultureIgnoreCase)
                    .Select(artist => new RepresentationRepairArtistGroupViewModel(
                        artist.Key,
                        artist.GroupBy(entry => entry.Album, StringComparer.CurrentCultureIgnoreCase)
                            .OrderBy(album => album.Key, StringComparer.CurrentCultureIgnoreCase)
                            .Select(album => new RepresentationRepairAlbumGroupViewModel(
                                album.Key,
                                album.Select(entry => entry.Item)
                                    .OrderBy(item => item.SourcePath,
                                        StringComparer.CurrentCultureIgnoreCase)
                                    .ToList()))
                            .ToList()))
                    .ToList()))
            .ToList();
    }

    private static string AlbumLabel(string path, TrackRecord? record)
    {
        string? album = record is null
            ? Path.GetFileName(Path.GetDirectoryName(path))
            : !string.IsNullOrWhiteSpace(record.StrippedAlbum)
                ? record.StrippedAlbum
                : record.Album;
        return string.IsNullOrWhiteSpace(album) ? "Unknown Album" : album;
    }

    private void ArtistChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RepresentationRepairArtistGroupViewModel.ActiveCount) or
            nameof(RepresentationRepairArtistGroupViewModel.Disposition))
            RefreshState();
    }

    private void RefreshState()
    {
        SetProperty(ref _disposition,
            AnalysisRepairCategoryGroupViewModel.Aggregate(
                Artists.Select(artist => artist.Disposition)),
            nameof(Disposition));
        OnPropertyChanged(nameof(ActiveCount));
    }
}

public sealed class RepresentationRepairArtistGroupViewModel : ViewModelBase
{
    private AnalysisRepairDisposition _disposition;
    private bool _propagating;

    public string Artist { get; }
    public IReadOnlyList<RepresentationRepairAlbumGroupViewModel> Albums { get; }
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
            foreach (var album in Albums)
                album.Disposition = value;
            _propagating = false;
            RefreshState();
        }
    }

    public RepresentationRepairArtistGroupViewModel(
        string artist,
        IReadOnlyList<RepresentationRepairAlbumGroupViewModel> albums)
    {
        Artist = artist;
        Albums = albums;
        foreach (var album in Albums)
            album.PropertyChanged += AlbumChanged;
        _disposition = AnalysisRepairCategoryGroupViewModel.Aggregate(
            Albums.Select(album => album.Disposition));
    }

    private void AlbumChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RepresentationRepairAlbumGroupViewModel.ActiveCount) or
            nameof(RepresentationRepairAlbumGroupViewModel.Disposition))
            RefreshState();
    }

    private void RefreshState()
    {
        SetProperty(ref _disposition,
            AnalysisRepairCategoryGroupViewModel.Aggregate(
                Albums.Select(album => album.Disposition)),
            nameof(Disposition));
        OnPropertyChanged(nameof(ActiveCount));
    }
}

public sealed class RepresentationRepairAlbumGroupViewModel : ViewModelBase
{
    private AnalysisRepairDisposition _disposition;
    private bool _propagating;

    public string Album { get; }
    public IReadOnlyList<RepresentationRepairActionItemViewModel> Items { get; }
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
            foreach (var item in Items.Where(item => !item.IsApplied))
                item.Disposition = value;
            _propagating = false;
            RefreshState();
        }
    }

    public RepresentationRepairAlbumGroupViewModel(
        string album,
        IReadOnlyList<RepresentationRepairActionItemViewModel> items)
    {
        Album = album;
        Items = items;
        foreach (var item in Items)
            item.PropertyChanged += ItemChanged;
        _disposition = AnalysisRepairCategoryGroupViewModel.Aggregate(
            Items.Select(item => item.Disposition));
    }

    private void ItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RepresentationRepairActionItemViewModel.Disposition) or
            nameof(RepresentationRepairActionItemViewModel.IsApplied) or
            nameof(RepresentationRepairActionItemViewModel.IsActive))
            RefreshState();
    }

    private void RefreshState()
    {
        SetProperty(ref _disposition,
            AnalysisRepairCategoryGroupViewModel.Aggregate(
                Items.Select(item => item.Disposition)),
            nameof(Disposition));
        OnPropertyChanged(nameof(ActiveCount));
    }
}

public partial class RepresentationRepairActionItemViewModel : ViewModelBase
{
    public RepresentationRepairAction Action { get; }
    public string SourcePath => Action.SourcePath;
    public string DestinationPath => Action.DestinationPath;
    public string Description => Action.Description;
    public string Category => Action.Kind switch
    {
        RepresentationRepairKind.DeriveCdFlac => "Derive missing CD FLAC",
        RepresentationRepairKind.DeriveAac => "Derive missing AAC",
        _ => "Organize representation",
    };
    public IReadOnlyList<AnalysisRepairDisposition> Dispositions { get; } =
        Enum.GetValues<AnalysisRepairDisposition>()
            .Where(value => value != AnalysisRepairDisposition.Mixed)
            .ToArray();
    public bool IsActive => Disposition == AnalysisRepairDisposition.Active && !IsApplied;
    public bool CanChangeDisposition => !IsApplied;

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

    public RepresentationRepairActionItemViewModel(RepresentationRepairAction action) =>
        Action = action;

    partial void OnDispositionChanged(AnalysisRepairDisposition value) => StateChanged?.Invoke();
    partial void OnIsAppliedChanged(bool value) => StateChanged?.Invoke();
}

/// <summary>All findings for one problem, divided into artists and albums.</summary>
public sealed class AnalysisProblemGroupViewModel : ViewModelBase
{
    private AnalysisFindingDisposition _disposition;
    private bool _propagating;

    public string Problem { get; }
    public IReadOnlyList<AnalysisArtistGroupViewModel> Artists { get; }
    public int Count => Artists.Sum(artist => artist.Count);
    public int ActiveCount => Artists.Sum(artist => artist.ActiveCount);
    public IReadOnlyList<AnalysisFindingDisposition> Dispositions { get; } =
        Enum.GetValues<AnalysisFindingDisposition>();

    public AnalysisFindingDisposition Disposition
    {
        get => _disposition;
        set
        {
            if (value == AnalysisFindingDisposition.Mixed || _propagating)
                return;
            _propagating = true;
            foreach (var artist in Artists)
                artist.Disposition = value;
            _propagating = false;
            RefreshState();
        }
    }

    private AnalysisProblemGroupViewModel(
        string problem,
        IReadOnlyList<AnalysisArtistGroupViewModel> artists)
    {
        Problem = problem;
        Artists = artists;
        foreach (var artist in Artists)
            artist.PropertyChanged += ArtistChanged;
        _disposition = Aggregate(
            Artists.Select(artist => artist.Disposition));
    }

    public static IReadOnlyList<AnalysisProblemGroupViewModel> Build(
        IReadOnlyList<AnalysisFinding> findings,
        IReadOnlyList<TrackRecord> records,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken ct = default,
        string stage = "Preparing analysis findings")
    {
        var recordsByPath = new Dictionary<string, TrackRecord>(
            StringComparer.OrdinalIgnoreCase);
        progress?.Report(new(0, records.Count, "tracks",
            "Indexing tracks for artwork results"));
        for (int index = 0; index < records.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            TrackRecord record = records[index];
            recordsByPath.TryAdd(record.Path, record);
            int completed = index + 1;
            if ((completed & 127) == 0 || completed == records.Count)
                progress?.Report(new(completed, records.Count, "tracks",
                    "Indexing tracks for artwork results", record.Path));
        }

        var items = new List<AnalysisFindingViewModel>(findings.Count);
        progress?.Report(new(0, findings.Count, "findings", stage));
        for (int index = 0; index < findings.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            AnalysisFinding finding = findings[index];
            items.Add(new AnalysisFindingViewModel(
                finding,
                ArtistLabel(recordsByPath.GetValueOrDefault(finding.Path)),
                AlbumLabel(finding.Path, recordsByPath.GetValueOrDefault(finding.Path))));
            int completed = index + 1;
            if ((completed & 127) == 0 || completed == findings.Count)
                progress?.Report(new(completed, findings.Count, "findings", stage, finding.Path));
        }

        return items
            .GroupBy(item => item.Problem, StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new AnalysisProblemGroupViewModel(
                group.Key,
                group.GroupBy(item => item.Artist, StringComparer.CurrentCultureIgnoreCase)
                    .OrderBy(artist => artist.Key, StringComparer.CurrentCultureIgnoreCase)
                    .Select(artist => new AnalysisArtistGroupViewModel(
                        artist.Key,
                        artist.GroupBy(item => item.Album, StringComparer.CurrentCultureIgnoreCase)
                            .OrderBy(album => album.Key, StringComparer.CurrentCultureIgnoreCase)
                            .Select(album => new AnalysisAlbumGroupViewModel(
                                album.Key,
                                album.OrderBy(item => item.Path, StringComparer.CurrentCultureIgnoreCase).ToList()))
                            .ToList()))
                    .ToList()))
            .ToList();
    }

    private static string ArtistLabel(TrackRecord? record) =>
        record?.EffectiveAlbumArtist ?? "Unknown Artist";

    private static string AlbumLabel(string path, TrackRecord? record)
    {
        if (record is not null)
        {
            string? album = !string.IsNullOrWhiteSpace(record.StrippedAlbum)
                ? record.StrippedAlbum
                : record.Album;
            return string.IsNullOrWhiteSpace(album) ? "Unknown Album" : album;
        }

        var directory = Path.GetDirectoryName(path);
        var folder = string.IsNullOrWhiteSpace(directory) ? null : Path.GetFileName(directory);
        return string.IsNullOrWhiteSpace(folder) ? "Unknown Album" : folder;
    }

    private void ArtistChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(AnalysisArtistGroupViewModel.ActiveCount) or
            nameof(AnalysisArtistGroupViewModel.Disposition)))
            return;
        RefreshState();
    }

    private void RefreshState()
    {
        SetProperty(ref _disposition,
            Aggregate(
                Artists.Select(artist => artist.Disposition)),
            nameof(Disposition));
        OnPropertyChanged(nameof(ActiveCount));
    }

    public static AnalysisFindingDisposition Aggregate(
        IEnumerable<AnalysisFindingDisposition> dispositions)
    {
        AnalysisFindingDisposition[] values = dispositions.Distinct().ToArray();
        return values.Length == 0
            ? AnalysisFindingDisposition.None
            : values.Length == 1 ? values[0] : AnalysisFindingDisposition.Mixed;
    }
}

/// <summary>Findings for one artist within a problem group, divided into albums.</summary>
public sealed class AnalysisArtistGroupViewModel : ViewModelBase
{
    private AnalysisFindingDisposition _disposition;
    private bool _propagating;

    public string Artist { get; }
    public IReadOnlyList<AnalysisAlbumGroupViewModel> Albums { get; }
    public int Count => Albums.Sum(album => album.Count);
    public int ActiveCount => Albums.Sum(album => album.ActiveCount);
    public IReadOnlyList<AnalysisFindingDisposition> Dispositions { get; } =
        Enum.GetValues<AnalysisFindingDisposition>();

    public AnalysisFindingDisposition Disposition
    {
        get => _disposition;
        set
        {
            if (value == AnalysisFindingDisposition.Mixed || _propagating)
                return;
            _propagating = true;
            foreach (var album in Albums)
                album.Disposition = value;
            _propagating = false;
            RefreshState();
        }
    }

    public AnalysisArtistGroupViewModel(
        string artist,
        IReadOnlyList<AnalysisAlbumGroupViewModel> albums)
    {
        Artist = artist;
        Albums = albums;
        foreach (var album in Albums)
            album.PropertyChanged += AlbumChanged;
        _disposition = AnalysisProblemGroupViewModel.Aggregate(
            Albums.Select(album => album.Disposition));
    }

    private void AlbumChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(AnalysisAlbumGroupViewModel.ActiveCount) or
            nameof(AnalysisAlbumGroupViewModel.Disposition)))
            return;
        RefreshState();
    }

    private void RefreshState()
    {
        SetProperty(ref _disposition,
            AnalysisProblemGroupViewModel.Aggregate(
                Albums.Select(album => album.Disposition)),
            nameof(Disposition));
        OnPropertyChanged(nameof(ActiveCount));
    }
}

/// <summary>Findings for one album within a problem group.</summary>
public sealed class AnalysisAlbumGroupViewModel : ViewModelBase
{
    private AnalysisFindingDisposition _disposition;
    private bool _propagating;

    public string Album { get; }
    public IReadOnlyList<AnalysisFindingViewModel> Findings { get; }
    public int Count => Findings.Count;
    public int ActiveCount => Findings.Count(finding =>
        finding.Disposition == AnalysisFindingDisposition.None);
    public IReadOnlyList<AnalysisFindingDisposition> Dispositions { get; } =
        Enum.GetValues<AnalysisFindingDisposition>();

    public AnalysisFindingDisposition Disposition
    {
        get => _disposition;
        set
        {
            if (value == AnalysisFindingDisposition.Mixed || _propagating)
                return;
            _propagating = true;
            foreach (var finding in Findings)
                finding.Disposition = value;
            _propagating = false;
            RefreshState();
        }
    }

    public AnalysisAlbumGroupViewModel(string album, IReadOnlyList<AnalysisFindingViewModel> findings)
    {
        Album = album;
        Findings = findings;
        foreach (var finding in Findings)
            finding.PropertyChanged += FindingChanged;
        _disposition = AggregateFindings();
    }

    private void FindingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AnalysisFindingViewModel.Disposition))
            RefreshState();
    }

    private void RefreshState()
    {
        SetProperty(ref _disposition, AggregateFindings(), nameof(Disposition));
        OnPropertyChanged(nameof(ActiveCount));
    }

    private AnalysisFindingDisposition AggregateFindings() =>
        AnalysisProblemGroupViewModel.Aggregate(
            Findings.Select(finding => finding.Disposition));
}

/// <summary>A finding plus its review disposition within a retained analysis run.</summary>
public partial class AnalysisFindingViewModel : ViewModelBase
{
    public AnalysisFinding Finding { get; }
    public string Path => Finding.Path;
    public string Description => Finding.Description;
    public string Problem => Finding.Problem ?? Finding.Description;
    public string Artist { get; }
    public string Album { get; }
    public IReadOnlyList<AnalysisFindingDisposition> Dispositions { get; } =
        Enum.GetValues<AnalysisFindingDisposition>()
            .Where(value => value != AnalysisFindingDisposition.Mixed)
            .ToArray();

    [ObservableProperty]
    private AnalysisFindingDisposition _disposition;

    public AnalysisFindingViewModel(AnalysisFinding finding, string artist, string album)
    {
        Finding = finding;
        Artist = artist;
        Album = album;
    }
}
