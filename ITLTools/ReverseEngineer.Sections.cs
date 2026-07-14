using System.Buffers.Binary;
using System.Text;

namespace iTunes.Binary;

public static partial class ReverseEngineer
{
    /// <summary>Prints all fixed type-15 records without treating their +8 payload as a length.</summary>
    public static void Mprh(ItlLibrary library)
    {
        ItlSection? section = library.Sections.FirstOrDefault(candidate => candidate.Chunk.Type == 15);
        if (section is null)
        {
            Console.WriteLine("type-15 mlrh/mprh section is absent");
            return;
        }
        if (section.InnerSignature != "mlrh")
        {
            Console.WriteLine($"type-15 section has unrecognized inner layout '{section.InnerSignature}'");
            return;
        }

        byte[] body = library.Envelope.Body;
        ItlDocument document = ItlDocument.Parse(library.Envelope);
        ItlChunk list = ItlChunk.Read(body, section.Chunk.BodyOffset);
        IReadOnlyList<ItlFixedItem> records = ItlTraversal.WalkFixedItems(body, list, section.Chunk.EndOffset);
        Console.WriteLine($"type-15 {list.Signature}: {records.Count:N0} fixed {ItlTraversal.MprhLength}-byte records");
        Console.WriteLine(" idx       +8 / Mac date          +12       persistent-id       model matches  payload GUID");
        foreach (ItlFixedItem record in records)
        {
            ReadOnlySpan<byte> bytes = body.AsSpan(record.Offset, record.Length);
            uint word12 = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
            uint word8 = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
            ulong persistentId = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]);
            var matches = new List<string>();
            int trackMatches = library.Tracks.Count(track => track.PersistentId == persistentId);
            ItlRecord[] matchingPlaylists = [.. document.Playlists.Where(playlist =>
                BinaryPrimitives.ReadUInt64LittleEndian(
                    playlist.Header.AsSpan(ItlDocument.PlaylistPersistentIdOffset)) == persistentId)];
            int playlistMatches = matchingPlaylists.Length;
            int entryMatches = document.Playlists.SelectMany(playlist => playlist.Entries)
                .Count(entry => entry.PersistentId == persistentId);
            if (trackMatches > 0) matches.Add($"track:{trackMatches}");
            if (playlistMatches > 0)
            {
                string names = string.Join('|', matchingPlaylists.Select(ItlDocument.PlaylistNameOf));
                string[] entryFields = [.. matchingPlaylists.SelectMany(playlist => playlist.Entries)
                    .SelectMany(entry => new[]
                    {
                        entry.EntryId == word12 ? "entry-id" : null,
                        entry.OrderKey == word12 ? "order-key" : null,
                        entry.TrackId == word12 ? "track-id" : null,
                    })
                    .OfType<string>()
                    .Distinct()];
                matches.Add($"playlist:{names}" + (entryFields.Length == 0 ? "" : $"/{string.Join('|', entryFields)}"));
            }
            if (entryMatches > 0) matches.Add($"entry:{entryMatches}");

