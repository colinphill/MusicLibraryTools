using CommunityToolkit.Mvvm.ComponentModel;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;

namespace MusicLibraryManager.Presentation;

public enum ArtworkRepairKind
{
    NormalizeAlbum,
    NormalizeFile,
}

public enum ArtworkCandidateSelectionRule
{
    First,
    HighestResolution,
    LargestFile,
}

/// <summary>A previewed embedded image that loads its bytes and thumbnail only when realized.</summary>
public partial class ArtworkRepairCandidateViewModel(
    string sourcePath,
    string label,
    string hash,
    string details,
    int width,
    int height,
    long size,
    ILibraryService library,
    IThumbnailService? thumbnails) : ViewModelBase
{
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private bool _dataLoaded;

    public string SourcePath { get; } = sourcePath;
    public string Label { get; } = label;
    public string Hash { get; } = hash;
    public string Details { get; } = details;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public long Size { get; } = size;
    public long PixelCount => (long)Width * Height;

    [ObservableProperty]
    private byte[]? _data;

    [ObservableProperty]
    private object? _imageSource;

    public async Task<byte[]?> EnsureDataAsync(CancellationToken ct = default)
    {
        if (_dataLoaded)
            return Data;
        await _loadGate.WaitAsync(ct);
        try
        {
            if (!_dataLoaded)
            {
                Data = await library.GetFirstImageAsync(SourcePath, ct);
                _dataLoaded = true;
            }
            return Data;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public async Task EnsureThumbnailAsync(CancellationToken ct = default)
    {
        byte[]? data = await EnsureDataAsync(ct);
        if (data is null || data.Length == 0 || thumbnails is null || ImageSource is not null)
            return;
        await _loadGate.WaitAsync(ct);
        try
        {
            if (ImageSource is null)
                ImageSource = await thumbnails.CreateImageSourceAsync(data, 180, ct);
        }
        finally
        {
            _loadGate.Release();
        }
    }
}

/// <summary>One reviewed artwork normalization action, potentially affecting an entire album.</summary>
public partial class ArtworkRepairItemViewModel : ViewModelBase
{
    public ArtworkRepairKind Kind { get; }
    public string Title { get; }
    public string Artist { get; }
    public string Album { get; }
    public string Description { get; }
    public IReadOnlyList<ArtistPathViewModel> AffectedPaths { get; }
    public IReadOnlyList<ArtworkRepairCandidateViewModel> Candidates { get; }
    public bool ShowGallery { get; }
    public string? BlockingReason { get; }
    public int MaximumBytes { get; }
    public int MaximumDimension { get; }
    public int FileCount => AffectedPaths.Count;
    public bool CanApply => BlockingReason is null && SelectedCandidate is not null;
    public bool CanChangeDisposition => !IsApplied;
    public bool IsActive => CanApply && CanChangeDisposition &&
        Disposition == AnalysisRepairDisposition.Active;
    public string TargetSummary =>
        $"Target: at most {MaximumDimension:N0}px and {MaximumBytes / 1024d / 1024d:0.##} MiB";
    public IReadOnlyList<AnalysisRepairDisposition> Dispositions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    private AnalysisRepairDisposition _disposition = AnalysisRepairDisposition.Ignored;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    private ArtworkRepairCandidateViewModel? _selectedCandidate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeDisposition))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    private bool _isApplied;

    [ObservableProperty]
    private string? _resultText;

    public event Action? StateChanged;

    public ArtworkRepairItemViewModel(
        ArtworkRepairKind kind,
        string title,
        string description,
        IReadOnlyList<string> affectedPaths,
        IReadOnlyList<ArtworkRepairCandidateViewModel> candidates,
        bool showGallery,
        int maximumBytes,
        int maximumDimension,
        string? blockingReason = null,
        string? artist = null,
        string? album = null)
    {
        Kind = kind;
        Title = title;
        Artist = string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist;
        Album = string.IsNullOrWhiteSpace(album) ? "Unknown Album" : album;
        Description = description;
        AffectedPaths = affectedPaths.Select(path => new ArtistPathViewModel(path)).ToList();
        Candidates = candidates;
        ShowGallery = showGallery;
        MaximumBytes = maximumBytes;
        MaximumDimension = maximumDimension;
        BlockingReason = blockingReason;
        _selectedCandidate = candidates.FirstOrDefault();
        Dispositions = Enum.GetValues<AnalysisRepairDisposition>()
            .Where(value => value != AnalysisRepairDisposition.Mixed &&
                (CanApply || value is not (AnalysisRepairDisposition.Active or
                    AnalysisRepairDisposition.Completed)))
            .ToArray();
    }

    public bool SelectCandidateAndActivate(ArtworkCandidateSelectionRule rule)
    {
        if (!CanChangeDisposition || BlockingReason is not null || Candidates.Count == 0)
            return false;

        SelectedCandidate = rule switch
        {
            ArtworkCandidateSelectionRule.HighestResolution => Candidates
                .OrderByDescending(candidate => candidate.PixelCount)
                .ThenByDescending(candidate => candidate.Size)
                .First(),
            ArtworkCandidateSelectionRule.LargestFile => Candidates
                .OrderByDescending(candidate => candidate.Size)
                .ThenByDescending(candidate => candidate.PixelCount)
                .First(),
            _ => Candidates[0],
        };
        if (!CanApply || !Dispositions.Contains(AnalysisRepairDisposition.Active))
            return false;
        Disposition = AnalysisRepairDisposition.Active;
        return true;
    }

    partial void OnDispositionChanged(AnalysisRepairDisposition value) => StateChanged?.Invoke();
    partial void OnSelectedCandidateChanged(ArtworkRepairCandidateViewModel? value) => StateChanged?.Invoke();
    partial void OnIsAppliedChanged(bool value) => StateChanged?.Invoke();
}

