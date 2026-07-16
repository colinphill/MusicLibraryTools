using MusicLibraryTools;
using Xunit;

namespace MusicFileUtilities.Tests;

public sealed class LibraryOrganizationPolicyTests
{
    [Fact]
    public void ExcludedRootIsNotEligibleButUnassignedRootDefaultsToEligible()
    {
        string parent = Path.Combine(Path.GetTempPath(), "organization-policy");
        string included = Path.Combine(parent, "included");
        string excluded = Path.Combine(parent, "excluded");
        LibraryIndexLocation[] locations =
        [
            new(included, null, [], null),
            new(excluded, null, [], null, Organize: false),
        ];

        Assert.Equal([included], LibraryOrganizationPolicy.EligibleRoots(locations));
        Assert.True(LibraryOrganizationPolicy.IsPathEligible(
            Path.Combine(included, "song.flac"), locations));
        Assert.False(LibraryOrganizationPolicy.IsPathEligible(
            Path.Combine(excluded, "song.flac"), locations));
    }

    [Fact]
    public void NestedExcludedRootProtectsItsFilesAndParentFromRecursiveCleanup()
    {
        string parent = Path.Combine(Path.GetTempPath(), "organization-policy");
        string excluded = Path.Combine(parent, "purchased");
        LibraryIndexLocation[] locations =
        [
            new(parent, null, [], null),
            new(excluded, null, [], null, Organize: false),
        ];

        Assert.True(LibraryOrganizationPolicy.IsPathEligible(
            Path.Combine(parent, "regular", "song.flac"), locations));
        Assert.False(LibraryOrganizationPolicy.IsPathEligible(
            Path.Combine(excluded, "song.m4a"), locations));
        Assert.Empty(LibraryOrganizationPolicy.CleanupRoots(locations));
    }
}
