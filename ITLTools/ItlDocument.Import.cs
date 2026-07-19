namespace iTunes.Binary;

public sealed partial class ItlDocument
{
    /// <summary>
    /// Adds a locally generated AAC file by cloning a non-store AAC track layout, replacing all
    /// modeled metadata, linking album/artist entities, and adding the native built-in playlist
    /// memberships inherited from that template. An existing record at the same path is returned.
    /// </summary>
    public ItlRecord ImportAacTrack(ItlAacTrackImport import)
    {
        ArgumentNullException.ThrowIfNull(import);
        string path = Path.GetFullPath(import.Path);
        if (!File.Exists(path))
            throw new FileNotFoundException("The AAC file to import does not exist.", path);
        if (!Path.GetExtension(path).Equals(".m4a", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only .m4a AAC imports are supported.", nameof(import));

        ItlRecord? existing = Tracks.FirstOrDefault(track => string.Equals(
            ItlLocation.ToLocalPath(track.GetString(ItlDataType.Location)), path,
            StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;

        ItlRecord template = Tracks.FirstOrDefault(track =>
            !track.GetHasVideo() &&
            track.GetStoreItemId() == 0 && track.GetStoreItemIdMirror() == 0 &&
            Path.GetExtension(ItlLocation.ToLocalPath(track.GetString(ItlDataType.Location)) ?? string.Empty)
                .Equals(".m4a", StringComparison.OrdinalIgnoreCase) &&
            (track.GetString(ItlDataType.Kind) ?? string.Empty)
                .Contains("AAC audio file", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "The library has no ordinary non-store AAC track that can safely serve as an import template.");

        ItlRecord track = AddTrack(template);

        // Keep the three fields whose native structural headers must be cloned. Every other mhoh
        // belongs to the template's media item and must not leak into the imported track.
        int[] structuralTypes = [(int)ItlDataType.Kind, (int)ItlDataType.Location, (int)ItlDataType.FileUrl];
        track.Children.RemoveAll(child => child is ItlField field && !structuralTypes.Contains(field.Type));

        SetTrackString(track, ItlDataType.Title, import.Title);
        SetTrackString(track, ItlDataType.Artist, import.Artist);
        SetTrackString(track, ItlDataType.AlbumArtist, import.AlbumArtist);
        SetTrackString(track, ItlDataType.Album, import.Album);
        if (!string.IsNullOrWhiteSpace(import.Genre))
            SetTrackString(track, ItlDataType.Genre, import.Genre);
        SetTrackString(track, ItlDataType.Location, path);
        string fileUrl = new Uri(path).AbsoluteUri;
        if (fileUrl.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
            fileUrl = "file://localhost/" + fileUrl[8..];
        SetTrackString(track, ItlDataType.FileUrl, fileUrl);

        var file = new FileInfo(path);
        LinkAlbumAndArtist(track, new ItlLocalTrackMetadata
        {
            Artist = import.Artist,
            AlbumArtist = import.AlbumArtist,
            Album = import.Album,
        });
        track.SetTrackNumber(import.TrackNumber);
        track.SetTrackCount(import.TrackCount);
        track.SetDiscNumber(0);
        track.SetDiscCount(0);
        track.SetDuration(import.Duration);
        track.SetBitRate(import.BitRate);
        track.SetArtworkCount(import.ArtworkCount);
        track.SetSize((ulong)file.Length);
        track.SetDateModified(file.LastWriteTimeUtc);
        track.SetYear(0);
        track.SetCompilation(import.Compilation);
        track.SetHasVideo(false);
        track.SetLoved(false);
        track.SetAdvisory(0);
        return track;
    }
}
