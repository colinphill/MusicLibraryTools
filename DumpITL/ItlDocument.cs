using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace iTunes.Binary;

/// <summary>
/// A fully editable library. Sections we understand are parsed into records; the rest are kept
/// byte for byte, so serializing an unmodified document reproduces the original body exactly.
/// </summary>
public sealed class ItlDocument
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

    private ItlDocument(ItlEnvelope envelope, List<ItlSectionNode> sections)
    {
        Envelope = envelope;
        Sections = sections;

        // Every object in the library draws its id from one counter: track ids, album ids, artist
        // ids and playlist entry ids all overlap in range and none collides. Allocate above all.
        uint highestTrack = Tracks.Select(t => (uint)TrackIdOf(t)).DefaultIfEmpty(0u).Max();
        uint highestCloud = CloudTracks.Select(t => (uint)TrackIdOf(t)).DefaultIfEmpty(0u).Max();
        uint highestAlbum = Albums.Select(RecordIdOf).DefaultIfEmpty(0u).Max();
        uint highestArtist = Artists.Select(RecordIdOf).DefaultIfEmpty(0u).Max();
        uint highestEntry = Playlists.SelectMany(p => p.Entries).Select(e => e.EntryId).DefaultIfEmpty(0u).Max();

        _nextId = new[] { highestTrack, highestCloud, highestAlbum, highestArtist, highestEntry }.Max() + 1;
    }

    /// <summary>The record id every "mith", "miah" and "miih" carries at +16.</summary>
    public static uint RecordIdOf(ItlRecord record) =>
        BinaryPrimitives.ReadUInt32LittleEndian(record.Header.AsSpan(16));

    /// <summary>Every record but "miph" carries a unique 64-bit persistent id at +20.</summary>
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

    private List<ItlRecord> RecordsOf(int type) =>
        Sections.First(s => s.Type == type).List!.Records;

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

    public void Save(string path) => ItlWriter.Save(Envelope, Serialize(), path);

    // ---- tracks -------------------------------------------------------------------------------

    public ItlRecord? FindTrack(int trackId) =>
        Tracks.FirstOrDefault(t => TrackIdOf(t) == trackId);

    public static int TrackIdOf(ItlRecord track) =>
        BinaryPrimitives.ReadInt32LittleEndian(track.Header.AsSpan(16));

    /// <summary>
    /// Adds a track by cloning an existing one. Fields we do not understand are inherited from the
    /// template, which is the only way to produce a header iTunes will accept.
    /// </summary>
    public ItlRecord AddTrack(ItlRecord template)
    {
        ItlRecord track = template.Clone();

        int trackId = (int)NextId();
        BinaryPrimitives.WriteInt32LittleEndian(track.Header.AsSpan(16), trackId);
        BinaryPrimitives.WriteUInt64LittleEndian(track.Header.AsSpan(128), NewPersistentId());

        // A fresh track has no history.
        foreach (int offset in (int[])[76, 100, 216, 284])
            BinaryPrimitives.WriteUInt32LittleEndian(track.Header.AsSpan(offset), 0);

        uint now = (uint)(DateTime.Now - new DateTime(1904, 1, 1)).TotalSeconds;
        BinaryPrimitives.WriteUInt32LittleEndian(track.Header.AsSpan(120), now);
        BinaryPrimitives.WriteUInt32LittleEndian(track.Header.AsSpan(32), now);

        Tracks.Add(track);
        AddToMasterPlaylist(trackId);
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

    public static string? PlaylistNameOf(ItlRecord playlist) =>
        playlist.Field((int)ItlDataType.PlaylistName)?.Text;

    public static bool IsMasterPlaylist(ItlRecord playlist) => PlaylistNameOf(playlist) == "####!####";

    public ItlRecord? FindPlaylist(string name) =>
        Playlists.FirstOrDefault(p => PlaylistNameOf(p) == name);

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

        Playlists.Add(playlist);
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

    private void AddToMasterPlaylist(int trackId)
    {
        if (Playlists.FirstOrDefault(IsMasterPlaylist) is { } master)
            AddToPlaylist(master, trackId);
    }

    // ---- albums and artists -------------------------------------------------------------------

    public ItlRecord AddAlbum(string name, string artist, ItlRecord template)
    {
        ItlRecord album = template.Clone();
        AssignNewIdentity(album, NextId());
        album.SetField((int)ItlDataType.AlbumRecordName, name);
        album.SetField((int)ItlDataType.AlbumRecordArtist, artist);
        album.SetField((int)ItlDataType.AlbumRecordSortArtist, artist);
        Albums.Add(album);
        return album;
    }

    public bool RemoveAlbum(string name)
    {
        ItlRecord? album = Albums.FirstOrDefault(a => a.Field((int)ItlDataType.AlbumRecordName)?.Text == name);
        return album is not null && Albums.Remove(album);
    }

    public ItlRecord AddArtist(string name, ItlRecord template)
    {
        ItlRecord artist = template.Clone();
        AssignNewIdentity(artist, NextId());
        artist.SetField((int)ItlDataType.ArtistRecordName, name);
        Artists.Add(artist);
        return artist;
    }

    public bool RemoveArtist(string name)
    {
        ItlRecord? artist = Artists.FirstOrDefault(a => a.Field((int)ItlDataType.ArtistRecordName)?.Text == name);
        return artist is not null && Artists.Remove(artist);
    }

    private static ulong NewPersistentId()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
}
