using System.Buffers.Binary;

namespace iTunes.Binary;

/// <summary>
/// Metadata proven safe to mirror from a local media file into an existing iTunes track record.
/// Playback history, ratings, persistent IDs, store data, and unknown fields are intentionally
/// absent so refreshing a file cannot overwrite iTunes-owned state.
/// </summary>
public sealed record ItlLocalTrackMetadata
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Album { get; init; }
    public string? Genre { get; init; }
    public string? Composer { get; init; }
    public string? Grouping { get; init; }
    public string? Comment { get; init; }
    public string? SortTitle { get; init; }
    public string? SortArtist { get; init; }
    public string? SortAlbumArtist { get; init; }
    public string? SortAlbum { get; init; }
    public string? SortComposer { get; init; }
    public string? Kind { get; init; }
    public int? TrackNumber { get; init; }
    public int? TrackCount { get; init; }
    public int? DiscNumber { get; init; }
    public int? DiscCount { get; init; }
    public int? Year { get; init; }
    public int? Bpm { get; init; }
    public TimeSpan Duration { get; init; }
    public int BitRateKbps { get; init; }
    public int ArtworkCount { get; init; }
    public bool Compilation { get; init; }
    public bool Gapless { get; init; }
}

/// <summary>
/// Tag values retained by the library metadata cache. This intentionally excludes fields such as
/// artwork, sort values, comments, and playback state that the cache cannot reconstruct.
/// </summary>
public sealed record ItlCachedTrackMetadata
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? AlbumArtist { get; init; }
    /// <summary>
    /// True only when the cache observed a nonblank album-artist tag. When false, repair removes
    /// the ITL album-artist field and uses Artist only for the internal album/artist linkage.
    /// </summary>
    public bool HasExplicitAlbumArtist { get; init; }
    public string? Album { get; init; }
    public int? TrackNumber { get; init; }
    public int? TrackCount { get; init; }
    public int? DiscNumber { get; init; }
    public int? DiscCount { get; init; }
    public int? Year { get; init; }
    public bool Compilation { get; init; }
}

public sealed partial class ItlDocument
{
    private static readonly StringComparer LocalPathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>Returns every ITL record whose normalized local location names <paramref name="path"/>.</summary>
    public IReadOnlyList<ItlRecord> FindTracksByPath(string path)
    {
        string normalized = NormalizeLocalPath(path);
        return Tracks.Where(track =>
            ItlLocation.ToLocalPath(track.GetString(ItlDataType.Location)) is { } candidate &&
            LocalPathComparer.Equals(NormalizeLocalPath(candidate), normalized)).ToArray();
    }

    /// <summary>
    /// Updates the location of every track at <paramref name="oldPath"/> without replacing the
    /// record. Track IDs, persistent IDs, playback history, and playlist memberships are retained.
    /// </summary>
    public IReadOnlyList<ItlRecord> RelocateTracks(string oldPath, string newPath)
    {
        ItlRecord[] tracks = [.. FindTracksByPath(oldPath)];
        string normalized = NormalizeLocalPath(newPath);
        string fileUrl = ToFileUrl(normalized);
        foreach (ItlRecord track in tracks)
        {
            SetTrackString(track, ItlDataType.Location, normalized);
            SetTrackString(track, ItlDataType.FileUrl, fileUrl);
        }
        return tracks;
    }

