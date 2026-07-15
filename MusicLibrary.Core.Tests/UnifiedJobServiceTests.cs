using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class UnifiedJobServiceTests
{
    [Fact]
    public void CatalogCoversMutableAndReadOnlyLibraryOperations()
    {
        var catalog = new UnifiedJobService().Catalog;

        foreach (string id in new[]
                 {
                     "playlist-sync", "cross-library-sync", "android-sync", "car-card",
                     "smart-storage", "artwork-repair", "redundancies", "itunes-validation",
                 })
            Assert.Contains(catalog, job => job.Id == id);
        Assert.Equal(UnifiedJobApplyMode.ReadOnly,
            catalog.Single(job => job.Id == "itunes-validation").ApplyMode);
    }

    [Fact]
    public void ArgumentParserPreservesQuotedPathsAndRejectsUnmatchedQuotes()
    {
        Assert.Equal([@"C:\Music Library\library.xml", "--max-removals", "20"],
            UnifiedJobService.ParseArguments("\"C:\\Music Library\\library.xml\" --max-removals 20"));
        Assert.Throws<ArgumentException>(() => UnifiedJobService.ParseArguments("\"unfinished"));
    }
}
