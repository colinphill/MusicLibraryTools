using MusicFileUtilities;
using MusicLibraryTools;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace MusicLibrary.Core.Tests;

public class IngestMusicTests
{
    [Fact]
    public async Task Preview_NormalizesMultiDiscAlbumAndTrackOffsets()
    {
        using var tree = new TempTree();
        string source = tree.Dir("incoming");
        string config = tree.Config();
        string fixture = Path.Combine(AppContext.BaseDirectory, "TestFiles", "sample.flac");
        foreach (var item in new[] { ("d1t1", 1, 1), ("d1t2", 2, 1), ("d2t3", 3, 2), ("d2t4", 4, 2) })
        {
            string path = Path.Combine(source, item.Item1 + ".flac");
            File.Copy(fixture, path);
            WriteTags(path, "Album (Disc 9)", item.Item1, item.Item2, item.Item3);
        }

        var plan = await new IngestMusicService(new FakeFfmpeg()).PreviewAsync(new(source, config));

        Assert.Empty(plan.Conflicts);
        var tracks = Assert.Single(plan.Albums).Tracks.OrderBy(t => t.OriginalDiscNumber).ThenBy(t => t.TrackNumber).ToArray();
        Assert.Equal(new[] { 1, 2, 1, 2 }, tracks.Select(t => t.TrackNumber));
        Assert.All(tracks, t => Assert.Equal(2, t.TrackTotal));
        Assert.Equal(new[] { "Album (Disc 1)", "Album (Disc 1)", "Album (Disc 2)", "Album (Disc 2)" }, tracks.Select(t => t.Album));
        Assert.All(plan.Albums.Single().Outputs.Where(o => o.Kind == IngestOutputKind.CdFlac),
            o => Assert.StartsWith(tree.Path("cd"), o.DestinationPath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, plan.Files.Count);
        Assert.All(plan.Files, file =>
        {
            Assert.Contains("CD FLAC", file.Summary);
            Assert.Contains("AAC", file.Summary);
            Assert.Contains("Quarantine after successful ingest", file.Summary);
        });
    }

    [Fact]
    public async Task Apply_DeclinedDerivationStopsBeforeFfmpegOrFilesystemChanges()
    {
        using var tree = new TempTree();
        string source = tree.FileFromFixture("incoming", "one.flac", "sample.flac");
        var fake = new FakeFfmpeg();
        var plan = ManualPlan(tree, [source], requireApproval: true);

        var result = await new IngestMusicService(fake).ApplyAsync(plan,
            [new IngestApprovalDecision("album", false)]);

        Assert.True(result.Cancelled);
        Assert.Equal(0, fake.PreflightCalls);
        Assert.True(File.Exists(source));
        Assert.False(Directory.Exists(tree.Path("aac")));
    }

    [Fact]
    public async Task Apply_ParallelizesIndependentTranscodesUpToCpuLimit()
    {
        using var tree = new TempTree();
        string first = tree.FileFromFixture("incoming", "one.flac", "sample.flac");
        string second = tree.FileFromFixture("incoming", "two.flac", "sample.flac");
        var fake = new FakeFfmpeg(delay: TimeSpan.FromMilliseconds(100));
        var plan = ManualPlan(tree, [first, second], requireApproval: false);

        var result = await new IngestMusicService(fake).ApplyAsync(plan, []);

        Assert.False(result.Cancelled);
        Assert.True(result.Failed == 0,
            string.Join(Environment.NewLine, result.Albums.Select(album => album.Error)));
        Assert.Equal(Math.Min(2, Environment.ProcessorCount), fake.MaxConcurrent);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.False(Directory.Exists(tree.Path("paired", ".IngestMusic-staging")));
        Assert.False(Directory.Exists(tree.Path("aac", ".IngestMusic-staging")));
    }

    [Fact]
    public async Task Apply_PreservesMissingAlbumArtistInsteadOfWritingArtistFallback()
    {
        using var tree = new TempTree();
        string source = tree.FileFromFixture("incoming", "one.flac", "sample.flac");
        IngestPlan original = ManualPlan(tree, [source], requireApproval: false);
        List<IngestTrackPlan> tracks = original.Albums.Single().Tracks
            .Select(track => track with { AlbumArtist = null })
            .ToList();
        Dictionary<string, IngestTrackPlan> tracksByIdentity = tracks
            .ToDictionary(track => track.Identity);
        IngestAlbumPlan album = original.Albums.Single() with
        {
            Tracks = tracks,
            Outputs = original.Albums.Single().Outputs
                .Select(output => output with { Metadata = tracksByIdentity[output.Identity] })
                .ToList(),
        };
        IngestPlan plan = original with { Albums = [album] };

        IngestResult result = await new IngestMusicService(new FakeFfmpeg()).ApplyAsync(plan, []);

        Assert.Equal(0, result.Failed);
        foreach (IngestOutputPlan output in album.Outputs)
        {
            IMetadataProvider metadata = MediaFile.GetFile(output.DestinationPath).Tags.First();
            Assert.False(metadata.HasAlbumArtist);
            Assert.Equal(string.Empty, metadata.AlbumArtist);
        }
    }

    [Fact]
    public async Task Apply_UsesMutationServiceWithThePlansExplicitItunesLibrary()
    {
        using var tree = new TempTree();
        string source = tree.FileFromFixture("incoming", "one.flac", "sample.flac");
        string libraryPath = tree.Path("legacy-selected.itl");
        IngestPlan plan = ManualPlan(tree, [source], requireApproval: false);
        plan = plan with
        {
            Configuration = plan.Configuration with { ItunesLibraryPath = libraryPath },
        };
        var itunes = new RecordingItunesMutationService();

        IngestResult result = await new IngestMusicService(
            new FakeFfmpeg(), itunes: itunes).ApplyAsync(plan, []);

        Assert.Equal(0, result.Failed);
        Assert.Equal(libraryPath, itunes.LibraryPath);
        Assert.Contains(itunes.Mutations, mutation =>
            mutation.Kind == ItunesMediaMutationKind.Add &&
            mutation.CurrentPath == plan.Albums.Single().Outputs.Single(
                output => output.Kind == IngestOutputKind.Aac).DestinationPath);
        Assert.Contains(itunes.Mutations, mutation =>
            mutation.Kind == ItunesMediaMutationKind.Remove &&
            mutation.OriginalPath == source);
        Assert.True(itunes.Completed);
    }

    [Fact]
    public async Task Apply_ReportsDeterminateOutputProgress()
    {
        using var tree = new TempTree();
        string source = tree.FileFromFixture("incoming", "one.flac", "sample.flac");
        var plan = ManualPlan(tree, [source], requireApproval: false);
        var updates = new List<IngestProgress>();

        var result = await new IngestMusicService(new FakeFfmpeg()).ApplyAsync(plan, [], new InlineProgress(updates.Add));

        Assert.Equal(0, result.Failed);
        Assert.Contains(updates, update => update.Operation.StartsWith("Staged CD FLAC", StringComparison.Ordinal));
        Assert.Contains(updates, update => update.Operation.StartsWith("Staged AAC", StringComparison.Ordinal));
        Assert.Contains(updates, update => update.SourcePath == source && update.FileState == IngestFileProgressState.InProgress);
        Assert.Contains(updates, update => update.SourcePath == source && update.FileState == IngestFileProgressState.Completed);
        Assert.Equal(updates[^1].TotalItems, updates[^1].CompletedItems);
    }

    [Fact]
    public async Task Apply_DeleteDispositionRemovesCommittedSourcesInsteadOfQuarantiningThem()
    {
        using var tree = new TempTree();
        string source = tree.FileFromFixture("incoming", "one.flac", "sample.flac");
        var plan = ManualPlan(tree, [source], requireApproval: false);
        plan = plan with { Configuration = plan.Configuration with { DeleteSourcesAfterIngest = true } };

        var result = await new IngestMusicService(new FakeFfmpeg()).ApplyAsync(plan, []);

        Assert.Equal(0, result.Failed);
        Assert.False(File.Exists(source));
        string quarantine = tree.Path("incoming") + ".IngestMusic-quarantine";
        Assert.DoesNotContain(Directory.EnumerateFiles(quarantine, "*", SearchOption.AllDirectories),
            path => Path.GetFileName(path).Equals("one.flac", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Preview_RemoveNonMusicShowsSelectedDisposition()
    {
        using var tree = new TempTree();
        tree.FileFromFixture("incoming", "one.flac", "sample.flac");
        string notes = tree.TestFile("incoming", "artwork", "notes.txt");
        string packagedNotes = tree.TestFile("incoming", "bundle.itlp", "notes.txt");
        string configPath = tree.Config();
        var config = IngestMusicConfiguration.Load(configPath) with { RemoveNonMusicAfterIngest = true };
        config.Save(configPath);

        var plan = await new IngestMusicService(new FakeFfmpeg()).PreviewAsync(
            new(tree.Path("incoming"), configPath));

        var ignored = Assert.Single(plan.Files, file => file.Source == notes);
        Assert.Equal("Unsupported/non-audio", ignored.SourceType);
        Assert.Contains("Quarantine after successful ingest", ignored.Summary);
        Assert.Equal(2, plan.IgnoredFileSnapshots.Count);
        Assert.Contains(plan.IgnoredFileSnapshots, snapshot =>
            snapshot.Path == notes && snapshot.Length == new FileInfo(notes).Length &&
            snapshot.LastWriteTimeUtc == new FileInfo(notes).LastWriteTimeUtc);
        Assert.Contains(plan.IgnoredFileSnapshots, snapshot => snapshot.Path == packagedNotes);
        Assert.Contains(tree.Path("incoming", "artwork"), plan.SourceDirectories);
        Assert.Contains(tree.Path("incoming", "bundle.itlp"), plan.SourceDirectories);
    }

    [Fact]
    public async Task Apply_RemoveNonMusicQuarantinesFilesAndRemovesEmptiedSourceFolders()
    {
        using var tree = new TempTree();
        string source = tree.FileFromFixture(Path.Combine("incoming", "album"), "one.flac", "sample.flac");
        string notes = tree.TestFile("incoming", "artwork", "notes.txt");
        string empty = tree.Dir("incoming", "empty");
        var plan = WithNonMusicCleanup(ManualPlan(tree, [source], requireApproval: false), tree, notes);

        var result = await new IngestMusicService(new FakeFfmpeg()).ApplyAsync(plan, []);

        Assert.Equal(0, result.Failed);
        Assert.False(File.Exists(notes));
        Assert.False(Directory.Exists(tree.Path("incoming", "album")));
        Assert.False(Directory.Exists(tree.Path("incoming", "artwork")));
        Assert.False(Directory.Exists(empty));
        string quarantine = tree.Path("incoming") + ".IngestMusic-quarantine";
        Assert.Single(Directory.EnumerateFiles(quarantine, "notes.txt", SearchOption.AllDirectories));
        Assert.Single(Directory.EnumerateDirectories(quarantine, "empty", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Apply_RemoveNonMusicDeletesFilesWhenDeleteDispositionIsSelected()
    {
        using var tree = new TempTree();
        string source = tree.FileFromFixture(Path.Combine("incoming", "album"), "one.flac", "sample.flac");
        string notes = tree.TestFile("incoming", "artwork", "notes.txt");
        var plan = WithNonMusicCleanup(ManualPlan(tree, [source], requireApproval: false), tree, notes);
        plan = plan with { Configuration = plan.Configuration with { DeleteSourcesAfterIngest = true } };

        var result = await new IngestMusicService(new FakeFfmpeg()).ApplyAsync(plan, []);

        Assert.Equal(0, result.Failed);
        Assert.False(File.Exists(notes));
        string quarantine = tree.Path("incoming") + ".IngestMusic-quarantine";
        Assert.Empty(Directory.EnumerateFiles(quarantine, "notes.txt", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(tree.Path("incoming", "artwork")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_RemoveNonMusicWithoutMusicFilesRunsSelectedDisposition(bool deleteSources)
    {
        using var tree = new TempTree();
        string notes = tree.TestFile("incoming", "artwork", "notes.txt");
        string empty = tree.Dir("incoming", "empty");
        string configPath = tree.Config();
        var config = IngestMusicConfiguration.Load(configPath) with
        {
            RemoveNonMusicAfterIngest = true,
            DeleteSourcesAfterIngest = deleteSources,
        };
        config.Save(configPath);
        var fake = new FakeFfmpeg();
        var service = new IngestMusicService(fake);

        var plan = await service.PreviewAsync(new(tree.Path("incoming"), configPath));
        var result = await service.ApplyAsync(plan, []);

        Assert.True(plan.CanApply);
        Assert.Empty(plan.Albums);
        Assert.Empty(plan.Conflicts);
        Assert.False(result.Cancelled);
        Assert.Empty(result.Albums);
        Assert.Equal(0, fake.PreflightCalls);
        Assert.False(File.Exists(notes));
        Assert.False(Directory.Exists(tree.Path("incoming", "artwork")));
        Assert.False(Directory.Exists(empty));
        string quarantine = tree.Path("incoming") + ".IngestMusic-quarantine";
        if (deleteSources)
            Assert.Empty(Directory.EnumerateFiles(quarantine, "notes.txt", SearchOption.AllDirectories));
        else
        {
            Assert.Single(Directory.EnumerateFiles(quarantine, "notes.txt", SearchOption.AllDirectories));
            Assert.Single(Directory.EnumerateDirectories(quarantine, "empty", SearchOption.AllDirectories));
        }
    }

    [Fact]
    public async Task Apply_RemoveNonMusicCanCleanAnEmptyFolderWithoutMusicOrOtherFiles()
    {
        using var tree = new TempTree();
        string empty = tree.Dir("incoming", "empty");
        string configPath = tree.Config();
        var config = IngestMusicConfiguration.Load(configPath) with { RemoveNonMusicAfterIngest = true };
        config.Save(configPath);
        var fake = new FakeFfmpeg();
        var service = new IngestMusicService(fake);

        var plan = await service.PreviewAsync(new(tree.Path("incoming"), configPath));
        var result = await service.ApplyAsync(plan, []);

        Assert.True(plan.CanApply);
        Assert.False(result.Cancelled);
        Assert.Equal(0, fake.PreflightCalls);
        Assert.False(Directory.Exists(empty));
        string quarantine = tree.Path("incoming") + ".IngestMusic-quarantine";
        Assert.Single(Directory.EnumerateDirectories(quarantine, "empty", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Preview_WithoutMusicOrEnabledCleanupCannotApply()
    {
        using var tree = new TempTree();
        tree.TestFile("incoming", "notes.txt");

        var plan = await new IngestMusicService(new FakeFfmpeg()).PreviewAsync(
            new(tree.Path("incoming"), tree.Config()));

        Assert.False(plan.CanApply);
    }

    [Fact]
    public async Task RecipeCopy_PreservesSourceSidecarsAndTags_WithoutFfmpeg()
    {
        using var tree = new TempTree();
        string source = tree.FileFromFixture("incoming", "original.flac", "sample.flac");
        string sidecar = tree.TestFile("incoming", "booklet.pdf");
        (string configPath, string destination, _) = CreateRecipeLibrary(tree);
        var ffmpeg = new FakeFfmpeg();
        var service = new IngestMusicService(ffmpeg);

        IngestPlan plan = await service.PreviewAsync(new(tree.Path("incoming"), configPath));

        IngestOutputPlan output = Assert.Single(Assert.Single(plan.Albums).Outputs);
        Assert.Equal(IngestOutputKind.Recipe, output.Kind);
        Assert.Equal(LibraryIngestAction.Copy, output.Action);
        Assert.StartsWith(destination, output.DestinationPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(plan.Configuration.PolicySnapshot?.Fingerprint, plan.PolicyFingerprint);
        Assert.DoesNotContain(plan.Files, file => file.Source == sidecar &&
            file.Summary.Contains("Delete", StringComparison.OrdinalIgnoreCase));

        IngestResult result = await service.ApplyAsync(plan, []);

        Assert.True(result.Failed == 0,
            string.Join(Environment.NewLine, result.Albums.Select(album => album.Error)));
        Assert.Equal(0, ffmpeg.PreflightCalls);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(sidecar));
        Assert.True(File.Exists(output.DestinationPath));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(output.DestinationPath));
    }

    [Fact]
    public async Task RecipeIngestStagesFrontCoverSidecarInsideReviewedTransaction()
    {
        using var tree = new TempTree();
        string source = tree.FileFromFixture("incoming", "artwork.flac", "sample.flac");
        WriteArtwork(source,
        [
            new ArtworkImage(ID3v2Util.APICType.FrontCover, "image/png", "front",
                MakePng(40, 20, Color.Red)),
            new ArtworkImage(ID3v2Util.APICType.BackCover, "image/png", "back",
                MakePng(20, 40, Color.Blue)),
        ]);
        var policy = new LibraryArtworkPolicy(
            LibraryArtworkStorage.Sidecar,
            LibraryArtworkRoleSelection.FrontCoverOnly,
            LibraryArtworkEncoding.Png,
            MaximumDimension: 16,
            MaximumEncodedBytes: 1_000_000,
            JpegQuality: 85,
            SidecarFileNameTemplate: "{Role}{Extension}");
        (string configPath, _, _) = CreateRecipeLibrary(
            tree, policy, grantArtworkPermission: true);
        var service = new IngestMusicService(new FakeFfmpeg());

        IngestPlan plan = await service.PreviewAsync(
            new(tree.Path("incoming"), configPath));

        IngestOutputPlan output = Assert.Single(Assert.Single(plan.Albums).Outputs);
        IngestArtworkArtifactPlan artifact = Assert.Single(output.ArtworkArtifacts);
        Assert.Equal("cover", artifact.Role);
        Assert.Equal("image/png", artifact.MimeType);
        Assert.Equal(16, artifact.Width);
        Assert.EndsWith("cover.png", artifact.SidecarDestination,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(artifact.SidecarDestination!,
            Assert.Single(plan.Files).Summary, StringComparison.Ordinal);

        IngestResult result = await service.ApplyAsync(plan, []);

        Assert.Equal(0, result.Failed);
        Assert.True(File.Exists(output.DestinationPath));
        Assert.True(File.Exists(artifact.SidecarDestination));
        Assert.Empty(MediaFile.GetFile(output.DestinationPath, readOnly: true).Tags
            .SelectMany(tag => tag.GetImageMetadata()));
        using Image sidecar = Image.Load(artifact.SidecarDestination!);
        Assert.Equal(16, sidecar.Width);
        Assert.Equal(8, sidecar.Height);
    }

    [Fact]
    public async Task RecipeIngestArtworkNoneRemovesEmbeddedImagesWithoutArtworkPermission()
    {
        using var tree = new TempTree();
        string source = tree.FileFromFixture("incoming", "artwork.flac", "sample.flac");
        WriteArtwork(source,
        [
            new ArtworkImage(ID3v2Util.APICType.FrontCover, "image/png", "front",
                MakePng(16, 16, Color.Red)),
        ]);
        LibraryArtworkPolicy policy = LibraryProfilePresets.Create(
            LibraryProfilePreset.Custom).Artwork;
        (string configPath, _, _) = CreateRecipeLibrary(tree, policy);
        var service = new IngestMusicService(new FakeFfmpeg());

        IngestPlan plan = await service.PreviewAsync(
            new(tree.Path("incoming"), configPath));
        IngestOutputPlan output = Assert.Single(Assert.Single(plan.Albums).Outputs);

        Assert.Empty(output.ArtworkArtifacts);
        Assert.Empty(plan.Conflicts);
        IngestResult result = await service.ApplyAsync(plan, []);
        Assert.Equal(0, result.Failed);
        Assert.Empty(MediaFile.GetFile(output.DestinationPath, readOnly: true).Tags
            .SelectMany(tag => tag.GetImageMetadata()));
    }

    [Fact]
    public async Task RecipeArtworkTransferRequiresDestinationArtworkPermission()
    {
        using var tree = new TempTree();
        string source = tree.FileFromFixture("incoming", "artwork.flac", "sample.flac");
        WriteArtwork(source,
        [
            new ArtworkImage(ID3v2Util.APICType.FrontCover, "image/png", "front",
                MakePng(16, 16, Color.Red)),
        ]);
        LibraryArtworkPolicy policy = LibraryProfilePresets.Create(
            LibraryProfilePreset.ArtistAlbum).Artwork;
        (string configPath, _, _) = CreateRecipeLibrary(tree, policy);

        IngestPlan plan = await new IngestMusicService(new FakeFfmpeg()).PreviewAsync(
            new(tree.Path("incoming"), configPath));

        Assert.Contains(plan.Conflicts, conflict => conflict.Message.Contains(
            "does not permit artwork writes", StringComparison.OrdinalIgnoreCase));
        Assert.False(plan.CanApply);
    }

    [Fact]
    public async Task GenericDiscPolicy_PreservesAlbumTitleAndDiscTag()
    {
        using var tree = new TempTree();
        string source = tree.FileFromFixture("incoming", "disc-track.flac", "sample.flac");
        WriteTags(source, "Edition (Disc 9)", "Disc track", 3, 2);
        (string configPath, _, _) = CreateRecipeLibrary(tree);

        IngestPlan plan = await new IngestMusicService(new FakeFfmpeg())
            .PreviewAsync(new(tree.Path("incoming"), configPath));

        IngestTrackPlan track = Assert.Single(Assert.Single(plan.Albums).Tracks);
        Assert.Equal("Edition (Disc 9)", track.Album);
        Assert.Equal(2, track.OriginalDiscNumber);
        Assert.Equal(3, track.TrackNumber);
        Assert.True(Assert.Single(Assert.Single(plan.Albums).Outputs).PreserveDiscTags);
    }

    [Fact]
    public async Task GenericRecipeUsesNamingFallbacksForMissingCoreTags()
    {
        using var tree = new TempTree();
        string source = tree.FileFromFixture("incoming", "untagged.flac", "sample.flac");
        RemoveCoreTags(source);
        (string configPath, string destination, EditableLibraryConfig editable) =
            CreateRecipeLibrary(tree);
        int profileIndex = editable.Profiles.FindIndex(profile =>
            profile.Id == editable.ActiveProfileId);
        LibraryProfile profile = editable.Profiles[profileIndex];
        editable.Profiles[profileIndex] = profile with
        {
            Naming = profile.Naming with
            {
                DirectoryTemplate = "{AlbumArtist}/{Album}",
                FileNameTemplate = "[{Track} ]{Title}{Extension}",
            },
        };
        editable.Save(configPath);
        var service = new IngestMusicService(new FakeFfmpeg());

        IngestPlan generic = await service.PreviewAsync(
            new(tree.Path("incoming"), configPath));

        Assert.Empty(generic.Conflicts);
        IngestTrackPlan track = Assert.Single(Assert.Single(generic.Albums).Tracks);
        Assert.False(track.HadTrackNumber);
        Assert.Equal("", track.Artist);
        Assert.Equal("", track.Album);
        Assert.Equal("", track.Title);
        Assert.Equal(
            Path.Combine(destination, "Unknown Artist", "Unknown Album", "Untitled.flac"),
            Assert.Single(Assert.Single(generic.Albums).Outputs).DestinationPath);

        IngestPlan legacy = await service.PreviewAsync(
            new(tree.Path("incoming"), tree.Config()));
        Assert.Contains(legacy.Conflicts, conflict => conflict.Message.Contains(
            "required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Apply_RejectsRecipePreviewAfterPolicyChanges()
    {
        using var tree = new TempTree();
        string source = tree.FileFromFixture("incoming", "one.flac", "sample.flac");
        (string configPath, _, EditableLibraryConfig editable) = CreateRecipeLibrary(tree);
        var service = new IngestMusicService(new FakeFfmpeg());
        IngestPlan plan = await service.PreviewAsync(new(tree.Path("incoming"), configPath));
        int index = editable.Profiles.FindIndex(profile => profile.Id == editable.ActiveProfileId);
        editable.Profiles[index] = editable.Profiles[index] with
        {
            Quality = new LibraryQualityPolicy(96_000, 32),
        };
        editable.Save(configPath);

        IngestResult result = await service.ApplyAsync(plan, []);

        Assert.True(result.Cancelled);
        Assert.Contains("policy changed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Apply_RemoveNonMusicRejectsAFileChangedSincePreview()
    {
        using var tree = new TempTree();
        string source = tree.FileFromFixture("incoming", "one.flac", "sample.flac");
        string notes = tree.TestFile("incoming", "notes.txt");
        var plan = WithNonMusicCleanup(ManualPlan(tree, [source], requireApproval: false), tree, notes);
        System.IO.File.AppendAllText(notes, "changed");
        var ffmpeg = new FakeFfmpeg();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new IngestMusicService(ffmpeg).ApplyAsync(plan, []));

        Assert.Contains("Source changed since preview", error.Message);
        Assert.Equal(0, ffmpeg.PreflightCalls);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(notes));
    }

    [Fact]
    public void Configuration_RejectsMissingDestination()
    {
        using var tree = new TempTree();
        string path = tree.Path("bad.xml");
        File.WriteAllText(path, "<IngestMusicConfiguration><FfmpegPath>ffmpeg</FfmpegPath></IngestMusicConfiguration>");
        Assert.Throws<InvalidDataException>(() => IngestMusicConfiguration.Load(path));
    }

    [Fact]
    public void Configuration_SaveRoundTripsAllEditableValues()
    {
        using var tree = new TempTree();
        string path = tree.Path("saved.xml");
        var expected = new IngestMusicConfiguration
        {
            FfmpegPath = "custom-ffmpeg", AacDestination = tree.Path("aac-out"),
            CdDestination = tree.Path("cd-out"), PairedCdDestination = tree.Path("paired-out"),
            HighResolutionDestination = tree.Path("hires-out"), LengthLimit = 180,
            DiscNumLengthLimit = 160, AacEncoder = "libfdk_aac", AacBitrateKbps = 256,
            DeleteSourcesAfterIngest = true, RemoveNonMusicAfterIngest = true,
            ItunesLibraryPath = tree.Path("library.itl"),
        };

        expected.Save(path);
        var actual = IngestMusicConfiguration.Load(path);

        Assert.Equal(expected, actual);
    }

    private static IngestPlan WithNonMusicCleanup(IngestPlan plan, TempTree tree, params string[] files)
    {
        var snapshots = files.Select(path =>
        {
            var file = new FileInfo(path);
            return new IngestFileSnapshot(file.FullName, file.Length, file.LastWriteTimeUtc);
        }).ToList();
        return plan with
        {
            Configuration = plan.Configuration with { RemoveNonMusicAfterIngest = true },
            IgnoredFiles = files,
            IgnoredFileSnapshots = snapshots,
            SourceDirectories = Directory.EnumerateDirectories(tree.Path("incoming"), "*", SearchOption.AllDirectories).ToList(),
        };
    }

    private static (string ConfigPath, string Destination, EditableLibraryConfig Editable)
        CreateRecipeLibrary(
            TempTree tree,
            LibraryArtworkPolicy? artworkPolicy = null,
            bool grantArtworkPermission = false)
    {
        string destination = tree.Path("recipe-output");
        var editable = EditableLibraryConfig.CreateNew();
        var root = new IndexTargetEntry
        {
            Target = destination,
            ProfileId = "copy-recipe",
            Permissions = LibraryRootPermissions.IngestOutput |
                (grantArtworkPermission ? LibraryRootPermissions.WriteArtwork :
                    LibraryRootPermissions.None),
            Organize = false,
        };
        LibraryProfile custom = LibraryProfilePresets.Create(
            LibraryProfilePreset.Custom, "copy-recipe", "Preserving copy recipe");
        var recipe = new LibraryIngestRecipe(
            "copy-flac", "Copy FLAC", true, [".flac"], true,
            null, null, null, false, LibraryIngestAction.Copy,
            root.Id, LibraryIngestRole.None, ".flac", "flac", null,
            null, null, null, null, custom.Id, true, true,
            LibraryPathCollisionPolicy.Stop);
        custom = custom with
        {
            DefaultRootPermissions = LibraryRootPermissions.IngestOutput,
            Naming = custom.Naming with
            {
                DirectoryTemplate = "{AlbumArtist}/{Album}",
                FileNameTemplate = "{OriginalName}{Extension}",
            },
            Ingest = new LibraryIngestPolicy(
                true, LibrarySourceDisposition.Preserve, true, [recipe]),
            Artwork = artworkPolicy ?? custom.Artwork,
        };
        editable.Profiles.Add(custom);
        editable.ActiveProfileId = custom.Id;
        editable.IndexTargets.Add(root);
        string configPath = tree.Path("recipe-library.xml");
        editable.Save(configPath);
        return (configPath, destination, editable);
    }

    private static byte[] MakePng(int width, int height, Color color)
    {
        using var image = new Image<Rgba32>(width, height);
        image.Mutate(context => context.BackgroundColor(color));
        using var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());
        return stream.ToArray();
    }

    private static void WriteArtwork(
        string path,
        IReadOnlyList<ArtworkImage> images)
    {
        IMediaFile media = MediaFile.GetFile(path);
        IArtworkWriter writer = media as IArtworkWriter ??
            media.Tags.OfType<IArtworkWriter>().First();
        writer.SetImages(images);
        media.SaveTags();
    }

    private static IngestPlan ManualPlan(TempTree tree, IReadOnlyList<string> sources, bool requireApproval)
    {
        var config = IngestMusicConfiguration.Load(tree.Config());
        var tracks = sources.Select((path, i) => new IngestTrackPlan
        {
            Identity = $"track{i}", SourcePath = path, Title = $"Track {i + 1}", Artist = "Artist",
            AlbumArtist = "Artist", Album = "Album", TrackNumber = i + 1, TrackTotal = sources.Count,
            OriginalDiscNumber = 1, SampleRate = 96000, BitsPerSample = 24, Channels = 2,
            DurationInSeconds = 0, IsAlac = false, IsHighResolution = true,
        }).ToList();
        var outputs = tracks.SelectMany(t => new[]
        {
            new IngestOutputPlan { Identity = t.Identity, Kind = IngestOutputKind.CdFlac, Metadata = t,
                SourcePath = t.SourcePath, DestinationPath = tree.Path("paired", t.Identity + ".flac"), DeriveCd = true },
            new IngestOutputPlan { Identity = t.Identity, Kind = IngestOutputKind.Aac, Metadata = t,
                SourcePath = t.SourcePath, DestinationPath = tree.Path("aac", t.Identity + ".m4a") },
        }).ToList();
        var snapshots = sources.Select(p => { var f = new FileInfo(p); return new IngestFileSnapshot(p, f.Length, f.LastWriteTimeUtc); }).ToList();
        var album = new IngestAlbumPlan { Key = "album", Display = "Artist — Album", Tracks = tracks, Outputs = outputs,
            Sources = snapshots, HasHighResolution = true };
        return new IngestPlan
        {
            Request = new IngestRequest(tree.Path("incoming"), tree.Path("config.xml")), Configuration = config,
            Albums = [album], Files = [], Conflicts = [], IgnoredFiles = [],
            RequiredApprovals = requireApproval ? [new IngestApprovalItem("album", album.Display, tracks.Select(t => t.Title).ToList())] : [],
        };
    }

    private static void WriteTags(string path, string album, string title, int track, int disc)
    {
        var media = MediaFile.GetFile(path);
        var writer = Assert.IsAssignableFrom<IMetadataWriter>(media);
        writer.SetField(TagFields.Album, album);
        writer.SetField(TagFields.Title, title);
        writer.SetField(TagFields.TrackNumber, track.ToString());
        writer.SetField(TagFields.DiscNumber, disc.ToString());
        writer.Save();
    }

    private static void RemoveCoreTags(string path)
    {
        var media = MediaFile.GetFile(path);
        var writer = Assert.IsAssignableFrom<IMetadataWriter>(media);
        writer.RemoveField(TagFields.Artist);
        writer.RemoveField(TagFields.AlbumArtist);
        writer.RemoveField(TagFields.Album);
        writer.RemoveField(TagFields.Title);
        writer.RemoveField(TagFields.TrackNumber);
        writer.RemoveField(TagFields.TotalTracks);
        writer.Save();
    }

    private sealed class FakeFfmpeg(TimeSpan? delay = null) : IFfmpegRunner
    {
        private int _active;
        public int MaxConcurrent { get; private set; }
        public int PreflightCalls { get; private set; }
        public Task PreflightAsync(string executable, string requiredEncoder, CancellationToken ct = default) { PreflightCalls++; return Task.CompletedTask; }
        public Task ConvertAlacToFlacAsync(string executable, string input, string output, CancellationToken ct = default) => Copy("sample.flac", output, ct);
        public Task DeriveCdFlacAsync(string executable, string input, string output, CancellationToken ct = default) => Copy("sample.flac", output, ct);
        public Task EncodeAacAsync(string executable, string encoder, int bitrateKbps, string input, string output, CancellationToken ct = default) => Copy("sample_aac.m4a", output, ct);
        public Task<string> ComputeDecodedAudioHashAsync(string executable, string input, CancellationToken ct = default) => Task.FromResult("SHA256=test");
        private async Task Copy(string fixture, string output, CancellationToken ct)
        {
            int active = Interlocked.Increment(ref _active);
            lock (this) MaxConcurrent = Math.Max(MaxConcurrent, active);
            try
            {
                if (delay is { } d) await Task.Delay(d, ct);
                File.Copy(Path.Combine(AppContext.BaseDirectory, "TestFiles", fixture), output);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class InlineProgress(Action<IngestProgress> report) : IProgress<IngestProgress>
    {
        public void Report(IngestProgress value) => report(value);
    }

    private sealed class RecordingItunesMutationService :
        IItunesMediaMutationService,
        IItunesMediaMutationSession
    {
        public string? LibraryPath { get; private set; }
        public string? MediaFolder => null;
        public bool Active => true;
        public bool Completed { get; private set; }
        public IReadOnlyList<ItunesMediaMutation> Mutations { get; private set; } = [];

        public Task<IItunesMediaMutationSession> BeginAsync(
            IReadOnlyCollection<string> candidatePaths,
            bool backupFiles,
            CancellationToken ct = default) =>
            throw new Xunit.Sdk.XunitException(
                "Ingest must select the ITL from its resolved plan.");

        public Task<IItunesMediaMutationSession> BeginAsync(
            IReadOnlyCollection<string> candidatePaths,
            bool backupFiles,
            string? libraryPath,
            CancellationToken ct = default)
        {
            LibraryPath = libraryPath;
            return Task.FromResult<IItunesMediaMutationSession>(this);
        }

        public Task<ItunesMediaReconciliationResult> ReconcileAsync(
            IReadOnlyCollection<ItunesMediaIndexedFile> indexedFiles,
            IReadOnlyCollection<string> indexedRoots,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ItunesMediaMutationResult> CommitAsync(
            IReadOnlyList<ItunesMediaMutation> mutations,
            CancellationToken ct = default)
        {
            Mutations = mutations;
            return Task.FromResult(new ItunesMediaMutationResult(
                true, 0, 0, 1, 1, LibraryPath, null, null, []));
        }

        public Task CompleteAsync(CancellationToken ct = default)
        {
            Completed = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TempTree : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ingest-tests-" + Guid.NewGuid().ToString("N"));
        public TempTree() => Directory.CreateDirectory(_root);
        public string Path(params string[] parts) => System.IO.Path.Combine([_root, .. parts]);
        public string Dir(params string[] parts) { string p = Path(parts); Directory.CreateDirectory(p); return p; }
        public string FileFromFixture(string dir, string name, string fixture)
        {
            string path = System.IO.Path.Combine(Dir(dir), name);
            File.Copy(System.IO.Path.Combine(AppContext.BaseDirectory, "TestFiles", fixture), path);
            return path;
        }
        public string TestFile(params string[] parts)
        {
            string path = Path(parts);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, "test");
            return path;
        }
        public string Config()
        {
            string path = Path("config.xml");
            if (!File.Exists(path)) File.WriteAllText(path, $"""
                <IngestMusicConfiguration>
                  <FfmpegPath>ffmpeg</FfmpegPath>
                  <AacDestination>{Path("aac")}</AacDestination>
                  <CdDestination>{Path("cd")}</CdDestination>
                  <PairedCdDestination>{Path("paired")}</PairedCdDestination>
                  <HighResolutionDestination>{Path("hires")}</HighResolutionDestination>
                  <LengthLimit>255</LengthLimit><DiscNumLengthLimit>255</DiscNumLengthLimit>
                  <AacEncoder>libfdk_aac</AacEncoder><AacBitrateKbps>256</AacBitrateKbps>
                </IngestMusicConfiguration>
                """);
            return path;
        }
        public void Dispose() { try { Directory.Delete(_root, true); } catch { } try { Directory.Delete(_root + ".IngestMusic-quarantine", true); } catch { } }
    }
}
