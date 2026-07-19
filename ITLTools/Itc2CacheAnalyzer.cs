using System.Buffers.Binary;

namespace iTunes.Binary;

public sealed record Itc2CacheExample(
    string RelativePath,
    string Category,
    ulong ArtworkPersistentId,
    ulong? FileReferencePersistentId,
    string? TrackTitle,
    string Shape);

public sealed record Itc2TrackHeaderLinkExample(
    int TrackId,
    ulong TrackPersistentId,
    string? TrackTitle,
    string? TrackAlbum,
    int HeaderOffset,
    ulong ArtworkPersistentId,
    string? ArtworkTrackTitle,
    string? ArtworkTrackAlbum);

/// <summary>A read-only correlation report between an ITL library and its Album Artwork tree.</summary>
public sealed class Itc2CacheReport
{
    internal Itc2CacheReport(string rootPath, ulong libraryPersistentId) =>
        (RootPath, LibraryPersistentId) = (rootPath, libraryPersistentId);

    public string RootPath { get; }
    public ulong LibraryPersistentId { get; }
    public int FilesScanned { get; internal set; }
    public long FileBytes { get; internal set; }
    public int Items { get; internal set; }
    public int ParseFailures { get; internal set; }
    public int LibraryIdMatches { get; internal set; }
    public int LibraryIdMismatches { get; internal set; }
    public int ShardMatches { get; internal set; }
    public int ShardMismatches { get; internal set; }
    public int LocalFileNameMatches { get; internal set; }
    public int CloudFileNameMatches { get; internal set; }
    public int FileNameMismatches { get; internal set; }
    public int ArtworkTrackMatches { get; internal set; }
    public int ArtworkAlbumMatches { get; internal set; }
    public int ArtworkArtistMatches { get; internal set; }
    public int ReferencedTrackFiles { get; internal set; }
    public int MissingTrackFiles { get; internal set; }
    public int DistinctArtworkIds { get; internal set; }
    public int DistinctReferencedTracks { get; internal set; }
    public int LibraryTracks { get; internal set; }
    public int LibraryAlbums { get; internal set; }
    public int TracksClaimingArtwork { get; internal set; }
    public int TracksWithCloudArtworkMetadata { get; internal set; }
    public int LocalCacheTracksClaimingArtwork { get; internal set; }
    public int DistinctLocalCacheAlbums { get; internal set; }
    public int TrackHeader316Nonzero { get; internal set; }
    public int TrackHeader316SelfMatches { get; internal set; }
    public int TrackHeader316TrackMatches { get; internal set; }
    public bool WasLimited { get; internal set; }
    public Dictionary<string, int> Categories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Shapes { get; } = new(StringComparer.Ordinal);
    public Dictionary<int, int> TrackHeaderArtworkIdOffsets { get; } = [];
    public List<string> Failures { get; } = [];
    public List<Itc2CacheExample> Examples { get; } = [];
    public List<Itc2TrackHeaderLinkExample> TrackHeaderLinkExamples { get; } = [];
}

