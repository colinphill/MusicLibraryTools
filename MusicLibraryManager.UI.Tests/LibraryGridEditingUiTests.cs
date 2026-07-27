using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
public sealed class LibraryGridEditingUiTests
{
    private static readonly MetadataFieldKey
        TotalTracksField =
            MetadataFieldKey.Known(
                TagFields.TotalTracks);
    private static readonly MetadataFieldKey
        CustomField =
            MetadataFieldKey.Custom(
                "DJ_SET");
    private static readonly
        UserMetadataColumnDescriptor
        TotalTracksColumn =
            new(
                Guid.Parse(
                    "2031f38b-c07d-4d8e-8fa4-e0a3b7bf95ef"),
                "Configured track total",
                TotalTracksField,
                true,
                5,
                120,
                MetadataGridColumnSortType.Numeric,
                TotalTracksField);
    private static readonly
        UserMetadataColumnDescriptor
        CustomColumn =
            new(
                Guid.Parse(
                    "f4f58280-7a1f-4b6e-9fdd-f214c717b5ab"),
                "DJ set",
                CustomField,
                true,
                6,
                170,
                MetadataGridColumnSortType.Text,
                CustomField);

    [AvaloniaFact]
    public async Task Standard_title_editor_commits_to_pending_changes()
    {
        using EditingFixture fixture =
            await CreateEditingFixtureAsync();
        DataGridColumn column =
            fixture.Column("Title");

        TextBox editor = await BeginTextEditAsync(
            fixture,
            column,
            nameof(LibraryRow.Title),
            "Original title");
        editor.Text = "Edited through the grid";
        Render();

        Assert.True(
            fixture.Grid.CommitEdit(
                DataGridEditingUnit.Cell,
                true));
        Render();

        await WaitForAsync(
            () =>
                fixture.Row.Title ==
                    "Edited through the grid" &&
                fixture.Model
                    .HasInlinePendingChanges &&
                fixture.Model.PendingChanges
                    .Any(change =>
                        change.After ==
                        "Edited through the grid"),
            "Committing the Title cell did not stage the edit for review.");
        MetadataValueEdit edit =
            Assert.Single(
                fixture.Row
                    .CreatePendingEdits());
        Assert.Equal(
            TagFields.Title,
            edit.Field.KnownField);
        Assert.Equal(
            ["Edited through the grid"],
            edit.Values);
    }

    [AvaloniaFact]
    public async Task Configured_total_tracks_editor_commits_to_pending_changes()
    {
        using EditingFixture fixture =
            await CreateEditingFixtureAsync();
        DataGridColumn column =
            fixture.Column(
                TotalTracksColumn.ColumnKey);

        TextBox editor = await BeginTextEditAsync(
            fixture,
            column,
            nameof(
                LibraryRow
                    .TrackTotalEditValue),
            "12");
        editor.Text = "24";
        Render();

        Assert.True(
            fixture.Grid.CommitEdit(
                DataGridEditingUnit.Cell,
                true));
        Render();

        await WaitForAsync(
            () =>
                fixture.Row
                    .TrackTotalEditValue == "24" &&
                fixture.Model
                    .HasInlinePendingChanges,
            "Committing the configured TotalTracks cell did not stage the edit.");
        MetadataValueEdit edit =
            Assert.Single(
                fixture.Row
                    .CreatePendingEdits());
        Assert.Equal(
            TagFields.TotalTracks,
            edit.Field.KnownField);
        Assert.Equal(["24"], edit.Values);
        Assert.Contains(
            fixture.Model.PendingChanges,
            change =>
                change.After == "24");
    }

    [AvaloniaFact]
    public async Task Custom_string_editor_commits_to_pending_changes()
    {
        using EditingFixture fixture =
            await CreateEditingFixtureAsync();
        DataGridColumn column =
            fixture.Column(
                CustomColumn.ColumnKey);
        string bindingPath =
            $"MetadataValues[" +
            $"{CustomColumn.ValueKey}]";

        TextBox editor = await BeginTextEditAsync(
            fixture,
            column,
            bindingPath,
            "Morning");
        editor.Text = "Evening";
        Render();

        Assert.True(
            fixture.Grid.CommitEdit(
                DataGridEditingUnit.Cell,
                true));
        Render();

        await WaitForAsync(
            () =>
                fixture.Row.MetadataValues[
                    CustomColumn.ValueKey] ==
                    "Evening" &&
                fixture.Model
                    .HasInlinePendingChanges,
            "Committing the custom string cell did not stage the edit.");
        MetadataValueEdit edit =
            Assert.Single(
                fixture.Row
                    .CreatePendingEdits());
        Assert.Equal(
            "DJ_SET",
            edit.Field.CustomName);
        Assert.Equal(
            ["Evening"],
            edit.Values);
        Assert.Contains(
            fixture.Model.PendingChanges,
            change =>
                change.After == "Evening");
    }

