using System.Security.Cryptography;
using iTunes.Binary;
using MetadataCaching;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public sealed record ItlMetadataDifference(string Field, string? Before, string? After);

public sealed record ItlMetadataRepairItem(
    Guid Id,
    int TrackId,
    ulong PersistentId,
    string Path,
    ItlCachedTrackMetadata Metadata,
    DateTime CacheLastWriteTimeUtc,
    IReadOnlyList<ItlMetadataDifference> Differences);

public sealed record ItlMetadataRepairPlan(
    string LibraryPath,
    string LibrarySha256,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ItlMetadataRepairItem> Items);

public enum ItlMetadataRepairOutcome
{
    Applied,
    Skipped,
    Failed,
}

public sealed record ItlMetadataRepairItemResult(
    ItlMetadataRepairItem Item,
    ItlMetadataRepairOutcome Outcome,
    string? Error = null);

public sealed record ItlMetadataRepairApplyResult(
    string LibraryPath,
    IReadOnlyList<ItlMetadataRepairItemResult> Items)
{
    public int Applied => Items.Count(item => item.Outcome == ItlMetadataRepairOutcome.Applied);
    public int Skipped => Items.Count(item => item.Outcome == ItlMetadataRepairOutcome.Skipped);
    public int Failed => Items.Count(item => item.Outcome == ItlMetadataRepairOutcome.Failed);
}

