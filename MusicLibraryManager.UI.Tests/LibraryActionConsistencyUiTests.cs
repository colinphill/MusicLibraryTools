using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MetadataCaching;
using Microsoft.Extensions.DependencyInjection;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Views;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

[Collection(
    ApplicationServiceProviderCollection.Name)]
public sealed class LibraryActionConsistencyUiTests
{
    [AvaloniaFact]
    public async Task Inline_editing_keeps_column_layout_and_sort_state_intact()
    {
        string path = Path.GetFullPath(
            "inline-layout.flac");
        MetadataFieldKey custom =
            MetadataFieldKey.Custom("DJ_SET");
        var descriptor =
            new UserMetadataColumnDescriptor(
                Guid.Parse(
                    "99ceadf4-9cfa-438a-9dc7-331900a42df0"),
                "DJ set",
                custom,
                true,
                4,
                205,
                MetadataGridColumnSortType.Text,
                custom);
        MetadataFieldKey totalField =
            MetadataFieldKey.Known(
                TagFields.TotalTracks);
        var totalDescriptor =
            new UserMetadataColumnDescriptor(
                Guid.Parse(
                    "d364c949-5797-4f67-afee-3abf32a9ade5"),
                "Track total",
                totalField,
                true,
                5,
                110,
                MetadataGridColumnSortType.Numeric,
                totalField);
        using ServiceProvider services =
            Composition.BuildServices(collection =>
            {
                collection.AddSingleton<IAppSettings>(
                    new TestSettings());
                collection.AddSingleton<ILibraryService>(
                    new FixtureLibraryService(
                    [
                        new TrackRecord
                        {
                            Path = path,
                            Title = "Original title",
                            TrackTotal = 12,
                            Metadata =
                                new Dictionary<
                                    string,
                                    string[]>
                                {
                                    [CachedMetadataKeys
                                        .Custom(
                                            "DJ_SET")] =
                                        ["Morning"],
                                },
                        },
                    ]));
                collection.AddSingleton<
                    IMetadataGridColumnStore>(
                    new FixtureMetadataColumnStore(
                        descriptor,
                        totalDescriptor));
            });
        App.UseServicesForTests(services);
        LibraryViewModel model =
            services.GetRequiredService<
                LibraryViewModel>();
        await model.ReloadAsync();
        var view = new LibraryView();
        var window = new Window
        {
            Width = 1200,
            Height = 700,
            Content = view,
        };
        try
        {
            window.Show();
            Render();

            AppDataGrid grid =
                view.FindControl<AppDataGrid>(
                    "LibraryGrid")!;
            model.ColumnEditor.SelectedColumn =
                model.ColumnEditor.Columns.Single(
                    column =>
                        column.Descriptor.Id ==
                            descriptor.Id);
            Render();
            CheckBox inlineToggle =
                view.FindControl<CheckBox>(
                    "LibraryColumnInlineEditingToggle")!;
            Assert.True(inlineToggle.IsEnabled);
            Assert.True(inlineToggle.IsChecked);
            string[] visibleKeys =
            [
                .. grid.Columns.Select(column =>
                    grid.KeyFor(column) ?? "<none>"),
            ];
            Assert.Contains(
                "Title",
                visibleKeys);
            Assert.Contains(
                totalDescriptor.ColumnKey,
                visibleKeys);
            Assert.Contains(
                descriptor.ColumnKey,
                visibleKeys);
            DataGridColumn title = grid.Columns.Single(
                column =>
                    grid.KeyFor(column) == "Title");
            DataGridColumn total = grid.Columns.Single(
                column =>
                    grid.KeyFor(column) ==
                    totalDescriptor.ColumnKey);
            DataGridColumn customColumn =
                grid.Columns.Single(column =>
                    grid.KeyFor(column) ==
                    descriptor.ColumnKey);
            Assert.False(grid.IsReadOnly);
            Assert.False(title.IsReadOnly);
            Assert.False(total.IsReadOnly);
            Assert.False(customColumn.IsReadOnly);
            Assert.Equal(
                BindingMode.TwoWay,
                Assert.IsType<Binding>(
                        Assert.IsType<
                                DataGridTextColumn>(
                                customColumn)
                            .Binding)
                    .Mode);

            title.Width = new DataGridLength(317);
            int customDisplayIndex =
                customColumn.DisplayIndex;
            Assert.True(
                grid.ApplySort(
                    new(
                        "Title",
                        true)));
            LibraryRow row =
                Assert.Single(model.Rows);
            row.Title = "Pending title";
            row.TrackTotalEditValue = "24";
            row.MetadataValues[
                    MetadataGridValueKey.For(custom)] =
                "Evening";
            Render();
            await model.PendingDirectPreviewTask
                .WaitAsync(
                    TestContext.Current
                        .CancellationToken);

            Assert.Same(
                title,
                grid.Columns.Single(column =>
                    grid.KeyFor(column) == "Title"));
            Assert.Same(
                customColumn,
                grid.Columns.Single(column =>
                    grid.KeyFor(column) ==
                    descriptor.ColumnKey));
            Assert.Equal(
                317,
                title.Width.Value);
            Assert.Equal(
                customDisplayIndex,
                customColumn.DisplayIndex);
            Assert.Equal(
                "Title",
                grid.CurrentSortKey);
            Assert.True(
                grid.CurrentSortDescending);
            Assert.Equal(
                3,
                row.CreatePendingEdits()
                    .Count);
            Assert.True(
                model.HasInlinePendingChanges);
            Assert.NotEmpty(
                model.PendingChanges);
        }
        finally
        {
            window.Close();
            Render();
        }
    }