/// <summary>Common disposition behavior for artwork repair hierarchy branches.</summary>
public abstract class ArtworkRepairGroupViewModel : ViewModelBase
{
    private AnalysisRepairDisposition _disposition;
    private bool _propagating;

    public IReadOnlyList<ArtworkRepairItemViewModel> DescendantItems { get; }
    public int Count => DescendantItems.Count;
    public int ActiveCount => DescendantItems.Count(item => item.IsActive);
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
            foreach (ArtworkRepairItemViewModel item in DescendantItems.Where(item =>
                         item.CanChangeDisposition && item.Dispositions.Contains(value)))
                item.Disposition = value;
            _propagating = false;
            RefreshState();
        }
    }

    protected ArtworkRepairGroupViewModel(IReadOnlyList<ArtworkRepairItemViewModel> items)
    {
        DescendantItems = items;
        foreach (ArtworkRepairItemViewModel item in items)
            item.PropertyChanged += ItemChanged;
        _disposition = Aggregate(items.Select(item => item.Disposition));
    }

    private void ItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ArtworkRepairItemViewModel.Disposition) or
            nameof(ArtworkRepairItemViewModel.IsActive) or
            nameof(ArtworkRepairItemViewModel.IsApplied) or
            nameof(ArtworkRepairItemViewModel.SelectedCandidate))
            RefreshState();
    }

    private void RefreshState()
    {
        SetProperty(ref _disposition, Aggregate(DescendantItems.Select(item => item.Disposition)),
            nameof(Disposition));
        OnPropertyChanged(nameof(ActiveCount));
    }

    private static AnalysisRepairDisposition Aggregate(
        IEnumerable<AnalysisRepairDisposition> dispositions)
    {
        AnalysisRepairDisposition[] values = dispositions.Distinct().ToArray();
        return values.Length == 0 ? AnalysisRepairDisposition.Ignored
            : values.Length == 1 ? values[0]
            : AnalysisRepairDisposition.Mixed;
    }
}

public sealed class ArtworkRepairCategoryGroupViewModel : ArtworkRepairGroupViewModel
{
    public string Category { get; }
    public ArtworkRepairKind Kind { get; }
    public IReadOnlyList<ArtworkRepairArtistGroupViewModel> Artists { get; }

    private ArtworkRepairCategoryGroupViewModel(
        ArtworkRepairKind kind,
        IReadOnlyList<ArtworkRepairItemViewModel> items,
        IReadOnlyList<ArtworkRepairArtistGroupViewModel> artists) : base(items)
    {
        Kind = kind;
        Category = kind == ArtworkRepairKind.NormalizeAlbum
            ? "Mixed album artwork"
            : "File artwork";
        Artists = artists;
    }

