using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace iTunes.Binary;

/// <summary>
/// A fully editable library. Sections we understand are parsed into records; the rest are kept
/// byte for byte, so serializing an unmodified document reproduces the original body exactly.
/// </summary>
public sealed partial class ItlDocument
{
    // Section types, keyed by the word at msdh+12.
    private const int TrackSectionType = 1;
    private const int PlaylistSectionType = 2;
    private const int AlbumSectionType = 9;
    private const int ArtistSectionType = 11;

    /// <summary>A second "mlth" of cloud entries. It reuses the main track ids, so deletes must reach it.</summary>
    private const int CloudTrackSectionType = 13;

    private const int EnvelopeCopySectionType = 16;

    private static readonly int[] StructuredSectionTypes =
        [TrackSectionType, PlaylistSectionType, AlbumSectionType, ArtistSectionType, CloudTrackSectionType];

    private uint _nextId;
    private readonly AggregateCounts _originalCounts;

    private readonly record struct AggregateCounts(int Tracks, int Playlists, int Albums, int Artists);

    private ItlDocument(ItlEnvelope envelope, List<ItlSectionNode> sections)
    {
        Envelope = envelope;
        Sections = sections;

        // Every object in the library draws its id from one counter: track ids, album ids, artist
        // ids and playlist entry ids all overlap in range and none collides. Allocate above all.
        uint highestTrack = Tracks.Select(t => (uint)TrackIdOf(t)).DefaultIfEmpty(0u).Max();
        uint highestTrackSecondary = Tracks.Select(TrackSecondaryIdOf).DefaultIfEmpty(0u).Max();
        uint highestCloud = CloudTracks.Select(t => (uint)TrackIdOf(t)).DefaultIfEmpty(0u).Max();
        uint highestAlbum = Albums.Select(RecordIdOf).DefaultIfEmpty(0u).Max();
        uint highestArtist = Artists.Select(RecordIdOf).DefaultIfEmpty(0u).Max();
        uint highestPlaylist = Playlists.Select(PlaylistRecordIdOf).DefaultIfEmpty(0u).Max();
        uint highestEntry = Playlists.SelectMany(p => p.Entries).Select(e => e.EntryId).DefaultIfEmpty(0u).Max();

        _nextId = new[] { highestTrack, highestTrackSecondary, highestCloud, highestAlbum, highestArtist, highestPlaylist, highestEntry }.Max() + 1;
        _originalCounts = CurrentCounts;
    }

    /// <summary>The record id every "mith", "miah" and "miih" carries at +16.</summary>
    public static uint RecordIdOf(ItlRecord record) =>
        BinaryPrimitives.ReadUInt32LittleEndian(record.Header.AsSpan(16));

