using System.Text;
using System.Buffers.Binary;
using System.Security.Cryptography;
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
            if (!ItlTraversal.TryWalkChunkItems(body, list, section.Chunk.EndOffset, out var records, out _))
                continue;

            foreach (ItlChunk record in records)
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

    /// <summary>Correlates the 128-bit keys in the mhgh playback-state plist with track-header bytes.</summary>
    public static void PlaybackLinks(ItlLibrary library)
    {
        byte[] body = library.Envelope.Body;
        ItlSection? section = library.Sections.FirstOrDefault(s => s.Chunk.Type == 12);
        if (section is null || library.Tracks.Count == 0)
        {
            Console.WriteLine("playback correlation requires mhgh and at least one track");
            return;
        }

        ItlChunk mhgh = ItlChunk.Read(body, section.Chunk.BodyOffset);
        ItlDataObject? state = ItlChunk.Walk(body, mhgh.HeaderEnd, section.Chunk.EndOffset)
            .Where(c => c.Signature == "mhoh" && c.Type == (int)ItlDataType.PlaybackStatePlist)
            .Select(c => ItlDataObject.Parse(body, c)).FirstOrDefault();
        if (state is null)
        {
            Console.WriteLine("mhgh has no playback-state plist");
            return;
        }

        string text = Encoding.UTF8.GetString(state.Raw);
        int xmlStart = text.IndexOf("<?xml", StringComparison.Ordinal);
        if (xmlStart < 0)
        {
            Console.WriteLine("playback-state payload is not an XML plist");
            return;
        }

        XDocument document = XDocument.Parse(text[xmlStart..]);
        byte[] xmlBytes = state.Raw.AsSpan(xmlStart).ToArray();
        Console.WriteLine($"envelope +108=0x{library.Envelope.RawWord108:X8}; " +
                          $"payload crc32=0x{Crc32(state.Raw):X8} adler32=0x{Adler32(state.Raw):X8}; " +
                          $"xml crc32=0x{Crc32(xmlBytes):X8} adler32=0x{Adler32(xmlBytes):X8}");
        XElement playbackDict = document.Root!.Element("dict")!;
        SummarizePlaybackPlist(playbackDict);
        string[] outerKeys = [.. playbackDict.Elements("key").Select(key => key.Value)];
        TestDecimalPlaybackKeys(library, outerKeys);
        string[] keys = [.. playbackDict.Elements("key").Select(k => k.Value)
            .Where(k => k.Length == 32 && k.All(Uri.IsHexDigit))];
        var keySet = keys.Select(key => key.ToLowerInvariant()).ToHashSet();
        TestCandidateHashes(library, keySet);
        var direct = new HashSet<(ulong, ulong)>();
        var reversed = new HashSet<(ulong, ulong)>();
        foreach (string key in keys)
        {
            byte[] bytes = Convert.FromHexString(key);
            direct.Add((BinaryPrimitives.ReadUInt64LittleEndian(bytes), BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8))));
            Array.Reverse(bytes);
            reversed.Add((BinaryPrimitives.ReadUInt64LittleEndian(bytes), BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8))));
        }

        int length = library.Tracks.Min(t => t.Header.Length);
        Console.WriteLine($"{keys.Length:N0} playback keys; scanning {library.Tracks.Count:N0} track headers ({length} bytes)");
        bool found = false;
        for (int offset = 12; offset + 16 <= length; offset++)
        {
            int directHits = 0, reverseHits = 0;
            foreach (ItlTrack track in library.Tracks)
            {
                var value = (BinaryPrimitives.ReadUInt64LittleEndian(track.Header.AsSpan(offset)),
                             BinaryPrimitives.ReadUInt64LittleEndian(track.Header.AsSpan(offset + 8)));
                if (direct.Contains(value)) directHits++;
                if (reversed.Contains(value)) reverseHits++;
            }
            if (directHits == 0 && reverseHits == 0) continue;
            found = true;
            Console.WriteLine($"  +{offset,-4} direct={directHits:N0} reversed={reverseHits:N0}");
        }
        if (!found)
            Console.WriteLine("  no raw or byte-reversed 128-bit key appears in a fixed track-header window");
    }

    private static void SummarizePlaybackPlist(XElement dictionary)
    {
        XElement[] children = [.. dictionary.Elements()];
        var fields = new Dictionary<(string Name, string Type), (int Count, HashSet<string> Samples)>();
        var samples = new List<string>();
        var outerKeys = new List<string>();
        int entries = 0, dictionaries = 0, empty = 0;

        for (int index = 0; index + 1 < children.Length; index += 2)
        {
            if (children[index].Name != "key")
                continue;

            entries++;
            string outerKey = children[index].Value;
            outerKeys.Add(outerKey);
            XElement value = children[index + 1];
            if (value.Name != "dict")
            {
                Add("(top-level)", value.Name.LocalName, value.Value);
                continue;
            }

            dictionaries++;
            XElement[] nested = [.. value.Elements()];
            if (nested.Length == 0) empty++;
            var rendered = new List<string>();
            for (int nestedIndex = 0; nestedIndex + 1 < nested.Length; nestedIndex += 2)
            {
                if (nested[nestedIndex].Name != "key")
                    continue;
                string name = nested[nestedIndex].Value;
                XElement nestedValue = nested[nestedIndex + 1];
                Add(name, nestedValue.Name.LocalName, nestedValue.Value);
                rendered.Add($"{name}={nestedValue.Name.LocalName}:{Clip(nestedValue.Value, 32)}");
            }
            if (samples.Count < 5)
                samples.Add($"  {outerKey}: {string.Join(", ", rendered)}");
        }

        Console.WriteLine($"type-514 structure: {entries:N0} top-level entries, " +
                          $"{dictionaries:N0} dictionaries ({empty:N0} empty)");
        int hexKeys = outerKeys.Count(key => key.Length == 32 && key.All(Uri.IsHexDigit));
        Console.WriteLine($"  outer keys: {hexKeys:N0} are 32 hexadecimal characters; " +
                          $"{outerKeys.Count - hexKeys:N0} use another form");
        foreach (IGrouping<int, string> group in outerKeys
                     .Where(key => key.Length != 32 || !key.All(Uri.IsHexDigit))
                     .GroupBy(key => key.Length).OrderBy(group => group.Key))
        {
            Console.WriteLine($"    length {group.Key,-3} {group.Count(),5:N0}: " +
                              string.Join(" | ", group.Take(4).Select(key => Clip(key, 50))));
        }
        foreach (((string name, string type), (int count, HashSet<string> values)) in
                 fields.OrderBy(field => field.Key.Name).ThenBy(field => field.Key.Type))
        {
            Console.WriteLine($"  {name,-14} {type,-8} {count,7:N0}  " +
                              string.Join(" | ", values.Select(value => Clip(value, 50))));
        }
        Console.WriteLine("sample playback entries:");
        foreach (string sample in samples) Console.WriteLine(sample);

        void Add(string name, string type, string value)
        {
            (int Count, HashSet<string> Samples) current = fields.TryGetValue((name, type), out var found)
                ? found
                : (0, []);
            current.Count++;
            if (current.Samples.Count < 4) current.Samples.Add(value);
            fields[(name, type)] = current;
        }
    }

    private static void TestCandidateHashes(ItlLibrary library, HashSet<string> keys)
    {
        string libraryId = library.Envelope.LibraryPersistentId.ToString("X16");
        (string Label, Func<ItlTrack, byte[]?> Bytes)[] candidates =
        [
            ("persistent ID uppercase", t => Utf8(t.PersistentId.ToString("X16"))),
            ("persistent ID lowercase", t => Utf8(t.PersistentId.ToString("x16"))),
            ("persistent ID decimal", t => Utf8(t.PersistentId.ToString())),
            ("track ID decimal", t => Utf8(t.Id.ToString())),
            ("store item ID decimal", t => t.StoreItemId == 0 ? null : Utf8(t.StoreItemId.ToString())),
            ("store identifier", t => Text(t[ItlDataType.StoreIdentifier])),
            ("location", t => Text(t.Location)),
            ("lowercase location", t => Text(t.Location?.ToLowerInvariant())),
            ("file URL", t => Text(t[ItlDataType.FileUrl])),
            ("title", t => Text(t.Title)),
            ("title UTF-16LE", t => Unicode(t.Title)),
            ("artist - title", t => Text(t.Artist is null || t.Title is null ? null : $"{t.Artist} - {t.Title}")),
            ("artist - title UTF-16LE", t => Unicode(t.Artist is null || t.Title is null ? null : $"{t.Artist} - {t.Title}")),
            ("artist NUL title", t => Text(t.Artist is null || t.Title is null ? null : $"{t.Artist}\0{t.Title}")),
            ("album artist - title", t => Text(t.AlbumArtist is null || t.Title is null ? null : $"{t.AlbumArtist} - {t.Title}")),
            ("series - title", t => Text(t[ItlDataType.Series] is not { } series || t.Title is null ? null : $"{series} - {t.Title}")),
            ("album - title", t => Text(t.Album is null || t.Title is null ? null : $"{t.Album} - {t.Title}")),
            ("title - artist", t => Text(t.Artist is null || t.Title is null ? null : $"{t.Title} - {t.Artist}")),
            ("artist - album - title", t => Text(t.Artist is null || t.Album is null || t.Title is null ? null : $"{t.Artist} - {t.Album} - {t.Title}")),
            ("filename", t => Text(t.Location is null ? null : Path.GetFileName(t.Location))),
            ("filename without extension", t => Text(t.Location is null ? null : Path.GetFileNameWithoutExtension(t.Location))),
            ("persistent ID with 0x", t => Utf8("0x" + t.PersistentId.ToString("X16"))),
            ("library ID + persistent ID", t => Utf8(libraryId + t.PersistentId.ToString("X16"))),
            ("library ID: persistent ID", t => Utf8(libraryId + ":" + t.PersistentId.ToString("X16"))),
            ("persistent ID raw LE", t => RawPersistentId(t.PersistentId, littleEndian: true)),
            ("persistent ID raw BE", t => RawPersistentId(t.PersistentId, littleEndian: false)),
        ];

        Console.WriteLine("candidate MD5 correlations:");
        bool any = false;
        foreach ((string label, Func<ItlTrack, byte[]?> bytes) in candidates)
        {
            int matches = 0;
            var examples = new List<string>();
            foreach (ItlTrack track in library.Tracks)
            {
                byte[]? input = bytes(track);
                if (input is null) continue;
                string hash = Convert.ToHexString(MD5.HashData(input)).ToLowerInvariant();
                if (!keys.Contains(hash)) continue;
                matches++;
                if (examples.Count < 4)
                    examples.Add($"[{track.Id}] {track.Artist} - {track.Title} ({track.Kind})");
            }
            if (matches == 0) continue;
            any = true;
            Console.WriteLine($"  {label,-32} {matches,7:N0} matches");
            foreach (string example in examples) Console.WriteLine($"    {Clip(example, 110)}");
        }
        if (!any) Console.WriteLine("  no matches for common track identity inputs");

        static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
        static byte[]? Text(string? value) => value is null ? null : Encoding.UTF8.GetBytes(value);
        static byte[]? Unicode(string? value) => value is null ? null : Encoding.Unicode.GetBytes(value);
        static byte[] RawPersistentId(ulong value, bool littleEndian)
        {
            byte[] bytes = new byte[8];
            if (littleEndian) BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
            else BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
            return bytes;
        }
    }

    private static void TestDecimalPlaybackKeys(ItlLibrary library, IEnumerable<string> outerKeys)
    {
        var decimalKeys = new HashSet<uint>();
        foreach (string key in outerKeys)
        {
            if (uint.TryParse(key, out uint value)) decimalKeys.Add(value);
        }
        if (decimalKeys.Count == 0) return;

        Console.WriteLine($"decimal-key correlations ({decimalKeys.Count:N0} keys):");
        int headerLength = library.Tracks.Select(track => track.Header.Length).DefaultIfEmpty(0).Min();
        bool found = false;
        for (int offset = 12; offset + 4 <= headerLength; offset++)
        {
            ItlTrack[] matches = [.. library.Tracks.Where(track =>
                decimalKeys.Contains(BinaryPrimitives.ReadUInt32LittleEndian(track.Header.AsSpan(offset))))];
            if (matches.Length == 0) continue;
            found = true;
            Console.WriteLine($"  fixed header +{offset,-4} {matches.Length,5:N0} matches");
            foreach (ItlTrack track in matches.Take(4))
            {
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(track.Header.AsSpan(offset));
                Console.WriteLine($"    {value} -> [{track.Id}] {Clip($"{track.Artist} - {track.Title}", 90)}");
            }
        }

        var stringMatches = new List<(uint Key, ItlTrack Track, int Type, string Value)>();
        foreach (ItlTrack track in library.Tracks)
        {
            foreach (ItlDataObject field in track.DataObjects.Where(field => field.IsString))
            {
                string value = field.Text!;
                ReadOnlySpan<char> candidate = value.AsSpan();
                int colon = value.LastIndexOf(':');
                if (colon >= 0) candidate = candidate[(colon + 1)..];
                if (uint.TryParse(candidate, out uint key) && decimalKeys.Contains(key))
                    stringMatches.Add((key, track, field.Type, value));
            }
        }
        if (stringMatches.Count > 0)
        {
            found = true;
            Console.WriteLine($"  string fields {stringMatches.Count:N0} matches");
            foreach ((uint key, ItlTrack track, int type, string value) in stringMatches.Take(8))
                Console.WriteLine($"    {key} -> [{track.Id}] mhoh {type}: {Clip(value, 80)}");
        }
        if (!found) Console.WriteLine("  no decimal key appears in a current fixed header or string field");
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
