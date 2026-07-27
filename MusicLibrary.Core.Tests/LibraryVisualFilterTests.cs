using MetadataCaching;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using System.Collections.Immutable;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class LibraryVisualFilterTests
{
    [Fact]
    public void GroupsCombineKnownCustomAndTechnicalConditions()
    {
        var expression = new LibraryFilterGroup(
            LibraryFilterGroupMode.All,
            [
                new LibraryFilterCondition(
                    LibraryFilterField.Known(TagFields.Genre),
                    LibraryFilterComparison.Contains,
                    "Jazz"),
                new LibraryFilterCondition(
                    LibraryFilterField.Custom("DJ_SET"),
                    LibraryFilterComparison.Present),
                new LibraryFilterCondition(
                    LibraryFilterField.Technical("SampleRate"),
                    LibraryFilterComparison.GreaterThanOrEqual,
                    "96000"),
            ]);
        var filter = new LibraryVisualFilter(expression);
        var matching = new TrackRecord
        {
            Path = "match.flac",
            Genre = "Modal Jazz",
            SampleRate = 96000,
            Metadata = new Dictionary<string, string[]>
            {
                [nameof(TagFields.Genre)] = ["Modal Jazz"],
                [CachedMetadataKeys.Custom("DJ_SET")] =
                    ["Sunrise"],
            },
        };

        Assert.True(filter.IsValid);
        Assert.True(filter.IsMatch(matching));
        Assert.False(filter.IsMatch(
            matching with { SampleRate = 44100 }));
        Assert.False(filter.IsMatch(
            matching with
            {
                Metadata =
                    new Dictionary<string, string[]>
                    {
                        [nameof(TagFields.Genre)] =
                            ["Modal Jazz"],
                    },
            }));
    }

    [Fact]
    public void AnyGroupsAndNegationSupportBooleanExpressions()
    {
        var expression = new LibraryFilterGroup(
            LibraryFilterGroupMode.Any,
            [
                new LibraryFilterCondition(
                    LibraryFilterField.Known(TagFields.Artist),
                    LibraryFilterComparison.Equals,
                    "Miles Davis"),
                new LibraryFilterGroup(
                    LibraryFilterGroupMode.All,
                    [
                        new LibraryFilterCondition(
                            LibraryFilterField.Known(
                                TagFields.Artist),
                            LibraryFilterComparison.Contains,
                            "Coltrane"),
                        new LibraryFilterCondition(
                            LibraryFilterField.Technical("Codec"),
                            LibraryFilterComparison.Equals,
                            "MP3",
                            IsNegated: true),
                    ]),
            ]);
        var filter = new LibraryVisualFilter(expression);

        Assert.True(filter.IsMatch(new()
        {
            Path = "miles.flac",
            Artist = "Miles Davis",
        }));
        Assert.True(filter.IsMatch(new()
        {
            Path = "john.flac",
            Artist = "John Coltrane",
            CodecName = "FLAC",
        }));
        Assert.False(filter.IsMatch(new()
        {
            Path = "john.mp3",
            Artist = "John Coltrane",
            CodecName = "MP3",
        }));
    }

    [Fact]
    public void InvalidRegularExpressionIsRejectedBeforeEvaluation()
    {
        var filter = new LibraryVisualFilter(
            new LibraryFilterCondition(
                LibraryFilterField.Custom("NOTE"),
                LibraryFilterComparison.MatchesRegularExpression,
                "([broken"));

        Assert.False(filter.IsValid);
        Assert.Contains(
            "regular expression",
            filter.Error!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SparseProjectionSuppliesMetadataWithoutMutatingBrowseRecord()
    {
        MetadataFieldKey custom =
            MetadataFieldKey.Custom(
                "DJ_SET");
        var filter =
            new LibraryVisualFilter(
                new LibraryFilterGroup(
                    LibraryFilterGroupMode.All,
                    [
                        new LibraryFilterCondition(
                            LibraryFilterField
                                .Custom(
                                    "DJ_SET"),
                            LibraryFilterComparison
                                .Equals,
                            "Sunrise"),
                        new LibraryFilterCondition(
                            LibraryFilterField
                                .Technical(
                                    "Codec"),
                            LibraryFilterComparison
                                .Equals,
                            "FLAC"),
                    ]));
        var browse = new TrackRecord
        {
            Path = "song.flac",
            CodecName = "FLAC",
        };
        var projection = new Dictionary<
            MetadataFieldKey,
            ImmutableArray<string>>
        {
            [custom] = ["Sunrise"],
        };

        Assert.Equal(
            [custom],
            filter.RequiredMetadataFields);
        Assert.True(
            filter.IsMatch(
                browse,
                projection));
        Assert.Empty(browse.Metadata);
    }
}
