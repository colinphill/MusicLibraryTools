using MusicLibraryTools.Build;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class SyncerResourceVerifierTests
{
    private static readonly string FixtureRoot = Path.Combine(
        AppContext.BaseDirectory, "syncer-server-inputs");

    [Fact]
    public void RequiredResourcesHaveStableAbiPathsAndManifestNames()
    {
        string[] expectedAbis = ["arm64-v8a", "armeabi-v7a", "x86_64", "x86"];

        Assert.Equal(expectedAbis,
            SyncerResourceVerifier.RequiredResources.Select(resource => resource.Abi));
        Assert.Equal(expectedAbis.Select(abi => $"Syncer.Servers.{abi}.syncerd"),
            SyncerResourceVerifier.RequiredResources.Select(resource => resource.ResourceName));
        Assert.All(SyncerResourceVerifier.RequiredResources,
            resource => Assert.Equal("syncerd", resource.FileName));
    }

    [Fact]
    public void VerifierAcceptsAllFourExactEmbeddedPayloads() =>
        SyncerResourceVerifier.Verify(typeof(SyncerResourceVerifierTests).Assembly.Location, FixtureRoot);

    [Fact]
    public void VerifierRejectsAnUnexpectedSyncerManifestResource()
    {
        string[] names = SyncerResourceVerifier.RequiredResources
            .Select(resource => resource.ResourceName)
            .Append("Syncer.Servers.riscv64.syncerd")
            .ToArray();

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            SyncerResourceVerifier.VerifyResourceNames("fixture.dll", names));

        Assert.Contains("Syncer.Servers.riscv64.syncerd", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifierRejectsAChangedDaemonPayload()
    {
        string temporary = Path.Combine(Path.GetTempPath(),
            "syncer-resource-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyFixtures(temporary);
            File.AppendAllText(Path.Combine(temporary, "x86_64", "syncerd"), "changed");

            InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                SyncerResourceVerifier.Verify(
                    typeof(SyncerResourceVerifierTests).Assembly.Location, temporary));

            Assert.Contains("Syncer.Servers.x86_64.syncerd", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    private static void CopyFixtures(string destinationRoot)
    {
        foreach (SyncerDaemonResource resource in SyncerResourceVerifier.RequiredResources)
        {
            string directory = Path.Combine(destinationRoot, resource.Abi);
            Directory.CreateDirectory(directory);
            File.Copy(Path.Combine(FixtureRoot, resource.Abi, resource.FileName),
                Path.Combine(directory, resource.FileName));
        }
    }
}