            DateTime macDate = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(word8);
            Console.WriteLine($" {((record.Offset - list.HeaderEnd) / ItlTraversal.MprhLength),3}  " +
                              $"{word8:X8} {macDate:yyyy-MM-dd HH:mm:ss}  {word12:X8}  " +
                              $"{persistentId:X16}  {(matches.Count == 0 ? "none" : string.Join(',', matches)),-13}  " +
                              $"{new Guid(bytes[8..24])}");
            foreach (ItlEntry entry in matchingPlaylists.SelectMany(playlist => playlist.Entries)
                         .Where(entry => entry.EntryId == word12))
            {
                ItlTrack? track = library.Tracks.FirstOrDefault(candidate => candidate.Id == entry.TrackId);
                Console.WriteLine($"      -> track {entry.TrackId}, order {entry.OrderKey}, " +
                                  $"'{track?.Artist} - {track?.Title}', added {track?.DateAdded:yyyy-MM-dd HH:mm:ss}");
            }
        }
    }

    /// <summary>Dumps every section we do not model, trying to find structure inside it.</summary>
    public static void Sections(ItlLibrary library)
    {
        byte[] body = library.Envelope.Body;
        int[] modelled = [1, 2, 9, 11, 13, 16];

        foreach (ItlSection section in library.Sections)
        {
            if (modelled.Contains(section.Chunk.Type))
                continue;

            Console.WriteLine($"=== section type {section.Chunk.Type}, inner '{section.InnerSignature}', " +
                              $"{section.Chunk.TotalLength:N0} bytes (header {section.Chunk.HeaderLength}) ===");

            int start = section.Chunk.BodyOffset;
            int end = section.Chunk.EndOffset;

            // Text payloads speak for themselves.
            if (!section.InnerSignature.All(c => c is >= 'a' and <= 'z') || section.InnerSignature is "file")
            {
                Console.WriteLine(Clip(Encoding.UTF8.GetString(body, start, Math.Min(600, end - start)), 600));
                Console.WriteLine();
                continue;
            }

            ItlChunk inner = ItlChunk.Read(body, start);
            Console.WriteLine($"  inner '{inner.Signature}' hlen={inner.HeaderLength} word8={inner.SizeOrCount} word12={inner.Type}");
            Dump(body, start, Math.Min(inner.HeaderLength, 128), start);

            if (ItlTraversal.IsFixedSizeList(inner))
            {
                IReadOnlyList<ItlFixedItem> records = ItlTraversal.WalkFixedItems(body, inner, end);
                Console.WriteLine($"  {records.Count:N0} fixed {ItlTraversal.MprhLength}-byte records follow:");
                foreach (ItlFixedItem record in records.Take(6))
                {
                    uint word8 = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(record.Offset + 8));
                    uint word12 = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(record.Offset + 12));
                    Console.WriteLine($"    '{record.Signature}' +8=0x{word8:X8} +12=0x{word12:X8}");
                }
                if (records.Count > 6)
                    Console.WriteLine("    ...");
                Console.WriteLine();
                continue;
            }

            // Anything after the inner header: chunks, or just bytes?
            int after = start + inner.HeaderLength;
            if (after < end)
            {
                Console.WriteLine($"  {end - after:N0} bytes follow the header; walking as chunks:");
                int count = 0;
                foreach (ItlChunk child in ItlChunk.Walk(body, after, end))
                {
                    if (count++ >= 6) { Console.WriteLine("    ..."); break; }
                    Console.WriteLine($"    '{child.Signature}' hlen={child.HeaderLength} total={child.TotalLength} word12={child.Type}");
                }
                if (count == 0)
                {
                    Console.WriteLine("    (not chunk-structured)");
                    Dump(body, after, Math.Min(96, end - after), after);
                }
            }
            Console.WriteLine();
        }
    }

    /// <summary>Prints the plist blobs iTunes hides inside mhoh objects.</summary>
    public static void Plists(ItlLibrary library)
    {
        byte[] body = library.Envelope.Body;
        var shown = new HashSet<int>();

        foreach (ItlTrack track in library.Tracks.Take(200))
        {
            foreach (ItlDataObject o in track.DataObjects.Where(o => !o.IsString))
            {
                if (!shown.Add(o.Type))
                    continue;

                Console.WriteLine($"=== mhoh type {o.Type} on mith, {o.Raw.Length:N0} bytes ===");
                string text = Encoding.UTF8.GetString(o.Raw);
                int xml = text.IndexOf("<?xml", StringComparison.Ordinal);

                if (xml >= 0)
                    Console.WriteLine(Clip(text[xml..], 900));
                else if (o.Raw.Length >= 8 && Encoding.ASCII.GetString(o.Raw, 0, 6) == "bplist")
                    Console.WriteLine("  binary plist (bplist00)");
                else
                    Dump(o.Raw, 0, Math.Min(96, o.Raw.Length), 0);
                Console.WriteLine();
            }
        }

        // The same for the non-track records.
        foreach (ItlSection section in library.Sections)
        {
            if (section.InnerSignature is not ['m', 'l', _, 'h'] || section.Chunk.Type is 1 or 13)
                continue;

            ItlChunk list = ItlChunk.Read(body, section.Chunk.BodyOffset);
            if (!ItlTraversal.TryWalkChunkItems(body, list, section.Chunk.EndOffset, out var records, out _))
                continue;

            foreach (ItlChunk record in records.Take(3))
            {
                foreach (ItlChunk child in ItlChunk.Walk(body, record.BodyOffset, record.EndOffset))
                {
                    if (child.Signature != "mhoh")
                        continue;
                    ItlDataObject o = ItlDataObject.Parse(body, child);
                    if (o.IsString || !shown.Add(1000 + o.Type))
                        continue;

                    Console.WriteLine($"=== mhoh type {o.Type} on {record.Signature}, {o.Raw.Length:N0} bytes ===");
                    string text = Encoding.UTF8.GetString(o.Raw);
                    int xml = text.IndexOf("<?xml", StringComparison.Ordinal);
                    if (xml >= 0)
                        Console.WriteLine(Clip(text[xml..], 700));
                    else
                        Dump(o.Raw, 0, Math.Min(80, o.Raw.Length), 0);
                    Console.WriteLine();
                }
            }
        }
    }

    /// <summary>Annotates every word of the 144-byte envelope, known and unknown alike.</summary>
    public static void Envelope(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        int headerLength = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(4));

        Console.WriteLine($"hdfm envelope, {headerLength} bytes (big-endian)\n");
        Dump(file, 0, headerLength, 0);

        Console.WriteLine("\nword-by-word (BE u32, and the same bytes as LE / signed / Mac date):");
        for (int offset = 0; offset + 4 <= headerLength; offset += 4)
        {
            uint be = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(offset));
            if (be == 0)
                continue;

            int signed = unchecked((int)be);
            string date = be is > 2_000_000_000 and < 4_000_000_000
                ? $"  as MacDate {MacEpoch.AddSeconds(be):yyyy-MM-dd HH:mm}"
                : "";
            string hours = Math.Abs(signed) % 3600 == 0 && Math.Abs(signed) < 100000
                ? $"  = {signed / 3600.0:+0.#;-0.#}h"
                : "";
            Console.WriteLine($"  +{offset,-4} BE 0x{be:X8} {be,12}  signed {signed,12}{hours}{date}");
        }
    }

    internal static void Dump(byte[] data, int start, int length, int labelBase)
    {
        for (int i = 0; i < length; i += 16)
        {
            int n = Math.Min(16, length - i);
            string hex = Convert.ToHexString(data.AsSpan(start + i, n));
            string ascii = string.Concat(data.Skip(start + i).Take(n).Select(b => b is >= 32 and <= 126 ? (char)b : '.'));
            Console.WriteLine($"  +{labelBase + i - labelBase,-4} {hex,-32} {ascii}");
        }
    }
}
