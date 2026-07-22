using MusicLibraryTools;
using Xunit;

namespace MusicLibrary.Core.Tests;

public class FfmpegRunnerTests
{
    [Fact]
    public void ExtraOptions_ParsesQuotedFiltersAndEscapedWhitespace()
    {
        IReadOnlyList<string> arguments = FfmpegOptionTokenizer.Parse(
            "-af \"loudnorm=I=-16:LRA=11\" -metadata 'comment=custom ingest' " +
            "-filter_name value\\ with\\ spaces");

        Assert.Equal([
            "-af", "loudnorm=I=-16:LRA=11",
            "-metadata", "comment=custom ingest",
            "-filter_name", "value with spaces",
        ], arguments);
    }

    [Fact]
    public void ExtraOptions_PreservesOrdinaryBackslashesAndEmptyQuotedArguments()
    {
        IReadOnlyList<string> arguments = FfmpegOptionTokenizer.Parse(
            "-metadata path=C:\\Music\\Album -metadata comment=\"\"");

        Assert.Equal([
            "-metadata", "path=C:\\Music\\Album",
            "-metadata", "comment=",
        ], arguments);
    }

    [Fact]
    public void ExtraOptions_RejectsUnmatchedQuotes()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            FfmpegOptionTokenizer.Parse("-af \"loudnorm"));

        Assert.Contains("unmatched quotation mark", error.Message);
    }
}