    [AvaloniaFact]
    public async Task Dynamic_numeric_total_column_sorts_by_value_and_preserves_persisted_order()
    {
        MetadataFieldKey totalField =
            MetadataFieldKey.Known(
                TagFields.TotalTracks);
        var totalDescriptor =
            new UserMetadataColumnDescriptor(
                Guid.Parse(
                    "4214f782-6e4a-4ea2-9cf1-1b1917691e2e"),
                "Configured track total",
                totalField,
                true,
                5,
                110,
                MetadataGridColumnSortType.Numeric,
                totalField);
        var settings = new TestSettings();
        using ServiceProvider services =
            Composition.BuildServices(collection =>
            {
                collection.AddSingleton<IAppSettings>(
                    settings);
                collection.AddSingleton<ILibraryService>(
                    new FixtureLibraryService(
                    [
                        new TrackRecord
                        {
                            Path = Path.GetFullPath(
                                "numeric-total-10.flac"),
                            Title = "Ten",
                            TrackTotal = 10,
                        },
                        new TrackRecord
                        {
                            Path = Path.GetFullPath(
                                "numeric-total-2.flac"),
                            Title = "Two",
                            TrackTotal = 2,
                        },
                    ]));
                collection.AddSingleton<
                    IMetadataGridColumnStore>(
                    new FixtureMetadataColumnStore(
                        totalDescriptor));
            });
        App.UseServicesForTests(services);
        var firstView = new LibraryView();
        var firstWindow = new Window
        {
            Width = 1200,
            Height = 700,
            Content = firstView,
        };
        string[] persistedOrder;
        try
        {
            firstWindow.Show();
            LibraryViewModel model =
                services.GetRequiredService<
                    LibraryViewModel>();
            await model.ReloadAsync();
            Render();

            AppDataGrid grid =
                firstView.FindControl<AppDataGrid>(
                    "LibraryGrid")!;
            DataGridColumn total =
                grid.Columns.Single(column =>
                    grid.KeyFor(column) ==
                    totalDescriptor.ColumnKey);
            Assert.NotNull(
                total.CustomSortComparer);

            total.DisplayIndex = 2;
            Render();
            persistedOrder =
            [
                .. grid.Columns
                    .OrderBy(column =>
                        column.DisplayIndex)
                    .Select(column =>
                        grid.KeyFor(column)!),
            ];

            Assert.True(
                grid.ApplySort(
                    new(
                        totalDescriptor.ColumnKey,
                        false)));
            await WaitForAsync(
                () => grid.CollectionView!
                    .Cast<LibraryRow>()
                    .Select(row =>
                        row.Record.TrackTotal)
                    .SequenceEqual(
                        [2, 10]),
                "The configured numeric metadata column did not sort 2 before 10.");

            Assert.Equal(
                [2, 10],
                grid.CollectionView!
                    .Cast<LibraryRow>()
                    .Select(row =>
                        row.Record.TrackTotal));
            Assert.Equal(
                persistedOrder,
                grid.Columns
                    .OrderBy(column =>
                        column.DisplayIndex)
                    .Select(column =>
                        grid.KeyFor(column)));
        }
        finally
        {
            firstWindow.Close();
            Render();
        }

        var reopenedView = new LibraryView();
        var reopenedWindow = new Window
        {
            Width = 1200,
            Height = 700,
            Content = reopenedView,
        };
        try
        {
            reopenedWindow.Show();
            Render();
            AppDataGrid reopenedGrid =
                reopenedView.FindControl<AppDataGrid>(
                    "LibraryGrid")!;

            Assert.Equal(
                persistedOrder,
                reopenedGrid.Columns
                    .OrderBy(column =>
                        column.DisplayIndex)
                    .Select(column =>
                        reopenedGrid.KeyFor(column)));
            Assert.Equal(
                totalDescriptor.ColumnKey,
                reopenedGrid.CurrentSortKey);
            Assert.False(
                reopenedGrid
                    .CurrentSortDescending);
        }
        finally
        {
            reopenedWindow.Close();
            Render();
        }
    }

