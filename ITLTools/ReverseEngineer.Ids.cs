using System.Buffers.Binary;

namespace iTunes.Binary;

public static partial class ReverseEngineer
{
    /// <summary>Correlates each child mhoh +16 word with record order and record-local grouping.</summary>
    public static void ChildKeys(ItlDocument document)
    {
        (string Label, IEnumerable<ItlRecord> Records, Func<ItlRecord, uint> Id)[] groups =
        [
            ("track", document.Tracks, r => (uint)ItlDocument.TrackIdOf(r)),
            ("album", document.Albums, ItlDocument.RecordIdOf),
            ("artist", document.Artists, ItlDocument.RecordIdOf),
            ("playlist", document.Playlists, ItlDocument.PlaylistRecordIdOf),
        ];

        foreach ((string label, IEnumerable<ItlRecord> records, Func<ItlRecord, uint> id) in groups)
        {
            ItlRecord[] recordArray = [.. records];
            int fields = 0, ordinalMatches = 0, zero = 0, uniformRecords = 0;
            for (int index = 0; index < recordArray.Length; index++)
            {
                ItlRecord record = recordArray[index];
                uint[] keys = [.. record.Fields.Select(field =>
                    BinaryPrimitives.ReadUInt32LittleEndian(field.Header.AsSpan(16)))];
                if (keys.Length > 0 && keys.Distinct().Count() == 1) uniformRecords++;
                foreach (ItlField field in record.Fields)
                {
                    fields++;
                    uint child = BinaryPrimitives.ReadUInt32LittleEndian(field.Header.AsSpan(16));
                    if (child == index + 1u) ordinalMatches++;
                    else if (child == 0) zero++;
                }
            }

            Console.WriteLine($"{label,-9} records={recordArray.Length,7:N0} fields={fields,9:N0} " +
                              $"ordinal matches={ordinalMatches,9:N0} zero={zero,7:N0} uniform records={uniformRecords,7:N0}");

            if (label == "playlist")
            {
                foreach ((ItlRecord record, int index) in recordArray.Select((record, index) => (record, index)).TakeLast(6))
                {
                    uint[] keys = [.. record.Fields.Select(field =>
                        BinaryPrimitives.ReadUInt32LittleEndian(field.Header.AsSpan(16))).Distinct().Order()];
                    Console.WriteLine($"  [{index + 1,2}] id={id(record),5} keys={string.Join(',', keys)} " +
                                      $"name=\"{ItlDocument.PlaylistNameOf(record)}\"");
                    Console.WriteLine("       " + string.Join(' ', record.Fields.Select(field =>
                        $"{field.Type}:{BinaryPrimitives.ReadUInt32LittleEndian(field.Header.AsSpan(16))}")));
                }
            }
            else if (recordArray.Length is > 0 and <= 5)
            {
                foreach ((ItlRecord record, int index) in recordArray.Select((record, index) => (record, index)))
                {
                    string keys = string.Join(' ', record.Fields.Select(field =>
                        $"{field.Type}:{BinaryPrimitives.ReadUInt32LittleEndian(field.Header.AsSpan(16))}"));
                    Console.WriteLine($"  [{index + 1,2}] id={id(record),5} {keys}");
                }
            }
        }

        uint word88 = document.Envelope.RawWord88;
        var keyRanges = groups
            .SelectMany(group => group.Records.SelectMany(record => record.Fields.Select(field =>
                (Owner: group.Label, field.Type,
                 Key: BinaryPrimitives.ReadUInt32LittleEndian(field.Header.AsSpan(16))))))
            .Where(item => item.Key != 0)
            .GroupBy(item => (item.Owner, item.Type))
            .Select(group => new
            {
                group.Key.Owner,
                group.Key.Type,
                Count = group.Count(),
                Distinct = group.Select(item => item.Key).Distinct().Count(),
                Minimum = group.Min(item => item.Key),
                Maximum = group.Max(item => item.Key),
            })
            .OrderBy(range => Math.Abs((long)range.Maximum - word88))
            .ThenBy(range => range.Owner)
            .ThenBy(range => range.Type)
            .ToArray();

        Console.WriteLine($"\nchild-key ranges nearest envelope +88 ({word88:N0}):");
        foreach (var range in keyRanges.Take(12))
            Console.WriteLine($"  {range.Owner,-9} mhoh {range.Type,-3} count={range.Count,7:N0} " +
                              $"distinct={range.Distinct,7:N0} min={range.Minimum,7:N0} max={range.Maximum,7:N0} " +
                              $"delta={(long)range.Maximum - word88,7:+#;-#;0}");
    }

