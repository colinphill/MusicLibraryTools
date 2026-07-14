using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace iTunes.Binary;

/// <summary>A deterministic, machine-readable summary of the fields used by native-iTunes research.</summary>
public sealed record ItlResearchSnapshot(
    int SchemaVersion,
    string FileSha256,
    long FileLength,
    string DecodedBodySha256,
    ItlEnvelopeSnapshot Envelope,
    ItlEnvelopeMirrorSnapshot? EnvelopeMirror,
    ItlAggregateSnapshot ParsedCounts,
    IReadOnlyList<ItlSectionSnapshot> Sections,
    ItlIdentifierSnapshot Identifiers,
    ItlMhghSnapshot? Mhgh,
    IReadOnlyList<ItlValidationIssue> Diagnostics)
{
    public const int CurrentSchemaVersion = 1;

    public static ItlResearchSnapshot Capture(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        ItlEnvelope envelope = ItlEnvelope.Parse(file);
        ItlLibrary library = ItlLibrary.Parse(envelope);
        ItlDocument document = ItlDocument.Parse(envelope);

        ItlSectionNode? cloudSection = document.Sections.FirstOrDefault(section => section.Type == 13);
        ItlRecord[] cloudTracks = cloudSection?.List?.Records.ToArray() ?? [];
        ItlEntry[] entries = [.. document.Playlists.SelectMany(playlist => playlist.Entries)];

        return new ItlResearchSnapshot(
            CurrentSchemaVersion,
            HexHash(file),
            file.LongLength,
            HexHash(envelope.Body),
            new ItlEnvelopeSnapshot(
                envelope.Version,
                envelope.RawHeader.Length,
                envelope.LibraryPersistentId.ToString("X16"),
                envelope.SectionCount,
                envelope.TrackCount,
                envelope.PlaylistCount,
                envelope.AlbumCount,
                envelope.ArtistCount,
                envelope.RawWord88,
                envelope.MaxCryptSize,
                envelope.UtcOffsetSeconds,
                envelope.RawWord108,
                envelope.ModifiedDateSeconds,
                envelope.ModifiedDate),
            CaptureMirror(library),
            new ItlAggregateSnapshot(
                library.Sections.Count,
                document.Tracks.Count,
                document.Playlists.Count,
                document.Albums.Count,
                document.Artists.Count,
                cloudTracks.Length,
                entries.Length),
            [.. library.Sections.Select((section, index) => new ItlSectionSnapshot(
                index,
                section.Chunk.Type,
                section.Chunk.Signature,
                section.InnerSignature,
                section.Chunk.Offset,
                section.Chunk.HeaderLength,
                section.Chunk.TotalLength))],
            new ItlIdentifierSnapshot(
                Sorted(document.Tracks.Select(record => (uint)ItlDocument.TrackIdOf(record))),
                Sorted(document.Tracks.Select(ItlDocument.TrackSecondaryIdOf)),
                Sorted(cloudTracks.Select(record => (uint)ItlDocument.TrackIdOf(record))),
                Sorted(document.Albums.Select(ItlDocument.RecordIdOf)),
                Sorted(document.Artists.Select(ItlDocument.RecordIdOf)),
                Sorted(document.Playlists.Select(ItlDocument.PlaylistRecordIdOf)),
                Sorted(entries.Select(entry => entry.EntryId)),
                HexHashU64(document.Tracks.Select(record => record.GetPersistentId()))),
            CaptureMhgh(library),
            document.Validate());
    }

    private static ItlEnvelopeMirrorSnapshot? CaptureMirror(ItlLibrary library)
    {
        ItlSection? section = library.Sections.FirstOrDefault(candidate => candidate.Chunk.Type == 16);
        if (section is null)
            return null;

        byte[] body = library.Envelope.Body;
        ItlChunk mirror = ItlChunk.Read(body, section.Chunk.BodyOffset);
        if (mirror.Signature != "mfdh" || mirror.HeaderLength < 116)
            return null;

        ReadOnlySpan<byte> header = body.AsSpan(mirror.Offset, mirror.HeaderLength);
        return new ItlEnvelopeMirrorSnapshot(
            BinaryPrimitives.ReadUInt32LittleEndian(header[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[48..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[68..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[72..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[76..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[84..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[88..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[108..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[112..]));
    }

    private static ItlMhghSnapshot? CaptureMhgh(ItlLibrary library)
    {
        ItlSection? section = library.Sections.FirstOrDefault(candidate => candidate.Chunk.Type == 12);
        if (section is null)
            return null;

        byte[] body = library.Envelope.Body;
        ItlChunk mhgh = ItlChunk.Read(body, section.Chunk.BodyOffset);
        byte[] header = body.AsSpan(mhgh.Offset, mhgh.HeaderLength).ToArray();
        ItlDataObject? playback = ItlChunk.Walk(body, mhgh.HeaderEnd, section.Chunk.EndOffset)
            .Where(child => child.Signature == "mhoh" && child.Type == (int)ItlDataType.PlaybackStatePlist)
            .Select(child => ItlDataObject.Parse(body, child))
            .FirstOrDefault();

        var words = new List<ItlHeaderWordSnapshot>();
        for (int offset = 4; offset + 4 <= header.Length; offset += 4)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(offset));
            words.Add(new ItlHeaderWordSnapshot(offset, value, $"{value:X8}"));
        }

        return new ItlMhghSnapshot(
            mhgh.HeaderLength,
            mhgh.SizeOrCount,
            mhgh.Type,
            HexHash(header),
            header.Length > 233 ? header[233] : null,
            words,
            playback is not null,
            playback?.Raw.Length,
            playback is null ? null : HexHash(playback.Raw),
            playback is null ? null : CountPlaybackEntries(playback.Raw));
    }

    private static int? CountPlaybackEntries(byte[] payload)
    {
        string text = Encoding.UTF8.GetString(payload);
        int xml = text.IndexOf("<?xml", StringComparison.Ordinal);
        if (xml < 0)
            return null;

        try
        {
            XDocument document = XDocument.Parse(text[xml..]);
            return document.Root?.Element("dict")?.Elements("key").Count();
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static uint[] Sorted(IEnumerable<uint> values) => [.. values.Order()];

    private static string HexHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string HexHashU64(IEnumerable<ulong> values)
    {
        ulong[] ordered = [.. values.Order()];
        byte[] bytes = new byte[ordered.Length * sizeof(ulong)];
        for (int index = 0; index < ordered.Length; index++)
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(index * sizeof(ulong)), ordered[index]);
        return HexHash(bytes);
    }
}

public sealed record ItlEnvelopeSnapshot(
    string Version,
    int HeaderLength,
    string LibraryPersistentId,
    int SectionCount,
    int TrackCount,
    int PlaylistCount,
    int AlbumCount,
    int ArtistCount,
    uint RawWord88,
    int MaxCryptSize,
    int UtcOffsetSeconds,
    uint RawWord108,
    uint ModifiedDateSeconds,
    DateTime? ModifiedDateUtc);

public sealed record ItlEnvelopeMirrorSnapshot(
    uint TotalLength,
    uint SectionCount,
    uint TrackCount,
    uint PlaylistCount,
    uint AlbumCount,
    uint ArtistCount,
    uint RawWord88,
    uint RawWord108,
    uint ModifiedDateSeconds);

public sealed record ItlAggregateSnapshot(
    int Sections,
    int Tracks,
    int Playlists,
    int Albums,
    int Artists,
    int CloudTracks,
    int PlaylistEntries);

public sealed record ItlSectionSnapshot(
    int Index,
    int Type,
    string Signature,
    string InnerSignature,
    int Offset,
    int HeaderLength,
    int TotalLength);

public sealed record ItlIdentifierSnapshot(
    IReadOnlyList<uint> TrackIds,
    IReadOnlyList<uint> TrackSecondaryIds,
    IReadOnlyList<uint> CloudTrackIds,
    IReadOnlyList<uint> AlbumIds,
    IReadOnlyList<uint> ArtistIds,
    IReadOnlyList<uint> PlaylistIds,
    IReadOnlyList<uint> PlaylistEntryIds,
    string TrackPersistentIdsSha256);

public sealed record ItlMhghSnapshot(
    int HeaderLength,
    int SizeOrCount,
    int Type,
    string HeaderSha256,
    byte? MutationFlag233,
    IReadOnlyList<ItlHeaderWordSnapshot> HeaderWords,
    bool HasPlaybackState,
    int? PlaybackPayloadLength,
    string? PlaybackPayloadSha256,
    int? PlaybackEntryCount);

public sealed record ItlHeaderWordSnapshot(int Offset, uint Value, string HexValue);