    [AvaloniaFact]
    public async Task Selection_bar_and_grid_menu_share_multi_selection_state_and_captured_paths()
    {
        string first = Path.GetFullPath(
            "action-first.flac");
        string second = Path.GetFullPath(
            "action-second.flac");
        string third = Path.GetFullPath(
            "action-third.flac");
        TrackRecord[] records =
        [
            new()
            {
                Path = first,
                Title = "First",
            },
            new()
            {
                Path = second,
                Title = "Second",
            },
            new()
            {
                Path = third,
                Title = "Third",
            },
        ];
        var platform =
            new RecordingPlatformService();
        var reindex =
            new RecordingReindexService();
        var workbench =
            new RecordingWorkbenchService();
        using ServiceProvider services =
            Composition.BuildServices(collection =>
            {
                collection.AddSingleton<IAppSettings>(
                    new TestSettings());
                collection.AddSingleton<ILibraryService>(
                    new FixtureLibraryService(records));
                collection.AddSingleton<IPlatformService>(
                    platform);
                collection.AddSingleton<IReindexService>(
                    reindex);
                collection.AddSingleton<IWorkbenchService>(
                    workbench);
            });
        App.UseServicesForTests(services);
        var view = new LibraryView();
        var window = new Window
        {
            Width = 1200,
            Height = 700,
            Content = view,
        };
        try
        {
            window.Show();
            Render();
            LibraryViewModel viewModel =
                services.GetRequiredService<
                    LibraryViewModel>();
            await viewModel.ReloadAsync();
            Assert.True(
                await viewModel.SelectAsync(
                    [
                        viewModel.Rows[0],
                        viewModel.Rows[1],
                    ]));
            Render();

            Button handoff =
                view.FindControl<Button>(
                    "SelectionWorkbenchButton")!;
            Button copy =
                view.FindControl<Button>(
                    "CopyButton")!;
            Button reveal =
                view.FindControl<Button>(
                    "RevealButton")!;
            Button refresh =
                view.FindControl<Button>(
                    "ReindexButton")!;
            AppDataGrid grid =
                view.FindControl<AppDataGrid>(
                    "LibraryGrid")!;
            ContextMenu contextMenu =
                Assert.IsType<ContextMenu>(
                    grid.ContextMenu);
            grid.RaiseEvent(
                new ContextRequestedEventArgs
                {
                    RoutedEvent =
                        InputElement
                            .ContextRequestedEvent,
                });
            Render();

            object[] menuItems =
            [
                .. contextMenu.ItemsSource!
                    .Cast<object>(),
            ];
            Assert.Equal(7, menuItems.Length);
            MenuItem selectedHandoff =
                Assert.IsType<MenuItem>(
                    menuItems[0]);
            MenuItem menuCopy =
                Assert.IsType<MenuItem>(
                    menuItems[4]);
            MenuItem menuReveal =
                Assert.IsType<MenuItem>(
                    menuItems[5]);
            MenuItem menuRefresh =
                Assert.IsType<MenuItem>(
                    menuItems[6]);

            Assert.True(handoff.IsVisible);
            Assert.True(copy.IsVisible);
            Assert.True(reveal.IsVisible);
            Assert.True(refresh.IsVisible);
            Assert.Equal(
                handoff.IsEnabled,
                selectedHandoff.IsEnabled);
            Assert.Equal(
                copy.IsEnabled,
                menuCopy.IsEnabled);
            Assert.Equal(
                reveal.IsEnabled,
                menuReveal.IsEnabled);
            Assert.Equal(
                refresh.IsEnabled,
                menuRefresh.IsEnabled);
            Assert.True(handoff.IsEnabled);
            Assert.True(copy.IsEnabled);
            Assert.False(reveal.IsEnabled);
            Assert.True(refresh.IsEnabled);

            MenuItem capturedSection =
                Assert.IsType<MenuItem>(
                    selectedHandoff
                        .ItemsSource!
                        .Cast<object>()
                        .First());

            Assert.True(
                await viewModel.SelectAsync(
                    [viewModel.Rows[2]]));
            Render();

            menuCopy.RaiseEvent(
                new RoutedEventArgs(
                    MenuItem.ClickEvent));
            await WaitForAsync(
                () => platform.Text is not null,
                "The captured context-menu copy action did not run.");
            Assert.Equal(
                string.Join(
                    Environment.NewLine,
                    first,
                    second),
                platform.Text);

            menuReveal.RaiseEvent(
                new RoutedEventArgs(
                    MenuItem.ClickEvent));
            Assert.Null(platform.RevealedPath);

            menuRefresh.RaiseEvent(
                new RoutedEventArgs(
                    MenuItem.ClickEvent));
            await WaitForAsync(
                () => reindex.Paths.Count == 2,
                "The captured context-menu refresh action did not run.");
            Assert.Equal(
                [first, second],
                reindex.Paths);
            await WaitForAsync(
                () => !viewModel.IsBusy,
                "The captured refresh did not finish.");

            capturedSection.RaiseEvent(
                new RoutedEventArgs(
                    MenuItem.ClickEvent));
            await WaitForAsync(
                () => workbench.Requests.Count == 1,
                "The captured context-menu Workbench handoff did not run.");
            Assert.Equal(
                [first, second],
                Assert.Single(
                    workbench.Requests)
                    .Sources);

            copy.RaiseEvent(
                new RoutedEventArgs(
                    Button.ClickEvent));
            await WaitForAsync(
                () => platform.Text == third,
                "The visible copy action did not use the current selection.");
            Assert.Equal(third, platform.Text);
            Assert.True(reveal.IsEnabled);
            reveal.RaiseEvent(
                new RoutedEventArgs(
                    Button.ClickEvent));
            Assert.Equal(
                third,
                platform.RevealedPath);
        }
        finally
        {
            window.Close();
            Render();
        }
    }