    public static void Memberships(ItlDocument document, int trackId)
    {
        ItlRecord? track = document.FindTrack(trackId);
        if (track is null)
        {
            Console.WriteLine($"track {trackId} is not present");
            return;
        }

        Console.WriteLine($"track {trackId} appears in:");
        foreach (ItlRecord playlist in document.Playlists.Where(p => p.Entries.Any(e => e.TrackId == trackId)))
        {
            ItlEntry[] entries = [.. playlist.Entries.Where(e => e.TrackId == trackId)];
            Console.WriteLine($"  id={ItlDocument.PlaylistRecordIdOf(playlist),5} " +
                              $"pid={BinaryPrimitives.ReadUInt64LittleEndian(playlist.Header.AsSpan(ItlDocument.PlaylistPersistentIdOffset)):X16} " +
                              $"entries={string.Join(',', entries.Select(e => e.EntryId))} " +
                              $"name=\"{ItlDocument.PlaylistNameOf(playlist)}\"");
        }
    }

    /// <summary>Prints the varying playlist-header flag bytes alongside manual/smart classification.</summary>
    public static void PlaylistHeaders(ItlDocument document)
    {
        int[] offsets = [22, 24, 28, 29, 30, 32, 33, 34, 538, 568, 569, 628, 629, 630, 1840, 1847, 3189, 3217, 3467];
        Console.WriteLine(" id   entries smart  " + string.Join(' ', offsets.Select(offset => $"+{offset}")) + "  name");
        foreach (ItlRecord playlist in document.Playlists)
        {
            bool smart = playlist.Field((int)ItlDataType.SmartInfo) is not null &&
                         playlist.Field((int)ItlDataType.SmartCriteria) is not null;
            string values = string.Join(' ', offsets.Select(offset => playlist.Header[offset].ToString("X2")));
            Console.WriteLine($"{ItlDocument.PlaylistRecordIdOf(playlist),4} {playlist.Entries.Count(),9} " +
                              $"{(smart ? "yes" : "no "),5}  {values}  {ItlDocument.PlaylistNameOf(playlist)}");
        }
    }

    /// <summary>Finds identifier-like fields: words that are unique across every record of a kind.</summary>
    public static void Ids(ItlLibrary library, string signature)
    {
        byte[][] headers = HeadersOf(library, signature);
        if (headers.Length == 0)
        {
            Console.WriteLine($"{signature}: no records");
            return;
        }
        int length = headers.Min(h => h.Length);
        Console.WriteLine($"{signature}: {headers.Length:N0} records, header {length} bytes\n");

        foreach (int width in (int[])[4, 8])
        {
            for (int offset = 12; offset + width <= length; offset += 4)
            {
                object[] values = [.. headers.Select(h => width == 4
                    ? BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(offset))
                    : (object)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(offset)))];

                if (values.Distinct().Count() != values.Length)
                    continue;

                if (width == 4)
                {
                    uint[] u = [.. values.Cast<uint>()];
                    Console.WriteLine($"  +{offset,-4} u32 unique, min {u.Min():N0} max {u.Max():N0}" +
                                      $"{(u.Max() - u.Min() + 1 == u.Length ? "  (dense: a plain counter)" : "")}");
                }
                else
                {
                    Console.WriteLine($"  +{offset,-4} u64 unique (likely a persistent id)");
                }
            }
        }
    }

    /// <summary>
    /// Scans every word of the track header for foreign keys: values that resolve into the id
    /// field (+16) of the album or artist records.
    /// </summary>
    public static void ForeignKeys(ItlLibrary library)
    {
        var albumIds = HeadersOf(library, "miah").Select(h => BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(16))).ToHashSet();
        var artistIds = HeadersOf(library, "miih").Select(h => BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(16))).ToHashSet();

        ItlTrack[] tracks = [.. library.Tracks];
        if (tracks.Length == 0)
        {
            Console.WriteLine($"{albumIds.Count:N0} album ids, {artistIds.Count:N0} artist ids, 0 tracks");
            return;
        }
        int length = tracks.Min(t => t.Header.Length);

        Console.WriteLine($"{albumIds.Count:N0} album ids, {artistIds.Count:N0} artist ids, {tracks.Length:N0} tracks\n");

        for (int offset = 12; offset + 4 <= length; offset += 4)
        {
            uint[] values = [.. tracks.Select(t => BinaryPrimitives.ReadUInt32LittleEndian(t.Header.AsSpan(offset)))];
            uint[] nonZero = [.. values.Where(v => v != 0)];
            if (nonZero.Length < tracks.Length / 2)
                continue;

            double albumHit = (double)nonZero.Count(albumIds.Contains) / nonZero.Length;
            double artistHit = (double)nonZero.Count(artistIds.Contains) / nonZero.Length;

            int distinct = nonZero.Distinct().Count();
            bool albumCandidate = albumHit >= 0.98 && distinct >= albumIds.Count / 2;
            bool artistCandidate = artistHit >= 0.98 && distinct >= artistIds.Count / 2;
            if (albumCandidate || artistCandidate)
            {
                string what = albumCandidate ? "album" : "artist";
                double hit = albumCandidate ? albumHit : artistHit;
                Console.WriteLine($"  +{offset,-4} u32 -> {what} id  ({hit:P1} of {nonZero.Length:N0} non-zero values resolve, " +
                                  $"{distinct:N0} distinct)");
            }
        }
    }
}