    /// <summary>Album and artist entity records carry their unique 64-bit persistent ID at +20.</summary>
    private static void AssignNewIdentity(ItlRecord record, uint id)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(record.Header.AsSpan(16), id);
        BinaryPrimitives.WriteUInt64LittleEndian(record.Header.AsSpan(20), NewPersistentId());
    }

    /// <summary>Mints an identifier that collides with no existing track or playlist entry.</summary>
    private uint NextId() => _nextId++;

    public ItlEnvelope Envelope { get; }
    public List<ItlSectionNode> Sections { get; }

    public List<ItlRecord> Tracks => RecordsOf(TrackSectionType);

    /// <summary>The parallel cloud track list. Its records share track ids with <see cref="Tracks"/>.</summary>
    public List<ItlRecord> CloudTracks => RecordsOf(CloudTrackSectionType);
    public List<ItlRecord> Albums => RecordsOf(AlbumSectionType);
    public List<ItlRecord> Artists => RecordsOf(ArtistSectionType);
    public List<ItlRecord> Playlists => RecordsOf(PlaylistSectionType);

    /// <summary>The full mhgh +124 DSID associated with the optional type-514 playback-state plist.</summary>
    public ulong PlaybackStateDsid => ReadMhghUInt64(124);

    /// <summary>
    /// The account DSID cached at mhgh +212. Native iTunes initializes this from its active
    /// account's `dsid` value and may retain it after removing the type-514 plist.
    /// </summary>
    public ulong CachedAccountDsid => ReadMhghUInt64(212);

    private List<ItlRecord> RecordsOf(int type) =>
        Sections.First(s => s.Type == type).List!.Records;

    private ulong ReadMhghUInt64(int offset)
    {
        byte[]? mhgh = Sections.FirstOrDefault(section => section.Type == 12)?.Raw;
        if (mhgh is null || mhgh.Length < offset + sizeof(ulong) ||
            !mhgh.AsSpan(0, 4).SequenceEqual("mhgh"u8))
            return 0;
        int headerLength = BinaryPrimitives.ReadInt32LittleEndian(mhgh.AsSpan(4));
        return headerLength < offset + sizeof(ulong)
            ? 0
            : BinaryPrimitives.ReadUInt64LittleEndian(mhgh.AsSpan(offset));
    }

    private AggregateCounts CurrentCounts => new(Tracks.Count, Playlists.Count, Albums.Count, Artists.Count);

    public bool HasStructuralCountChanges => CurrentCounts != _originalCounts;

    public static ItlDocument Load(string path) => Parse(ItlEnvelope.Load(path));

    public static ItlDocument Parse(ItlEnvelope envelope)
    {
        byte[] body = envelope.Body;
        var sections = new List<ItlSectionNode>();

        foreach (ItlChunk section in ItlChunk.Walk(body, 0, body.Length))
        {
            if (section.Signature != "msdh")
                throw new InvalidDataException($"Expected 'msdh' at {section.Offset}, found '{section.Signature}'.");

            // Section 13 is a second "mlth" of cloud entries: it looks structured but we do not
            // model it, so it stays opaque and survives untouched.
            bool structured = StructuredSectionTypes.Contains(section.Type);
            sections.Add(ItlSectionNode.Read(body, section, structured));
        }

        return new ItlDocument(envelope, sections);
    }

    /// <summary>Serializes the whole body, refreshing every cached length and count.</summary>
    public byte[] Serialize()
    {
        int length = Sections.Sum(s => s.Length);
        byte[] body = new byte[length];

        int position = 0;
        foreach (ItlSectionNode section in Sections)
        {
            section.WriteTo(body.AsSpan(position));
            position += section.Length;
        }

        PatchEnvelopeCopy(body);
        return body;
    }

    /// <summary>
    /// The "mfdh" record inside section 16 mirrors the envelope and records the *uncompressed*
    /// total length, header included. iTunes reads it back, so it has to track what we just wrote.
    /// </summary>
    private void PatchEnvelopeCopy(byte[] body)
    {
        ItlSectionNode section = Sections.First(s => s.Type == EnvelopeCopySectionType);
        int offset = Sections.TakeWhile(s => s != section).Sum(s => s.Length) + section.Header.Length;

        if (Encoding.ASCII.GetString(body, offset, 4) != "mfdh")
            throw new InvalidDataException("Section 16 does not contain the expected 'mfdh' record.");

        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(offset + 8), Envelope.RawHeader.Length + body.Length);
    }

    public void Save(string path, ItlWriteOptions? options = null)
    {
        _ = options; // Retained for source compatibility with the earlier research-only writer API.
        ItlWriter.Save(Envelope, Serialize(), path);
    }

    // ---- tracks -------------------------------------------------------------------------------

    public ItlRecord? FindTrack(int trackId) =>
        Tracks.FirstOrDefault(t => TrackIdOf(t) == trackId);

    public static ulong TrackPersistentIdOf(ItlRecord track) =>
        BinaryPrimitives.ReadUInt64LittleEndian(track.Header.AsSpan(128));

    public ItlRecord? FindTrackByPersistentId(ulong persistentId) =>
        Tracks.FirstOrDefault(track => TrackPersistentIdOf(track) == persistentId);

    /// <summary>
    /// Updates a track string. Native iTunes reuses the mhoh +16 key for an equal semantic value
    /// and allocates the next key for a new distinct value. Location and FileUrl instead carry
    /// fixed structural subtypes, which are preserved from the existing field.
    /// </summary>
    public void SetTrackString(ItlRecord track, ItlDataType type, string value)
    {
        if (!Tracks.Contains(track))
            throw new ArgumentException("The record is not a track in this document.", nameof(track));

        if (type is ItlDataType.FileUrl or ItlDataType.Location)
        {
            track.SetString(type, value);
            return;
        }

        uint? sharedKey = type switch
        {
            ItlDataType.Album => SharedAlbumNameKey(value),
            ItlDataType.Artist or ItlDataType.AlbumArtist => SharedArtistNameKey(value),
            _ => null,
        };
        if (sharedKey.HasValue)
            SetFieldWithKey(track, (int)type, value, sharedKey.Value);
        else
            SetInternedField(Tracks, track, (int)type, value);
        track.SetDateModified(DateTime.UtcNow);
    }

    private uint SharedAlbumNameKey(string value) => SharedStringKey(value,
        Albums.SelectMany(record => record.Fields.Where(field =>
            field.Type == (int)ItlDataType.AlbumRecordName))
        .Concat(Tracks.SelectMany(record => record.Fields.Where(field =>
            field.Type == (int)ItlDataType.Album))));

    private uint SharedArtistNameKey(string value) => SharedStringKey(value,
        Artists.SelectMany(record => record.Fields.Where(field =>
            field.Type == (int)ItlDataType.ArtistRecordName))
        .Concat(Albums.SelectMany(record => record.Fields.Where(field =>
            field.Type is (int)ItlDataType.AlbumRecordArtist or
                (int)ItlDataType.AlbumRecordSortArtist)))
        .Concat(Tracks.SelectMany(record => record.Fields.Where(field =>
            field.Type == (int)ItlDataType.Artist)))
        .Concat(Tracks.SelectMany(record => record.Fields.Where(field =>
            field.Type == (int)ItlDataType.AlbumArtist))));

    private static uint SharedStringKey(string value, IEnumerable<ItlField> domain)
    {
        ItlField[] fields = [.. domain];
        ItlField? existing = fields.FirstOrDefault(field => field.Text == value);
        if (existing is not null)
            return FieldKey(existing);
        return checked(fields.Select(FieldKey).DefaultIfEmpty(0u).Max() + 1);
    }

    private static uint FieldKey(ItlField field) =>
        BinaryPrimitives.ReadUInt32LittleEndian(field.Header.AsSpan(16));

    private static void SetFieldWithKey(ItlRecord record, int type, string value, uint key)
    {
        record.SetField(type, value);
        BinaryPrimitives.WriteUInt32LittleEndian(record.Field(type)!.Header.AsSpan(16), key);
    }

    private static void SetInternedField(IEnumerable<ItlRecord> records, ItlRecord record, int type, string value)
    {
        ItlField[] fields = [.. records.SelectMany(item => item.Fields).Where(field => field.Type == type)];
        uint? existingKey = fields.Where(field => field.Text == value)
            .Select(field => (uint?)BinaryPrimitives.ReadUInt32LittleEndian(field.Header.AsSpan(16)))
            .FirstOrDefault();
        uint key;
        if (existingKey.HasValue)
        {
            key = existingKey.Value;
        }
        else
        {
            uint highest = fields.Select(field => BinaryPrimitives.ReadUInt32LittleEndian(field.Header.AsSpan(16)))
                .DefaultIfEmpty(0u).Max();
            key = checked(highest + 1);
        }

        record.SetField(type, value);
        ItlField field = record.Field(type)!;
        BinaryPrimitives.WriteUInt32LittleEndian(field.Header.AsSpan(16), key);
    }

    public static int TrackIdOf(ItlRecord track) =>
        BinaryPrimitives.ReadInt32LittleEndian(track.Header.AsSpan(16));

    public const int TrackSecondaryIdOffset = 500;
    public static uint TrackSecondaryIdOf(ItlRecord track) =>
        BinaryPrimitives.ReadUInt32LittleEndian(track.Header.AsSpan(TrackSecondaryIdOffset));

    /// <summary>
    /// Adds a track by cloning an existing one. Fields we do not understand are inherited from the
    /// template, which is the only way to produce a header iTunes will accept.
    /// </summary>
    public ItlRecord AddTrack(ItlRecord template)
    {
        int templateId = TrackIdOf(template);
        ItlRecord track = template.Clone();

        int trackId = (int)NextId();
        uint secondaryId = NextId();
        BinaryPrimitives.WriteInt32LittleEndian(track.Header.AsSpan(16), trackId);
        BinaryPrimitives.WriteUInt32LittleEndian(track.Header.AsSpan(TrackSecondaryIdOffset), secondaryId);
        BinaryPrimitives.WriteUInt64LittleEndian(track.Header.AsSpan(128), NewPersistentId());

        // A fresh track has no history.
        foreach (int offset in (int[])[76, 100, 216, 284])
            BinaryPrimitives.WriteUInt32LittleEndian(track.Header.AsSpan(offset), 0);

        uint now = (uint)(DateTime.Now - new DateTime(1904, 1, 1)).TotalSeconds;
        BinaryPrimitives.WriteUInt32LittleEndian(track.Header.AsSpan(120), now);
        BinaryPrimitives.WriteUInt32LittleEndian(track.Header.AsSpan(32), now);

        Tracks.Add(track);
        AddToBuiltInTrackPlaylists(trackId, templateId);
        return track;
    }

    /// <summary>
    /// Removes a track along with every reference to it: its playlist entries and its twin in the
    /// cloud track list, which shares the same track id.
    /// </summary>
    public bool RemoveTrack(int trackId)
    {
        ItlRecord? track = FindTrack(trackId);
        if (track is null)
            return false;

        Tracks.Remove(track);
        CloudTracks.RemoveAll(t => TrackIdOf(t) == trackId);
        foreach (ItlRecord playlist in Playlists)
            playlist.Children.RemoveAll(c => c is ItlEntry e && e.TrackId == trackId);

        return true;
    }

    // ---- playlists ----------------------------------------------------------------------------

    public const int PlaylistPersistentIdOffset = 440;
    public const int PlaylistRecordIdOffset = 3392;

    /// <summary>The library-wide numeric ID carried by every playlist header at +3392.</summary>
    public static uint PlaylistRecordIdOf(ItlRecord playlist) =>
        BinaryPrimitives.ReadUInt32LittleEndian(playlist.Header.AsSpan(PlaylistRecordIdOffset));

    public static string? PlaylistNameOf(ItlRecord playlist) =>
        playlist.Field((int)ItlDataType.PlaylistName)?.Text;

    public static bool IsMasterPlaylist(ItlRecord playlist) => PlaylistNameOf(playlist) == "####!####";

    public static ItlSmartPlaylist? SmartPlaylistOf(ItlRecord playlist)
    {
        ItlField? info = playlist.Field((int)ItlDataType.SmartInfo);
        ItlField? criteria = playlist.Field((int)ItlDataType.SmartCriteria);
        if (info is null && criteria is null) return null;
        if (info is null || criteria is null)
            throw new InvalidDataException($"Playlist '{PlaylistNameOf(playlist)}' has only one smart-playlist blob.");
        return ItlSmartPlaylist.Parse(info.Payload, criteria.Payload);
    }

    /// <summary>
    /// Replaces the two blobs of an existing smart playlist, or converts a manual playlist by
    /// inserting native-compatible zero-key Smart Criteria and Smart Info fields. Native iTunes
    /// accepts the conversion without any fixed-header changes.
    /// </summary>
    public void SetSmartPlaylist(ItlRecord playlist, ItlSmartPlaylist smart)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(smart);
        if (!Playlists.Contains(playlist))
            throw new ArgumentException("The playlist does not belong to this document.", nameof(playlist));
        ItlField? info = playlist.Field((int)ItlDataType.SmartInfo);
        ItlField? criteria = playlist.Field((int)ItlDataType.SmartCriteria);
        (byte[] encodedInfo, byte[] encodedCriteria) = smart.Encode();
        if (info is null && criteria is null)
        {
            int fieldEnd = playlist.Children.FindLastIndex(child => child is ItlField) + 1;
            playlist.Children.Insert(fieldEnd,
                ItlField.CreateBlob((int)ItlDataType.SmartCriteria, encodedCriteria));
            playlist.Children.Insert(fieldEnd + 1,
                ItlField.CreateBlob((int)ItlDataType.SmartInfo, encodedInfo));
            return;
        }
        if (info is null || criteria is null)
            throw new InvalidDataException(
                $"Playlist '{PlaylistNameOf(playlist)}' has only one smart-playlist blob.");

        info.SetBlob(encodedInfo);
        criteria.SetBlob(encodedCriteria);
    }

    public ItlRecord? FindPlaylist(string name) =>
        Playlists.FirstOrDefault(p => PlaylistNameOf(p) == name);

    public IEnumerable<ItlRecord> FindPlaylists(string name, StringComparison comparison) =>
        Playlists.Where(playlist => string.Equals(PlaylistNameOf(playlist), name, comparison));

    /// <summary>
    /// Adds a playlist by cloning a template. Pass a plain manual playlist: a smart playlist's
    /// criteria live in blobs we copy verbatim, and would come along with it.
    /// </summary>
    public ItlRecord AddPlaylist(string name, ItlRecord template)
    {
        ItlRecord playlist = template.Clone();
        playlist.Children.RemoveAll(c => c is ItlEntry);
        playlist.SetField((int)ItlDataType.PlaylistName, name);
        BinaryPrimitives.WriteUInt64LittleEndian(playlist.Header.AsSpan(PlaylistPersistentIdOffset), NewPersistentId());
        BinaryPrimitives.WriteUInt32LittleEndian(playlist.Header.AsSpan(PlaylistRecordIdOffset), NextId());

        Playlists.Add(playlist);
        return playlist;
    }

    /// <summary>
    /// Adds a smart playlist by cloning a native smart-playlist header and child layout. Native
    /// experiments prove Smart Info and Smart Criteria use child key zero; cloning also preserves
    /// version-specific playlist flags without synthesizing them on a manual-playlist header.
    /// </summary>
    public ItlRecord AddSmartPlaylist(string name, ItlSmartPlaylist smart, ItlRecord template,
        IEnumerable<int> initialTrackIds)
    {
        ArgumentNullException.ThrowIfNull(smart);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(initialTrackIds);
        if (!Playlists.Contains(template))
            throw new ArgumentException("The smart-playlist template does not belong to this document.", nameof(template));
        if (SmartPlaylistOf(template) is null)
            throw new InvalidOperationException("Adding a smart playlist requires an existing native smart-playlist template.");
        int[] trackIds = [.. initialTrackIds.Distinct()];
        int? missingTrackId = trackIds.Where(trackId => FindTrack(trackId) is null)
            .Select(trackId => (int?)trackId)
            .FirstOrDefault();
        if (missingTrackId.HasValue)
            throw new ArgumentException($"Initial smart-playlist member track {missingTrackId.Value} is not in this document.",
                nameof(initialTrackIds));

        ItlRecord playlist = AddPlaylist(name, template);
        SetSmartPlaylist(playlist, smart);
        foreach (int trackId in trackIds)
            AddToPlaylist(playlist, trackId);
        return playlist;
    }

    public bool RemovePlaylist(string name)
    {
        ItlRecord? playlist = FindPlaylist(name);
        if (playlist is null || IsMasterPlaylist(playlist))
            return false;

        Playlists.Remove(playlist);
        return true;
    }

    /// <summary>Appends a track to a playlist, minting the entry ids iTunes expects to be unique.</summary>
    public ItlEntry AddToPlaylist(ItlRecord playlist, int trackId)
    {
        ItlEntry template = playlist.Entries.FirstOrDefault()
            ?? Playlists.SelectMany(p => p.Entries).FirstOrDefault()
            ?? throw new InvalidOperationException("No mtph entry anywhere to use as a template.");

        return AddToPlaylist(playlist, trackId, template);
    }

    private ItlEntry AddToPlaylist(ItlRecord playlist, int trackId, ItlEntry template)
    {
        ItlEntry entry = template.Clone();
        entry.TrackId = trackId;
        entry.EntryId = NextId();
        entry.PersistentId = NewPersistentId();

        // The order key is unique per entry and usually ascending within a playlist; its exact
        // meaning is unknown, so we simply continue past the largest one already present.
        entry.OrderKey = playlist.Entries.Select(e => e.OrderKey).DefaultIfEmpty(0u).Max() + 3;

        playlist.Children.Add(entry);
        return entry;
    }

    public bool RemoveFromPlaylist(ItlRecord playlist, int trackId) =>
        playlist.Children.RemoveAll(c => c is ItlEntry e && e.TrackId == trackId) > 0;

    /// <summary>
    /// Replaces a playlist's complete membership after first resolving every requested track.
    /// Duplicate track occurrences and caller order are retained. The fixed playlist header and
    /// all non-membership children remain unchanged.
    /// </summary>
    public void ReplacePlaylistEntries(ItlRecord playlist, IEnumerable<int> trackIds)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(trackIds);
        if (!Playlists.Contains(playlist))
            throw new ArgumentException("The playlist does not belong to this document.", nameof(playlist));

        int[] desired = [.. trackIds];
        int? missing = desired.Where(trackId => FindTrack(trackId) is null)
            .Select(trackId => (int?)trackId).FirstOrDefault();
        if (missing.HasValue)
            throw new ArgumentException($"Track {missing.Value} is not in this document.", nameof(trackIds));

        // Resolve and clone an entry template before changing anything, keeping failure atomic even
        // when this playlist contains the only mtph records in the document.
        ItlEntry? template = playlist.Entries.FirstOrDefault()?.Clone()
            ?? Playlists.SelectMany(candidate => candidate.Entries).FirstOrDefault()?.Clone();
        if (desired.Length > 0 && template is null)
            throw new InvalidOperationException("No mtph entry anywhere to use as a template.");

        playlist.Children.RemoveAll(child => child is ItlEntry);
        foreach (int trackId in desired)
            AddToPlaylist(playlist, trackId, template!);
    }

    /// <summary>
    /// Redirects each occurrence of one track in a playlist to another while preserving entry
    /// identities and ordering. This is useful when consolidating byte-identical tracks.
    /// </summary>
    public int RedirectPlaylistEntries(ItlRecord playlist, int oldTrackId, int replacementTrackId)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        if (!Playlists.Contains(playlist))
            throw new ArgumentException("The playlist does not belong to this document.", nameof(playlist));
        if (FindTrack(replacementTrackId) is null)
            throw new ArgumentException($"Replacement track {replacementTrackId} is not in this document.",
                nameof(replacementTrackId));

        ItlEntry[] matches = [.. playlist.Entries.Where(entry => entry.TrackId == oldTrackId)];
        foreach (ItlEntry entry in matches)
            entry.TrackId = replacementTrackId;
        return matches.Length;
    }

    private void AddToBuiltInTrackPlaylists(int trackId, int templateId)
    {
        foreach (ItlRecord playlist in Playlists.Where(playlist =>
                     IsMasterPlaylist(playlist) ||
                     ((PlaylistNameOf(playlist) is "Downloaded" or "Music") &&
                      playlist.Entries.Any(entry => entry.TrackId == templateId))))
            AddToPlaylist(playlist, trackId);
    }

    // ---- albums and artists -------------------------------------------------------------------

    public ItlRecord AddAlbum(string name, string artist, ItlRecord template)
    {
        uint nameKey = SharedAlbumNameKey(name);
        uint artistKey = SharedArtistNameKey(artist);
        ItlRecord album = template.Clone();
        AssignNewIdentity(album, NextId());
        SetFieldWithKey(album, (int)ItlDataType.AlbumRecordName, name, nameKey);
        SetFieldWithKey(album, (int)ItlDataType.AlbumRecordArtist, artist, artistKey);
        if (album.Field((int)ItlDataType.AlbumRecordSortArtist) is not null)
            SetFieldWithKey(album, (int)ItlDataType.AlbumRecordSortArtist, artist, artistKey);
        Albums.Add(album);
        return album;
    }

    public bool RemoveAlbum(string name)
    {
        ItlRecord? album = Albums.FirstOrDefault(a => a.Field((int)ItlDataType.AlbumRecordName)?.Text == name);
        if (album is null)
            return false;
        uint id = RecordIdOf(album);
        if (Tracks.Any(t => t.GetAlbumId() == id))
            throw new InvalidOperationException($"Album '{name}' is still referenced by one or more tracks.");
        return Albums.Remove(album);
    }

    public ItlRecord AddArtist(string name, ItlRecord template)
    {
        uint nameKey = SharedArtistNameKey(name);
        ItlRecord artist = template.Clone();
        AssignNewIdentity(artist, NextId());
        SetFieldWithKey(artist, (int)ItlDataType.ArtistRecordName, name, nameKey);
        Artists.Add(artist);
        return artist;
    }

    public bool RemoveArtist(string name)
    {
        ItlRecord? artist = Artists.FirstOrDefault(a => a.Field((int)ItlDataType.ArtistRecordName)?.Text == name);
        if (artist is null)
            return false;
        uint id = RecordIdOf(artist);
        if (Tracks.Any(t => t.GetArtistId() == id))
            throw new InvalidOperationException($"Artist '{name}' is still referenced by one or more tracks.");
        return Artists.Remove(artist);
    }

    private static ulong NewPersistentId()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
}