    public static IReadOnlyList<ArtworkRepairCategoryGroupViewModel> Build(
        IReadOnlyList<ArtworkRepairItemViewModel> items,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken ct = default)
    {
        var result = new List<ArtworkRepairCategoryGroupViewModel>();
        int completed = 0;
        progress?.Report(new(0, items.Count, "repair actions", "Grouping artwork repairs"));
        foreach (IGrouping<ArtworkRepairKind, ArtworkRepairItemViewModel> category in items
                     .GroupBy(item => item.Kind).OrderBy(group => group.Key))
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<ArtworkRepairItemViewModel> categoryItems = category
                .OrderBy(item => item.Artist, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Album, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            IReadOnlyList<ArtworkRepairArtistGroupViewModel> artists = categoryItems
                .GroupBy(item => item.Artist, StringComparer.CurrentCultureIgnoreCase)
                .Select(artist => new ArtworkRepairArtistGroupViewModel(
                    artist.Key,
                    artist.GroupBy(item => item.Album, StringComparer.CurrentCultureIgnoreCase)
                        .Select(album => new ArtworkRepairAlbumGroupViewModel(
                            album.Key, album.ToList()))
                        .ToList()))
                .ToList();
            result.Add(new ArtworkRepairCategoryGroupViewModel(
                category.Key, categoryItems, artists));
            completed += categoryItems.Count;
            progress?.Report(new(completed, items.Count, "repair actions",
                "Grouping artwork repairs", category.Key.ToString()));
        }
        return result;
    }
}

public sealed class ArtworkRepairArtistGroupViewModel : ArtworkRepairGroupViewModel
{
    public string Artist { get; }
    public IReadOnlyList<ArtworkRepairAlbumGroupViewModel> Albums { get; }

    public ArtworkRepairArtistGroupViewModel(
        string artist,
        IReadOnlyList<ArtworkRepairAlbumGroupViewModel> albums)
        : base(albums.SelectMany(album => album.Items).ToList())
    {
        Artist = artist;
        Albums = albums;
    }
}

public sealed class ArtworkRepairAlbumGroupViewModel : ArtworkRepairGroupViewModel
{
    public string Album { get; }
    public IReadOnlyList<ArtworkRepairItemViewModel> Items { get; }

    public ArtworkRepairAlbumGroupViewModel(
        string album,
        IReadOnlyList<ArtworkRepairItemViewModel> items) : base(items)
    {
        Album = album;
        Items = items;
    }
}

