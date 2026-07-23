using MusicFileUtilities;
using System.Diagnostics;
using Xunit;

namespace MusicFileUtilities.Tests;

public sealed class MatroskaFormatTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwC" +
        "AAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public void RealMatroskaFixtureProjectsCodecTagsChaptersAndAttachment()
    {
        MatroskaFile file = Assert.IsType<MatroskaFile>(
            MediaFile.GetFile(
                MediaFixtures.Path_("sample.mka"),
                readArtwork: true));
        IMetadataProvider tag = Assert.Single(file.Tags);
        Dictionary<TagFields, string> known = Known(tag);

        Assert.Equal("matroska", file.DocType);
        Assert.Equal("FLAC", file.CodecName);
        Assert.Equal(CodecType.Lossless, file.CodecType);
        Assert.Equal(44100u, file.Samplerate);
        Assert.Equal(2u, file.Channels);
        Assert.Equal(16u, file.BitsPerSample);
        Assert.True(file.DurationInFrames > 0);
        Assert.Equal("Matroska Tags", tag.TagType);
        Assert.Equal("TestTitle", tag.Title);
        Assert.Equal("TestArtist", tag.Artist);
        Assert.Equal("TestAlbumArtist", tag.AlbumArtist);
        Assert.Equal("TestAlbum", tag.Album);
        Assert.Equal(3, tag.TrackNumber);
        Assert.Equal(12, tag.TrackTotal);
        Assert.Equal(1, tag.DiscNumber);
        Assert.Equal(2, tag.DiscTotal);
        Assert.Equal("2021", tag.ReleaseDate);
        Assert.Equal("Rock", known[TagFields.Genre]);
        Assert.Contains(
            file.GetUserStrings(),
            item => item.Key == "CUSTOM_FIELD" &&
                    item.Value == "CustomValue");

        Assert.Collection(
            file.Chapters,
            chapter =>
            {
                Assert.Equal(0UL, chapter.StartNanoseconds);
                Assert.Equal(150_000_000UL, chapter.EndNanoseconds);
                Assert.Equal("Opening", chapter.Title);
            },
            chapter =>
            {
                Assert.Equal(
                    150_000_000UL, chapter.StartNanoseconds);
                Assert.Equal(
                    300_000_000UL, chapter.EndNanoseconds);
                Assert.Equal("Closing", chapter.Title);
            });
        IMetadataImage cover =
            Assert.Single(tag.GetImageMetadata());
        Assert.Equal("image/jpeg", cover.ImageType);
        Assert.Equal("FrontCover", cover.Category);
        Assert.Equal(16, cover.Width);
        Assert.Equal(16, cover.Height);
        Assert.NotEmpty(cover.Data);
    }

    [Fact]
    public void RealWebmFixtureProjectsOpusTagsAndChapters()
    {
        MatroskaFile file = Assert.IsType<MatroskaFile>(
            MediaFile.GetFile(
                MediaFixtures.Path_("sample.webm"),
                readArtwork: true));

        Assert.Equal("webm", file.DocType);
        Assert.Equal("Opus", file.CodecName);
        Assert.Equal(CodecType.Lossy, file.CodecType);
        Assert.Equal(48000u, file.Samplerate);
        Assert.Equal(2u, file.Channels);
        Assert.Equal("TestTitle", file.Title);
        Assert.Equal(2, file.Chapters.Count);
        Assert.Empty(file.GetImageMetadata());
        Assert.Throws<NotSupportedException>(
            () => file.SetFrontCover(Png, "image/png"));
    }

    [Fact]
    public void MetadataArtworkChaptersAndStagedSavePreserveClusters()
    {
        using var source = MediaFixtures.Copy("sample.mka");
        string output = Path.Combine(
            Path.GetTempPath(),
            $"matroska_{Guid.NewGuid():N}.mka");
        byte[] original = File.ReadAllBytes(source.Path);
        (long Offset, byte[] Data)[] clusters =
            ReadClusters(source.Path);
        try
        {
            MatroskaFile file = Assert.IsType<MatroskaFile>(
                MediaFile.GetFile(
                    source.Path,
                    readOnly: false,
                    readArtwork: true));
            file.SetField(TagFields.Title, "Edited title");
            file.SetFieldValues(
                TagFields.Artist, ["First artist", "Second artist"]);
            file.SetField(TagFields.TotalTracks, "18");
            file.SetUserString("X-CUSTOM", "Preserved value");
            file.SetImages(
            [
                new(
                    ID3v2Util.APICType.FrontCover,
                    "image/png",
                    "Front",
                    Png),
                new(
                    ID3v2Util.APICType.BackCover,
                    "image/png",
                    "Back",
                    Png),
            ]);
            file.SetChapters(
            [
                new(0, 100_000_000, "One", "en"),
                new(100_000_000, 300_000_000, "Two", "en-US"),
            ]);
            file.SaveTags(output);

            Assert.Equal(original, File.ReadAllBytes(source.Path));
            AssertClustersEqual(clusters, ReadClusters(output));

            MatroskaFile saved = Assert.IsType<MatroskaFile>(
                MediaFile.GetFile(output, readArtwork: true));
            Assert.Equal("Edited title", saved.Title);
            Assert.Equal(
                ["First artist", "Second artist"],
                saved.GetKnownMetadata()
                    .Where(item => item.Key == TagFields.Artist)
                    .Select(item => item.Value));
            Assert.Equal(18, saved.TrackTotal);
            Assert.Contains(
                saved.GetUserStrings(),
                item => item.Key == "X-CUSTOM" &&
                        item.Value == "Preserved value");
            Assert.Equal(
                ["One", "Two"],
                saved.Chapters.Select(chapter => chapter.Title));
            Assert.Equal(
                ["FrontCover", "BackCover"],
                saved.GetImageMetadata()
                    .Select(image => image.Category));
            DecodeAudioWithFfmpeg(output);
            string probe = ProbeWithFfprobe(output);
            Assert.Contains("Edited title", probe);
            Assert.Contains("One", probe);
            Assert.Contains("Two", probe);

            saved.SetField(TagFields.Title, "Repeated title");
            saved.SaveTags();
            MatroskaFile repeated = Assert.IsType<MatroskaFile>(
                MediaFile.GetFile(output, readArtwork: true));
            Assert.Equal("Repeated title", repeated.Title);
            Assert.Equal(2, repeated.Chapters.Count);
            Assert.Equal(2, repeated.GetImageMetadata().Count());
            AssertClustersEqual(clusters, ReadClusters(output));
        }
        finally
        {
            try { File.Delete(output); } catch { }
        }
    }

    [Fact]
    public void MetadataOnlyEditPreservesDeferredAttachments()
    {
        using var media = MediaFixtures.Copy("sample.mka");
        MatroskaFile file = Assert.IsType<MatroskaFile>(
            MediaFile.GetFile(
                media.Path,
                readOnly: false,
                readArtwork: false));
        Assert.Empty(file.GetImageMetadata());
        file.SetField(TagFields.Album, "Deferred artwork");
        file.SaveTags();

        MatroskaFile reloaded = Assert.IsType<MatroskaFile>(
            MediaFile.GetFile(media.Path, readArtwork: true));
        Assert.Equal("Deferred artwork", reloaded.Album);
        Assert.Single(reloaded.GetImageMetadata());
    }

    [Fact]
    public void WebmTagAndChapterWritesRemainDecodableAndPreserveClusters()
    {
        using var media = MediaFixtures.Copy("sample.webm");
        (long Offset, byte[] Data)[] clusters =
            ReadClusters(media.Path);
        MatroskaFile file = Assert.IsType<MatroskaFile>(
            MediaFile.GetFile(media.Path, readOnly: false));
        file.SetField(TagFields.Title, "Edited WebM title");
        file.SetUserString("WEBM_NOTE", "native tags");
        file.SetChapters(
        [
            new(0, 125_000_000, "First"),
            new(125_000_000, 300_000_000, "Second"),
        ]);
        file.SaveTags();

        MatroskaFile reloaded = Assert.IsType<MatroskaFile>(
            MediaFile.GetFile(media.Path));
        Assert.Equal("Edited WebM title", reloaded.Title);
        Assert.Equal(
            ["First", "Second"],
            reloaded.Chapters.Select(chapter => chapter.Title));
        Assert.Contains(
            reloaded.GetUserStrings(),
            item => item.Key == "WEBM_NOTE" &&
                    item.Value == "native tags");
        AssertClustersEqual(clusters, ReadClusters(media.Path));
        DecodeAudioWithFfmpeg(media.Path);
        string probe = ProbeWithFfprobe(media.Path);
        Assert.Contains("Edited WebM title", probe);
        Assert.Contains("First", probe);
        Assert.Contains("Second", probe);
    }

    [Theory]
    [InlineData("sample.mka", ".mkv", "matroska")]
    [InlineData("sample.webm", ".weba", "webm")]
    public void ContainerAliasesDispatchToNativeHandler(
        string fixture,
        string extension,
        string docType)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"matroska_alias_{Guid.NewGuid():N}{extension}");
        File.Copy(MediaFixtures.Path_(fixture), path);
        try
        {
            MatroskaFile file = Assert.IsType<MatroskaFile>(
                MediaFile.GetFile(path));
            Assert.Equal(docType, file.DocType);
            Assert.Equal("TestTitle", file.Title);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Theory]
    [InlineData(".mka", true, true)]
    [InlineData(".mkv", false, true)]
    [InlineData(".weba", true, false)]
    [InlineData(".webm", false, false)]
    public void RegistryReportsMatroskaCapabilities(
        string extension,
        bool indexed,
        bool artwork)
    {
        IMediaFormatRegistry registry = MediaFormatRegistry.Default;
        Assert.True(registry.SupportsExtension(
            extension,
            MediaFormatCapabilities.ReadMetadata |
            MediaFormatCapabilities.WriteMetadata |
            MediaFormatCapabilities.TranscodeSource |
            MediaFormatCapabilities.Remux));
        Assert.Equal(indexed, registry.SupportsExtension(
            extension, MediaFormatCapabilities.LibraryIndex));
        Assert.Equal(artwork, registry.SupportsExtension(
            extension,
            MediaFormatCapabilities.ReadArtwork |
            MediaFormatCapabilities.WriteArtwork));
    }

    [Fact]
    public void InvalidDocTypeAndTruncatedElementsAreRejected()
    {
        using var media = MediaFixtures.Copy("sample.mka");
        byte[] bytes = File.ReadAllBytes(media.Path);
        int docType = Find(bytes, "matroska"u8);
        Assert.True(docType >= 0);
        bytes[docType] = (byte)'x';
        File.WriteAllBytes(media.Path, bytes);
        Assert.Throws<InvalidDataException>(
            () => MediaFile.GetFile(media.Path));

        File.Copy(
            MediaFixtures.Path_("sample.mka"),
            media.Path,
            overwrite: true);
        using (FileStream stream = File.OpenWrite(media.Path))
            stream.SetLength(100);
        Assert.ThrowsAny<Exception>(
            () => MediaFile.GetFile(media.Path));
    }

    private static Dictionary<TagFields, string> Known(
        IMetadataProvider tag)
    {
        var result = new Dictionary<TagFields, string>();
        foreach (KeyValuePair<TagFields, string> item in
                 tag.GetKnownMetadata())
            result[item.Key] = item.Value;
        return result;
    }

    private static int Find(byte[] haystack, ReadOnlySpan<byte> needle)
    {
        for (int index = 0;
             index <= haystack.Length - needle.Length;
             index++)
            if (haystack.AsSpan(index, needle.Length)
                .SequenceEqual(needle))
                return index;
        return -1;
    }

    private static void AssertClustersEqual(
        (long Offset, byte[] Data)[] expected,
        (long Offset, byte[] Data)[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Offset, actual[index].Offset);
            Assert.Equal(expected[index].Data, actual[index].Data);
        }
    }

    private static void DecodeAudioWithFfmpeg(string path)
    {
        string executable = new[]
            {
                @"C:\ffmpeg\nonfree\ffmpeg.exe",
                @"C:\ffmpeg\ffmpeg.exe",
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                "ffmpeg",
            }
            .First(candidate =>
                candidate == "ffmpeg" || File.Exists(candidate));
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-v");
        start.ArgumentList.Add("error");
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add(path);
        start.ArgumentList.Add("-map");
        start.ArgumentList.Add("0:a:0");
        start.ArgumentList.Add("-f");
        start.ArgumentList.Add("null");
        start.ArgumentList.Add(
            OperatingSystem.IsWindows() ? "NUL" : "/dev/null");
        using Process process = Process.Start(start)!;
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"ffmpeg failed to decode edited Matroska/WebM: {error}");
    }

    private static string ProbeWithFfprobe(string path)
    {
        string ffmpeg = new[]
            {
                @"C:\ffmpeg\nonfree\ffmpeg.exe",
                @"C:\ffmpeg\ffmpeg.exe",
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                "ffmpeg",
            }
            .First(candidate =>
                candidate == "ffmpeg" || File.Exists(candidate));
        string executable = ffmpeg == "ffmpeg"
            ? "ffprobe"
            : Path.Combine(
                Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-v");
        start.ArgumentList.Add("error");
        start.ArgumentList.Add("-show_entries");
        start.ArgumentList.Add(
            "format_tags=title:chapter_tags=title");
        start.ArgumentList.Add("-of");
        start.ArgumentList.Add("default=noprint_wrappers=1");
        start.ArgumentList.Add(path);
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"ffprobe rejected edited Matroska/WebM: {error}");
        return output;
    }

    private static (long Offset, byte[] Data)[] ReadClusters(
        string path)
    {
        byte[] data = File.ReadAllBytes(path);
        int offset = 0;
        Element ebml = ReadElement(data, ref offset, data.Length);
        Assert.Equal(0x1A45DFA3UL, ebml.Id);
        offset = ebml.End;
        Element segment = ReadElement(data, ref offset, data.Length);
        while (segment.Id != 0x18538067)
        {
            offset = segment.End;
            segment = ReadElement(data, ref offset, data.Length);
        }

        var clusters = new List<(long Offset, byte[] Data)>();
        offset = segment.DataOffset;
        while (offset < segment.End)
        {
            Element child = ReadElement(data, ref offset, segment.End);
            if (child.Id == 0x1F43B675)
                clusters.Add((
                    child.Offset,
                    data.AsSpan(
                        child.Offset, child.End - child.Offset)
                        .ToArray()));
            offset = child.End;
        }
        return clusters.ToArray();
    }

    private static Element ReadElement(
        byte[] data,
        ref int offset,
        int limit)
    {
        int start = offset;
        (ulong id, _) = ReadVint(data, ref offset, false);
        (ulong size, bool unknown) =
            ReadVint(data, ref offset, true);
        int end = unknown
            ? limit
            : checked(offset + (int)size);
        if (end > limit)
            throw new InvalidDataException();
        return new(start, id, offset, end);
    }

    private static (ulong Value, bool Unknown) ReadVint(
        byte[] data,
        ref int offset,
        bool removeMarker)
    {
        int first = data[offset++];
        int marker = 0x80;
        int width = 1;
        while ((first & marker) == 0)
        {
            marker >>= 1;
            width++;
        }
        ulong value = removeMarker
            ? checked((ulong)(first & (marker - 1)))
            : checked((ulong)first);
        for (int index = 1; index < width; index++)
            value = (value << 8) | data[offset++];
        ulong maximum = width == 8
            ? 0x00FFFFFFFFFFFFFFUL
            : (1UL << (width * 7)) - 1;
        return (value, removeMarker && value == maximum);
    }

    private sealed record Element(
        int Offset,
        ulong Id,
        int DataOffset,
        int End);
}
