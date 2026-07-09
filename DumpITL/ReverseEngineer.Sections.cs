using System.Buffers.Binary;
using System.Text;

namespace iTunes.Binary;

public static partial class ReverseEngineer
{
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
            foreach (ItlChunk record in ItlChunk.Walk(body, list.HeaderEnd, section.Chunk.EndOffset).Take(3))
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