/// <summary>Creates cache-derived artwork actions and loads only the gallery images they need.</summary>
public static class ArtworkRepairPlanner
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static Task<IReadOnlyList<ArtworkRepairItemViewModel>> BuildAsync(
        IReadOnlyList<TrackRecord> records,
        IReadOnlyList<ArtworkAuditFile> artwork,
        LibraryArtworkHealthSettings settings,
        ILibraryService library,
        IArtworkService artworkService,
        IThumbnailService? thumbnails,
        CancellationToken ct = default)
        => BuildAsync(records, artwork, settings, library, artworkService, thumbnails,
            null, ct);

    public static Task<IReadOnlyList<ArtworkRepairItemViewModel>> BuildAsync(
        IReadOnlyList<TrackRecord> records,
        IReadOnlyList<ArtworkAuditFile> artwork,
        LibraryArtworkHealthSettings settings,
        ILibraryService library,
        IArtworkService artworkService,
        IThumbnailService? thumbnails,
        LibraryConfiguration? configuration,
        CancellationToken ct = default)
        => BuildAsync(records, artwork, settings, library, artworkService, thumbnails,
            configuration, null, ct);

    public static Task<IReadOnlyList<ArtworkRepairItemViewModel>> BuildAsync(
        IReadOnlyList<TrackRecord> records,
        IReadOnlyList<ArtworkAuditFile> artwork,
        LibraryArtworkHealthSettings settings,
        ILibraryService library,
        IArtworkService artworkService,
        IThumbnailService? thumbnails,
        LibraryConfiguration? configuration,
        IProgress<AnalysisProgress>? progress,
        CancellationToken ct = default)
    {
        var byPath = new Dictionary<string, ArtworkAuditFile>(artwork.Count, PathComparer);
        progress?.Report(new(0, artwork.Count, "files", "Indexing artwork audit"));
        for (int index = 0; index < artwork.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            ArtworkAuditFile file = artwork[index];
            byPath.Add(file.Path, file);
            int completed = index + 1;
            if ((completed & 127) == 0 || completed == artwork.Count)
                progress?.Report(new(completed, artwork.Count, "files",
                    "Indexing artwork audit", file.Path));
        }
        var recordsByPath = new Dictionary<string, TrackRecord>(records.Count, PathComparer);
        progress?.Report(new(0, records.Count, "tracks", "Indexing tracks for artwork repairs"));
        for (int index = 0; index < records.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            TrackRecord record = records[index];
            recordsByPath.Add(record.Path, record);
            int completed = index + 1;
            if ((completed & 127) == 0 || completed == records.Count)
                progress?.Report(new(completed, records.Count, "tracks",
                    "Indexing tracks for artwork repairs", record.Path));
        }
        var plans = new List<PlannedAction>();
        var coveredPaths = new HashSet<string>(PathComparer);

        Func<TrackRecord, string> albumKey = configuration is null
            ? AlbumKey
            : record => LibraryAlbumIdentityResolver.Key(record, configuration);
        int completedTracks = 0;
        int lastReportedTracks = 0;
        progress?.Report(new(0, records.Count, "tracks", "Planning album artwork repairs"));
        foreach (IGrouping<string, TrackRecord> album in records.GroupBy(albumKey))
        {
            ct.ThrowIfCancellationRequested();
            int albumTrackCount = album.Count();
            var scanned = album
                .Select(record => (Record: record, Artwork: byPath.GetValueOrDefault(record.Path)))
                .Where(item => item.Artwork?.ArtworkScanned == true)
                .ToList();
            if (scanned.Count < 2)
            {
                CompleteAlbum();
                continue;
            }

            int signatureCount = scanned.Select(item => Signature(item.Artwork!))
                .Distinct(StringComparer.Ordinal).Count();
            bool hasMissing = scanned.Any(item => item.Artwork!.Images.Count == 0);
            if (signatureCount <= 1 && !hasMissing)
            {
                CompleteAlbum();
                continue;
            }

            CandidateDescriptor[] candidates = scanned
                .Select(item => Candidate(item.Record, item.Artwork!))
                .Where(candidate => candidate is not null)
                .Cast<CandidateDescriptor>()
                .GroupBy(candidate => candidate.Hash, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            string artist = MostCommon(scanned.Select(item => item.Record.EffectiveAlbumArtist));
            string albumName = MostCommon(scanned.Select(item => item.Record.Album));
            string[] targets = scanned.Select(item => item.Record.Path)
                .Distinct(PathComparer).ToArray();
            foreach (string path in targets)
                coveredPaths.Add(path);
            plans.Add(new(
                ArtworkRepairKind.NormalizeAlbum,
                $"{artist} — {albumName}",
                hasMissing
                    ? "Replace every scanned track's embedded-artwork set with the selected album image, including tracks whose artwork is missing."
                    : "Replace every track's embedded-artwork set with the selected album image.",
                targets,
                candidates,
                artist,
                albumName,
                ShowGallery: true));
            CompleteAlbum();

            void CompleteAlbum()
            {
                completedTracks += albumTrackCount;
                if (completedTracks - lastReportedTracks < 128 && completedTracks != records.Count)
                    return;
                progress?.Report(new(completedTracks, records.Count, "tracks",
                    "Planning album artwork repairs"));
                lastReportedTracks = completedTracks;
            }
        }

        progress?.Report(new(0, artwork.Count, "files", "Planning file artwork repairs"));
        for (int fileIndex = 0; fileIndex < artwork.Count; fileIndex++)
        {
            ArtworkAuditFile file = artwork[fileIndex];
            ct.ThrowIfCancellationRequested();
            if (file.ArtworkScanned && !coveredPaths.Contains(file.Path))
            {
                bool oversized = file.Images.Any(image => image.Size > settings.OversizedByteThreshold ||
                    image.Width > settings.OversizedDimensionThreshold ||
                    image.Height > settings.OversizedDimensionThreshold);
                bool duplicates = file.Images.Where(image => !string.IsNullOrWhiteSpace(image.Hash))
                    .GroupBy(image => image.Hash, StringComparer.Ordinal).Any(group => group.Count() > 1);
                bool unreadable = file.Images.Any(image =>
                    string.IsNullOrWhiteSpace(image.Hash) ||
                    string.IsNullOrWhiteSpace(image.ImageType) || image.Width <= 0 ||
                    image.Height <= 0 || image.Size <= 0);
                if (oversized || duplicates || unreadable)
                {
                    TrackRecord? record = recordsByPath.GetValueOrDefault(file.Path);
                    CandidateDescriptor? candidate = record is null ? null : Candidate(record, file);
                    string artist = string.IsNullOrWhiteSpace(record?.EffectiveAlbumArtist)
                        ? "Unknown Artist"
                        : record.EffectiveAlbumArtist!;
                    string? taggedAlbum = record is null
                        ? null
                        : !string.IsNullOrWhiteSpace(record.StrippedAlbum)
                            ? record.StrippedAlbum
                            : record.Album;
                    string albumName = string.IsNullOrWhiteSpace(taggedAlbum)
                        ? Path.GetFileName(Path.GetDirectoryName(file.Path)) ?? "Unknown Album"
                        : taggedAlbum;
                    plans.Add(new(
                        ArtworkRepairKind.NormalizeFile,
                        Path.GetFileName(file.Path),
                        unreadable
                            ? "Re-encode the first readable image and replace the invalid embedded-artwork set."
                            : oversized && duplicates
                            ? "Re-encode the first image within the configured limits and remove duplicate embedded images."
                            : oversized
                                ? "Re-encode the first image within the configured artwork limits."
                                : "Keep the first image and remove duplicate embedded images.",
                        [file.Path],
                        candidate is null ? [] : [candidate],
                        artist,
                        albumName,
                        ShowGallery: false));
                }
            }
            int completedFiles = fileIndex + 1;
            if ((completedFiles & 127) == 0 || completedFiles == artwork.Count)
                progress?.Report(new(completedFiles, artwork.Count, "files",
                    "Planning file artwork repairs", file.Path));
        }

        var result = new List<ArtworkRepairItemViewModel>(plans.Count);
        progress?.Report(new(0, plans.Count, "repair actions", "Preparing artwork repair choices"));
        for (int planIndex = 0; planIndex < plans.Count; planIndex++)
        {
            PlannedAction plan = plans[planIndex];
            var candidates = plan.Candidates
                .Select(candidate => new ArtworkRepairCandidateViewModel(
                    candidate.Path, candidate.Label, candidate.Hash, candidate.Details,
                    candidate.Width, candidate.Height, candidate.Size,
                    library, thumbnails))
                .ToList();
            string[] unsupported = plan.Paths.Where(path => !artworkService.SupportsWrite(path)).ToArray();
            string? blocking = unsupported.Length > 0
                ? $"Artwork writing is not supported for {unsupported.Length:N0} affected file(s)."
                : candidates.Count == 0
                    ? "No readable source image is available for this repair."
                    : null;
            result.Add(new ArtworkRepairItemViewModel(
                plan.Kind, plan.Title, plan.Description, plan.Paths, candidates,
                plan.ShowGallery, settings.RepairTargetByteSize,
                settings.RepairTargetDimension, blocking, plan.Artist, plan.Album));
            int completedPlans = planIndex + 1;
            if ((completedPlans & 63) == 0 || completedPlans == plans.Count)
                progress?.Report(new(completedPlans, plans.Count, "repair actions",
                    "Preparing artwork repair choices", plan.Title));
        }
        return Task.FromResult<IReadOnlyList<ArtworkRepairItemViewModel>>(result);
    }

    private static CandidateDescriptor? Candidate(TrackRecord record, ArtworkAuditFile artwork)
    {
        ArtworkAuditImage? image = artwork.Images.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate.Hash));
        return image is null ? null : new CandidateDescriptor(
            record.Path,
            image.Hash,
            string.IsNullOrWhiteSpace(record.Title) ? Path.GetFileName(record.Path) : record.Title!,
            $"{image.Width:N0}×{image.Height:N0} · {image.Size / 1024d:0.#} KiB · {Path.GetFileName(record.Path)}",
            image.Width,
            image.Height,
            image.Size);
    }

    private static string Signature(ArtworkAuditFile file) => string.Join("|", file.Images
        .Select(image => image.Hash).OrderBy(hash => hash, StringComparer.Ordinal));

    private static string AlbumKey(TrackRecord record) =>
        Normalize(record.EffectiveAlbumArtist) + "\0" + Normalize(record.StrippedAlbum ?? record.Album);

    private static string Normalize(string? value) => string.Join(' ', (value ?? "").Trim()
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static string MostCommon(IEnumerable<string?> values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .GroupBy(value => value!, StringComparer.CurrentCultureIgnoreCase)
        .OrderByDescending(group => group.Count())
        .Select(group => group.First()!)
        .FirstOrDefault() ?? "(unknown)";

    private sealed record CandidateDescriptor(
        string Path,
        string Hash,
        string Label,
        string Details,
        int Width,
        int Height,
        long Size);

    private sealed record PlannedAction(
        ArtworkRepairKind Kind,
        string Title,
        string Description,
        IReadOnlyList<string> Paths,
        IReadOnlyList<CandidateDescriptor> Candidates,
        string Artist,
        string Album,
        bool ShowGallery);
}
