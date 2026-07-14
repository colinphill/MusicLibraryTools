using System.Buffers.Binary;
using System.Xml.Linq;

namespace iTunes.Binary;

/// <summary>
/// Answers the questions a writer has to settle before it can synthesize playlist records:
/// which words in an "mtph" entry are per-track, which are unique per entry, and where a
/// playlist's persistent id lives inside the 3500-byte "miph" header.
/// </summary>
public static class PlaylistProbe
{
    public static void Run(ItlLibrary library, string xmlPath)
    {
        byte[] body = library.Envelope.Body;
        ItlSection section = library.Sections.First(s => s.Chunk.Type == 2);
        ItlChunk list = ItlChunk.Read(body, section.Chunk.BodyOffset);

        var playlists = new List<(byte[] Header, string? Name, List<byte[]> Entries)>();
        var records = new List<(byte[] Record, string? Name)>();

        foreach (ItlChunk miph in ItlChunk.Walk(body, list.HeaderEnd, section.Chunk.EndOffset))
        {
            string? name = null;
            var entries = new List<byte[]>();

            foreach (ItlChunk child in ItlChunk.Walk(body, miph.BodyOffset, miph.EndOffset))
            {
                if (child.Signature == "mhoh")
                {
                    ItlDataObject o = ItlDataObject.Parse(body, child);
                    if (o.Type == (int)ItlDataType.PlaylistName && o.IsString)
                        name = o.Text;
                }
                else if (child.Signature == "mtph")
                {
                    entries.Add(body.AsSpan(child.Offset, child.HeaderLength).ToArray());
                }
            }

            playlists.Add((body.AsSpan(miph.Offset, miph.HeaderLength).ToArray(), name, entries));

            // The header alone does not hold the persistent id, so keep the record up to its first
            // mtph entry: that covers the header plus every mhoh attribute blob.
            int end = ItlChunk.Walk(body, miph.BodyOffset, miph.EndOffset)
                              .FirstOrDefault(c => c.Signature == "mtph") is { Offset: > 0 } first
                ? first.Offset
                : miph.EndOffset;
            records.Add((body.AsSpan(miph.Offset, end - miph.Offset).ToArray(), name));
        }

        ProbeEntries(playlists);
        ProbePersistentId(playlists, xmlPath);
        SearchPersistentId(records, xmlPath);
    }

    /// <summary>Scans the whole playlist record, blobs included, for the persistent id bytes.</summary>
    private static void SearchPersistentId(List<(byte[] Record, string? Name)> records, string xmlPath)
    {
        Dictionary<string, string> xml = LoadXmlPersistentIds(xmlPath);

        var hits = new Dictionary<string, int>();
        int matched = 0;

        foreach ((byte[] record, string? name) in records)
        {
            string key = name == "####!####" ? "Library" : name ?? "";
            if (!xml.TryGetValue(key, out string? pid))
                continue;
            matched++;

            ulong value = Convert.ToUInt64(pid, 16);
            byte[] le = BitConverter.GetBytes(value);
            byte[] be = [.. le.Reverse()];

            for (int offset = 0; offset + 8 <= record.Length; offset++)
            {
                if (record.AsSpan(offset, 8).SequenceEqual(le))
                    hits[$"offset {offset} LE"] = hits.GetValueOrDefault($"offset {offset} LE") + 1;
                if (record.AsSpan(offset, 8).SequenceEqual(be))
                    hits[$"offset {offset} BE"] = hits.GetValueOrDefault($"offset {offset} BE") + 1;
            }
        }

        Console.WriteLine($"\nsearching {matched} whole playlist records (header + blobs) for the persistent id:");
        foreach ((string where, int count) in hits.OrderByDescending(kv => kv.Value).Take(6))
            Console.WriteLine($"  {where}: found in {count}/{matched}");
        if (hits.Count == 0)
            Console.WriteLine("  the persistent id bytes appear nowhere in the record");
    }

    private static Dictionary<string, string> LoadXmlPersistentIds(string xmlPath)
    {
        XDocument doc = XDocument.Load(xmlPath);
        XElement array = doc.Root!.Element("dict")!
            .Elements("key").First(k => k.Value == "Playlists").ElementsAfterSelf().First();

        var xml = new Dictionary<string, string>();
        foreach (XElement dict in array.Elements("dict"))
        {
            string? name = dict.Elements("key").FirstOrDefault(k => k.Value == "Name")?.ElementsAfterSelf().First().Value;
            string? pid = dict.Elements("key").FirstOrDefault(k => k.Value == "Playlist Persistent ID")?.ElementsAfterSelf().First().Value;
            if (name is not null && pid is not null)
                xml.TryAdd(name, pid);
        }
        return xml;
    }

    private static void ProbeEntries(List<(byte[] Header, string? Name, List<byte[]> Entries)> playlists)
    {
        byte[][] all = [.. playlists.SelectMany(p => p.Entries)];
        int length = all[0].Length;
        Console.WriteLine($"mtph entries: {all.Length:N0}, header {length} bytes\n");

        // Which 4-byte words are globally unique? Those are per-entry identifiers we must synthesize.
        Console.WriteLine("word uniqueness across every entry in the library:");
        for (int offset = 12; offset + 4 <= length; offset += 4)
        {
            var values = all.Select(e => BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(offset))).ToArray();
            int distinct = values.Distinct().Count();
            if (distinct == 1)
                Console.WriteLine($"  +{offset,-3} constant 0x{values[0]:X8}");
            else if (distinct == values.Length)
                Console.WriteLine($"  +{offset,-3} UNIQUE per entry (min {values.Min():N0} max {values.Max():N0})");
            else
                Console.WriteLine($"  +{offset,-3} {distinct:N0} distinct of {values.Length:N0}");
        }

