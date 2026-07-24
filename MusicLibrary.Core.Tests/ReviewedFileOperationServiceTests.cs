using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class ReviewedFileOperationServiceTests
{
    [Fact]
    public async Task CopyPreviewResolvesClaimedNamesAndAppliesReviewedPlan()
    {
        using var temp = new TempDirectory();
        string first = temp.File(
            Path.Combine("source-a", "song.flac"),
            "first");
        string second = temp.File(
            Path.Combine("source-b", "song.flac"),
            "second");
        string destination = temp.Directory("copies");
        ReviewedFileOperationService service =
            CreateService(temp);

        ReviewedFileOperationPlan plan =
            await service.PreviewAsync(new(
                [first, second],
                ReviewedFileOperationKind.Copy,
                destination,
                CollisionPolicy:
                    ReviewedFileCollisionPolicy.Suffix));

        Assert.True(plan.CanApply);
        Assert.Equal(2, plan.MutationPlan.Actions.Count);
        Assert.Equal(
            Path.Combine(destination, "song.flac"),
            plan.Items[0].DestinationPath);
        Assert.Equal(
            Path.Combine(destination, "song_2.flac"),
            plan.Items[1].DestinationPath);

        FileMutationSummary result =
            await service.ApplyAsync(plan);

        Assert.Equal(2, result.Copied);
        Assert.Equal("first", File.ReadAllText(
            Path.Combine(destination, "song.flac")));
        Assert.Equal("second", File.ReadAllText(
            Path.Combine(destination, "song_2.flac")));
        Assert.NotNull(result.JournalPath);
    }

    [Fact]
    public async Task RenameUsesMoveJournalAndCanBeRestored()
    {
        using var temp = new TempDirectory();
        string source = temp.File(
            Path.Combine("album", "old.flac"),
            "audio");
        ReviewedFileOperationService service =
            CreateService(temp);
        ReviewedFileOperationPlan plan =
            await service.PreviewAsync(new(
                [source],
                ReviewedFileOperationKind.Rename,
                FileNameTemplate:
                    "new{Extension}"));
        string destination =
            Assert.Single(plan.Items).DestinationPath!;

        FileMutationSummary result =
            await service.ApplyAsync(plan);

        Assert.Equal(1, result.Moved);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(destination));
        Assert.Contains(
            $"MOVE\tFILE\t{source}\t{destination}",
            File.ReadAllLines(result.JournalPath!));

        string run = Path.GetDirectoryName(
            result.JournalPath!)!;
        var journal = new OperationJournalService();
        var summary = new OperationJournalSummary(
            "MusicLibraryManager",
            OperationJournalKind.Other,
            OperationJournalState.Completed,
            run,
            result.JournalPath,
            DateTimeOffset.UtcNow,
            1);
        OperationFileEntry entry = Assert.Single(
            (await journal.BrowseAsync(summary)).Entries);
        Assert.Equal(OperationEntryKind.Moved, entry.Kind);
        OperationRestorePlan restore =
            await journal.PreviewRestoreAsync(
                summary,
                [entry]);

        await journal.ApplyRestoreAsync(restore);

        Assert.True(File.Exists(source));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task QuarantineMovesToReviewedFolderAndRemainsRecoverable()
    {
        using var temp = new TempDirectory();
        string source = temp.File(
            Path.Combine("album", "remove.flac"),
            "audio");
        string quarantine =
            temp.Directory("reviewed-quarantine");
        ReviewedFileOperationService service =
            CreateService(temp);

        ReviewedFileOperationPlan plan =
            await service.PreviewAsync(new(
                [source],
                ReviewedFileOperationKind.Quarantine,
                quarantine));
        FileMutationSummary result =
            await service.ApplyAsync(plan);

        Assert.Equal(1, result.Quarantined);
        Assert.False(File.Exists(source));
        Assert.Equal(
            "audio",
            File.ReadAllText(
                Path.Combine(
                    quarantine,
                    "remove.flac")));
        Assert.Contains(
            "QUARANTINE\tSTALE",
            File.ReadAllText(result.JournalPath!));
    }

    [Fact]
    public async Task CollisionIsBlockingUnlessSuffixingWasReviewed()
    {
        using var temp = new TempDirectory();
        string source = temp.File(
            Path.Combine("source", "song.flac"),
            "new");
        string destination =
            temp.Directory("destination");
        temp.File(
            Path.Combine("destination", "song.flac"),
            "existing");
        ReviewedFileOperationService service =
            CreateService(temp);

        ReviewedFileOperationPlan blocked =
            await service.PreviewAsync(new(
                [source],
                ReviewedFileOperationKind.Move,
                destination));
        ReviewedFileOperationPlan suffixed =
            await service.PreviewAsync(new(
                [source],
                ReviewedFileOperationKind.Move,
                destination,
                CollisionPolicy:
                    ReviewedFileCollisionPolicy.Suffix));

        Assert.False(blocked.CanApply);
        Assert.Contains(
            blocked.MutationPlan.Issues,
            issue =>
                issue.Code ==
                "file-operation.collision");
        Assert.True(suffixed.CanApply);
        Assert.EndsWith(
            "song_2.flac",
            Assert.Single(suffixed.Items)
                .DestinationPath);

        ReviewedFileOperationPlan destinationIsFile =
            await service.PreviewAsync(new(
                [source],
                ReviewedFileOperationKind.Copy,
                Path.Combine(
                    destination,
                    "song.flac")));
        Assert.False(destinationIsFile.CanApply);
        Assert.Contains(
            destinationIsFile.MutationPlan.Issues,
            issue =>
                issue.Code ==
                "file-operation.destination-file");
    }

    [Fact]
    public async Task ApplyRejectsStaleSourceBeforeMovingAnyFile()
    {
        using var temp = new TempDirectory();
        string first = temp.File(
            Path.Combine("source", "first.flac"),
            "first");
        string second = temp.File(
            Path.Combine("source", "second.flac"),
            "second");
        string destination =
            temp.Directory("destination");
        ReviewedFileOperationService service =
            CreateService(temp);
        ReviewedFileOperationPlan plan =
            await service.PreviewAsync(new(
                [first, second],
                ReviewedFileOperationKind.Move,
                destination));
        File.AppendAllText(second, " changed");

        InvalidOperationException error =
            await Assert.ThrowsAsync<
                InvalidOperationException>(
                () => service.ApplyAsync(plan));

        Assert.Contains(
            "Stale plan",
            error.Message);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.False(File.Exists(
            Path.Combine(
                destination,
                "first.flac")));
    }

    [Fact]
    public async Task PreserveRelativeLayoutAndTemplateTokensAreDeterministic()
    {
        using var temp = new TempDirectory();
        string first = temp.File(
            Path.Combine(
                "source",
                "disc-1",
                "one.flac"),
            "one");
        string second = temp.File(
            Path.Combine(
                "source",
                "disc-2",
                "two.flac"),
            "two");
        string destination =
            temp.Directory("destination");
        ReviewedFileOperationService service =
            CreateService(temp);

        ReviewedFileOperationPlan plan =
            await service.PreviewAsync(new(
                [first, second],
                ReviewedFileOperationKind.Copy,
                destination,
                "{Index}-{Name}{Extension}",
                PreserveRelativeLayout: true));

        Assert.Equal(
            Path.Combine(
                destination,
                "disc-1",
                "1-one.flac"),
            plan.Items[0].DestinationPath);
        Assert.Equal(
            Path.Combine(
                destination,
                "disc-2",
                "2-two.flac"),
            plan.Items[1].DestinationPath);
    }

    [Fact]
    public async Task ActiveLibraryPolicyBlocksMovingFromReadOnlyRoot()
    {
        using var temp = new TempDirectory();
        string libraryRoot =
            temp.Directory("library");
        string source = temp.File(
            Path.Combine(
                "library",
                "song.flac"),
            "audio");
        string destination =
            temp.Directory("outside");
        string configuration =
            temp.File(
                "library.xml",
                "<LibraryConfiguration>" +
                $"<IndexTarget Organize=\"false\">{System.Security.SecurityElement.Escape(libraryRoot)}</IndexTarget>" +
                "</LibraryConfiguration>");
        var settings = new AppSettings(
            Path.Combine(
                temp.Root,
                "settings.json"));
        settings.LoadConfig(configuration);
        ReviewedFileOperationService service =
            CreateService(
                temp,
                settings);

        ReviewedFileOperationPlan plan =
            await service.PreviewAsync(new(
                [source],
                ReviewedFileOperationKind.Move,
                destination));

        Assert.False(plan.CanApply);
        Assert.Contains(
            plan.MutationPlan.Issues,
            issue =>
                issue.Code ==
                "file-operation.source-permission");
    }

    private static ReviewedFileOperationService CreateService(
        TempDirectory temp,
        AppSettings? settings = null)
    {
        settings ??= new AppSettings(
            Path.Combine(
                temp.Root,
                "settings.json"));
        var executor = new FileMutationPlanExecutor(
            new FileMutationCoordinator(),
            settings: settings);
        return new(executor, settings);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"reviewed-file-operation-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Directory(params string[] parts)
        {
            string path =
                parts.Aggregate(Root, Path.Combine);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public string File(
            string relativePath,
            string content)
        {
            string path =
                Path.Combine(Root, relativePath);
            System.IO.Directory.CreateDirectory(
                Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(
                    Root,
                    recursive: true);
            }
            catch
            {
            }
        }
    }
}
