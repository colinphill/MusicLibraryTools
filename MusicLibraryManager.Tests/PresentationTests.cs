using System.Diagnostics;
using MetadataCaching;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class PresentationTests
{
    [Fact]
    public void Activity_service_tracks_lifecycle_and_retains_history()
    {
        var service = new AppActivityService();
        Guid id = service.Start("Index", "Preparing");

        Assert.NotNull(service.Current);
        service.Report(id, "Reading metadata", 0.5);
        Assert.Equal(0.5, service.Current!.Progress);

        service.Finish(id, "Complete");
        Assert.Null(service.Current);
        Assert.Equal(AppActivityState.Completed, service.Activities[0].State);
        Assert.Equal("Complete", service.Activities[0].Message);
    }

    [Fact]
    public void Navigation_service_publishes_the_selected_destination()
    {
        var service = new NavigationService();
        ShellDestination? observed = null;
        service.NavigationRequested += destination => observed = destination;

        service.Navigate(ShellDestination.Library);

        Assert.Equal(ShellDestination.Library, service.Current);
        Assert.Equal(ShellDestination.Library, observed);
    }

    [Fact]
    public async Task Library_view_loads_filters_and_uses_manager_namespaced_state()
    {
        var settings = new FakeSettings();
        var records = new[]
        {
            Track("Miles", "Kind of Blue", "So What", "FLAC", @"C:\Music\So What.flac"),
            Track("Massive Attack", "Mezzanine", "Teardrop", "MP3", @"C:\Music\Teardrop.mp3"),
        };
        LibraryViewModel viewModel = BuildLibrary(settings, records);

        await viewModel.ReloadAsync();
        viewModel.FilterText = "Artist:Miles AND Codec:FLAC";
        await viewModel.ApplyFilterNowAsync(TestContext.Current.CancellationToken);

        Assert.Single(viewModel.Rows);
        Assert.Equal("So What", viewModel.Rows[0].Title);
        Assert.Contains("manager.library.workspace.v1", settings.Preferences.Keys);
        Assert.DoesNotContain("table.workspace.v1", settings.Preferences.Keys);
    }

    [Fact]
    public async Task Library_view_handles_one_hundred_thousand_cached_tracks_without_artwork_reads()
    {
        var settings = new FakeSettings();
        TrackRecord[] records = Enumerable.Range(0, 100_000)
            .Select(index => Track($"Artist {index % 500}", $"Album {index % 4000}",
                $"Track {index}", index % 7 == 0 ? "ALAC" : "FLAC", $@"C:\Music\Track {index}.flac"))
            .ToArray();
        var library = new FakeLibrary(records);
        LibraryViewModel viewModel = BuildLibrary(settings, records, library);
        int rowReplacements = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(LibraryViewModel.Rows))
                rowReplacements++;
        };
        var clock = Stopwatch.StartNew();

        await viewModel.ReloadAsync();

        Assert.Equal(100_000, viewModel.Rows.Count);
        Assert.Equal(1, rowReplacements);
        Assert.Equal(0, library.ArtworkReadCount);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"Cached projection took {clock.Elapsed}.");
    }

    [Fact]
    public async Task Library_view_labels_flac_correctly_and_loads_only_realized_thumbnails()
    {
        var settings = new FakeSettings();
        TrackRecord[] records =
        [
            Track("Artist", "Album", "FLAC track", "Vorbis", @"C:\Music\Track.flac"),
            Track("Artist", "Album", "Ogg track", "Vorbis", @"C:\Music\Track.ogg"),
        ];
        var library = new FakeLibrary(records);
        LibraryViewModel viewModel = BuildLibrary(settings, records, library);

        await viewModel.ReloadAsync();

        Assert.Equal("FLAC", viewModel.Rows[0].Codec);
        Assert.Equal("Vorbis", viewModel.Rows[1].Codec);
        Assert.Equal(0, library.ArtworkReadCount);

        LibraryRow realized = viewModel.Rows[0];
        await viewModel.LoadThumbnailAsync(realized);
        Assert.True(realized.ThumbnailLoaded);
        Assert.Equal(1, library.ArtworkReadCount);

        viewModel.ReleaseThumbnail(realized);
        await viewModel.LoadThumbnailAsync(realized);
        Assert.Equal(1, library.ArtworkReadCount); // cached even when the track has no artwork
    }

    [Fact]
    public async Task Selection_inspector_detects_mixed_values_and_writes_only_modified_fields()
    {
        var media = new FakeMediaService(
            Model(@"C:\one.flac", "One", "Artist A"),
            Model(@"C:\two.flac", "Two", "Artist B"));
        var writer = new FakeTagWriter();
        var inspector = new SelectionInspectorViewModel(media, writer, new FakeArtworkService(),
            new FakeFilePicker(), new FakeDialogs(), new FakeFieldsEditor(),
            new FakeThumbnails(), new AppActivityService());

        await inspector.LoadAsync(new SelectionContext([@"C:\one.flac", @"C:\two.flac"]));
        EditableTagField artist = inspector.Fields.Single(field => field.Field == TagFields.Artist);
        Assert.True(artist.IsMixed);

        artist.Value = "Canonical Artist";
        await inspector.SaveTagsCommand.ExecuteAsync(null);

        TagEdit edit = Assert.Single(writer.Edits!);
        Assert.Equal(TagFields.Artist, edit.Field);
        Assert.Equal("Canonical Artist", edit.Value);
    }

    [Fact]
    public async Task Settings_editor_round_trips_complete_library_configuration()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"manager-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string configurationPath = Path.Combine(directory, "library.xml");
        string settingsPath = Path.Combine(directory, "settings.json");
        try
        {
            new EditableLibraryConfig
            {
                DatabaseFile = "music.db",
                ItunesLibraryPath = @"C:\Music\iTunes Library.itl",
                FfmpegPath = "ffmpeg-custom",
                LengthLimit = 220,
                DiscNumLengthLimit = 180,
                AacEncoder = "aac_encoder",
                AacBitrateKbps = 288,
                DeleteSourcesAfterIngest = true,
                RemoveNonMusicAfterIngest = true,
                IndexTargets =
                [
                    new IndexTargetEntry
                    {
                        Target = @"Z:\Lossless",
                        Filter = "*.flac",
                        DefaultOffset = "/Music",
                        Organize = false,
                        UseItunesCanonicalNaming = true,
                        IngestRole = LibraryIngestRole.Cd,
                        IsSyncTarget = true,
                        Memberships =
                        [
                            new IndexTargetSetEntry { Name = "Lossless", Offset = "/FLAC" },
                            new IndexTargetSetEntry { Name = "Desktop" },
                        ],
                    },
                ],
                SyncPlaylists = ["Favorites", "RoadTrip"],
                PlaylistTargets =
                [
                    new PlaylistTargetEntry
                    {
                        Target = @"Z:\Playlists", Type = "wpl", Sets = ["Lossless"],
                    },
                ],
            }.Save(configurationPath);
            var settings = new AppSettings(settingsPath);
            settings.LoadConfig(configurationPath);
            var viewModel = new SettingsViewModel(settings, new FakeFilePicker(),
                new FakeDialogs(), new FakeTheme());

            // The active configuration is restored directly into the editor; users should not
            // need to click Edit active before their roots and workflow targets appear.
            IndexTargetEditorRow root = Assert.Single(viewModel.IndexTargets);
            Assert.Equal("/Music", root.DefaultOffset);
            Assert.True(root.UseItunesCanonicalNaming);
            Assert.Equal(LibraryIngestRole.Cd, root.IngestRole);
            Assert.True(root.IsSyncTarget);
            Assert.Equal(2, root.Memberships.Count);
            Assert.Equal("/FLAC", root.Memberships[0].Offset);
            Assert.Equal(220, viewModel.LengthLimit);
            Assert.Equal(180, viewModel.DiscNumLengthLimit);
            Assert.Equal("aac_encoder", viewModel.AacEncoder);
            Assert.Equal(288, viewModel.AacBitrateKbps);
            Assert.True(viewModel.DeleteSourcesAfterIngest);
            Assert.True(viewModel.RemoveNonMusicAfterIngest);
            Assert.Equal(["Favorites", "RoadTrip"], viewModel.SyncPlaylists.Select(row => row.Name));
            Assert.Equal("Lossless", Assert.Single(viewModel.PlaylistTargets).Sets);

            root.Memberships[0].Offset = "/Portable/FLAC";
            viewModel.PlaylistTargets[0].Type = "m3u";
            await viewModel.SaveConfigurationCommand.ExecuteAsync(null);

            EditableLibraryConfig saved = EditableLibraryConfig.Load(configurationPath);
            IndexTargetEntry savedRoot = Assert.Single(saved.IndexTargets);
            Assert.Equal("/Portable/FLAC", savedRoot.Memberships[0].Offset);
            Assert.True(savedRoot.UseItunesCanonicalNaming);
            Assert.True(savedRoot.IsSyncTarget);
            Assert.Equal(LibraryIngestRole.Cd, savedRoot.IngestRole);
            Assert.Equal(["Favorites", "RoadTrip"], saved.SyncPlaylists);
            Assert.Equal("m3u", Assert.Single(saved.PlaylistTargets).Type);
            Assert.Equal(["Lossless"], saved.PlaylistTargets[0].Sets);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task All_fields_editor_handles_mixed_add_remove_and_batch_save()
    {
        var media = new FakeMediaService(
            new MediaFileModel
            {
                Path = @"C:\one.flac", IsWritable = true,
                KnownFields =
                [
                    new TagFieldValue(TagFields.Grouping, "First"),
                    new TagFieldValue(TagFields.Copyright, "Copyright owner"),
                ],
            },
            new MediaFileModel
            {
                Path = @"C:\two.flac", IsWritable = true,
                KnownFields = [new TagFieldValue(TagFields.Grouping, "Second")],
            });
        var writer = new FakeTagWriter();
        var viewModel = new MusicLibrary.App.ViewModels.FieldsDialogViewModel(
            media, writer, [@"C:\one.flac", @"C:\two.flac"]);
        await viewModel.Loading;

        MusicLibrary.App.ViewModels.FieldRow grouping =
            viewModel.Rows.Single(row => row.Field == TagFields.Grouping);
        Assert.True(grouping.IsMixed);
        grouping.Value = "Canonical grouping";

        MusicLibrary.App.ViewModels.FieldRow copyright =
            viewModel.Rows.Single(row => row.Field == TagFields.Copyright);
        viewModel.RemoveFieldCommand.Execute(copyright);

        viewModel.FieldToAdd = TagFields.Mood;
        viewModel.AddFieldCommand.Execute(null);
        viewModel.Rows.Single(row => row.Field == TagFields.Mood).Value = "Calm";

        bool? closed = null;
        viewModel.CloseRequested += result => closed = result;
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Contains(writer.Edits!, edit =>
            edit.Field == TagFields.Grouping && edit.Value == "Canonical grouping");
        Assert.Contains(writer.Edits!, edit =>
            edit.Field == TagFields.Copyright && edit.Value is null);
        Assert.Contains(writer.Edits!, edit =>
            edit.Field == TagFields.Mood && edit.Value == "Calm");
    }

    private static LibraryViewModel BuildLibrary(
        FakeSettings settings,
        IReadOnlyList<TrackRecord> records,
        FakeLibrary? library = null)
    {
        library ??= new FakeLibrary(records);
        var activity = new AppActivityService();
        var inspector = new SelectionInspectorViewModel(new FakeMediaService(), new FakeTagWriter(),
            new FakeArtworkService(), new FakeFilePicker(), new FakeDialogs(), new FakeFieldsEditor(),
            new FakeThumbnails(), activity);
        var indexing = new IndexingViewModel(library, settings, activity);
        return new LibraryViewModel(library, new FakeReindex(), settings, inspector,
            new NavigationService(), indexing, new FakeThumbnails());
    }

    private static TrackRecord Track(string artist, string album, string title, string codec, string path) => new()
    {
        Path = path,
        Artist = artist,
        AlbumArtist = artist,
        Album = album,
        Title = title,
        CodecName = codec,
        CodecType = codec == "MP3" ? CodecType.Lossy : CodecType.Lossless,
        DurationInSeconds = 240,
        LastWriteTime = new DateTime(2026, 1, 1),
    };

    private static MediaFileModel Model(string path, string title, string artist) => new()
    {
        Path = path,
        Title = title,
        Artist = artist,
        IsWritable = true,
        KnownFields =
        [
            new TagFieldValue(TagFields.Title, title),
            new TagFieldValue(TagFields.Artist, artist),
        ],
    };
}

