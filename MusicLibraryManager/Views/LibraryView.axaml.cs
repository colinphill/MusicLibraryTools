using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Controls.Presenters;
using global::Avalonia.Controls.Templates;
using global::Avalonia.Data;
using global::Avalonia.Interactivity;
using global::Avalonia.Input;
using global::Avalonia.Markup.Xaml;
using global::Avalonia.Media;
using global::Avalonia.Threading;
using System.ComponentModel;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Services;

namespace MusicLibraryManager.Views;

public partial class LibraryView : UserControl
{
    private readonly LibraryViewModel _viewModel;
    private readonly GridStateService _gridState;
    private readonly IPlatformService _platform;
    private readonly List<AppGridColumnDefinition> _columns = [];
    private IReadOnlyList<LibraryRow> _selected = [];
    private LibrarySortState? _sort;
    private bool _restoringSelection;
    private bool _selectionChangePending;
    private bool _responsiveCompact;
    private bool _drawerOpen;

    public LibraryView()
    {
        InitializeComponent();
        _viewModel = App.GetService<LibraryViewModel>();
        _gridState = App.GetService<GridStateService>();
        _platform = App.GetService<IPlatformService>();
        DataContext = _viewModel;
        BuildColumns();
        ApplySnapshot(_gridState.Load());
        ConfigureGrid();
        LibraryOperationPreviewGrid.ConfigureColumns(
        [
            new("File", "File", "File", 200, 130),
            new("Field", "Field", "Field", 140, 90),
            new("Before", "Before", "Before", 280, 160),
            new("After", "After", "After", 280, 160),
        ]);
        LibraryAudioDiscoveryGrid.ConfigureColumns(
        [
            new("File", "File", "File", 190, 120),
            new("Duration", "Duration", "Duration", 90, 70),
            new("Confidence", "Confidence", "Confidence", 105, 85),
            new("AcoustID", "AcoustID", "AcoustId", 270, 170),
            new("MusicBrainz", "MusicBrainz recording IDs",
                "MusicBrainzRecordingIds", 360, 210),
            new("Status", "Status", "Status", 240, 140),
        ]);
        LibraryReleaseDiscoveryGrid.ConfigureColumns(
        [
            new("Title", "Release", "Title", 220, 130),
            new("Artist", "Artist credit", "Artist", 180, 105),
            new("Date", "Date", "Date", 95, 72),
            new("Country", "Country", "Country", 75, 62),
            new("Label", "Label", "Label", 150, 90),
            new("Catalog", "Catalog no.", "CatalogNumber", 110, 80),
            new("Formats", "Formats", "Formats", 125, 85),
            new("Position", "Matched position", "MatchedTrackPositions", 130, 90),
            new("ReleaseID", "MusicBrainz release ID", "ReleaseId", 260, 170),
        ]);
        ConfigureDiscogsGrid(LibraryDiscogsDiscoveryGrid);
        ConfigureDiscogsTrackMappingGrid(
            LibraryDiscogsTrackMappingGrid);
        ConfigureReleaseTrackMappingGrid(LibraryReleaseTrackMappingGrid);
        ConfigureReleaseArtworkGrid(LibraryReleaseArtworkGrid);
        LibraryReportOutputGrid.ConfigureColumns(
        [
            new("Group", "Group", "Group", 140, 85),
            new("File", "Destination", "File", 380, 210),
            new("Rows", "Rows", "Rows", 75, 58),
            new("Bytes", "Bytes", "Bytes", 95, 68),
        ]);
        LibraryPlaylistOutputGrid.ConfigureColumns(
        [
            new("Group", "Group", "Group", 140, 85),
            new("File", "Destination", "File", 380, 210),
            new("Tracks", "Tracks", "Tracks", 75, 58),
            new("Bytes", "Bytes", "Bytes", 95, 68),
        ]);
        LibraryExternalToolInvocationGrid.ConfigureColumns(
        [
            new("Number", "#", "Number", 52, 44),
            new("Executable", "Executable", "Executable", 175, 110),
            new("Arguments", "Arguments", "Arguments", 330, 180),
            new("WorkingDirectory", "Working directory",
                "WorkingDirectory", 200, 120),
            new("Files", "Files", "Files", 62, 50),
        ]);
        LibraryGrid.ApplySort(_sort);
        BuildColumnOptions();
        LibraryGrid.LayoutChanged += (_, _) => PersistLayout();
        LibraryGrid.SortChanged += (_, _) => Dispatcher.UIThread.Post(CaptureSortAndPersist);
        InspectorView.CloseRequested += OnInspectorCloseRequested;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        if (_viewModel.Rows.Count == 0)
            _ = _viewModel.ReloadAsync();
    }

