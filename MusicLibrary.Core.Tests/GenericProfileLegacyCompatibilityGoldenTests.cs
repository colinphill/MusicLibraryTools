using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using iTunes.Binary;
using MetadataCaching;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;
using Xunit;

namespace MusicLibrary.Core.Tests;

/// <summary>
/// Compatibility goldens for the policy-profile boundary. These expectations intentionally
/// describe the pre-profile behavior so a future preset edit cannot silently change an existing
/// unversioned library.
/// </summary>
public sealed class GenericProfileLegacyCompatibilityGoldenTests
{
    [Fact]
    public void LegacyV1OrganizationPathMatchesHistoricalCanonicalName()
    {
        using var workspace = new TempDirectory();
        string libraryRoot = workspace.Directory("library");
        string configurationPath = WriteLegacyLibraryConfiguration(
            workspace, libraryRoot);
        var configuration = new LibraryConfiguration(configurationPath);
        LibraryIndexLocation location = Assert.Single(configuration.IndexLocations);
        LibraryProfile profile = configuration.GetEffectiveProfile(location);

        string destination = LibraryPathLayoutResolver.Shared.Resolve(
            location.Target,
            profile,
            new LibraryPathMetadata(
                "Track Artist", "Album Artist", "Album (HiRes)", "A $ong",
                3, 1, false, "2024", "unclean source", ".flac"),
            configuration.LengthLimit,
            configuration.DiscNumLengthLimit);

        Assert.Equal(LibraryConfigurationSchema.LegacyVersion, configuration.SchemaVersion);
        Assert.Equal(LibraryProfilePreset.LegacyMusicLibraryTools, profile.Preset);
        Assert.Equal(Path.GetFullPath(Path.Combine(
            libraryRoot, "Album Artist", "Album", "03 A song.flac")), destination);
    }

    [Fact]
    public async Task LegacyV1IngestPlanMatchesHistoricalCdAndAacDestinations()
    {
        using var workspace = new TempDirectory();
        string incoming = workspace.Directory("incoming");
        string source = Path.Combine(incoming, "unclean source.flac");
        File.Copy(Fixture("sample.flac"), source);
        WriteGoldenTags(source);

        string cd = workspace.Path("cd");
        string aac = workspace.Path("aac");
        string configurationPath = workspace.Path("legacy-ingest.xml");
        new IngestMusicConfiguration
        {
            FfmpegPath = "ffmpeg",
            AacDestination = aac,
            CdDestination = cd,
            PairedCdDestination = workspace.Path("paired"),
            HighResolutionDestination = workspace.Path("hires"),
            LengthLimit = 255,
            DiscNumLengthLimit = 255,
        }.Save(configurationPath);

        IngestPlan plan = await new IngestMusicService(new PreviewOnlyFfmpeg())
            .PreviewAsync(new(incoming, configurationPath),
                TestContext.Current.CancellationToken);

        Assert.Equal(LibraryProfilePreset.LegacyMusicLibraryTools,
            plan.Configuration.Profile.Preset);
        Assert.Equal(LibrarySourceDisposition.Quarantine,
            plan.Configuration.SourceDisposition);
        Assert.Empty(plan.Conflicts);
        IngestOutputPlan[] outputs = Assert.Single(plan.Albums).Outputs.ToArray();
        Assert.Collection(outputs,
            output =>
            {
                Assert.Equal(IngestOutputKind.Recipe, output.Kind);
                Assert.Equal("legacy-cd-flac", output.RecipeId);
                Assert.False(output.DeriveCd);
                Assert.Equal(3, output.Metadata.TrackTotal);
                Assert.Equal(Path.GetFullPath(Path.Combine(
                    cd, "Album Artist", "Album", "03 A song.flac")),
                    output.DestinationPath);
            },
            output =>
            {
                Assert.Equal(IngestOutputKind.Recipe, output.Kind);
                Assert.Equal("legacy-aac", output.RecipeId);
                Assert.False(output.AddToMediaCatalog);
                Assert.Equal(Path.GetFullPath(Path.Combine(
                    aac, "Album Artist", "Album", "03 A song.m4a")),
                    output.DestinationPath);
            });
    }

