using System.Buffers.Binary;
using System.Text;
using iTunes.Binary;
using Xunit;

namespace DumpITL.Tests;

public sealed class FormatSafetyTests
{
    [Fact]
    public void ChunkReadRejectsOverflowingHeaderLength()
    {
        byte[] data = new byte[16];
        "msdh"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), int.MaxValue);
        Assert.Throws<InvalidDataException>(() => ItlChunk.Read(data, 0));
    }

    [Fact]
    public void ChunkWalkRejectsTruncatedItem()
    {
        byte[] data = new byte[16];
        "mhit"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 12);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 20);
        Assert.Throws<InvalidDataException>(() => ItlChunk.Walk(data, 0, data.Length).ToArray());
    }

    [Fact]
    public void FixedMprhListUsesDeclaredCountAndFixedSize()
    {
        byte[] body = new byte[12 + 2 * ItlTraversal.MprhLength];
        "mlrh"u8.CopyTo(body);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), 12);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), 2);
        for (int i = 0; i < 2; i++)
        {
            int offset = 12 + i * ItlTraversal.MprhLength;
            "mprh"u8.CopyTo(body.AsSpan(offset));
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(offset + 4), ItlTraversal.MprhLength);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset + 8), 0xDEAD0000u + (uint)i);
        }

        ItlChunk list = ItlChunk.Read(body, 0);
        Assert.False(ItlTraversal.TryWalkChunkItems(body, list, body.Length, out _, out _));
        IReadOnlyList<ItlFixedItem> items = ItlTraversal.WalkFixedItems(body, list, body.Length);
        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Equal(ItlTraversal.MprhLength, item.Length));

        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), 3);
        Assert.Throws<InvalidDataException>(() =>
            ItlTraversal.WalkFixedItems(body, ItlChunk.Read(body, 0), body.Length));
    }

    [Fact]
    public void StshUsesCountAtTwelveAndOnlyOpaqueTypes900And901()
    {
        byte[] valid = Stsh(2, 901, 900);
        ItlChunk header = ItlChunk.Read(valid, 0);
        IReadOnlyList<ItlChunk> fields = ItlTraversal.WalkStshDataObjects(valid, header, valid.Length);
        Assert.Equal([901, 900], fields.Select(field => field.Type));

        byte[] wrongCount = (byte[])valid.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(wrongCount.AsSpan(12), 1);
        Assert.Throws<InvalidDataException>(() =>
            ItlTraversal.WalkStshDataObjects(wrongCount, ItlChunk.Read(wrongCount, 0), wrongCount.Length));

        byte[] duplicate = Stsh(2, 900, 900);
        Assert.Throws<InvalidDataException>(() =>
            ItlTraversal.WalkStshDataObjects(duplicate, ItlChunk.Read(duplicate, 0), duplicate.Length));

        byte[] unsupported = Stsh(1, 902);
        Assert.Throws<InvalidDataException>(() =>
            ItlTraversal.WalkStshDataObjects(unsupported, ItlChunk.Read(unsupported, 0), unsupported.Length));
    }

    [Fact]
    public void DocumentAndWriterRejectMalformedStshWithoutGuessing()
    {
        byte[] body = AppendSection(SyntheticLibrary.CreateBody(), 23, Stsh(1, 901, 900));
        ItlEnvelope envelope = SyntheticLibrary.CreateEnvelope(body);
        ItlDocument document = ItlDocument.Parse(envelope);

        ItlValidationIssue issue = Assert.Single(document.Validate(), item => item.Code == "stsh.layout");
        Assert.Equal(ItlValidationSeverity.Error, issue.Severity);
        Assert.Throws<InvalidDataException>(() => ItlWriter.Build(envelope, body));
    }

    [Fact]
    public void Type14MlphIsASeparateCountedPlaylistPartition()
    {
        byte[] valid = Mlph(1, "miph");
        IReadOnlyList<ItlChunk> records =
            ItlTraversal.WalkMlphRecords(valid, ItlChunk.Read(valid, 0), valid.Length);
        Assert.Single(records);

        byte[] body = AppendSection(SyntheticLibrary.CreateBody(), 14, Mlph(0, "miph"));
        ItlEnvelope envelope = SyntheticLibrary.CreateEnvelope(body);
        ItlDocument document = ItlDocument.Parse(envelope);
        Assert.Contains(document.Validate(), item =>
            item.Code == "mlph14.layout" && item.Severity == ItlValidationSeverity.Error);
        Assert.Throws<InvalidDataException>(() => ItlWriter.Build(envelope, body));

        byte[] wrongChild = Mlph(1, "mith");
        Assert.Throws<InvalidDataException>(() =>
            ItlTraversal.WalkMlphRecords(wrongChild, ItlChunk.Read(wrongChild, 0), wrongChild.Length));
    }

    [Fact]
    public void Type21MlshContainsCountedPodcastStationSettings()
    {
        byte[] valid = Mlsh(1, 1, (int)ItlDataType.PodcastSettingsPlist);
        IReadOnlyList<ItlChunk> stations =
            ItlTraversal.WalkPodcastStations(valid, ItlChunk.Read(valid, 0), valid.Length);
        Assert.Single(stations);

        byte[] body = AppendSection(SyntheticLibrary.CreateBody(), 21, Mlsh(1, 0, 800));
        ItlEnvelope envelope = SyntheticLibrary.CreateEnvelope(body);
        Assert.Contains(ItlDocument.Parse(envelope).Validate(), item =>
            item.Code == "mlsh.layout" && item.Severity == ItlValidationSeverity.Error);
        Assert.Throws<InvalidDataException>(() => ItlWriter.Build(envelope, body));

        byte[] wrongType = Mlsh(1, 1, 801);
        Assert.Throws<InvalidDataException>(() =>
            ItlTraversal.WalkPodcastStations(wrongType, ItlChunk.Read(wrongType, 0), wrongType.Length));
    }

    [Fact]
    public void MprhProbeHandlesAbsentSection()
    {
        ItlLibrary library = ItlLibrary.Parse(ItlEnvelope.Parse(SyntheticLibrary.CreateFile()));
        TextWriter original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            ReverseEngineer.Mprh(library);
        }
        finally { Console.SetOut(original); }

        Assert.Contains("is absent", output.ToString());
    }

    [Fact]
    public void StringFieldsRoundTripAllEncodingsAndEmptyValues()
    {
        AssertField("", 1);
        AssertField("café", 3);
        AssertField("東京", 2);
        AssertField("Grüße 東京", 1);

        static void AssertField(string value, int preferred)
        {
            ItlField field = ItlField.CreateString(2, value);
            field.SetText(value, preferred);
            byte[] bytes = new byte[field.Length];
            field.WriteTo(bytes);
            ItlDataObject parsed = ItlDataObject.Parse(bytes, ItlChunk.Read(bytes, 0));
            Assert.True(parsed.IsString);
            Assert.Equal(value, parsed.Text);
            Assert.Equal(preferred, parsed.Encoding);
        }
    }

    [Fact]
    public void ComparerReportsNoDifferencesForSameFileModel()
    {
        byte[] file = SyntheticLibrary.CreateFile();
        string directory = Path.Combine(Path.GetTempPath(), "dumpitl_compare_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "same.itl");
        try
        {
            File.WriteAllBytes(path, file);
            using var output = new StringWriter();
            ItlComparer.Compare(path, path, output);
            Assert.Contains("differences: 0", output.ToString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ComparerKeepsRecordAlignmentAfterAStringChangesSize()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dumpitl_compare_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string before = Path.Combine(directory, "before.itl");
        string after = Path.Combine(directory, "after.itl");
        try
        {
            File.WriteAllBytes(before, SyntheticLibrary.CreateFile());
            ItlDocument document = ItlDocument.Load(before);
            document.Tracks.Single().SetString(ItlDataType.Title, "a deliberately longer title");
            document.Save(after);

            using var output = new StringWriter();
            ItlComparer.Compare(before, after, output);
            string report = output.ToString();
            Assert.Contains("record mith:1#0 child mhoh:2#0 added", report);
            Assert.DoesNotContain("record mith:1#0 removed", report);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ComparerReportsValuesForSmallChangedByteRanges()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dumpitl_compare_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string before = Path.Combine(directory, "before.itl");
        string after = Path.Combine(directory, "after.itl");
        try
        {
            File.WriteAllBytes(before, SyntheticLibrary.CreateFile());
            ItlDocument document = ItlDocument.Load(before);
            document.Tracks.Single().Header[703] = 0x80;
            document.Save(after);

            using var output = new StringWriter();
            ItlComparer.Compare(before, after, output, "mith:1");
            Assert.Contains("header bytes +703..+703 changed (00 -> 80)", output.ToString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void LovedAccessorsUseBitOneAtTrackHeader703AndPreserveOtherBits()
    {
        ItlDocument document = ItlDocument.Parse(ItlEnvelope.Parse(SyntheticLibrary.CreateFile()));
        ItlRecord editable = document.Tracks.Single();
        editable.Header[703] = 0xA5;

        Assert.False(editable.GetLoved());
        editable.SetLoved(true);
        Assert.Equal(0xA7, editable.Header[703]);
        Assert.True(editable.GetLoved());
        editable.SetLoved(false);
        Assert.Equal(0xA5, editable.Header[703]);

        var parsed = new ItlTrack { Id = 1, Header = (byte[])editable.Header.Clone(), DataObjects = [] };
        Assert.False(parsed.Loved);
        parsed.Header[703] = 0xA7;
        Assert.True(parsed.Loved);
    }

    private static byte[] Stsh(int declaredCount, params int[] types)
    {
        byte[][] children = [.. types.Select(DataObject)];
        byte[] result = new byte[ItlTraversal.StshHeaderLength + children.Sum(child => child.Length)];
        "stsh"u8.CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), ItlTraversal.StshHeaderLength);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(12), declaredCount);
        int offset = ItlTraversal.StshHeaderLength;
        foreach (byte[] child in children)
        {
            child.CopyTo(result, offset);
            offset += child.Length;
        }
        return result;

        static byte[] DataObject(int type)
        {
            byte[] child = new byte[28];
            "mhoh"u8.CopyTo(child);
            BinaryPrimitives.WriteInt32LittleEndian(child.AsSpan(4), 24);
            BinaryPrimitives.WriteInt32LittleEndian(child.AsSpan(8), child.Length);
            BinaryPrimitives.WriteInt32LittleEndian(child.AsSpan(12), type);
            return child;
        }
    }

    private static byte[] AppendSection(byte[] body, int type, byte[] payload)
    {
        byte[] result = new byte[body.Length + 16 + payload.Length];
        body.CopyTo(result, 0);
        int offset = body.Length;
        "msdh"u8.CopyTo(result.AsSpan(offset));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset + 4), 16);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset + 8), 16 + payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset + 12), type);
        payload.CopyTo(result, offset + 16);
        return result;
    }

    private static byte[] Mlph(int declaredCount, params string[] signatures)
    {
        byte[] result = new byte[ItlTraversal.MlphHeaderLength + signatures.Length * 16];
        "mlph"u8.CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), ItlTraversal.MlphHeaderLength);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), declaredCount);
        for (int index = 0; index < signatures.Length; index++)
        {
            int offset = ItlTraversal.MlphHeaderLength + index * 16;
            Encoding.ASCII.GetBytes(signatures[index]).CopyTo(result, offset);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset + 4), 16);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset + 8), 16);
        }
        return result;
    }

    private static byte[] Mlsh(int stationCount, int fieldCount, int fieldType)
    {
        byte[] field = new byte[28];
        "mhoh"u8.CopyTo(field);
        BinaryPrimitives.WriteInt32LittleEndian(field.AsSpan(4), 24);
        BinaryPrimitives.WriteInt32LittleEndian(field.AsSpan(8), field.Length);
        BinaryPrimitives.WriteInt32LittleEndian(field.AsSpan(12), fieldType);

        byte[] station = new byte[ItlTraversal.MsphHeaderLength + field.Length];
        "msph"u8.CopyTo(station);
        BinaryPrimitives.WriteInt32LittleEndian(station.AsSpan(4), ItlTraversal.MsphHeaderLength);
        BinaryPrimitives.WriteInt32LittleEndian(station.AsSpan(8), station.Length);
        BinaryPrimitives.WriteInt32LittleEndian(station.AsSpan(12), fieldCount);
        field.CopyTo(station, ItlTraversal.MsphHeaderLength);

        byte[] result = new byte[ItlTraversal.MlshHeaderLength + station.Length];
        "mlsh"u8.CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), ItlTraversal.MlshHeaderLength);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), stationCount);
        station.CopyTo(result, ItlTraversal.MlshHeaderLength);
        return result;
    }
}
