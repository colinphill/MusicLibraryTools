using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.ComponentModel;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Services;

namespace MusicLibraryManager.Views.WorkbenchSections;

public partial class WorkbenchSessionSectionView : UserControl
{
    private readonly WorkbenchViewModel _viewModel;
    private readonly GridStateService _gridState;
    private readonly ILocalizationService _localization;
    private readonly List<AppGridColumnDefinition>
        _workbenchColumns = [];
    private LibrarySortState? _workbenchSort;
    private bool _restoringSelection;
    private bool _selectionChangePending;
    private bool _compactHeight;

    public WorkbenchSessionSectionView()
    {
        InitializeComponent();
        _viewModel = App.GetService<WorkbenchViewModel>();
        _gridState = App.GetService<GridStateService>();
        _localization = App.GetService<ILocalizationService>();
        WorkbenchGrid.IsReadOnly = false;
        BuildColumns();
        ApplySnapshot(
            _gridState.Load("workbench.session"));
        ConfigureGrid();
        WorkbenchGrid.LayoutChanged += (_, _) =>
            PersistLayout();
        WorkbenchGrid.SortChanged += (_, _) =>
            Dispatcher.UIThread.Post(
                CaptureSortAndPersist);
        AttachedToVisualTree += (_, _) =>
        {
            _viewModel.ColumnEditor.Changed +=
                RebuildMetadataColumns;
            _viewModel.PropertyChanged +=
                OnViewModelPropertyChanged;
            _localization.CultureChanged +=
                OnLocalizationCultureChanged;
            ApplySummaryVisibility();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _viewModel.ColumnEditor.Changed -=
                RebuildMetadataColumns;
            _viewModel.PropertyChanged -=
                OnViewModelPropertyChanged;
            _localization.CultureChanged -=
                OnLocalizationCultureChanged;
        };
        SizeChanged += (_, _) =>
        {
            _compactHeight =
                Bounds.Height > 0 &&
                Bounds.Height < 360;
            EmptyStateSupportingText.IsVisible =
                !_compactHeight;
            ApplySummaryVisibility();
        };
    }

