using System.Security.Cryptography;
using iTunes.Binary;
using MusicFileUtilities;
using MusicLibrary.Core.Services;
using MusicLibraryTools;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class ItlMetadataRepairServiceTests
{
    [Fact]
    public async Task ApplyRejectsChangedLibraryBeforeParsingOrWriting()
    {
        using var workspace = new TempDirectory();
        string libraryPath = workspace.File(
            "library.itl",
            "reviewed library bytes");
        string mediaPath = workspace.File(
            "track.flac",
            "media remains untouched");
        string configurationPath = Path.Combine(
            workspace.Path,
            "library.xml");
        var editable = new EditableLibraryConfig
        {
            ActiveProfileId =
                LibraryProfilePresets.PreserveLayoutAndTagsId,
            ItunesLibraryPath = libraryPath,
            DatabaseFile = Path.Combine(
                workspace.Path,
                "cache.db"),
        };
        editable.IndexTargets.Add(
            editable.CreateIndexTarget(workspace.Path));
        editable.Save(configurationPath);
        var configuration = new LibraryConfiguration(
            configurationPath);
        string reviewedHash = Convert.ToHexString(
            SHA256.HashData(
                await File.ReadAllBytesAsync(
                    libraryPath,
                    TestContext.Current.CancellationToken)));
        var item = new ItlMetadataRepairItem(
            Guid.NewGuid(),
            TrackId: 1,
            PersistentId: 42,
            mediaPath,
            new ItlCachedTrackMetadata
            {
                Title = "Reviewed title",
            },
            DateTime.UtcNow,
            [new("Title", "Before", "Reviewed title")]);
        var plan = new ItlMetadataRepairPlan(
            libraryPath,
            reviewedHash,
            DateTimeOffset.UtcNow,
            [item])
        {
            LibraryId = configuration.LibraryId,
            PolicyFingerprint =
                configuration.PolicySnapshot.Fingerprint,
            ConfigurationPath = configurationPath,
        };
        byte[] mediaBefore = await File.ReadAllBytesAsync(
            mediaPath,
            TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(
            libraryPath,
            " external change",
            TestContext.Current.CancellationToken);
        byte[] changedLibrary = await File.ReadAllBytesAsync(
            libraryPath,
            TestContext.Current.CancellationToken);
        var service = new ItlMetadataRepairService(
            new LibraryOperationContextFactory());

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.ApplyAsync(
                    plan,
                    [item.Id],
                    ct: TestContext.Current.CancellationToken));

        Assert.Contains(
            "changed after this preview",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            changedLibrary,
            await File.ReadAllBytesAsync(
                libraryPath,
                TestContext.Current.CancellationToken));
        Assert.Equal(
            mediaBefore,
            await File.ReadAllBytesAsync(
                mediaPath,
                TestContext.Current.CancellationToken));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "mlm-itl-repair-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public string File(string name, string content)
        {
            string path = System.IO.Path.Combine(Path, name);
            System.IO.File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch { }
        }
    }
}
