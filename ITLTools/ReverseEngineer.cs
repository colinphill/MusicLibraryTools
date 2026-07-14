using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;

namespace iTunes.Binary;

/// <summary>Data-driven reverse engineering of the parts of the format we do not yet understand.</summary>
public static partial class ReverseEngineer
{
    /// <summary>Every key iTunes exports for a track, with its plist type and how many tracks carry it.</summary>
    public static void Keys(ItlLibrary library, string xmlPath)
    {
        var keys = new Dictionary<string, (string Type, int Count)>();
        foreach (Dictionary<string, XElement> track in XmlTracks(xmlPath).Values)
        {
            foreach ((string key, XElement value) in track)
            {
                (string Type, int Count) entry = keys.TryGetValue(key, out var e) ? e : (value.Name.LocalName, 0);
                keys[key] = (entry.Type, entry.Count + 1);
            }
        }

        Console.WriteLine($"--- XML track keys ({keys.Count}) ---");
        foreach ((string key, (string type, int count)) in keys.OrderBy(k => k.Value.Type).ThenByDescending(k => k.Value.Count))
            Console.WriteLine($"  {key,-28} {type,-8} {count,7:N0}");

        // Now the other side: which mhoh types actually occur, on which record, and how they decode.
        byte[] body = library.Envelope.Body;
        var seen = new Dictionary<(string Record, int Type), (int Count, int Strings, string Sample)>();

        foreach (ItlSection section in library.Sections)
        {
            if (section.InnerSignature is not ['m', 'l', _, 'h'])
                continue;

            ItlChunk list = ItlChunk.Read(body, section.Chunk.BodyOffset);
            if (!ItlTraversal.TryWalkChunkItems(body, list, section.Chunk.EndOffset, out var records, out _))
                continue;

            foreach (ItlChunk record in records)
            {
                foreach (ItlChunk child in ItlChunk.Walk(body, record.BodyOffset, record.EndOffset))
                {
                    if (child.Signature != "mhoh")
                        continue;

                    ItlDataObject o = ItlDataObject.Parse(body, child);
                    var key = (record.Signature, o.Type);
                    (int Count, int Strings, string Sample) entry = seen.TryGetValue(key, out var e) ? e : (0, 0, "");
                    string sample = entry.Sample;
                    if (sample.Length == 0)
                        sample = o.IsString ? Clip(o.Text!, 44) : $"<blob {o.Raw.Length}b {Convert.ToHexString(o.Raw.AsSpan(0, Math.Min(8, o.Raw.Length)))}>";
                    seen[key] = (entry.Count + 1, entry.Strings + (o.IsString ? 1 : 0), sample);
                }
            }
        }

        Console.WriteLine($"\n--- mhoh types present ({seen.Count}) ---");
        Console.WriteLine($"  {"rec",-6} {"type",5} {"count",9} {"str%",6}  sample");
        foreach (((string rec, int type), (int count, int strings, string sample)) in seen.OrderBy(k => k.Key.Record).ThenBy(k => k.Key.Type))
        {
            string known = Enum.IsDefined(typeof(ItlDataType), type) ? ((ItlDataType)type).ToString() : "";
            Console.WriteLine($"  {rec,-6} {type,5} {count,9:N0} {(double)strings / count,6:P0}  {sample}  {known}");
        }
    }

    /// <summary>Which bytes of a record header ever change? Everything else is dead space.</summary>
    public static void Map(ItlLibrary library, string recordSignature)
    {
        byte[][] headers = HeadersOf(library, recordSignature);
        if (headers.Length == 0)
        {
            Console.WriteLine($"{recordSignature}: no records");
            return;
        }
        int length = headers.Min(h => h.Length);
        Console.WriteLine($"{recordSignature}: {headers.Length:N0} records, header {length} bytes\n");

        var alwaysZero = new List<int>();
        var constant = new List<(int Offset, byte Value)>();
        var varying = new List<int>();

        for (int i = 0; i < length; i++)
        {
            byte first = headers[0][i];
            bool same = true;
            foreach (byte[] h in headers)
            {
                if (h[i] != first) { same = false; break; }
            }

            if (!same) varying.Add(i);
            else if (first == 0) alwaysZero.Add(i);
            else constant.Add((i, first));
        }

        Console.WriteLine($"always zero : {alwaysZero.Count} bytes  {Ranges(alwaysZero)}");
        Console.WriteLine($"constant    : {constant.Count} bytes  {string.Join(" ", constant.Select(c => $"+{c.Offset}=0x{c.Value:X2}"))}");
        Console.WriteLine($"varying     : {varying.Count} bytes  {Ranges(varying)}");
    }

    internal static byte[][] HeadersOf(ItlLibrary library, string signature)
    {
        byte[] body = library.Envelope.Body;
        var headers = new List<byte[]>();

        foreach (ItlSection section in library.Sections)
        {
            if (section.InnerSignature is not ['m', 'l', _, 'h'])
                continue;

            ItlChunk list = ItlChunk.Read(body, section.Chunk.BodyOffset);
            if (!ItlTraversal.TryWalkChunkItems(body, list, section.Chunk.EndOffset, out var records, out _))
                continue;

            foreach (ItlChunk record in records)
            {
                if (record.Signature == signature)
                    headers.Add(body.AsSpan(record.Offset, record.HeaderLength).ToArray());
            }
        }

        return [.. headers];
    }

    internal static string Ranges(List<int> offsets)
    {
        if (offsets.Count == 0)
            return "";

        var parts = new List<string>();
        int start = offsets[0], previous = offsets[0];

        foreach (int offset in offsets.Skip(1))
        {
            if (offset == previous + 1) { previous = offset; continue; }
            parts.Add(start == previous ? $"{start}" : $"{start}-{previous}");
            start = previous = offset;
        }
        parts.Add(start == previous ? $"{start}" : $"{start}-{previous}");
        return string.Join(",", parts);
    }

    internal static string Clip(string s, int max)
    {
        s = s.ReplaceLineEndings(" ");
        return s.Length <= max ? s : s[..max] + "…";
    }

    /// <summary>Track id -> its XML keys, as raw elements so callers can read any plist type.</summary>
    internal static Dictionary<int, Dictionary<string, XElement>> XmlTracks(string xmlPath)
    {
        XDocument doc = XDocument.Load(xmlPath);
        XElement tracks = doc.Root!.Element("dict")!
            .Elements("key").First(k => k.Value == "Tracks").ElementsAfterSelf().First();

        var result = new Dictionary<int, Dictionary<string, XElement>>();
        foreach (XElement dict in tracks.Elements("dict"))
        {
            var values = new Dictionary<string, XElement>();
            foreach (XElement key in dict.Elements("key"))
                values[key.Value] = (XElement)key.NextNode!;
            result[int.Parse(values["Track ID"].Value)] = values;
        }
        return result;
    }
}