/// <summary>Inventory and identity checks for the sharded Windows iTunes Album Artwork cache.</summary>
public static class Itc2CacheAnalyzer
{
    private static readonly HashSet<string> KnownCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "Cloud Purchases", "Custom", "Download", "Generated", "Store",
    };

    public static Itc2CacheReport Analyze(ItlDocument library, string artworkRoot, int maxFiles = 0)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentException.ThrowIfNullOrWhiteSpace(artworkRoot);
        if (maxFiles < 0)
            throw new ArgumentOutOfRangeException(nameof(maxFiles));

        string root = Path.GetFullPath(artworkRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Artwork cache directory not found: {root}");

        Dictionary<ulong, ItlRecord> tracks = library.Tracks
            .GroupBy(ItlDocument.TrackPersistentIdOf)
            .ToDictionary(group => group.Key, group => group.First());
        HashSet<ulong> albumIds = [.. library.Albums.Select(EntityPersistentId)];
        HashSet<ulong> artistIds = [.. library.Artists.Select(EntityPersistentId)];
        var distinctArtwork = new HashSet<ulong>();
        var localArtwork = new HashSet<ulong>();
        var localAlbums = new HashSet<uint>();
        var distinctTracks = new HashSet<ulong>();
        var report = new Itc2CacheReport(root, library.Envelope.LibraryPersistentId);

        foreach (string path in Directory.EnumerateFiles(root, "*.itc2", SearchOption.AllDirectories))
        {
            if (maxFiles != 0 && report.FilesScanned >= maxFiles)
            {
                report.WasLimited = true;
                break;
            }

            report.FilesScanned++;
            try
            {
                var fileInfo = new FileInfo(path);
                report.FileBytes += fileInfo.Length;
                Itc2File file = Itc2File.Load(path);
                string relative = Path.GetRelativePath(root, path);
                string category = CategoryOf(root, relative);
                Increment(report.Categories, category);

                Itc2Item identity = file.Items[0];
                ulong? fileReference = FileReferencePersistentId(path, identity, library.Envelope.LibraryPersistentId,
                    out bool localNameMatch, out bool cloudNameMatch);
                if (localNameMatch) report.LocalFileNameMatches++;
                else if (cloudNameMatch) report.CloudFileNameMatches++;
                else report.FileNameMismatches++;

                if (fileReference is { } referenceId)
                {
                    distinctTracks.Add(referenceId);
                    if (tracks.TryGetValue(referenceId, out ItlRecord? referenced))
                    {
                        report.ReferencedTrackFiles++;
                        if (localNameMatch)
                        {
                            localArtwork.Add(identity.ArtworkPersistentId);
                            localAlbums.Add(referenced.GetAlbumId());
                            if (referenced.GetArtworkCount() > 0)
                                report.LocalCacheTracksClaimingArtwork++;
                        }
                    }
                    else report.MissingTrackFiles++;
                }

                if (ShardMatches(path, identity.ArtworkPersistentId)) report.ShardMatches++;
                else report.ShardMismatches++;

                foreach (Itc2Item item in file.Items)
                {
                    report.Items++;
                    distinctArtwork.Add(item.ArtworkPersistentId);
                    if (item.LibraryPersistentId == library.Envelope.LibraryPersistentId) report.LibraryIdMatches++;
                    else report.LibraryIdMismatches++;
                    if (tracks.ContainsKey(item.ArtworkPersistentId)) report.ArtworkTrackMatches++;
                    if (albumIds.Contains(item.ArtworkPersistentId)) report.ArtworkAlbumMatches++;
                    if (artistIds.Contains(item.ArtworkPersistentId)) report.ArtworkArtistMatches++;
                    Increment(report.Shapes, Shape(item));
                }

                if (report.Examples.Count < 16)
                {
                    ItlRecord? track = fileReference is { } id && tracks.TryGetValue(id, out ItlRecord? found)
                        ? found
                        : null;
                    report.Examples.Add(new Itc2CacheExample(
                        relative,
                        category,
                        identity.ArtworkPersistentId,
                        fileReference,
                        track?.GetString(ItlDataType.Title),
                        string.Join(", ", file.Items.Select(Shape))));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                report.ParseFailures++;
                if (report.Failures.Count < 20)
                    report.Failures.Add($"{Path.GetRelativePath(root, path)}: {exception.Message}");
            }
        }

        report.DistinctArtworkIds = distinctArtwork.Count;
        report.DistinctReferencedTracks = distinctTracks.Count;
        report.LibraryTracks = library.Tracks.Count;
        report.LibraryAlbums = library.Albums.Count;
        report.TracksClaimingArtwork = library.Tracks.Count(track => track.GetArtworkCount() > 0);
        report.TracksWithCloudArtworkMetadata = library.Tracks.Count(track =>
            track.Field((int)ItlDataType.CloudArtworkPlist) is not null);
        report.DistinctLocalCacheAlbums = localAlbums.Count;
        foreach (ItlRecord track in library.Tracks.Where(track => track.Header.Length >= 324))
        {
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(track.Header.AsSpan(316));
            if (value == 0)
                continue;
            report.TrackHeader316Nonzero++;
            if (value == track.GetPersistentId())
                report.TrackHeader316SelfMatches++;
            if (tracks.ContainsKey(value))
                report.TrackHeader316TrackMatches++;
        }
        FindTrackHeaderLinks(library.Tracks, localArtwork, report.TrackHeaderArtworkIdOffsets);
        CaptureTrackHeaderLinkExamples(library.Tracks, tracks, localArtwork, report.TrackHeaderArtworkIdOffsets.Keys,
            report.TrackHeaderLinkExamples);
        return report;
    }

    /// <summary>
    /// iTunes shards on the low twelve bits of the artwork ID. Each nibble is rendered as a
    /// two-digit decimal directory (00 through 15), least-significant nibble first.
    /// </summary>
    public static string[] ShardDirectories(ulong artworkPersistentId) =>
    [
        $"{artworkPersistentId & 0xF:D2}",
        $"{(artworkPersistentId >> 4) & 0xF:D2}",
        $"{(artworkPersistentId >> 8) & 0xF:D2}",
    ];

    public static bool ShardMatches(string filePath, ulong artworkPersistentId)
    {
        string[] expected = ShardDirectories(artworkPersistentId);
        DirectoryInfo? directory = new FileInfo(filePath).Directory;
        for (int index = expected.Length - 1; index >= 0; index--)
        {
            if (directory is null || !string.Equals(directory.Name, expected[index], StringComparison.OrdinalIgnoreCase))
                return false;
            directory = directory.Parent;
        }
        return true;
    }

    private static ulong EntityPersistentId(ItlRecord record) =>
        BinaryPrimitives.ReadUInt64LittleEndian(record.Header.AsSpan(20));

    private static ulong? FileReferencePersistentId(
        string path,
        Itc2Item item,
        ulong libraryPersistentId,
        out bool localMatch,
        out bool cloudMatch)
    {
        localMatch = false;
        cloudMatch = false;
        string[] parts = Path.GetFileNameWithoutExtension(path).Split('-');
        if (parts.Length != 2)
            return null;

        if (TryHex(parts[0], out ulong first) && TryHex(parts[1], out ulong second) &&
            first == libraryPersistentId && second == item.ArtworkPersistentId)
        {
            localMatch = true;
            return second;
        }

        // Cloud Purchases prefixes its 16-hex artwork ID with the high shard nibble. The semantic
        // identity of the 16-hex filename suffix remains unresolved; it does not resolve to a
        // current track in the reference corpus sampled during format research.
        if (parts[0].Length == 18 &&
            TryHex(parts[0][2..], out ulong cloudArtwork) && cloudArtwork == item.ArtworkPersistentId &&
            TryHex(parts[1], out ulong trackPersistentId) &&
            string.Equals(parts[0][..2], ShardDirectories(item.ArtworkPersistentId)[2],
                StringComparison.OrdinalIgnoreCase))
        {
            cloudMatch = true;
            return trackPersistentId;
        }

        return null;
    }

    private static bool TryHex(string value, out ulong result)
    {
        result = 0;
        return value.Length == 16 && ulong.TryParse(value, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out result);
    }

    private static string CategoryOf(string root, string relative)
    {
        if (KnownCategories.Contains(new DirectoryInfo(root).Name))
            return new DirectoryInfo(root).Name;
        string first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return KnownCategories.Contains(first) ? first : "(unknown)";
    }

    private static string Shape(Itc2Item item)
    {
        string format = item.Encoding == Itc2ImageEncoding.Unknown
            ? $"0x{item.PixelFormatCode:X8}"
            : item.Encoding.ToString();
        return $"{item.OriginTag}/{format} {item.Width}x{item.Height}";
    }

    private static void Increment(Dictionary<string, int> values, string key) =>
        values[key] = values.TryGetValue(key, out int count) ? count + 1 : 1;

    private static void FindTrackHeaderLinks(
        IEnumerable<ItlRecord> tracks,
        HashSet<ulong> artworkIds,
        Dictionary<int, int> result)
    {
        ItlRecord[] records = [.. tracks];
        if (records.Length == 0 || artworkIds.Count == 0)
            return;
        int length = records.Min(record => record.Header.Length);
        for (int offset = 8; offset + sizeof(ulong) <= length; offset += sizeof(uint))
        {
            int matches = records.Count(record => artworkIds.Contains(
                BinaryPrimitives.ReadUInt64LittleEndian(record.Header.AsSpan(offset))));
            if (matches > 0)
                result[offset] = matches;
        }
    }

    private static void CaptureTrackHeaderLinkExamples(
        IEnumerable<ItlRecord> tracks,
        Dictionary<ulong, ItlRecord> tracksByPersistentId,
        HashSet<ulong> artworkIds,
        IEnumerable<int> offsets,
        List<Itc2TrackHeaderLinkExample> result)
    {
        foreach (ItlRecord track in tracks)
        {
            foreach (int offset in offsets.Where(candidate => candidate != 128))
            {
                ulong targetId = BinaryPrimitives.ReadUInt64LittleEndian(track.Header.AsSpan(offset));
                if (!artworkIds.Contains(targetId) || !tracksByPersistentId.TryGetValue(targetId, out ItlRecord? target))
                    continue;
                result.Add(new Itc2TrackHeaderLinkExample(
                    track.GetTrackId(),
                    track.GetPersistentId(),
                    track.GetString(ItlDataType.Title),
                    track.GetString(ItlDataType.Album),
                    offset,
                    targetId,
                    target.GetString(ItlDataType.Title),
                    target.GetString(ItlDataType.Album)));
                if (result.Count >= 20)
                    return;
            }
        }
    }
}
