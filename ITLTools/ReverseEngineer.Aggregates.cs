using System.Buffers.Binary;

namespace iTunes.Binary;

public static partial class ReverseEngineer
{
    /// <summary>
    /// The envelope caches counts of things. Computes every plausible library-wide aggregate and
    /// reports which envelope word each one equals, naming the unknown fields by construction.
    /// </summary>
    public static void Aggregates(ItlLibrary library, string path)
    {
        byte[] file = File.ReadAllBytes(path);
        int headerLength = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(4));

        var candidates = new Dictionary<string, long>
        {
            ["track count"] = library.Tracks.Count,
            ["album count"] = library.Albums.Count,
            ["artist count"] = library.Artists.Count,
            ["playlist count"] = library.Playlists.Count,
            ["section count"] = library.Sections.Count,
            ["cloud track count"] = CloudTrackCount(library),
            ["distinct genres"] = Distinct(library, t => t.Genre),
            ["distinct composers"] = Distinct(library, t => t[ItlDataType.Composer]),
            ["distinct album artists"] = Distinct(library, t => t.AlbumArtist),
            ["distinct artists (from tracks)"] = Distinct(library, t => t.Artist),
            ["distinct albums (from tracks)"] = Distinct(library, t => t.Album),
            ["distinct kinds"] = Distinct(library, t => t.Kind),
            ["max track id"] = library.Tracks.Select(t => (long)t.Id).DefaultIfEmpty(0).Max(),
            ["max track id + 1"] = library.Tracks.Select(t => (long)t.Id).DefaultIfEmpty(0).Max() + 1,
            ["total playlist entries"] = library.Playlists.Sum(p => (long)p.TrackIds.Count),
            ["total mhoh on tracks"] = library.Tracks.Sum(t => (long)t.DataObjects.Count),
            ["sum of sizes (bytes)"] = library.Tracks.Sum(t => (long)t.Size),
            ["sum of sizes (KiB)"] = library.Tracks.Sum(t => (long)t.Size) / 1024,
            ["sum of sizes (MiB)"] = library.Tracks.Sum(t => (long)t.Size) / (1024 * 1024),
            ["sum of durations (ms)"] = library.Tracks.Sum(t => (long)t.Duration.TotalMilliseconds),
            ["sum of durations (s)"] = library.Tracks.Sum(t => (long)t.Duration.TotalSeconds),
            ["sum of play counts"] = library.Tracks.Sum(t => (long)t.PlayCount),
            ["sum of durations (min)"] = library.Tracks.Sum(t => (long)t.Duration.TotalMinutes),
            ["sum of bit rates"] = library.Tracks.Sum(t => (long)t.BitRate),
            ["sum of track ids"] = library.Tracks.Sum(t => (long)t.Id),
            ["sum of sizes / 2048"] = library.Tracks.Sum(t => (long)t.Size) / 2048,
            ["sum of sizes (bytes) mod 2^32"] = library.Tracks.Sum(t => (long)t.Size) % 4294967296L,
            ["sum of durations (ms) mod 2^32"] = library.Tracks.Sum(t => (long)t.Duration.TotalMilliseconds) % 4294967296L,
            ["sum of persistent ids mod 2^32"] = (long)(library.Tracks.Aggregate(0UL, (a, t) => a + t.PersistentId) % 4294967296UL),
            ["playback-state plist entries"] = PlaybackStateEntries(library),
            ["adler32 of body"] = Adler32(library.Envelope.Body),
            ["crc32 of body"] = Crc32(library.Envelope.Body),
            ["byte sum of body mod 2^32"] = library.Envelope.Body.Aggregate(0L, (a, b) => (a + b) % 4294967296L),
        };

        Console.WriteLine("library aggregates:");
        foreach ((string name, long value) in candidates.OrderBy(k => k.Key))
            Console.WriteLine($"  {name,-32} {value,18:N0}");

        Console.WriteLine("\nenvelope words (BE) matched against them:");
        for (int offset = 0; offset + 4 <= headerLength; offset += 4)
        {
            uint be = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(offset));
            if (be == 0)
                continue;

            string[] matches = [.. candidates.Where(c => c.Value == be).Select(c => c.Key)];
            string note = matches.Length > 0 ? string.Join(" / ", matches) : "";
            Console.WriteLine($"  +{offset,-4} {be,12:N0}  {note}");
        }

        // Two words are not 4-aligned counts; show them split as 16-bit halves too.
        Console.WriteLine("\nsuspicious words split as two BE u16:");
        foreach (int offset in (int[])[60, 64, 80, 88, 108])
        {
            if (offset + 4 > headerLength) continue;
            ushort high = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(offset));
            ushort low = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(offset + 2));
            Console.WriteLine($"  +{offset,-4} {high,6} {low,6}");
        }
    }

    /// <summary>Counts the top-level entries of the per-track playback-state plist in mhgh.</summary>
    private static long PlaybackStateEntries(ItlLibrary library)
    {
        byte[] body = library.Envelope.Body;
        ItlSection? section = library.Sections.FirstOrDefault(s => s.Chunk.Type == 12);
        if (section is null)
            return -1;

        ItlChunk mhgh = ItlChunk.Read(body, section.Chunk.BodyOffset);
        foreach (ItlChunk child in ItlChunk.Walk(body, mhgh.HeaderEnd, section.Chunk.EndOffset))
        {
            ItlDataObject o = ItlDataObject.Parse(body, child);
            if (child.Type != 514)
                continue;

            string text = System.Text.Encoding.UTF8.GetString(o.Raw);
            int start = text.IndexOf("<?xml", StringComparison.Ordinal);
            if (start < 0)
                return -1;

            var doc = System.Xml.Linq.XDocument.Parse(text[start..]);
            return doc.Root!.Element("dict")!.Elements("key").Count();
        }
        return -1;
    }

    private static long Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (byte x in data) { a = (a + x) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
    }

    private static long Crc32(byte[] data)
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }

        uint crc = 0xFFFFFFFF;
        foreach (byte x in data)
            crc = table[(crc ^ x) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    private static long Distinct(ItlLibrary library, Func<ItlTrack, string?> selector) =>
        library.Tracks.Select(selector).Where(v => v is not null).Distinct().Count();

    private static long CloudTrackCount(ItlLibrary library)
    {
        byte[] body = library.Envelope.Body;
        ItlSection? section = library.Sections.FirstOrDefault(s => s.Chunk.Type == 13);
        if (section is null)
            return 0;
        ItlChunk list = ItlChunk.Read(body, section.Chunk.BodyOffset);
        return list.ItemCount;
    }
}
