using Microsoft.Extensions.DependencyInjection;
using MetadataCaching;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class ConfiguredExportServiceTests
{
    [Fact]
    public async Task EntireLibraryPreviewAndApplyCopyAndQuarantineThroughTransport()
    {
        using var workspace = new TestWorkspace();
        string source = workspace.Media("source", "song.flac");
        string stale = workspace.Text("target", "stale.txt", "stale");
        TestLibrary library = workspace.Library(
            ExportSelectionPolicy.EntireLibrary,
            reconciliation: new(ExportExtraFileDisposition.Quarantine,
                ReplaceChangedFiles: true, MaximumRemovals: 5));
        ConfiguredExportService service = CreateService(library, [source]);
        var progress = new List<OperationProgress>();

        ConfiguredExportPlan plan = await service.PreviewAsync(
            new("portable", library.ConfigurationPath),
            new SynchronousProgress<OperationProgress>(
                progress.Add),
            ct: TestContext.Current.CancellationToken);

        Assert.True(plan.CanApply, string.Join(Environment.NewLine,
            plan.Issues.Select(issue => issue.Message)));
        Assert.Equal(OperationPhase.Completed, progress[^1].Phase);
        Assert.Equal(library.Configuration.LibraryId, plan.LibraryId);
        Assert.Equal(library.Configuration.PolicySnapshot.Fingerprint,
            plan.LibraryFingerprint);
        Assert.Equal(plan.Profile!.Fingerprint, plan.ProfileFingerprint);
        ConfiguredExportFile planned = Assert.Single(plan.Files);
        Assert.Equal(FileMutationKind.Copy, planned.Mutation);
        Assert.StartsWith(library.TargetRoot, planned.DestinationPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(plan.TransportPlan!.MutationPlan.Actions,
            action => action.Kind == FileMutationKind.Quarantine &&
                      action.SourcePath == stale);

        ConfiguredExportResult result = await service.ApplyAsync(
            plan, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Mutations.Copied);
        Assert.Equal(1, result.Mutations.Quarantined);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(planned.DestinationPath));
        Assert.False(File.Exists(stale));
        Assert.NotNull(result.Mutations.JournalPath);
    }

    [Fact]
    public async Task ExplicitTracksWithPreservedLayoutSelectOnlyRequestedCachedPath()
    {
        using var workspace = new TestWorkspace();
        string selected = workspace.Media("source", "disc-one", "selected.flac");
        string omitted = workspace.Media("source", "disc-two", "omitted.flac");
        var selection = new ExportSelectionPolicy(
            ExportSelectionKind.ExplicitTracks, [selected]);
        TestLibrary library = workspace.Library(selection,
            naming: new(PreserveSourceLayout: true));
        ConfiguredExportService service = CreateService(library, [selected, omitted]);

        ConfiguredExportPlan plan = await service.PreviewAsync(
            new("portable", library.ConfigurationPath),
            ct: TestContext.Current.CancellationToken);

        ConfiguredExportFile file = Assert.Single(plan.Files);
        Assert.Equal(selected, file.SourcePath);
        Assert.Equal(Path.Combine(library.TargetRoot, "disc-one", "selected.flac"),
            file.DestinationPath);
        Assert.DoesNotContain(plan.Files, item => item.SourcePath == omitted);
    }

    [Fact]
    public async Task PreviewObservesCancellationBetweenSelectedFiles()
    {
        using var workspace = new TestWorkspace();
        string first = workspace.Media("source", "one.flac");
        string second = workspace.Media("source", "two.flac");
        TestLibrary library = workspace.Library(
            ExportSelectionPolicy.EntireLibrary);
        ConfiguredExportService service =
            CreateService(library, [first, second]);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.PreviewAsync(
                new("portable", library.ConfigurationPath),
                new SynchronousProgress<OperationProgress>(
                    report =>
                    {
                        if (report.CurrentPath is not null)
                            cancellation.Cancel();
                    }),
                cancellation.Token));
    }

    [Fact]
    public async Task SelfContainedNamingOverridesDoNotInheritLegacyNamingBranches()
    {
        using var workspace = new TestWorkspace();
        string source = workspace.Media("source", "song.flac");
        WriteTrackTags(source, "Album Artist", "Album (HiRes)", "A $ong", 1, 1);
        LibraryExportProfile profile = TestWorkspace.Profile(
            workspace.Path("target"), ExportSelectionPolicy.EntireLibrary,
            naming: new(
                FolderTemplate: "Portable/{AlbumArtist}/{Album}",
                FileNameTemplate: "{Track:00} {Title}{Extension}",
                CollisionPolicy: LibraryPathCollisionPolicy.Stop));
        TestLibrary library = workspace.Library(profile,
            activeProfileId: LibraryProfilePresets.LegacyId);
        ConfiguredExportService service = CreateService(library, [source]);

        ConfiguredExportPlan plan = await service.PreviewAsync(
            new(profile.Id, library.ConfigurationPath),
            ct: TestContext.Current.CancellationToken);

        Assert.True(plan.CanApply, string.Join(Environment.NewLine,
            plan.Issues.Select(issue => issue.Message)));
        string relative = Path.GetRelativePath(library.TargetRoot,
            Assert.Single(plan.Files).DestinationPath);
        Assert.Equal(Path.Combine("Portable", "Album Artist", "Album (HiRes)",
            "01 A $ong.flac"), relative);
    }

    [Fact]
    public async Task FlattenContinuousNumbersUnequalDiscsByActualAlbumSlots()
    {
        using var workspace = new TestWorkspace();
        string discOneTrackOne = workspace.Media("source", "d1t1.flac");
        string discOneTrackTwo = workspace.Media("source", "d1t2.flac");
        string discTwoTrackOne = workspace.Media("source", "d2t1.flac");
        WriteTrackTags(discOneTrackOne, "Album Artist", "Unequal Album", "D1T1", 1, 1);
        WriteTrackTags(discOneTrackTwo, "Album Artist", "Unequal Album", "D1T2", 2, 1);
        WriteTrackTags(discTwoTrackOne, "Album Artist", "Unequal Album", "D2T1", 1, 2);

        LibraryProfile flattened = LibraryProfilePresets.Create(
            LibraryProfilePreset.Custom, "flattened-export", "Flattened export") with
        {
            Disc = new(LibraryDiscStrategy.FlattenContinuous,
                LibraryTrackTotalScope.PerDisc, InferAlbumSuffix: false),
        };
        LibraryExportProfile profile = TestWorkspace.Profile(
            workspace.Path("target"), ExportSelectionPolicy.EntireLibrary,
            naming: new(LibraryProfileId: flattened.Id,
                CollisionPolicy: LibraryPathCollisionPolicy.Stop));
        TestLibrary library = workspace.Library(profile,
            additionalProfiles: [flattened]);
        ConfiguredExportService service = CreateService(library,
            [discOneTrackOne, discOneTrackTwo, discTwoTrackOne]);

        ConfiguredExportPlan plan = await service.PreviewAsync(
            new(profile.Id, library.ConfigurationPath),
            ct: TestContext.Current.CancellationToken);

        Assert.True(plan.CanApply, string.Join(Environment.NewLine,
            plan.Issues.Select(issue => issue.Message)));
        ConfiguredExportFile third = Assert.Single(plan.Files,
            file => file.SourcePath == discTwoTrackOne);
        Assert.Equal("03 D2T1.flac", Path.GetFileName(third.DestinationPath));
    }

    [Fact]
    public async Task DeleteReconciliationStagesUnderRecoveryAndHonorsRemovalLimit()
    {
        using var workspace = new TestWorkspace();
        string stale = workspace.Text("target", "stale.txt", "stale");
        TestLibrary blockedLibrary = workspace.Library(
            ExportSelectionPolicy.EntireLibrary,
            reconciliation: new(ExportExtraFileDisposition.Delete,
                MaximumRemovals: 0),
            profileId: "blocked-delete");
        ConfiguredExportService blockedService = CreateService(blockedLibrary, []);

        ConfiguredExportPlan blocked = await blockedService.PreviewAsync(
            new("blocked-delete", blockedLibrary.ConfigurationPath),
            ct: TestContext.Current.CancellationToken);

        Assert.False(blocked.CanApply);
        Assert.Contains(blocked.Issues, issue => issue.Code == "export-removal-limit");
        FileMutationAction delete = Assert.Single(
            blocked.TransportPlan!.MutationPlan.Actions,
            action => action.Kind == FileMutationKind.Delete);
        Assert.StartsWith(blocked.TransportPlan.MutationPlan.RecoveryRoot,
            delete.DestinationPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.DirectorySeparatorChar + "deleted" + Path.DirectorySeparatorChar,
            delete.DestinationPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(stale));

        TestLibrary allowedLibrary = workspace.Library(
            ExportSelectionPolicy.EntireLibrary,
            reconciliation: new(ExportExtraFileDisposition.Delete,
                MaximumRemovals: 1),
            profileId: "allowed-delete");
        ConfiguredExportService allowedService = CreateService(allowedLibrary, []);
        ConfiguredExportPlan allowed = await allowedService.PreviewAsync(
            new("allowed-delete", allowedLibrary.ConfigurationPath),
            ct: TestContext.Current.CancellationToken);

        ConfiguredExportResult result = await allowedService.ApplyAsync(
            allowed, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Mutations.Deleted);
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public async Task UnsupportedPolicyDimensionsAreExplicitPreviewBlockers()
    {
        using var workspace = new TestWorkspace();
        LibraryExportProfile unsupported = TestWorkspace.Profile(
            workspace.Path("target"),
            new(ExportSelectionKind.SavedView, [], "recent")) with
        {
            Transform = new(ExportTransformMode.Transcode, Codec: "flac"),
            Artwork = new(ExportArtworkMode.Sidecar),
            Playlists = new(Enabled: true),
            Reconciliation = new(RemoveEmptyDirectories: true),
        };
        TestLibrary library = workspace.Library(unsupported);
        ConfiguredExportService service = CreateService(library, []);

        ConfiguredExportPlan plan = await service.PreviewAsync(
            new(unsupported.Id, library.ConfigurationPath),
            ct: TestContext.Current.CancellationToken);

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Issues, issue => issue.Code == "export-selection-unsupported");
        Assert.Contains(plan.Issues, issue => issue.Code == "export-transform-unsupported");
        Assert.Contains(plan.Issues, issue => issue.Code == "export-artwork-unsupported");
        Assert.Contains(plan.Issues, issue => issue.Code == "export-playlists-unsupported");
        Assert.Contains(plan.Issues,
            issue => issue.Code == "export-empty-directories-unsupported");
    }

    [Fact]
    public async Task ApplyRejectsLibraryPolicyChangedAfterPreview()
    {
        using var workspace = new TestWorkspace();
        string source = workspace.Media("source", "song.flac");
        TestLibrary library = workspace.Library(ExportSelectionPolicy.EntireLibrary);
        ConfiguredExportService service = CreateService(library, [source]);
        ConfiguredExportPlan plan = await service.PreviewAsync(
            new("portable", library.ConfigurationPath),
            ct: TestContext.Current.CancellationToken);
        Assert.True(plan.CanApply);

        EditableLibraryConfig changed = EditableLibraryConfig.Load(library.ConfigurationPath);
        int index = changed.ExportProfiles.FindIndex(profile => profile.Id == "portable");
        changed.ExportProfiles[index] = changed.ExportProfiles[index] with
        {
            Reconciliation = changed.ExportProfiles[index].Reconciliation with
            {
                MaximumRemovals = 17,
            },
        };
        changed.Save(library.ConfigurationPath);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyAsync(plan, ct: TestContext.Current.CancellationToken));

        Assert.Contains("changed after preview", error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Assert.Single(plan.Files).DestinationPath));
    }

    [Fact]
    public void CoreDependencyInjectionResolvesConfiguredExportService()
    {
        var services = new ServiceCollection();
        services.AddMusicLibraryCore();
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<ConfiguredExportService>(
            provider.GetRequiredService<IConfiguredExportService>());
    }

    private static ConfiguredExportService CreateService(
        TestLibrary library,
        IReadOnlyList<string> sources)
    {
        var cache = new MetadataCache(buildSecondaryIndexes: false);
        foreach (string source in sources)
        {
            var entry = new MetadataCacheEntry(
                MediaFile.GetFile(source, readOnly: true), File.GetLastWriteTimeUtc(source));
            entry.Strip();
            cache.FileCache[source] = entry;
        }
        var context = new IndexedLibraryOperationContext(
            library.Configuration, library.Configuration.IndexLocations.ToArray(), cache);
        var executor = new FileMutationPlanExecutor(new FileMutationCoordinator());
        var transport = new LocalFileSystemExportTransport(executor);
        return new(new StubContextFactory(context), new FileInventoryService(), [transport]);
    }

    private static void WriteTrackTags(
        string path,
        string albumArtist,
        string album,
        string title,
        int track,
        int disc)
    {
        IMediaFile media = MediaFile.GetFile(path);
        IMetadataWriter writer = Assert.IsAssignableFrom<IMetadataWriter>(media);
        writer.SetField(TagFields.Artist, albumArtist);
        writer.SetField(TagFields.AlbumArtist, albumArtist);
        writer.SetField(TagFields.Album, album);
        writer.SetField(TagFields.Title, title);
        writer.SetField(TagFields.TrackNumber, track.ToString());
        writer.SetField(TagFields.DiscNumber, disc.ToString());
        writer.Save();
    }

    private sealed class StubContextFactory(IndexedLibraryOperationContext context)
        : ILibraryOperationContextFactory
    {
        public Task<IndexedLibraryOperationContext> CreateIndexedAsync(
            string? configurationPath,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => Task.FromResult(context);

        public Task<LibraryOperationContext> CreateAsync(
            string? configurationPath,
            string? itunesLibraryPath = null,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed record TestLibrary(
        string ConfigurationPath,
        string SourceRoot,
        string TargetRoot,
        LibraryConfiguration Configuration);

    private sealed class TestWorkspace : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "configured-export-" + Guid.NewGuid().ToString("N"));

        public TestWorkspace() => Directory.CreateDirectory(_root);

        public string Path(params string[] parts) =>
            System.IO.Path.Combine([_root, .. parts]);

        public string Media(params string[] parts)
        {
            string path = Path(parts);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.Copy(MediaFixtures.Path_("sample.flac"), path);
            return path;
        }

        public string Text(string directory, string name, string content)
        {
            string path = Path(directory, name);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public TestLibrary Library(
            ExportSelectionPolicy selection,
            ExportNamingPolicy? naming = null,
            ExportReconciliationPolicy? reconciliation = null,
            string profileId = "portable",
            bool allowEmptyExplicitSelection = false,
            string? activeProfileId = null,
            IReadOnlyList<LibraryProfile>? additionalProfiles = null)
        {
            LibraryExportProfile profile = Profile(Path("target"), selection, naming,
                reconciliation, profileId);
            return Library(profile, allowEmptyExplicitSelection, activeProfileId,
                additionalProfiles);
        }

        public TestLibrary Library(
            LibraryExportProfile profile,
            bool allowEmptyExplicitSelection = false,
            string? activeProfileId = null,
            IReadOnlyList<LibraryProfile>? additionalProfiles = null)
        {
            string sourceRoot = Directory.CreateDirectory(Path("source")).FullName;
            string targetRoot = Directory.CreateDirectory(Path("target")).FullName;
            string configurationPath = Path(profile.Id + ".xml");
            // Keep catalog-only active; export naming explicitly selects another profile.
            var editable = EditableLibraryConfig.CreateNew();
            editable.ActiveProfileId = activeProfileId ?? editable.ActiveProfileId;
            if (activeProfileId == LibraryProfilePresets.LegacyId &&
                editable.Profiles.All(candidate => candidate.Id != activeProfileId))
                editable.Profiles.Add(LibraryProfilePresets.Create(
                    LibraryProfilePreset.LegacyMusicLibraryTools));
            if (additionalProfiles is not null)
                editable.Profiles.AddRange(additionalProfiles);
            editable.DatabaseFile = Path("cache.db");
            editable.IndexTargets.Add(new()
            {
                Target = sourceRoot,
                ProfileId = LibraryProfilePresets.ArtistAlbumId,
                Permissions = LibraryRootPermissions.None,
                Organize = false,
                RepresentationRole = LibraryRepresentationRole.Ignore,
            });
            editable.IndexTargets.Add(new()
            {
                Target = targetRoot,
                ProfileId = LibraryProfilePresets.ArtistAlbumId,
                Permissions = LibraryRootPermissions.SynchronizeOutput,
                Organize = false,
                RepresentationRole = LibraryRepresentationRole.Ignore,
            });
            if (allowEmptyExplicitSelection &&
                profile.Selection.Kind == ExportSelectionKind.ExplicitTracks &&
                profile.Selection.Values.Length == 0)
            {
                // XML validation intentionally requires a value. A path that is absent from the
                // cache yields the same zero desired files while retaining a valid configuration.
                profile = profile with
                {
                    Selection = new(ExportSelectionKind.ExplicitTracks,
                        [Path("not-indexed.flac")]),
                };
            }
            editable.ExportProfiles.Add(profile);
            editable.Save(configurationPath);
            var configuration = new LibraryConfiguration(configurationPath);
            return new(configurationPath, sourceRoot, targetRoot, configuration);
        }

        public static LibraryExportProfile Profile(
            string destination,
            ExportSelectionPolicy selection,
            ExportNamingPolicy? naming = null,
            ExportReconciliationPolicy? reconciliation = null,
            string id = "portable") => new(
                id,
                "Portable export",
                true,
                selection,
                new(ExportTransformMode.Copy),
                naming ?? new(LibraryProfileId: LibraryProfilePresets.ArtistAlbumId,
                    CollisionPolicy: LibraryPathCollisionPolicy.Stop),
                new(ExportArtworkMode.Embedded, FrontCoverOnly: false,
                    PreserveEncoding: true),
                new(Enabled: false),
                new(LocalFileSystemExportTransport.ProviderId, destination),
                reconciliation ?? new(ExportExtraFileDisposition.Preserve));

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { }
        }
    }
}