    [AvaloniaFact]
    public void Library_column_pickers_render_localized_choices_with_stable_values()
    {
        using ServiceProvider services =
            Composition.BuildServices(collection =>
                collection.AddSingleton<IAppSettings>(
                    new TestSettings()));
        App.UseServicesForTests(services);
        var view = new LibraryView();
        var window = new Window
        {
            Width = 1200,
            Height = 700,
            Content = view,
        };
        try
        {
            window.Show();
            Render();
            ComboBox fieldKind =
                view.FindControl<ComboBox>(
                    "LibraryColumnFieldKindPicker")!;
            ComboBox sortType =
                view.FindControl<ComboBox>(
                    "LibraryColumnSortTypePicker")!;

            LocalizedChoice<MetadataGridFieldKind>[]
                fieldChoices =
                [
                    .. fieldKind.ItemsSource!
                        .Cast<
                            LocalizedChoice<
                                MetadataGridFieldKind>>(),
                ];
            LocalizedChoice<
                MetadataGridColumnSortType>[]
                sortChoices =
                [
                    .. sortType.ItemsSource!
                        .Cast<
                            LocalizedChoice<
                                MetadataGridColumnSortType>>(),
                ];

            Assert.Equal(
                [
                    MetadataGridFieldKind.Known,
                    MetadataGridFieldKind.Custom,
                ],
                fieldChoices.Select(
                    choice => choice.Value));
            Assert.Equal(
                [
                    MetadataGridColumnSortType.Text,
                    MetadataGridColumnSortType.Numeric,
                    MetadataGridColumnSortType.Date,
                ],
                sortChoices.Select(
                    choice => choice.Value));
            Assert.Equal(
                "Standard metadata field",
                fieldChoices[0].Label);
            Assert.Equal(
                "Custom metadata field",
                fieldChoices[1].Label);
            LibraryViewModel viewModel =
                Assert.IsType<LibraryViewModel>(
                    view.DataContext);
            fieldKind.SelectedValue =
                MetadataGridFieldKind.Custom;
            sortType.SelectedValue =
                MetadataGridColumnSortType.Numeric;
            Render();
            Assert.Equal(
                MetadataGridFieldKind.Custom,
                viewModel.ColumnEditor.FieldKind);
            Assert.Equal(
                MetadataGridColumnSortType.Numeric,
                viewModel.ColumnEditor.SortType);
        }
        finally
        {
            window.Close();
            Render();
        }
    }

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform
            .ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitForAsync(
        Func<bool> condition,
        string message)
    {
        for (int attempt = 0;
             attempt < 80 &&
             !condition();
             attempt++)
        {
            Render();
            await Task.Delay(
                5,
                TestContext.Current
                    .CancellationToken);
        }
        Assert.True(condition(), message);
    }

    private sealed class TestSettings : IAppSettings
    {
        private readonly Dictionary<string, string>
            _preferences = [];