    [Fact]
    public async Task LegacyV1RepresentationRepairMatchesHistoricalDerivedPaths()
    {
        using var workspace = new TempDirectory();
        string cd = workspace.Directory("cd");
        string paired = workspace.Directory("paired");
        string hires = workspace.Directory("hires");
        string aac = workspace.Directory("aac");
        string configurationPath = WriteLegacyIngestLibraryConfiguration(
            workspace, cd, paired, hires, aac);
        var configuration = new LibraryConfiguration(configurationPath);
        string source = Path.Combine(
            hires, "Album Artist", "Album", "03 A song.flac");
        var highResolution = new TrackRecord
        {
            Path = source,
            Artist = "Track Artist",
            AlbumArtist = "Album Artist",
            HasAlbumArtist = true,
            Album = "Album (HiRes)",
            StrippedAlbum = "Album",
            Title = "A $ong",
            ReleaseDate = "2024",
            TrackNumber = 3,
            TrackTotal = 10,
            DiscNumber = 1,
            DiscTotal = 1,
            CodecName = "FLAC",
            CodecType = CodecType.Lossless,
            SampleRate = 96_000,
            BitsPerSample = 24,
            Channels = 2,
            Length = 123,
            LastWriteTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var service = new RepresentationRepairService(new EmptyOrganizer());

        RepresentationRepairPreview preview = await service.PreviewAsync(
            [highResolution], configuration, TestContext.Current.CancellationToken);

        Assert.Empty(preview.Warnings);
        RepresentationRepairAction cdAction = Assert.Single(preview.FileActions,
            action => action.Kind == RepresentationRepairKind.DeriveCdFlac);
        RepresentationRepairAction aacAction = Assert.Single(preview.FileActions,
            action => action.Kind == RepresentationRepairKind.DeriveAac);
        Assert.Equal(Path.GetFullPath(Path.Combine(
            paired, "Album Artist", "Album", "03 A song.flac")),
            cdAction.DestinationPath);
        Assert.Equal(Path.GetFullPath(Path.Combine(
            aac, "Album Artist", "Album", "03 A song.m4a")),
            aacAction.DestinationPath);
    }

    [Fact]
    public async Task LegacyV1PlaylistTargetPreservesHistoricalM3uBytes()
    {
        using var workspace = new TempDirectory();
        string sourceRoot = workspace.Directory("source");
        string source = Path.Combine(sourceRoot, "track.flac");
        File.Copy(Fixture("sample.flac"), source);
        string targetRoot = workspace.Directory("playlists");
        string configurationPath = WriteLegacyPlaylistConfiguration(
            workspace, sourceRoot, targetRoot);
        var configuration = new LibraryConfiguration(configurationPath);
        var cache = new MetadataCache();
        var entry = new MetadataCacheEntry(
            MediaFile.GetFile(source, readOnly: true), File.GetLastWriteTimeUtc(source));
        entry.Strip();
        cache.FileCache[source] = entry;
        (ItlLibrary library, ItlTrack track) = CreateItunesPlaylist(source);
        var context = new LibraryOperationContext(
            configuration,
            configuration.IndexLocations.ToArray(),
            cache,
            library,
            new Dictionary<int, ItlTrack> { [track.Id] = track },
            workspace.Path("library.itl"));
        var service = new PlaylistExportService(
            new FixedContextFactory(context),
            new FileInventoryService(),
            new FileMutationPlanExecutor(new FileMutationCoordinator()));

        PlaylistExportPlan plan = await service.PreviewAsync(
            new(configurationPath), ct: TestContext.Current.CancellationToken);

        Assert.True(plan.CanApply);
        PlaylistExportFile playlist = Assert.Single(
            Assert.Single(plan.Targets).Files,
            file => file.PlaylistName == "Favorites");
        FileMutationAction write = Assert.Single(plan.MutationPlan.Actions,
            action => action.DestinationPath == playlist.DestinationPath);
        int duration = entry.DurationInSeconds == 0 ? -1 : entry.DurationInSeconds;
        Assert.Equal(RenderLegacyM3u(
            duration, "../default/track.flac"), write.Content.ToArray());
    }

    [Fact]
    public void CatalogOnlyProfileDeniesEveryRootMutationAndProducesNoIngestPlan()
    {
        using var workspace = new TempDirectory();
        string root = workspace.Directory("catalog");
        var editable = EditableLibraryConfig.CreateNew();
        editable.IndexTargets.Add(editable.CreateIndexTarget(root));
        string configurationPath = workspace.Path("catalog-only.xml");
        editable.Save(configurationPath);
        var configuration = new LibraryConfiguration(configurationPath);
        LibraryIndexLocation location = Assert.Single(configuration.IndexLocations);
        string track = Path.Combine(root, "track.flac");

        Assert.Equal(LibraryProfilePreset.CatalogOnly,
            configuration.ActiveProfile.Preset);
        Assert.Equal(LibraryRootPermissions.None, location.Permissions);
        Assert.False(location.Organize);
        Assert.Empty(LibraryOrganizationPolicy.EligibleTargets([location]));
        foreach (LibraryRootPermissions permission in new[]
                 {
                     LibraryRootPermissions.WriteMetadata,
                     LibraryRootPermissions.WriteArtwork,
                     LibraryRootPermissions.OrganizeFiles,
                     LibraryRootPermissions.IngestOutput,
                     LibraryRootPermissions.SynchronizeOutput,
                 })
            Assert.False(LibraryRootPermissionPolicy.Allows(track, [location], permission));
        Assert.Contains(IngestMusicConfiguration.MissingLibrarySettings(configuration),
            message => message.Contains("does not enable ingest",
                StringComparison.OrdinalIgnoreCase));
        Assert.Throws<InvalidDataException>(() =>
            IngestMusicConfiguration.FromLibraryConfiguration(configuration));
    }

    [Fact]
    public void LegacyV1DeleteSourcesOptInMigratesToLegacyProfileDisposition()
    {
        using var workspace = new TempDirectory();
        string configurationPath = workspace.Path("delete-source-migration.xml");
        new XDocument(new XElement("LibraryConfiguration",
            new XElement("DatabaseFile", workspace.Path("cache.db")),
            new XElement("FfmpegPath", "ffmpeg"),
            LegacyIngestRoot(workspace.Directory("cd"), LibraryIngestRole.Cd),
            LegacyIngestRoot(workspace.Directory("paired"), LibraryIngestRole.CdFallback),
            LegacyIngestRoot(workspace.Directory("hires"), LibraryIngestRole.HiRes),
            LegacyIngestRoot(workspace.Directory("aac"), LibraryIngestRole.AacFallback),
            new XElement("IngestSettings",
                new XAttribute("DeleteSourcesAfterIngest", true)),
            new XElement("LengthLimit", 255),
            new XElement("DiscNumLengthLimit", 255)))
            .Save(configurationPath);

        EditableLibraryConfig editable = EditableLibraryConfig.Load(configurationPath);
        LibraryIngestProfile projectedLegacy = Assert.Single(editable.IngestProfiles,
            profile => profile.Id == LibraryProfilePresets.LegacyId);
        Assert.Equal(LibrarySourceDisposition.Delete,
            projectedLegacy.Ingest.SourceDisposition);

        editable.Save(configurationPath);

        var migrated = new LibraryConfiguration(configurationPath);
        LibraryIngestProfile persistedLegacy = Assert.Single(migrated.IngestProfiles,
            profile => profile.Id == LibraryProfilePresets.LegacyId);
        Assert.Equal(LibraryConfigurationSchema.CurrentVersion, migrated.SchemaVersion);
        Assert.Equal(LibrarySourceDisposition.Delete,
            persistedLegacy.Ingest.SourceDisposition);
        Assert.True(migrated.IngestSettings.DeleteSourcesAfterIngest);
        Assert.Equal(LibrarySourceDisposition.Delete,
            IngestMusicConfiguration.FromLibraryConfiguration(migrated).SourceDisposition);
    }

    [Fact]
    public void PolicyFingerprintIncludesAllGlobalPlanInputs()
    {
        using var workspace = new TempDirectory();
        string basePath = workspace.Path("fingerprint-base.xml");
        var editable = new EditableLibraryConfig();
        editable.IndexTargets.Add(editable.CreateIndexTarget(
            workspace.Directory("library")));
        editable.Save(basePath);
        var baselineDocument = XDocument.Load(basePath);
        string baseline = new LibraryConfiguration(basePath).PolicySnapshot.Fingerprint;

        string identicalPath = workspace.Path("fingerprint-identical.xml");
        new XDocument(baselineDocument).Save(identicalPath);
        Assert.Equal(baseline,
            new LibraryConfiguration(identicalPath).PolicySnapshot.Fingerprint);

        var changes = new (string Name, Action<XDocument> Apply)[]
        {
            ("length-limit", document =>
                document.Root!.Element("LengthLimit")!.Value = "254"),
            ("disc-length-limit", document =>
                document.Root!.Element("DiscNumLengthLimit")!.Value = "253"),
            ("sync-playlists", document =>
                document.Root!.Add(new XElement("SyncPlaylist", "Road Trip"))),
            ("delete-stale-sync", document =>
                document.Root!.Element("CrossSyncMusicSettings")!
                    .SetAttributeValue("DeleteStaleFiles", true)),
            ("clean-playlists", document =>
                document.Root!.Element("CrossSyncPlaylistsSettings")!
                    .SetAttributeValue("Clean", true)),
            ("legacy-delete-non-music", document =>
                document.Root!.Add(new XElement("DeleteNonMusic", "true"))),
            ("legacy-keep-folder-images", document =>
                document.Root!.Add(new XElement("KeepFolderImages", "true"))),
            ("legacy-sync-target", document =>
                document.Root!.Add(new XElement("SyncTarget", "alternate-sync"))),
            ("aac-encoder", document =>
                document.Root!.Element("IngestSettings")!
                    .SetAttributeValue("AacEncoder", "aac")),
            ("aac-bitrate", document =>
                document.Root!.Element("IngestSettings")!
                    .SetAttributeValue("AacBitrateKbps", 320)),
            ("delete-ingest-source", document =>
                document.Root!.Element("IngestSettings")!
                    .SetAttributeValue("DeleteSourcesAfterIngest", true)),
            ("remove-ingest-sidecars", document =>
                document.Root!.Element("IngestSettings")!
                    .SetAttributeValue("RemoveNonMusicAfterIngest", true)),
            ("artwork-byte-threshold", document =>
                document.Root!.Element("ArtworkHealthSettings")!
                    .SetAttributeValue("OversizedByteThreshold", 1_000_000)),
            ("artwork-dimension-threshold", document =>
                document.Root!.Element("ArtworkHealthSettings")!
                    .SetAttributeValue("OversizedDimensionThreshold", 1_500)),
            ("artwork-repair-bytes", document =>
                document.Root!.Element("ArtworkHealthSettings")!
                    .SetAttributeValue("RepairTargetByteSize", 200_000)),
            ("artwork-repair-dimension", document =>
                document.Root!.Element("ArtworkHealthSettings")!
                    .SetAttributeValue("RepairTargetDimension", 500)),
            ("database-binding", document =>
                document.Root!.Element("DatabaseFile")!.Value = "alternate-cache.db"),
            ("ffmpeg-binding", document =>
                document.Root!.Element("FfmpegPath")!.Value = "alternate-ffmpeg"),
            ("itunes-binding", document =>
                document.Root!.Add(new XElement("ItunesLibrary", "alternate.itl"))),
        };

        foreach ((string name, Action<XDocument> apply) in changes)
        {
            var variant = new XDocument(baselineDocument);
            apply(variant);
            string path = workspace.Path("fingerprint-" + name + ".xml");
            variant.Save(path);

            string changed = new LibraryConfiguration(path).PolicySnapshot.Fingerprint;

            Assert.True(!StringComparer.Ordinal.Equals(baseline, changed),
                $"Changing '{name}' did not invalidate the policy fingerprint.");
        }
    }

    private static string WriteLegacyLibraryConfiguration(
        TempDirectory workspace,
        string libraryRoot)
    {
        string path = workspace.Path("legacy-library.xml");
        var root = new XElement("LibraryConfiguration",
            new XElement("DatabaseFile", workspace.Path("cache.db")),
            new XElement("IndexTarget", libraryRoot),
            new XElement("LengthLimit", 255),
            new XElement("DiscNumLengthLimit", 255));
        new XDocument(root).Save(path);
        return path;
    }

    private static string WriteLegacyIngestLibraryConfiguration(
        TempDirectory workspace,
        string cd,
        string paired,
        string hires,
        string aac)
    {
        string path = workspace.Path("legacy-representations.xml");
        new XDocument(new XElement("LibraryConfiguration",
            new XElement("DatabaseFile", workspace.Path("cache.db")),
            new XElement("FfmpegPath", "ffmpeg"),
            LegacyIngestRoot(cd, LibraryIngestRole.Cd),
            LegacyIngestRoot(paired, LibraryIngestRole.CdFallback),
            LegacyIngestRoot(hires, LibraryIngestRole.HiRes),
            LegacyIngestRoot(aac, LibraryIngestRole.AacFallback),
            new XElement("LengthLimit", 255),
            new XElement("DiscNumLengthLimit", 255)))
            .Save(path);
        return path;
    }

    private static XElement LegacyIngestRoot(
        string path,
        LibraryIngestRole role) =>
        new("IndexTarget", new XAttribute("IngestRole", role), path);

    private static string WriteLegacyPlaylistConfiguration(
        TempDirectory workspace,
        string sourceRoot,
        string targetRoot)
    {
        string path = workspace.Path("legacy-playlist.xml");
        new XDocument(new XElement("LibraryConfiguration",
            new XElement("DatabaseFile", workspace.Path("cache.db")),
            new XElement("IndexTarget",
                new XAttribute("Set", "Primary"),
                new XAttribute("Offset", "../default"), sourceRoot),
            new XElement("PlaylistTarget",
                new XAttribute("Type", "m3u"),
                new XAttribute("Set", "Primary"), targetRoot),
            new XElement("LengthLimit", 255),
            new XElement("DiscNumLengthLimit", 255)))
            .Save(path);
        return path;
    }

    private static void WriteGoldenTags(string path)
    {
        IMediaFile media = MediaFile.GetFile(path);
        IMetadataWriter writer = Assert.IsAssignableFrom<IMetadataWriter>(media);
        writer.SetField(TagFields.Artist, "Track Artist");
        writer.SetField(TagFields.AlbumArtist, "Album Artist");
        writer.SetField(TagFields.Album, "Album (HiRes)");
        writer.SetField(TagFields.Title, "A $ong");
        writer.SetField(TagFields.TrackNumber, "3");
        writer.SetField(TagFields.TotalTracks, "10");
        writer.SetField(TagFields.DiscNumber, "1");
        writer.SetField(TagFields.TotalDiscs, "1");
        writer.Save();
    }

    private static (ItlLibrary Library, ItlTrack Track) CreateItunesPlaylist(string source)
    {
        byte[] header = new byte[800];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(44), 3);
        var track = new ItlTrack
        {
            Id = 1,
            Header = header,
            DataObjects =
            [
                StringObject(ItlDataType.Location, new Uri(source).AbsoluteUri),
                StringObject(ItlDataType.Title, "TestTitle"),
                StringObject(ItlDataType.Artist, "TestArtist"),
                StringObject(ItlDataType.Album, "TestAlbum"),
            ],
        };
        var library = new ItlLibrary
        {
            Envelope = new ItlEnvelope
            {
                Version = "test",
                LibraryPersistentId = 0,
                SectionCount = 0,
                MaxCryptSize = 0,
                FileLength = 0,
                RawHeader = [],
                Body = [],
            },
            Sections = [],
            Tracks = [track],
            Albums = [],
            Artists = [],
            Playlists =
            [
                new ItlPlaylist { Name = "####!####", TrackIds = [1] },
                new ItlPlaylist { Name = "Favorites", TrackIds = [1] },
            ],
        };
        return (library, track);
    }

