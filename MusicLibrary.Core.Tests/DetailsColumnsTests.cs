using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class DetailsColumnsTests
{
    [Fact]
    public void Duration_DoesNotWrapAfterTwentyFourHours()
    {
        var record = new TrackRecord
        {
            Path = "audiobook.m4b",
            DurationInSeconds = 25 * 60 * 60 + 2 * 60 + 3,
        };

        Assert.Equal("25:02:03", DetailsColumns.Get("Duration").Get(record));
    }

    [Fact]
    public void BrowseMetadataColumnsExposeTypedValues()
    {
        var record = new TrackRecord
        {
            Path = "song.flac",
            Genre = "Jazz",
            Composer = "Alice Composer",
            Grouping = "Reference",
            Year = 1997,
        };

        Assert.Equal("Jazz", DetailsColumns.Get("Genre").Get(record));
        Assert.Equal("Alice Composer", DetailsColumns.Get("Composer").Get(record));
        Assert.Equal("Reference", DetailsColumns.Get("Grouping").Get(record));
        Assert.Equal("1997", DetailsColumns.Get("Year").Get(record));
        Assert.Equal(1997, DetailsColumns.Get("Year").SortKey!(record));
    }

    [Fact]
    public void CatalogIncludesSelectableFileAndCodecProperties()
    {
        var record = new TrackRecord
        {
            Path = "song.flac",
            TagType = "VorbisComment",
            Length = 1536,
            SampleRate = 96000,
            BitsPerSample = 24,
            AverageBitRate = 2800,
            Channels = 2,
        };

        Assert.Equal("VorbisComment",
            DetailsColumns.Get("TagType").Get(record));
        Assert.Equal("1.5 KB",
            DetailsColumns.Get("FileSize").Get(record));
        Assert.Equal("96,000 Hz",
            DetailsColumns.Get("SampleRate").Get(record));
        Assert.Equal(1536L,
            DetailsColumns.Get("FileSize").SortKey!(record));
    }
}
