using System.Linq;
using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests;

public sealed class Id3MultiValueTests
{
    public static TheoryData<string> Id3Containers => new()
    {
        "sample.mp3",
        "sample.dsf",
        "sample.wav",
        "sample.aiff",
        "sample.aac",
    };

    [Theory]
    [MemberData(nameof(Id3Containers))]
    public void Id3v24OrderedTextValuesRoundTripAcrossContainers(
        string fixture)
    {
        using var media = MediaFixtures.Copy(fixture);
        IMediaFile file =
            MediaFile.GetFile(media.Path, readOnly: false);
        ID3v2Tag tag =
            Assert.IsAssignableFrom<ID3v2Tag>(file.Tags.First());
        tag.ChangeVersion(ID3v2Version.V24);
        var writer =
            Assert.IsAssignableFrom<IMultiValueMetadataWriter>(file);

        Assert.True(writer.SupportsMultipleValues(TagFields.Artist));
        Assert.True(writer.SupportsMultipleValues(TagFields.Genre));
        Assert.False(
            writer.SupportsMultipleValues(TagFields.TrackNumber));
        writer.SetFieldValues(
            TagFields.Artist,
            ["First artist", "Second artist"]);
        writer.SetFieldValues(
            TagFields.Genre,
            ["Rock", "Electronic"]);

        file.SaveTags();

        IMediaFile reopened = MediaFile.GetFile(media.Path);
        ID3v2Tag reopenedTag =
            Assert.IsAssignableFrom<ID3v2Tag>(
                reopened.Tags.First());
        Assert.Equal(4, reopenedTag.Version);
        Assert.Equal(
            ["First artist", "Second artist"],
            reopenedTag.GetKnownMetadata()
                .Where(value =>
                    value.Key == TagFields.Artist)
                .Select(value => value.Value));
        Assert.Equal(
            ["Rock", "Electronic"],
            reopenedTag.GetKnownMetadata()
                .Where(value =>
                    value.Key == TagFields.Genre)
                .Select(value => value.Value));
    }

    [Fact]
    public void LegacyId3VersionsAndCompoundFieldsRejectMultipleValues()
    {
        using var media = MediaFixtures.Copy("sample.mp3");
        var file = Assert.IsType<MP3File>(
            MediaFile.GetFile(media.Path, readOnly: false));
        var writer =
            Assert.IsAssignableFrom<IMultiValueMetadataWriter>(file);

        Assert.False(
            writer.SupportsMultipleValues(TagFields.Artist));
        Assert.Throws<InvalidOperationException>(() =>
            writer.SetFieldValues(
                TagFields.Artist,
                ["First", "Second"]));

        file.ChangeVersion(ID3v2Version.V24);
        Assert.False(
            writer.SupportsMultipleValues(TagFields.TrackNumber));
        Assert.Throws<ArgumentException>(() =>
            writer.SetFieldValues(
                TagFields.TrackNumber,
                ["1", "2"]));
        Assert.Throws<ArgumentException>(() =>
            writer.SetFieldValues(
                TagFields.Artist,
                ["First", ""]));
    }
}
