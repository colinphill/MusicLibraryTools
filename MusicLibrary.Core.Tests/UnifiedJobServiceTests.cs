using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class UnifiedJobServiceTests
{
    [Fact]
    public void CatalogContainsOnlyOperationsBackedByTypedCoreServices()
    {
        var catalog = new UnifiedJobService().Catalog;

        Assert.Equal(
            ["playlist-sync", "artwork-normalization", "smart-storage", "car-card", "cross-library-sync", "redundancies", "itunes-validation"],
            catalog.Select(job => job.Id).ToArray());
        Assert.Equal(UnifiedJobApplyMode.ApplyFlag,
            catalog.Single(job => job.Id == "playlist-sync").ApplyMode);
        Assert.Equal(UnifiedJobApplyMode.ReadOnly,
            catalog.Single(job => job.Id == "itunes-validation").ApplyMode);
        Assert.Equal(UnifiedJobApplyMode.ApplyFlag,
            catalog.Single(job => job.Id == "artwork-normalization").ApplyMode);
    }

    [Fact]
    public void CatalogCarriesPresentationMetadataButNoExecutionArguments()
    {
        foreach (UnifiedJobDescriptor descriptor in new UnifiedJobService().Catalog)
        {
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Name));
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Description));
            Assert.DoesNotContain(".exe", descriptor.ArgumentsHint,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