        public string? ConfigPath => null;
        public LibraryConfiguration? Configuration =>
            null;
        public event EventHandler? ConfigurationChanged;

        public AppConfigurationSnapshot GetSnapshot() =>
            new(null, null, 0);

        public void LoadConfig(string path) =>
            ConfigurationChanged?.Invoke(
                this,
                EventArgs.Empty);

        public string? GetRememberedConfigPath() =>
            null;
        public IReadOnlyList<string>
            RecentConfigPaths => [];
        public void ClearRecentConfigs()
        {
        }

        public string? GetPreference(string key) =>
            _preferences.GetValueOrDefault(key);

        public void SetPreference(
            string key,
            string? value)
        {
            if (value is null)
                _preferences.Remove(key);
            else
                _preferences[key] = value;
        }
    }

    private sealed class RecordingPlatformService :
        IPlatformService
    {
        public string? Text { get; private set; }
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

    private sealed class RecordingReindexService :
        IReindexService
    {
        public List<string> Paths { get; } = [];

        public Task ReindexFileAsync(
            string path,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Paths.Add(path);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingWorkbenchService :
        IWorkbenchService
    {
        public List<WorkbenchLoadRequest> Requests { get; } =
            [];

        public Task<WorkbenchLoadResult> LoadAsync(
            WorkbenchLoadRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(
                new WorkbenchLoadResult(
                    [
                        .. request.Sources.Select(
                            source =>
                                CreateDocument(
                                    source)),
                    ],
                    []));
        }

        private static MediaDocument CreateDocument(
            string source)
        {
            string path =
                Path.GetFullPath(source);
            return new(
                path,
                [new(
                    "VorbisComment",
                    [new(
                        MetadataFieldKey.Known(
                            TagFields.Title),
                        [
                            Path
                                .GetFileNameWithoutExtension(
                                    path),
                        ])],
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
                    $"snapshot:{path}"),
                true);
        }
    }

    private sealed class FixtureLibraryService(
        IReadOnlyList<TrackRecord> records) :
        ILibraryService
    {
        public bool IsReady => true;

        public Task<(
            int Added,
            int Modified,
            int Removed,
            int Unchanged)> IndexAsync(
            IProgress<IndexProgress>? progress = null,
            CancellationToken ct = default) =>
            Task.FromResult(
                (records.Count, 0, 0, 0));

        public Task<LibrarySnapshot>
            BuildSnapshotAsync(
                LibraryGrouping grouping =
                    LibraryGrouping.AlbumArtist,
                CancellationToken ct = default) =>
            Task.FromResult(
                new LibrarySnapshot
                {
                    TotalTracks = records.Count,
                });

        public Task<IReadOnlyList<TrackRecord>>
            GetAllRecordsAsync(
                CancellationToken ct = default) =>
            Task.FromResult(records);

        public Task<AnalysisReport> CheckSetsAsync(
            CancellationToken ct = default) =>
            Task.FromResult(
                new AnalysisReport(
                    "Fixture",
                    []));

        public Task<FileDetails?> GetFileDetailsAsync(
            string path,
            bool includeArtwork,
            CancellationToken ct = default) =>
            Task.FromResult<FileDetails?>(null);

        public Task<byte[]?> GetFirstImageAsync(
            string path,
            CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(null);

        public Task<IReadOnlyList<byte[]?>>
            GetFirstImagesAsync(
                IReadOnlyList<string> paths,
                CancellationToken ct = default) =>
            Task.FromResult<
                IReadOnlyList<byte[]?>>(
                paths
                    .Select(_ => (byte[]?)null)
                    .ToArray());

        public Task<IReadOnlyList<string>>
            GetImageSignaturesAsync(
                IReadOnlyList<string> paths,
                CancellationToken ct = default) =>
            Task.FromResult<
                IReadOnlyList<string>>(
                paths
                    .Select(_ => "")
                    .ToArray());
    }

    private sealed class FixtureMetadataColumnStore(
        params UserMetadataColumnDescriptor[] columns) :
        IMetadataGridColumnStore
    {
        private readonly List<
            UserMetadataColumnDescriptor> _columns =
            [.. columns];

        public IReadOnlyList<
            UserMetadataColumnDescriptor> Load(
            MetadataGridSurface surface) =>
            surface == MetadataGridSurface.Library
                ? _columns.ToArray()
                : [];

        public void Save(
            MetadataGridSurface surface,
            IReadOnlyList<
                UserMetadataColumnDescriptor> columns)
        {
            if (surface !=
                MetadataGridSurface.Library)
                return;
            _columns.Clear();
            _columns.AddRange(columns);
        }
    }
}
