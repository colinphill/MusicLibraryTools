using System.Buffers.Binary;

namespace iTunes.Binary;

/// <summary>Structure-aware comparison of two decoded iTunes libraries.</summary>
public static class ItlComparer
{
    public static void Compare(string beforePath, string afterPath, TextWriter output, string? recordFilter = null)
    {
        ItlEnvelope before = ItlEnvelope.Load(beforePath);
        ItlEnvelope after = ItlEnvelope.Load(afterPath);
        var sink = new DifferenceSink(output);

        CompareEnvelope(before.RawHeader, after.RawHeader, sink);

        List<SectionView> left = ReadSections(before.Body);
        List<SectionView> right = ReadSections(after.Body);
        var leftByKey = left.ToDictionary(s => s.Key);
        var rightByKey = right.ToDictionary(s => s.Key);

        foreach (string key in leftByKey.Keys.Union(rightByKey.Keys).Order())
        {
            if (!leftByKey.TryGetValue(key, out SectionView? a))
            {
                sink.Add($"section {key} added ({rightByKey[key].Chunk.TotalLength:N0} bytes)");
                continue;
            }
            if (!rightByKey.TryGetValue(key, out SectionView? b))
            {
                sink.Add($"section {key} removed ({a.Chunk.TotalLength:N0} bytes)");
                continue;
            }

            CompareSection(before.Body, after.Body, a, b, sink, recordFilter);
        }

        sink.Finish();
    }

    private static void CompareEnvelope(byte[] before, byte[] after, DifferenceSink sink)
    {
        int words = Math.Min(before.Length, after.Length) / 4;
        for (int offset = 0; offset < words * 4; offset += 4)
        {
            uint a = BinaryPrimitives.ReadUInt32BigEndian(before.AsSpan(offset));
            uint b = BinaryPrimitives.ReadUInt32BigEndian(after.AsSpan(offset));
            if (a != b)
                sink.Add($"envelope +{offset}: 0x{a:X8} ({a}) -> 0x{b:X8} ({b})");
        }
        if (before.Length != after.Length)
            sink.Add($"envelope length: {before.Length} -> {after.Length}");
    }

    private static void CompareSection(byte[] beforeBody, byte[] afterBody, SectionView before, SectionView after,
        DifferenceSink sink, string? recordFilter)
    {
        if (before.Chunk.TotalLength != after.Chunk.TotalLength)
            sink.Add($"section {before.Key} length: {before.Chunk.TotalLength:N0} -> {after.Chunk.TotalLength:N0}");

        if (!before.Structured || !after.Structured)
        {
            ReportByteRanges($"section {before.Key}",
                beforeBody.AsSpan(before.Chunk.Offset, before.Chunk.TotalLength),
                afterBody.AsSpan(after.Chunk.Offset, after.Chunk.TotalLength), sink);
            return;
        }

        Dictionary<string, ItlChunk> left = ReadRecords(beforeBody, before);
        Dictionary<string, ItlChunk> right = ReadRecords(afterBody, after);
        foreach (string key in left.Keys.Union(right.Keys).Order())
        {
            if (!string.IsNullOrWhiteSpace(recordFilter))
            {
                bool matches = recordFilter.Contains(':')
                    ? key.StartsWith(recordFilter + "#", StringComparison.OrdinalIgnoreCase)
                    : key.Contains(recordFilter, StringComparison.OrdinalIgnoreCase);
                if (!matches) continue;
            }
            if (!left.TryGetValue(key, out ItlChunk a))
            {
                sink.Add($"section {before.Key} record {key} added");
                continue;
            }
            if (!right.TryGetValue(key, out ItlChunk b))
            {
                sink.Add($"section {before.Key} record {key} removed");
                continue;
            }

            ReportByteRanges($"section {before.Key} record {key} header",
                beforeBody.AsSpan(a.Offset, a.HeaderLength), afterBody.AsSpan(b.Offset, b.HeaderLength), sink);
            CompareChildren(beforeBody, afterBody, before.Key, key, a, b, sink);
        }
    }

    private static void CompareChildren(byte[] beforeBody, byte[] afterBody, string sectionKey, string recordKey,
        ItlChunk before, ItlChunk after, DifferenceSink sink)
    {
        List<ItlChunk> a = [.. ItlChunk.Walk(beforeBody, before.BodyOffset, before.EndOffset)];
        List<ItlChunk> b = [.. ItlChunk.Walk(afterBody, after.BodyOffset, after.EndOffset)];
        Dictionary<string, ItlChunk> left = KeyChildren(beforeBody, a);
        Dictionary<string, ItlChunk> right = KeyChildren(afterBody, b);

        foreach (string key in left.Keys.Union(right.Keys).Order())
        {
            string label = $"section {sectionKey} record {recordKey} child {key}";
            if (!left.TryGetValue(key, out ItlChunk x)) { sink.Add(label + " added"); continue; }
            if (!right.TryGetValue(key, out ItlChunk y)) { sink.Add(label + " removed"); continue; }
            ReportByteRanges(label,
                beforeBody.AsSpan(x.Offset, x.TotalLength), afterBody.AsSpan(y.Offset, y.TotalLength), sink);
        }
    }

