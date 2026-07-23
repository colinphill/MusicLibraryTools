using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests;

public sealed class Id3v1Tests
{
    [Fact]
    public void ReadsId3v10CommentAndGenreWithoutInventingTrack()
    {
        using var media = MediaFixtures.Copy("sample.mp3");
        byte[] tag = new byte[128];
        "TAG"u8.CopyTo(tag);
        WriteLatin1(tag, 3, 30, "Legacy");
        WriteLatin1(tag, 97, 30, "Thirty byte legacy comment");
        tag[127] = 17;
        using (var stream = new FileStream(
                   media.Path, FileMode.Append, FileAccess.Write))
            stream.Write(tag);

        var mp3 = Assert.IsType<MP3File>(MediaFile.GetFile(media.Path));
        IMetadataProvider v1 = mp3.Tags.Last();

        Assert.Equal("Legacy", v1.Title);
        Assert.Null(v1.TrackNumber);
        Assert.Contains(v1.GetKnownMetadata(), value =>
            value.Key == TagFields.Comment &&
            value.Value == "Thirty byte legacy comment");
        Assert.Contains(v1.GetKnownMetadata(), value =>
            value.Key == TagFields.Genre && value.Value == "Rock");
    }

    [Fact]
    public void AddReadEditAndRemovePreserveId3v2AndAudio()
    {
        using var media = MediaFixtures.Copy("sample.mp3");
        byte[] audio = ReadAudio(media.Path);
        var mp3 = Assert.IsType<MP3File>(
            MediaFile.GetFile(media.Path, readOnly: false));

        mp3.AddTagLayer(
            TagLayerKind.Id3v1, TagLayerCopyMode.CopyPrimary);
        mp3.Save();

        var added = Assert.IsType<MP3File>(MediaFile.GetFile(media.Path));
        Assert.Equal(["ID3v23", "ID3v1"],
            added.Tags.Select(tag => tag.TagType));
        IMetadataProvider v1 = added.Tags.Last();
        Assert.Equal("TestTitle", v1.Title);
        Assert.Equal("TestArtist", v1.Artist);
        Assert.Equal(audio, ReadAudio(media.Path));

        IMetadataWriter v1Writer = Assert.IsAssignableFrom<IMetadataWriter>(v1);
        v1Writer.SetField(TagFields.Title, "Legacy title");
        v1Writer.SetField(TagFields.TrackNumber, "7");
        v1Writer.Save();

        var edited = Assert.IsType<MP3File>(MediaFile.GetFile(media.Path));
        Assert.Equal("TestTitle", edited.Tags.First().Title);
        Assert.Equal("Legacy title", edited.Tags.Last().Title);
        Assert.Equal(7, edited.Tags.Last().TrackNumber);
        Assert.Equal(audio, ReadAudio(media.Path));

        edited.RemoveTagLayer(TagLayerKind.Id3v1);
        edited.Save();

        var removed = Assert.IsType<MP3File>(MediaFile.GetFile(media.Path));
        Assert.Single(removed.Tags);
        Assert.False(Assert.Single(
            removed.EditableTagLayers).IsPresent);
        Assert.Equal(audio, ReadAudio(media.Path));
    }

    [Fact]
    public void ConversionCopiesSupportedFieldsInBothDirections()
    {
        using var media = MediaFixtures.Copy("sample.mp3");
        var mp3 = Assert.IsType<MP3File>(
            MediaFile.GetFile(media.Path, readOnly: false));
        mp3.CopyTagLayer(TagLayerKind.Id3v2, TagLayerKind.Id3v1);
        IMetadataWriter v1 = Assert.IsAssignableFrom<IMetadataWriter>(
            mp3.Tags.Last());
        v1.SetField(TagFields.Title, "Legacy source");
        v1.SetField(TagFields.Comment, "Legacy comment");

        mp3.CopyTagLayer(TagLayerKind.Id3v1, TagLayerKind.Id3v2);
        mp3.Save();

        var reloaded = Assert.IsType<MP3File>(MediaFile.GetFile(media.Path));
        Assert.Equal("Legacy source", reloaded.Tags.First().Title);
        Assert.Equal("Legacy source", reloaded.Tags.Last().Title);
        Assert.Contains(
            reloaded.Tags.Last().GetKnownMetadata(),
            value => value.Key == TagFields.Comment &&
                value.Value == "Legacy comment");
    }

    [Fact]
    public void CompatibilityIssuesReportFixedWidthTruncation()
    {
        using var media = MediaFixtures.Copy("sample.mp3");
        var mp3 = Assert.IsType<MP3File>(
            MediaFile.GetFile(media.Path, readOnly: false));
        mp3.AddTagLayer(TagLayerKind.Id3v1, TagLayerCopyMode.Empty);
        var v1 = Assert.IsType<ID3v1Tag>(mp3.Tags.Last());
        v1.SetField(TagFields.Title, new string('A', 31));
        v1.SetField(TagFields.Album, "日本語");
        v1.SetField(TagFields.Comment, new string('B', 29));
        v1.SetField(TagFields.TrackNumber, "1");

        IReadOnlyList<ID3v1CompatibilityIssue> issues =
            v1.GetCompatibilityIssues();

        Assert.Contains(issues, issue =>
            issue.Field == TagFields.Title &&
            issue.MaximumByteCount == 30);
        Assert.Contains(issues, issue =>
            issue.Field == TagFields.Comment &&
            issue.MaximumByteCount == 28);
        Assert.Contains(issues, issue =>
            issue.Field == TagFields.Album &&
            issue.Message.Contains("cannot represent"));
    }

    [Fact]
    public void SaveToSeparatePathLeavesSourceTailUntouched()
    {
        using var source = MediaFixtures.Copy("sample.mp3");
        string outputPath = Path.Combine(
            Path.GetTempPath(), $"id3v1-output-{Guid.NewGuid():N}.mp3");
        using var output = new MediaFixtures.TempMedia(outputPath);
        byte[] original = File.ReadAllBytes(source.Path);
        var mp3 = Assert.IsType<MP3File>(
            MediaFile.GetFile(source.Path, readOnly: false));
        mp3.AddTagLayer(
            TagLayerKind.Id3v1, TagLayerCopyMode.CopyPrimary);

        mp3.Save(outputPath);

        Assert.Equal(original, File.ReadAllBytes(source.Path));
        Assert.Equal(
            "TestTitle",
            MediaFile.GetFile(outputPath).Tags.Last().Title);
        Assert.Equal(ReadAudio(source.Path), ReadAudio(outputPath));
    }

    private static byte[] ReadAudio(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int start = 0;
        if (bytes.Length >= 10 &&
            bytes.AsSpan(0, 3).SequenceEqual("ID3"u8))
        {
            start = 10 +
                (bytes[6] << 21) +
                (bytes[7] << 14) +
                (bytes[8] << 7) +
                bytes[9];
        }
        int end = bytes.Length >= 128 &&
            bytes.AsSpan(bytes.Length - 128, 3).SequenceEqual("TAG"u8)
                ? bytes.Length - 128
                : bytes.Length;
        return bytes.AsSpan(start, end - start).ToArray();
    }

    private static void WriteLatin1(
        byte[] destination,
        int offset,
        int maximum,
        string value)
    {
        byte[] bytes = System.Text.Encoding.Latin1.GetBytes(value);
        Array.Copy(
            bytes, 0, destination, offset, Math.Min(maximum, bytes.Length));
    }
}
