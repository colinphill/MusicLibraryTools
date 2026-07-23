using global::Avalonia.Controls;
using global::Avalonia.Controls.Templates;
using global::Avalonia.Data;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Markup.Xaml;
using global::Avalonia.Media;
using global::Avalonia.Platform.Storage;
using global::Avalonia.Threading;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Services;

namespace MusicLibraryManager.Views;

public partial class WorkbenchView : UserControl
{
    private readonly WorkbenchViewModel _viewModel;
    private readonly GridStateService _gridState;
    private readonly List<AppGridColumnDefinition>
        _workbenchColumns = [];
    private LibrarySortState? _workbenchSort;

    public WorkbenchView()
    {
        InitializeComponent();
        _viewModel = App.GetService<WorkbenchViewModel>();
        _gridState = App.GetService<GridStateService>();
        DataContext = _viewModel;
        WorkbenchGrid.IsReadOnly = false;
        BuildWorkbenchColumns();
        ApplyWorkbenchSnapshot(
            _gridState.Load("workbench.session"));
        ConfigureWorkbenchGrid();
        BuildWorkbenchColumnOptions();
        WorkbenchGrid.LayoutChanged += (_, _) =>
            PersistWorkbenchLayout();
        WorkbenchGrid.SortChanged += (_, _) =>
            Dispatcher.UIThread.Post(
                CaptureWorkbenchSortAndPersist);
        AttachedToVisualTree += (_, _) =>
            _viewModel.ColumnEditor.Changed +=
                RebuildWorkbenchMetadataColumns;
        DetachedFromVisualTree += (_, _) =>
            _viewModel.ColumnEditor.Changed -=
                RebuildWorkbenchMetadataColumns;
        PreviewGrid.ConfigureColumns(
        [
            new("File", "File", "File", 220, 140),
            new("Field", "Field", "Field", 150, 100),
            new("Before", "Before", "Before", 320, 180),
            new("After", "After", "After", 320, 180),
        ]);
        MetadataFieldsGrid.ConfigureColumns(
        [
            new("Name", "Field", "Name", 210, 120),
            new("Kind", "Kind", "Kind", 90, 70),
            new("Layers", "Tag layers", "Layers", 150, 100),
            new("Value", "Values", "DisplayValue", 340, 180),
        ]);
        AudioDiscoveryGrid.ConfigureColumns(
        [
            new("File", "File", "File", 220, 130),
            new("Duration", "Duration", "Duration", 90, 70),
            new("Confidence", "Confidence", "Confidence", 105, 85),
            new("AcoustID", "AcoustID", "AcoustId", 285, 180),
            new("MusicBrainz", "MusicBrainz recording IDs",
                "MusicBrainzRecordingIds", 390, 220),
            new("Status", "Status", "Status", 280, 160),
        ]);
        ReleaseDiscoveryGrid.ConfigureColumns(
        [
            new("Title", "Release", "Title", 240, 140),
            new("Artist", "Artist credit", "Artist", 190, 110),
            new("Date", "Date", "Date", 100, 75),
            new("Country", "Country", "Country", 80, 65),
            new("Status", "Status", "Status", 90, 70),
            new("Label", "Label", "Label", 170, 100),
            new("Catalog", "Catalog no.", "CatalogNumber", 120, 85),
            new("Formats", "Formats", "Formats", 140, 90),
            new("Position", "Matched position", "MatchedTrackPositions", 140, 95),
            new("Tracks", "Tracks", "TrackCount", 75, 60),
            new("ReleaseID", "MusicBrainz release ID", "ReleaseId", 280, 180),
        ]);
        ConfigureDiscogsGrid(DiscogsDiscoveryGrid);
        ConfigureDiscogsTrackMappingGrid(DiscogsTrackMappingGrid);
        ConfigureReleaseTrackMappingGrid(ReleaseTrackMappingGrid);
        ConfigureReleaseArtworkGrid(ReleaseArtworkGrid);
        ReportOutputGrid.ConfigureColumns(
        [
            new("Group", "Group", "Group", 150, 90),
            new("File", "Destination", "File", 420, 220),
            new("Rows", "Rows", "Rows", 80, 60),
            new("Bytes", "Bytes", "Bytes", 100, 70),
        ]);
        PlaylistOutputGrid.ConfigureColumns(
        [
            new("Group", "Group", "Group", 150, 90),
            new("File", "Destination", "File", 420, 220),
            new("Tracks", "Tracks", "Tracks", 80, 60),
            new("Bytes", "Bytes", "Bytes", 100, 70),
        ]);
        ExternalToolInvocationGrid.ConfigureColumns(
        [
            new("Number", "#", "Number", 55, 45),
            new("Executable", "Executable", "Executable", 190, 120),
            new("Arguments", "Arguments", "Arguments", 360, 190),
            new("WorkingDirectory", "Working directory",
                "WorkingDirectory", 220, 130),
            new("Files", "Files", "Files", 65, 52),
        ]);
    }

