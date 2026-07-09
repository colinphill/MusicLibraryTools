using System.Xml.Linq;

namespace iTunes.Binary;

public static partial class ReverseEngineer
{
    /// <summary>
    /// Some booleans are not bits but values of an enum: "Movie", "TV Show" and "Music Video" are
    /// all one media kind. Finds any byte whose *value* perfectly predicts each boolean, meaning no
    /// value of that byte appears on both a true and a false track.
    /// </summary>
    public static void Predict(ItlLibrary library, string xmlPath)
    {
        Dictionary<int, Dictionary<string, XElement>> xml = XmlTracks(xmlPath);

        string[] boolKeys = [.. xml.Values.SelectMany(t => t)
            .Where(kv => kv.Value.Name == "true" || kv.Value.Name == "false")
            .Select(kv => kv.Key).Distinct().Order()];

        (ItlTrack Track, Dictionary<string, XElement> Xml)[] pairs =
            [.. library.Tracks.Where(t => xml.ContainsKey(t.Id)).Select(t => (t, xml[t.Id]))];

        int length = pairs.Min(p => p.Track.Header.Length);

        foreach (string key in boolKeys)
        {
            bool[] expected = [.. pairs.Select(p => p.Xml.TryGetValue(key, out XElement? e) && e.Name == "true")];
            int trueCount = expected.Count(v => v);
            if (trueCount == 0 || trueCount == expected.Length)
                continue;

            var hits = new List<string>();
            for (int offset = 12; offset < length && hits.Count < 4; offset++)
            {
                var trueValues = new HashSet<byte>();
                var falseValues = new HashSet<byte>();
                for (int i = 0; i < pairs.Length; i++)
                    (expected[i] ? trueValues : falseValues).Add(pairs[i].Track.Header[offset]);

                if (trueValues.Overlaps(falseValues))
                    continue;

                hits.Add($"+{offset} when byte in {{{string.Join(",", trueValues.Order())}}}");
            }

            Console.WriteLine($"  {key,-22} true on {trueCount,6:N0}  {(hits.Count == 0 ? "no predicting byte" : string.Join("   ", hits))}");
        }

        // Show the value distribution of the bytes that turned out to be enums.
        foreach (int offset in (int[])[76 + 0, 100 + 0, 232, 233, 240, 241])
        {
            if (offset >= length)
                continue;
            var histogram = pairs.GroupBy(p => p.Track.Header[offset])
                                 .OrderByDescending(g => g.Count())
                                 .Take(6)
                                 .Select(g => $"{g.Key}={g.Count():N0}");
            Console.WriteLine($"  byte +{offset,-4} distribution: {string.Join("  ", histogram)}");
        }
    }

    /// <summary>Distinct values of one mhoh type, to identify what an unnamed string field holds.</summary>
    public static void Values(ItlLibrary library, int type)
    {
        var values = new Dictionary<string, int>();
        int blobs = 0;

        foreach (ItlTrack track in library.Tracks)
        {
            foreach (ItlDataObject o in track.DataObjects.Where(o => o.Type == type))
            {
                if (o.IsString)
                    values[o.Text!] = values.GetValueOrDefault(o.Text!) + 1;
                else
                    blobs++;
            }
        }

        Console.WriteLine($"mhoh type {type}: {values.Values.Sum():N0} strings ({values.Count:N0} distinct), {blobs:N0} blobs\n");
        foreach ((string value, int count) in values.OrderByDescending(v => v.Value).Take(25))
            Console.WriteLine($"  {count,7:N0}  {Clip(value, 90)}");
    }

    /// <summary>
    /// Groups tracks by the media kind implied by the XML booleans and the "Track Type" string,
    /// then reports which header bytes are constant within each kind.
    /// </summary>
    public static void Kinds(ItlLibrary library, string xmlPath)
    {
        Dictionary<int, Dictionary<string, XElement>> xml = XmlTracks(xmlPath);

        static string KindOf(Dictionary<string, XElement> t)
        {
            bool Has(string k) => t.TryGetValue(k, out XElement? e) && e.Name == "true";
            if (Has("TV Show")) return "TV Show";
            if (Has("Movie")) return "Movie";
            if (Has("Music Video")) return "Music Video";
            if (Has("Has Video")) return "Other Video";
            return "Audio";
        }

        var groups = library.Tracks
            .Where(t => xml.ContainsKey(t.Id))
            .GroupBy(t => KindOf(xml[t.Id]))
            .ToArray();

        Console.WriteLine("media kinds: " + string.Join("  ", groups.Select(g => $"{g.Key}={g.Count():N0}")) + "\n");

        int length = library.Tracks.Min(t => t.Header.Length);
        for (int offset = 12; offset < length; offset++)
        {
            // Constant within every kind, and different between them.
            var perKind = groups.Select(g => g.Select(t => t.Header[offset]).Distinct().ToArray()).ToArray();
            if (perKind.Any(v => v.Length != 1))
                continue;
            if (perKind.Select(v => v[0]).Distinct().Count() < 2)
                continue;

            string detail = string.Join("  ", groups.Zip(perKind).Select(p => $"{p.First.Key}={p.Second[0]}"));
            Console.WriteLine($"  +{offset,-4} {detail}");
        }
    }
}
