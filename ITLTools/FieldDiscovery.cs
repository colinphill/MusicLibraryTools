using System.Buffers.Binary;
using System.Xml.Linq;

namespace iTunes.Binary;

/// <summary>
/// Locates numeric fields in the fixed "mith" header by brute force: for every offset and width,
/// count how often the value there equals the value iTunes exported to XML for the same track.
/// An offset that agrees on every sampled track is the field.
/// </summary>
public static class FieldDiscovery
{
    /// <summary>iTunes dates are seconds since 1 Jan 1904 UTC.</summary>
    private static readonly DateTime MacEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string[] IntegerKeys =
    [
        "Total Time", "Year", "Track Number", "Track Count", "Disc Number", "Disc Count",
        "Bit Rate", "Sample Rate", "Size", "Play Count", "Skip Count", "Rating",
        "Album Rating", "Start Time", "Stop Time", "Volume Adjustment", "BPM", "Artwork Count",
        "File Folder Count", "Library Folder Count",
    ];

    private static readonly string[] DateKeys = ["Date Added", "Date Modified", "Play Date UTC", "Skip Date"];

    public static void Run(ItlLibrary library, string xmlPath, int sampleSize)
    {
        Dictionary<int, Dictionary<string, long>> xml = LoadXml(xmlPath);

        Dictionary<int, ItlTrack> byId = library.Tracks
            .Where(t => xml.ContainsKey(t.Id))
            .ToDictionary(t => t.Id);

        string[] allKeys = [.. IntegerKeys, .. DateKeys];

        foreach (string key in allKeys)
        {
            // Sample per key, strided across the whole library: neighbouring tracks share an album,
            // and rare keys (Disc Number, BPM) appear on only a fraction of tracks.
            (long Expected, byte[] Header)[] all =
            [
                .. byId.Values.Where(t => xml[t.Id].ContainsKey(key))
                              .Select(t => (xml[t.Id][key], t.Header))
            ];

            int stride = Math.Max(1, all.Length / sampleSize);
            var pairs = all.Where((_, i) => i % stride == 0).Take(sampleSize).ToArray();

            if (pairs.Length < 20)
            {
                Console.WriteLine($"{key,-22} too few tracks carry this key ({all.Length})");
                continue;
            }

            int distinct = pairs.Select(p => p.Expected).Where(v => v != 0).Distinct().Count();
            int minHeader = pairs.Min(p => p.Header.Length);
            var found = new List<(int Offset, int Width, long Delta, bool Scaled)>();

            foreach (int width in (int[])[2, 4, 8])
            {
                for (int offset = 12; offset + width <= minHeader; offset++)
                {
                    // A field matches if the stored value tracks the XML value with a constant
                    // offset. Delta 0 is a plain match; a non-zero delta reveals a different epoch.
                    long delta = 0;
                    bool first = true, consistent = true, anyNonZero = false;

                    foreach ((long expected, byte[] header) in pairs)
                    {
                        long value = (long)ReadUnsigned(header, offset, width);
                        if (value != 0) anyNonZero = true;
                        long d = value - expected;
                        if (first) { delta = d; first = false; }
                        else if (d != delta) { consistent = false; break; }
                    }

                    if (consistent && anyNonZero)
                        found.Add((offset, width, delta, false));

                    // Sample rate and similar are sometimes stored as 16.16 fixed point.
                    if (width == 4)
                    {
                        bool scaled = pairs.All(p =>
                        {
                            ulong v = ReadUnsigned(p.Header, offset, 4);
                            return (v & 0xFFFF) == 0 && (v >> 16) == (ulong)p.Expected;
                        });
                        if (scaled && distinct >= 2)
                            found.Add((offset, width, 0, true));
                    }
                }
            }

            if (distinct < 5)
            {
                Console.WriteLine($"{key,-22} only {distinct} distinct value(s) in library, offset not identifiable ({found.Count} candidates)");
                continue;
            }

            if (found.Count == 0)
            {
                // Nothing agreed on every track. Fall back to the most common delta at each offset:
                // a field can still be identified even when a handful of tracks disagree.
                ReportModal(key, pairs, minHeader);
                continue;
            }

            foreach (var c in found.OrderBy(c => c.Offset).ThenBy(c => c.Width))
            {
                string note = c.Scaled ? "16.16 fixed point" : c.Delta == 0 ? "exact" : $"delta {c.Delta:+#;-#;0}";
                Console.WriteLine($"{key,-22} +{c.Offset,-4} u{c.Width * 8,-2} {note}, {distinct} distinct, n={pairs.Length}");
            }
        }
    }

