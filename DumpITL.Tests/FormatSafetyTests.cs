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
}
