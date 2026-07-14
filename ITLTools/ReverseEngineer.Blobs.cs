using System.Text;
using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
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
        ItlChunk[] stateChunks = [.. ItlChunk.Walk(body, mhgh.HeaderEnd, section.Chunk.EndOffset)
            .Where(c => c.Signature == "mhoh" && c.Type == (int)ItlDataType.PlaybackStatePlist)];
        if (stateChunks.Length == 0)
        {
            Console.WriteLine("mhgh has no playback-state plist");
            return;
        }
        ItlChunk stateChunk = stateChunks[0];
        ItlDataObject state = ItlDataObject.Parse(body, stateChunk);

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
        TestPlaybackTokenCandidates(library, section, mhgh, stateChunk, state.Raw);
        XElement playbackDict = document.Root!.Element("dict")!;
        SummarizePlaybackPlist(playbackDict);
        CorrelatePlaybackState(library, playbackDict);
        string[] outerKeys = [.. playbackDict.Elements("key").Select(key => key.Value)];
        TestDecimalPlaybackKeys(library, outerKeys);
        string[] keys = [.. playbackDict.Elements("key").Select(k => k.Value)
            .Where(k => k.Length == 32 && k.All(Uri.IsHexDigit))];
        var keySet = keys.Select(key => key.ToLowerInvariant()).ToHashSet();
        TestCandidateHashes(library, keySet);
        TestDataObjectHashes(library, keySet);
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

    private static void CorrelatePlaybackState(ItlLibrary library, XElement dictionary)
    {
        DateTime appleEpoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var tracksByState = library.Tracks
            .Where(track => track.PlayDate is not null)
            .GroupBy(track => (track.PlayCount, Seconds: new DateTimeOffset(track.PlayDate!.Value).ToUnixTimeSeconds()))
            .ToDictionary(group => group.Key, group => group.ToArray());

        XElement[] children = [.. dictionary.Elements()];
        int usable = 0, unique = 0, ambiguous = 0, missing = 0;
        int uniqueHex = 0, uniqueDecimal = 0;
        var examples = new List<string>();
        for (int index = 0; index + 1 < children.Length; index += 2)
        {
            if (children[index].Name != "key" || children[index + 1].Name != "dict")
                continue;

            string key = children[index].Value;
            Dictionary<string, XElement> fields = PlistFields(children[index + 1]);
            if (!fields.TryGetValue("plct", out XElement? playCountElement) ||
                !int.TryParse(playCountElement.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int playCount) ||
                !fields.TryGetValue("tstm", out XElement? timestampElement) ||
                !double.TryParse(timestampElement.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double timestamp) ||
                !double.IsFinite(timestamp))
                continue;

            usable++;
            long stateSecond = new DateTimeOffset(appleEpoch.AddSeconds(timestamp)).ToUnixTimeSeconds();
            ItlTrack[] matches = new long[] { -1L, 0L, 1L }
                .SelectMany(delta => tracksByState.TryGetValue((playCount, stateSecond + delta), out ItlTrack[]? found)
                    ? found
                    : [])
                .Distinct()
                .ToArray();
            if (matches.Length == 0)
            {
                missing++;
                continue;
            }
            if (matches.Length > 1)
            {
                ambiguous++;
                continue;
            }

            unique++;
            if (key.Length == 32 && key.All(Uri.IsHexDigit)) uniqueHex++;
            else if (uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out _)) uniqueDecimal++;
            if (examples.Count < 8)
            {
                ItlTrack track = matches[0];
                examples.Add($"{key} -> [{track.Id}] {track.Artist} - {track.Title} " +
                             $"(plct={playCount}, tstm={timestamp:R})");
            }
        }

        Console.WriteLine("playback-value correlations (plct plus tstm within one second of mith play state):");
        Console.WriteLine($"  usable={usable:N0}, unique={unique:N0} (hex={uniqueHex:N0}, decimal={uniqueDecimal:N0}), " +
                          $"ambiguous={ambiguous:N0}, unmatched={missing:N0}");
        foreach (string example in examples) Console.WriteLine($"    {Clip(example, 120)}");
        CorrelateBookmarkWords(library, children);

        static Dictionary<string, XElement> PlistFields(XElement dictionaryElement)
        {
            XElement[] nested = [.. dictionaryElement.Elements()];
            var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
            for (int nestedIndex = 0; nestedIndex + 1 < nested.Length; nestedIndex += 2)
            {
                if (nested[nestedIndex].Name == "key")
                    result[nested[nestedIndex].Value] = nested[nestedIndex + 1];
            }
            return result;
        }
    }

    private static void CorrelateBookmarkWords(ItlLibrary library, XElement[] plistChildren)
    {
        var states = new List<(string Key, int PlayCount, uint Milliseconds)>();
        for (int index = 0; index + 1 < plistChildren.Length; index += 2)
        {
            if (plistChildren[index].Name != "key" || plistChildren[index + 1].Name != "dict")
                continue;

            XElement[] nested = [.. plistChildren[index + 1].Elements()];
            XElement? bookmark = null, playCount = null;
            for (int nestedIndex = 0; nestedIndex + 1 < nested.Length; nestedIndex += 2)
            {
                if (nested[nestedIndex].Name != "key") continue;
                if (nested[nestedIndex].Value == "bktm") bookmark = nested[nestedIndex + 1];
                else if (nested[nestedIndex].Value == "plct") playCount = nested[nestedIndex + 1];
            }

            if (bookmark is null || playCount is null ||
                !double.TryParse(bookmark.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) ||
                !int.TryParse(playCount.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) ||
                !double.IsFinite(seconds) || seconds <= 0 || seconds * 1000 > uint.MaxValue)
                continue;

            states.Add((plistChildren[index].Value, count, (uint)Math.Round(seconds * 1000)));
        }
        if (states.Count == 0) return;

        int headerLength = library.Tracks.Select(track => track.Header.Length).DefaultIfEmpty(0).Min();
        var wantedValues = states.Select(state => state.Milliseconds).ToHashSet();
        var results = new List<(int Offset, int Unique, int Ambiguous, int UniqueWithPlayCount)>();
        for (int offset = 12; offset + 4 <= headerLength; offset += 4)
        {
            var tracksByValue = new Dictionary<uint, List<ItlTrack>>();
            foreach (ItlTrack track in library.Tracks)
            {
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(track.Header.AsSpan(offset));
                if (!wantedValues.Contains(value)) continue;
                if (!tracksByValue.TryGetValue(value, out List<ItlTrack>? tracks))
                    tracksByValue[value] = tracks = [];
                tracks.Add(track);
            }

            int unique = 0, ambiguous = 0, uniqueWithPlayCount = 0;
            foreach ((_, int statePlayCount, uint milliseconds) in states)
            {
                if (!tracksByValue.TryGetValue(milliseconds, out List<ItlTrack>? tracks)) continue;
                if (tracks.Count == 1) unique++;
                else ambiguous++;
                if (tracks.Count(track => track.PlayCount == statePlayCount) == 1) uniqueWithPlayCount++;
            }
            if (unique > 0 || uniqueWithPlayCount > 0)
                results.Add((offset, unique, ambiguous, uniqueWithPlayCount));
        }

        Console.WriteLine($"bookmark word candidates ({states.Count:N0} nonzero bktm values, rounded to milliseconds; " +
                          "chance matches are expected):");
        if (results.Count == 0)
        {
            Console.WriteLine("  no aligned fixed-header word contains a playback bookmark value");
            return;
        }
        foreach ((int offset, int unique, int ambiguous, int uniqueWithPlayCount) in results
                 .OrderByDescending(result => result.UniqueWithPlayCount)
                 .ThenByDescending(result => result.Unique)
                 .Take(12))
            Console.WriteLine($"  mith +{offset,-4} unique={unique,5:N0}, ambiguous={ambiguous,5:N0}, " +
                              $"unique with plct={uniqueWithPlayCount,5:N0}");
        if (results.All(result => result.Offset != 624))
            Console.WriteLine("  mith +624, changed by controlled native bookmark edits, has no corpus match");
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

    private static void TestPlaybackTokenCandidates(
        ItlLibrary library,
        ItlSection section,
        ItlChunk mhgh,
        ItlChunk stateChunk,
        byte[] payload)
    {
        byte[] body = library.Envelope.Body;
        byte[] persistentIdLittle = new byte[8];
        byte[] persistentIdBig = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(persistentIdLittle, library.Envelope.LibraryPersistentId);
        BinaryPrimitives.WriteUInt64BigEndian(persistentIdBig, library.Envelope.LibraryPersistentId);

        (string Name, byte[] Bytes)[] ranges =
        [
            ("payload", payload),
            ("mhoh", body.AsSpan(stateChunk.Offset, stateChunk.TotalLength).ToArray()),
            ("mhgh header", body.AsSpan(mhgh.Offset, mhgh.HeaderLength).ToArray()),
            ("mhgh record", body.AsSpan(mhgh.Offset, section.Chunk.EndOffset - mhgh.Offset).ToArray()),
            ("msdh section", body.AsSpan(section.Chunk.Offset, section.Chunk.TotalLength).ToArray()),
            ("library-id LE + payload", Join(persistentIdLittle, payload)),
            ("payload + library-id LE", Join(payload, persistentIdLittle)),
            ("library-id BE + payload", Join(persistentIdBig, payload)),
            ("payload + library-id BE", Join(payload, persistentIdBig)),
        ];

        uint token = library.Envelope.RawWord108;
        uint reversedToken = BinaryPrimitives.ReverseEndianness(token);
        var matches = new List<string>();
        foreach ((string rangeName, byte[] bytes) in ranges)
        {
            foreach ((string algorithm, uint value) in HashCandidates(bytes))
            {
                if (value == token) matches.Add($"{rangeName}: {algorithm}");
                else if (value == reversedToken) matches.Add($"{rangeName}: {algorithm} (byte-reversed)");
            }
        }

        if (matches.Count == 0)
            Console.WriteLine("+108 token candidates: no standard 32-bit or truncated digest match");
        else
            foreach (string match in matches) Console.WriteLine($"+108 token candidate MATCH: {match}");

        static byte[] Join(byte[] left, byte[] right)
        {
            byte[] result = new byte[left.Length + right.Length];
            left.CopyTo(result, 0);
            right.CopyTo(result, left.Length);
            return result;
        }
    }

    private static IEnumerable<(string Name, uint Value)> HashCandidates(byte[] bytes)
    {
        yield return ("CRC-32", (uint)Crc32(bytes));
        yield return ("CRC-32C", Crc32C(bytes));
        yield return ("Adler-32", (uint)Adler32(bytes));
        yield return ("FNV-1", Fnv1(bytes));
        yield return ("FNV-1a", Fnv1A(bytes));
        yield return ("DJB2", Djb2(bytes));
        yield return ("SDBM", Sdbm(bytes));
        yield return ("Jenkins", Jenkins(bytes));
        yield return ("Murmur3", Murmur3(bytes));

        foreach ((string name, byte[] digest) in new[]
                 {
                     ("MD5", MD5.HashData(bytes)),
                     ("SHA-1", SHA1.HashData(bytes)),
                     ("SHA-256", SHA256.HashData(bytes)),
                 })
        {
            yield return ($"{name} first LE", BinaryPrimitives.ReadUInt32LittleEndian(digest));
            yield return ($"{name} first BE", BinaryPrimitives.ReadUInt32BigEndian(digest));
            yield return ($"{name} last LE", BinaryPrimitives.ReadUInt32LittleEndian(digest.AsSpan(digest.Length - 4)));
            yield return ($"{name} last BE", BinaryPrimitives.ReadUInt32BigEndian(digest.AsSpan(digest.Length - 4)));
        }
    }

    private static uint Crc32C(byte[] bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0x82F63B78u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    private static uint Fnv1(byte[] bytes)
    {
        uint hash = 2166136261;
        foreach (byte value in bytes) hash = unchecked(hash * 16777619) ^ value;
        return hash;
    }

    private static uint Fnv1A(byte[] bytes)
    {
        uint hash = 2166136261;
        foreach (byte value in bytes) hash = unchecked((hash ^ value) * 16777619);
        return hash;
    }

    private static uint Djb2(byte[] bytes)
    {
        uint hash = 5381;
        foreach (byte value in bytes) hash = unchecked(hash * 33 + value);
        return hash;
    }

    private static uint Sdbm(byte[] bytes)
    {
        uint hash = 0;
        foreach (byte value in bytes) hash = unchecked(value + (hash << 6) + (hash << 16) - hash);
        return hash;
    }

    private static uint Jenkins(byte[] bytes)
    {
        uint hash = 0;
        foreach (byte value in bytes)
        {
            hash = unchecked(hash + value);
            hash = unchecked(hash + (hash << 10));
            hash ^= hash >> 6;
        }
        hash = unchecked(hash + (hash << 3));
        hash ^= hash >> 11;
        return unchecked(hash + (hash << 15));
    }

    private static uint Murmur3(byte[] bytes)
    {
        const uint c1 = 0xCC9E2D51;
        const uint c2 = 0x1B873593;
        uint hash = 0;
        int position = 0;
        while (position + 4 <= bytes.Length)
        {
            uint block = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position));
            block = unchecked(block * c1);
            block = BitOperations.RotateLeft(block, 15);
            block = unchecked(block * c2);
            hash ^= block;
            hash = BitOperations.RotateLeft(hash, 13);
            hash = unchecked(hash * 5 + 0xE6546B64);
            position += 4;
        }

        uint tail = 0;
        int remaining = bytes.Length - position;
        if (remaining >= 3) tail ^= (uint)bytes[position + 2] << 16;
        if (remaining >= 2) tail ^= (uint)bytes[position + 1] << 8;
        if (remaining >= 1)
        {
            tail ^= bytes[position];
            tail = unchecked(tail * c1);
            tail = BitOperations.RotateLeft(tail, 15);
            tail = unchecked(tail * c2);
            hash ^= tail;
        }

        hash ^= (uint)bytes.Length;
        hash ^= hash >> 16;
        hash = unchecked(hash * 0x85EBCA6B);
        hash ^= hash >> 13;
        hash = unchecked(hash * 0xC2B2AE35);
        hash ^= hash >> 16;
        return hash;
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

    private static void TestDataObjectHashes(ItlLibrary library, HashSet<string> keys)
    {
        var matches = new Dictionary<(int Type, string Input), HashSet<(string Key, int TrackId)>>();
        foreach (ItlTrack track in library.Tracks)
        {
            foreach (ItlDataObject field in track.DataObjects)
            {
                Test("mhoh body", field.Raw);
                if (!field.IsString) continue;
                Test("encoded payload", field.Payload);
                Test("UTF-8 text", Encoding.UTF8.GetBytes(field.Text!));
                Test("UTF-16LE text", Encoding.Unicode.GetBytes(field.Text!));
                Test("UTF-8 text + NUL", [.. Encoding.UTF8.GetBytes(field.Text!), 0]);
                Test("UTF-16LE text + NUL", [.. Encoding.Unicode.GetBytes(field.Text!), 0, 0]);

                void Test(string input, byte[] bytes)
                {
                    string hash = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
                    if (!keys.Contains(hash)) return;
                    if (!matches.TryGetValue((field.Type, input), out HashSet<(string, int)>? found))
                        matches[(field.Type, input)] = found = [];
                    found.Add((hash, track.Id));
                }
            }
        }

        Console.WriteLine("all data-object MD5 correlations:");
        if (matches.Count == 0)
        {
            Console.WriteLine("  no playback key hashes an entire mhoh body or encoded string payload");
            return;
        }
        foreach (((int type, string input), HashSet<(string Key, int TrackId)> found) in matches
                 .OrderByDescending(match => match.Value.Count)
                 .ThenBy(match => match.Key.Type)
                 .ThenBy(match => match.Key.Input))
        {
            Console.WriteLine($"  mhoh {type,-3} {input,-20} {found.Count,7:N0} track/key matches " +
                              $"({found.Select(match => match.Key).Distinct().Count():N0} keys)");
            foreach ((string key, int trackId) in found.Take(4))
            {
                ItlTrack track = library.Tracks.First(candidate => candidate.Id == trackId);
                Console.WriteLine($"    {key} -> [{trackId}] {Clip($"{track.Artist} - {track.Title}", 88)}");
            }
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

        var xml = new Dictionary<string, Queue<(byte[]? Info, byte[]? Criteria)>>(StringComparer.Ordinal);
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

            if (!xml.TryGetValue(name, out Queue<(byte[]? Info, byte[]? Criteria)>? values))
                xml[name] = values = new();
            values.Enqueue((Data("Smart Info"), Data("Smart Criteria")));
        }

        Console.WriteLine($"{"playlist",-28} {"mhoh 102 == Smart Info",24}  {"mhoh 101 == Smart Criteria",26}");
        foreach (ItlPlaylist playlist in library.Playlists.Where(playlist => playlist.Smart is not null))
        {
            string name = playlist.Name ?? "(unnamed)";
            (byte[]? Info, byte[]? Criteria) expected = xml.TryGetValue(name, out var values) && values.Count > 0
                ? values.Dequeue()
                : (null, null);
            ItlSmartPlaylist smart = playlist.Smart!;
            Console.WriteLine($"{Clip(name, 28),-28} {Compare(smart.Info.Raw, expected.Info),24}  " +
                              $"{Compare(smart.Criteria.Raw, expected.Criteria),26}");
            Console.WriteLine($"  info: live={smart.Info.LiveUpdating} match={smart.Info.MatchRules} " +
                              $"checked={smart.Info.CheckedOnly} limit={smart.Info.HasLimit}" +
                              (smart.Info.HasLimit
                                  ? $" {smart.Info.LimitSize} {smart.Info.LimitUnit}, sort={smart.Info.SortField}, descending={smart.Info.Descending}"
                                  : ""));
            PrintCriteria(smart.Criteria, "  ");
        }

        static string Compare(byte[]? actual, byte[]? expected)
        {
            if (actual is null && expected is null) return "-";
            if (actual is null) return "missing in itl";
            if (expected is null) return "missing in xml";
            if (actual.AsSpan().SequenceEqual(expected)) return $"IDENTICAL ({actual.Length}b)";
            return $"differ ({actual.Length} vs {expected.Length}b)";
        }

        static void PrintCriteria(ItlSmartCriteria criteria, string indent)
        {
            Console.WriteLine($"{indent}{criteria.Conjunction}: {criteria.Rules.Count} rule(s)");
            foreach (ItlSmartRule rule in criteria.Rules)
            {
                if (rule.NestedCriteria is not null)
                {
                    Console.WriteLine($"{indent}- nested {rule.Sign} {rule.Operator}");
                    PrintCriteria(rule.NestedCriteria, indent + "  ");
                    continue;
                }

                string value = rule.ValueKind switch
                {
                    ItlSmartValueKind.String => $"\"{Clip(rule.StringValue ?? "", 70)}\"",
                    ItlSmartValueKind.Playlist => rule.PlaylistPersistentId?.ToString("X16") ?? "(missing)",
                    ItlSmartValueKind.Date when rule.RelativeSeconds != 0 => $"relative {rule.RelativeSeconds:N0}s",
                    ItlSmartValueKind.Date when rule.DateValues.Count > 0 => string.Join(" .. ", rule.DateValues.Select(date => date.ToString("O"))),
                    ItlSmartValueKind.Unknown => Convert.ToHexString(rule.RawValue.AsSpan(0, Math.Min(24, rule.RawValue.Length))),
                    _ => string.Join(", ", rule.IntegerValues),
                };
                Console.WriteLine($"{indent}- {rule.Field} (0x{rule.RawField:X}) {rule.Sign} {rule.Operator}: " +
                                  $"{value} [{rule.ValueKind}, {rule.RawValue.Length}b]");
            }
        }
    }
}