    private void BuildWorkbenchColumns()
    {
        _workbenchColumns.AddRange(
        [
            new("File", "File", "FileName", 220, 140),
            new("Title", "Title", "Title", 220, 120,
                Editable: true),
            new("Artist", "Artist", "Artist", 190, 110,
                Editable: true),
            new("AlbumArtist", "Album artist", "AlbumArtist",
                190, 110, Editable: true),
            new("Album", "Album", "Album", 210, 120,
                Editable: true),
            new("Genre", "Genre", "Genre", 130, 90,
                Editable: true),
            new("Composer", "Composer", "Composer", 170, 100,
                Editable: true),
            new("Date", "Date", "Date", 90, 70,
                Editable: true),
            new("Track", "Track", "Track", 75, 60,
                Editable: true),
            new("Disc", "Disc", "Disc", 70, 60,
                Editable: true),
            new("Format", "Format", "Format", 80, 65),
            new("Codec", "Codec", "Codec", 110, 80, false),
            new("CodecType", "Codec type", "CodecType",
                105, 80, false),
            new("SampleRate", "Sample rate", "SampleRate",
                115, 85, false),
            new("BitsPerSample", "Bits", "BitsPerSample",
                70, 55, false),
            new("Channels", "Channels", "Channels",
                85, 65, false),
            new("Duration", "Duration", "Duration", 90, 75),
            new("Bitrate", "Bitrate", "Bitrate", 100, 75),
            new("TagLayers", "Tag layers", "LayerSummary",
                170, 105, false),
            new("Artwork", "Artwork", "ArtworkCount",
                85, 65, false),
            new("FileSize", "File size", "FileSize",
                105, 75, false),
            new("Modified", "Modified", "Modified",
                155, 110, false),
            new("Path", "Path", "Path", 420, 180, false),
        ]);
        AddWorkbenchMetadataColumns();
    }