    private static Dictionary<string, ItlChunk> KeyChildren(byte[] body, List<ItlChunk> children)
    {
        var result = new Dictionary<string, ItlChunk>();
        var ordinals = new Dictionary<string, int>();
        foreach (ItlChunk child in children)
        {
            string baseKey = child.Signature switch
            {
                "mhoh" => $"mhoh:{child.Type}",
                "mtph" when child.HeaderLength >= 20 => $"mtph:{BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(child.Offset + 16))}",
                _ => child.Signature,
            };
            int ordinal = ordinals.GetValueOrDefault(baseKey);
            ordinals[baseKey] = ordinal + 1;
            result[$"{baseKey}#{ordinal}"] = child;
        }
        return result;
    }

    private static Dictionary<string, ItlChunk> ReadRecords(byte[] body, SectionView section)
    {
        ItlChunk list = ItlChunk.Read(body, section.Chunk.BodyOffset);
        ItlTraversal.TryWalkChunkItems(body, list, section.Chunk.EndOffset, out var records, out _);
        var result = new Dictionary<string, ItlChunk>();
        var ordinals = new Dictionary<string, int>();
        foreach (ItlChunk record in records)
        {
            string key = record.Signature switch
            {
                "mith" or "miah" or "miih" when record.HeaderLength >= 20 =>
                    $"{record.Signature}:{BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(record.Offset + 16))}",
                "miph" when record.HeaderLength >= ItlDocument.PlaylistPersistentIdOffset + 8 =>
                    $"miph:{BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(record.Offset + ItlDocument.PlaylistPersistentIdOffset)):X16}",
                _ => record.Signature,
            };
            int ordinal = ordinals.GetValueOrDefault(key);
            ordinals[key] = ordinal + 1;
            result[$"{key}#{ordinal}"] = record;
        }
        return result;
    }

    private static List<SectionView> ReadSections(byte[] body)
    {
        var result = new List<SectionView>();
        var ordinals = new Dictionary<int, int>();
        foreach (ItlChunk section in ItlChunk.Walk(body, 0, body.Length))
        {
            int ordinal = ordinals.GetValueOrDefault(section.Type);
            ordinals[section.Type] = ordinal + 1;
            bool structured = section.BodyLength >= 12;
            if (structured)
            {
                try
                {
                    ItlChunk list = ItlChunk.Read(body, section.BodyOffset);
                    structured = list.Signature is "mlth" or "mlph" or "mlah" or "mlih";
                }
                catch (InvalidDataException) { structured = false; }
            }
            result.Add(new SectionView($"{section.Type}#{ordinal}", section, structured));
        }
        return result;
    }

    private static void ReportByteRanges(string label, ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, DifferenceSink sink)
    {
        int common = Math.Min(before.Length, after.Length);
        int start = -1;
        for (int i = 0; i < common; i++)
        {
            if (before[i] != after[i])
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                ReportByteRange(label, before, after, start, i, sink);
                start = -1;
            }
        }
        if (start >= 0)
            ReportByteRange(label, before, after, start, common, sink);
        if (before.Length != after.Length)
            sink.Add($"{label} length {before.Length} -> {after.Length}");
    }

    private static void ReportByteRange(string label, ReadOnlySpan<byte> before, ReadOnlySpan<byte> after,
        int start, int endExclusive, DifferenceSink sink)
    {
        int length = endExclusive - start;
        string values = length <= 16
            ? $" ({Convert.ToHexString(before.Slice(start, length))} -> {Convert.ToHexString(after.Slice(start, length))})"
            : string.Empty;
        sink.Add($"{label} bytes +{start}..+{endExclusive - 1} changed{values}");
    }

    private sealed record SectionView(string Key, ItlChunk Chunk, bool Structured);

    private sealed class DifferenceSink(TextWriter output)
    {
        private const int Limit = 250;
        private int _count;

        public void Add(string message)
        {
            _count++;
            if (_count <= Limit)
                output.WriteLine(message);
        }

        public void Finish()
        {
            if (_count == 0) output.WriteLine("no structural or byte differences");
            else if (_count > Limit) output.WriteLine($"... {_count - Limit:N0} additional differences suppressed");
            output.WriteLine($"differences: {_count:N0}");
        }
    }
}