    /// <summary>
    /// Refreshes file-derived fields while preserving all iTunes-owned identity and playback state.
    /// Album and artist foreign keys are redirected to matching records, creating records from the
    /// native templates already present in the library when necessary.
    /// </summary>
    public void RefreshLocalTrack(
        ItlRecord track,
        string path,
        ItlLocalTrackMetadata metadata,
        long fileLength,
        DateTime lastWriteTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(metadata);
        if (!Tracks.Contains(track))
            throw new ArgumentException("The record is not a track in this document.", nameof(track));

        string normalized = NormalizeLocalPath(path);
        SetTrackString(track, ItlDataType.Location, normalized);
        SetTrackString(track, ItlDataType.FileUrl, ToFileUrl(normalized));
        SetOptionalTrackString(track, ItlDataType.Title, metadata.Title);
        SetOptionalTrackString(track, ItlDataType.Artist, metadata.Artist);
        SetOptionalTrackString(track, ItlDataType.AlbumArtist, metadata.AlbumArtist);
        SetOptionalTrackString(track, ItlDataType.Album, metadata.Album);
        SetOptionalTrackString(track, ItlDataType.Genre, metadata.Genre);
        SetOptionalTrackString(track, ItlDataType.Composer, metadata.Composer);
        SetOptionalTrackString(track, ItlDataType.Grouping, metadata.Grouping);
        SetOptionalTrackString(track, ItlDataType.Comment, metadata.Comment);
        SetOptionalTrackString(track, ItlDataType.SortTitle, metadata.SortTitle);
        SetOptionalTrackString(track, ItlDataType.SortArtist, metadata.SortArtist);
        SetOptionalTrackString(track, ItlDataType.SortAlbumArtist, metadata.SortAlbumArtist);
        SetOptionalTrackString(track, ItlDataType.SortAlbum, metadata.SortAlbum);
        SetOptionalTrackString(track, ItlDataType.SortComposer, metadata.SortComposer);
        if (!string.IsNullOrWhiteSpace(metadata.Kind))
            SetTrackString(track, ItlDataType.Kind, metadata.Kind.Trim());

        track.SetTrackNumber(metadata.TrackNumber.GetValueOrDefault());
        track.SetTrackCount(metadata.TrackCount.GetValueOrDefault());
        track.SetDiscNumber(metadata.DiscNumber.GetValueOrDefault());
        track.SetDiscCount(metadata.DiscCount.GetValueOrDefault());
        track.SetYear(metadata.Year.GetValueOrDefault());
        track.SetBpm(metadata.Bpm.GetValueOrDefault());
        track.SetDuration(metadata.Duration);
        track.SetBitRate(Math.Max(0, metadata.BitRateKbps));
        track.SetArtworkCount(Math.Max(0, metadata.ArtworkCount));
        track.SetCompilation(metadata.Compilation);
        track.SetPartOfGaplessAlbum(metadata.Gapless);
        track.SetSize(checked((ulong)Math.Max(0, fileLength)));

        LinkAlbumAndArtist(track, metadata);

        // String setters stamp "now"; the native cache should instead describe the media file.
        track.SetDateModified(lastWriteTimeUtc);
    }

    /// <summary>
    /// Repairs only the tag fields represented by the library cache. Unlike
    /// <see cref="RefreshLocalTrack"/>, unrelated file-derived fields are preserved.
    /// </summary>
    public void RepairLocalTrackFromCache(
        ItlRecord track,
        ItlCachedTrackMetadata metadata,
        DateTime lastWriteTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(metadata);
        if (!Tracks.Contains(track))
            throw new ArgumentException("The record is not a track in this document.", nameof(track));

        SetOptionalTrackString(track, ItlDataType.Title, metadata.Title);
        SetOptionalTrackString(track, ItlDataType.Artist, metadata.Artist);
        SetOptionalTrackString(track, ItlDataType.AlbumArtist,
            metadata.HasExplicitAlbumArtist ? metadata.AlbumArtist : null);
        SetOptionalTrackString(track, ItlDataType.Album, metadata.Album);
        track.SetTrackNumber(metadata.TrackNumber.GetValueOrDefault());
        track.SetTrackCount(metadata.TrackCount.GetValueOrDefault());
        track.SetDiscNumber(metadata.DiscNumber.GetValueOrDefault());
        track.SetDiscCount(metadata.DiscCount.GetValueOrDefault());
        track.SetYear(metadata.Year.GetValueOrDefault());
        track.SetCompilation(metadata.Compilation);

        string? album = Clean(metadata.Album);
        string? albumArtist = metadata.HasExplicitAlbumArtist
            ? Clean(metadata.AlbumArtist)
            : null;
        string? artist = albumArtist ?? Clean(metadata.Artist);
        if (album is null || artist is null)
            track.SetAlbumId(0);
        if (artist is null)
            track.SetArtistId(0);
        LinkAlbumAndArtist(track, new ItlLocalTrackMetadata
        {
            Album = album,
            Artist = Clean(metadata.Artist),
            AlbumArtist = albumArtist,
        });

        // String setters stamp now; the cache records when the source metadata was read.
        track.SetDateModified(lastWriteTimeUtc);
    }

