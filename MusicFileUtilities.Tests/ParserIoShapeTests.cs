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
        Assert.Equal(stream.Position, stream.BytesRead);
    }

    [Fact]
    public void FlacKeepsSmallNonFinalPaddingInsideSequentialReadBuffer()
    {
        byte[] comments = new VorbisComments().ToByteArray(includeart: false);
        using var bytes = new MemoryStream();
        bytes.Write("fLaC"u8);
        FLACFile.WriteMetaBlockHeader(bytes, 0, 34, isLast: false);
        bytes.Write(new byte[34]);
        FLACFile.WriteMetaBlockHeader(bytes, 1, 8192, isLast: false);
        bytes.Write(new byte[8192]);
        FLACFile.WriteMetaBlockHeader(bytes, 4, comments.Length, isLast: true);
        bytes.Write(comments);

        byte[] file = bytes.ToArray();
        using var stream = new CountingMemoryStream(file);
        _ = new FLACFile(stream, "test.flac", readArtwork: false);

        Assert.Equal(0, stream.SeekCount);
        Assert.Equal(file.Length, stream.BytesRead);
    }

    [Fact]
    public void MetadataOnlyFlacDoesNotDecodePictureFromVorbisComment()
    {
        var comments = new VorbisComments();
        comments.Artworks.Add(new VorbisArtwork
        {
            PictureType = ID3v2Util.APICType.FrontCover,
            MimeType = "image/jpeg",
            Description = "",
            Width = 1000,
            Height = 1000,
            Depth = 24,
            Data = new byte[256 * 1024],
        });
        byte[] commentData = comments.ToByteArray(includeart: true);
        using var bytes = new MemoryStream();
        bytes.Write("fLaC"u8);
        FLACFile.WriteMetaBlockHeader(bytes, 0, 34, isLast: false);
        bytes.Write(new byte[34]);
        FLACFile.WriteMetaBlockHeader(bytes, 4, commentData.Length, isLast: true);
        bytes.Write(commentData);

        using var stream = new CountingMemoryStream(bytes.ToArray());
        var flac = new FLACFile(stream, "test.flac", readArtwork: false);

        Assert.Empty(flac.GetImageMetadata());
    }

    [Fact]
    public void ReadOnlyMetadataFlacStopsAfterVorbisComment()
    {
        var comments = new VorbisComments();
        comments.Comments.Add(KeyValuePair.Create("TITLE", "Network Test"));
        byte[] commentData = comments.ToByteArray(includeart: false);
        byte[] picture = new VorbisArtwork
        {
            PictureType = ID3v2Util.APICType.FrontCover,
            MimeType = "image/jpeg",
            Description = "",
            Width = 1000,
            Height = 1000,
            Depth = 24,
            Data = new byte[512 * 1024],
        }.ToByteArray();
        using var bytes = new MemoryStream();
        bytes.Write("fLaC"u8);
        FLACFile.WriteMetaBlockHeader(bytes, 0, 34, isLast: false);
        bytes.Write(new byte[34]);
        FLACFile.WriteMetaBlockHeader(bytes, 4, commentData.Length, isLast: false);
        bytes.Write(commentData);
        long expectedEnd = bytes.Position;
        FLACFile.WriteMetaBlockHeader(bytes, 6, picture.Length, isLast: false);
        bytes.Write(picture);
        FLACFile.WriteMetaBlockHeader(bytes, 1, 8192, isLast: true);
        bytes.Write(new byte[8192]);
        byte[] file = bytes.ToArray();

        using var stream = new CountingMemoryStream(file);
        var flac = new FLACFile(
            stream,
            "test.flac",
            readArtwork: false,
            readOnly: true,
            knownLength: file.Length);

        Assert.Equal("Network Test", flac.Title);
        Assert.Equal(expectedEnd, stream.Position);
        Assert.Equal(expectedEnd, stream.BytesRead);
        Assert.Equal(0, stream.SeekCount);
        Assert.Equal(0, stream.LengthQueryCount);
    }

    [Fact]
    public void ReadOnlyFlacCannotBeSaved()
    {
        string path = MediaFixtures.Path_("sample.flac");
        var flac = Assert.IsType<FLACFile>(MediaFile.GetFile(
            path,
            readOnly: true,
            readArtwork: false,
            knownLength: new FileInfo(path).Length));

        Assert.Throws<InvalidOperationException>(() => flac.Save());
    }

    [Fact]
    public void MetadataOnlyFlacSeeksOverLargePictureBlock()
    {
        byte[] picture = new VorbisArtwork
        {
            PictureType = ID3v2Util.APICType.FrontCover,
            MimeType = "image/jpeg",
            Description = "",
            Width = 1000,
            Height = 1000,
            Depth = 24,
            Data = new byte[512 * 1024],
        }.ToByteArray();
        using var bytes = new MemoryStream();
        bytes.Write("fLaC"u8);
        FLACFile.WriteMetaBlockHeader(bytes, 0, 34, isLast: false);
        bytes.Write(new byte[34]);
        FLACFile.WriteMetaBlockHeader(bytes, 6, picture.Length, isLast: false);
        bytes.Write(picture);
        FLACFile.WriteMetaBlockHeader(bytes, 1, 0, isLast: true);

        byte[] file = bytes.ToArray();
        using var stream = new CountingMemoryStream(file);
        var flac = new FLACFile(stream, "test.flac", readArtwork: false);

        Assert.Empty(flac.GetImageMetadata());
        Assert.Equal(1, stream.SeekCount);
        Assert.True(stream.BytesRead < file.Length);
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

        var root = new RootAtom(stream, "test.m4a", knownLength: file.Length);

        Assert.Equal(0, stream.SeekCount);
        Assert.Equal(0, stream.LengthQueryCount);
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
        tag.Read(stream, knownLength: file.Length);

        Assert.Equal(10 + tagSize, stream.Position);
        Assert.Equal(1, stream.SeekCount);
        Assert.Equal(0, stream.LengthQueryCount);
        Assert.Equal("X", tag.Title);
    }

    [Fact]
    public void MetadataOnlyId3SeeksOverPictureAndContinuesWithTextFrames()
    {
        const int pictureSize = 256 * 1024;
        const int titleSize = 2;
        int tagSize = 10 + pictureSize + 10 + titleSize;
        byte[] file = new byte[10 + tagSize];
        Encoding.ASCII.GetBytes("ID3").CopyTo(file, 0);
        file[3] = 3;
        WriteSyncSafe(file, 6, tagSize);
        Encoding.ASCII.GetBytes("APIC").CopyTo(file, 10);
        WriteBigEndian(file, 14, pictureSize);
        int titleHeader = 20 + pictureSize;
        Encoding.ASCII.GetBytes("TIT2").CopyTo(file, titleHeader);
        WriteBigEndian(file, titleHeader + 4, titleSize);
        file[titleHeader + 10] = 0;
        file[titleHeader + 11] = (byte)'X';

        using var stream = new CountingMemoryStream(file);
        var tag = new ReadableId3Tag();
        tag.Read(stream, readArtwork: false, knownLength: file.Length);

        Assert.Equal("X", tag.Title);
        Assert.Empty(tag.GetImageMetadata());
        Assert.Equal(1, stream.SeekCount);
        Assert.Equal(0, stream.LengthQueryCount);
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

        Assert.True(parsed.ReadTag(stream, onlyAtEnd: true, knownLength: file.Length));

        Assert.Equal(2, stream.SeekCount); // footer, then item data; no front/header probes
        Assert.Equal(0, stream.LengthQueryCount);
        Assert.Equal(128, parsed.AudioEndOffset);
        Assert.Equal("Tail Tag", parsed.Title);
    }

    [Fact]
    public void MetadataOnlyApeSeeksOverLargeArtworkValue()
    {
        var source = new APETag();
        source.SetField(TagFields.Title, "Tail Tag");
        source.SetFrontCover(new byte[512 * 1024], "image/jpeg");
        byte[] tagBytes = source.ToByteArray();
        byte[] file = new byte[128 + tagBytes.Length];
        tagBytes.CopyTo(file, 128);
        using var stream = new CountingMemoryStream(file);
        var parsed = new APETag();

        Assert.True(parsed.ReadTag(
            stream,
            onlyAtEnd: true,
            readArtwork: false,
            knownLength: file.Length));

        Assert.Equal("Tail Tag", parsed.Title);
        Assert.Empty(parsed.GetImageMetadata());
        Assert.Equal(3, stream.SeekCount); // footer, item start, then the large image value
        Assert.Equal(0, stream.LengthQueryCount);
    }

    [Fact]
    public void DsfParsesFormatBeforeSingleTailTagSeek()
    {
        byte[] file = new byte[110];
        Encoding.ASCII.GetBytes("DSD ").CopyTo(file, 0);
        BitConverter.GetBytes(100L).CopyTo(file, 20); // metadata pointer
        Encoding.ASCII.GetBytes("fmt ").CopyTo(file, 28);
        BitConverter.GetBytes(52UL).CopyTo(file, 32);
        BitConverter.GetBytes(1U).CopyTo(file, 40);       // format version
        BitConverter.GetBytes(0U).CopyTo(file, 44);       // format id
        BitConverter.GetBytes(2U).CopyTo(file, 48);       // channel type
        BitConverter.GetBytes(2U).CopyTo(file, 52);       // channels
        BitConverter.GetBytes(2_822_400U).CopyTo(file, 56);
        BitConverter.GetBytes(1U).CopyTo(file, 60);       // bits per sample
        BitConverter.GetBytes(2_822_400UL).CopyTo(file, 64);
        Encoding.ASCII.GetBytes("ID3").CopyTo(file, 100);
        file[103] = 3;

        using var stream = new CountingMemoryStream(file);
        var dsf = new DSFFile(stream, "test.dsf", knownLength: file.Length);

        Assert.Equal(1, stream.SeekCount);
        Assert.Equal(0, stream.LengthQueryCount);
        Assert.Equal(2_822_400u, dsf.Samplerate);
        Assert.Equal(2u, dsf.Channels);
        Assert.Equal(75u, dsf.DurationInFrames);
    }

    private static void WriteSyncSafe(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)((value >> 21) & 0x7f);
        bytes[offset + 1] = (byte)((value >> 14) & 0x7f);
        bytes[offset + 2] = (byte)((value >> 7) & 0x7f);
        bytes[offset + 3] = (byte)(value & 0x7f);
    }

    private static void WriteBigEndian(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)(value >> 24);
        bytes[offset + 1] = (byte)(value >> 16);
        bytes[offset + 2] = (byte)(value >> 8);
        bytes[offset + 3] = (byte)value;
    }

    private sealed class ReadableId3Tag : ID3v2Tag
    {
        public void Read(
            Stream stream,
            bool readArtwork = true,
            long? knownLength = null) => ReadTag(stream, readArtwork, knownLength);
    }

    private sealed class CountingMemoryStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int SeekCount { get; private set; }
        public int LengthQueryCount { get; private set; }
        public int ReadCount { get; private set; }
        public long BytesRead { get; private set; }

        public override long Length
        {
            get
            {
                LengthQueryCount++;
                return base.Length;
            }
        }

        public override long Seek(long offset, SeekOrigin loc)
        {
            SeekCount++;
            return base.Seek(offset, loc);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = base.Read(buffer, offset, count);
            ReadCount++;
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            // MemoryStream's span implementation delegates to the array overload, where this
            // stream records the operation. Counting here too would double every ReadExactly.
            return base.Read(buffer);
        }

        public override int ReadByte()
        {
            int value = base.ReadByte();
            ReadCount++;
            if (value >= 0)
                BytesRead++;
            return value;
        }
    }
}
