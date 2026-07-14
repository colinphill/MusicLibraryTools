using System.Buffers.Binary;
using System.Text;

namespace iTunes.Binary;

/// <summary>
/// Known "mhoh" data object types. The number space is partitioned by owning record. Names were
/// established by matching each type's text against iTunes' own XML export, except where noted.
/// </summary>
public enum ItlDataType
{
    // Track ("mith")
    Title = 2,
    Album = 3,
    Artist = 4,
    Genre = 5,
    Kind = 6,
    Comment = 8,
    FileUrl = 11,
    Composer = 12,
    Location = 13,
    Grouping = 14,

    /// <summary>Episode description. 18 and 22 both hold it; which is the "long" one is unproven.</summary>
    Description = 18,
    DescriptionLong = 22,

    Series = 24,
    Episode = 25,
    ContentRating = 28,

    /// <summary>A plist of store asset info: file-size, flavor, download parameters.</summary>
    AssetInfoPlist = 29,

    AlbumArtist = 27,
    SortTitle = 30,
    SortAlbum = 31,
    SortArtist = 32,
    SortAlbumArtist = 33,
    SortComposer = 34,
    SortSeries = 35,
    StoreIdentifier = 43,

    /// <summary>Version string of an iTunes LP package.</summary>
    ItunesLpVersion = 45,

    Copyright = 46,

    /// <summary>Description of the whole series, shared by every episode.</summary>
    SeriesDescription = 51,

    /// <summary>Store quality flavor, e.g. "2:256" (stereo 256 kbps) or "6:640x480LC-256".</summary>
    StoreFlavor = 52,

    /// <summary>A plist holding cloud-artwork-token and cloud-artwork-url.</summary>
    CloudArtworkPlist = 54,

    /// <summary>A plist holding the store's redownload-params.</summary>
    RedownloadParamsPlist = 56,

    PurchaserEmail = 59,
    PurchaserName = 60,

    // Playlist ("miph")
    PlaylistName = 100,

    /// <summary>Smart playlist rules. Byte-identical to the XML's base64 "Smart Criteria".</summary>
    SmartCriteria = 101,

    /// <summary>Byte-identical to the XML's base64 "Smart Info".</summary>
    SmartInfo = 102,

    /// <summary>Column layout and view settings.</summary>
    PlaylistViewSettings = 105,

    /// <summary>A plist of view state: lastViewedPlaylist, tabViewMode.</summary>
    PlaylistViewStatePlist = 109,

    // Album ("miah")
    AlbumRecordName = 300,
    AlbumRecordArtist = 301,
    AlbumRecordSortArtist = 302,

    // Artist ("miih")
    ArtistRecordName = 400,

    /// <summary>A plist holding the artist's store artwork URL.</summary>
    ArtistArtworkPlist = 402,

    // Library info ("mhgh")
    LibraryName = 508,

    /// <summary>The library folder, stored as raw UTF-16 with no string preamble.</summary>
    LibraryFolderPath = 511,

    /// <summary>A plist of per-track playback state keyed by a 128-bit id: bktm, hbpl, plct.</summary>
    PlaybackStatePlist = 514,

    // Podcast settings ("msph")
    PodcastSettingsPlist = 800,
}

public sealed class ItlTrack
{
    /// <summary>iTunes writes timestamps as seconds since this epoch, in the machine's local time.</summary>
    internal static readonly DateTime MacEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public required int Id { get; init; }
    public required IReadOnlyList<ItlDataObject> DataObjects { get; init; }

    /// <summary>The undecoded "mith" fixed header. Numeric fields are read from here.</summary>
    public required byte[] Header { get; init; }

    public string? this[ItlDataType type] =>
        DataObjects.FirstOrDefault(o => o.Type == (int)type && o.IsString)?.Text;

    public string? Title => this[ItlDataType.Title];
    public string? Artist => this[ItlDataType.Artist];
    public string? Album => this[ItlDataType.Album];
    public string? AlbumArtist => this[ItlDataType.AlbumArtist];
    public string? Genre => this[ItlDataType.Genre];
    public string? Kind => this[ItlDataType.Kind];
    public string? Composer => this[ItlDataType.Composer];
    public string? Location => this[ItlDataType.Location];

