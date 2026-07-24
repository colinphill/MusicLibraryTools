using System.Collections.Immutable;
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
    public async Task Library_path_identity_matches_the_host_filesystem()
    {
        string upperPath = Path.Combine(
            Path.GetTempPath(),
            "CaseSensitiveTrack.flac");
        string lowerPath = Path.Combine(
            Path.GetTempPath(),
            "casesensitivetrack.flac");
        TrackRecord[] records =
        [
            Track("Artist", "Album", "Upper", "FLAC", upperPath),
            Track("Artist", "Album", "Lower", "FLAC", lowerPath),
        ];
        LibraryViewModel viewModel =
            BuildLibrary(new FakeSettings(), records);
        await viewModel.ReloadAsync();

        await viewModel.SelectAsync(viewModel.Rows);
        viewModel.SetHealthFilter([upperPath]);
        await viewModel.ApplyFilterNowAsync(
            TestContext.Current.CancellationToken);

        int expectedDistinctPaths =
            OperatingSystem.IsWindows() ? 1 : 2;
        Assert.Equal(
            expectedDistinctPaths,
            viewModel.SelectedPaths.Count);
        Assert.Equal(
            OperatingSystem.IsWindows() ? 2 : 1,
            viewModel.Rows.Count);
        if (!OperatingSystem.IsWindows())
            Assert.Equal("Upper", Assert.Single(viewModel.Rows).Title);
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
        viewModel.VisualFilterExpression =
            new LibraryFilterCondition(
                LibraryFilterField.Custom("DJ_SET"),
                LibraryFilterComparison.Present);

        viewModel.SaveNamedView("Mastering", columns, new LibrarySortState("codec", true));

        LibraryViewDefinition saved = Assert.Single(viewModel.SavedViews);
        Assert.Equal("Mastering", saved.Name);
        Assert.Equal(92, saved.Columns[0].Width);
        Assert.False(saved.Columns[1].Visible);
        Assert.Equal("codec", saved.Sort!.Key);
        Assert.True(saved.Sort.Descending);
        Assert.IsType<LibraryFilterCondition>(
            saved.VisualFilter);
        Assert.Contains("manager.library.views.v1", settings.Preferences.Keys);
    }

    [Fact]
    public void SavedViewFromBeforeVisualFiltersStillLoadsWithoutDataLoss()
    {
        var settings =
            new FakeSettings();
        settings.Preferences[
                "manager.library.views.v1"] =
            System.Text.Json.JsonSerializer.Serialize(
                new[]
                {
                    new
                    {
                        Name = "Legacy view",
                        Filter = "Artist:Miles",
                        FilterMode =
                            FilterMode.Substring,
                        Columns =
                            new[]
                            {
                                new
                                {
                                    Key = "title",
                                    Width = (double?)320,
                                    DisplayIndex = 0,
                                    Visible = true,
                                },
                            },
                        Sort = new
                        {
                            Key = "title",
                            Descending = false,
                        },
                    },
                });

        LibraryViewModel viewModel =
            BuildLibrary(settings, []);

        LibraryViewDefinition view =
            Assert.Single(
                viewModel.SavedViews);
        Assert.Equal(
            "Legacy view",
            view.Name);
        Assert.Equal(
            "Artist:Miles",
            view.Filter);
        Assert.Equal(
            320,
            Assert.Single(view.Columns).Width);
        Assert.Equal(
            "title",
            view.Sort!.Key);
        Assert.Null(
            view.VisualFilter);
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
        var operations =
            new FakeMetadataOperationService();
        var inspector = new SelectionInspectorViewModel(media, new FakeLibrary([]), writer, new FakeArtworkService(),
            new FakeFilePicker(), new FakeDialogs(), new FakeFieldsEditor(),
            new FakeThumbnails(), new AppActivityService(),
            operations);

        await inspector.LoadAsync(new SelectionContext([@"C:\one.flac", @"C:\two.flac"]));
        EditableTagField artist = inspector.Fields.Single(field => field.Field == TagFields.Artist);
        Assert.True(artist.IsMixed);

        artist.Value = "Canonical Artist";
        await inspector.SaveTagsCommand.ExecuteAsync(null);

        Assert.Null(writer.Edits);
        Assert.Equal(2, operations.PreviewedValueEdits.Count);
        MetadataValueEdit edit = Assert.Single(
            operations.PreviewedValueEdits[
                @"C:\one.flac"]);
        Assert.Equal(
            TagFields.Artist,
            edit.Field.KnownField);
        Assert.Equal(
            ["Canonical Artist"],
            edit.Values);
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
    public async Task Selection_inspector_prefers_lossless_shared_documents_for_fields_and_artwork()
    {
        const string path = @"C:\one.flac";
        var library = new FakeLibrary([]);
        library.ImageSignatures[path] =
            "authoritative-artwork";
        var documents =
            new FakeMetadataDocumentService(
                new MediaDocument(
                    path,
                    [new(
                        "VorbisComment",
                        [
                            new(
                                MetadataFieldKey.Known(
                                    TagFields.Title),
                                ["Authoritative title"]),
                            new(
                                MetadataFieldKey.Known(
                                    TagFields.Genre),
                                ["Rock", "Alternative"]),
                        ],
                        true,
                        true,
                        true,
                        true)],
                    [
                        new ArtworkModel
                        {
                            Category = "BackCover",
                            Description =
                                "Lossless rear scan",
                            ImageType = "image/png",
                            Width = 900,
                            Height = 880,
                            Size = 3,
                            Data = [4, 5, 6],
                        },
                    ],
                    new()
                    {
                        CodecName = "FLAC",
                    },
                    new(
                        path,
                        10,
                        DateTime.UtcNow,
                        "document-hash"),
                    true));
        var inspector = new SelectionInspectorViewModel(
            new FakeMediaService(
                Model(
                    path,
                    "Legacy title",
                    "Legacy artist")),
            library,
            new FakeTagWriter(),
            new FakeArtworkService(),
            new FakeFilePicker(),
            new FakeDialogs(),
            new FakeFieldsEditor(),
            new FakeThumbnails(),
            new AppActivityService(),
            metadataDocuments: documents);

        await inspector.LoadAsync(
            new SelectionContext([path]));

        Assert.Equal(
            "Authoritative title",
            inspector.Fields.Single(field =>
                field.Field ==
                TagFields.Title).Value);
        EditableTagField genre =
            inspector.Fields.Single(field =>
                field.Field ==
                TagFields.Genre);
        Assert.True(genre.IsMixed);
        Assert.Null(genre.Value);
        ArtworkPreviewItem artwork =
            Assert.Single(inspector.ArtworkItems);
        Assert.Equal(
            ID3v2Util.APICType.BackCover,
            artwork.Type);
        Assert.Equal(
            "Lossless rear scan",
            artwork.Description);
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
            new FakeThumbnails(), new AppActivityService(),
            metadataDocuments:
                new FakeMetadataDocumentService());

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
    public async Task Selection_inspector_keeps_malformed_artwork_editable_and_loads_healthy_siblings()
    {
        const string path = @"C:\one.flac";
        var library = new FakeLibrary([]);
        library.ImageSignatures[path] = "mixed-validity-artwork";
        MediaFileModel model = Model(path, "Title", "Artist") with
        {
            Artwork =
            [
                new ArtworkModel
                {
                    Category = "FrontCover",
                    ImageType = "image/jpeg",
                    Width = 1200,
                    Height = 1200,
                    Size = 3,
                    Data = [1, 2, 3],
                },
                new ArtworkModel
                {
                    Category = "BackCover",
                    ImageType = "image/png",
                    Width = 900,
                    Height = 880,
                    Size = 3,
                    Data = [4, 5, 6],
                },
            ],
        };
        var thumbnails = new SelectiveThumbnailService();
        var inspector = new SelectionInspectorViewModel(
            new FakeMediaService(model),
            library,
            new FakeTagWriter(),
            new FakeArtworkService(),
            new FakeFilePicker(),
            new FakeDialogs(),
            new FakeFieldsEditor(),
            thumbnails,
            new AppActivityService());

        await inspector.LoadAsync(new SelectionContext([path]));

        Assert.Collection(
            inspector.ArtworkItems,
            invalid => Assert.Null(invalid.Source),
            valid => Assert.NotNull(valid.Source));
        Assert.Same(inspector.ArtworkItems[1].Source, inspector.ArtworkSource);
        Assert.True(inspector.IsStatusWarning);
        Assert.Contains("1 embedded artwork image could not be decoded",
            inspector.StatusMessage);

        inspector.RemoveArtworkItem(inspector.ArtworkItems[0]);

        Assert.Single(inspector.ArtworkItems);
        Assert.True(inspector.HasPendingArtworkChanges);
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
        var operations =
            new FakeMetadataOperationService();
        var inspector = new SelectionInspectorViewModel(
            new FakeMediaService(model), library, new FakeTagWriter(), artworkService,
            new FakeFilePicker(), new FakeDialogs(), new FakeFieldsEditor(),
            new FakeThumbnails(), new AppActivityService(),
            operations);
        await inspector.LoadAsync(new SelectionContext([path]));

        ArtworkPreviewItem front = inspector.ArtworkItems[0];
        front.Description = "Restored front scan";
        inspector.RemoveArtworkItem(inspector.ArtworkItems[1]);
        Assert.True(inspector.SaveArtworkSetCommand.CanExecute(null));
        await inspector.SaveArtworkSetCommand.ExecuteAsync(null);

        Assert.Null(artworkService.SavedImages);
        ArtworkInput saved = Assert.Single(
            operations.PreviewedArtworkSets[
                path].Images);
        Assert.Equal(ID3v2Util.APICType.FrontCover, saved.Type);
        Assert.Equal("Restored front scan", saved.Description);
        Assert.Equal([1, 2, 3], saved.Data);
    }

    [Fact]
    public async Task Selection_inspector_routes_artwork_shortcuts_through_reviewed_plans()
    {
        const string path = @"C:\one.flac";
        var library = new FakeLibrary([]);
        library.ImageSignatures[path] = "artwork";
        MediaFileModel model = Model(
            path,
            "Title",
            "Artist") with
        {
            Artwork =
            [
                new ArtworkModel
                {
                    Category = "FrontCover",
                    ImageType = "image/jpeg",
                    Width = 800,
                    Height = 800,
                    Size = 3,
                    Data = [1, 2, 3],
                },
            ],
        };
        var operations =
            new FakeMetadataOperationService();
        var inspector = new SelectionInspectorViewModel(
            new FakeMediaService(model),
            library,
            new FakeTagWriter(),
            new FakeArtworkService(
                new PreparedImage(
                    [9, 8, 7],
                    "image/jpeg",
                    600,
                    600)),
            new FakeFilePicker(@"C:\cover.jpg"),
            new FakeDialogs(),
            new FakeFieldsEditor(),
            new FakeThumbnails(),
            new AppActivityService(),
            operations);
        await inspector.LoadAsync(
            new SelectionContext([path]));

        await inspector.ReplaceArtworkCommand
            .ExecuteAsync(null);

        Assert.Equal(
            ArtworkValueEditMode.ReplaceFrontCover,
            operations.PreviewedArtworkEdits[
                path].Mode);

        inspector.ArtworkMaxDimension = 512;
        await inspector.ScrubArtworkCommand
            .ExecuteAsync(null);

        Assert.Equal(
            512,
            operations.PreviewedArtworkSets[
                path].MaxDimension);
        Assert.Single(
            operations.PreviewedArtworkSets[
                path].Images);

        await inspector.RemoveArtworkCommand
            .ExecuteAsync(null);

        Assert.Equal(
            ArtworkValueEditMode.RemoveAll,
            operations.PreviewedArtworkEdits[
                path].Mode);
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
    public void Settings_online_metadata_tools_are_personal_and_persist_immediately()
    {
        var settings = new FakeSettings();
        settings.Preferences[AudioFingerprintService.ExecutablePreferenceKey] =
            "stored-fpcalc";
        settings.Preferences[
            OptimFrogFingerprintInputService
                .ToolsDirectoryPreferenceKey] =
            "stored-optimfrog";
        var viewModel = new SettingsViewModel(
            settings, new FakeFilePicker(), new FakeDialogs(), new FakeTheme());

        Assert.Equal("stored-fpcalc", viewModel.FpcalcPath);
        Assert.Equal(
            "stored-optimfrog",
            viewModel.OptimFrogToolsDirectory);
        viewModel.FpcalcPath = "new-fpcalc";
        viewModel.OptimFrogToolsDirectory =
            "new-optimfrog";
        viewModel.AcoustIdClientKey = "client-key";
        viewModel.OfflineMode = true;

        Assert.Equal(
            "new-fpcalc",
            settings.Preferences[AudioFingerprintService.ExecutablePreferenceKey]);
        Assert.Equal(
            "new-optimfrog",
            settings.Preferences[
                OptimFrogFingerprintInputService
                    .ToolsDirectoryPreferenceKey]);
        Assert.Equal(
            "client-key",
            settings.Preferences[AcoustIdLookupService.ClientKeyPreference]);
        Assert.Equal(
            bool.TrueString,
            settings.Preferences[ProviderNetworkPolicy.OfflinePreferenceKey]);
        Assert.False(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public async Task Settings_stores_Discogs_token_only_in_secret_store()
    {
        var settings = new FakeSettings();
        var secrets = new SessionSecretStore();
        var viewModel = new SettingsViewModel(
            settings,
            new FakeFilePicker(),
            new FakeDialogs(),
            new FakeTheme(),
            secrets);
        viewModel.DiscogsToken = "personal-token";

        await viewModel.SaveDiscogsTokenCommand.ExecuteAsync(null);

        Assert.Equal(
            "personal-token",
            await secrets.ReadAsync(
                DiscogsMetadataProvider.TokenSecretKey,
                TestContext.Current.CancellationToken));
        Assert.Null(viewModel.DiscogsToken);
        Assert.DoesNotContain(
            settings.Preferences.Keys,
            key => key.Contains("discogs",
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            "session",
            viewModel.DiscogsCredentialStatus,
            StringComparison.OrdinalIgnoreCase);

        await viewModel.ClearDiscogsTokenCommand.ExecuteAsync(null);

        Assert.Null(await secrets.ReadAsync(
            DiscogsMetadataProvider.TokenSecretKey,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Audio_discovery_labels_offline_cached_candidates()
    {
        var fingerprint = new AudioFingerprint(
            @"C:\music\track.flac",
            "AQAD",
            TimeSpan.FromSeconds(42),
            42);
        var lookup = new AcoustIdLookupResult(
            fingerprint,
            [new(
                Guid.Parse("9ff43b6a-4f16-427c-93c2-92307ca505e0"),
                0.91,
                [Guid.Parse(
                    "cd2e7c47-16f5-46c6-a37c-a1eb7bf599ff")])],
            DateTimeOffset.UtcNow,
            FromCache: true,
            OfflineFallback: true);
        var result = new AcoustIdDiscoveryResult(
        [
            new(
                fingerprint.Path,
                fingerprint,
                lookup,
                []),
        ]);

        AudioDiscoveryRow row = Assert.Single(
            AudioDiscoveryRows.Create(result));

        Assert.Equal("Offline cached candidate", row.Status);
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
        Assert.False(first.AllowOrganization);

        viewModel.AddIndexTargetCommand.Execute(null);

        IndexTargetEditorRow second = viewModel.IndexTargets[1];
        Assert.Equal(LibraryProfilePresets.CatalogOnlyId, second.ProfileId);
        Assert.Equal(LibraryRootPermissions.None, second.Permissions);
        viewModel.SelectedLibraryProfile = viewModel.LibraryProfiles.Single(profile =>
            profile.Id == LibraryProfilePresets.ArtistAlbumId);
        Assert.Equal(LibraryProfilePresets.CatalogOnlyId, first.ProfileId);
        Assert.Equal(LibraryProfilePresets.CatalogOnlyId, second.ProfileId);
        first.ProfileChoice = first.ProfileChoices.Single(profile =>
            profile.Id == LibraryProfilePresets.ArtistAlbumId);
        Assert.Equal(LibraryProfilePresets.ArtistAlbumId, first.ProfileId);
        Assert.False(first.AllowOrganization);
        Assert.True(first.IsReadOnly);
        second.AllowMetadataWrites = true;
        Assert.True(second.AllowMetadataWrites);
        Assert.Contains("metadata", second.PermissionSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Settings_ingest_recipe_root_picker_tracks_path_and_stable_id()
    {
        var viewModel = new SettingsViewModel(
            new FakeSettings(), new FakeFilePicker(), new FakeDialogs(), new FakeTheme());
        await viewModel.NewConfigurationCommand.ExecuteAsync(null);
        viewModel.SelectedLibraryProfile = viewModel.LibraryProfiles.Single(profile =>
            profile.Id == LibraryProfilePresets.ArtistAlbumId);
        IndexTargetEditorRow root = Assert.Single(viewModel.IndexTargets);
        root.Path = @"D:\Music\Paired CD";
        root.AllowIngestOutput = true;
        viewModel.AddIngestRecipeCommand.Execute(null);
        IngestRecipeEditorRow recipe = Assert.Single(
            Assert.IsType<IngestProfileEditorRow>(viewModel.AdvancedIngestProfile)
                .Recipes);

        recipe.DestinationRootChoice = Assert.Single(
            recipe.DestinationRootChoices, choice => choice.Id == root.Id);

        Assert.Equal(root.Id, recipe.DestinationRootId);
        Assert.Equal(@"D:\Music\Paired CD",
            recipe.DestinationRootChoice?.Label);
        root.Path = @"E:\Archive\CD Fallback";
        Assert.Equal(root.Id, recipe.DestinationRootId);
        Assert.Equal(@"E:\Archive\CD Fallback",
            recipe.DestinationRootChoice?.Label);

        viewModel.RemoveIndexTargetCommand.Execute(root);

        Assert.Equal(root.Id, recipe.DestinationRootId);
        Assert.StartsWith("Missing root", recipe.DestinationRootChoice?.Label);
        Assert.Equal(root.Id, recipe.Build().DestinationRootId);
    }

    [Fact]
    public async Task Settings_playlist_sync_target_picker_selects_one_library_root()
    {
        var viewModel = new SettingsViewModel(
            new FakeSettings(), new FakeFilePicker(), new FakeDialogs(), new FakeTheme());
        await viewModel.NewConfigurationCommand.ExecuteAsync(null);
        IndexTargetEditorRow first = Assert.Single(viewModel.IndexTargets);
        first.Path = @"D:\Music\Archive";
        viewModel.AddIndexTargetCommand.Execute(null);
        IndexTargetEditorRow second = viewModel.IndexTargets[1];
        second.Path = @"E:\Music\Portable";

        viewModel.SelectedSyncTargetRoot = viewModel.SyncTargetRootChoices.Single(choice =>
            choice.Id == second.Id);

        Assert.False(first.IsSyncTarget);
        Assert.True(second.IsSyncTarget);
        Assert.True(second.AllowSynchronizationOutput);
        Assert.Equal(second.Id, viewModel.SelectedSyncTargetRoot.Id);

        viewModel.SelectedSyncTargetRoot = viewModel.SyncTargetRootChoices.Single(choice =>
            choice.Id is null);

        Assert.All(viewModel.IndexTargets, root => Assert.False(root.IsSyncTarget));
    }

    [Fact]
    public async Task Settings_creates_duplicates_renames_and_deletes_custom_profiles()
    {
        var viewModel = new SettingsViewModel(
            new FakeSettings(), new FakeFilePicker(), new FakeDialogs(), new FakeTheme());
        await viewModel.NewConfigurationCommand.ExecuteAsync(null);
        int builtInCount = viewModel.LibraryProfiles.Count;
        Assert.False(viewModel.DeleteLibraryProfileCommand.CanExecute(null));

        viewModel.CreateLibraryProfileCommand.Execute(null);

        LibraryProfile created = Assert.IsType<LibraryProfile>(
            viewModel.SelectedLibraryProfile);
        Assert.Equal(LibraryProfilePreset.Custom, created.Preset);
        Assert.Equal(builtInCount + 1, viewModel.LibraryProfiles.Count);
        Assert.True(viewModel.DeleteLibraryProfileCommand.CanExecute(null));
        LibraryProfileEditorRow editor = Assert.IsType<LibraryProfileEditorRow>(
            viewModel.AdvancedProfile);
        editor.Name = "Home archive";

        viewModel.DuplicateLibraryProfileCommand.Execute(null);

        LibraryProfile duplicate = Assert.IsType<LibraryProfile>(
            viewModel.SelectedLibraryProfile);
        Assert.Equal(LibraryProfilePreset.Custom, duplicate.Preset);
        Assert.Equal("Home archive copy", duplicate.Name);
        Assert.NotEqual(created.Id, duplicate.Id);
        Assert.Equal(LibraryProfilePresets.CatalogOnlyId,
            Assert.Single(viewModel.IndexTargets).ProfileId);

        await viewModel.DeleteLibraryProfileCommand.ExecuteAsync(null);

        Assert.DoesNotContain(viewModel.LibraryProfiles,
            profile => profile.Id == duplicate.Id);
        Assert.Equal(LibraryProfilePresets.CatalogOnlyId,
            viewModel.SelectedLibraryProfile?.Id);
        Assert.Equal(LibraryProfilePresets.CatalogOnlyId,
            Assert.Single(viewModel.IndexTargets).ProfileId);
        Assert.False(viewModel.DeleteLibraryProfileCommand.CanExecute(null));
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

        Assert.Equal(7, viewModel.SelectedTabIndex);
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
            Assert.False(viewModel.AdvancedIngestProfile!.Recipes.Single(recipe =>
                recipe.Id == "legacy-hires-flac").Enabled);

            // The active configuration is restored directly into the editor; users should not
            // need to click Edit active before their roots and workflow targets appear.
            IndexTargetEditorRow root = Assert.Single(viewModel.IndexTargets);
            Assert.Equal(LibraryProfilePresets.LegacyId, viewModel.SelectedLibraryProfile?.Id);
            Assert.NotEqual(LibraryProfilePresets.LegacyId, root.ProfileId);
            Assert.True(root.AllowMetadataWrites);
            Assert.True(root.AllowArtworkWrites);
            Assert.False(root.AllowOrganization);
            Assert.True(root.AllowIngestOutput);
            Assert.True(root.AllowSynchronizationOutput);
            Assert.True(viewModel.LibraryProfiles.Single(profile => string.Equals(
                profile.Id, root.ProfileId, StringComparison.OrdinalIgnoreCase))
                .Naming.UseItunesCanonicalNaming);
            Assert.Equal(LibraryDiscStrategy.PreserveTags,
                viewModel.LibraryProfiles.Single(profile => string.Equals(
                    profile.Id, root.ProfileId, StringComparison.OrdinalIgnoreCase))
                    .Disc.Strategy);
            Assert.True(root.IsSyncTarget);
            Assert.Equal(root.Id, viewModel.SelectedSyncTargetRoot?.Id);
            Assert.Equal(".flac, .m4a", root.IndexFormats);
            Assert.Equal("Music/**; *.flac", root.IndexIncludePatterns);
            Assert.Equal("Temp/**; *.tmp", root.IndexExcludePatterns);
            Assert.Equal(2, root.Memberships.Count);
            Assert.Equal("Lossless, Mobile", root.Memberships[0].Name);
            Assert.Equal("/FLAC", root.Memberships[0].Offset);
            Assert.Equal("Desktop", root.Memberships[1].Name);
            Assert.Equal("/Music", root.Memberships[1].Offset);
            Assert.Equal(5m, viewModel.OversizedArtworkSizeThresholdMib);
            Assert.Equal(5 * 1024 * 1024, viewModel.OversizedArtworkByteThreshold);
            Assert.Equal(3_200, viewModel.OversizedArtworkDimensionThreshold);
            Assert.Equal(220, viewModel.AdvancedProfile!.ComponentLengthLimit);
            Assert.Equal(180, viewModel.AdvancedProfile.DiscAlbumLengthLimit);
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
            viewModel.AdvancedProfile.UseItunesCanonicalNaming = true;
            viewModel.AdvancedProfile.ComponentLengthLimit = 180;
            viewModel.AdvancedProfile.DiscAlbumLengthLimit = 170;
            viewModel.AdvancedProfile.IdentityStripsFormatSuffixes = false;
            viewModel.AdvancedProfile.PreserveReplayGain = false;
            viewModel.AdvancedProfile.HealthRules.Single(rule =>
                rule.Id == LibraryHealthRuleIds.LossyFile).Enabled = false;
            viewModel.AdvancedIngestProfile!.Recipes[0].PreserveMetadata = false;
            IngestRecipeEditorRow aacRecipe = viewModel.AdvancedIngestProfile.Recipes.Single(
                recipe => recipe.Id == "legacy-aac");
            aacRecipe.Encoder = "aac-advanced";
            aacRecipe.BitrateKbps = 320;
            aacRecipe.ExtraFfmpegOptions =
                "-af \"loudnorm=I=-16:LRA=11\" -movflags +faststart";
            aacRecipe.AddToMediaCatalog = true;
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
            Assert.True(viewModel.SaveConfigurationCommand.CanExecute(null),
                viewModel.ValidationSummary);
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
            Assert.False(savedRoot.UseItunesCanonicalNaming);
            Assert.True(saved.Profiles.Single(profile => string.Equals(
                profile.Id, savedRoot.ProfileId, StringComparison.OrdinalIgnoreCase))
                .Naming.UseItunesCanonicalNaming);
            Assert.True(savedRoot.IsSyncTarget);
            Assert.Equal(LibraryIngestRole.None, savedRoot.IngestRole);
            Assert.Equal(LibraryRepresentationRole.Ignore,
                savedRoot.RepresentationRole);
            Assert.Equal([".flac"], savedRoot.IndexFormats);
            Assert.Equal(["Music/**", "*.flac"], savedRoot.IndexIncludePatterns);
            Assert.Equal(["Temp/**", "Drafts/**"], savedRoot.IndexExcludePatterns);
            Assert.Equal(LibraryPathCollisionPolicy.Hash,
                saved.ActiveProfile.Naming.CollisionPolicy);
            Assert.True(saved.ActiveProfile.Naming.UseItunesCanonicalNaming);
            Assert.Equal(180, saved.ActiveProfile.Naming.ComponentLengthLimit);
            Assert.Equal(170, saved.ActiveProfile.Naming.DiscAlbumLengthLimit);
            Assert.Equal(180, saved.LengthLimit);
            Assert.Equal(170, saved.DiscNumLengthLimit);
            Assert.Equal("aac-advanced", saved.AacEncoder);
            Assert.Equal(320, saved.AacBitrateKbps);
            Assert.Equal("-af \"loudnorm=I=-16:LRA=11\" -movflags +faststart",
                saved.ActiveIngestProfile.Ingest.Recipes.Single(recipe =>
                    recipe.Id == "legacy-aac").ExtraFfmpegOptions);
            Assert.True(saved.ActiveIngestProfile.Ingest.Recipes.Single(recipe =>
                recipe.Id == "legacy-aac").AddToMediaCatalog);
            Assert.False(saved.ActiveProfile.AlbumIdentity.StripFormatSuffixes);
            Assert.False(saved.ActiveProfile.Metadata.PreserveReplayGain);
            Assert.False(saved.ActiveProfile.Health.Find(
                LibraryHealthRuleIds.LossyFile)!.Enabled);
            Assert.False(saved.ActiveIngestProfile.Ingest.Recipes[0].PreserveMetadata);
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
        var documents = new FakeMetadataDocumentService(
            Document(
                @"C:\one.flac",
                new(
                    MetadataFieldKey.Known(
                        TagFields.Grouping),
                    ["First"]),
                new(
                    MetadataFieldKey.Known(
                        TagFields.Copyright),
                    ["Copyright owner"]),
                new(
                    MetadataFieldKey.Custom(
                        "CUSTOM_NOTE"),
                    ["First"])),
            Document(
                @"C:\two.flac",
                new(
                    MetadataFieldKey.Known(
                        TagFields.Grouping),
                    ["Second"]),
                new(
                    MetadataFieldKey.Custom(
                        "CUSTOM_NOTE"),
                    ["Second"])));
        var operations =
            new FakeMetadataOperationService();
        var viewModel = new FieldsDialogViewModel(
            documents,
            operations,
            [@"C:\one.flac", @"C:\two.flac"]);
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
        custom.Value =
            "Canonical note" +
            Environment.NewLine +
            "Archive note";

        viewModel.NewUserStringName = "DJ_SET";
        viewModel.AddUserStringCommand.Execute(null);
        viewModel.Rows.Single(row => row.UserStringKey == "DJ_SET").Value = "Warmup";

        bool? closed = null;
        viewModel.CloseRequested += result => closed = result;
        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Null(closed);
        Assert.True(viewModel.IsConfirmingSave);
        Assert.Contains(
            "2 file(s) and 5 field change(s)",
            viewModel.StatusMessage);
        Assert.Contains(
            "recovery journals",
            viewModel.StatusMessage);
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(closed);
        IReadOnlyList<MetadataValueEdit> edits =
            operations.PreviewedValueEdits[
                @"C:\one.flac"];
        Assert.Contains(edits, edit =>
            edit.Field.KnownField ==
                TagFields.Grouping &&
            edit.Values.SequenceEqual(
                ["Canonical grouping"]));
        Assert.Contains(edits, edit =>
            edit.Field.KnownField ==
                TagFields.Copyright &&
            edit.Values.Length == 0);
        Assert.Contains(edits, edit =>
            edit.Field.KnownField ==
                TagFields.Mood &&
            edit.Values.SequenceEqual(["Calm"]));
        Assert.Contains(edits, edit =>
            edit.Field.CustomName ==
                "CUSTOM_NOTE" &&
            edit.Values.SequenceEqual(
                ["Canonical note", "Archive note"]));
        Assert.Contains(edits, edit =>
            edit.Field.CustomName == "DJ_SET" &&
            edit.Values.SequenceEqual(["Warmup"]));

        static MediaDocument Document(
            string path,
            params MetadataValueSet[] fields) =>
            new(
                path,
                [new(
                    "VorbisComment",
                    [.. fields],
                    true,
                    true,
                    true,
                    true)],
                [],
                null,
                new(
                    path,
                    10,
                    DateTime.UtcNow,
                    "hash"),
                true);
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

    [Fact]
    public async Task Library_operations_resolve_selected_album_and_use_shared_catalog()
    {
        TrackRecord[] records =
        [
            Track("Artist", "First", "One", "FLAC", @"C:\music\one.flac"),
            Track("Artist", "First", "Two", "FLAC", @"C:\music\two.flac"),
            Track("Artist", "Second", "Three", "FLAC", @"C:\music\three.flac"),
        ];
        var library = new FakeLibrary(records);
        var settings = new FakeSettings();
        var activity = new AppActivityService();
        var inspector = new SelectionInspectorViewModel(
            new FakeMediaService(), library, new FakeTagWriter(),
            new FakeArtworkService(), new FakeFilePicker(), new FakeDialogs(),
            new FakeFieldsEditor(), new FakeThumbnails(), activity);
        var indexing = new IndexingViewModel(library, settings, activity);
        var operations = new FakeMetadataOperationService();
        var viewModel = new LibraryViewModel(
            library,
            new FakeReindex(),
            settings,
            inspector,
            new NavigationService(),
            indexing,
            new FakeThumbnails(),
            metadataOperations: operations,
            operationCatalog: new MetadataOperationCatalog());
        await viewModel.ReloadAsync();
        await viewModel.SelectAsync(
            [viewModel.Rows.Single(row => row.Title == "One")]);
        viewModel.SelectedOperationScope = LibraryOperationScope.SelectedAlbums;
        viewModel.OperationEditor.OperationValue = "Reviewed";

        await viewModel.PreviewLibraryOperationCommand.ExecuteAsync(null);

        Assert.Equal(
            [@"C:\music\one.flac", @"C:\music\two.flac"],
            operations.PreviewedPaths);
        Assert.True(viewModel.HasApplicableOperationPreview);
        Assert.Single(viewModel.OperationPreviewChanges);
        Assert.All(viewModel.OperationEditor.OperationDescriptors, descriptor =>
            Assert.True(descriptor.Supports(MetadataOperationSurface.Library)));
    }

    [Fact]
    public async Task Library_clipboard_preserves_field_identity_and_previews_scope()
    {
        TrackRecord[] records =
        [
            Track(
                "Artist",
                "First",
                "One",
                "FLAC",
                @"C:\music\one.flac") with
            {
                Metadata = new Dictionary<string, string[]>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [nameof(TagFields.Artist)] =
                        ["Artist", "Guest"],
                },
            },
            Track(
                "Artist",
                "First",
                "Two",
                "FLAC",
                @"C:\music\two.flac"),
        ];
        var library = new FakeLibrary(records);
        var settings = new FakeSettings();
        var activity = new AppActivityService();
        var inspector = new SelectionInspectorViewModel(
            new FakeMediaService(),
            library,
            new FakeTagWriter(),
            new FakeArtworkService(),
            new FakeFilePicker(),
            new FakeDialogs(),
            new FakeFieldsEditor(),
            new FakeThumbnails(),
            activity);
        var operations = new FakeMetadataOperationService();
        var clipboard = new FakePlatformService();
        var viewModel = new LibraryViewModel(
            library,
            new FakeReindex(),
            settings,
            inspector,
            new NavigationService(),
            new IndexingViewModel(
                library,
                settings,
                activity),
            new FakeThumbnails(),
            metadataOperations: operations,
            operationCatalog: new MetadataOperationCatalog(),
            platform: clipboard);
        await viewModel.ReloadAsync();
        await viewModel.SelectAsync(
            [viewModel.Rows.Single(row => row.Title == "One")]);
        viewModel.SelectedOperationScope =
            LibraryOperationScope.SelectedAlbums;
        viewModel.OperationEditor.SelectedField =
            viewModel.OperationEditor.Fields.Single(field =>
                field.Field == TagFields.Artist);

        await viewModel.CopyLibraryMetadataFieldCommand
            .ExecuteAsync(null);

        Assert.True(MetadataClipboardCodec.TryDecode(
            clipboard.Text,
            out MetadataClipboardPayload? copied));
        Assert.Equal(TagFields.Artist, copied!.Field.KnownField);
        Assert.Equal(["Artist", "Guest"], copied.Values);

        clipboard.Text = MetadataClipboardCodec.Encode(
            new(
                MetadataFieldKey.Custom("DJ_SET"),
                ["Warmup", "Peak"]));
        await viewModel.PasteLibraryMetadataFieldCommand
            .ExecuteAsync(null);

        Assert.Equal(2, operations.PreviewedValueEdits.Count);
        Assert.All(
            operations.PreviewedValueEdits.Values,
            edits =>
            {
                MetadataValueEdit edit = Assert.Single(edits);
                Assert.Equal("DJ_SET", edit.Field.CustomName);
                Assert.Equal(["Warmup", "Peak"], edit.Values);
            });
        Assert.True(viewModel.HasApplicableOperationPreview);
        Assert.Contains("Previewed", viewModel.OperationStatus);
    }

    [Fact]
    public async Task Library_operation_preview_reports_progress_and_can_be_cancelled()
    {
        TrackRecord[] records =
        [
            Track("Artist", "First", "One", "FLAC", @"C:\music\one.flac"),
        ];
        var library = new FakeLibrary(records);
        var settings = new FakeSettings();
        var activity = new AppActivityService();
        var inspector = new SelectionInspectorViewModel(
            new FakeMediaService(), library, new FakeTagWriter(),
            new FakeArtworkService(), new FakeFilePicker(), new FakeDialogs(),
            new FakeFieldsEditor(), new FakeThumbnails(), activity);
        var indexing = new IndexingViewModel(library, settings, activity);
        var operations = new FakeMetadataOperationService
        {
            WaitForCancellation = true,
        };
        var viewModel = new LibraryViewModel(
            library,
            new FakeReindex(),
            settings,
            inspector,
            new NavigationService(),
            indexing,
            new FakeThumbnails(),
            metadataOperations: operations,
            operationCatalog: new MetadataOperationCatalog());
        await viewModel.ReloadAsync();
        await viewModel.SelectAsync([viewModel.Rows.Single()]);
        viewModel.OperationEditor.OperationValue = "Reviewed";

        Task preview = viewModel.PreviewLibraryOperationCommand.ExecuteAsync(null);
        await operations.PreviewStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.True(viewModel.IsOperationBusy);
        Assert.Equal("Reading metadata", viewModel.OperationProgressText);
        viewModel.CancelLibraryOperationCommand.Execute(null);
        await preview;

        Assert.True(operations.CancellationObserved);
        Assert.False(viewModel.IsOperationBusy);
        Assert.Contains("cancelled", viewModel.OperationStatus,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Representative_preview_debounces_to_the_latest_file()
    {
        var operations =
            new FakeMetadataOperationService();
        var preview =
            new RepresentativeMetadataPreviewViewModel(
                operations);
        static OperationRecipe Recipe() =>
            OperationRecipe.Create(
                "Draft",
                new AssignFieldOperation(
                    MetadataFieldKey.Known(
                        TagFields.Title),
                    "Reviewed"));

        preview.Schedule(
            @"C:\first.flac",
            Recipe);
        preview.Schedule(
            @"C:\second.flac",
            Recipe);
        DateTime deadline =
            DateTime.UtcNow.AddSeconds(3);
        while (preview.IsBusy &&
               DateTime.UtcNow < deadline)
            await Task.Delay(
                10,
                TestContext.Current.CancellationToken);

        Assert.False(preview.IsBusy);
        Assert.Equal(
            [@"C:\second.flac"],
            operations.PreviewedPaths);
        Assert.True(preview.HasPreview);
        Assert.Contains(
            "Title: Before",
            preview.BeforeText);
        Assert.Contains(
            "Title: Reviewed",
            preview.AfterText);
    }

    [Fact]
    public void Operation_editor_change_event_includes_draft_and_step_edits()
    {
        var editor =
            new MetadataOperationEditorViewModel(
                new MetadataOperationCatalog(),
                MetadataOperationSurface.Workbench);
        int changed = 0;
        editor.Changed += () => changed++;

        editor.OperationValue = "Reviewed";

        Assert.True(changed > 0);
        changed = 0;
        editor.AddCurrentOperationCommand.Execute(null);

        Assert.True(changed > 0);
        changed = 0;
        Assert.Single(editor.Steps).Name =
            "Renamed draft step";

        Assert.True(changed > 0);
    }

    [Fact]
    public void Report_editor_builds_and_reorders_typed_configuration()
    {
        var editor = new ReportEditorViewModel
        {
            Name = "Album inventory",
            Format = ReportFormat.Html,
            Encoding = ReportEncoding.Utf8WithBom,
            OutputPath = @"C:\reports\albums.html",
            CustomFieldName = "CATALOGNUMBER",
            OneFilePerGroup = true,
            GroupFileNameTemplate = "{Group}-inventory.{Format}",
        };

        editor.AddCustomFieldCommand.Execute(null);
        ReportFieldEditorRow custom = Assert.Single(
            editor.Fields,
            field => field.Descriptor.Kind ==
                ReportFieldKind.CustomMetadata);
        editor.SelectedField = custom;
        editor.MoveFieldUpCommand.Execute(null);
        editor.SelectedGroupField = editor.Fields.Single(field =>
            field.Descriptor.KnownField == TagFields.Album);
        editor.SelectedSortField = editor.Fields.Single(field =>
            field.Descriptor.KnownField == TagFields.TrackNumber);
        editor.SortType = ReportSortType.Numeric;
        editor.SortDescending = true;

        ReportConfiguration configuration =
            editor.CreateConfiguration();

        Assert.Equal("Album inventory", configuration.Name);
        Assert.Equal(ReportFormat.Html, configuration.Format);
        Assert.Equal(ReportEncoding.Utf8WithBom,
            configuration.Encoding);
        Assert.Equal("metadata.Album",
            configuration.GroupByFieldId);
        Assert.True(configuration.OneFilePerGroup);
        Assert.Equal("{Group}-inventory.{Format}",
            configuration.GroupFileNameTemplate);
        Assert.Contains(configuration.Fields, field =>
            field.Kind == ReportFieldKind.CustomMetadata &&
            field.Name == "CATALOGNUMBER");
        ReportSortDescriptor sort =
            Assert.Single(configuration.Sorting);
        Assert.Equal("metadata.TrackNumber", sort.FieldId);
        Assert.Equal(ReportSortType.Numeric, sort.Type);
        Assert.True(sort.Descending);
    }

    [Fact]
    public void Playlist_editor_builds_grouped_typed_configuration()
    {
        var editor = new PlaylistEditorViewModel
        {
            Name = "Album playlists",
            Format = "wpl",
            OutputPath = @"C:\playlists",
            PathStyle = PlaylistPathStyle.Absolute,
            Encoding = PlaylistWorkspaceEncoding.Utf16LittleEndian,
            LineEnding = PlaylistLineEnding.CrLf,
            IncludeExtendedInfo = false,
            OnePlaylistPerGroup = true,
            GroupFileNameTemplate = "{Name}-{Group}",
        };
        editor.SelectedGroupField = editor.GroupFields.Single(choice =>
            choice.Field.KnownField == TagFields.AlbumArtist);

        PlaylistWorkspaceConfiguration configuration =
            editor.CreateConfiguration();

        Assert.Equal("Album playlists", configuration.Name);
        Assert.Equal("wpl", configuration.Format);
        Assert.Equal(PlaylistPathStyle.Absolute,
            configuration.PathStyle);
        Assert.Equal(PlaylistWorkspaceEncoding.Utf16LittleEndian,
            configuration.Encoding);
        Assert.Equal(PlaylistLineEnding.CrLf,
            configuration.LineEnding);
        Assert.False(configuration.IncludeExtendedInfo);
        Assert.True(configuration.OnePlaylistPerGroup);
        Assert.Equal(TagFields.AlbumArtist,
            configuration.GroupByField!.KnownField);
        Assert.Equal("{Name}-{Group}",
            configuration.GroupFileNameTemplate);
    }

    [Fact]
    public void External_tool_editor_persists_structured_arguments()
    {
        var store = new FakeExternalToolStore();
        var editor = new ExternalToolEditorViewModel(store)
        {
            Name = "Waveform",
            Executable = "waveform-tool",
            ArgumentsText =
                "--input" + Environment.NewLine +
                "{File}" + Environment.NewLine +
                "--position={Index}/{Count}",
            WorkingDirectory = "{Directory}",
            InvocationMode =
                ExternalToolInvocationMode.OncePerFile,
        };

        editor.SaveToolCommand.Execute(null);

        ExternalToolDefinition saved =
            Assert.Single(store.Tools);
        Assert.Equal(
            ["--input", "{File}", "--position={Index}/{Count}"],
            saved.Arguments);
        Assert.Equal("{Directory}", saved.WorkingDirectory);
        Assert.Equal(
            ExternalToolInvocationMode.OncePerFile,
            saved.InvocationMode);
        Assert.Single(editor.SavedTools);

        editor.SelectedSavedTool = editor.SavedTools[0];
        editor.DeleteToolCommand.Execute(null);

        Assert.Empty(store.Tools);
        Assert.Empty(editor.SavedTools);
    }

    [Theory]
    [InlineData("control+shift+p", "Ctrl+Shift+P")]
    [InlineData("cmd+alt+f8", "Alt+Meta+F8")]
    [InlineData("CTRL+Enter", "Ctrl+Enter")]
    public void Workbench_shortcut_parser_canonicalizes_supported_gestures(
        string input,
        string expected)
    {
        bool parsed = WorkbenchShortcutGestureParser.TryParse(
            input,
            out ParsedWorkbenchShortcut? gesture,
            out string? error);

        Assert.True(parsed, error);
        Assert.Equal(expected, gesture!.Display);
    }

    [Theory]
    [InlineData("P")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+Ctrl+P")]
    [InlineData("Ctrl+P+R")]
    public void Workbench_shortcut_parser_rejects_ambiguous_gestures(
        string input)
    {
        Assert.False(WorkbenchShortcutGestureParser.TryParse(
            input,
            out _,
            out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Workbench_shortcut_editor_persists_matches_and_rejects_conflicts()
    {
        var store = new FakeWorkbenchShortcutStore();
        var editor = new WorkbenchShortcutEditorViewModel(store)
        {
            GestureText = "control+shift+p",
        };
        editor.SelectedCommand = editor.Commands.Single(choice =>
            choice.Command ==
            WorkbenchShortcutCommand.PreviewCurrentRecipe);

        editor.SaveShortcutCommand.Execute(null);

        WorkbenchShortcutBinding saved = Assert.Single(store.Bindings);
        Assert.Equal("Ctrl+Shift+P", saved.Gesture);
        Assert.True(editor.TryMatch(
            WorkbenchShortcutModifiers.Control |
            WorkbenchShortcutModifiers.Shift,
            "P",
            out WorkbenchShortcutBinding? matched));
        Assert.Equal(saved.Id, matched!.Id);

        editor.NewShortcutCommand.Execute(null);
        editor.GestureText = "CTRL+SHIFT+P";
        editor.SelectedCommand = editor.Commands.Single(choice =>
            choice.Command == WorkbenchShortcutCommand.Redo);
        editor.SaveShortcutCommand.Execute(null);

        Assert.Single(store.Bindings);
        Assert.Contains("already assigned", editor.Status,
            StringComparison.OrdinalIgnoreCase);

        editor.GestureText = "Ctrl+K";
        editor.SaveShortcutCommand.Execute(null);

        Assert.Single(store.Bindings);
        Assert.Contains("reserved", editor.Status,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Metadata_column_editor_persists_typed_custom_and_editable_columns()
    {
        var store = new FakeMetadataGridColumnStore();
        var editor = new MetadataGridColumnEditorViewModel(
            store,
            MetadataGridSurface.Workbench)
        {
            Label = "DJ set",
            FieldKind = MetadataGridFieldKind.Custom,
            CustomFieldName = "DJ_SET",
            Width = 210,
        };

        editor.SaveColumnCommand.Execute(null);

        UserMetadataColumnDescriptor custom =
            Assert.Single(store.Workbench);
        Assert.Equal("DJ_SET", custom.Field.CustomName);
        Assert.Equal(210, custom.Width);
        Assert.Null(custom.EditTarget);

        editor.NewColumnCommand.Execute(null);
        editor.Label = "Editable title";
        editor.FieldKind = MetadataGridFieldKind.Known;
        editor.SelectedKnownField = editor.KnownFields.Single(choice =>
            choice.Field == TagFields.Title);
        editor.InlineEditable = true;
        editor.SaveColumnCommand.Execute(null);

        Assert.Equal(2, store.Workbench.Count);
        UserMetadataColumnDescriptor title =
            store.Workbench.Single(column =>
                column.Field.KnownField == TagFields.Title);
        Assert.Equal(
            TagFields.Title,
            title.EditTarget!.KnownField);

        var libraryEditor =
            new MetadataGridColumnEditorViewModel(
                store,
                MetadataGridSurface.Library)
            {
                Label = "Catalog",
                FieldKind = MetadataGridFieldKind.Known,
            };
        libraryEditor.SelectedKnownField =
            libraryEditor.KnownFields.Single(choice =>
                choice.Field == TagFields.CatalogNumber);
        libraryEditor.InlineEditable = true;
        libraryEditor.SaveColumnCommand.Execute(null);

        Assert.Null(Assert.Single(store.Library).EditTarget);
    }

    [Fact]
    public void Dynamic_metadata_values_project_for_library_and_workbench_rows()
    {
        MetadataFieldKey custom =
            MetadataFieldKey.Custom("DJ_SET");
        var library = new LibraryRow(new TrackRecord
        {
            Path = "song.flac",
            Metadata = new Dictionary<string, string[]>
            {
                [nameof(TagFields.CatalogNumber)] = ["ABC-123"],
                [CachedMetadataKeys.Custom("DJ_SET")] =
                    ["Morning", "Evening"],
            },
        });
        var document = new MediaDocument(
            "song.flac",
            [new(
                "VorbisComment",
                [new(custom, ["Morning", "Evening"])],
                true,
                true,
                true,
                true)],
            [],
            null,
            new(
                "song.flac",
                10,
                DateTime.UtcNow,
                "hash"),
            true);
        var workbench = new WorkbenchTrackViewModel(document);

        Assert.Equal(
            "ABC-123",
            library.MetadataValues[
                MetadataGridValueKey.For(
                    MetadataFieldKey.Known(
                        TagFields.CatalogNumber))]);
        Assert.Equal(
            "Morning; Evening",
            library.MetadataValues[
                MetadataGridValueKey.For(custom)]);
        Assert.Equal(
            "Morning; Evening",
            workbench.MetadataValues[
                MetadataGridValueKey.For(custom)]);
    }

    [Fact]
    public void Workbench_selection_projects_mixed_values_and_builds_per_file_edits()
    {
        MetadataFieldKey title =
            MetadataFieldKey.Known(TagFields.Title);
        MetadataFieldKey artist =
            MetadataFieldKey.Known(TagFields.Artist);
        MetadataFieldKey custom =
            MetadataFieldKey.Custom("DJ_SET");
        WorkbenchTrackViewModel[] files =
        [
            Track(
                "first.flac",
                new(title, ["Shared title"]),
                new(artist, ["First artist"]),
                new(custom, ["Morning"])),
            Track(
                "second.flac",
                new(title, ["Shared title"]),
                new(
                    artist,
                    ["Second artist", "Guest artist"])),
        ];

        IReadOnlyList<WorkbenchMetadataFieldRow> rows =
            WorkbenchViewModel.BuildMetadataFieldRows(files);

        WorkbenchMetadataFieldRow common =
            rows.Single(row => row.Field == title);
        Assert.False(common.IsMixed);
        Assert.Equal(["Shared title"], common.Values);
        Assert.Equal("2/2 files", common.Coverage);
        WorkbenchMetadataFieldRow mixed =
            rows.Single(row => row.Field == artist);
        Assert.True(mixed.IsMixed);
        Assert.Empty(mixed.Values);
        Assert.Equal(
            "Mixed across 2 selected files",
            mixed.DisplayValue);
        WorkbenchMetadataFieldRow partial =
            rows.Single(row => row.Field == custom);
        Assert.True(partial.IsMixed);
        Assert.Equal(1, partial.PresentFileCount);
        Assert.Equal("1/2 files", partial.Coverage);

        IReadOnlyDictionary<
            string,
            IReadOnlyList<MetadataValueEdit>> append =
                WorkbenchViewModel.BuildValueEdits(
                    files,
                    artist,
                    WorkbenchFieldEditMode.Append,
                    ["Added artist"]);
        Assert.Equal(
            ["First artist", "Added artist"],
            Assert.Single(append["first.flac"]).Values);
        Assert.Equal(
            [
                "Second artist",
                "Guest artist",
                "Added artist",
            ],
            Assert.Single(append["second.flac"]).Values);

        IReadOnlyDictionary<
            string,
            IReadOnlyList<MetadataValueEdit>> remove =
                WorkbenchViewModel.BuildValueEdits(
                    files,
                    artist,
                    WorkbenchFieldEditMode.RemoveValues,
                    ["Guest artist"]);
        Assert.Equal(
            ["First artist"],
            Assert.Single(remove["first.flac"]).Values);
        Assert.Equal(
            ["Second artist"],
            Assert.Single(remove["second.flac"]).Values);

        static WorkbenchTrackViewModel Track(
            string path,
            params MetadataValueSet[] fields) =>
            new(new MediaDocument(
                path,
                [new(
                    "VorbisComment",
                    fields.ToImmutableArray(),
                    true,
                    true,
                    true,
                    true)],
                [],
                null,
                new(
                    path,
                    10,
                    DateTime.UtcNow,
                    "hash"),
                true));
    }

    [Fact]
    public void Workbench_builds_ordered_multi_file_artwork_set_requests()
    {
        WorkbenchTrackViewModel[] files =
        [
            Track("first.flac"),
            Track("second.flac"),
        ];
        ArtworkPreviewItem[] artwork =
        [
            new(
                null,
                ID3v2Util.APICType.BackCover,
                "image/png",
                [4, 5, 6],
                "back details",
                "Rear scan"),
            new(
                null,
                ID3v2Util.APICType.FrontCover,
                "image/jpeg",
                [1, 2, 3],
                "front details",
                "Front scan"),
        ];

        IReadOnlyDictionary<string, ArtworkSetPreviewRequest>
            requests =
                WorkbenchViewModel.BuildArtworkSetRequests(
                    files,
                    artwork,
                    720);

        Assert.Equal(2, requests.Count);
        foreach (ArtworkSetPreviewRequest request in
                 requests.Values)
        {
            Assert.Equal(720, request.MaxDimension);
            Assert.Equal(
                [
                    ID3v2Util.APICType.BackCover,
                    ID3v2Util.APICType.FrontCover,
                ],
                request.Images.Select(image => image.Type));
            Assert.Equal(
                ["Rear scan", "Front scan"],
                request.Images.Select(
                    image => image.Description));
            Assert.Equal(
                [4, 5, 6],
                request.Images[0].Data);
        }

        static WorkbenchTrackViewModel Track(string path) =>
            new(new MediaDocument(
                path,
                [],
                [],
                null,
                new(
                    path,
                    10,
                    DateTime.UtcNow,
                    "hash"),
                true));
    }

    [Fact]
    public void Metadata_preview_projects_physical_tag_layer_changes()
    {
        var preview = new System.Collections.ObjectModel.ObservableCollection<
            MetadataPreviewRow>();
        string path = Path.Combine(Path.GetTempPath(), "track.aac");
        var file = new MetadataFilePlan(
            path,
            new(path, 1, DateTime.UtcNow, "hash"),
            [],
            [],
            [],
            TagLayerEdits:
            [
                new(
                    TagLayerKind.ApeV2,
                    TagLayerEditMode.Add),
            ],
            TagLayerDifferences:
            [
                new(
                    TagLayerKind.ApeV2,
                    WasPresent: false,
                    WillBePresent: true),
            ]);
        var plan = new MetadataOperationPlan(
            Guid.NewGuid(),
            "Add layer",
            [file],
            DateTimeOffset.UtcNow);

        MetadataPreviewRowBuilder.Populate(preview, plan);

        MetadataPreviewRow row = Assert.Single(preview);
        Assert.Equal("APEv2 tag layer", row.Field);
        Assert.Equal("Absent", row.Before);
        Assert.Equal("Present", row.After);
    }

    [Fact]
    public void Metadata_preview_projects_id3_version_and_compatibility_issues()
    {
        var preview = new System.Collections.ObjectModel.ObservableCollection<
            MetadataPreviewRow>();
        string path = Path.Combine(Path.GetTempPath(), "track.mp3");
        var issue = new ID3VersionConversionIssue(
            "SIGN",
            "No legacy representation.",
            Dropped: true);
        var file = new MetadataFilePlan(
            path,
            new(path, 1, DateTime.UtcNow, "hash"),
            [],
            [],
            [],
            Id3VersionEdit: new(
                ID3v2Version.V23,
                DropUnsupportedFrames: true),
            Id3VersionDifference: new(
                ID3v2Version.V24,
                ID3v2Version.V23,
                4,
                [issue]));
        var plan = new MetadataOperationPlan(
            Guid.NewGuid(),
            "Convert ID3",
            [file],
            DateTimeOffset.UtcNow);

        MetadataPreviewRowBuilder.Populate(preview, plan);

        MetadataPreviewRow row = Assert.Single(preview);
        Assert.Equal("ID3 version", row.Field);
        Assert.Equal("ID3v2.4", row.Before);
        Assert.Equal(
            "ID3v2.3 (1 compatibility issue(s))",
            row.After);
    }

    [Fact]
    public void Dynamic_metadata_columns_use_configured_numeric_and_date_sorting()
    {
        MetadataFieldKey field =
            MetadataFieldKey.Custom("SEQUENCE");
        string key = MetadataGridValueKey.For(field);
        LibraryRow Row(string value) =>
            new(new TrackRecord
            {
                Path = value + ".flac",
                Metadata = new Dictionary<string, string[]>
                {
                    [CachedMetadataKeys.Custom("SEQUENCE")] =
                        [value],
                },
            });

        var numeric = new MetadataGridRowComparer(
            key,
            MetadataGridColumnSortType.Numeric);
        Assert.True(numeric.Compare(Row("10"), Row("2")) > 0);

        var date = new MetadataGridRowComparer(
            key,
            MetadataGridColumnSortType.Date);
        Assert.True(date.Compare(
            Row("2025-01-01"),
            Row("2024-12-31")) > 0);
    }

    [Fact]
    public void Visual_filter_editor_builds_and_restores_grouped_conditions()
    {
        var editor = new VisualFilterEditorViewModel
        {
            RootMode = LibraryFilterGroupMode.Any,
        };
        VisualFilterConditionViewModel first =
            Assert.Single(editor.Conditions);
        first.Group = 1;
        first.FieldKind =
            LibraryFilterFieldKind.KnownMetadata;
        first.SelectedKnownField = editor.KnownFields.Single(choice =>
            choice.Field == TagFields.Artist);
        first.Comparison = LibraryFilterComparison.Contains;
        first.Value = "Miles";

        editor.AddConditionCommand.Execute(null);
        VisualFilterConditionViewModel second =
            editor.SelectedCondition!;
        second.Group = 2;
        second.FieldKind =
            LibraryFilterFieldKind.CustomMetadata;
        second.CustomFieldName = "DJ_SET";
        second.Comparison = LibraryFilterComparison.Present;

        LibraryVisualFilterNode expression =
            Assert.IsType<LibraryFilterGroup>(
                editor.Build(out string? error));

        Assert.Null(error);
        var group = Assert.IsType<LibraryFilterGroup>(expression);
        Assert.Equal(LibraryFilterGroupMode.Any, group.Mode);
        Assert.Equal(2, group.Children.Length);

        var restored = new VisualFilterEditorViewModel();
        restored.Load(expression);
        Assert.Equal(2, restored.Conditions.Count);
        Assert.Equal(
            LibraryFilterFieldKind.CustomMetadata,
            restored.Conditions[1].FieldKind);
    }

    [Fact]
    public async Task Library_visual_filter_combines_with_cached_rows()
    {
        TrackRecord[] records =
        [
            Track("Miles", "Kind of Blue", "So What", "FLAC",
                @"C:\music\one.flac"),
            Track("Massive Attack", "Mezzanine", "Teardrop", "MP3",
                @"C:\music\two.mp3"),
        ];
        LibraryViewModel viewModel = BuildLibrary(
            new FakeSettings(),
            records);
        await viewModel.ReloadAsync();
        VisualFilterConditionViewModel condition =
            Assert.Single(viewModel.VisualFilterEditor.Conditions);
        condition.FieldKind =
            LibraryFilterFieldKind.Technical;
        condition.SelectedTechnicalField =
            viewModel.VisualFilterEditor.TechnicalFields.Single(field =>
                field.Name == "Codec");
        condition.Comparison =
            LibraryFilterComparison.Equals;
        condition.Value = "FLAC";

        await viewModel.ApplyVisualFilterCommand.ExecuteAsync(null);

        Assert.Equal(
            "So What",
            Assert.Single(viewModel.Rows).Title);
        Assert.True(viewModel.HasVisualFilter);

        await viewModel.ClearVisualFilterCommand.ExecuteAsync(null);

        Assert.Equal(2, viewModel.Rows.Count);
        Assert.False(viewModel.HasVisualFilter);
    }

    [Fact]
    public async Task Library_audio_discovery_uses_explicit_scope_and_preserves_candidates()
    {
        TrackRecord[] records =
        [
            Track("Artist", "First", "One", "FLAC", @"C:\music\one.flac"),
            Track("Artist", "First", "Two", "FLAC", @"C:\music\two.flac"),
            Track("Artist", "Second", "Three", "FLAC", @"C:\music\three.flac"),
        ];
        var library = new FakeLibrary(records);
        var settings = new FakeSettings();
        var activity = new AppActivityService();
        var inspector = new SelectionInspectorViewModel(
            new FakeMediaService(), library, new FakeTagWriter(),
            new FakeArtworkService(), new FakeFilePicker(), new FakeDialogs(),
            new FakeFieldsEditor(), new FakeThumbnails(), activity);
        var indexing = new IndexingViewModel(library, settings, activity);
        var discovery = new FakeAcoustIdDiscoveryService();
        var metadataOperations = new FakeMetadataOperationService();
        var musicBrainz = new FakeMusicBrainzMetadataProvider();
        var discogs = new FakeDiscogsMetadataProvider();
        var coverArt = new FakeCoverArtArchiveProvider();
        var reports = new FakeReportExportService();
        var playlists = new FakePlaylistWorkspaceService();
        var externalTools = new FakeExternalToolService();
        var delimited = new FakeDelimitedMetadataImportService();
        var viewModel = new LibraryViewModel(
            library,
            new FakeReindex(),
            settings,
            inspector,
            new NavigationService(),
            indexing,
            new FakeThumbnails(),
            metadataOperations: metadataOperations,
            dialogs: new FakeDialogs(),
            audioDiscovery: discovery,
            musicBrainz: musicBrainz,
            releaseMapping: new MusicBrainzReleaseMappingService(),
            coverArt: coverArt,
            files: new FakeFilePicker(
                typeof(PresentationTests).Assembly.Location),
            discogs: discogs,
            discogsMapping: new DiscogsReleaseMappingService(),
            reports: reports,
            playlists: playlists,
            externalTools: externalTools,
            delimitedImports: delimited);
        await viewModel.ReloadAsync();
        await viewModel.SelectAsync(
            [viewModel.Rows.Single(row => row.Title == "One")]);
        viewModel.SelectedOperationScope = LibraryOperationScope.SelectedAlbums;

        await viewModel.ImportLibraryDelimitedMetadataCommand
            .ExecuteAsync(null);

        Assert.Equal(
            [@"C:\music\one.flac", @"C:\music\two.flac"],
            delimited.CandidatePaths);
        Assert.Equal(
            DelimitedMetadataEmptyCellMode.Ignore,
            delimited.Options!.EmptyCellMode);
        Assert.Equal(
            delimited.CandidatePaths.Select(Path.GetFullPath),
            metadataOperations.PreviewedValueEdits.Keys);
        Assert.Contains(
            "Mapped 2 of 2 row(s)",
            viewModel.OperationStatus);
        viewModel.SelectedOperationScope =
            LibraryOperationScope.SelectedTracks;
        viewModel.SelectedOperationScope =
            LibraryOperationScope.SelectedAlbums;

        await viewModel.DiscoverLibraryAudioCommand.ExecuteAsync(null);

        Assert.Equal(
            [@"C:\music\one.flac", @"C:\music\two.flac"],
            discovery.Paths);
        AudioDiscoveryRow row = Assert.Single(viewModel.AudioMatches);
        Assert.Equal(0.92, row.Score);
        Assert.Contains("candidate", viewModel.OperationStatus,
            StringComparison.OrdinalIgnoreCase);

        viewModel.ReportEditor.OutputPath =
            @"C:\reports\selected-album.csv";
        await viewModel.PreviewLibraryReportCommand.ExecuteAsync(null);

        Assert.Equal(
            [@"C:\music\one.flac", @"C:\music\two.flac"],
            reports.PreviewedPaths);
        Assert.Single(viewModel.ReportOutputs);
        Assert.True(viewModel.HasUnsavedChanges);

        await viewModel.ApplyLibraryReportCommand.ExecuteAsync(null);

        Assert.True(reports.Applied);
        Assert.False(viewModel.HasUnsavedChanges);
        Assert.Contains("2 row(s)", viewModel.OperationStatus);

        viewModel.PlaylistEditor.OutputPath =
            @"C:\playlists\selected-album.m3u8";
        await viewModel.PreviewLibraryPlaylistCommand.ExecuteAsync(null);

        Assert.Equal(
            [@"C:\music\one.flac", @"C:\music\two.flac"],
            playlists.PreviewedPaths);
        Assert.Single(viewModel.PlaylistOutputs);
        Assert.True(viewModel.HasUnsavedChanges);

        await viewModel.ApplyLibraryPlaylistCommand.ExecuteAsync(null);

        Assert.True(playlists.Applied);
        Assert.False(viewModel.HasUnsavedChanges);
        Assert.Contains("2 track reference(s)",
            viewModel.OperationStatus);

        viewModel.ExternalToolEditor.Executable = "selection-tool";
        viewModel.ExternalToolEditor.ArgumentsText = "{Files}";
        viewModel.PreviewLibraryExternalToolCommand.Execute(null);

        Assert.Equal(
            [@"C:\music\one.flac", @"C:\music\two.flac"],
            externalTools.PreviewedPaths);
        Assert.Single(viewModel.ExternalToolInvocations);
        Assert.True(viewModel.HasUnsavedChanges);

        await viewModel.RunLibraryExternalToolCommand.ExecuteAsync(null);

        Assert.True(externalTools.Ran);
        Assert.False(viewModel.HasUnsavedChanges);
        Assert.Contains("1 succeeded", viewModel.OperationStatus);

        await viewModel.PreviewLibraryAudioIdentifiersCommand.ExecuteAsync(null);

        Assert.Equal([@"C:\music\one.flac"], metadataOperations.PreviewedPaths);
        Assert.Contains(metadataOperations.PreviewedRecipe!.EnabledOperations,
            operation => operation is AssignFieldOperation assign &&
                assign.Field.KnownField == TagFields.AcoustID_Fingerprint);
        Assert.Contains(metadataOperations.PreviewedRecipe.EnabledOperations,
            operation => operation is AssignFieldOperation assign &&
                assign.Field.KnownField == TagFields.AcoustID_ID);
        Assert.Contains(metadataOperations.PreviewedRecipe.EnabledOperations,
            operation => operation is AssignFieldOperation assign &&
                assign.Field.KnownField == TagFields.MusicBrainz_RecordingID);
        Assert.True(viewModel.HasApplicableOperationPreview);

        await viewModel.ResolveLibraryRecordingCommand.ExecuteAsync(null);

        Assert.Equal(
            Guid.Parse("cd2e7c47-16f5-46c6-a37c-a1eb7bf599ff"),
            musicBrainz.RecordingId);
        MusicBrainzReleaseRow release = Assert.Single(viewModel.ReleaseMatches);
        Assert.Equal("Matched Album", release.Title);
        Assert.Equal("1-1", release.MatchedTrackPositions);

        await viewModel.BuildLibraryReleaseMappingCommand.ExecuteAsync(null);

        MusicBrainzTrackMappingRow mapping = viewModel.ReleaseTrackMappings
            .Single(row => row.Path == @"C:\music\one.flac");
        Assert.True(mapping.IsIncluded);
        Assert.Equal("1-1", mapping.Position);
        Assert.Contains("92.0% AcoustID", mapping.Status);

        await viewModel.PreviewLibraryReleaseMetadataCommand.ExecuteAsync(null);

        IReadOnlyList<MetadataValueEdit> imported =
            metadataOperations.PreviewedValueEdits[
                Path.GetFullPath(@"C:\music\one.flac")];
        Assert.Contains(imported, edit =>
            edit.Field.KnownField == TagFields.Album &&
            edit.Values.SequenceEqual(["Matched Album"]));
        Assert.Contains(imported, edit =>
            edit.Field.KnownField == TagFields.MusicBrainz_AlbumID);
        Assert.True(viewModel.HasApplicableOperationPreview);

        viewModel.SelectedOperationScope =
            LibraryOperationScope.VisibleFilteredResults;

        Assert.Empty(viewModel.ReleaseTrackMappings);
        Assert.False(viewModel.HasApplicableOperationPreview);

        viewModel.ReleaseSearch.Artist = "Matched Artist";
        viewModel.ReleaseSearch.Album = "Matched Album";
        await viewModel.SearchLibraryMusicBrainzReleasesCommand.ExecuteAsync(null);

        Assert.Equal("Matched Artist", musicBrainz.SearchQuery!.Artist);
        MusicBrainzReleaseRow searchResult =
            Assert.Single(viewModel.ReleaseMatches);
        Assert.Empty(searchResult.Candidate.Tracks);

        await viewModel.BuildLibraryReleaseMappingCommand.ExecuteAsync(null);

        Assert.Equal(searchResult.ReleaseId, musicBrainz.RequestedReleaseId);
        Assert.Contains(viewModel.ReleaseTrackMappings,
            row => row.Path == @"C:\music\one.flac" && row.IsIncluded);

        viewModel.DiscogsSearch.Artist = "Matched Artist";
        viewModel.DiscogsSearch.Album = "Matched Album";
        await viewModel.SearchLibraryDiscogsReleasesCommand.ExecuteAsync(null);

        Assert.Equal("Matched Artist", discogs.SearchQuery!.Artist);
        DiscogsReleaseRow discogsResult =
            Assert.Single(viewModel.DiscogsMatches);
        Assert.Equal(4242, discogsResult.ReleaseId);
        Assert.Equal("Discogs", discogsResult.Source);
        Assert.Equal(0, discogsResult.TrackCount);

        await viewModel.LoadLibraryDiscogsReleaseDetailsCommand
            .ExecuteAsync(null);

        Assert.Equal(4242, discogs.RequestedReleaseId);
        Assert.Equal(3, viewModel.SelectedDiscogsRelease!.TrackCount);

        await viewModel.BuildLibraryDiscogsReleaseMappingCommand
            .ExecuteAsync(null);

        Assert.Equal(3, viewModel.DiscogsTrackMappings.Count);
        Assert.All(
            viewModel.DiscogsTrackMappings,
            row => Assert.True(row.IsIncluded));

        await viewModel.PreviewLibraryDiscogsReleaseMetadataCommand
            .ExecuteAsync(null);

        Assert.All(
            metadataOperations.PreviewedValueEdits.Values,
            edits => Assert.Contains(edits, edit =>
                edit.Field.CustomName == "DISCOGS_RELEASE_ID" &&
                edit.Values.SequenceEqual(["4242"])));
        Assert.True(viewModel.HasApplicableOperationPreview);

        await viewModel.PreviewLibraryDiscogsReleaseArtworkCommand
            .ExecuteAsync(null);

        Assert.Equal(
            records.Select(record => Path.GetFullPath(record.Path)),
            metadataOperations.PreviewedArtworkEdits.Keys);
        Assert.All(
            metadataOperations.PreviewedArtworkEdits.Values,
            edit => Assert.Equal(
                ArtworkValueEditMode.ReplaceFrontCover,
                edit.Mode));

        await viewModel.FindLibraryReleaseArtworkCommand.ExecuteAsync(null);

        CoverArtCandidateRow artwork =
            Assert.Single(viewModel.ArtworkMatches);
        Assert.True(artwork.Candidate.IsFront);
        Assert.Equal("4 bytes", artwork.ThumbnailStatus);
        Assert.Equal(searchResult.ReleaseId, coverArt.ReleaseId);

        string[] confirmedArtworkPaths = viewModel.ReleaseTrackMappings
            .Where(row => row.IsIncluded && row.SelectedTrack is not null)
            .Select(row => row.Path)
            .ToArray();
        await viewModel.PreviewLibraryReleaseArtworkCommand.ExecuteAsync(null);

        Assert.Equal(
            confirmedArtworkPaths,
            metadataOperations.PreviewedArtworkEdits.Keys);
        Assert.All(
            metadataOperations.PreviewedArtworkEdits.Values,
            edit => Assert.Equal(
                ArtworkValueEditMode.ReplaceFrontCover, edit.Mode));
        Assert.All(
            metadataOperations.PreviewedArtworkEdits.Values,
            edit => Assert.Equal(
                ID3v2Util.APICType.FrontCover,
                Assert.IsType<ArtworkInput>(edit.Image).Type));
        Assert.Contains(
            viewModel.OperationPreviewChanges,
            row => row.Field == "Artwork");
        Assert.True(viewModel.HasApplicableOperationPreview);

        await viewModel.PreviewLocalLibraryArtworkCommand.ExecuteAsync(null);

        Assert.Equal(
            records.Select(record => Path.GetFullPath(record.Path)),
            metadataOperations.PreviewedArtworkEdits.Keys);
        Assert.All(
            metadataOperations.PreviewedArtworkEdits.Values,
            edit =>
            {
                Assert.Equal(
                    ArtworkValueEditMode.ReplaceFrontCover, edit.Mode);
                Assert.NotNull(edit.Image);
            });

        await viewModel.PreviewRemoveAllLibraryArtworkCommand.ExecuteAsync(null);

        Assert.All(
            metadataOperations.PreviewedArtworkEdits.Values,
            edit =>
            {
                Assert.Equal(ArtworkValueEditMode.RemoveAll, edit.Mode);
                Assert.Null(edit.Image);
            });
        Assert.Contains(
            viewModel.OperationPreviewChanges,
            row => row.Field == "Artwork");
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

internal sealed class FakeMetadataDocumentService(
    params MediaDocument[] documents) :
    IMetadataDocumentService
{
    private readonly Dictionary<string, MediaDocument> _documents =
        documents.ToDictionary(
            document => document.Path,
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

    public Task<MediaDocument> LoadAsync(
        string path,
        bool includeArtwork = true,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_documents[path]);
    }
}

internal class FakeMetadataOperationService : IMetadataOperationService
{
    public IReadOnlyList<string> PreviewedPaths { get; private set; } = [];
    public OperationRecipe? PreviewedRecipe { get; private set; }
    public IReadOnlyDictionary<string, IReadOnlyList<MetadataValueEdit>>
        PreviewedValueEdits { get; private set; } =
            new Dictionary<string, IReadOnlyList<MetadataValueEdit>>();
    public IReadOnlyDictionary<string, ArtworkValueEdit>
        PreviewedArtworkEdits { get; private set; } =
            new Dictionary<string, ArtworkValueEdit>();
    public IReadOnlyDictionary<string, ArtworkSetPreviewRequest>
        PreviewedArtworkSets { get; private set; } =
            new Dictionary<string, ArtworkSetPreviewRequest>();
    public IReadOnlyDictionary<string, IReadOnlyList<TagLayerEdit>>
        PreviewedTagLayerEdits { get; private set; } =
            new Dictionary<string, IReadOnlyList<TagLayerEdit>>();
    public IReadOnlyDictionary<string, Id3VersionEdit>
        PreviewedId3VersionEdits { get; private set; } =
            new Dictionary<string, Id3VersionEdit>();
    public IReadOnlyDictionary<string, TagLayerConversionEdit>
        PreviewedTagLayerConversions { get; private set; } =
            new Dictionary<string, TagLayerConversionEdit>();
    public bool WaitForCancellation { get; init; }
    public bool CancellationObserved { get; private set; }
    public TaskCompletionSource<bool> PreviewStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<MetadataOperationPlan> PreviewAsync(
        IReadOnlyList<string> paths,
        OperationRecipe recipe,
        CancellationToken ct = default) =>
        PreviewCoreAsync(paths, recipe, progress: null, ct);

    public Task<MetadataOperationPlan> PreviewAsync(
        IReadOnlyList<string> paths,
        OperationRecipe recipe,
        IProgress<OperationProgress>? progress,
        CancellationToken ct = default) =>
        PreviewCoreAsync(paths, recipe, progress, ct);

    private async Task<MetadataOperationPlan> PreviewCoreAsync(
        IReadOnlyList<string> paths,
        OperationRecipe recipe,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        PreviewedPaths = paths.ToArray();
        PreviewedRecipe = recipe;
        progress?.Report(new(
            OperationPhase.Planning, 0, paths.Count, Message: "Reading metadata"));
        PreviewStarted.TrySetResult(true);
        if (WaitForCancellation)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }
        string path = paths[0];
        var change = new MetadataFieldDifference(
            MetadataFieldKey.Known(TagFields.Title),
            ["Before"],
            ["Reviewed"]);
        var file = new MetadataFilePlan(
            path,
            new(path, 1, DateTime.UtcNow, "hash"),
            [change],
            [new(change.Field, change.After)],
            []);
        return new MetadataOperationPlan(
            Guid.NewGuid(), recipe.Name, [file], DateTimeOffset.UtcNow, recipe);
    }

    public Task<MetadataOperationPlan> PreviewEditsAsync(
        IReadOnlyDictionary<string, IReadOnlyList<TagEdit>> editsByPath,
        string name,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<MetadataOperationPlan> PreviewValueEditsAsync(
        IReadOnlyDictionary<string, IReadOnlyList<MetadataValueEdit>> editsByPath,
        string name,
        CancellationToken ct = default) =>
        PreviewValueEditsAsync(editsByPath, name, progress: null, ct);

    public Task<MetadataOperationPlan> PreviewValueEditsAsync(
        IReadOnlyDictionary<string, IReadOnlyList<MetadataValueEdit>> editsByPath,
        string name,
        IProgress<OperationProgress>? progress,
        CancellationToken ct = default)
    {
        PreviewedValueEdits = editsByPath;
        progress?.Report(new(
            OperationPhase.Planning,
            0,
            editsByPath.Count,
            Message: "Reading mapped metadata"));
        MetadataFilePlan[] files = editsByPath.Select(
            pair =>
            {
                ImmutableArray<MetadataFieldDifference>
                    differences =
                    [
                        .. pair.Value.Select(edit =>
                            new MetadataFieldDifference(
                                edit.Field,
                                ["Before"],
                                edit.Values)),
                    ];
                return new MetadataFilePlan(
                    pair.Key,
                    new(
                        pair.Key,
                        1,
                        DateTime.UtcNow,
                        "hash"),
                    differences,
                    [.. pair.Value],
                    []);
            }).ToArray();
        return Task.FromResult(new MetadataOperationPlan(
            Guid.NewGuid(),
            name,
            [.. files],
            DateTimeOffset.UtcNow));
    }

    public Task<MetadataOperationPlan> PreviewArtworkEditsAsync(
        IReadOnlyDictionary<string, ArtworkValueEdit> editsByPath,
        string name,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        PreviewedArtworkEdits = editsByPath;
        progress?.Report(new(
            OperationPhase.Planning,
            0,
            editsByPath.Count,
            Message: "Reading mapped artwork"));
        MetadataFilePlan[] files = editsByPath.Select(pair =>
        {
            ArtworkInput? image = pair.Value.Image;
            ImmutableArray<ArtworkDescriptor> before = image is null
                ?
                [
                    new(
                        ID3v2Util.APICType.FrontCover,
                        "image/jpeg",
                        "",
                        4,
                        "old-hash"),
                ]
                : [];
            ImmutableArray<ArtworkDescriptor> after = image is null
                ? []
                :
                [
                    new(
                        image.Type,
                        image.MimeType,
                        image.Description ?? "",
                        image.Data.Length,
                        "hash"),
                ];
            ImmutableArray<ArtworkInput> images = image is null
                ? []
                : [image];
            return new MetadataFilePlan(
                pair.Key,
                new(pair.Key, 1, DateTime.UtcNow, "hash"),
                [],
                [],
                [],
                new(images),
                new(before, after));
        }).ToArray();
        return Task.FromResult(new MetadataOperationPlan(
            Guid.NewGuid(), name, [.. files], DateTimeOffset.UtcNow));
    }

    public Task<MetadataOperationPlan> PreviewArtworkSetsAsync(
        IReadOnlyDictionary<string, ArtworkSetPreviewRequest>
            requestsByPath,
        string name,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        PreviewedArtworkSets = requestsByPath;
        progress?.Report(new(
            OperationPhase.Planning,
            0,
            requestsByPath.Count,
            Message: "Reading complete artwork sets"));
        MetadataFilePlan[] files = requestsByPath.Select(pair =>
        {
            ImmutableArray<ArtworkDescriptor> after =
            [
                .. pair.Value.Images.Select(image =>
                    new ArtworkDescriptor(
                        image.Type,
                        image.MimeType,
                        image.Description ?? "",
                        image.Data.Length,
                        "hash")),
            ];
            return new MetadataFilePlan(
                pair.Key,
                new(pair.Key, 1, DateTime.UtcNow, "hash"),
                [],
                [],
                [],
                new(pair.Value.Images),
                new([], after));
        }).ToArray();
        return Task.FromResult(new MetadataOperationPlan(
            Guid.NewGuid(),
            name,
            [.. files],
            DateTimeOffset.UtcNow));
    }

    public Task<MetadataOperationPlan> PreviewTagLayerEditsAsync(
        IReadOnlyDictionary<string, IReadOnlyList<TagLayerEdit>> editsByPath,
        string name,
        CancellationToken ct = default)
    {
        PreviewedTagLayerEdits = editsByPath;
        (string path, IReadOnlyList<TagLayerEdit> edits) =
            editsByPath.First();
        TagLayerEdit edit = edits.First();
        bool adding = edit.Mode == TagLayerEditMode.Add;
        var difference = new TagLayerDifference(
            edit.Kind, !adding, adding);
        var file = new MetadataFilePlan(
            path,
            new(path, 1, DateTime.UtcNow, "hash"),
            [],
            [],
            [],
            TagLayerEdits: [edit],
            TagLayerDifferences: [difference]);
        return Task.FromResult(new MetadataOperationPlan(
            Guid.NewGuid(), name, [file], DateTimeOffset.UtcNow));
    }

    public Task<MetadataOperationPlan> PreviewId3VersionEditsAsync(
        IReadOnlyDictionary<string, Id3VersionEdit> editsByPath,
        string name,
        CancellationToken ct = default)
    {
        PreviewedId3VersionEdits = editsByPath;
        (string path, Id3VersionEdit edit) = editsByPath.First();
        var difference = new Id3VersionDifference(
            ID3v2Version.V23,
            edit.TargetVersion,
            1,
            []);
        var file = new MetadataFilePlan(
            path,
            new(path, 1, DateTime.UtcNow, "hash"),
            [],
            [],
            [],
            Id3VersionEdit: edit,
            Id3VersionDifference: difference);
        return Task.FromResult(new MetadataOperationPlan(
            Guid.NewGuid(), name, [file], DateTimeOffset.UtcNow));
    }

    public Task<MetadataOperationPlan> PreviewTagLayerConversionsAsync(
        IReadOnlyDictionary<string, TagLayerConversionEdit> editsByPath,
        string name,
        CancellationToken ct = default)
    {
        PreviewedTagLayerConversions = editsByPath;
        (string path, TagLayerConversionEdit edit) = editsByPath.First();
        var difference = new TagLayerConversionDifference(
            edit.Source, edit.Target, []);
        var file = new MetadataFilePlan(
            path,
            new(path, 1, DateTime.UtcNow, "hash"),
            [],
            [],
            [],
            TagLayerConversions: [edit],
            TagLayerConversionDifferences: [difference]);
        return Task.FromResult(new MetadataOperationPlan(
            Guid.NewGuid(), name, [file], DateTimeOffset.UtcNow));
    }

    public virtual Task<MetadataApplyResult> ApplyAsync(
        MetadataOperationPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default) =>
        Task.FromResult(new MetadataApplyResult(
            plan.ChangedFileCount, [], []));
}

internal sealed class FakeDelimitedMetadataImportService :
    IDelimitedMetadataImportService
{
    public IReadOnlyList<string> CandidatePaths { get; private set; } =
        [];
    public DelimitedMetadataImportOptions? Options { get; private set; }

    public Task<DelimitedMetadataImportResult> ImportAsync(
        string sourcePath,
        IReadOnlyList<string> candidateMediaPaths,
        DelimitedMetadataImportOptions? options = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        CandidatePaths = candidateMediaPaths.ToArray();
        Options = options;
        var edits = candidateMediaPaths.ToDictionary(
            path => path,
            path => (IReadOnlyList<MetadataValueEdit>)
            [
                new(
                    MetadataFieldKey.Known(TagFields.Title),
                    ["Imported title"]),
            ],
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        progress?.Report(new(
            OperationPhase.Planning,
            candidateMediaPaths.Count,
            candidateMediaPaths.Count,
            Message: "Delimited import mapped"));
        return Task.FromResult(new DelimitedMetadataImportResult(
            edits,
            [],
            candidateMediaPaths.Count,
            candidateMediaPaths.Count));
    }
}

internal sealed class FakeReportExportService : IReportExportService
{
    public IReadOnlyList<string> PreviewedPaths { get; private set; } = [];
    public bool Applied { get; private set; }

    public Task<ReportExportPlan> PreviewAsync(
        ReportExportRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        PreviewedPaths = request.Paths.ToArray();
        progress?.Report(new(
            OperationPhase.Planning,
            request.Paths.Count,
            request.Paths.Count,
            Message: "Report preview complete"));
        string output = Path.GetFullPath(
            request.Configuration.OutputPath);
        var mutation = new FileMutationPlan(
            "ReportExport",
            Path.GetDirectoryName(output)!,
            "",
            [],
            [],
            DateTimeOffset.UtcNow);
        return Task.FromResult(new ReportExportPlan(
            request,
            [new("", output, request.Paths.Count, 128)],
            mutation,
            []));
    }

    public Task<ReportExportResult> ApplyAsync(
        ReportExportPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Applied = true;
        progress?.Report(new(
            OperationPhase.Completed,
            plan.Files.Count,
            plan.Files.Count,
            Message: "Report written"));
        return Task.FromResult(new ReportExportResult(
            plan.Files.Count,
            plan.Files.Sum(file => file.RowCount),
            new(0, 0, 0, 0, null, []),
            []));
    }
}

internal sealed class FakePlaylistWorkspaceService :
    IPlaylistWorkspaceService
{
    public IReadOnlyList<string> PreviewedPaths { get; private set; } = [];
    public bool Applied { get; private set; }

    public Task<PlaylistWorkspacePlan> PreviewAsync(
        PlaylistWorkspaceRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        PreviewedPaths = request.Paths.ToArray();
        progress?.Report(new(
            OperationPhase.Planning,
            request.Paths.Count,
            request.Paths.Count,
            Message: "Playlist preview complete"));
        string output = Path.GetFullPath(
            request.Configuration.OutputPath);
        var mutation = new FileMutationPlan(
            "PlaylistWorkspace",
            Path.GetDirectoryName(output)!,
            "",
            [],
            [],
            DateTimeOffset.UtcNow);
        return Task.FromResult(new PlaylistWorkspacePlan(
            request,
            [new("", output, request.Paths.Count, 128)],
            mutation,
            []));
    }

    public Task<PlaylistWorkspaceResult> ApplyAsync(
        PlaylistWorkspacePlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Applied = true;
        progress?.Report(new(
            OperationPhase.Completed,
            plan.Files.Count,
            plan.Files.Count,
            Message: "Playlist written"));
        return Task.FromResult(new PlaylistWorkspaceResult(
            plan.Files.Count,
            plan.Files.Sum(file => file.TrackCount),
            new(0, 0, 0, 0, null, []),
            []));
    }
}

internal sealed class FakeExternalToolService : IExternalToolService
{
    public IReadOnlyList<string> PreviewedPaths { get; private set; } = [];
    public bool Ran { get; private set; }

    public ExternalToolPlan Preview(
        ExternalToolDefinition definition,
        IReadOnlyList<string> paths)
    {
        PreviewedPaths = paths.ToArray();
        var invocation = new ExternalToolInvocation(
            definition.Executable,
            paths.ToArray(),
            null,
            paths.ToArray());
        return new(
            definition,
            [invocation],
            [],
            DateTimeOffset.UtcNow);
    }

    public Task<ExternalToolRunResult> RunAsync(
        ExternalToolPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Ran = true;
        progress?.Report(new(
            OperationPhase.Completed,
            plan.Invocations.Count,
            plan.Invocations.Count,
            Message: "Tool complete"));
        return Task.FromResult(new ExternalToolRunResult(
            plan.Invocations.Select(invocation =>
                new ExternalToolInvocationResult(
                    invocation,
                    0,
                    "",
                    "")).ToArray()));
    }
}

internal sealed class FakeExternalToolStore : IExternalToolStore
{
    public List<ExternalToolDefinition> Tools { get; } = [];

    public IReadOnlyList<ExternalToolDefinition> Load() =>
        Tools.ToArray();

    public void Save(ExternalToolDefinition definition)
    {
        int index = Tools.FindIndex(tool =>
            tool.Id == definition.Id);
        if (index < 0)
            Tools.Add(definition);
        else
            Tools[index] = definition;
    }

    public void Delete(Guid id) =>
        Tools.RemoveAll(tool => tool.Id == id);
}

internal sealed class FakeWorkbenchShortcutStore :
    IWorkbenchShortcutStore
{
    public List<WorkbenchShortcutBinding> Bindings { get; } = [];

    public IReadOnlyList<WorkbenchShortcutBinding> Load() =>
        Bindings.ToArray();

    public void Save(
        IReadOnlyList<WorkbenchShortcutBinding> bindings)
    {
        Bindings.Clear();
        Bindings.AddRange(bindings);
    }
}

internal sealed class FakeMetadataGridColumnStore :
    IMetadataGridColumnStore
{
    public List<UserMetadataColumnDescriptor> Workbench { get; } = [];
    public List<UserMetadataColumnDescriptor> Library { get; } = [];

    public IReadOnlyList<UserMetadataColumnDescriptor> Load(
        MetadataGridSurface surface) =>
        List(surface).ToArray();

    public void Save(
        MetadataGridSurface surface,
        IReadOnlyList<UserMetadataColumnDescriptor> columns)
    {
        List<UserMetadataColumnDescriptor> destination =
            List(surface);
        destination.Clear();
        destination.AddRange(columns);
    }

    private List<UserMetadataColumnDescriptor> List(
        MetadataGridSurface surface) =>
        surface == MetadataGridSurface.Workbench
            ? Workbench
            : Library;
}

internal sealed class FakeAcoustIdDiscoveryService : IAcoustIdDiscoveryService
{
    public IReadOnlyList<string> Paths { get; private set; } = [];

    public Task<AcoustIdDiscoveryResult> DiscoverAsync(
        IReadOnlyList<string> paths,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        Paths = paths.ToArray();
        string path = paths[0];
        var fingerprint = new AudioFingerprint(
            path, "AQAD", TimeSpan.FromSeconds(42), 42);
        var lookup = new AcoustIdLookupResult(
            fingerprint,
            [new(
                Guid.Parse("9ff43b6a-4f16-427c-93c2-92307ca505e0"),
                0.92,
                [Guid.Parse("cd2e7c47-16f5-46c6-a37c-a1eb7bf599ff")])],
            DateTimeOffset.UtcNow);
        progress?.Report(new(
            OperationPhase.Completed, 2, 2, path, "Discovery complete"));
        return Task.FromResult(new AcoustIdDiscoveryResult(
            [new(path, fingerprint, lookup, [])]));
    }
}

internal sealed class FakeMusicBrainzMetadataProvider : IMusicBrainzMetadataProvider
{
    public MetadataSourceDescriptor Descriptor { get; } = new(
        "fake-musicbrainz",
        "Fake MusicBrainz",
        MetadataSourceCapabilities.RecordingReleaseLookup |
        MetadataSourceCapabilities.ReleaseSearch |
        MetadataSourceCapabilities.ReleaseDetails);

    public Guid? RecordingId { get; private set; }
    public Guid? RequestedReleaseId { get; private set; }
    public MusicBrainzReleaseSearchQuery? SearchQuery { get; private set; }

    public Task<MusicBrainzReleaseResult> ResolveRecordingAsync(
        Guid recordingId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        RecordingId = recordingId;
        MusicBrainzReleaseCandidate release = CreateRelease(recordingId);
        progress?.Report(new(
            OperationPhase.Completed, 1, 1, Message: "Release lookup complete"));
        return Task.FromResult(new MusicBrainzReleaseResult(
            recordingId, [release], DateTimeOffset.UtcNow));
    }

    public Task<MusicBrainzReleaseSearchResult> SearchReleasesAsync(
        MusicBrainzReleaseSearchQuery query,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        SearchQuery = query;
        MusicBrainzReleaseCandidate summary = CreateRelease(
            Guid.Parse("cd2e7c47-16f5-46c6-a37c-a1eb7bf599ff")) with
        {
            Tracks = [],
        };
        return Task.FromResult(new MusicBrainzReleaseSearchResult(
            [summary], DateTimeOffset.UtcNow));
    }

    public Task<MusicBrainzReleaseCandidate> GetReleaseAsync(
        Guid releaseId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        RequestedReleaseId = releaseId;
        return Task.FromResult(CreateRelease(
            Guid.Parse("cd2e7c47-16f5-46c6-a37c-a1eb7bf599ff")));
    }

    private static MusicBrainzReleaseCandidate CreateRelease(Guid recordingId)
    {
        var track = new MusicBrainzTrackCandidate(
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            1,
            1,
            "1",
            "Matched Song",
            42000,
            recordingId,
            "Matched Song",
            "Matched Artist");
        return new MusicBrainzReleaseCandidate(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "Matched Album",
            "Matched Artist",
            "2026",
            "US",
            "Official",
            null,
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "Matched Album",
            "Album",
            "Matched Label",
            "CAT-1",
            ["Digital Media"],
            [track]);
    }
}

internal sealed class FakeDiscogsMetadataProvider : IDiscogsMetadataProvider
{
    public MetadataSourceDescriptor Descriptor { get; } = new(
        "fake-discogs",
        "Fake Discogs",
        MetadataSourceCapabilities.ReleaseSearch |
        MetadataSourceCapabilities.ReleaseDetails,
        RequiresCredential: true);

    public DiscogsReleaseSearchQuery? SearchQuery { get; private set; }
    public long? RequestedReleaseId { get; private set; }

    public Task<DiscogsReleaseSearchResult> SearchReleasesAsync(
        DiscogsReleaseSearchQuery query,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        SearchQuery = query;
        progress?.Report(new(
            OperationPhase.Completed,
            1,
            1,
            Message: "Discogs search complete"));
        return Task.FromResult(new DiscogsReleaseSearchResult(
            [CreateRelease(withTracks: false)],
            DateTimeOffset.UtcNow));
    }

    public Task<DiscogsReleaseCandidate> GetReleaseAsync(
        long releaseId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RequestedReleaseId = releaseId;
        progress?.Report(new(
            OperationPhase.Completed,
            1,
            1,
            Message: "Discogs release loaded"));
        return Task.FromResult(CreateRelease(withTracks: true));
    }

    public Task<CoverArtDownload> DownloadPrimaryArtworkAsync(
        DiscogsReleaseCandidate release,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        progress?.Report(new(
            OperationPhase.Completed,
            4,
            4,
            Message: "Discogs artwork downloaded"));
        return Task.FromResult(new CoverArtDownload(
            [1, 2, 3, 4],
            "image/jpeg",
            FromCache: false));
    }

    private static DiscogsReleaseCandidate CreateRelease(bool withTracks) =>
        new(
            4242,
            4000,
            "Matched Album",
            "Artist",
            2001,
            "2001-02-03",
            "US",
            ["Matched Label"],
            ["CAT-42"],
            ["1 CD (Album)"],
            ["Electronic"],
            ["Downtempo"],
            ["0123456789012"],
            new Uri("https://www.discogs.com/release/4242"),
            null,
            new Uri("https://i.discogs.com/cover.jpg"),
            withTracks
                ?
                [
                    new("1", "One", null, "Artist"),
                    new("2", "Two", null, "Artist"),
                    new("3", "Three", null, "Artist"),
                ]
                : []);
}

internal sealed class FakeCoverArtArchiveProvider : ICoverArtArchiveProvider
{
    public MetadataSourceDescriptor Descriptor { get; } = new(
        "fake-cover-art",
        "Fake Cover Art Archive",
        MetadataSourceCapabilities.ReleaseArtwork);

    public Guid? ReleaseId { get; private set; }

    public Task<CoverArtArchiveResult> GetReleaseArtworkAsync(
        Guid releaseId,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ReleaseId = releaseId;
        var candidate = new CoverArtArchiveCandidate(
            releaseId,
            "cover-1",
            new("https://coverartarchive.org/original.jpg"),
            new("https://coverartarchive.org/250.jpg"),
            ["Front"],
            true,
            false,
            true,
            null);
        return Task.FromResult(new CoverArtArchiveResult(
            releaseId, [candidate], DateTimeOffset.UtcNow));
    }

    public Task<CoverArtDownload> DownloadAsync(
        CoverArtArchiveCandidate candidate,
        bool thumbnail = false,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default) =>
        Task.FromResult(new CoverArtDownload(
            [1, 2, 3, 4], "image/jpeg", false));
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

internal sealed class FakePlatformService : IPlatformService
{
    public string? Text { get; set; }
    public string? RevealedPath { get; private set; }

    public Task CopyTextAsync(string text)
    {
        Text = text;
        return Task.CompletedTask;
    }

    public Task<string?> ReadTextAsync() =>
        Task.FromResult(Text);

    public void RevealFile(string path) =>
        RevealedPath = path;
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

internal sealed class SelectiveThumbnailService : IThumbnailService
{
    public Task<object?> CreateImageSourceAsync(
        byte[] data,
        int decodePixelWidth = 0,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return data.FirstOrDefault() == 1
            ? Task.FromException<object?>(
                new InvalidDataException("Malformed fixture image."))
            : Task.FromResult<object?>(new object());
    }
}

internal sealed class FakeTheme : IThemeService
{
    public string Current { get; private set; } = "System";
    public void Apply(string theme) => Current = theme;
}