internal sealed class FakeSettings : IAppSettings
{
    public Dictionary<string, string> Preferences { get; } = [];
    public string? ConfigPath { get; private set; } = "library.xml";
    public LibraryConfiguration? Configuration => null;
    public event EventHandler? ConfigurationChanged;
    public AppConfigurationSnapshot GetSnapshot() => new(ConfigPath, Configuration, 1);
    public void LoadConfig(string path) { ConfigPath = path; ConfigurationChanged?.Invoke(this, EventArgs.Empty); }
    public string? GetRememberedConfigPath() => ConfigPath;
    public IReadOnlyList<string> RecentConfigPaths => ConfigPath is null ? [] : [ConfigPath];
    public void ClearRecentConfigs() { }
    public string? GetPreference(string key) => Preferences.GetValueOrDefault(key);
    public void SetPreference(string key, string? value)
    {
        if (value is null) Preferences.Remove(key); else Preferences[key] = value;
    }
}

internal sealed class FakeLibrary(IReadOnlyList<TrackRecord> records) : ILibraryService
{
    public int ArtworkReadCount { get; private set; }
    public bool IsReady => true;
    public Task<(int Added, int Modified, int Removed, int Unchanged)> IndexAsync(IProgress<IndexProgress>? progress = null, CancellationToken ct = default)
        => Task.FromResult((0, 0, 0, records.Count));
    public Task<LibrarySnapshot> BuildSnapshotAsync(LibraryGrouping grouping = LibraryGrouping.AlbumArtist, CancellationToken ct = default)
        => Task.FromResult(new LibrarySnapshot { TotalTracks = records.Count });
    public Task<IReadOnlyList<TrackRecord>> GetAllRecordsAsync(CancellationToken ct = default)
        => Task.FromResult(records);
    public Task<AnalysisReport> CheckSetsAsync(CancellationToken ct = default)
        => Task.FromResult(new AnalysisReport("Sets", []));
    public Task<FileDetails?> GetFileDetailsAsync(string path, bool includeArtwork, CancellationToken ct = default)
        => Task.FromResult<FileDetails?>(null);
    public Task<byte[]?> GetFirstImageAsync(string path, CancellationToken ct = default)
    {
        ArtworkReadCount++;
        return Task.FromResult<byte[]?>(null);
    }
    public Task<IReadOnlyList<byte[]?>> GetFirstImagesAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        ArtworkReadCount += paths.Count;
        return Task.FromResult<IReadOnlyList<byte[]?>>(paths.Select(_ => (byte[]?)null).ToArray());
    }
    public Task<IReadOnlyList<string>> GetImageSignaturesAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(paths.Select(_ => "").ToArray());
}

