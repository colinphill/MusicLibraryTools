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
    public void Shell_navigation_capabilities_follow_the_active_library_policy()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            $"shell-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string configPath = Path.Combine(directory, "library.xml");
        string settingsPath = Path.Combine(directory, "settings.json");
        try
        {
            EditableLibraryConfig config = EditableLibraryConfig.CreateNew();
            config.IndexTargets.Add(config.CreateIndexTarget(Path.Combine(directory, "music")));
            config.Save(configPath);
            var settings = new AppSettings(settingsPath);
            settings.LoadConfig(configPath);
            var shell = new ShellViewModel(settings, new NavigationService(),
                new AppActivityService());

            Assert.True(shell.CanOpenHealth);
            Assert.False(shell.CanOpenIngest);
            Assert.False(shell.CanOpenOrganize);
            Assert.False(shell.CanOpenDevices);

            config.ActiveProfileId = LibraryProfilePresets.ArtistAlbumId;
            config.IndexTargets[0].ProfileId = LibraryProfilePresets.ArtistAlbumId;
            config.IndexTargets[0].Permissions = LibraryRootPermissions.OrganizeFiles;
            config.Save(configPath);
            settings.LoadConfig(configPath);

            Assert.True(shell.CanOpenOrganize);
            Assert.False(shell.CanOpenIngest);

            config.ExportProfiles.Add(new LibraryExportProfile(
                "portable-copy", "Portable copy", true,
                ExportSelectionPolicy.EntireLibrary,
                new(ExportTransformMode.Copy),
                new(PreserveSourceLayout: true),
                new(ExportArtworkMode.Embedded),
                new(),
                new(LocalFileSystemExportTransport.ProviderId,
                    Path.Combine(directory, "portable")),
                new()));
            config.Save(configPath);
            settings.LoadConfig(configPath);

            Assert.False(shell.CanOpenDevices);

            config.ExportProfiles.Add(BuiltInExportProfiles.Android with
            {
                Enabled = true,
                Transport = new("android-syncer", "configured-device"),
            });
            config.Save(configPath);
            settings.LoadConfig(configPath);

            Assert.True(shell.CanOpenDevices);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

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

        bool cancelled = false;
        Guid cancellable = service.Start(
            "Preview", "Working", ShellDestination.Operations, () => cancelled = true);
        Assert.Equal(ShellDestination.Operations, service.Current!.Destination);
        Assert.True(service.Current.CanCancel);
        Assert.True(service.Cancel(cancellable));
        Assert.True(cancelled);
        Assert.False(service.Current!.CanCancel);
    }

    [Fact]
    public void Activity_capacity_trims_completed_history_before_a_running_activity()
    {
        var service = new AppActivityService();
        bool runningCancelled = false;
        Guid running = service.Start("Long operation", "Working", cancel: () => runningCancelled = true);

        for (int index = 0; index < 30; index++)
        {
            Guid completed = service.Start($"Completed {index}", "Working");
            service.Finish(completed, "Done");
        }

        AppActivity retained = Assert.Single(service.Activities, activity => activity.Id == running);
        Assert.Equal(AppActivityState.Running, retained.State);
        Assert.True(retained.CanCancel);
        Assert.Equal(25, service.Activities.Count);
        Assert.True(service.Cancel(running));
        Assert.True(runningCancelled);
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
    public async Task Navigation_service_ignores_an_older_guard_that_completes_last()
    {
        var service = new NavigationService();
        var libraryGuard = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var settingsGuard = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new List<ShellDestination>();
        service.NavigationRequested += observed.Add;
        service.Guard = destination => destination switch
        {
            ShellDestination.Library => libraryGuard.Task,
            ShellDestination.Settings => settingsGuard.Task,
            _ => Task.FromResult(true),
        };

        Task older = service.NavigateAsync(ShellDestination.Library);
        Task newer = service.NavigateAsync(ShellDestination.Settings);
        settingsGuard.SetResult(true);
        await newer;
        libraryGuard.SetResult(true);
        await older;

        Assert.Equal(ShellDestination.Settings, service.Current);
        Assert.Equal([ShellDestination.Settings], observed);
    }

    [Fact]
    public async Task Navigation_service_contains_and_publishes_guard_failures()
    {
        var service = new NavigationService();
        var expected = new InvalidOperationException("guard failed");
        Exception? observed = null;
        service.NavigationFailed += error => observed = error;
        service.Guard = _ => Task.FromException<bool>(expected);

        await service.NavigateAsync(ShellDestination.Library);

        Assert.Same(expected, service.LastError);
        Assert.Same(expected, observed);
        Assert.Equal(ShellDestination.Home, service.Current);
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
        viewModel.FilterText = "Artist:Miles aNd Codec:FLAC";
        await viewModel.ApplyFilterNowAsync(TestContext.Current.CancellationToken);

        Assert.Single(viewModel.Rows);
        Assert.Equal("So What", viewModel.Rows[0].Title);
        Assert.Contains("manager.library.workspace.v1", settings.Preferences.Keys);
        Assert.DoesNotContain("table.workspace.v1", settings.Preferences.Keys);
    }

    [Fact]
    public async Task Library_view_treats_quoted_boolean_word_as_literal_text()
    {
        var settings = new FakeSettings();
        var records = new[]
        {
            Track("Artist", "Album", "Rock and Roll", "FLAC", @"C:\Music\Rock and Roll.flac"),
            Track("Artist", "Album", "Instrumental", "FLAC", @"C:\Music\Instrumental.flac"),
        };
        LibraryViewModel viewModel = BuildLibrary(settings, records);
        await viewModel.ReloadAsync();

        viewModel.FilterText = "\"and\"";
        await viewModel.ApplyFilterNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Rock and Roll", Assert.Single(viewModel.Rows).Title);
    }

    [Fact]
    public async Task Library_view_intersects_text_filter_with_health_dispositions()
    {
        var settings = new FakeSettings();
        var records = new[]
        {
            Track("Miles", "Kind of Blue", "So What", "FLAC", @"C:\Music\So What.flac"),
            Track("Massive Attack", "Mezzanine", "Teardrop", "MP3", @"C:\Music\Teardrop.mp3"),
        };
        LibraryViewModel viewModel = BuildLibrary(settings, records);
        await viewModel.ReloadAsync();

        viewModel.SetHealthFilter([records[1].Path]);
        await viewModel.ApplyFilterNowAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.HasHealthFilter);
        Assert.Equal("Teardrop", Assert.Single(viewModel.Rows).Title);

        viewModel.FilterText = "Artist:Miles";
        await viewModel.ApplyFilterNowAsync(TestContext.Current.CancellationToken);
        Assert.Empty(viewModel.Rows);

        viewModel.SetHealthFilter([]);
        await viewModel.ApplyFilterNowAsync(TestContext.Current.CancellationToken);
        Assert.Equal("So What", Assert.Single(viewModel.Rows).Title);
    }

    [Fact]
    public void Named_view_accepts_explicit_column_layout_and_typed_sort_state()
    {
        var settings = new FakeSettings();
        LibraryViewModel viewModel = BuildLibrary(settings, []);
        LibraryColumnState[] columns =
        [
            new("codec", 92, 0, true),
            new("title", 340, 1, false),
        ];

        viewModel.SaveNamedView("Mastering", columns, new LibrarySortState("codec", true));

        LibraryViewDefinition saved = Assert.Single(viewModel.SavedViews);
        Assert.Equal("Mastering", saved.Name);
        Assert.Equal(92, saved.Columns[0].Width);
        Assert.False(saved.Columns[1].Visible);
        Assert.Equal("codec", saved.Sort!.Key);
        Assert.True(saved.Sort.Descending);
        Assert.Contains("manager.library.views.v1", settings.Preferences.Keys);
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
    public async Task Loading_a_configuration_restores_cached_rows_then_starts_the_startup_scan()
    {
        var settings = new FakeSettings();
        TrackRecord[] records =
        [
            Track("Artist", "Album", "Track", "FLAC", @"C:\Music\Track.flac"),
        ];
        var library = new FakeLibrary(records)
        {
            IndexRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        LibraryViewModel viewModel = BuildLibrary(settings, records, library);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.Indexing.IndexCompleted += () => completed.TrySetResult(true);

        settings.LoadConfig("startup-library.xml");
        await library.IndexStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(1, viewModel.TotalCount);
        Assert.True(viewModel.Indexing.IsIndexing);
        Assert.Equal("Scanning…", viewModel.Indexing.ProgressText);

        library.IndexRelease.SetResult(true);
        await completed.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(1, library.IndexCallCount);
        Assert.False(viewModel.Indexing.IsIndexing);
    }

    [Fact]
    public async Task Selection_inspector_detects_mixed_values_and_writes_only_modified_fields()
    {
        var media = new FakeMediaService(
            Model(@"C:\one.flac", "One", "Artist A"),
            Model(@"C:\two.flac", "Two", "Artist B"));
        var writer = new FakeTagWriter();
        var inspector = new SelectionInspectorViewModel(media, new FakeLibrary([]), writer, new FakeArtworkService(),
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
    public async Task Selection_inspector_preserves_multiple_values_from_one_tag()
    {
        var model = Model(@"C:\one.flac", "One", "Artist") with
        {
            KnownFields =
            [
                new TagFieldValue(TagFields.Title, "One"),
                new TagFieldValue(TagFields.Artist, "Artist"),
                new TagFieldValue(TagFields.Genre, "Rock"),
                new TagFieldValue(TagFields.Genre, "Alternative"),
            ],
        };
        var inspector = new SelectionInspectorViewModel(
            new FakeMediaService(model),
            new FakeLibrary([]),
            new FakeTagWriter(),
            new FakeArtworkService(),
            new FakeFilePicker(),
            new FakeDialogs(),
            new FakeFieldsEditor(),
            new FakeThumbnails(),
            new AppActivityService());

        await inspector.LoadAsync(new SelectionContext([model.Path]));

        EditableTagField genre = inspector.Fields.Single(field => field.Field == TagFields.Genre);
        Assert.True(genre.IsMixed);
        Assert.Null(genre.Value);
        Assert.False(genre.IsModified);
    }

    [Fact]
    public async Task Selection_inspector_summarizes_all_selected_file_and_tag_formats()
    {
        TrackRecord[] records =
        [
            Track("Artist", "Album", "One", "FLAC", @"C:\one.flac") with { TagType = "Vorbis" },
            Track("Artist", "Album", "Two", "FLAC", @"C:\two.flac") with { TagType = "Vorbis" },
            Track("Artist", "Album", "Three", "MP3", @"C:\three.mp3") with { TagType = "ID3v24" },
        ];
        var inspector = new SelectionInspectorViewModel(
            new FakeMediaService(records.Select(record => Model(record.Path, record.Title!, record.Artist!)).ToArray()),
            new FakeLibrary(records),
            new FakeTagWriter(),
            new FakeArtworkService(),
            new FakeFilePicker(),
            new FakeDialogs(),
            new FakeFieldsEditor(),
            new FakeThumbnails(),
            new AppActivityService());

        await inspector.LoadAsync(new SelectionContext(
            records.Select(record => record.Path).ToArray(), records));

        Assert.Contains("3 tracks selected", inspector.Overview);
        Assert.Contains("FLAC: 2 (67%)", inspector.Overview);
        Assert.Contains("MP3: 1 (33%)", inspector.Overview);
        Assert.Contains("Vorbis comments: 2 (67%)", inspector.Overview);
        Assert.Contains("ID3v24: 1 (33%)", inspector.Overview);
    }

    [Fact]
    public async Task Selection_inspector_includes_codec_in_mp4_family_file_formats()
    {
        TrackRecord[] records =
        [
            Track("Artist", "Album", "AAC", "AAC", @"C:\one.mp4"),
            Track("Artist", "Album", "Lossless", "ALAC", @"C:\two.m4a"),
        ];
        var inspector = new SelectionInspectorViewModel(
            new FakeMediaService(records.Select(record => Model(record.Path, record.Title!, record.Artist!)).ToArray()),
            new FakeLibrary(records), new FakeTagWriter(), new FakeArtworkService(), new FakeFilePicker(),
            new FakeDialogs(), new FakeFieldsEditor(), new FakeThumbnails(), new AppActivityService());

        await inspector.LoadAsync(new SelectionContext(
            records.Select(record => record.Path).ToArray(), records));

        Assert.Contains("MP4 (AAC): 1 (50%)", inspector.Overview);
        Assert.Contains("M4A (ALAC): 1 (50%)", inspector.Overview);
    }

    [Fact]
    public async Task Selection_inspector_uses_full_cache_for_large_selections_and_marks_other_fields_unverified()
    {
        TrackRecord[] records = Enumerable.Range(0, 201)
            .Select(index => Track("Shared artist", "Album", $"Track {index}", "FLAC", $@"C:\Music\{index}.flac"))
            .ToArray();
        var writer = new FakeTagWriter();
        var inspector = new SelectionInspectorViewModel(
            new FakeMediaService(), new FakeLibrary(records), writer, new FakeArtworkService(),
            new FakeFilePicker(), new FakeDialogs(), new FakeFieldsEditor(),
            new FakeThumbnails(), new AppActivityService());

        await inspector.LoadAsync(new SelectionContext(
            records.Select(record => record.Path).ToArray(), records));

        EditableTagField artist = inspector.Fields.Single(item => item.Field == TagFields.Artist);
        EditableTagField genre = inspector.Fields.Single(item => item.Field == TagFields.Genre);
        Assert.Equal(FieldValueVerification.Exact, artist.Verification);
        Assert.Equal("Shared artist", artist.Value);
        Assert.Equal(FieldValueVerification.Unverified, genre.Verification);
        Assert.Null(genre.Value);
        Assert.False(genre.IsModified);

        genre.Value = "Ambient";
        await inspector.SaveTagsCommand.ExecuteAsync(null);

        TagEdit edit = Assert.Single(writer.Edits!);
        Assert.Equal(TagFields.Genre, edit.Field);
        Assert.Equal("Ambient", edit.Value);
    }

    [Fact]
    public async Task Selection_inspector_refuses_to_replace_a_dirty_selection_when_discard_is_cancelled()
    {
        MediaFileModel first = Model(@"C:\one.flac", "One", "Artist");
        MediaFileModel second = Model(@"C:\two.flac", "Two", "Artist");
        var inspector = new SelectionInspectorViewModel(
            new FakeMediaService(first, second), new FakeLibrary([]), new FakeTagWriter(),
            new FakeArtworkService(), new FakeFilePicker(), new RejectingDialogs(),
            new FakeFieldsEditor(), new FakeThumbnails(), new AppActivityService());
        await inspector.LoadAsync(new SelectionContext([first.Path]));
        inspector.Fields.Single(item => item.Field == TagFields.Title).Value = "Edited";

        bool changed = await inspector.TryLoadAsync(new SelectionContext([second.Path]));

        Assert.False(changed);
        Assert.Equal(first.Path, Assert.Single(inspector.Selection.Paths));
        Assert.True(inspector.HasUnsavedChanges);
    }

    [Fact]
    public async Task Selection_inspector_keeps_dirty_values_when_revert_is_cancelled()
    {
        MediaFileModel model = Model(@"C:\one.flac", "One", "Artist");
        var inspector = new SelectionInspectorViewModel(
            new FakeMediaService(model), new FakeLibrary([]), new FakeTagWriter(),
            new FakeArtworkService(), new FakeFilePicker(), new RejectingDialogs(),
            new FakeFieldsEditor(), new FakeThumbnails(), new AppActivityService());
        await inspector.LoadAsync(new SelectionContext([model.Path]));
        EditableTagField title = inspector.Fields.Single(item => item.Field == TagFields.Title);
        title.Value = "Edited";

        Assert.True(inspector.RevertCommand.CanExecute(null));
        await inspector.RevertCommand.ExecuteAsync(null);

        Assert.Equal("Edited", title.Value);
        Assert.True(inspector.HasUnsavedChanges);
    }

    [Fact]
    public async Task Library_filter_keeps_a_dirty_selection_visible_until_it_is_resolved()
    {
        var settings = new FakeSettings();
        TrackRecord[] records =
        [
            Track("Miles", "Kind of Blue", "So What", "FLAC", @"C:\Music\So What.flac"),
            Track("Massive Attack", "Mezzanine", "Teardrop", "MP3", @"C:\Music\Teardrop.mp3"),
        ];
        LibraryViewModel viewModel = BuildLibrary(settings, records);
        await viewModel.ReloadAsync();
        Assert.True(await viewModel.SelectAsync([viewModel.Rows[0]]));
        viewModel.Inspector.Fields.Single(item => item.Field == TagFields.Title).Value = "Edited title";

        viewModel.FilterText = "Artist:Massive";
        await viewModel.ApplyFilterNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, viewModel.Rows.Count);
        Assert.Contains(viewModel.Rows, row => row.Path == records[0].Path);
        Assert.Equal(records[0].Path, Assert.Single(viewModel.SelectedPaths));
        Assert.Contains("unsaved changes kept visible", viewModel.StatusText);
        INavigationGuard guard = Assert.IsAssignableFrom<INavigationGuard>(viewModel);
        Assert.True(guard.HasUnsavedChanges);
        Assert.True(await guard.ConfirmNavigationAsync());
        Assert.False(guard.HasUnsavedChanges);
    }

    [Fact]
    public void Library_row_exposes_track_and_disc_totals_for_typed_grid_columns()
    {
        var row = new LibraryRow(Track("Artist", "Album", "Title", "FLAC", @"C:\one.flac") with
        {
            TrackTotal = 12,
            DiscTotal = 3,
        });

        Assert.Equal(12, row.TrackTotal);
        Assert.Equal(3, row.DiscTotal);
    }

    [Fact]
    public async Task Selection_inspector_reports_mixed_artwork_without_showing_a_representative_cover()
    {
        string[] paths = [@"C:\one.flac", @"C:\two.flac"];
        var library = new FakeLibrary([]);
        library.ImageSignatures[paths[0]] = "cover-a";
        library.ImageSignatures[paths[1]] = "cover-b";
        var inspector = new SelectionInspectorViewModel(
            new FakeMediaService(paths.Select(path => Model(path, "Title", "Artist")).ToArray()),
            library,
            new FakeTagWriter(),
            new FakeArtworkService(),
            new FakeFilePicker(),
            new FakeDialogs(),
            new FakeFieldsEditor(),
            new FakeThumbnails(),
            new AppActivityService());

        await inspector.LoadAsync(new SelectionContext(paths));

        Assert.True(inspector.IsArtworkMixed);
        Assert.StartsWith("Mixed values", inspector.ArtworkSummary);
        Assert.Null(inspector.ArtworkSource);
    }

    [Fact]
    public async Task Selection_inspector_exposes_every_embedded_artwork_with_type_and_dimensions()
    {
        const string path = @"C:\one.flac";
        var library = new FakeLibrary([]);
        library.ImageSignatures[path] = "multi-artwork";
        MediaFileModel model = Model(path, "Title", "Artist") with
        {
            Artwork =
            [
                new ArtworkModel
                {
                    Category = "FrontCover", ImageType = "image/jpeg", Width = 1200, Height = 1200,
                    Size = 4096, Data = [1, 2, 3],
                },
                new ArtworkModel
                {
                    Category = "BackCover", Description = "Rear scan", ImageType = "image/png",
                    Width = 900, Height = 880, Size = 8192, Data = [4, 5, 6],
                },
            ],
        };
        var inspector = new SelectionInspectorViewModel(
            new FakeMediaService(model), library, new FakeTagWriter(), new FakeArtworkService(),
            new FakeFilePicker(), new FakeDialogs(), new FakeFieldsEditor(),
            new FakeThumbnails(), new AppActivityService());

        await inspector.LoadAsync(new SelectionContext([path]));

        Assert.Collection(inspector.ArtworkItems,
            front =>
            {
                Assert.Equal("Front cover", front.Label);
                Assert.Contains("1,200 × 1,200", front.Summary);
            },
            back =>
            {
                Assert.Equal("Back cover", back.Label);
                Assert.Contains("Rear scan", back.Summary);
            });
        Assert.Equal("2 embedded artworks", inspector.ArtworkSummary);
    }

    [Fact]
    public async Task Selection_inspector_saves_each_artwork_card_without_reencoding_it()
    {
        const string musicPath = @"C:\Music\one.flac";
        string outputPath = Path.Combine(Path.GetTempPath(),
            $"inspector-artwork-{Guid.NewGuid():N}.png");
        try
        {
            var library = new FakeLibrary([]);
            library.ImageSignatures[musicPath] = "artwork";
            MediaFileModel model = Model(musicPath, "One", "Artist") with
            {
                Artwork =
                [
                    new ArtworkModel
                    {
                        Category = "BackCover", ImageType = "image/png",
                        Width = 900, Height = 880, Size = 4, Data = [4, 5, 6, 7],
                    },
                ],
            };
            var files = new FakeFilePicker(saveFile: outputPath);
            var inspector = new SelectionInspectorViewModel(
                new FakeMediaService(model), library, new FakeTagWriter(),
                new FakeArtworkService(), files, new FakeDialogs(),
                new FakeFieldsEditor(), new FakeThumbnails(), new AppActivityService());
            await inspector.LoadAsync(new SelectionContext([musicPath]));

            await inspector.SaveArtworkItemToFileAsync(Assert.Single(inspector.ArtworkItems));

            Assert.Equal([4, 5, 6, 7], await File.ReadAllBytesAsync(
                outputPath, TestContext.Current.CancellationToken));
            Assert.Equal(".png", files.LastSaveExtension);
            Assert.Equal("one-back-cover.png", files.LastSuggestedName);
            Assert.Equal(MessageTone.Success, inspector.StatusTone);
            Assert.False(inspector.HasUnsavedChanges);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task Selection_inspector_saves_description_edits_and_individual_removals()
    {
        const string path = @"C:\one.flac";
        var library = new FakeLibrary([]);
        library.ImageSignatures[path] = "multi-artwork";
        MediaFileModel model = Model(path, "Title", "Artist") with
        {
            Artwork =
            [
                new ArtworkModel { Category = "FrontCover", ImageType = "image/jpeg", Width = 800, Height = 800, Size = 3, Data = [1, 2, 3] },
                new ArtworkModel { Category = "BackCover", ImageType = "image/png", Width = 700, Height = 700, Size = 3, Data = [4, 5, 6] },
            ],
        };
        var artworkService = new FakeArtworkService();
        var inspector = new SelectionInspectorViewModel(
            new FakeMediaService(model), library, new FakeTagWriter(), artworkService,
            new FakeFilePicker(), new FakeDialogs(), new FakeFieldsEditor(),
            new FakeThumbnails(), new AppActivityService());
        await inspector.LoadAsync(new SelectionContext([path]));

        ArtworkPreviewItem front = inspector.ArtworkItems[0];
        front.Description = "Restored front scan";
        inspector.RemoveArtworkItem(inspector.ArtworkItems[1]);
        Assert.True(inspector.SaveArtworkSetCommand.CanExecute(null));
        await inspector.SaveArtworkSetCommand.ExecuteAsync(null);

        ArtworkInput saved = Assert.Single(artworkService.SavedImages!);
        Assert.Equal(ID3v2Util.APICType.FrontCover, saved.Type);
        Assert.Equal("Restored front scan", saved.Description);
        Assert.Equal([1, 2, 3], saved.Data);
    }

    [Fact]
    public async Task Selection_inspector_adds_prepared_artwork_as_a_front_cover()
    {
        const string path = @"C:\one.flac";
        var artworkService = new FakeArtworkService(
            new PreparedImage([7, 8, 9], "image/jpeg", 640, 640));
        var inspector = new SelectionInspectorViewModel(
            new FakeMediaService(Model(path, "Title", "Artist")), new FakeLibrary([]),
            new FakeTagWriter(), artworkService, new FakeFilePicker(@"C:\cover.png"),
            new FakeDialogs(), new FakeFieldsEditor(), new FakeThumbnails(),
            new AppActivityService());
        await inspector.LoadAsync(new SelectionContext([path]));

        await inspector.AddArtworkCommand.ExecuteAsync(null);

        ArtworkPreviewItem added = Assert.Single(inspector.ArtworkItems);
        Assert.Equal(ID3v2Util.APICType.FrontCover, added.Type);
        Assert.Equal([7, 8, 9], added.Data);
        Assert.True(inspector.SaveArtworkSetCommand.CanExecute(null));
    }

    [Fact]
    public async Task Selection_inspector_replaces_one_artwork_without_losing_its_metadata()
    {
        const string path = @"C:\one.flac";
        var library = new FakeLibrary([]);
        library.ImageSignatures[path] = "artwork";
        MediaFileModel model = Model(path, "Title", "Artist") with
        {
            Artwork =
            [
                new ArtworkModel
                {
                    Category = "BackCover", Description = "Original booklet scan",
                    ImageType = "image/png", Width = 800, Height = 800, Size = 3,
                    Data = [1, 2, 3],
                },
            ],
        };
        var artworkService = new FakeArtworkService(
            new PreparedImage([9, 8, 7], "image/jpeg", 600, 600));
        var inspector = new SelectionInspectorViewModel(
            new FakeMediaService(model), library, new FakeTagWriter(), artworkService,
            new FakeFilePicker(@"C:\replacement.jpg"), new FakeDialogs(),
            new FakeFieldsEditor(), new FakeThumbnails(), new AppActivityService());
        await inspector.LoadAsync(new SelectionContext([path]));

        ArtworkPreviewItem item = Assert.Single(inspector.ArtworkItems);
        await inspector.ReplaceArtworkItemAsync(item);

        Assert.Equal(ID3v2Util.APICType.BackCover, item.Type);
        Assert.Equal("Original booklet scan", item.Description);
        Assert.Equal("image/jpeg", item.MimeType);
        Assert.Equal([9, 8, 7], item.Data);
        Assert.True(inspector.SaveArtworkSetCommand.CanExecute(null));
    }

    [Fact]
    public void Settings_unknown_stored_theme_falls_back_and_normalizes_preference()
    {
        var settings = new FakeSettings();
        settings.Preferences["manager.appearance.theme.v1"] = "LegacySepia";

        var viewModel = new SettingsViewModel(
            settings, new FakeFilePicker(), new FakeDialogs(), new FakeTheme());

        Assert.Equal("System", viewModel.SelectedTheme);
        Assert.Equal("System", viewModel.SelectedThemeChoice?.Name);
        Assert.Equal("System", settings.Preferences["manager.appearance.theme.v1"]);
        Assert.False(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public void Settings_steel_blue_theme_applies_immediately_and_persists()
    {
        var settings = new FakeSettings();
        var theme = new FakeTheme();
        var viewModel = new SettingsViewModel(
            settings, new FakeFilePicker(), new FakeDialogs(), theme);

        viewModel.SelectedThemeChoice = viewModel.ThemeChoices.Single(
            choice => choice.Name == "Steel Blue");

        Assert.Equal("Steel Blue", viewModel.SelectedTheme);
        Assert.Equal("Steel Blue", theme.Current);
        Assert.Equal("Steel Blue", settings.Preferences["manager.appearance.theme.v1"]);
        Assert.False(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public async Task Settings_new_library_and_roots_start_catalog_only()
    {
        var viewModel = new SettingsViewModel(
            new FakeSettings(), new FakeFilePicker(), new FakeDialogs(), new FakeTheme());

        await viewModel.NewConfigurationCommand.ExecuteAsync(null);

        Assert.Equal(LibraryProfilePresets.CatalogOnlyId, viewModel.SelectedLibraryProfile?.Id);
        Assert.Contains("catalog only (read-only)", viewModel.EffectivePolicySummary,
            StringComparison.OrdinalIgnoreCase);
        IndexTargetEditorRow first = Assert.Single(viewModel.IndexTargets);
        Assert.Equal(LibraryProfilePresets.CatalogOnlyId, first.ProfileId);
        Assert.Equal(LibraryRootPermissions.None, first.Permissions);
        Assert.True(first.IsReadOnly);
        Assert.False(first.Organize);

        viewModel.AddIndexTargetCommand.Execute(null);

        IndexTargetEditorRow second = viewModel.IndexTargets[1];
        Assert.Equal(LibraryProfilePresets.CatalogOnlyId, second.ProfileId);
        Assert.Equal(LibraryRootPermissions.None, second.Permissions);
        viewModel.SelectedLibraryProfile = viewModel.LibraryProfiles.Single(profile =>
            profile.Id == LibraryProfilePresets.ArtistAlbumId);
        Assert.Equal(LibraryProfilePresets.ArtistAlbumId, first.ProfileId);
        Assert.Equal(LibraryProfilePresets.ArtistAlbumId, second.ProfileId);
        Assert.True(first.AllowOrganization);
        Assert.False(first.IsReadOnly);
        second.AllowMetadataWrites = true;
        Assert.True(second.AllowMetadataWrites);
        Assert.Contains("metadata", second.PermissionSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Settings_guided_setup_starts_safe_and_reviews_effective_policy()
    {
        var viewModel = new SettingsViewModel(
            new FakeSettings(), new FakeFilePicker(), new FakeDialogs(), new FakeTheme());

        await viewModel.StartGuidedSetupCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsGuidedSetupActive);
        Assert.Equal(1, viewModel.SelectedTabIndex);
        Assert.Equal(LibraryProfilePresets.CatalogOnlyId,
            viewModel.SelectedLibraryProfile?.Id);
        Assert.True(Assert.Single(viewModel.IndexTargets).IsReadOnly);

        viewModel.ReviewGuidedSetupCommand.Execute(null);

        Assert.Equal(6, viewModel.SelectedTabIndex);
        Assert.Contains("catalog only", viewModel.EffectivePolicySummary,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Settings_ffmpeg_picker_accepts_extensionless_executables()
    {
        var picker = new FakeFilePicker(selectedFile: "/usr/local/bin/ffmpeg");
        var viewModel = new SettingsViewModel(
            new FakeSettings(), picker, new FakeDialogs(), new FakeTheme());

        await viewModel.BrowseFfmpegCommand.ExecuteAsync(null);

        Assert.Equal("/usr/local/bin/ffmpeg", viewModel.FfmpegPath);
        Assert.Equal("Choose ffmpeg executable", picker.LastPickTitle);
        Assert.Null(picker.LastPickTypes);
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
                OversizedArtworkByteThreshold = 5 * 1024 * 1024,
                OversizedArtworkDimensionThreshold = 3_200,
                DeleteSourcesAfterIngest = true,
                RemoveNonMusicAfterIngest = true,
                DeleteStaleCrossSyncFiles = true,
                CleanCrossSyncPlaylists = true,
                IndexTargets =
                [
                    new IndexTargetEntry
                    {
                        Target = @"Z:\Lossless",
                        Filter = "*.flac",
                        IndexFormats = [".flac", ".m4a"],
                        IndexIncludePatterns = ["Music/**", "*.flac"],
                        IndexExcludePatterns = ["Temp/**", "*.tmp"],
                        DefaultOffset = "/Music",
                        Organize = false,
                        UseItunesCanonicalNaming = true,
                        IngestRole = LibraryIngestRole.Cd,
                        RepresentationRole = LibraryRepresentationRole.LosslessByQuality,
                        IsSyncTarget = true,
                        Memberships =
                        [
                            new IndexTargetSetEntry { Name = "Lossless", Offset = "/FLAC" },
                            new IndexTargetSetEntry { Name = "Mobile", Offset = "/FLAC" },
                            new IndexTargetSetEntry { Name = "Desktop" },
                        ],
                    },
                ],
                SyncPlaylists = ["Favorites", "RoadTrip"],
                PlaylistSources =
                [
                    new PlaylistSourceEntry
                    {
                        Location = @"Z:\Source Playlists",
                        Type = "m3u",
                        Recursive = false,
                    },
                ],
                PlaylistTargets =
                [
                    new PlaylistTargetEntry
                    {
                        Target = @"Z:\Playlists", Type = "wpl", Sets = ["Lossless"],
                        PathStyle = "relative",
                        Encoding = "utf-16",
                        EmitByteOrderMark = false,
                        LineEnding = "lf",
                        IncludeExtendedInfo = false,
                        FileNameTransform = "sanitize",
                        MaxTrackCount = 900,
                        CollisionPolicy = LibraryPathCollisionPolicy.Hash,
                    },
                ],
                ExportProfiles =
                [
                    new LibraryExportProfile(
                        "portable", "Portable copy", false,
                        ExportSelectionPolicy.EntireLibrary,
                        new(ExportTransformMode.Copy),
                        new(PreserveSourceLayout: true),
                        new(ExportArtworkMode.Embedded),
                        new(),
                        new("local-filesystem", @"Z:\Portable"),
                        new()),
                ],
            }.Save(configurationPath);
            var settings = new AppSettings(settingsPath);
            settings.LoadConfig(configurationPath);
            var viewModel = new SettingsViewModel(settings, new FakeFilePicker(),
                new FakeDialogs(), new FakeTheme());

            Assert.False(viewModel.HasUnsavedChanges);
            Assert.False(viewModel.SaveConfigurationCommand.CanExecute(null));

            viewModel.EditCurrentConfigurationCommand.Execute(null);
            Assert.Equal(1, viewModel.SelectedTabIndex);

            // The active configuration is restored directly into the editor; users should not
            // need to click Edit active before their roots and workflow targets appear.
            IndexTargetEditorRow root = Assert.Single(viewModel.IndexTargets);
            Assert.Equal(LibraryProfilePresets.LegacyId, viewModel.SelectedLibraryProfile?.Id);
            Assert.Equal(LibraryProfilePresets.LegacyId, root.ProfileId);
            Assert.True(root.AllowMetadataWrites);
            Assert.True(root.AllowArtworkWrites);
            Assert.False(root.AllowOrganization);
            Assert.True(root.AllowIngestOutput);
            Assert.True(root.AllowSynchronizationOutput);
            Assert.True(root.UseItunesCanonicalNaming);
            Assert.Equal(LibraryIngestRole.Cd, root.IngestRole);
            Assert.Equal(LibraryRepresentationRole.LosslessByQuality,
                root.RepresentationRole);
            Assert.True(root.IsSyncTarget);
            Assert.Equal(".flac, .m4a", root.IndexFormats);
            Assert.Equal("Music/**; *.flac", root.IndexIncludePatterns);
            Assert.Equal("Temp/**; *.tmp", root.IndexExcludePatterns);
            Assert.Equal(2, root.Memberships.Count);
            Assert.Equal("Lossless, Mobile", root.Memberships[0].Name);
            Assert.Equal("/FLAC", root.Memberships[0].Offset);
            Assert.Equal("Desktop", root.Memberships[1].Name);
            Assert.Equal("/Music", root.Memberships[1].Offset);
            Assert.Equal(220, viewModel.LengthLimit);
            Assert.Equal(180, viewModel.DiscNumLengthLimit);
            Assert.Equal("aac_encoder", viewModel.AacEncoder);
            Assert.Equal(288, viewModel.AacBitrateKbps);
            Assert.Equal(5m, viewModel.OversizedArtworkSizeThresholdMib);
            Assert.Equal(5 * 1024 * 1024, viewModel.OversizedArtworkByteThreshold);
            Assert.Equal(3_200, viewModel.OversizedArtworkDimensionThreshold);
            Assert.True(viewModel.DeleteSourcesAfterIngest);
            Assert.True(viewModel.RemoveNonMusicAfterIngest);
            Assert.True(viewModel.DeleteStaleCrossSyncFiles);
            Assert.True(viewModel.CleanCrossSyncPlaylists);
            Assert.Equal(["Favorites", "RoadTrip"], viewModel.SyncPlaylists.Select(row => row.Name));
            PlaylistSourceEditorRow playlistSource = Assert.Single(viewModel.PlaylistSources);
            Assert.Equal(@"Z:\Source Playlists", playlistSource.Location);
            PlaylistTargetEditorRow playlistTarget = Assert.Single(viewModel.PlaylistTargets);
            Assert.Equal("Lossless", playlistTarget.Sets);
            Assert.Equal("relative", playlistTarget.PathStyle);
            Assert.Equal("utf-16", playlistTarget.Encoding);
            Assert.False(playlistTarget.EmitByteOrderMark);
            Assert.Equal(LibraryPathCollisionPolicy.Hash, playlistTarget.CollisionPolicy);
            ExportProfileEditorRow export = Assert.Single(viewModel.ExportProfiles);
            Assert.Equal("Portable copy", export.Name);
            Assert.False(export.Enabled);

            root.Memberships[0].Name = "Lossless, Mobile, Portable";
            root.Memberships[0].Offset = "/Portable/FLAC";
            root.IndexFormats = ".flac";
            root.IndexExcludePatterns = "Temp/**; Drafts/**";
            Assert.NotNull(viewModel.AdvancedProfile);
            viewModel.AdvancedProfile!.CollisionPolicy = LibraryPathCollisionPolicy.Hash;
            viewModel.AdvancedProfile.ComponentLengthLimit = 180;
            viewModel.AdvancedProfile.IdentityStripsFormatSuffixes = false;
            viewModel.AdvancedProfile.PreserveReplayGain = false;
            viewModel.AdvancedProfile.HealthRules.Single(rule =>
                rule.Id == LibraryHealthRuleIds.LossyFile).Enabled = false;
            viewModel.AdvancedProfile.IngestRecipes[0].PreserveMetadata = false;
            SidecarRuleEditorRow newSidecar = SidecarRuleEditorRow.Create();
            newSidecar.Patterns = "*.lrc, lyrics/**";
            viewModel.AdvancedProfile.SidecarRules.Add(newSidecar);
            playlistSource.Recursive = true;
            playlistTarget.Type = "m3u";
            playlistTarget.MaxTrackCount = 750;
            playlistTarget.LineEnding = "crlf";
            export.Enabled = true;
            export.ExtraFileDisposition = ExportExtraFileDisposition.Quarantine;
            viewModel.OversizedArtworkSizeThresholdMib = 3.5m;
            viewModel.OversizedArtworkDimensionThreshold = 2_500;
            Assert.True(viewModel.HasUnsavedChanges);
            Assert.True(viewModel.SaveConfigurationCommand.CanExecute(null));
            await viewModel.SaveConfigurationCommand.ExecuteAsync(null);
            Assert.False(viewModel.HasUnsavedChanges);
            Assert.False(viewModel.SaveConfigurationCommand.CanExecute(null));

            EditableLibraryConfig saved = EditableLibraryConfig.Load(configurationPath);
            IndexTargetEntry savedRoot = Assert.Single(saved.IndexTargets);
            Assert.Null(savedRoot.DefaultOffset);
            Assert.Equal(["Lossless", "Mobile", "Portable", "Desktop"],
                savedRoot.Memberships.Select(membership => membership.Name));
            Assert.All(savedRoot.Memberships.Take(3), membership =>
                Assert.Equal("/Portable/FLAC", membership.Offset));
            Assert.Equal("/Music", savedRoot.Memberships[3].Offset);
            Assert.True(savedRoot.UseItunesCanonicalNaming);
            Assert.True(savedRoot.IsSyncTarget);
            Assert.Equal(LibraryIngestRole.Cd, savedRoot.IngestRole);
            Assert.Equal(LibraryRepresentationRole.LosslessByQuality,
                savedRoot.RepresentationRole);
            Assert.Equal([".flac"], savedRoot.IndexFormats);
            Assert.Equal(["Music/**", "*.flac"], savedRoot.IndexIncludePatterns);
            Assert.Equal(["Temp/**", "Drafts/**"], savedRoot.IndexExcludePatterns);
            Assert.Equal(LibraryPathCollisionPolicy.Hash,
                saved.ActiveProfile.Naming.CollisionPolicy);
            Assert.Equal(180, saved.ActiveProfile.Naming.ComponentLengthLimit);
            Assert.False(saved.ActiveProfile.AlbumIdentity.StripFormatSuffixes);
            Assert.False(saved.ActiveProfile.Metadata.PreserveReplayGain);
            Assert.False(saved.ActiveProfile.Health.Find(
                LibraryHealthRuleIds.LossyFile)!.Enabled);
            Assert.False(saved.ActiveProfile.Ingest.Recipes[0].PreserveMetadata);
            Assert.Contains(saved.ActiveProfile.Sidecars.Rules,
                rule => rule.Patterns.Contains("*.lrc"));
            Assert.True(saved.DeleteStaleCrossSyncFiles);
            Assert.True(saved.CleanCrossSyncPlaylists);
            Assert.Equal((int)(3.5m * 1024 * 1024), saved.OversizedArtworkByteThreshold);
            Assert.Equal(2_500, saved.OversizedArtworkDimensionThreshold);
            Assert.Equal(["Favorites", "RoadTrip"], saved.SyncPlaylists);
            Assert.True(Assert.Single(saved.PlaylistSources).Recursive);
            Assert.Equal("m3u", Assert.Single(saved.PlaylistTargets).Type);
            Assert.Equal(["Lossless"], saved.PlaylistTargets[0].Sets);
            Assert.Equal(750, saved.PlaylistTargets[0].MaxTrackCount);
            Assert.Equal("crlf", saved.PlaylistTargets[0].LineEnding);
            LibraryExportProfile savedExport = Assert.Single(saved.ExportProfiles);
            Assert.True(savedExport.Enabled);
            Assert.Equal(ExportExtraFileDisposition.Quarantine,
                savedExport.Reconciliation.ExtraFiles);

            viewModel.LengthLimit = 0;
            Assert.False(viewModel.SaveConfigurationCommand.CanExecute(null));
            Assert.Contains("Path length limit", viewModel.ValidationSummary);
            viewModel.OpenValidationCommand.Execute(null);
            Assert.Equal(3, viewModel.SelectedTabIndex);

            viewModel.LengthLimit = 255;
            viewModel.OversizedArtworkDimensionThreshold = 0;
            Assert.False(viewModel.SaveConfigurationCommand.CanExecute(null));
            Assert.Contains("Oversized artwork dimension", viewModel.ValidationSummary);
            viewModel.OpenValidationCommand.Execute(null);
            Assert.Equal(4, viewModel.SelectedTabIndex);
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
                TextFields = [new TextField("CUSTOM_NOTE", "First")],
            },
            new MediaFileModel
            {
                Path = @"C:\two.flac", IsWritable = true,
                KnownFields = [new TagFieldValue(TagFields.Grouping, "Second")],
                TextFields = [new TextField("CUSTOM_NOTE", "Second")],
            });
        var writer = new FakeTagWriter();
        var viewModel = new FieldsDialogViewModel(
            media, writer, [@"C:\one.flac", @"C:\two.flac"]);
        await viewModel.Loading;

        FieldRow grouping =
            viewModel.Rows.Single(row => row.Field == TagFields.Grouping);
        Assert.True(grouping.IsMixed);
        grouping.Value = "Canonical grouping";

        FieldRow copyright =
            viewModel.Rows.Single(row => row.Field == TagFields.Copyright);
        viewModel.RemoveFieldCommand.Execute(copyright);

        viewModel.FieldToAdd = TagFields.Mood;
        viewModel.AddFieldCommand.Execute(null);
        viewModel.Rows.Single(row => row.Field == TagFields.Mood).Value = "Calm";

        FieldRow custom =
            viewModel.Rows.Single(row => row.UserStringKey == "CUSTOM_NOTE");
        Assert.True(custom.IsMixed);
        custom.Value = "Canonical note";

        viewModel.NewUserStringName = "DJ_SET";
        viewModel.AddUserStringCommand.Execute(null);
        viewModel.Rows.Single(row => row.UserStringKey == "DJ_SET").Value = "Warmup";

        bool? closed = null;
        viewModel.CloseRequested += result => closed = result;
        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Null(closed);
        Assert.True(viewModel.IsConfirmingSave);
        Assert.Contains("5 field change(s) to 2 file(s)", viewModel.StatusMessage);
        Assert.Contains("no recovery journal", viewModel.StatusMessage);
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Contains(writer.Edits!, edit =>
            edit.Field == TagFields.Grouping && edit.Value == "Canonical grouping");
        Assert.Contains(writer.Edits!, edit =>
            edit.Field == TagFields.Copyright && edit.Value is null);
        Assert.Contains(writer.Edits!, edit =>
            edit.Field == TagFields.Mood && edit.Value == "Calm");
        Assert.Contains(writer.Edits!, edit =>
            edit.UserStringKey == "CUSTOM_NOTE" && edit.Value == "Canonical note");
        Assert.Contains(writer.Edits!, edit =>
            edit.UserStringKey == "DJ_SET" && edit.Value == "Warmup");
    }

    private static LibraryViewModel BuildLibrary(
        FakeSettings settings,
        IReadOnlyList<TrackRecord> records,
        FakeLibrary? library = null)
    {
        library ??= new FakeLibrary(records);
        var activity = new AppActivityService();
        var inspector = new SelectionInspectorViewModel(new FakeMediaService(), library, new FakeTagWriter(),
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
    public int IndexCallCount { get; private set; }
    public TaskCompletionSource<bool> IndexStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool>? IndexRelease { get; init; }
    public Dictionary<string, string> ImageSignatures { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsReady => true;
    public async Task<(int Added, int Modified, int Removed, int Unchanged)> IndexAsync(
        IProgress<IndexProgress>? progress = null, CancellationToken ct = default)
    {
        IndexCallCount++;
        IndexStarted.TrySetResult(true);
        if (IndexRelease is not null)
            await IndexRelease.Task.WaitAsync(ct);
        return (0, 0, 0, records.Count);
    }
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
        => Task.FromResult<IReadOnlyList<string>>(paths
            .Select(path => ImageSignatures.GetValueOrDefault(path) ?? "")
            .ToArray());
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

internal sealed class FakeArtworkService(PreparedImage? prepared = null) : IArtworkService
{
    public IReadOnlyList<ArtworkInput>? SavedImages { get; private set; }
    public bool SupportsWrite(string musicPath) => true;
    public Task<ArtworkOpResult> SetCoverFromFileAsync(string musicPath, string imagePath, int maxDimension = 0, CancellationToken ct = default) => Success();
    public Task<ArtworkOpResult> ScrubAsync(string musicPath, int maxDimension, int quality = 90, CancellationToken ct = default) => Success();
    public Task<ArtworkOpResult> RemoveAsync(string musicPath, CancellationToken ct = default) => Success();
    public Task<PreparedImage?> PrepareFromFileAsync(string imagePath, int maxDimension = 0, CancellationToken ct = default) => Task.FromResult(prepared);
    public Task<PreparedImage?> PrepareFromBytesAsync(byte[] data, int maxDimension = 0, int quality = 90, CancellationToken ct = default) => Task.FromResult<PreparedImage?>(null);
    public Task<ArtworkOpResult> SaveImagesAsync(string musicPath, IReadOnlyList<ArtworkInput> images, CancellationToken ct = default)
    {
        SavedImages = images.ToArray();
        return Success();
    }
    private static Task<ArtworkOpResult> Success() => Task.FromResult(new ArtworkOpResult { Success = true });
}

internal sealed class FakeFilePicker(
    string? selectedFile = null,
    string? saveFile = null) : IFilePickerService
{
    public string? LastSuggestedName { get; private set; }
    public string? LastSaveExtension { get; private set; }
    public string? LastPickTitle { get; private set; }
    public IReadOnlyList<FilePickerType>? LastPickTypes { get; private set; }
    public Task<string?> PickFileAsync(string title, IReadOnlyList<FilePickerType>? types = null)
    {
        LastPickTitle = title;
        LastPickTypes = types;
        return Task.FromResult(selectedFile);
    }
    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    public Task<string?> SaveFileAsync(string title, string suggestedName, string extension)
    {
        LastSuggestedName = suggestedName;
        LastSaveExtension = extension;
        return Task.FromResult(saveFile);
    }
}

internal sealed class FakeDialogs : IDialogCoordinator
{
    public Task<bool> ConfirmAsync(string title, string message, string primaryText) => Task.FromResult(true);
    public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
}

internal sealed class RejectingDialogs : IDialogCoordinator
{
    public Task<bool> ConfirmAsync(string title, string message, string primaryText) => Task.FromResult(false);
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