    private ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Header.AsSpan(offset));
    private uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Header.AsSpan(offset));

    public ulong PersistentId => BinaryPrimitives.ReadUInt64LittleEndian(Header.AsSpan(128));

    /// <summary>
    /// Apple Store/catalog item ID. Type-514 playback-state entries use its decimal representation
    /// directly for one key branch. iTunes keeps an identical mirror at +428.
    /// </summary>
    public uint StoreItemId => U32(168);
    public uint StoreItemIdMirror => U32(428);

    /// <summary>File size. A 32-bit copy lives at +36 but truncates above 4 GiB, so this is the truth.</summary>
    public ulong Size => BinaryPrimitives.ReadUInt64LittleEndian(Header.AsSpan(324));

    public TimeSpan Duration => TimeSpan.FromMilliseconds(U32(40));
    public int TrackNumber => U16(44);
    public int TrackCount => U16(48);
    public int Year => U16(52);
    public int BitRate => U16(56);
    public int PlayCount => (int)U32(76);
    public int Bpm => U16(164);
    public int SkipCount => (int)U32(216);

    public int DiscNumber => U16(104);
    public int DiscCount => U16(106);
    public int ArtworkCount => U16(144);
    /// <summary>Signed: iTunes writes -1 for a file it cannot locate under the library folder.</summary>
    public int FileFolderCount => BinaryPrimitives.ReadInt16LittleEndian(Header.AsSpan(92));

    public int LibraryFolderCount => BinaryPrimitives.ReadInt16LittleEndian(Header.AsSpan(94));
    public int EpisodeOrder => (int)U32(268);
    public int Season => (int)U32(272);

    public uint AlbumId => U32(220);
    public uint ArtistId => U32(480);

    public bool Compilation => (Header[83] & 1) != 0;
    public bool HasVideo => (Header[233] & 1) != 0;
    public bool PartOfGaplessAlbum => (Header[278] & 1) != 0;

    /// <summary>0 = none, 1 = explicit, 2 = clean.</summary>
    public int Advisory => Header[166];

    public DateTime? DateModified => ToUtc(U32(32));
    public DateTime? PlayDate => ToUtc(U32(100));
    public DateTime? DateAdded => ToUtc(U32(120));
    public DateTime? SkipDate => ToUtc(U32(284));

    /// <summary>Unlike the other timestamps, iTunes stores the release date in UTC.</summary>
    public DateTime? ReleaseDate => U32(160) == 0 ? null : MacEpoch.AddSeconds(U32(160));

    /// <summary>
    /// Timestamps are local-time seconds since 1904, so converting back needs this machine's
    /// timezone rules. Values written on a machine in another zone will be off by the difference.
    /// </summary>
    private static DateTime? ToUtc(uint seconds)
    {
        if (seconds == 0)
            return null;

        DateTime local = DateTime.SpecifyKind(MacEpoch.AddSeconds(seconds), DateTimeKind.Unspecified);
        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.Local);
        }
        catch (ArgumentException)
        {
            // Falls inside a daylight-saving gap; no real instant corresponds to it.
            return null;
        }
    }
}

public sealed class ItlAlbum
{
    public required IReadOnlyList<ItlDataObject> DataObjects { get; init; }

    public string? this[ItlDataType type] =>
        DataObjects.FirstOrDefault(o => o.Type == (int)type && o.IsString)?.Text;

    public string? Name => this[ItlDataType.AlbumRecordName];
    public string? Artist => this[ItlDataType.AlbumRecordArtist];
}

public sealed class ItlArtist
{
    public required IReadOnlyList<ItlDataObject> DataObjects { get; init; }

    public string? Name => DataObjects
        .FirstOrDefault(o => o.Type == (int)ItlDataType.ArtistRecordName && o.IsString)?.Text;
}

public sealed class ItlPlaylist
{
    public required string? Name { get; init; }
    public required IReadOnlyList<int> TrackIds { get; init; }

    /// <summary>iTunes names the master library playlist with this sentinel.</summary>
    public bool IsMaster => Name == "####!####";
}

public sealed class ItlSection
{
    public required ItlChunk Chunk { get; init; }
    public required string InnerSignature { get; init; }
}

public sealed class ItlLibrary
{
    public required ItlEnvelope Envelope { get; init; }
    public required IReadOnlyList<ItlSection> Sections { get; init; }
    public required IReadOnlyList<ItlTrack> Tracks { get; init; }
    public required IReadOnlyList<ItlAlbum> Albums { get; init; }
    public required IReadOnlyList<ItlArtist> Artists { get; init; }
    public required IReadOnlyList<ItlPlaylist> Playlists { get; init; }

    // Section types, keyed by the word at msdh+12.
    private const int TrackSection = 1;
    private const int PlaylistSection = 2;
    private const int AlbumSection = 9;
    private const int ArtistSection = 11;