    private static ItlDataObject StringObject(ItlDataType type, string value)
    {
        var result = new ItlDataObject { Type = (int)type, Raw = [] };
        typeof(ItlDataObject).GetProperty(
                "Text", BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(result, value);
        return result;
    }

    private static byte[] RenderLegacyM3u(int duration, string path)
    {
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.WriteLine("#EXTM3U");
        writer.WriteLine($"#EXTINF:{duration},TestArtist - TestTitle");
        writer.WriteLine(path);
        writer.Flush();
        return stream.ToArray();
    }

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "TestFiles", name);

    private sealed class EmptyOrganizer : ILibraryOrganizer
    {
        public Task<IReadOnlyList<PlannedMove>> PreviewMovesAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PlannedMove>>([]);

        public Task<OrganizeResult> ApplyMovesAsync(
            IReadOnlyList<PlannedMove> moves,
            IProgress<int>? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class PreviewOnlyFfmpeg : IFfmpegRunner
    {
        public Task PreflightAsync(string executable, string requiredEncoder,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task ConvertAlacToFlacAsync(string executable, string input, string output,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeriveCdFlacAsync(string executable, string input, string output,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task EncodeAacAsync(string executable, string encoder, int bitrateKbps,
            string input, string output, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> ComputeDecodedAudioHashAsync(string executable, string input,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FixedContextFactory(LibraryOperationContext context)
        : ILibraryOperationContextFactory
    {
        public Task<LibraryOperationContext> CreateAsync(
            string? configurationPath,
            string? itunesLibraryPath = null,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => Task.FromResult(context);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "generic-profile-goldens-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => System.IO.Directory.CreateDirectory(Root);

        public string Path(params string[] parts) =>
            System.IO.Path.Combine([Root, .. parts]);

        public string Directory(params string[] parts)
        {
            string path = Path(parts);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Root, recursive: true); }
            catch { }
        }
    }
}
