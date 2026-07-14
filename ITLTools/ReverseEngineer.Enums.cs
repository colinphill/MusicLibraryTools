using System.Xml.Linq;

namespace iTunes.Binary;

public static partial class ReverseEngineer
{
    /// <summary>
    /// Correlates a materialized smart-playlist membership snapshot with otherwise opaque bytes in
    /// the fixed track header. This does not claim that correlated bytes implement the criteria;
    /// it ranks candidates for controlled native experiments.
    /// </summary>
    public static void SmartMembership(ItlLibrary library, string playlistName)
    {
        ItlPlaylist[] matches = [.. library.Playlists.Where(playlist =>
            playlist.Smart is not null && string.Equals(playlist.Name, playlistName, StringComparison.OrdinalIgnoreCase))];
        if (matches.Length == 0)
        {
            Console.WriteLine($"no smart playlist named '{playlistName}'");
            return;
        }
        if (matches.Length > 1)
        {
            Console.WriteLine($"{matches.Length} smart playlists are named '{playlistName}'; use a unique name");
            return;
        }

        ItlPlaylist playlist = matches[0];
        var memberIds = playlist.TrackIds.ToHashSet();
        ItlTrack[] members = [.. library.Tracks.Where(track => memberIds.Contains(track.Id))];
        ItlTrack[] others = [.. library.Tracks.Where(track => !memberIds.Contains(track.Id))];
        int missing = memberIds.Count - members.Select(track => track.Id).Distinct().Count();
        Console.WriteLine($"{playlist.Name}: {playlist.TrackIds.Count:N0} entries, {memberIds.Count:N0} distinct IDs, " +
                          $"{members.Length:N0} resolved tracks, {missing:N0} missing IDs");

        if (members.Length == 0)
        {
            Console.WriteLine("no materialized members to correlate");
            return;
        }
        if (others.Length == 0)
        {
            Console.WriteLine("every track is a member; there is no comparison population");
            return;
        }

        int length = library.Tracks.Min(track => track.Header.Length);
        var bytes = new List<(int Offset, byte[] Members, byte[] Others)>();
        for (int offset = 12; offset < length; offset++)
        {
            byte[] memberValues = [.. members.Select(track => track.Header[offset]).Distinct().Order()];
            byte[] otherValues = [.. others.Select(track => track.Header[offset]).Distinct().Order()];
            if (!memberValues.Intersect(otherValues).Any())
                bytes.Add((offset, memberValues, otherValues));
        }

        Console.WriteLine($"byte-value partitions: {bytes.Count:N0}");
        foreach (var candidate in bytes.Take(32))
            Console.WriteLine($"  +{candidate.Offset,-4} members={Values(candidate.Members)} others={Values(candidate.Others)}");
        if (bytes.Count > 32)
            Console.WriteLine($"  ... {bytes.Count - 32:N0} more");

        var bits = new List<(int Offset, int Bit, bool Value, int FalsePositives)>();
        for (int offset = 12; offset < length; offset++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                int mask = 1 << bit;
                bool value = (members[0].Header[offset] & mask) != 0;
                if (members.Any(track => ((track.Header[offset] & mask) != 0) != value))
                    continue;
                int falsePositives = others.Count(track => ((track.Header[offset] & mask) != 0) == value);
                if (falsePositives != others.Length)
                    bits.Add((offset, bit, value, falsePositives));
            }
        }

        Console.WriteLine("strongest constant-bit candidates:");
        foreach (var candidate in bits.OrderBy(candidate => candidate.FalsePositives)
                                      .ThenBy(candidate => candidate.Offset)
                                      .ThenBy(candidate => candidate.Bit)
                                      .Take(32))
        {
            double rate = 100.0 * candidate.FalsePositives / others.Length;
            Console.WriteLine($"  +{candidate.Offset,-4} bit {candidate.Bit} = {(candidate.Value ? 1 : 0)}  " +
                              $"also true for {candidate.FalsePositives:N0}/{others.Length:N0} others ({rate:N2}%)");
        }

        static string Values(byte[] values)
        {
            const int limit = 12;
            string text = string.Join(",", values.Take(limit));
            return values.Length <= limit ? $"{{{text}}}" : $"{{{text},...}} ({values.Length} values)";
        }
    }

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

        if (pairs.Length == 0)
        {
            Console.WriteLine($"{boolKeys.Length} boolean keys, no matched tracks to correlate");
            return;
        }

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

        if (groups.Length == 0)
        {
            Console.WriteLine("no matched tracks to classify");
            return;
        }

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