    public static ItlLibrary Load(string path) => Parse(ItlEnvelope.Load(path));

    public static ItlLibrary Parse(ItlEnvelope envelope)
    {
        byte[] body = envelope.Body;

        var sections = new List<ItlSection>();
        var tracks = new List<ItlTrack>();
        var albums = new List<ItlAlbum>();
        var artists = new List<ItlArtist>();
        var playlists = new List<ItlPlaylist>();

        foreach (ItlChunk section in ItlChunk.Walk(body, 0, body.Length))
        {
            if (section.Signature != "msdh")
                throw new InvalidDataException($"Expected 'msdh' at {section.Offset}, found '{section.Signature}'.");

            // Not every section holds a chunk: type 19 is an XML plist and type 4 a "file://" URL.
            string innerSignature = Encoding.ASCII.GetString(body, section.BodyOffset, 4);
            sections.Add(new ItlSection { Chunk = section, InnerSignature = innerSignature });

            switch (section.Type)
            {
                // Type 13 is a second "mlth" of cloud/other entries; only type 1 is the real library.
                case TrackSection when innerSignature == "mlth":
                    ForEachItem(body, section, "mith", item => tracks.Add(ReadTrack(body, item)));
                    break;

                case AlbumSection when innerSignature == "mlah":
                    ForEachItem(body, section, "miah", item =>
                        albums.Add(new ItlAlbum { DataObjects = ReadDataObjects(body, item) }));
                    break;

                case ArtistSection when innerSignature == "mlih":
                    ForEachItem(body, section, "miih", item =>
                        artists.Add(new ItlArtist { DataObjects = ReadDataObjects(body, item) }));
                    break;

                case PlaylistSection when innerSignature == "mlph":
                    ForEachItem(body, section, "miph", item => playlists.Add(ReadPlaylist(body, item)));
                    break;
            }
        }

        return new ItlLibrary
        {
            Envelope = envelope,
            Sections = sections,
            Tracks = tracks,
            Albums = albums,
            Artists = artists,
            Playlists = playlists,
        };
    }

    /// <summary>
    /// Walks the items of a section's list header. The count the list header declares is honoured
    /// where it is non-zero: some lists ("mlqh") declare zero yet still carry items.
    /// </summary>
    private static void ForEachItem(byte[] body, ItlChunk section, string itemSignature, Action<ItlChunk> onItem)
    {
        ItlChunk list = ItlChunk.Read(body, section.BodyOffset);
        int seen = 0;

        foreach (ItlChunk item in ItlChunk.Walk(body, list.HeaderEnd, section.EndOffset))
        {
            if (item.Signature != itemSignature)
                break;
            onItem(item);
            seen++;
        }

        if (list.ItemCount != 0 && seen != list.ItemCount)
            throw new InvalidDataException($"'{list.Signature}' declares {list.ItemCount} items but {seen} '{itemSignature}' were read.");
    }

    private static List<ItlDataObject> ReadDataObjects(byte[] body, ItlChunk record)
    {
        var objects = new List<ItlDataObject>();
        foreach (ItlChunk child in ItlChunk.Walk(body, record.BodyOffset, record.EndOffset))
        {
            if (child.Signature != "mhoh")
                break;
            objects.Add(ItlDataObject.Parse(body, child));
        }
        return objects;
    }

    private static ItlTrack ReadTrack(byte[] body, ItlChunk mith) => new()
    {
        Id = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(mith.Offset + 16)),
        DataObjects = ReadDataObjects(body, mith),
        Header = body.AsSpan(mith.Offset, mith.HeaderLength).ToArray(),
    };

    private static ItlPlaylist ReadPlaylist(byte[] body, ItlChunk miph)
    {
        string? name = null;
        var trackIds = new List<int>();

        // A playlist's children are its "mhoh" attributes followed by one "mtph" per member track.
        foreach (ItlChunk child in ItlChunk.Walk(body, miph.BodyOffset, miph.EndOffset))
        {
            switch (child.Signature)
            {
                case "mhoh":
                    ItlDataObject o = ItlDataObject.Parse(body, child);
                    if (o.Type == (int)ItlDataType.PlaylistName && o.IsString)
                        name = o.Text;
                    break;

                case "mtph":
                    trackIds.Add(BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(child.Offset + 24)));
                    break;
            }
        }

        return new ItlPlaylist { Name = name, TrackIds = trackIds };
    }
}
