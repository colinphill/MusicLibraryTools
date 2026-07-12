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
}