    /// <summary>Reports the single best offset by most-common delta, tolerating outlier tracks.</summary>
    private static void ReportModal(string key, (long Expected, byte[] Header)[] pairs, int minHeader)
    {
        (int Offset, int Width, bool BigEndian, long Delta, double Ratio) best = default;

        foreach (bool bigEndian in (bool[])[false, true])
        foreach (int width in (int[])[4, 8])
        {
            for (int offset = 12; offset + width <= minHeader; offset++)
            {
                var deltas = new Dictionary<long, int>();
                foreach ((long expected, byte[] header) in pairs)
                {
                    ulong value = ReadUnsigned(header, offset, width, bigEndian);
                    if (value == 0)
                        continue;
                    long d = (long)value - expected;
                    deltas[d] = deltas.GetValueOrDefault(d) + 1;
                }

                if (deltas.Count == 0)
                    continue;

                (long delta, int count) = deltas.MaxBy(kv => kv.Value);
                double ratio = (double)count / pairs.Length;
                if (ratio > best.Ratio)
                    best = (offset, width, bigEndian, delta, ratio);
            }
        }

        if (best.Ratio < 0.5)
        {
            Console.WriteLine($"{key,-22} not found");
            return;
        }

        string endian = best.BigEndian ? "BE" : "LE";
        Console.WriteLine($"{key,-22} +{best.Offset,-4} u{best.Width * 8}{endian} best delta {best.Delta:+#;-#;0} ({best.Ratio:P1})");

        // Show the delta spread at the winning offset: a field stored in local time splits into one
        // delta per UTC offset, which is the signature of a daylight-saving shift.
        var spread = new Dictionary<long, int>();
        int nonZero = 0;
        foreach ((long expected, byte[] header) in pairs)
        {
            ulong value = ReadUnsigned(header, best.Offset, best.Width, best.BigEndian);
            if (value == 0) continue;
            nonZero++;
            long d = (long)value - expected;
            spread[d] = spread.GetValueOrDefault(d) + 1;
        }

        foreach ((long delta, int count) in spread.OrderByDescending(kv => kv.Value).Take(3))
            Console.WriteLine($"{"",-22}   delta {delta,-8:+#;-#;0} on {count,4} of {nonZero} non-zero ({(double)count / nonZero:P1}) = {delta / 3600.0:+0.#;-0.#;0}h");
    }

    private static ulong ReadUnsigned(byte[] header, int offset, int width, bool bigEndian = false) => (width, bigEndian) switch
    {
        (2, false) => BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(offset)),
        (4, false) => BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(offset)),
        (8, false) => BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(offset)),
        (2, true) => BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(offset)),
        (4, true) => BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(offset)),
        _ => BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(offset)),
    };

    private static Dictionary<int, Dictionary<string, long>> LoadXml(string path)
    {
        XDocument doc = XDocument.Load(path);
        XElement tracksDict = doc.Root!.Element("dict")!
            .Elements("key").First(k => k.Value == "Tracks").ElementsAfterSelf().First();

        var result = new Dictionary<int, Dictionary<string, long>>();
        foreach (XElement dict in tracksDict.Elements("dict"))
        {
            var values = new Dictionary<string, long>();
            int id = 0;

            foreach (XElement keyElement in dict.Elements("key"))
            {
                string name = keyElement.Value;
                XElement value = (XElement)keyElement.NextNode!;

                if (name == "Track ID")
                    id = int.Parse(value.Value);
                else if (value.Name == "integer" && IntegerKeys.Contains(name))
                    values[name] = long.Parse(value.Value);
                else if (value.Name == "date" && DateKeys.Contains(name))
                    values[name] = (long)(DateTime.Parse(value.Value).ToUniversalTime() - MacEpoch).TotalSeconds;
            }

            if (id != 0)
                result[id] = values;
        }

        return result;
    }
}
