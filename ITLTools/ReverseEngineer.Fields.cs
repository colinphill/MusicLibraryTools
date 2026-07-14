using System.Buffers.Binary;
using System.Xml.Linq;

namespace iTunes.Binary;

public static partial class ReverseEngineer
{
    private static readonly DateTime MacEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Maps every mhoh type on a track to the XML key that carries the same text.</summary>
    public static void Strings(ItlLibrary library, string xmlPath)
    {
        Dictionary<int, Dictionary<string, XElement>> xml = XmlTracks(xmlPath);

        // Collect the decoded strings of every track, keyed by mhoh type.
        var perType = new Dictionary<int, Dictionary<int, string>>();
        foreach (ItlTrack track in library.Tracks)
        {
            foreach (ItlDataObject o in track.DataObjects.Where(o => o.IsString))
            {
                if (!perType.TryGetValue(o.Type, out var map))
                    perType[o.Type] = map = [];
                map.TryAdd(track.Id, o.Text!);
            }
        }

        string[] stringKeys = [.. xml.Values.SelectMany(t => t)
            .Where(kv => kv.Value.Name == "string")
            .Select(kv => kv.Key).Distinct()];

        Console.WriteLine($"{"type",5} {"tracks",8}  best XML key (agreement)");
        foreach ((int type, var values) in perType.OrderBy(k => k.Key))
        {
            (string Key, double Ratio, int Compared) best = ("", 0, 0);

            foreach (string key in stringKeys)
            {
                int hits = 0, compared = 0;
                foreach ((int id, string text) in values)
                {
                    if (!xml.TryGetValue(id, out var t) || !t.TryGetValue(key, out XElement? element))
                        continue;
                    compared++;
                    if (element.Value == text)
                        hits++;
                }

                if (compared < 20)
                    continue;

                // Prefer the key that agrees; break ties toward the one that covers more tracks,
                // since a TV show's Series and Artist hold the same text but Series covers them all.
                double ratio = (double)hits / compared;
                if (ratio > best.Ratio || (ratio == best.Ratio && compared > best.Compared))
                    best = (key, ratio, compared);
            }

            string known = Enum.IsDefined(typeof(ItlDataType), type) ? $"[{(ItlDataType)type}]" : "";
            string verdict = best.Ratio >= 0.99 ? $"{best.Key} ({best.Ratio:P1} of {best.Compared:N0})"
                           : best.Ratio >= 0.5 ? $"~{best.Key} ({best.Ratio:P1} of {best.Compared:N0})"
                           : "no XML equivalent";
            Console.WriteLine($"{type,5} {values.Count,8:N0}  {verdict,-46} {known}");
        }
    }

    /// <summary>
    /// Finds the bit that carries each boolean XML key. iTunes omits false booleans from the export,
    /// so a track without the key is treated as false.
    /// </summary>
    public static void Flags(ItlLibrary library, string xmlPath)
    {
        Dictionary<int, Dictionary<string, XElement>> xml = XmlTracks(xmlPath);

        string[] boolKeys = [.. xml.Values.SelectMany(t => t)
            .Where(kv => kv.Value.Name == "true" || kv.Value.Name == "false")
            .Select(kv => kv.Key).Distinct().Order()];

        (ItlTrack Track, Dictionary<string, XElement> Xml)[] pairs =
            [.. library.Tracks.Where(t => xml.ContainsKey(t.Id)).Select(t => (t, xml[t.Id]))];

        if (pairs.Length == 0)
        {
            Console.WriteLine($"{boolKeys.Length} boolean keys, no matched tracks to correlate");
            return;
        }

        int length = pairs.Min(p => p.Track.Header.Length);
        Console.WriteLine($"{boolKeys.Length} boolean keys, {pairs.Length:N0} tracks\n");

        foreach (string key in boolKeys)
        {
            bool[] expected = [.. pairs.Select(p => p.Xml.TryGetValue(key, out XElement? e) && e.Name == "true")];
            int trueCount = expected.Count(v => v);
            if (trueCount == 0 || trueCount == expected.Length)
            {
                Console.WriteLine($"  {key,-22} constant {(trueCount > 0 ? "true" : "false")} in this library, not locatable");
                continue;
            }

            var hits = new List<string>();
            for (int offset = 12; offset < length; offset++)
            {
                for (int bit = 0; bit < 8; bit++)
                {
                    bool match = true;
                    for (int i = 0; i < pairs.Length; i++)
                    {
                        bool value = ((pairs[i].Track.Header[offset] >> bit) & 1) == 1;
                        if (value != expected[i]) { match = false; break; }
                    }
                    if (match)
                        hits.Add($"+{offset}.{bit}");
                }
            }

            string where = hits.Count == 0 ? "not found" : string.Join(" ", hits.Take(6));
            Console.WriteLine($"  {key,-22} true on {trueCount,6:N0}  {where}");
        }
    }

