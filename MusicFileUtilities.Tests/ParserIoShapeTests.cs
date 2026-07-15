using System.Text;
using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests;

public class ParserIoShapeTests
{
    [Fact]
    public void FlacDoesNotSeekAcrossFinalPadding()
    {
        using var bytes = new MemoryStream();
        bytes.Write("fLaC"u8);
        FLACFile.WriteMetaBlockHeader(bytes, 0, 34, isLast: false);
        bytes.Write(new byte[34]);
        FLACFile.WriteMetaBlockHeader(bytes, 1, 8192, isLast: true);
        bytes.Write(new byte[8192]);

        using var stream = new CountingMemoryStream(bytes.ToArray());
        _ = new FLACFile(stream, "test.flac");

        Assert.Equal(0, stream.SeekCount);
        Assert.True(stream.Position < stream.Length);
    }

    [Fact]
    public void Mp4DoesNotSeekAfterFullyConsumedAtoms()
    {
        // moov(size 20) -> unknown child(size 12, four payload bytes). Both atoms finish exactly
        // at their boundary, so neither the container nor root should reset the read buffer.
        byte[] file = [
            0, 0, 0, 20, (byte)'m', (byte)'o', (byte)'o', (byte)'v',
            0, 0, 0, 12, (byte)'t', (byte)'e', (byte)'s', (byte)'t',
            1, 2, 3, 4,
        ];
        using var stream = new CountingMemoryStream(file);

        var root = new RootAtom(stream, "test.m4a");

        Assert.Equal(0, stream.SeekCount);
        Assert.Single(root.Children);
    }

    [Fact]
    public void ReadOnlyMp4DoesNotRetainUnknownPayload()
    {
        byte[] file = [
            0, 0, 0, 20, (byte)'m', (byte)'o', (byte)'o', (byte)'v',
            0, 0, 0, 12, (byte)'t', (byte)'e', (byte)'s', (byte)'t',
            1, 2, 3, 4,
        ];
        using var stream = new CountingMemoryStream(file);

        var root = new RootAtom(stream, "test.m4a", preserveUnknownData: false);
        var moov = Assert.IsType<ContainerAtom>(Assert.Single(root.Children));

        Assert.IsType<DiscardedAtom>(Assert.Single(moov.Children));
        Assert.Equal(0, stream.SeekCount);
    }

    [Theory]
    [InlineData("sample_aac.m4a")]
    [InlineData("sample_alac.m4a")]
    public void ReadOnlyMp4ProjectsSameMetadataAndCodec(string fixture)
    {
        string path = MediaFixtures.Path_(fixture);
        var editable = (MP4File)MediaFile.GetFile(path);
        var readOnly = (MP4File)MediaFile.GetFile(path, readOnly: true);

        Assert.Equal(
            editable.GetKnownMetadata().OrderBy(field => field.Key).ThenBy(field => field.Value),
            readOnly.GetKnownMetadata().OrderBy(field => field.Key).ThenBy(field => field.Value));
        Assert.Equal(editable.CodecName, readOnly.CodecName);
        Assert.Equal(editable.Samplerate, readOnly.Samplerate);
        Assert.Equal(editable.Channels, readOnly.Channels);
        Assert.Equal(editable.BitsPerSample, readOnly.BitsPerSample);
        Assert.Equal(
            editable.GetImageMetadata().Select(image => (image.ImageType, image.Size)),
            readOnly.GetImageMetadata().Select(image => (image.ImageType, image.Size)));
        Assert.Throws<InvalidOperationException>(() => readOnly.Save());
    }

    [Fact]
    public void Id3ReaderJumpsOverDeclaredPadding()
    {
        const int tagSize = 8192;
        byte[] file = new byte[10 + tagSize + 4];
        Encoding.ASCII.GetBytes("ID3").CopyTo(file, 0);
        file[3] = 3;
        WriteSyncSafe(file, 6, tagSize);
        Encoding.ASCII.GetBytes("TIT2").CopyTo(file, 10);
        file[17] = 2; // big-endian frame payload length
        file[20] = 0; // ISO-8859-1 text encoding
        file[21] = (byte)'X';

        using var stream = new CountingMemoryStream(file);
        var tag = new ReadableId3Tag();
        tag.Read(stream);

        Assert.Equal(10 + tagSize, stream.Position);
        Assert.Equal(1, stream.SeekCount);
        Assert.Equal("X", tag.Title);
    }

    [Fact]
    public void ApeEndOnlyReaderUsesFooterWithoutHeaderProbe()
    {
        var source = new APETag();
        source.SetField(TagFields.Title, "Tail Tag");
        byte[] tagBytes = source.ToByteArray();
        byte[] file = new byte[128 + tagBytes.Length];
        tagBytes.CopyTo(file, 128);
        using var stream = new CountingMemoryStream(file);
        var parsed = new APETag();

        Assert.True(parsed.ReadTag(stream, onlyAtEnd: true));

        Assert.Equal(2, stream.SeekCount); // footer, then item data; no front/header probes
        Assert.Equal(128, parsed.AudioEndOffset);
        Assert.Equal("Tail Tag", parsed.Title);
    }

    private static void WriteSyncSafe(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)((value >> 21) & 0x7f);
        bytes[offset + 1] = (byte)((value >> 14) & 0x7f);
        bytes[offset + 2] = (byte)((value >> 7) & 0x7f);
        bytes[offset + 3] = (byte)(value & 0x7f);
    }

    private sealed class ReadableId3Tag : ID3v2Tag
    {
        public void Read(Stream stream) => ReadTag(stream);
    }

    private sealed class CountingMemoryStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int SeekCount { get; private set; }

        public override long Seek(long offset, SeekOrigin loc)
        {
            SeekCount++;
            return base.Seek(offset, loc);
        }
    }
}