public interface IItlMetadataRepairService
{
    Task<ItlMetadataRepairPlan> PreviewAsync(
        string? configurationPath = null,
        string? itunesLibraryPath = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    Task<ItlMetadataRepairApplyResult> ApplyAsync(
        ItlMetadataRepairPlan plan,
        IReadOnlyCollection<Guid> selectedItemIds,
        IProgress<int>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Compares local-track tag fields in an ITL with the indexed metadata cache and applies reviewed
/// repairs atomically. The plan is pinned to a hash of the ITL so a stale preview can never
/// overwrite changes made by iTunes or another operation.
/// </summary>
public sealed class ItlMetadataRepairService(
    ILibraryOperationContextFactory contextFactory) : IItlMetadataRepairService
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public async Task<ItlMetadataRepairPlan> PreviewAsync(
        string? configurationPath = null,
        string? itunesLibraryPath = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        LibraryOperationContext context = await contextFactory.CreateAsync(
            configurationPath, itunesLibraryPath, progress, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        string hash = await ComputeSha256Async(context.ItunesLibraryPath, ct).ConfigureAwait(false);
        ItlDocument document = ItlDocument.Parse(context.ItunesLibrary.Envelope);
        Dictionary<string, MetadataCacheEntry> cacheByPath = BuildPathIndex(context.Cache);
        Dictionary<uint, ItlRecord> albums = document.Albums.ToDictionary(ItlDocument.RecordIdOf);
        Dictionary<uint, ItlRecord> artists = document.Artists.ToDictionary(ItlDocument.RecordIdOf);
        var items = new List<ItlMetadataRepairItem>();

        foreach (ItlRecord track in document.Tracks)
        {
            ct.ThrowIfCancellationRequested();
            string? localPath = ItlLocation.ToLocalPath(track.GetString(ItlDataType.Location));
            if (localPath is null || !TryNormalizePath(localPath, out string normalized) ||
                !cacheByPath.TryGetValue(normalized, out MetadataCacheEntry? entry))
                continue;

            ItlCachedTrackMetadata metadata = FromCache(entry);
            IReadOnlyList<ItlMetadataDifference> differences = FindDifferences(
                track, metadata, albums, artists);
            if (differences.Count == 0)
                continue;

            items.Add(new(
                Guid.NewGuid(),
                track.GetTrackId(),
                track.GetPersistentId(),
                normalized,
                metadata,
                entry.LastWriteTime.Kind == DateTimeKind.Utc
                    ? entry.LastWriteTime
                    : entry.LastWriteTime.ToUniversalTime(),
                differences));
        }

        return new(context.ItunesLibraryPath, hash, DateTimeOffset.UtcNow, items);
    }

    public async Task<ItlMetadataRepairApplyResult> ApplyAsync(
        ItlMetadataRepairPlan plan,
        IReadOnlyCollection<Guid> selectedItemIds,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(selectedItemIds);
        HashSet<Guid> selected = selectedItemIds.ToHashSet();
        ItlMetadataRepairItem[] items = [.. plan.Items.Where(item => selected.Contains(item.Id))];
        if (items.Length == 0)
            return new(plan.LibraryPath, []);

        ItlFileEditor.EnsureItunesIsClosed();
        string currentHash = await ComputeSha256Async(plan.LibraryPath, ct).ConfigureAwait(false);
        if (!string.Equals(currentHash, plan.LibrarySha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The iTunes library changed after this preview. Preview the repairs again before applying them.");

        ItlDocument document = await Task.Run(() => ItlDocument.Load(plan.LibraryPath), ct)
            .ConfigureAwait(false);
        var pending = new List<(ItlMetadataRepairItem Item, ItlRecord Track)>();
        var results = new List<ItlMetadataRepairItemResult>(items.Length);
        foreach (ItlMetadataRepairItem item in items)
        {
            ct.ThrowIfCancellationRequested();
            ItlRecord? track = document.FindTrackByPersistentId(item.PersistentId);
            if (track is null || track.GetTrackId() != item.TrackId)
            {
                results.Add(new(item, ItlMetadataRepairOutcome.Failed,
                    "The track identity no longer exists in the library."));
                continue;
            }
            string? currentPath = ItlLocation.ToLocalPath(track.GetString(ItlDataType.Location));
            if (currentPath is null || !TryNormalizePath(currentPath, out string normalized) ||
                !PathComparer.Equals(normalized, item.Path))
            {
                results.Add(new(item, ItlMetadataRepairOutcome.Failed,
                    "The track path changed after preview."));
                continue;
            }
            pending.Add((item, track));
        }

        if (results.Count > 0)
            return new(plan.LibraryPath, results);

        foreach ((ItlMetadataRepairItem item, ItlRecord track) in pending)
        {
            ct.ThrowIfCancellationRequested();
            document.RepairLocalTrackFromCache(track, item.Metadata, item.CacheLastWriteTimeUtc);
        }

        await Task.Run(() => ItlFileEditor.SaveValidated(document, plan.LibraryPath), ct)
            .ConfigureAwait(false);
        int completed = 0;
        foreach ((ItlMetadataRepairItem item, _) in pending)
        {
            results.Add(new(item, ItlMetadataRepairOutcome.Applied));
            progress?.Report(++completed);
        }
        return new(plan.LibraryPath, results);
    }

    private static Dictionary<string, MetadataCacheEntry> BuildPathIndex(MetadataCache cache)
    {
        var result = new Dictionary<string, MetadataCacheEntry>(PathComparer);
        foreach ((string path, MetadataCacheEntry entry) in cache.FileCache)
            if (TryNormalizePath(path, out string normalized))
                result.TryAdd(normalized, entry);
        return result;
    }

    private static ItlCachedTrackMetadata FromCache(MetadataCacheEntry entry) => new()
    {
        Title = Clean(entry.Title),
        Artist = Clean(entry.Artist),
        AlbumArtist = entry.HasAlbumArtist ? Clean(entry.AlbumArtist) : null,
        HasExplicitAlbumArtist = entry.HasAlbumArtist,
        Album = Clean(entry.Album),
        TrackNumber = entry.TrackNumber,
        TrackCount = entry.TrackTotal,
        DiscNumber = entry.DiscNumber,
        DiscCount = entry.DiscTotal,
        Year = ParseYear(entry.ReleaseDate),
        Compilation = entry.Compilation,
    };

    private static IReadOnlyList<ItlMetadataDifference> FindDifferences(
        ItlRecord track,
        ItlCachedTrackMetadata expected,
        IReadOnlyDictionary<uint, ItlRecord> albums,
        IReadOnlyDictionary<uint, ItlRecord> artists)
    {
        var result = new List<ItlMetadataDifference>();
        AddText("Title", track.GetString(ItlDataType.Title), expected.Title);
        AddText("Artist", track.GetString(ItlDataType.Artist), expected.Artist);
        AddText("Album artist", track.GetString(ItlDataType.AlbumArtist),
            expected.HasExplicitAlbumArtist ? expected.AlbumArtist : null);
        AddText("Album", track.GetString(ItlDataType.Album), expected.Album);
        AddNumber("Track number", track.GetTrackNumber(), expected.TrackNumber);
        AddNumber("Track total", track.GetTrackCount(), expected.TrackCount);
        AddNumber("Disc number", track.GetDiscNumber(), expected.DiscNumber);
        AddNumber("Disc total", track.GetDiscCount(), expected.DiscCount);
        AddNumber("Year", track.GetYear(), expected.Year);
        if (track.GetCompilation() != expected.Compilation)
            result.Add(new("Compilation", YesNo(track.GetCompilation()), YesNo(expected.Compilation)));

        string? effectiveArtist = expected.HasExplicitAlbumArtist
            ? expected.AlbumArtist ?? expected.Artist
            : expected.Artist;
        albums.TryGetValue(track.GetAlbumId(), out ItlRecord? albumRecord);
        string? linkedAlbum = albumRecord?.Field((int)ItlDataType.AlbumRecordName)?.Text;
        string? linkedAlbumArtist = albumRecord?.Field((int)ItlDataType.AlbumRecordArtist)?.Text;
        if (!Same(linkedAlbum, expected.Album) || !Same(linkedAlbumArtist, effectiveArtist))
            result.Add(new("Album link", EntityLabel(linkedAlbum, linkedAlbumArtist),
                EntityLabel(expected.Album, effectiveArtist)));

        artists.TryGetValue(track.GetArtistId(), out ItlRecord? artistRecord);
        string? linkedArtist = artistRecord?.Field((int)ItlDataType.ArtistRecordName)?.Text;
        if (!Same(linkedArtist, effectiveArtist))
            result.Add(new("Artist link", linkedArtist, effectiveArtist));
        return result;

        void AddText(string field, string? before, string? after)
        {
            before = Clean(before);
            if (!Same(before, after))
                result.Add(new(field, before, after));
        }

        void AddNumber(string field, int before, int? after)
        {
            int expectedValue = after.GetValueOrDefault();
            if (before != expectedValue)
                result.Add(new(field, before == 0 ? null : before.ToString(),
                    expectedValue == 0 ? null : expectedValue.ToString()));
        }
    }

    private static string? EntityLabel(string? name, string? artist) =>
        name is null && artist is null ? null : $"{name ?? "(missing)"} — {artist ?? "(missing)"}";

    private static bool Same(string? left, string? right) =>
        string.Equals(Clean(left), Clean(right), StringComparison.Ordinal);

    private static string YesNo(bool value) => value ? "Yes" : "No";

    private static int? ParseYear(string? value)
    {
        value = Clean(value);
        if (value is null || value.Length < 4 || !int.TryParse(value.AsSpan(0, 4), out int year) ||
            year is < 1 or > 9999)
            return null;
        return year;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryNormalizePath(string path, out string normalized)
    {
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}
