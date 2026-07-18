using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MusicLibrary.Core.Models;

namespace MusicLibrary.App.ViewModels;

/// <summary>What the user has decided to do with an analysis finding.</summary>
public enum AnalysisFindingDisposition
{
    Active,
    Completed,
    Deferred,
    Ignored,
}

/// <summary>
/// Review state for an applicable repair. Mixed is calculated for tree branches and is never
/// assigned to a leaf.
/// </summary>
public enum AnalysisRepairDisposition
{
    Active,
    Completed,
    Deferred,
    Ignored,
    Mixed,
}

/// <summary>
/// An immutable analysis result snapshot. The contained finding and action view models remain
/// mutable so review state survives while the user navigates between runs.
/// </summary>
public sealed class AnalysisRunViewModel : ViewModelBase
{
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
    public AnalysisRepairPlan? RepairPlan { get; }
    public int Count { get; }

    public int ActiveFindingCount => FindingGroups.Sum(group => group.ActiveCount);
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
        IReadOnlyList<AlbumMetadataMatrix>? matrices = null)
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

        foreach (var group in FindingGroups)
            group.PropertyChanged += FindingGroupChanged;
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

    private void FindingGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AnalysisProblemGroupViewModel.ActiveCount))
            OnPropertyChanged(nameof(ActiveFindingCount));
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
            foreach (var item in Items.Where(item => item.CanChangeDisposition))
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
            Items.Where(item => item.Repair.CanApply).Select(item => item.Disposition));
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
                Items.Where(item => item.Repair.CanApply).Select(item => item.Disposition)),
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
    private AnalysisRepairDisposition _disposition;
    private bool _propagating;

    public string Problem { get; }
    public IReadOnlyList<AnalysisArtistGroupViewModel> Artists { get; }
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

    private AnalysisProblemGroupViewModel(
        string problem,
        IReadOnlyList<AnalysisArtistGroupViewModel> artists)
    {
        Problem = problem;
        Artists = artists;
        foreach (var artist in Artists)
            artist.PropertyChanged += ArtistChanged;
        _disposition = AnalysisRepairCategoryGroupViewModel.Aggregate(
            Artists.Select(artist => artist.Disposition));
    }

    public static IReadOnlyList<AnalysisProblemGroupViewModel> Build(
        IReadOnlyList<AnalysisFinding> findings,
        IReadOnlyList<TrackRecord> records)
    {
        var recordsByPath = records
            .GroupBy(record => record.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return findings
            .Select(finding => new AnalysisFindingViewModel(
                finding,
                ArtistLabel(recordsByPath.GetValueOrDefault(finding.Path)),
                AlbumLabel(finding.Path, recordsByPath.GetValueOrDefault(finding.Path))))
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
            AnalysisRepairCategoryGroupViewModel.Aggregate(
                Artists.Select(artist => artist.Disposition)),
            nameof(Disposition));
        OnPropertyChanged(nameof(ActiveCount));
    }
}

/// <summary>Findings for one artist within a problem group, divided into albums.</summary>
public sealed class AnalysisArtistGroupViewModel : ViewModelBase
{
    private AnalysisRepairDisposition _disposition;
    private bool _propagating;

    public string Artist { get; }
    public IReadOnlyList<AnalysisAlbumGroupViewModel> Albums { get; }
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

    public AnalysisArtistGroupViewModel(
        string artist,
        IReadOnlyList<AnalysisAlbumGroupViewModel> albums)
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
        if (e.PropertyName is not (nameof(AnalysisAlbumGroupViewModel.ActiveCount) or
            nameof(AnalysisAlbumGroupViewModel.Disposition)))
            return;
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

/// <summary>Findings for one album within a problem group.</summary>
public sealed class AnalysisAlbumGroupViewModel : ViewModelBase
{
    private AnalysisRepairDisposition _disposition;
    private bool _propagating;

    public string Album { get; }
    public IReadOnlyList<AnalysisFindingViewModel> Findings { get; }
    public int Count => Findings.Count;
    public int ActiveCount => Findings.Count(finding => finding.Disposition == AnalysisFindingDisposition.Active);
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
            AnalysisFindingDisposition findingDisposition = value switch
            {
                AnalysisRepairDisposition.Completed => AnalysisFindingDisposition.Completed,
                AnalysisRepairDisposition.Deferred => AnalysisFindingDisposition.Deferred,
                AnalysisRepairDisposition.Ignored => AnalysisFindingDisposition.Ignored,
                _ => AnalysisFindingDisposition.Active,
            };
            foreach (var finding in Findings)
                finding.Disposition = findingDisposition;
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

    private AnalysisRepairDisposition AggregateFindings() =>
        AnalysisRepairCategoryGroupViewModel.Aggregate(Findings.Select(finding =>
            finding.Disposition switch
            {
                AnalysisFindingDisposition.Completed => AnalysisRepairDisposition.Completed,
                AnalysisFindingDisposition.Deferred => AnalysisRepairDisposition.Deferred,
                AnalysisFindingDisposition.Ignored => AnalysisRepairDisposition.Ignored,
                _ => AnalysisRepairDisposition.Active,
            }));
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
        Enum.GetValues<AnalysisFindingDisposition>();

    [ObservableProperty]
    private AnalysisFindingDisposition _disposition;

    public AnalysisFindingViewModel(AnalysisFinding finding, string artist, string album)
    {
        Finding = finding;
        Artist = artist;
        Album = album;
    }
}