    public event EventHandler? ColumnsRequested;
    public event EventHandler? TranscodeRequested;
    public event Action? ColumnDefinitionsChanged;

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName ==
            nameof(WorkbenchViewModel.SelectedFile))
            ApplySummaryVisibility();
    }

    private void ApplySummaryVisibility() =>
        SessionSummary.IsVisible =
            !_compactHeight &&
            _viewModel.SelectedFile is not null;

    public AppDataGrid SessionGrid =>
        WorkbenchGrid;

    public Button ColumnsButton =>
        WorkbenchColumnsButton;

    public Button SelectionActionsButton =>
        WorkbenchSessionActionsButton;

    public IReadOnlyList<AppGridColumnDefinition>
        ColumnDefinitions =>
            _workbenchColumns;

    public void SetColumnVisibility(
        string key,
        bool visible)
    {
        int index = _workbenchColumns.FindIndex(
            column => column.Key == key);
        if (index < 0)
            return;
        if (!visible &&
            _workbenchColumns.Count(
                column => column.Visible) == 1)
            return;
        _workbenchColumns[index] =
            _workbenchColumns[index] with
            {
                Visible = visible,
            };
        ConfigureGrid();
        PersistLayout();
        ColumnDefinitionsChanged?.Invoke();
    }

    public void MoveColumn(
        string key,
        int offset)
    {
        int index = _workbenchColumns.FindIndex(
            column => column.Key == key);
        if (index < 0 || offset == 0)
            return;
        int destination = Math.Clamp(
            index + offset,
            0,
            _workbenchColumns.Count - 1);
        if (destination == index)
            return;

        AppGridColumnDefinition definition =
            _workbenchColumns[index];
        _workbenchColumns.RemoveAt(index);
        _workbenchColumns.Insert(
            destination,
            definition);
        ConfigureGrid();
        PersistLayout();
        ColumnDefinitionsChanged?.Invoke();
    }

    private void OnColumnsClick(
        object? sender,
        global::Avalonia.Interactivity.RoutedEventArgs e) =>
        ColumnsRequested?.Invoke(this, EventArgs.Empty);

    private void OnTranscodeClick(
        object? sender,
        global::Avalonia.Interactivity.RoutedEventArgs e) =>
        TranscodeRequested?.Invoke(this, EventArgs.Empty);

    private void OnWorkbenchPointerPressed(
        object? sender,
        PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(WorkbenchGrid)
                .Properties.PointerUpdateKind !=
            PointerUpdateKind.RightButtonPressed)
            return;
        DataGridRow? row =
            (e.Source as Control)?
                .GetVisualAncestors()
                .OfType<DataGridRow>()
                .FirstOrDefault();
        if (row?.DataContext is not
            WorkbenchTrackViewModel item ||
            WorkbenchGrid.SelectedItems.Contains(item))
            return;
        WorkbenchGrid.SelectedItems.Clear();
        WorkbenchGrid.SelectedItems.Add(item);
        WorkbenchGrid.SelectedItem = item;
        _viewModel.SetSelectedFiles([item]);
    }

    private void BuildColumns()
    {
        _workbenchColumns.AddRange(
        [
            new("File", L("Column.File"), "FileName", 220, 140, HeaderResourceKey: "Column.File"),
            new("Title", L("Column.Title"), "Title", 220, 120, Editable: true, HeaderResourceKey: "Column.Title"),
            new("Artist", L("Column.Artist"), "Artist", 190, 110, Editable: true, HeaderResourceKey: "Column.Artist"),
            new("AlbumArtist", L("Column.AlbumArtist"), "AlbumArtist", 190, 110, Editable: true, HeaderResourceKey: "Column.AlbumArtist"),
            new("Album", L("Column.Album"), "Album", 210, 120, Editable: true, HeaderResourceKey: "Column.Album"),
            new("Genre", L("Column.Genre"), "Genre", 130, 90, Editable: true, HeaderResourceKey: "Column.Genre"),
            new("Composer", L("Column.Composer"), "Composer", 170, 100, Editable: true, HeaderResourceKey: "Column.Composer"),
            new("Date", L("Column.Date"), "Date", 90, 70, Editable: true, HeaderResourceKey: "Column.Date"),
            new("Track", L("Column.Track"), "Track", 75, 60, Editable: true, HeaderResourceKey: "Column.Track"),
            new("Disc", L("Column.Disc"), "Disc", 70, 60, Editable: true, HeaderResourceKey: "Column.Disc"),
            new("Format", L("Column.Formats"), "Format", 80, 65, HeaderResourceKey: "Column.Formats"),
            new("Codec", L("Column.Codec"), "Codec", 110, 80, false, HeaderResourceKey: "Column.Codec"),
            new("CodecType", L("Column.CodecType"), "CodecType", 105, 80, false, HeaderResourceKey: "Column.CodecType"),
            new("SampleRate", L("Column.SampleRate"), "SampleRate", 115, 85, false, HeaderResourceKey: "Column.SampleRate"),
            new("BitsPerSample", L("Column.Bits"), "BitsPerSample", 70, 55, false, HeaderResourceKey: "Column.Bits"),
            new("Channels", L("Column.Channels"), "Channels", 85, 65, false, HeaderResourceKey: "Column.Channels"),
            new("Duration", L("Column.Duration"), "Duration", 90, 75, HeaderResourceKey: "Column.Duration"),
            new("Bitrate", L("Column.Bitrate"), "Bitrate", 100, 75, HeaderResourceKey: "Column.Bitrate"),
            new("TagLayers", L("Column.TagLayers"), "LayerSummary", 170, 105, false, HeaderResourceKey: "Column.TagLayers"),
            new("Artwork", L("Column.Artwork"), "ArtworkCount", 85, 65, false, HeaderResourceKey: "Column.Artwork"),
            new("FileSize", L("Column.FileSize"), "FileSize", 105, 75, false, HeaderResourceKey: "Column.FileSize"),
            new("Modified", L("Column.Modified"), "Modified", 155, 110, false, HeaderResourceKey: "Column.Modified"),
            new("Path", L("Column.Path"), "Path", 420, 180, false, HeaderResourceKey: "Column.Path"),
        ]);
        AddMetadataColumns();
    }

    private void AddMetadataColumns()
    {
        foreach (UserMetadataColumnRow row in
                 _viewModel.ColumnEditor.Columns
                     .OrderBy(column =>
                         column.Descriptor.Order))
        {
            UserMetadataColumnDescriptor descriptor =
                row.Descriptor;
            string? editPath =
                descriptor.EditTarget is null
                    ? null
                    : WorkbenchEditPath(
                        descriptor.EditTarget);
            var definition =
                new AppGridColumnDefinition(
                    descriptor.ColumnKey,
                    descriptor.Label,
                    editPath ??
                        $"MetadataValues[{descriptor.ValueKey}]",
                    descriptor.Width,
                    70,
                    descriptor.Visible,
                    Editable: editPath is not null,
                    CustomSortComparer:
                        editPath is null
                            ? new MetadataGridRowComparer(
                                descriptor.ValueKey,
                                descriptor.SortType)
                            : null);
            _workbenchColumns.Insert(
                Math.Clamp(
                    descriptor.Order,
                    0,
                    _workbenchColumns.Count),
                definition);
        }
    }

    private static string? WorkbenchEditPath(
        MetadataFieldKey field) =>
        field.KnownField is { } known
            ? MetadataGridColumnEditorViewModel
                .InlineEditPath(known)
            : null;

    private void RebuildMetadataColumns()
    {
        List<LibraryColumnState> columns =
            CaptureColumns()
                .Where(column =>
                    !column.Key.StartsWith(
                        "Metadata.",
                        StringComparison.Ordinal))
                .ToList();
        columns.AddRange(
            _viewModel.ColumnEditor.Columns.Select(
                row =>
                    new LibraryColumnState(
                        row.Descriptor.ColumnKey,
                        row.Descriptor.Width,
                        row.Descriptor.Order,
                        row.Descriptor.Visible)));
        GridSnapshot snapshot =
            new(columns, _workbenchSort);
        _workbenchColumns.RemoveAll(
            column =>
                column.Key.StartsWith(
                    "Metadata.",
                    StringComparison.Ordinal));
        AddMetadataColumns();
        ApplySnapshot(snapshot);
        ConfigureGrid();
        PersistLayout();
        ColumnDefinitionsChanged?.Invoke();
    }

    private void ApplySnapshot(GridSnapshot? snapshot)
    {
        if (snapshot is null)
            return;
        IReadOnlyList<AppGridColumnDefinition> restored =
            PersistedGridLayout.ApplySnapshot(
                _workbenchColumns,
                snapshot);
        _workbenchColumns.Clear();
        _workbenchColumns.AddRange(restored);
        _workbenchSort = snapshot.Sort;
    }

    private void ConfigureGrid()
    {
        WorkbenchGrid.ConfigureColumns(
            _workbenchColumns);
        WorkbenchGrid.ApplySort(
            _workbenchSort);
    }

    private async void OnWorkbenchSelectionChanged(
        object? sender,
        SelectionChangedEventArgs e)
    {
        if (_restoringSelection ||
            _selectionChangePending)
            return;
        if (_viewModel.Inspector?.HasUnsavedChanges !=
            true)
        {
            _viewModel.SetSelectedFiles(
                WorkbenchGrid.SelectedItems
                    .OfType<WorkbenchTrackViewModel>());
            return;
        }
        WorkbenchTrackViewModel[] previous =
            _viewModel.SelectedFiles.ToArray();
        WorkbenchTrackViewModel[] selected =
            WorkbenchGrid.SelectedItems
                .OfType<WorkbenchTrackViewModel>()
                .ToArray();
        _selectionChangePending = true;
        bool accepted;
        try
        {
            accepted =
                await _viewModel
                    .TrySetSelectedFilesAsync(selected);
        }
        finally
        {
            _selectionChangePending = false;
        }
        if (accepted)
            return;
        _restoringSelection = true;
        try
        {
            WorkbenchGrid.SelectedItems.Clear();
            foreach (WorkbenchTrackViewModel file in
                     previous)
                WorkbenchGrid.SelectedItems.Add(file);
            _viewModel.SelectedFile =
                previous.FirstOrDefault();
        }
        finally
        {
            _restoringSelection = false;
        }
    }

    private IReadOnlyList<LibraryColumnState>
        CaptureColumns() =>
            PersistedGridLayout
                .CaptureSnapshotColumns(
                    _workbenchColumns,
                    WorkbenchGrid
                        .CaptureColumnLayout());

    private void PersistLayout() =>
        SaveLayout(CaptureColumns());

    private void SaveLayout(
        IReadOnlyList<LibraryColumnState> columns)
    {
        IReadOnlyList<AppGridColumnDefinition>
            synchronized =
                PersistedGridLayout.ApplySnapshot(
                    _workbenchColumns,
                    new GridSnapshot(
                        columns,
                        _workbenchSort));
        _workbenchColumns.Clear();
        _workbenchColumns.AddRange(synchronized);
        _gridState.Save(
            "workbench.session",
            new(columns, _workbenchSort));
        _viewModel.ColumnEditor.PersistLayout(
            columns);
    }

    private void CaptureSortAndPersist()
    {
        _workbenchSort =
            WorkbenchGrid.CurrentSortKey is not { } key
                ? null
                : new(
                    key,
                    WorkbenchGrid
                        .CurrentSortDescending);
        PersistLayout();
    }

    private string L(string key) =>
        _localization.Get(key);

    private void OnLocalizationCultureChanged(
        object? sender,
        EventArgs e)
    {
        WorkbenchGrid.RefreshLocalizedHeaders();
        ColumnDefinitionsChanged?.Invoke();
    }
}