    private static void ConfigureDiscogsGrid(AppDataGrid grid) =>
        grid.ConfigureColumns(
        [
            new("Title", "Release", "Title", 210, 125),
            new("Artist", "Artist credit", "Artist", 170, 100),
            new("Year", "Year", "Year", 70, 58),
            new("Country", "Country", "Country", 72, 60),
            new("Labels", "Labels", "Labels", 150, 90),
            new("Catalog", "Catalog no.", "CatalogNumbers", 125, 82),
            new("Formats", "Formats", "Formats", 140, 88),
            new("Genres", "Genres", "Genres", 125, 82),
            new("Styles", "Styles", "Styles", 135, 88),
            new("Tracks", "Tracks", "TrackCount", 68, 54),
            new("Source", "Source", "Source", 95, 72),
            new("ReleaseID", "Discogs release ID", "ReleaseId", 145, 95),
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
            new("Track", "Discogs track", null, 320, 185,
                CellTemplate: trackTemplate, Sortable: false),
            new("Position", "Position", "Position", 78, 60),
            new("Confidence", "Confidence", "Confidence", 98, 74),
            new("Status", "Reason", "Status", 250, 145),
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
            new("Include", "Use", null, 58, 48,
                CellTemplate: includeTemplate, Sortable: false),
            new("File", "File", "File", 180, 110),
            new("Track", "Release track", null, 330, 190,
                CellTemplate: trackTemplate, Sortable: false),
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
                    Width = 64,
                    Height = 64,
                    Stretch = Stretch.Uniform,
                };
                image.Bind(Image.SourceProperty,
                    new Binding(nameof(CoverArtCandidateRow.ThumbnailSource)));
                return image;
            });
        grid.RowHeight = 74;
        grid.ConfigureColumns(
        [
            new("Thumbnail", "Preview", null, 82, 72,
                CellTemplate: thumbnailTemplate, Sortable: false),
            new("Roles", "Types", "Roles", 140, 90),
            new("Front", "Front", "Front", 65, 52),
            new("Back", "Back", "Back", 65, 52),
            new("Approved", "Approved", "Approved", 80, 62),
            new("Comment", "Comment", "Comment", 200, 120),
            new("Status", "Thumbnail", "ThumbnailStatus", 130, 82),
        ]);
    }

    private void BuildColumns()
    {
        var artworkTemplate = new FuncDataTemplate<LibraryRow>((_, _) =>
        {
            var image = new Image { Width = 28, Height = 28, Stretch = Stretch.UniformToFill };
            image.Bind(Image.SourceProperty, new Binding(nameof(LibraryRow.ThumbnailSource)));
            // LoadingRow can be raised before every initially visible row has completed its
            // first layout when launch-time search navigation replaces ItemsSource. The artwork
            // cell itself is the authoritative virtualization boundary, so also load when it is
            // attached. LoadThumbnailAsync is idempotent when LoadingRow already did the work.
            image.AttachedToVisualTree += (_, _) =>
            {
                if (image.DataContext is LibraryRow row)
                    _ = _viewModel.LoadThumbnailAsync(row);
            };
            return image;
        });
        _columns.AddRange([
            new("Artwork", "Artwork", null, 100, 100, CellTemplate: artworkTemplate, Sortable: false),
            new("Title", "Title", "Title", 280, 140),
            new("Artist", "Artist", "Artist", 190, 100),
            new("AlbumArtist", "Album artist", "AlbumArtist", 190, 100, false),
            new("Album", "Album", "Album", 230, 120),
            new("Genre", "Genre", "Genre", 150, 90, false),
            new("Composer", "Composer", "Composer", 190, 100, false),
            new("Grouping", "Grouping", "Grouping", 170, 100, false),
            new("Year", "Year", "Year", 70, 58, false),
            new("Track", "Track", "Track", 70, 58),
            new("TrackTotal", "Track total", "TrackTotal", 90, 72, false),
            new("Disc", "Disc", "Disc", 65, 58, false),
            new("DiscTotal", "Disc total", "DiscTotal", 85, 70, false),
            new("Codec", "Codec", "Codec", 105, 80),
            new("TagType", "Tag type", "TagType", 120, 85, false),
            new("CodecType", "Codec type", "CodecType", 105, 80, false),
            new("SampleRate", "Sample rate", "SampleRate", 115, 85, false),
            new("BitsPerSample", "Bits", "BitsPerSample", 70, 55, false),
            new("Bitrate", "Bitrate", "Bitrate", 100, 75, false),
            new("Channels", "Channels", "Channels", 85, 65, false),
            new("Duration", "Duration", "Duration", 90, 75),
            new("FileSize", "File size", "FileSize", 105, 75, false),
            new("Modified", "Modified", "Modified", 150, 110, false),
            new("Path", "Path", "Path", 420, 180),
        ]);
    }

    private void ApplySnapshot(GridSnapshot? snapshot)
    {
        if (snapshot is null)
            return;
        foreach (LibraryColumnState saved in snapshot.Columns.OrderBy(item => item.DisplayIndex))
        {
            int index = _columns.FindIndex(item => item.Key.Equals(saved.Key, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                continue;
            AppGridColumnDefinition current = _columns[index];
            _columns.RemoveAt(index);
            _columns.Insert(Math.Min(saved.DisplayIndex, _columns.Count), current with
            {
                Width = saved.Width is > 0 ? saved.Width.Value : current.Width,
                Visible = saved.Visible,
            });
        }
        _sort = snapshot.Sort;
    }

    private void ConfigureGrid()
    {
        LibraryGrid.ConfigureColumns(_columns);
        LibraryGrid.FrozenColumnCount = _columns.TakeWhile(item => !item.Visible).Any() ? 0 : 1;
        LibraryGrid.ApplySort(_sort);
    }

    private void BuildColumnOptions()
    {
        ColumnOptions.Children.Clear();
        foreach (AppGridColumnDefinition definition in _columns)
        {
            var check = new CheckBox { Content = definition.Header, IsChecked = definition.Visible, Tag = definition.Key };
            check.IsCheckedChanged += OnColumnChecked;
            ColumnOptions.Children.Add(check);
        }
    }

    private void OnColumnChecked(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string key, IsChecked: bool visible })
            return;
        int index = _columns.FindIndex(item => item.Key == key);
        if (index < 0)
            return;
        _columns[index] = _columns[index] with { Visible = visible };
        ConfigureGrid();
        PersistLayout();
    }

    private IReadOnlyList<LibraryColumnState> CaptureColumns()
    {
        var visible = LibraryGrid.CaptureColumnLayout();
        var visibleByKey = visible.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
        int displayIndex = 0;
        var result = new List<LibraryColumnState>(_columns.Count);
        foreach (AppGridColumnDefinition definition in _columns.OrderBy(item =>
                     visibleByKey.TryGetValue(item.Key, out LibraryColumnState? state) ? state.DisplayIndex : int.MaxValue))
        {
            if (visibleByKey.TryGetValue(definition.Key, out LibraryColumnState? state))
                result.Add(state with { DisplayIndex = displayIndex++, Visible = true });
            else
                result.Add(new LibraryColumnState(definition.Key, definition.Width, displayIndex++, false));
        }
        return result;
    }

    private void PersistLayout() => _gridState.Save(new GridSnapshot(CaptureColumns(), _sort));

    private void CaptureSortAndPersist()
    {
        _sort = LibraryGrid.CurrentSortKey is not { } key
            ? null
            : new LibrarySortState(key, LibraryGrid.CurrentSortDescending);
        PersistLayout();
    }

    private async void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_restoringSelection || _selectionChangePending)
            return;

        // Replacing ItemsSource clears DataGrid's object-based selection before the new rows are
        // realized. Preserve the path-based selection and restore it against the replacement rows.
        if (LibraryGrid.SelectedItems.Count == 0 &&
            e.RemovedItems.OfType<LibraryRow>().Any(row => !_viewModel.Rows.Contains(row)) &&
            _viewModel.SelectedPaths.Count > 0)
        {
            Dispatcher.UIThread.Post(RestoreVisibleSelection);
            return;
        }

        LibraryRow[] requested = LibraryGrid.SelectedItems.OfType<LibraryRow>().ToArray();
        _selectionChangePending = true;
        LibraryGrid.IsEnabled = false;
        try
        {
            if (!await _viewModel.SelectAsync(requested))
            {
                RestoreVisibleSelection();
                return;
            }
            _selected = requested;
            UpdateSelectionActions();
        }
        finally
        {
            _selectionChangePending = false;
            LibraryGrid.IsEnabled = true;
        }
    }

    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is LibraryRow row)
            _ = _viewModel.LoadThumbnailAsync(row);
    }

    private void OnUnloadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is LibraryRow row)
            _viewModel.ReleaseThumbnail(row);
    }

    private void OnColumnsClick(object? sender, RoutedEventArgs e) => ColumnPopover.IsOpen = !ColumnPopover.IsOpen;

    private void OnColumnsClose(object? sender, RoutedEventArgs e) => ColumnPopover.IsOpen = false;

    private void OnLibraryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        if (ColumnPopover.IsOpen)
        {
            ColumnPopover.IsOpen = false;
            e.Handled = true;
        }
        else if (ViewsPopover.IsOpen)
        {
            ViewsPopover.IsOpen = false;
            e.Handled = true;
        }
        else if (FilterHelpPopover.IsOpen)
        {
            FilterHelpPopover.IsOpen = false;
            e.Handled = true;
        }
    }

    private void OnInspectorToggle(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsInspectorOpen)
        {
            _viewModel.IsInspectorOpen = true;
            _drawerOpen = true;
        }
        else if (_responsiveCompact)
        {
            _drawerOpen = !_drawerOpen;
        }
        ApplyInspectorVisibility();
    }

    public void ApplyResponsiveLayout(bool compact)
    {
        _responsiveCompact = compact;
        if (!compact)
            _drawerOpen = false;
        ApplyInspectorVisibility();
    }

    private void OnSavedViewChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel.SelectedView is { } view)
        {
            ApplySnapshot(new GridSnapshot(view.Columns, view.Sort));
            ConfigureGrid();
            LibraryGrid.ApplySort(view.Sort);
            _sort = view.Sort;
            BuildColumnOptions();
            ViewsPopover.IsOpen = false;
        }
    }

    private void OnSaveView(object? sender, RoutedEventArgs e)
    {
        string name = _viewModel.NewViewName?.Trim() ?? "";
        if (name.Length > 0)
            _viewModel.SaveNamedView(name, CaptureColumns(), _sort);
    }

    private async void OnCopyPaths(object? sender, RoutedEventArgs e) =>
        await _platform.CopyTextAsync(string.Join(Environment.NewLine, _selected.Select(item => item.Path)));

    private void OnReveal(object? sender, RoutedEventArgs e)
    {
        if (_selected.FirstOrDefault() is { } row)
            _platform.RevealFile(row.Path);
    }

    private async void OnReindex(object? sender, RoutedEventArgs e) =>
        await _viewModel.ReindexAsync(_selected.Select(item => item.Path).ToArray());

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Dispatcher.UIThread.Post(() =>
        {
            RestoreVisibleSelection();
            ApplyInspectorVisibility();
        });
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e) =>
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LibraryViewModel.Rows) or nameof(LibraryViewModel.SelectedPaths))
            Dispatcher.UIThread.Post(RestoreVisibleSelection);
        else if (e.PropertyName is nameof(LibraryViewModel.IsInspectorOpen) or
                 nameof(LibraryViewModel.HasUnsavedSelectionChanges))
            Dispatcher.UIThread.Post(ApplyInspectorVisibility);
    }

    private void RestoreVisibleSelection()
    {
        LibraryRow[] rows = _viewModel.GetVisibleSelectedRows().ToArray();
        _restoringSelection = true;
        try
        {
            LibraryGrid.SelectedItems.Clear();
            foreach (LibraryRow row in rows)
                LibraryGrid.SelectedItems.Add(row);
        }
        finally
        {
            _restoringSelection = false;
        }
        _selected = rows;
        UpdateSelectionActions();
    }

    private void UpdateSelectionActions()
    {
        bool hasSelection = _selected.Count > 0;
        SelectedCountLabel.IsVisible = CopyButton.IsVisible = RevealButton.IsVisible =
            ReindexButton.IsVisible = hasSelection;
        SelectedCountLabel.Text = $"{_selected.Count:N0} selected";
    }

    private void OnInspectorCloseRequested(object? sender, EventArgs e)
    {
        _drawerOpen = false;
        _viewModel.IsInspectorOpen = false;
        ApplyInspectorVisibility();
    }

    private void ApplyInspectorVisibility()
    {
        bool inspectorOpen = _viewModel.IsInspectorOpen;
        bool useCompactPresentation = _responsiveCompact || !inspectorOpen;
        WorkspaceSplit.SetCompact(useCompactPresentation);
        ContentPresenter? presenter = WorkspaceSplit.FindControl<ContentPresenter>("RightPresenter");
        if (presenter is not null)
        {
            presenter.Width = _responsiveCompact ? 320 : double.NaN;
            presenter.IsVisible = inspectorOpen && (!_responsiveCompact || _drawerOpen);
        }
        bool drawerVisible = inspectorOpen && _responsiveCompact && _drawerOpen;
        InspectorScrim.IsVisible = drawerVisible;
        InspectorToggle.IsVisible = _responsiveCompact || !inspectorOpen;
        InspectorToggle.Content = drawerVisible ? "Hide inspector" :
            _viewModel.HasUnsavedSelectionChanges ? "Inspector (unsaved)" : "Inspector";
    }

    private void OnInspectorScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        _drawerOpen = false;
        ApplyInspectorVisibility();
        e.Handled = true;
    }

    private void OnViewsClick(object? sender, RoutedEventArgs e) =>
        ViewsPopover.IsOpen = !ViewsPopover.IsOpen;

    private void OnViewsClose(object? sender, RoutedEventArgs e) => ViewsPopover.IsOpen = false;

    private void OnFilterHelpClick(object? sender, RoutedEventArgs e) =>
        FilterHelpPopover.IsOpen = !FilterHelpPopover.IsOpen;
}