internal sealed class FakeReindex : IReindexService
{
    public Task ReindexFileAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class FakeMediaService(params MediaFileModel[] models) : IMediaFileService
{
    private readonly Dictionary<string, MediaFileModel> _models = models.ToDictionary(model => model.Path, StringComparer.OrdinalIgnoreCase);
    public Task<OperationResult<MediaFileModel>> LoadAsync(string path, CancellationToken ct = default) => LoadAsync(path, true, ct);
    public Task<OperationResult<MediaFileModel>> LoadAsync(string path, bool includeArtwork, CancellationToken ct = default)
    {
        MediaFileModel model = _models.GetValueOrDefault(path) ?? new MediaFileModel { Path = path };
        return Task.FromResult(OperationResult<MediaFileModel>.Ok(model));
    }
}

internal sealed class FakeTagWriter : ITagWriteService
{
    public IReadOnlyList<TagEdit>? Edits { get; private set; }
    public Task<BatchWriteResult> ApplyAsync(IReadOnlyList<string> paths, IReadOnlyList<TagEdit> edits, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        Edits = edits;
        return Task.FromResult(new BatchWriteResult(paths.Select(path => new FileWriteResult { Path = path, Outcome = WriteOutcome.Saved }).ToArray()));
    }
}

internal sealed class FakeArtworkService : IArtworkService
{
    public bool SupportsWrite(string musicPath) => true;
    public Task<ArtworkOpResult> SetCoverFromFileAsync(string musicPath, string imagePath, int maxDimension = 0, CancellationToken ct = default) => Success();
    public Task<ArtworkOpResult> ScrubAsync(string musicPath, int maxDimension, int quality = 90, CancellationToken ct = default) => Success();
    public Task<ArtworkOpResult> RemoveAsync(string musicPath, CancellationToken ct = default) => Success();
    public Task<PreparedImage?> PrepareFromFileAsync(string imagePath, int maxDimension = 0, CancellationToken ct = default) => Task.FromResult<PreparedImage?>(null);
    public Task<PreparedImage?> PrepareFromBytesAsync(byte[] data, int maxDimension = 0, int quality = 90, CancellationToken ct = default) => Task.FromResult<PreparedImage?>(null);
    public Task<ArtworkOpResult> SaveImagesAsync(string musicPath, IReadOnlyList<ArtworkInput> images, CancellationToken ct = default) => Success();
    private static Task<ArtworkOpResult> Success() => Task.FromResult(new ArtworkOpResult { Success = true });
}

internal sealed class FakeFilePicker : IFilePickerService
{
    public Task<string?> PickFileAsync(string title, IReadOnlyList<FilePickerType>? types = null) => Task.FromResult<string?>(null);
    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    public Task<string?> SaveFileAsync(string title, string suggestedName, string extension) => Task.FromResult<string?>(null);
}

internal sealed class FakeDialogs : IDialogCoordinator
{
    public Task<bool> ConfirmAsync(string title, string message, string primaryText) => Task.FromResult(true);
    public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
}

internal sealed class FakeFieldsEditor : IFieldsEditorService
{
    public Task<bool> ShowAsync(IReadOnlyList<string> paths) => Task.FromResult(false);
}

internal sealed class FakeThumbnails : IThumbnailService
{
    public Task<object?> CreateImageSourceAsync(byte[] data, int decodePixelWidth = 0,
        CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
}

internal sealed class FakeTheme : IThemeService
{
    public string Current { get; private set; } = "System";
    public void Apply(string theme) => Current = theme;
}