    /// <summary>Exhaustive numeric correlation over every integer and date key iTunes exports.</summary>
    public static void Numbers(ItlLibrary library, string xmlPath)
    {
        Dictionary<int, Dictionary<string, XElement>> xml = XmlTracks(xmlPath);

        string[] intKeys = [.. xml.Values.SelectMany(t => t).Where(kv => kv.Value.Name == "integer").Select(kv => kv.Key).Distinct().Order()];
        string[] dateKeys = [.. xml.Values.SelectMany(t => t).Where(kv => kv.Value.Name == "date").Select(kv => kv.Key).Distinct().Order()];

        var tracks = library.Tracks.Where(t => xml.ContainsKey(t.Id)).ToArray();

        foreach (string key in intKeys.Concat(dateKeys))
        {
            bool isDate = dateKeys.Contains(key);

            // iTunes omits zero-valued integers and absent dates from the export, so a track that
            // lacks the key holds zero. Correlating over only the tracks that carry it throws away
            // nearly all the evidence and leaves rare fields unidentifiable.
            (long Expected, byte[] Header)[] pairs =
            [
                .. tracks.Select(t => (xml[t.Id].TryGetValue(key, out XElement? e) ? Value(e, isDate) : 0L, t.Header))
            ];

            int carriers = tracks.Count(t => xml[t.Id].ContainsKey(key));
            if (carriers < 5)
            {
                Console.WriteLine($"  {key,-24} only {carriers} tracks carry it");
                continue;
            }

            // Sample widely: a prefix of the library shares albums and shows almost no variety.
            int stride = Math.Max(1, pairs.Length / 4000);
            pairs = [.. pairs.Where((_, i) => i % stride == 0)];

            int distinct = pairs.Select(p => p.Expected).Distinct().Count();
            int length = pairs.Min(p => p.Header.Length);
            var found = new List<string>();

            foreach (int width in (int[])[1, 2, 4, 8])
            {
                for (int offset = 12; offset + width <= length; offset++)
                {
                    // With absent-as-zero the field must match exactly; a constant delta would break
                    // on the zeros, so only look for equality here.
                    bool consistent = true, anyNonZero = false;

                    foreach ((long expected, byte[] header) in pairs)
                    {
                        long value = (long)Read(header, offset, width);
                        if (value != 0) anyNonZero = true;
                        if (value != expected) { consistent = false; break; }
                    }

                    if (consistent && anyNonZero)
                        found.Add($"+{offset} u{width * 8}");
                }
            }

            // With few distinct values many offsets match by luck, but listing them still narrows
            // the field down to a handful of candidates rather than giving up.
            string note = found.Count == 0 ? "not found"
                        : distinct < 5 ? $"{found.Count} candidates (only {distinct} distinct values): {string.Join("  ", found.Take(6))}"
                        : string.Join("  ", found.Take(4));
            Console.WriteLine($"  {key,-24} n={pairs.Length,4} distinct={distinct,5}  {note}");
        }
    }

    private static long Value(XElement element, bool isDate) => isDate
        ? (long)(DateTime.Parse(element.Value).ToUniversalTime() - MacEpoch).TotalSeconds
        : long.Parse(element.Value);

    private static ulong Read(byte[] header, int offset, int width) => width switch
    {
        1 => header[offset],
        2 => BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(offset)),
        4 => BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(offset)),
        _ => BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(offset)),
    };

    /// <summary>
    /// Hunts for foreign keys: a word that is constant across every track of the same album (or
    /// artist, or genre) and differs between them is that entity's id.
    /// </summary>
    public static void Links(ItlLibrary library)
    {
        (string Label, Func<ItlTrack, string?> Key)[] groupings =
        [
            ("album", t => t.Album is null ? null : $"{t.AlbumArtist} {t.Album}"),
            ("artist", t => t.Artist),
            ("albumArtist", t => t.AlbumArtist),
            ("genre", t => t.Genre),
            ("kind", t => t.Kind),
        ];

        ItlTrack[] tracks = [.. library.Tracks.Take(20000)];
        if (tracks.Length == 0)
        {
            Console.WriteLine("no tracks to correlate");
            return;
        }
        int length = tracks.Min(t => t.Header.Length);

        foreach ((string label, var key) in groupings)
        {
            var groups = tracks.Where(t => key(t) is not null)
                               .GroupBy(key)
                               .Where(g => g.Count() > 1)
                               .ToArray();

            Console.WriteLine($"\n{label}: {groups.Length:N0} groups with >1 track");

            foreach (int width in (int[])[4, 8])
            {
                for (int offset = 12; offset + width <= length; offset++)
                {
                    // Constant inside every group?
                    bool constant = groups.All(g => g.Select(t => Read(t.Header, offset, width)).Distinct().Count() == 1);
                    if (!constant)
                        continue;

                    // And actually discriminating between groups?
                    int distinct = groups.Select(g => Read(g.First().Header, offset, width)).Distinct().Count();
                    if (distinct < groups.Length * 0.9)
                        continue;

                    Console.WriteLine($"  +{offset,-4} u{width * 8,-2} constant per {label}, {distinct:N0} distinct values across {groups.Length:N0} groups");
                }
            }
        }
    }
}
