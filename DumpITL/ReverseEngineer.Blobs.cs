using System.Text;
using System.Xml.Linq;

namespace iTunes.Binary;

public static partial class ReverseEngineer
{
    /// <summary>Prints one whole payload of an mhoh type, string or blob, from any record kind.</summary>
    public static void Blob(ItlLibrary library, int type)
    {
        byte[] body = library.Envelope.Body;

        foreach (ItlSection section in library.Sections)
        {
            if (section.InnerSignature is not ['m', 'l', _, 'h'])
                continue;

            ItlChunk list = ItlChunk.Read(body, section.Chunk.BodyOffset);
            foreach (ItlChunk record in ItlChunk.Walk(body, list.HeaderEnd, section.Chunk.EndOffset))
            {
                foreach (ItlChunk child in ItlChunk.Walk(body, record.BodyOffset, record.EndOffset))
                {
                    if (child.Signature != "mhoh")
                        continue;

                    ItlDataObject o = ItlDataObject.Parse(body, child);
                    if (o.Type != type)
                        continue;

                    Console.WriteLine($"mhoh type {type} on '{record.Signature}', payload {o.Raw.Length:N0} bytes, string={o.IsString}\n");
                    if (o.IsString)
                    {
                        Console.WriteLine(o.Text);
                    }
                    else
                    {
                        string text = Encoding.UTF8.GetString(o.Raw);
                        int xml = text.IndexOf("<?xml", StringComparison.Ordinal);
                        if (xml >= 0)
                            Console.WriteLine(text[xml..]);
                        else
                            Dump(o.Raw, 0, Math.Min(160, o.Raw.Length), 0);
                    }
                    return;
                }
            }
        }

        Console.WriteLine($"no mhoh of type {type} found");
    }

    /// <summary>Walks the library-info section, whose mhoh children are not attached to any record.</summary>
    public static void Mhgh(ItlLibrary library)
    {
        byte[] body = library.Envelope.Body;
        ItlSection section = library.Sections.First(s => s.Chunk.Type == 12);
        ItlChunk mhgh = ItlChunk.Read(body, section.Chunk.BodyOffset);

        Console.WriteLine($"mhgh header {mhgh.HeaderLength} bytes, word8={mhgh.SizeOrCount}, word12={mhgh.Type}");
        Dump(body, mhgh.Offset, mhgh.HeaderLength, 0);

        Console.WriteLine("\nchildren:");
        foreach (ItlChunk child in ItlChunk.Walk(body, mhgh.HeaderEnd, section.Chunk.EndOffset))
        {
            ItlDataObject o = ItlDataObject.Parse(body, child);
            Console.WriteLine($"\n  mhoh type {child.Type}, payload {o.Raw.Length:N0} bytes, string={o.IsString}");

            if (o.IsString)
            {
                Console.WriteLine($"    \"{Clip(o.Text!, 200)}\"");
                continue;
            }

            string text = Encoding.UTF8.GetString(o.Raw, 0, Math.Min(400, o.Raw.Length));
            int xml = text.IndexOf("<?xml", StringComparison.Ordinal);
            if (xml >= 0)
                Console.WriteLine("    " + Clip(text[xml..], 300));
            else
                Dump(o.Raw, 0, Math.Min(64, o.Raw.Length), 0);
        }
    }

    /// <summary>
    /// iTunes exports smart playlists as base64 "Smart Info" and "Smart Criteria". If the miph
    /// blobs are the same bytes, we have identified them beyond doubt.
    /// </summary>
    public static void Smart(ItlLibrary library, string xmlPath)
    {
        XDocument doc = XDocument.Load(xmlPath);
        XElement array = doc.Root!.Element("dict")!
            .Elements("key").First(k => k.Value == "Playlists").ElementsAfterSelf().First();

        var xml = new Dictionary<string, (byte[]? Info, byte[]? Criteria)>();
        foreach (XElement dict in array.Elements("dict"))
        {
            string? name = dict.Elements("key").FirstOrDefault(k => k.Value == "Name")?.ElementsAfterSelf().First().Value;
            if (name is null)
                continue;

            byte[]? Data(string key)
            {
                XElement? e = dict.Elements("key").FirstOrDefault(k => k.Value == key)?.ElementsAfterSelf().First();
                return e is null ? null : Convert.FromBase64String(e.Value);
            }

            xml.TryAdd(name, (Data("Smart Info"), Data("Smart Criteria")));
        }

        byte[] body = library.Envelope.Body;
        ItlSection section = library.Sections.First(s => s.Chunk.Type == 2);
        ItlChunk list = ItlChunk.Read(body, section.Chunk.BodyOffset);

        Console.WriteLine($"{"playlist",-28} {"mhoh 102 == Smart Info",24}  {"mhoh 101 == Smart Criteria",26}");

        foreach (ItlChunk miph in ItlChunk.Walk(body, list.HeaderEnd, section.Chunk.EndOffset))
        {
            string? name = null;
            byte[]? blob101 = null, blob102 = null;

            foreach (ItlChunk child in ItlChunk.Walk(body, miph.BodyOffset, miph.EndOffset))
            {
                if (child.Signature != "mhoh")
                    continue;
                ItlDataObject o = ItlDataObject.Parse(body, child);
                switch (o.Type)
                {
                    case 100: name = o.Text; break;
                    case 101: blob101 = o.Raw; break;
                    case 102: blob102 = o.Raw; break;
                }
            }

            if (name is null || (blob101 is null && blob102 is null))
                continue;
            if (!xml.TryGetValue(name, out var expected))
                continue;

            Console.WriteLine($"{Clip(name, 28),-28} {Compare(blob102, expected.Info),24}  {Compare(blob101, expected.Criteria),26}");
        }

        static string Compare(byte[]? actual, byte[]? expected)
        {
            if (actual is null && expected is null) return "-";
            if (actual is null) return "missing in itl";
            if (expected is null) return "missing in xml";
            if (actual.AsSpan().SequenceEqual(expected)) return $"IDENTICAL ({actual.Length}b)";
            return $"differ ({actual.Length} vs {expected.Length}b)";
        }
    }
}
