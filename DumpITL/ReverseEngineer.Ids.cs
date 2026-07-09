using System.Buffers.Binary;

namespace iTunes.Binary;

public static partial class ReverseEngineer
{
    /// <summary>Finds identifier-like fields: words that are unique across every record of a kind.</summary>
    public static void Ids(ItlLibrary library, string signature)
    {
        byte[][] headers = HeadersOf(library, signature);
        int length = headers.Min(h => h.Length);
        Console.WriteLine($"{signature}: {headers.Length:N0} records, header {length} bytes\n");

        foreach (int width in (int[])[4, 8])
        {
            for (int offset = 12; offset + width <= length; offset++)
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
        int length = tracks.Min(t => t.Header.Length);

        Console.WriteLine($"{albumIds.Count:N0} album ids, {artistIds.Count:N0} artist ids, {tracks.Length:N0} tracks\n");

        for (int offset = 12; offset + 4 <= length; offset++)
        {
            uint[] values = [.. tracks.Select(t => BinaryPrimitives.ReadUInt32LittleEndian(t.Header.AsSpan(offset)))];
            uint[] nonZero = [.. values.Where(v => v != 0)];
            if (nonZero.Length < tracks.Length / 2)
                continue;

            double albumHit = (double)nonZero.Count(albumIds.Contains) / nonZero.Length;
            double artistHit = (double)nonZero.Count(artistIds.Contains) / nonZero.Length;

            if (albumHit >= 0.98 || artistHit >= 0.98)
            {
                string what = albumHit >= 0.98 ? "album" : "artist";
                double hit = Math.Max(albumHit, artistHit);
                Console.WriteLine($"  +{offset,-4} u32 -> {what} id  ({hit:P1} of {nonZero.Length:N0} non-zero values resolve, " +
                                  $"{values.Distinct().Count():N0} distinct)");
            }
        }
    }
}
