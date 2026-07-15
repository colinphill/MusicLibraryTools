using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class DeviceSyncServiceTests
{
    [Fact]
    public async Task LocalPreviewAndApplyUseReviewedCopyReplaceAndQuarantineActions()
    {
        using var workspace = new TempDirectory();
        string source = Directory.CreateDirectory(Path.Combine(workspace.Path, "source")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(workspace.Path, "destination")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "album"));
        await File.WriteAllTextAsync(Path.Combine(source, "album", "new.txt"), "new",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(source, "replace.txt"), "replacement",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(destination, "replace.txt"), "old",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(destination, "stale.txt"), "stale",
            TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(destination, "stale-dir"));
        await File.WriteAllTextAsync(Path.Combine(destination, "stale-dir", "old.txt"), "old",
            TestContext.Current.CancellationToken);
        var service = CreateService();

        DeviceSyncPlan plan = await service.PreviewAsync(
            new(source, destination, MaxRemovals: 3),
            ct: TestContext.Current.CancellationToken);

        Assert.True(plan.CanApply);
        Assert.Contains(plan.Actions, action => action.Kind == DeviceSyncMutationKind.CreateDirectory);
        Assert.Contains(plan.Actions, action => action.Kind == DeviceSyncMutationKind.CopyFile);
        Assert.Contains(plan.Actions, action => action.Kind == DeviceSyncMutationKind.ReplaceFile);
        Assert.Contains(plan.Actions, action => action.Kind == DeviceSyncMutationKind.QuarantineFile);
        Assert.Contains(plan.Actions, action => action.Kind == DeviceSyncMutationKind.QuarantineDirectory);
        Assert.Equal(3, plan.RemovalCount);

        DeviceSyncResult result = await service.ApplyAsync(
            plan, ct: TestContext.Current.CancellationToken);

        Assert.Equal("new", await File.ReadAllTextAsync(
            Path.Combine(destination, "album", "new.txt"), TestContext.Current.CancellationToken));
        Assert.Equal("replacement", await File.ReadAllTextAsync(
            Path.Combine(destination, "replace.txt"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(destination, "stale.txt")));
        Assert.False(Directory.Exists(Path.Combine(destination, "stale-dir")));
        Assert.True(File.Exists(Path.Combine(plan.RecoveryRoot, "stale.txt")));
        Assert.True(File.Exists(Path.Combine(plan.RecoveryRoot, "stale-dir", "old.txt")));
        Assert.True(File.Exists(result.JournalPath));
    }

    [Fact]
    public async Task ApplyRejectsAnySourceChangeBeforeCreatingRecoveryRoot()
    {
        using var workspace = new TempDirectory();
        string source = Directory.CreateDirectory(Path.Combine(workspace.Path, "source")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(workspace.Path, "destination")).FullName;
        string file = Path.Combine(source, "track.flac");
        await File.WriteAllTextAsync(file, "original", TestContext.Current.CancellationToken);
        var service = CreateService();
        DeviceSyncPlan plan = await service.PreviewAsync(
            new(source, destination), ct: TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(file, "changed", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(
            plan, ct: TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(plan.RecoveryRoot));
        Assert.False(File.Exists(Path.Combine(destination, "track.flac")));
    }

    [Fact]
    public async Task RemovalLimitBlocksPlanWithoutChangingDestination()
    {
        using var workspace = new TempDirectory();
        string source = Directory.CreateDirectory(Path.Combine(workspace.Path, "source")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(workspace.Path, "destination")).FullName;
        string stale = Path.Combine(destination, "stale.txt");
        await File.WriteAllTextAsync(stale, "stale", TestContext.Current.CancellationToken);

        DeviceSyncPlan plan = await CreateService().PreviewAsync(
            new(source, destination), ct: TestContext.Current.CancellationToken);

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Issues, issue => issue.Code == "removal-limit");
        Assert.True(File.Exists(stale));
    }

    [Fact]
    public async Task MusicRemapCollisionIsBlocking()
    {
        using var workspace = new TempDirectory();
        string source = Directory.CreateDirectory(Path.Combine(workspace.Path, "source")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(workspace.Path, "destination")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "FLAC", "Artist"));
        Directory.CreateDirectory(Path.Combine(source, "Lossy", "Artist"));
        await File.WriteAllTextAsync(Path.Combine(source, "FLAC", "Artist", "song.flac"), "a",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(source, "Lossy", "Artist", "song.flac"), "b",
            TestContext.Current.CancellationToken);

        DeviceSyncPlan plan = await CreateService().PreviewAsync(
            new(source, destination, RemapMusic: true), ct: TestContext.Current.CancellationToken);

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Issues, issue => issue.Code == "remap-collision");
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task OverlappingRootsAreBlockedBeforeInventory()
    {
        using var workspace = new TempDirectory();
        string source = Directory.CreateDirectory(Path.Combine(workspace.Path, "source")).FullName;
        string destination = Path.Combine(source, "nested");

        DeviceSyncPlan plan = await CreateService().PreviewAsync(
            new(source, destination), ct: TestContext.Current.CancellationToken);

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Issues, issue => issue.Code == "root-overlap");
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task ApplyRollsBackCompletedQuarantinesWhenAWriteFails()
    {
        using var workspace = new TempDirectory();
        string source = Directory.CreateDirectory(Path.Combine(workspace.Path, "source")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(workspace.Path, "destination")).FullName;
        await File.WriteAllTextAsync(Path.Combine(source, "new.txt"), "new",
            TestContext.Current.CancellationToken);
        string stale = Path.Combine(destination, "stale.txt");
        await File.WriteAllTextAsync(stale, "stale", TestContext.Current.CancellationToken);
        var factory = new FailingWriteFactory(destination);
        var service = new DeviceSyncService(factory);
        DeviceSyncPlan plan = await service.PreviewAsync(
            new(source, destination, MaxRemovals: 1), ct: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(
            plan, ct: TestContext.Current.CancellationToken));

        Assert.True(File.Exists(stale));
        Assert.Equal("stale", await File.ReadAllTextAsync(stale, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(destination, "new.txt")));
        string journal = Path.Combine(plan.RecoveryRoot, "journal.tsv");
        Assert.Contains("ROLLBACK", await File.ReadAllTextAsync(
            journal, TestContext.Current.CancellationToken));
    }

    private static DeviceSyncService CreateService() => new(new FileTreeEndpointFactory());

    private sealed class FailingWriteFactory(string destination) : IFileTreeEndpointFactory
    {
        private readonly FileTreeEndpointFactory _inner = new();
        private readonly string _destination = Path.GetFullPath(destination);

        public FileTreeEndpointDescriptor Parse(string value) => _inner.Parse(value);

        public IFileTreeEndpoint Create(FileTreeEndpointDescriptor descriptor)
        {
            IFileTreeEndpoint endpoint = _inner.Create(descriptor);
            return descriptor.Kind == FileTreeEndpointKind.Local &&
                   StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(descriptor.Root), _destination)
                ? new FailingWriteEndpoint(endpoint)
                : endpoint;
        }
    }

    private sealed class FailingWriteEndpoint(IFileTreeEndpoint inner) : IFileTreeEndpoint
    {
        public FileTreeEndpointDescriptor Descriptor => inner.Descriptor;
        public Task<FileTreeSnapshot> CaptureAsync(IProgress<MusicLibrary.Core.Models.OperationProgress>? progress = null,
            CancellationToken ct = default) => inner.CaptureAsync(progress, ct);
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) =>
            inner.OpenReadAsync(path, ct);
        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) =>
            inner.CreateDirectoryAsync(path, ct);
        public Task WriteFileAsync(string path, Stream source, DateTime modifiedUtc,
            IProgress<long>? progress = null, CancellationToken ct = default) =>
            Task.FromException(new IOException("Injected destination write failure."));
        public Task MoveAsync(string sourcePath, string destinationPath, CancellationToken ct = default) =>
            inner.MoveAsync(sourcePath, destinationPath, ct);
        public Task DeleteFileAsync(string path, CancellationToken ct = default) =>
            inner.DeleteFileAsync(path, ct);
        public Task DeleteDirectoryAsync(string path, CancellationToken ct = default) =>
            inner.DeleteDirectoryAsync(path, ct);
        public Task AppendJournalLinesAsync(string journalPath, IReadOnlyList<string> lines,
            CancellationToken ct = default) => inner.AppendJournalLinesAsync(journalPath, lines, ct);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "DeviceSyncTests", Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