    /// <summary>
    /// Imports an ordinary local audio file by cloning a non-store, non-video track with the same
    /// extension. This preserves version-specific native header layout while replacing all
    /// file-derived metadata. Existing records at the same path are refreshed and returned.
    /// </summary>
    public ItlRecord ImportLocalTrack(
        string path,
        ItlLocalTrackMetadata metadata,
        long fileLength,
        DateTime lastWriteTimeUtc)
    {
        string normalized = NormalizeLocalPath(path);
        ItlRecord? existing = FindTracksByPath(normalized).FirstOrDefault();
        if (existing is not null)
        {
            RefreshLocalTrack(existing, normalized, metadata, fileLength, lastWriteTimeUtc);
            return existing;
        }

        string extension = Path.GetExtension(normalized);
        ItlRecord template = Tracks.FirstOrDefault(track =>
            !track.GetHasVideo() &&
            track.GetStoreItemId() == 0 &&
            track.GetStoreItemIdMirror() == 0 &&
            ItlLocation.ToLocalPath(track.GetString(ItlDataType.Location)) is { } templatePath &&
            Path.GetExtension(templatePath).Equals(extension, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"The library has no ordinary local '{extension}' track that can safely serve as an import template.");

        ItlRecord imported = AddTrack(template);
        int[] structuralTypes =
        [
            (int)ItlDataType.Kind,
            (int)ItlDataType.Location,
            (int)ItlDataType.FileUrl,
        ];
        imported.Children.RemoveAll(child =>
            child is ItlField field && !structuralTypes.Contains(field.Type));
        RefreshLocalTrack(imported, normalized, metadata, fileLength, lastWriteTimeUtc);
        imported.SetPlayCount(0);
        imported.SetSkipCount(0);
        imported.SetLoved(false);
        return imported;
    }

    private void LinkAlbumAndArtist(ItlRecord track, ItlLocalTrackMetadata metadata)
    {
        string? album = Clean(metadata.Album);
        string? artist = Clean(metadata.AlbumArtist) ?? Clean(metadata.Artist);
        ItlRecord? albumRecord = null;
        if (album is not null && artist is not null)
        {
            albumRecord = Albums.FirstOrDefault(candidate =>
                string.Equals(candidate.Field((int)ItlDataType.AlbumRecordName)?.Text, album,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Field((int)ItlDataType.AlbumRecordArtist)?.Text, artist,
                    StringComparison.OrdinalIgnoreCase))
                ?? AddAlbum(album, artist, Albums.FirstOrDefault()
                    ?? throw new InvalidOperationException(
                        "The library has no album record template."));
            track.SetAlbumId(RecordIdOf(albumRecord));
        }

        if (artist is not null)
        {
            ItlRecord artistRecord = Artists.FirstOrDefault(candidate =>
                string.Equals(candidate.Field((int)ItlDataType.ArtistRecordName)?.Text, artist,
                    StringComparison.OrdinalIgnoreCase))
                ?? AddArtist(artist, Artists.FirstOrDefault()
                    ?? throw new InvalidOperationException(
                        "The library has no artist record template."));
            track.SetArtistId(RecordIdOf(artistRecord));

            // These fields share one native string-key domain. Keeping their payload text equal is
            // insufficient: a reused key causes iTunes to substitute that key's unrelated value
            // across every track when it next rewrites the library.
            ItlField artistName = artistRecord.Field((int)ItlDataType.ArtistRecordName)!;
            SynchronizeKey(artistName, track.Field((int)ItlDataType.AlbumArtist));
            SynchronizeKey(artistName, track.Field((int)ItlDataType.Artist));
            SynchronizeKey(artistName, albumRecord?.Field((int)ItlDataType.AlbumRecordArtist));
            SynchronizeKey(artistName, albumRecord?.Field((int)ItlDataType.AlbumRecordSortArtist));
        }

        if (albumRecord is not null)
            SynchronizeKey(albumRecord.Field((int)ItlDataType.AlbumRecordName),
                track.Field((int)ItlDataType.Album));

        static void SynchronizeKey(ItlField? source, ItlField? target)
        {
            if (source is null || target is null || source.Text != target.Text)
                return;
            BinaryPrimitives.WriteUInt32LittleEndian(target.Header.AsSpan(16),
                BinaryPrimitives.ReadUInt32LittleEndian(source.Header.AsSpan(16)));
        }
    }

    private void SetOptionalTrackString(ItlRecord track, ItlDataType type, string? value)
    {
        value = Clean(value);
        if (value is null)
        {
            track.RemoveField((int)type);
            return;
        }
        SetTrackString(track, type, value);
    }

    private static string NormalizeLocalPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string ToFileUrl(string path)
    {
        string value = new Uri(path).AbsoluteUri;
        return value.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)
            ? "file://localhost/" + value[8..]
            : value;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
