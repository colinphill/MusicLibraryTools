using System.Security.Cryptography;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class ArtworkNormalizationServiceTests
{
    [Fact]
    public async Task EmptyReviewedPlanCompletesWithoutCreatingRecoveryArtifacts()
    {
        using var workspace = new TempDirectory();
        string library = Path.Combine(workspace.Path, "library.itl");
        await File.WriteAllBytesAsync(library, [1, 2, 3, 4],
            TestContext.Current.CancellationToken);
        ArtworkNormalizationPlan plan = CreatePlan(library);

        ArtworkNormalizationResult result = await new ArtworkNormalizationService().ApplyAsync(
            plan, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.UpdatedFileCount);
        Assert.Null(result.JournalPath);
        Assert.False(Directory.Exists(plan.RecoveryRoot));
    }

    [Fact]
    public async Task ApplyRejectsChangedLibraryBeforeCreatingRecoveryArtifacts()
    {
        using var workspace = new TempDirectory();
        string library = Path.Combine(workspace.Path, "library.itl");
        await File.WriteAllBytesAsync(library, [1, 2, 3, 4],
            TestContext.Current.CancellationToken);
        ArtworkNormalizationPlan plan = CreatePlan(library);
        await File.AppendAllTextAsync(library, "changed", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ArtworkNormalizationService().ApplyAsync(
                plan, ct: TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(plan.RecoveryRoot));
    }

    [Fact]
    public async Task ApplyRejectsBlockedPlan()
    {
        using var workspace = new TempDirectory();
        string library = Path.Combine(workspace.Path, "library.itl");
        await File.WriteAllBytesAsync(library, [1], TestContext.Current.CancellationToken);
        ArtworkNormalizationPlan plan = CreatePlan(library) with
        {
            Issues = [new("playlist-ambiguous", OperationIssueSeverity.Blocker, "Ambiguous")],
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ArtworkNormalizationService().ApplyAsync(
                plan, ct: TestContext.Current.CancellationToken));
    }

    private static ArtworkNormalizationPlan CreatePlan(string library)
    {
        var info = new FileInfo(library);
        var snapshot = new OperationPathSnapshot(true, false, info.Length, info.LastWriteTimeUtc)
        {
            Path = library,
        };
        return new(new("Artwork"), library, snapshot,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(library))), [], 0, 0, 0, [],
            Path.Combine(Path.GetDirectoryName(library)!, "recovery"), DateTimeOffset.UtcNow);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ArtworkNormalizationTests", Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