    [AvaloniaFact]
    public async Task Escape_cancels_the_active_cell_without_leaving_a_draft()
    {
        using EditingFixture fixture =
            await CreateEditingFixtureAsync();
        TextBox editor = await BeginTextEditAsync(
            fixture,
            fixture.Column("Title"),
            nameof(LibraryRow.Title),
            "Original title");
        editor.Text = "Canceled title";
        editor.Focus();
        Render();
        Assert.Equal(
            "Canceled title",
            fixture.Row.Title);

        editor.RaiseEvent(
            new KeyEventArgs
            {
                RoutedEvent =
                    InputElement.KeyDownEvent,
                Key = Key.Escape,
            });
        Render();

        await WaitForAsync(
            () =>
                fixture.Row.Title ==
                    "Original title" &&
                !fixture.Model
                    .HasInlinePendingChanges &&
                fixture.Model.PendingChanges
                    .Count == 0,
            "Escape did not restore the original cell value and clear its pending draft.");
        Assert.Empty(
            fixture.Row
                .CreatePendingEdits());
    }

    [AvaloniaFact]
    public async Task Grid_is_read_only_while_library_is_loading_or_operating()
    {
        string path = Path.GetFullPath(
            "library-grid-busy.flac");
        var library =
            new FixtureLibraryService(
                [CreateRecord(path)],
                delayReads: true);
        using ServiceProvider services =
            BuildServices(library);
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
            window.Activate();
            await library.ReadStarted.Task
                .WaitAsync(
                    TestContext.Current
                        .CancellationToken);
            Render();

            LibraryViewModel model =
                services.GetRequiredService<
                    LibraryViewModel>();
            AppDataGrid grid =
                view.FindControl<AppDataGrid>(
                    "LibraryGrid")!;
            Assert.True(model.IsBusy);
            Assert.True(grid.IsReadOnly);

            library.AllowRead();
            await WaitForAsync(
                () =>
                    !model.IsBusy &&
                    model.Rows.Count == 1,
                "The delayed Library load did not finish.");
            Assert.False(grid.IsReadOnly);

            LibraryRow row =
                Assert.Single(model.Rows);
            DataGridColumn title =
                grid.Columns.Single(
                    candidate =>
                        grid.KeyFor(candidate) ==
                        "Title");
            grid.SelectedItem = row;
            grid.CurrentColumn = title;
            grid.ScrollIntoView(row, title);
            Render();
            await WaitForAsync(
                () => HasRealizedCell(
                    grid,
                    row,
                    "Original title"),
                "The editable Title cell was not realized.");

            model.IsOperationBusy = true;
            Render();
            Assert.True(grid.IsReadOnly);
            Assert.False(grid.BeginEdit());

            model.IsOperationBusy = false;
            Render();
            Assert.False(grid.IsReadOnly);
            Assert.True(grid.BeginEdit());
            Assert.True(grid.CancelEdit());
        }
        finally
        {
            library.AllowRead();
            window.Close();
            Render();
        }
    }

    private static async Task<EditingFixture>
        CreateEditingFixtureAsync()
    {
        string path = Path.GetFullPath(
            "library-grid-editing.flac");
        var library =
            new FixtureLibraryService(
                [CreateRecord(path)]);
        ServiceProvider services =
            BuildServices(library);
        App.UseServicesForTests(services);
        var view = new LibraryView();
        var window = new Window
        {
            Width = 1200,
            Height = 700,
            Content = view,
        };
        window.Show();
        window.Activate();
        LibraryViewModel model =
            services.GetRequiredService<
                LibraryViewModel>();
        await model.ReloadAsync();
        Render();
        await WaitForAsync(
            () =>
                !model.IsBusy &&
                model.Rows.Count == 1,
            "The Library fixture did not load its row.");
        AppDataGrid grid =
            view.FindControl<AppDataGrid>(
                "LibraryGrid")!;
        Assert.False(grid.IsReadOnly);
        LibraryRow row =
            Assert.Single(model.Rows);
        grid.SelectedItem = row;
        grid.ScrollIntoView(
            row,
            grid.Columns[0]);
        Render();
        await WaitForAsync(
            () => grid.GetVisualDescendants()
                .OfType<DataGridRow>()
                .Any(candidate =>
                    ReferenceEquals(
                        candidate.DataContext,
                        row)),
            "The Library row was not realized for editing.");
        await model.LoadMetadataProjectionAsync(
            row);
        await WaitForAsync(
            () => row.HasExactMetadataValue(
                CustomField),
            "The visible custom metadata cell did not finish loading its exact value.");
        return new(
            services,
            window,
            view,
            model,
            grid,
            row);
    }

    private static ServiceProvider BuildServices(
        ILibraryService library) =>
        Composition.BuildServices(
            collection =>
            {
                collection.AddSingleton<
                    IAppSettings>(
                    new TestSettings());
                collection.AddSingleton(
                    library);
                collection.AddSingleton<
                    IMetadataGridColumnStore>(
                    new FixtureMetadataColumnStore(
                        TotalTracksColumn,
                        CustomColumn));
            });

    private static TrackRecord CreateRecord(
        string path) =>
        new()
        {
            Path = path,
            Title = "Original title",
            TrackTotal = 12,
            Length = 4096,
            LastWriteTime =
                new DateTime(
                    2026,
                    7,
                    20,
                    12,
                    0,
                    0,
                    DateTimeKind.Utc),
            Metadata =
                new Dictionary<
                    string,
                    string[]>
                {
                    [CachedMetadataKeys.Custom(
                        "DJ_SET")] =
                        ["Morning"],
                },
        };

    private static async Task<TextBox>
        BeginTextEditAsync(
        EditingFixture fixture,
        DataGridColumn column,
        string expectedBindingPath,
        string expectedInitialText)
    {
        Control? editingElement = null;
        void OnPreparing(
            object? sender,
            DataGridPreparingCellForEditEventArgs
                eventArgs) =>
            editingElement =
                eventArgs.EditingElement;

        fixture.Grid.PreparingCellForEdit +=
            OnPreparing;
        try
        {
            fixture.Grid.Focus();
            fixture.Grid.SelectedItem =
                fixture.Row;
            fixture.Grid.CurrentColumn =
                column;
            fixture.Grid.ScrollIntoView(
                fixture.Row,
                column);
            Render();
            await WaitForAsync(
                () => HasRealizedCell(
                    fixture.Grid,
                    fixture.Row,
                    expectedInitialText),
                "The target editable cell was not realized.");

            Assert.True(
                fixture.Grid.BeginEdit());
            Render();
        }
        finally
        {
            fixture.Grid.PreparingCellForEdit -=
                OnPreparing;
        }

        var textColumn =
            Assert.IsType<
                DataGridTextColumn>(
                column);
        Binding binding =
            Assert.IsType<Binding>(
                textColumn.Binding);
        Assert.Equal(
            BindingMode.TwoWay,
            binding.Mode);
        Assert.Equal(
            expectedBindingPath,
            binding.Path);
        TextBox editor =
            Assert.IsType<TextBox>(
                editingElement);
        await WaitForAsync(
            () => StringComparer.Ordinal.Equals(
                expectedInitialText,
                editor.Text),
            "The editing control did not receive its row binding.");
        Assert.Equal(
            expectedInitialText,
            editor.Text);
        return editor;
    }

    private static bool HasRealizedCell(
        AppDataGrid grid,
        LibraryRow row,
        string expectedText) =>
        grid.GetVisualDescendants()
            .OfType<DataGridCell>()
            .Where(cell =>
                ReferenceEquals(
                    cell.DataContext,
                    row))
            .SelectMany(cell =>
                cell.GetVisualDescendants()
                    .OfType<TextBlock>())
            .Any(text =>
                StringComparer.Ordinal.Equals(
                    expectedText,
                    text.Text));

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
             attempt < 300 &&
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

    private sealed class EditingFixture(
        ServiceProvider services,
        Window window,
        LibraryView view,
        LibraryViewModel model,
        AppDataGrid grid,
        LibraryRow row) :
        IDisposable
    {
        public LibraryView View { get; } =
            view;
        public LibraryViewModel Model { get; } =
            model;
        public AppDataGrid Grid { get; } =
            grid;
        public LibraryRow Row { get; } =
            row;

        public DataGridColumn Column(
            string key) =>
            Grid.Columns.Single(
                candidate =>
                    Grid.KeyFor(candidate) ==
                    key);

        public void Dispose()
        {
            Model.IsBusy = true;
            window.Close();
            Render();
            services.Dispose();
        }
    }

    private sealed class TestSettings :
        IAppSettings
    {
        private readonly Dictionary<
            string,
            string> _preferences = [];

        public string? ConfigPath => null;
        public LibraryConfiguration?
            Configuration => null;
        public event EventHandler?
            ConfigurationChanged;

        public AppConfigurationSnapshot
            GetSnapshot() =>
            new(null, null, 0);

        public void LoadConfig(string path) =>
            ConfigurationChanged?.Invoke(
                this,
                EventArgs.Empty);

        public string?
            GetRememberedConfigPath() =>
            null;

        public IReadOnlyList<string>
            RecentConfigPaths => [];

        public void ClearRecentConfigs()
        {
        }

        public string? GetPreference(
            string key) =>
            _preferences
                .GetValueOrDefault(key);

        public void SetPreference(
            string key,
            string? value)
        {
            if (value is null)
                _preferences.Remove(key);
            else
                _preferences[key] =
                    value;
        }
    }

    private sealed class FixtureMetadataColumnStore(
        params UserMetadataColumnDescriptor[]
            columns) :
        IMetadataGridColumnStore
    {
        private readonly List<
            UserMetadataColumnDescriptor>
            _columns = [.. columns];

        public IReadOnlyList<
            UserMetadataColumnDescriptor>
            Load(
                MetadataGridSurface surface) =>
            surface ==
                MetadataGridSurface.Library
                ? _columns.ToArray()
                : [];

        public void Save(
            MetadataGridSurface surface,
            IReadOnlyList<
                UserMetadataColumnDescriptor>
                columns)
        {
            if (surface !=
                MetadataGridSurface.Library)
                return;
            _columns.Clear();
            _columns.AddRange(columns);
        }
    }

    private sealed class FixtureLibraryService :
        ILibraryService
    {
        private readonly IReadOnlyList<
            TrackRecord> _records;
        private readonly
            TaskCompletionSource<bool>
            _readRelease =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);

        public FixtureLibraryService(
            IReadOnlyList<TrackRecord> records,
            bool delayReads = false)
        {
            _records = records;
            if (!delayReads)
                _readRelease.TrySetResult(
                    true);
        }

        public TaskCompletionSource<bool>
            ReadStarted { get; } =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);

        public bool IsReady => true;

        public void AllowRead() =>
            _readRelease.TrySetResult(true);

        public Task<(
            int Added,
            int Modified,
            int Removed,
            int Unchanged)> IndexAsync(
                IProgress<IndexProgress>?
                    progress = null,
                CancellationToken ct =
                    default) =>
            Task.FromResult(
                (_records.Count, 0, 0, 0));

        public Task<LibrarySnapshot>
            BuildSnapshotAsync(
                LibraryGrouping grouping =
                    LibraryGrouping.AlbumArtist,
                CancellationToken ct =
                    default) =>
            Task.FromResult(
                new LibrarySnapshot
                {
                    TotalTracks =
                        _records.Count,
                });

        public async Task<
            IReadOnlyList<TrackRecord>>
            GetAllRecordsAsync(
                CancellationToken ct =
                    default)
        {
            ReadStarted.TrySetResult(true);
            await _readRelease.Task
                .WaitAsync(ct);
            return _records;
        }

        public Task<AnalysisReport>
            CheckSetsAsync(
                CancellationToken ct =
                    default) =>
            Task.FromResult(
                new AnalysisReport(
                    "Fixture",
                    []));

        public Task<FileDetails?>
            GetFileDetailsAsync(
                string path,
                bool includeArtwork,
                CancellationToken ct =
                    default) =>
            Task.FromResult<
                FileDetails?>(null);

        public Task<byte[]?>
            GetFirstImageAsync(
                string path,
                CancellationToken ct =
                    default) =>
            Task.FromResult<byte[]?>(null);

        public Task<IReadOnlyList<byte[]?>>
            GetFirstImagesAsync(
                IReadOnlyList<string> paths,
                CancellationToken ct =
                    default) =>
            Task.FromResult<
                IReadOnlyList<byte[]?>>(
                paths.Select(
                        _ => (byte[]?)null)
                    .ToArray());

        public Task<IReadOnlyList<string>>
            GetImageSignaturesAsync(
                IReadOnlyList<string> paths,
                CancellationToken ct =
                    default) =>
            Task.FromResult<
                IReadOnlyList<string>>(
                paths.Select(_ => "")
                    .ToArray());
    }
}
