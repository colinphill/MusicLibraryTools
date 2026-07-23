using System.Linq;
using MusicFileUtilities;
using Xunit;

namespace MusicFileUtilities.Tests;

public sealed class RemainingMultiValueFormatTests
{
    public static TheoryData<string> Mp4Containers => new()
    {
        "sample_aac.m4a",
        "sample_alac.m4a",
    };

    [Theory]
    [MemberData(nameof(Mp4Containers))]
    public void Mp4OrderedKnownAndCustomValuesRoundTrip(
        string fixture)
    {
        using var media = MediaFixtures.Copy(fixture);
        var file = Assert.IsType<MP4File>(
            MediaFile.GetFile(media.Path, readOnly: false));
        var known =
            Assert.IsAssignableFrom<IMultiValueMetadataWriter>(file);
        var custom =
            Assert.IsAssignableFrom<IMultiValueUserStringMetadata>(
                file);

        Assert.True(
            known.SupportsMultipleValues(TagFields.Artist));
        Assert.True(
            known.SupportsMultipleValues(TagFields.Conductor));
        Assert.False(
            known.SupportsMultipleValues(TagFields.BPM));
        Assert.False(
            known.SupportsMultipleValues(TagFields.TrackNumber));
        known.SetFieldValues(
            TagFields.Artist,
            ["First artist", "", "Third artist"]);
        known.SetFieldValues(
            TagFields.Conductor,
            ["First conductor", "Second conductor"]);
        custom.SetUserStringValues(
            "CUSTOM_ORDER",
            ["first", "", "third"]);

        file.SaveTags();

        MP4File reopened = Assert.IsType<MP4File>(
            MediaFile.GetFile(media.Path, readOnly: false));
        Assert.Equal(
            ["First artist", "", "Third artist"],
            Values(reopened, TagFields.Artist));
        Assert.Equal(
            ["First conductor", "Second conductor"],
            Values(reopened, TagFields.Conductor));
        Assert.Equal(
            ["first", "", "third"],
            reopened.GetUserStrings()
                .Where(value =>
                    value.Key == "CUSTOM_ORDER")
                .Select(value => value.Value));

        reopened.SetField(TagFields.Artist, "Only artist");
        reopened.SetUserString("CUSTOM_ORDER", "only");
        reopened.SaveTags();

        MP4File single = Assert.IsType<MP4File>(
            MediaFile.GetFile(media.Path));
        Assert.Equal(
            ["Only artist"],
            Values(single, TagFields.Artist));
        Assert.Equal(
            ["only"],
            single.GetUserStrings()
                .Where(value =>
                    value.Key == "CUSTOM_ORDER")
                .Select(value => value.Value));
    }

    [Fact]
    public void AsfOrderedExtendedAndCustomValuesRoundTrip()
    {
        using var media = MediaFixtures.Copy("sample.wma");
        var file = Assert.IsType<AsfFile>(
            MediaFile.GetFile(media.Path, readOnly: false));
        var known =
            Assert.IsAssignableFrom<IMultiValueMetadataWriter>(file);
        var custom =
            Assert.IsAssignableFrom<IMultiValueUserStringMetadata>(
                file);

        Assert.True(
            known.SupportsMultipleValues(TagFields.Genre));
        Assert.True(
            known.SupportsMultipleValues(TagFields.AlbumArtist));
        Assert.True(
            known.SupportsMultipleValues(TagFields.Artist));
        Assert.False(
            known.SupportsMultipleValues(TagFields.Album));
        Assert.False(
            known.SupportsMultipleValues(TagFields.TrackNumber));
        known.SetFieldValues(
            TagFields.Genre,
            ["Rock", "", "Electronic"]);
        known.SetFieldValues(
            TagFields.AlbumArtist,
            ["First ensemble", "Second ensemble"]);
        known.SetFieldValues(
            TagFields.Artist,
            ["First artist", "Second artist"]);
        custom.SetUserStringValues(
            "CUSTOM_ORDER",
            ["first", "", "third"]);

        file.SaveTags();

        AsfFile reopened = Assert.IsType<AsfFile>(
            MediaFile.GetFile(media.Path));
        Assert.Equal(
            ["Rock", "", "Electronic"],
            Values(reopened, TagFields.Genre));
        Assert.Equal(
            ["First ensemble", "Second ensemble"],
            Values(reopened, TagFields.AlbumArtist));
        Assert.Equal(
            ["First artist", "Second artist"],
            Values(reopened, TagFields.Artist));
        Assert.Equal(
            ["first", "", "third"],
            reopened.GetUserStrings()
                .Where(value =>
                    value.Key == "CUSTOM_ORDER")
                .Select(value => value.Value));
    }

    private static IEnumerable<string> Values(
        IMediaFile file,
        TagFields field) =>
        file.Tags.Single()
            .GetKnownMetadata()
            .Where(value => value.Key == field)
            .Select(value => value.Value);
}