        // Group entries by the track they point at: any word that is constant within a track but
        // varies between tracks can be copied from another playlist's entry for the same track.
        var byTrack = all.GroupBy(e => BinaryPrimitives.ReadInt32LittleEndian(e.AsSpan(24)))
                         .Where(g => g.Count() > 1)
                         .Take(4000)
                         .ToArray();

        Console.WriteLine($"\nwords constant per track (sampled {byTrack.Length:N0} tracks appearing in >1 playlist):");
        for (int offset = 12; offset + 4 <= length; offset += 4)
        {
            int captured = offset;
            bool perTrack = byTrack.All(g => g.Select(e => BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(captured))).Distinct().Count() == 1);
            if (perTrack)
                Console.WriteLine($"  +{offset,-3} same for every entry of a given track");
        }

        // +16 and +32 vary per entry. Are they ordering keys within their own playlist?
        Console.WriteLine("\nwithin each playlist, are +16 / +32 unique and increasing?");
        foreach ((int offset, string label) in ((int, string)[])[(16, "+16"), (32, "+32")])
        {
            int uniqueIn = 0, increasing = 0, considered = 0;
            foreach (var p in playlists.Where(p => p.Entries.Count > 2))
            {
                considered++;
                uint[] values = [.. p.Entries.Select(e => BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(offset)))];
                if (values.Distinct().Count() == values.Length) uniqueIn++;
                if (values.Zip(values.Skip(1)).All(pair => pair.Second > pair.First)) increasing++;
            }
            Console.WriteLine($"  {label}: unique in {uniqueIn}/{considered} playlists, strictly increasing in {increasing}/{considered}");
        }

        // "Constant per track" was an all-or-nothing test. How close is +32 to being per-track?
        foreach (int offset in (int[])[16, 32, 68])
        {
            int captured = offset;
            int constant = byTrack.Count(g => g.Select(e => BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(captured))).Distinct().Count() == 1);
            Console.WriteLine($"  +{offset}: constant across playlists for {constant:N0}/{byTrack.Length:N0} tracks ({(double)constant / byTrack.Length:P1})");
        }

        // Does +32 track the entry's position in the playlist?
        var master = playlists.First(p => p.Name == "####!####");
        Console.WriteLine($"\nmaster playlist, first 8 entries: +16 / +24(trackId) / +32");
        foreach (byte[] e in master.Entries.Take(8))
            Console.WriteLine($"  {BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(16)),8}  {BinaryPrimitives.ReadInt32LittleEndian(e.AsSpan(24)),8}  {BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(32)),8}");
    }

    private static void ProbePersistentId(List<(byte[] Header, string? Name, List<byte[]> Entries)> playlists, string xmlPath)
    {
        XDocument doc = XDocument.Load(xmlPath);
        XElement array = doc.Root!.Element("dict")!
            .Elements("key").First(k => k.Value == "Playlists").ElementsAfterSelf().First();

        var xml = new Dictionary<string, string>();
        foreach (XElement dict in array.Elements("dict"))
        {
            string? name = dict.Elements("key").FirstOrDefault(k => k.Value == "Name")?.ElementsAfterSelf().First().Value;
            string? pid = dict.Elements("key").FirstOrDefault(k => k.Value == "Playlist Persistent ID")?.ElementsAfterSelf().First().Value;
            if (name is not null && pid is not null)
                xml.TryAdd(name, pid);
        }

        var matched = playlists
            .Where(p => p.Name is not null && xml.ContainsKey(p.IsMasterName() ? "Library" : p.Name))
            .Select(p => (p.Header, Pid: xml[p.IsMasterName() ? "Library" : p.Name!]))
            .ToArray();

        Console.WriteLine($"\nmiph offsets holding the playlist persistent id ({matched.Length} playlists matched to XML):");
        int minLength = matched.Min(m => m.Header.Length);
        bool any = false;

        // Try every plausible byte order: iTunes writes track ids as a plain LE64 but is not
        // consistent about it elsewhere.
        (string Label, Func<byte[], int, string> Read)[] readers =
        [
            ("u64 LE", (h, o) => $"{BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(o)):X16}"),
            ("u64 BE", (h, o) => $"{BinaryPrimitives.ReadUInt64BigEndian(h.AsSpan(o)):X16}"),
            ("2x u32 LE, high word first", (h, o) =>
                $"{BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(o)):X8}{BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(o + 4)):X8}"),
            ("2x u32 BE, high word first", (h, o) =>
                $"{BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(o)):X8}{BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(o + 4)):X8}"),
        ];

        foreach ((string label, var read) in readers)
        {
            for (int offset = 12; offset + 8 <= minLength; offset++)
            {
                if (matched.All(m => read(m.Header, offset) == m.Pid.ToUpperInvariant()))
                {
                    Console.WriteLine($"  +{offset} {label}");
                    any = true;
                }
            }
        }

        if (!any)
            Console.WriteLine("  none -- the playlist persistent id is not stored plainly in the miph header");
    }

    private static bool IsMasterName(this (byte[] Header, string? Name, List<byte[]> Entries) p) => p.Name == "####!####";
}