    private void AddWorkbenchMetadataColumns()
    {
        foreach (UserMetadataColumnRow row in
                 _viewModel.ColumnEditor.Columns.OrderBy(column =>
                     column.Descriptor.Order))
        {
            UserMetadataColumnDescriptor descriptor =
                row.Descriptor;
            string? editPath = descriptor.EditTarget is null
                ? null
                : WorkbenchEditPath(descriptor.EditTarget);
            var definition = new AppGridColumnDefinition(
                descriptor.ColumnKey,
                descriptor.Label,
                editPath ??
                    $"MetadataValues[{descriptor.ValueKey}]",
                descriptor.Width,
                70,
                descriptor.Visible,
                Editable: editPath is not null,
                CustomSortComparer: editPath is null
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
        field.KnownField switch
        {
            MusicFileUtilities.TagFields.Title => "Title",
            MusicFileUtilities.TagFields.Artist => "Artist",
            MusicFileUtilities.TagFields.AlbumArtist =>
                "AlbumArtist",
            MusicFileUtilities.TagFields.Album => "Album",
            MusicFileUtilities.TagFields.Genre => "Genre",
            MusicFileUtilities.TagFields.Composer => "Composer",
            MusicFileUtilities.TagFields.Date => "Date",
            MusicFileUtilities.TagFields.TrackNumber => "Track",
            MusicFileUtilities.TagFields.DiscNumber => "Disc",
            _ => null,
        };

    private void RebuildWorkbenchMetadataColumns()
    {
        List<LibraryColumnState> columns =
            CaptureWorkbenchColumns()
                .Where(column =>
                    !column.Key.StartsWith(
                        "Metadata.",
                        StringComparison.Ordinal))
                .ToList();
        columns.AddRange(
            _viewModel.ColumnEditor.Columns.Select(row =>
                new LibraryColumnState(
                    row.Descriptor.ColumnKey,
                    row.Descriptor.Width,
                    row.Descriptor.Order,
                    row.Descriptor.Visible)));
        GridSnapshot snapshot = new(
            columns,
            _workbenchSort);
        _workbenchColumns.RemoveAll(column =>
            column.Key.StartsWith(
                "Metadata.",
                StringComparison.Ordinal));
        AddWorkbenchMetadataColumns();
        ApplyWorkbenchSnapshot(snapshot);
        ConfigureWorkbenchGrid();
        BuildWorkbenchColumnOptions();
        PersistWorkbenchLayout();
    }

    private void ApplyWorkbenchSnapshot(GridSnapshot? snapshot)
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

    private void ConfigureWorkbenchGrid()
    {
        WorkbenchGrid.ConfigureColumns(_workbenchColumns);
        WorkbenchGrid.ApplySort(_workbenchSort);
    }

    private void BuildWorkbenchColumnOptions()
    {
        WorkbenchColumnOptions.Children.Clear();
        foreach (AppGridColumnDefinition definition in
                 _workbenchColumns)
        {
            var check = new CheckBox
            {
                Content = definition.Header,
                IsChecked = definition.Visible,
                Tag = definition.Key,
            };
            check.IsCheckedChanged +=
                OnWorkbenchColumnChecked;
            WorkbenchColumnOptions.Children.Add(check);
        }
    }

    private void OnWorkbenchColumnChecked(
        object? sender,
        RoutedEventArgs e)
    {
        if (sender is not CheckBox
            {
                Tag: string key,
                IsChecked: bool visible,
            })
            return;
        int index = _workbenchColumns.FindIndex(column =>
            column.Key == key);
        if (index < 0)
            return;
        if (!visible &&
            _workbenchColumns.Count(column => column.Visible) == 1)
        {
            ((CheckBox)sender).IsChecked = true;
            return;
        }
        _workbenchColumns[index] =
            _workbenchColumns[index] with { Visible = visible };
        ConfigureWorkbenchGrid();
        PersistWorkbenchLayout();
    }

    private IReadOnlyList<LibraryColumnState>
        CaptureWorkbenchColumns()
    {
        IReadOnlyList<LibraryColumnState> visible =
            WorkbenchGrid.CaptureColumnLayout();
        Dictionary<string, LibraryColumnState> byKey =
            visible.ToDictionary(
                column => column.Key,
                StringComparer.OrdinalIgnoreCase);
        int displayIndex = 0;
        var result = new List<LibraryColumnState>(
            _workbenchColumns.Count);
        foreach (AppGridColumnDefinition definition in
                 _workbenchColumns.OrderBy(column =>
                     byKey.TryGetValue(
                         column.Key,
                         out LibraryColumnState? state)
                         ? state.DisplayIndex
                         : int.MaxValue))
        {
            if (byKey.TryGetValue(
                    definition.Key,
                    out LibraryColumnState? state))
                result.Add(state with
                {
                    DisplayIndex = displayIndex++,
                    Visible = true,
                });
            else
                result.Add(new(
                    definition.Key,
                    definition.Width,
                    displayIndex++,
                    false));
        }
        return result;
    }

    private void PersistWorkbenchLayout() =>
        SaveWorkbenchLayout(CaptureWorkbenchColumns());

    private void SaveWorkbenchLayout(
        IReadOnlyList<LibraryColumnState> columns)
    {
        _gridState.Save(
            "workbench.session",
            new(columns, _workbenchSort));
        _viewModel.ColumnEditor.PersistLayout(columns);
    }

    private void CaptureWorkbenchSortAndPersist()
    {
        _workbenchSort =
            WorkbenchGrid.CurrentSortKey is not { } key
                ? null
                : new(
                    key,
                    WorkbenchGrid.CurrentSortDescending);
        PersistWorkbenchLayout();
    }

    private void OnWorkbenchColumnsClick(
        object? sender,
        RoutedEventArgs e) =>
        WorkbenchColumnPopover.IsOpen =
            !WorkbenchColumnPopover.IsOpen;

    private void OnWorkbenchColumnsClose(
        object? sender,
        RoutedEventArgs e) =>
        WorkbenchColumnPopover.IsOpen = false;

    private static void ConfigureDiscogsGrid(AppDataGrid grid) =>
        grid.ConfigureColumns(
        [
            new("Title", "Release", "Title", 220, 130),
            new("Artist", "Artist credit", "Artist", 180, 105),
            new("Year", "Year", "Year", 75, 60),
            new("Country", "Country", "Country", 75, 62),
            new("Labels", "Labels", "Labels", 160, 95),
            new("Catalog", "Catalog no.", "CatalogNumbers", 130, 85),
            new("Formats", "Formats", "Formats", 150, 90),
            new("Genres", "Genres", "Genres", 130, 85),
            new("Styles", "Styles", "Styles", 140, 90),
            new("Tracks", "Tracks", "TrackCount", 70, 55),
            new("Source", "Source", "Source", 100, 75),
            new("ReleaseID", "Discogs release ID", "ReleaseId", 150, 100),
        ]);

    private static void ConfigureDiscogsTrackMappingGrid(
        AppDataGrid grid)
    {
        var includeTemplate =
            new FuncDataTemplate<DiscogsTrackMappingRow>(
                (_, _) =>
                {
                    var check = new CheckBox();
                    check.Bind(
                        CheckBox.IsCheckedProperty,
                        new Binding(
                            nameof(DiscogsTrackMappingRow.IsIncluded))
                        {
                            Mode = BindingMode.TwoWay,
                        });
                    return check;
                });
        var trackTemplate =
            new FuncDataTemplate<DiscogsTrackMappingRow>(
                (_, _) =>
                {
                    var combo = new ComboBox
                    {
                        DisplayMemberBinding = new Binding(
                            nameof(DiscogsTrackChoice.Display)),
                    };
                    combo.Bind(
                        ItemsControl.ItemsSourceProperty,
                        new Binding(
                            nameof(DiscogsTrackMappingRow.TrackChoices)));
                    combo.Bind(
                        ComboBox.SelectedItemProperty,
                        new Binding(
                            nameof(DiscogsTrackMappingRow.SelectedTrack))
                        {
                            Mode = BindingMode.TwoWay,
                        });
                    return combo;
                });
        grid.ConfigureColumns(
        [
            new("Include", "Use", null, 58, 48,
                CellTemplate: includeTemplate, Sortable: false),
            new("File", "File", "File", 180, 110),
            new("Track", "Discogs track", null, 330, 190,
                CellTemplate: trackTemplate, Sortable: false),
            new("Position", "Position", "Position", 80, 62),
            new("Confidence", "Confidence", "Confidence", 100, 76),
            new("Status", "Reason", "Status", 260, 150),
        ]);
    }

    private static void ConfigureReleaseArtworkGrid(AppDataGrid grid)
    {
        var thumbnailTemplate = new FuncDataTemplate<CoverArtCandidateRow>(
            (_, _) =>
            {
                var image = new Image
                {
                    Width = 72,
                    Height = 72,
                    Stretch = Stretch.Uniform,
                };
                image.Bind(Image.SourceProperty,
                    new Binding(nameof(CoverArtCandidateRow.ThumbnailSource)));
                return image;
            });
        grid.RowHeight = 82;
        grid.ConfigureColumns(
        [
            new("Thumbnail", "Preview", null, 90, 80,
                CellTemplate: thumbnailTemplate, Sortable: false),
            new("Roles", "Types", "Roles", 170, 100),
            new("Front", "Front", "Front", 70, 55),
            new("Back", "Back", "Back", 70, 55),
            new("Approved", "Approved", "Approved", 85, 65),
            new("Comment", "Comment", "Comment", 240, 130),
            new("Status", "Thumbnail", "ThumbnailStatus", 150, 90),
            new("Id", "Archive ID", "Id", 150, 100),
        ]);
    }

    private static void ConfigureReleaseTrackMappingGrid(AppDataGrid grid)
    {
        var includeTemplate = new FuncDataTemplate<MusicBrainzTrackMappingRow>(
            (_, _) =>
            {
                var check = new CheckBox();
                check.Bind(CheckBox.IsCheckedProperty,
                    new Binding(nameof(MusicBrainzTrackMappingRow.IsIncluded))
                    {
                        Mode = BindingMode.TwoWay,
                    });
                return check;
            });
        var trackTemplate = new FuncDataTemplate<MusicBrainzTrackMappingRow>(
            (_, _) =>
            {
                var combo = new ComboBox
                {
                    DisplayMemberBinding =
                        new Binding(nameof(MusicBrainzTrackChoice.Display)),
                };
                combo.Bind(ItemsControl.ItemsSourceProperty,
                    new Binding(nameof(MusicBrainzTrackMappingRow.TrackChoices)));
                combo.Bind(ComboBox.SelectedItemProperty,
                    new Binding(nameof(MusicBrainzTrackMappingRow.SelectedTrack))
                    {
                        Mode = BindingMode.TwoWay,
                    });
                return combo;
            });
        grid.ConfigureColumns(
        [
            new("Include", "Use", null, 60, 50,
                CellTemplate: includeTemplate, Sortable: false),
            new("File", "File", "File", 210, 120),
            new("Track", "Release track", null, 390, 220,
                CellTemplate: trackTemplate, Sortable: false),
            new("Confidence", "Confidence", "Confidence", 110, 80),
            new("Status", "Reason", "Status", 310, 170),
        ]);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFiles()?.Any() == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        string[] paths = e.DataTransfer.TryGetFiles()?
            .Select(item => item.TryGetLocalPath())
            .Where(path => path is not null)
            .Cast<string>()
            .ToArray() ?? [];
        if (paths.Length > 0)
            await _viewModel.AddSourcesAsync(paths);
        e.Handled = true;
    }

    private async void OnWorkbenchKeyDown(
        object? sender,
        KeyEventArgs e)
    {
        object? focused = TopLevel.GetTopLevel(this)?
            .FocusManager?
            .GetFocusedElement();
        if (focused is TextBox or ComboBox or NumericUpDown)
            return;

        WorkbenchShortcutModifiers modifiers =
            WorkbenchShortcutModifiers.None;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            modifiers |= WorkbenchShortcutModifiers.Control;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            modifiers |= WorkbenchShortcutModifiers.Alt;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            modifiers |= WorkbenchShortcutModifiers.Shift;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            modifiers |= WorkbenchShortcutModifiers.Meta;
        if (modifiers == WorkbenchShortcutModifiers.None ||
            !_viewModel.ShortcutEditor.TryMatch(
                modifiers,
                e.Key.ToString(),
                out WorkbenchShortcutBinding? binding) ||
            binding is null)
            return;

        e.Handled = true;
        await _viewModel.ExecuteShortcutAsync(binding);
    }
}
