using MusicLibrary.Core.Services;
using System.Text.RegularExpressions;
using Xunit;

namespace MusicLibrary.Core.Tests;

public class PatternMatcherTests
{
    [Fact]
    public void Substring_MatchesCaseInsensitively()
    {
        var m = PatternMatcher.Create("beatles", FilterMode.Substring);
        Assert.True(m.IsMatch("The BEATLES - Help"));
        Assert.False(m.IsMatch("Radiohead"));
    }

    [Theory]
    [InlineData("*.flac", "song.flac", true)]
    [InlineData("*.flac", "song.mp3", false)]
    [InlineData("track0?", "track09", true)]
    [InlineData("track0?", "track10", false)]
    public void Glob_TranslatesWildcards(string pattern, string input, bool expected)
    {
        var m = PatternMatcher.Create(pattern, FilterMode.Glob);
        Assert.True(m.IsValid);
        Assert.Equal(expected, m.IsMatch(input));
    }

    [Fact]
    public void Glob_NonBacktrackingMatcherHasNoWallClockTimeout()
    {
        var m = PatternMatcher.Create("track0?", FilterMode.Glob);

        Assert.Equal(
            Regex.InfiniteMatchTimeout,
            m.RegexMatchTimeout);
        Assert.True(m.IsMatch("track09"));
    }

    [Fact]
    public void Regex_Matches()
    {
        var m = PatternMatcher.Create(@"^\d{2}\s", FilterMode.Regex);
        Assert.Equal(
            TimeSpan.FromMilliseconds(250),
            m.RegexMatchTimeout);
        Assert.True(m.IsMatch("01 Intro"));
        Assert.False(m.IsMatch("Intro"));
    }

    [Fact]
    public void Regex_InvalidPattern_IsInvalidAndMatchesNothing()
    {
        var m = PatternMatcher.Create("([unclosed", FilterMode.Regex);
        Assert.False(m.IsValid);
        Assert.False(m.IsMatch("anything"));
    }

    [Fact]
    public void EmptyPattern_MatchesEverything()
    {
        var m = PatternMatcher.Create("", FilterMode.Regex);
        Assert.True(m.IsEmpty);
        Assert.True(m.IsMatch("whatever"));
    }

    [Fact]
    public void Regex_PathologicalBacktrackingTimesOutAndDoesNotMatch()
    {
        var m = PatternMatcher.Create("^(a+)+$", FilterMode.Regex);
        var input = new string('a', 50_000) + "!";

        var started = DateTime.UtcNow;
        Assert.False(m.IsMatch(input));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
    }
}
