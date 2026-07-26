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
using global::Avalonia.VisualTree;
using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Services;

namespace MusicLibraryManager.Views;

public partial class LibraryView : UserControl
{
    private static readonly WorkbenchSection[] HandoffSections =
    [
        WorkbenchSection.Session,
        WorkbenchSection.BulkOperation,
        WorkbenchSection.AllFields,
        WorkbenchSection.Files,
        WorkbenchSection.OnlineMetadata,
        WorkbenchSection.Reports,
        WorkbenchSection.Playlists,
        WorkbenchSection.Tools,
    ];

    private readonly LibraryViewModel _viewModel;
    private readonly GridStateService _gridState;
    private readonly ILocalizationService _localization;
    private readonly List<AppGridColumnDefinition> _columns = [];
    private IReadOnlyList<LibraryRow> _selected = [];
    private LibraryActionScopeSnapshot? _selectionActions;
    private LibrarySortState? _sort;
    private bool _restoringSelection;
    private bool _selectionChangePending;
    private bool _shellRequestedCompact;
    private bool _responsiveCompact;
    private bool _drawerOpen;
    private readonly OverlayInteractionController
        _pendingChangesOverlay = new();
    private readonly OverlayInteractionController
        _inspectorOverlay = new();

    public LibraryView()
    {
        InitializeComponent();
        InspectorScrim.SetValue(
            Panel.ZIndexProperty,
            10);
        _viewModel = App.GetService<LibraryViewModel>();
        _gridState = App.GetService<GridStateService>();
        _localization = App.GetService<ILocalizationService>();
        DataContext = _viewModel;
        BuildColumns();
        ApplySnapshot(_gridState.Load());
        ConfigureGrid();
        LibraryGrid.ApplySort(_sort);
        BuildColumnOptions();
        LibraryGrid.LayoutChanged += (_, _) => PersistLayout();
        LibraryGrid.SortChanged += (_, _) => Dispatcher.UIThread.Post(CaptureSortAndPersist);
        InspectorView.CloseRequested += OnInspectorCloseRequested;
        InspectorView.ReviewChangesRequested +=
            OnInspectorReviewChangesRequested;
        SizeChanged += (_, _) =>
        {
            ApplyOverlayBounds();
            RecalculateResponsiveLayout();
        };
        WorkspaceSplit.SizeChanged += (_, _) =>
            RecalculateResponsiveLayout();
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        if (_viewModel.Rows.Count == 0)
            _ = _viewModel.ReloadAsync();
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
            new("Artwork", "Artwork", null, 100, 100,
                CellTemplate: artworkTemplate, Sortable: false,
                HeaderResourceKey: "Column.Artwork"),
            new("Title", "Title", "Title", 280, 140,
                Editable: true, HeaderResourceKey: "Column.Title"),
            new("Artist", "Artist", "Artist", 190, 100,
                Editable: true, HeaderResourceKey: "Column.Artist"),
            new("AlbumArtist", "Album artist", "AlbumArtist", 190, 100, false,
                Editable: true, HeaderResourceKey: "Column.AlbumArtist"),
            new("Album", "Album", "Album", 230, 120,
                Editable: true, HeaderResourceKey: "Column.Album"),
            new("Genre", "Genre", "Genre", 150, 90, false,
                Editable: true, HeaderResourceKey: "Column.Genre"),
            new("Composer", "Composer", "Composer", 190, 100, false,
                Editable: true, HeaderResourceKey: "Column.Composer"),
            new("Grouping", "Grouping", "Grouping", 170, 100, false,
                Editable: true, HeaderResourceKey: "Column.Grouping"),
            new("Year", "Year", "Year", 70, 58, false,
                Editable: true, HeaderResourceKey: "Column.Year"),
            new("Track", "Track", "TrackEditValue", 70, 58,
                Editable: true,
                CustomSortComparer:
                    new LibraryNumericColumnComparer(
                        row => row.TrackEditValue),
                HeaderResourceKey: "Column.Track"),
            new("TrackTotal", "Track total", "TrackTotalEditValue", 90, 72, false,
                Editable: true,
                CustomSortComparer:
                    new LibraryNumericColumnComparer(
                        row => row.TrackTotalEditValue),
                HeaderResourceKey: "Column.TrackTotal"),
            new("Disc", "Disc", "DiscEditValue", 65, 58, false,
                Editable: true,
                CustomSortComparer:
                    new LibraryNumericColumnComparer(
                        row => row.DiscEditValue),
                HeaderResourceKey: "Column.Disc"),
            new("DiscTotal", "Disc total", "DiscTotalEditValue", 85, 70, false,
                Editable: true,
                CustomSortComparer:
                    new LibraryNumericColumnComparer(
                        row => row.DiscTotalEditValue),
                HeaderResourceKey: "Column.DiscTotal"),
            new("Codec", "Codec", "Codec", 105, 80,
                HeaderResourceKey: "Column.Codec"),
            new("TagType", "Tag type", "TagType", 120, 85, false,
                HeaderResourceKey: "Column.TagType"),
            new("CodecType", "Codec type", "CodecType", 105, 80, false,
                HeaderResourceKey: "Column.CodecType"),
            new("SampleRate", "Sample rate", "SampleRate", 115, 85, false,
                HeaderResourceKey: "Column.SampleRate"),
            new("BitsPerSample", "Bits", "BitsPerSample", 70, 55, false,
                HeaderResourceKey: "Column.Bits"),
            new("Bitrate", "Bitrate", "Bitrate", 100, 75, false,
                HeaderResourceKey: "Column.Bitrate"),
            new("Channels", "Channels", "Channels", 85, 65, false,
                HeaderResourceKey: "Column.Channels"),
            new("Duration", "Duration", "Duration", 90, 75,
                HeaderResourceKey: "Column.Duration"),
            new("FileSize", "File size", "FileSize", 105, 75, false,
                HeaderResourceKey: "Column.FileSize"),
            new("Modified", "Modified", "Modified", 150, 110, false,
                HeaderResourceKey: "Column.Modified"),
            new("Path", "Path", "Path", 420, 180,
                HeaderResourceKey: "Column.Path"),
        ]);
        AddMetadataColumns();
    }

    private void AddMetadataColumns()
    {
        foreach (UserMetadataColumnRow row in
                 _viewModel.ColumnEditor.Columns.OrderBy(column =>
                     column.Descriptor.Order))
        {
            UserMetadataColumnDescriptor descriptor =
                row.Descriptor;
            string? editPath =
                descriptor.EditTarget is { } target
                    ? LibraryEditPath(target)
                    : null;
            _columns.Insert(
                Math.Clamp(
                    descriptor.Order,
                    0,
                    _columns.Count),
                new(
                    descriptor.ColumnKey,
                    descriptor.Label,
                    editPath ??
                        $"MetadataValues[{descriptor.ValueKey}]",
                    descriptor.Width,
                    70,
                    descriptor.Visible,
                    Editable: editPath is not null,
                    CustomSortComparer:
                        editPath is null ||
                        editPath.StartsWith(
                            "MetadataValues[",
                            StringComparison.Ordinal)
                            ? new MetadataGridRowComparer(
                                descriptor.ValueKey,
                                descriptor.SortType)
                            : new LibraryMetadataColumnComparer(
                                library =>
                                    LibraryEditValue(
                                        library,
                                        descriptor.EditTarget!),
                                descriptor.SortType)));
        }
    }

    private static string? LibraryEditValue(
        LibraryRow row,
        MetadataFieldKey field) =>
        field.KnownField switch
        {
            TagFields.Title => row.Title,
            TagFields.Artist => row.Artist,
            TagFields.AlbumArtist =>
                row.AlbumArtist,
            TagFields.Album => row.Album,
            TagFields.Genre => row.Genre,
            TagFields.Composer => row.Composer,
            TagFields.Grouping => row.Grouping,
            TagFields.Date => row.Year,
            TagFields.TrackNumber =>
                row.TrackEditValue,
            TagFields.TotalTracks =>
                row.TrackTotalEditValue,
            TagFields.DiscNumber =>
                row.DiscEditValue,
            TagFields.TotalDiscs =>
                row.DiscTotalEditValue,
            TagFields.Comment => row.Comment,
            _ => row.MetadataValues.GetValueOrDefault(
                MetadataGridValueKey.For(field)),
        };

    private static string LibraryEditPath(
        MetadataFieldKey field) =>
        field.KnownField switch
        {
            TagFields.Title =>
                nameof(LibraryRow.Title),
            TagFields.Artist =>
                nameof(LibraryRow.Artist),
            TagFields.AlbumArtist =>
                nameof(LibraryRow.AlbumArtist),
            TagFields.Album =>
                nameof(LibraryRow.Album),
            TagFields.Genre =>
                nameof(LibraryRow.Genre),
            TagFields.Composer =>
                nameof(LibraryRow.Composer),
            TagFields.Grouping =>
                nameof(LibraryRow.Grouping),
            TagFields.Date =>
                nameof(LibraryRow.Year),
            TagFields.TrackNumber =>
                nameof(LibraryRow.TrackEditValue),
            TagFields.TotalTracks =>
                nameof(LibraryRow.TrackTotalEditValue),
            TagFields.DiscNumber =>
                nameof(LibraryRow.DiscEditValue),
            TagFields.TotalDiscs =>
                nameof(LibraryRow.DiscTotalEditValue),
            TagFields.Comment =>
                nameof(LibraryRow.Comment),
            _ =>
                $"MetadataValues[" +
                $"{MetadataGridValueKey.For(field)}]",
        };

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
            _viewModel.ColumnEditor.Columns.Select(row =>
                new LibraryColumnState(
                    row.Descriptor.ColumnKey,
                    row.Descriptor.Width,
                    row.Descriptor.Order,
                    row.Descriptor.Visible)));
        GridSnapshot snapshot = new(
            columns,
            _sort);
        _columns.RemoveAll(column =>
            column.Key.StartsWith(
                "Metadata.",
                StringComparison.Ordinal));
        AddMetadataColumns();
        ApplySnapshot(snapshot);
        ConfigureGrid();
        BuildColumnOptions();
        PersistLayout();
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

    private IReadOnlyList<LibraryColumnState> CaptureColumns() =>
        PersistedGridLayout.CaptureSnapshotColumns(
            _columns,
            LibraryGrid.CaptureColumnLayout());

    private void PersistLayout()
    {
        IReadOnlyList<LibraryColumnState> columns =
            CaptureColumns();
        _gridState.Save(new GridSnapshot(columns, _sort));
        _viewModel.ColumnEditor.PersistLayout(columns);
    }

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
            ApplyInspectorVisibility();
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

    private void OnColumnsClick(object? sender, RoutedEventArgs e)
    {
        bool open = !ColumnPopover.IsOpen;
        CloseTransientPopups();
        ApplyOverlayBounds();
        ColumnPopover.IsOpen = open;
        if (open)
            Dispatcher.UIThread.Post(() => CloseColumnsButton.Focus());
    }

    private void OnColumnsClose(object? sender, RoutedEventArgs e)
    {
        ColumnPopover.IsOpen = false;
        ColumnsButton.Focus();
    }

    private void OnLibraryKeyDown(object? sender, KeyEventArgs e)
    {
        bool requestsContextMenu =
            e.Key == Key.Apps ||
            e.Key == Key.F10 &&
            e.KeyModifiers.HasFlag(
                KeyModifiers.Shift);
        if (requestsContextMenu)
        {
            Control? focused =
                TopLevel.GetTopLevel(this)?
                    .FocusManager?
                    .GetFocusedElement() as Control;
            if (TryOpenLibraryGridActionMenu(
                    focused))
            {
                e.Handled = true;
                return;
            }
        }

        if (LibraryPendingChangesPopover.IsOpen &&
            _pendingChangesOverlay.HandleKeyDown(
                e,
                LibraryPendingChangesSurface,
                canDismiss: true,
                () => ClosePendingChanges(
                    restoreFocus: true)))
        {
            return;
        }

        if (_responsiveCompact &&
            _drawerOpen &&
            InspectorScrim.IsVisible &&
            _inspectorOverlay.HandleKeyDown(
                e,
                InspectorView,
                canDismiss: true,
                () => CloseCompactInspector(
                    closePreference: false,
                    restoreFocus: true)))
        {
            return;
        }

        if (e.Key != Key.Escape)
            return;
        if (ColumnPopover.IsOpen)
        {
            ColumnPopover.IsOpen = false;
            ColumnsButton.Focus();
            e.Handled = true;
        }
        else if (ViewsPopover.IsOpen)
        {
            ViewsPopover.IsOpen = false;
            ViewsButton.Focus();
            e.Handled = true;
        }
        else if (FilterHelpPopover.IsOpen)
        {
            FilterHelpPopover.IsOpen = false;
            FilterHelpButton.Focus();
            e.Handled = true;
        }
        else if (VisualFilterPopover.IsOpen)
        {
            VisualFilterPopover.IsOpen = false;
            VisualFilterButton.Focus();
            e.Handled = true;
        }
    }

    private void OnLibraryPendingChangesClick(
        object? sender,
        RoutedEventArgs e)
    {
        if (LibraryPendingChangesPopover.IsOpen)
        {
            ClosePendingChanges(restoreFocus: true);
            return;
        }

        OpenPendingChanges();
    }

    private void OnInspectorReviewChangesRequested(
        object? sender,
        EventArgs e) =>
        OpenPendingChanges();

    private void OpenPendingChanges()
    {
        _pendingChangesOverlay
            .CaptureFocus(this);
        CloseTransientPopups();
        ApplyOverlayBounds();
        LibraryPendingChangesPopover.IsOpen = true;
        Dispatcher.UIThread.Post(() =>
            CloseLibraryPendingChangesButton.Focus());
    }

    private void OnLibraryPendingChangesClose(
        object? sender,
        RoutedEventArgs e) =>
        ClosePendingChanges(restoreFocus: true);

    private void ClosePendingChanges(
        bool restoreFocus)
    {
        LibraryPendingChangesPopover.IsOpen = false;
        if (restoreFocus)
        {
            _pendingChangesOverlay
                .RestoreFocus(
                    LibraryPendingChangesButton);
        }
        else
        {
            _pendingChangesOverlay
                .ClearFocusReturn();
        }
    }

    private void OnVisualFilterClick(
        object? sender,
        RoutedEventArgs e)
    {
        bool open = !VisualFilterPopover.IsOpen;
        CloseTransientPopups();
        ApplyOverlayBounds();
        VisualFilterPopover.IsOpen = open;
        if (open)
        {
            Dispatcher.UIThread.Post(
                () => CloseVisualFilterButton.Focus());
        }
    }

    private void OnVisualFilterClose(
        object? sender,
        RoutedEventArgs e)
    {
        VisualFilterPopover.IsOpen = false;
        VisualFilterButton.Focus();
    }

    private void OnEditInWorkbenchClick(
        object? sender,
        RoutedEventArgs e)
    {
        if (sender is not Control target)
            return;
        ContextMenu menu = CreateLibraryActionMenu(
            includeFileActions: false);
        menu.Open(target);
    }

    private void OnLibraryGridPointerPressed(
        object? sender,
        PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(LibraryGrid)
                .Properties.PointerUpdateKind !=
            PointerUpdateKind.RightButtonPressed)
            return;
        DataGridRow? row = (e.Source as Control)?
            .GetVisualAncestors()
            .OfType<DataGridRow>()
            .FirstOrDefault();
        if (row?.DataContext is not LibraryRow item ||
            LibraryGrid.SelectedItems.Contains(item))
            return;
        _restoringSelection = true;
        try
        {
            LibraryGrid.SelectedItems.Clear();
            LibraryGrid.SelectedItems.Add(item);
            LibraryGrid.SelectedItem = item;
        }
        finally
        {
            _restoringSelection = false;
        }
        _selected = [item];
        UpdateSelectionActions();
        _ = _viewModel.SelectAsync([item]);
    }

    private void OnLibraryGridContextRequested(
        object? sender,
        ContextRequestedEventArgs e)
    {
        Control? target = e.Source as Control ??
            sender as Control;
        if (TryOpenLibraryGridActionMenu(
                target))
            e.Handled = true;
    }

    private bool TryOpenLibraryGridActionMenu(
        Control? target)
    {
        bool targetIsInGrid =
            target is not null &&
            (ReferenceEquals(
                 target,
                 LibraryGrid) ||
             target.GetVisualAncestors()
                 .Contains(LibraryGrid));
        if (!targetIsInGrid)
            return false;

        if (LibraryGridActionMenu.IsOpen)
            LibraryGridActionMenu.Close();
        LibraryGridActionMenu.ItemsSource =
            CreateLibraryActionMenuItems(
                includeFileActions: true);
        LibraryGridActionMenu.Open(target!);
        return true;
    }

    private ContextMenu CreateLibraryActionMenu(
        bool includeFileActions) =>
        new()
        {
            ItemsSource =
                CreateLibraryActionMenuItems(
                    includeFileActions),
        };

    private IReadOnlyList<object>
        CreateLibraryActionMenuItems(
            bool includeFileActions)
    {
        LibraryActionScopeSnapshot selection =
            CaptureSelectionActions();
        var items = new List<object>
        {
            CreateHandoffScopeMenu(
                "Library.Handoff.Scope.Selected",
                selection),
            CreateHandoffScopeMenu(
                "Library.Handoff.Scope.Visible",
                _viewModel.CaptureActionScope(
                    WorkbenchHandoffScopeKind
                        .VisibleResults)),
            CreateHandoffScopeMenu(
                "Library.Handoff.Scope.All",
                _viewModel.CaptureActionScope(
                    WorkbenchHandoffScopeKind
                        .AllResults)),
        };

        if (includeFileActions && selection.HasPaths)
        {
            items.Add(new Separator());
            var copy = new MenuItem
            {
                Header = _localization.Get(
                    "Library.Action.CopyPaths"),
                IsEnabled = selection.CanCopyPaths,
            };
            copy.Click += async (_, _) =>
                await _viewModel.CopyPathsAsync(
                    selection);
            items.Add(copy);

            var reveal = new MenuItem
            {
                Header = _localization.Get(
                    "Library.Action.Reveal"),
                IsEnabled = selection.CanReveal,
            };
            reveal.Click += (_, _) =>
                _viewModel.RevealPath(
                    selection);
            items.Add(reveal);

            var refresh = new MenuItem
            {
                Header = _localization.Get(
                    "Library.Action.RefreshAffectedPaths"),
                IsEnabled =
                    selection
                        .CanRefreshAffectedPaths,
            };
            refresh.Click += async (_, _) =>
                await _viewModel
                    .RefreshAffectedPathsAsync(
                        selection);
            items.Add(refresh);
        }

        return items;
    }

    private MenuItem CreateHandoffScopeMenu(
        string headerKey,
        LibraryActionScopeSnapshot snapshot)
    {
        var sections = new List<MenuItem>();
        foreach (WorkbenchSection section in HandoffSections)
        {
            var item = new MenuItem
            {
                Header = _localization.Get(
                    $"Workbench.Navigation.Section.{section}"),
                IsEnabled = snapshot.CanHandoff,
            };
            item.Click += async (_, _) =>
                await _viewModel.HandoffToWorkbenchAsync(
                    section,
                    snapshot);
            sections.Add(item);
        }
        return new MenuItem
        {
            Header = _localization.Get(headerKey),
            IsEnabled = snapshot.CanHandoff,
            ItemsSource = sections,
        };
    }

    private void OnInspectorToggle(object? sender, RoutedEventArgs e)
    {
        bool wasDrawerVisible =
            _responsiveCompact &&
            _drawerOpen;
        if (wasDrawerVisible)
        {
            CloseCompactInspector(
                closePreference: false,
                restoreFocus: true);
            return;
        }

        if (_responsiveCompact)
        {
            _inspectorOverlay
                .CaptureFocus(this);
        }
        if (_viewModel.InspectorPreference !=
            LibraryInspectorPreference.Pinned)
        {
            _viewModel.SetInspectorPreference(
                LibraryInspectorPreference.Pinned);
            _drawerOpen = _responsiveCompact;
        }
        else if (_responsiveCompact)
        {
            _drawerOpen = !_drawerOpen;
        }
        ApplyInspectorVisibility();
        bool drawerVisible =
            _responsiveCompact &&
            _drawerOpen;
        if (drawerVisible)
        {
            Dispatcher.UIThread.Post(
                () =>
                    InspectorView.CloseButton
                        .Focus());
        }
    }

    public void ApplyResponsiveLayout(bool compact)
    {
        _shellRequestedCompact = compact;
        RecalculateResponsiveLayout();
    }

    private void RecalculateResponsiveLayout()
    {
        const double minimumCentralTaskWidth = 760;
        const double minimumInspectorWidth = 320;
        const double splitDividerWidth = 10;
        double requiredWorkspaceWidth =
            minimumCentralTaskWidth +
            splitDividerWidth +
            minimumInspectorWidth;
        double workspaceWidth =
            ResolveWorkspaceWidth();
        bool compact = workspaceWidth > 0
            ? workspaceWidth <
              requiredWorkspaceWidth
            : _shellRequestedCompact;
        if (_responsiveCompact == compact)
        {
            ApplyInspectorVisibility();
            return;
        }
        _responsiveCompact = compact;
        if (!compact)
        {
            _drawerOpen = false;
            _inspectorOverlay
                .ClearFocusReturn();
        }
        ApplyInspectorVisibility();
    }

    private double ResolveWorkspaceWidth()
    {
        // WorkspaceSplit can still report its previous width while the parent
        // SizeChanged event is being delivered. Derive the pending workspace
        // allocation from the current content-host bounds so growing through a
        // breakpoint never selects the narrower presentation transiently.
        if (Bounds.Width > 0)
        {
            bool compactHeight =
                Bounds.Height > 0 &&
                Bounds.Height <=
                AdaptivePage.CompactHeightThreshold;
            double gutter = compactHeight
                ? AdaptivePage.CompactHeightGutter
                : Bounds.Width <
                  AdaptivePage.NarrowContentThreshold
                    ? AdaptivePage.NarrowGutter
                    : AdaptivePage.WideGutter;
            return Math.Max(
                0,
                Bounds.Width - gutter * 2);
        }

        return WorkspaceSplit.Bounds.Width;
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

    private async void OnCopyPaths(
        object? sender,
        RoutedEventArgs e)
    {
        if (_selectionActions is { } selection)
            await _viewModel.CopyPathsAsync(selection);
    }

    private void OnReveal(
        object? sender,
        RoutedEventArgs e)
    {
        if (_selectionActions is { } selection)
            _viewModel.RevealPath(selection);
    }

    private async void OnReindex(
        object? sender,
        RoutedEventArgs e)
    {
        if (_selectionActions is { } selection)
            await _viewModel.RefreshAffectedPathsAsync(
                selection);
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.ColumnEditor.Changed +=
            RebuildMetadataColumns;
        _localization.CultureChanged += OnLocalizationCultureChanged;
        Dispatcher.UIThread.Post(() =>
        {
            RestoreVisibleSelection();
            ApplyInspectorVisibility();
        });
    }

    private void OnDetachedFromVisualTree(
        object? sender,
        VisualTreeAttachmentEventArgs e)
    {
        DetachViewModelEvents();
        _localization.CultureChanged -= OnLocalizationCultureChanged;
    }

    private void DetachViewModelEvents()
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.ColumnEditor.Changed -=
            RebuildMetadataColumns;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LibraryViewModel.Rows) or nameof(LibraryViewModel.SelectedPaths))
            Dispatcher.UIThread.Post(RestoreVisibleSelection);
        else if (e.PropertyName ==
                 nameof(LibraryViewModel.IsBusy))
            Dispatcher.UIThread.Post(
                UpdateSelectionActions);
        else if (e.PropertyName is nameof(LibraryViewModel.IsInspectorOpen) or
                  nameof(LibraryViewModel.InspectorPreference) or
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
        ApplyInspectorVisibility();
    }

    private void UpdateSelectionActions()
    {
        LibraryActionScopeSnapshot selection =
            CaptureSelectionActions();
        _selectionActions = selection;
        bool hasSelection = selection.HasPaths;
        SelectedCountLabel.IsVisible =
            SelectionWorkbenchButton.IsVisible =
            CopyButton.IsVisible =
            RevealButton.IsVisible =
            ReindexButton.IsVisible =
            hasSelection;
        SelectionWorkbenchButton.IsEnabled =
            selection.CanHandoff;
        CopyButton.IsEnabled =
            selection.CanCopyPaths;
        RevealButton.IsEnabled =
            selection.CanReveal;
        ReindexButton.IsEnabled =
            selection.CanRefreshAffectedPaths;
        SelectedCountLabel.Text = _localization.Format(
            "Library.Selection.CountFormat",
            selection.CapturedPaths.Length);
    }

    private LibraryActionScopeSnapshot
        CaptureSelectionActions() =>
        _viewModel.CaptureActionScope(
            WorkbenchHandoffScopeKind.Selected,
            _selected.Select(row => row.Path));

    private void OnInspectorCloseRequested(object? sender, EventArgs e)
    {
        CloseCompactInspector(
            closePreference: true,
            restoreFocus: true);
    }

    private void CloseCompactInspector(
        bool closePreference,
        bool restoreFocus)
    {
        _drawerOpen = false;
        if (closePreference)
        {
            _viewModel.SetInspectorPreference(
                LibraryInspectorPreference.Closed);
        }
        ApplyInspectorVisibility();
        if (restoreFocus)
        {
            _inspectorOverlay
                .RestoreFocus(
                    InspectorToggle);
        }
        else
        {
            _inspectorOverlay
                .ClearFocusReturn();
        }
    }

    private void ApplyInspectorVisibility()
    {
        bool hasSelection =
            _selected.Count > 0 ||
            _viewModel.HasUnsavedSelectionChanges;
        bool inspectorOpen =
            _viewModel.InspectorPreference switch
            {
                LibraryInspectorPreference.Pinned => true,
                LibraryInspectorPreference.Closed => false,
                _ => hasSelection && !_responsiveCompact,
            };
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
        InspectorToggle.Content = _localization.Get(
            drawerVisible
                ? "Library.Action.HideInspector"
                : _viewModel.HasUnsavedSelectionChanges
                    ? "Library.Action.InspectorUnsaved"
                    : "Library.Action.Inspector");
    }

    private void OnLocalizationCultureChanged(
        object? sender,
        EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            UpdateSelectionActions();
            ApplyInspectorVisibility();
        });

    private void OnInspectorScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        _inspectorOverlay
            .HandleScrimPressed(
                e,
                InspectorScrim,
                canDismiss: true,
                () => CloseCompactInspector(
                    closePreference: false,
                    restoreFocus: true));
    }

    private void OnViewsClick(object? sender, RoutedEventArgs e)
    {
        bool open = !ViewsPopover.IsOpen;
        CloseTransientPopups();
        ViewsPopover.IsOpen = open;
    }

    private void OnViewsClose(object? sender, RoutedEventArgs e)
    {
        ViewsPopover.IsOpen = false;
        ViewsButton.Focus();
    }

    private void OnFilterHelpClick(object? sender, RoutedEventArgs e)
    {
        bool open = !FilterHelpPopover.IsOpen;
        CloseTransientPopups();
        FilterHelpPopover.IsOpen = open;
    }

    private void CloseTransientPopups()
    {
        ColumnPopover.IsOpen = false;
        LibraryPendingChangesPopover.IsOpen = false;
        ViewsPopover.IsOpen = false;
        FilterHelpPopover.IsOpen = false;
        VisualFilterPopover.IsOpen = false;
    }

    private void ApplyOverlayBounds()
    {
        double availableWidth = Math.Max(
            320,
            Bounds.Width - 24);
        double availableHeight = Math.Max(320, Bounds.Height - 32);
        LibraryPendingChangesSurface.Width =
            OverlayInteractionController
                .ConstrainLength(
                    Bounds.Width,
                    300,
                    430,
                    viewportInset: 24);
        LibraryPendingChangesSurface.Height = Math.Min(620, availableHeight);
        LibraryColumnsSurface.Width =
            Math.Min(650, availableWidth);
        LibraryColumnsSurface.MaxHeight =
            Math.Min(610, availableHeight);
        double visualFilterWidth =
            Math.Min(720, availableWidth);
        LibraryVisualFilterSurface.Width =
            visualFilterWidth;
        LibraryVisualFilterSurface.MaxHeight =
            Math.Min(620, availableHeight);
        ApplyVisualFilterLayout(
            visualFilterWidth < 600);
    }

    private void ApplyVisualFilterLayout(
        bool compact)
    {
        LibraryVisualFilterLayout
            .ColumnDefinitions =
            compact
                ? new ColumnDefinitions("*")
                : new ColumnDefinitions("270,*");
        LibraryVisualFilterLayout
            .RowDefinitions =
            compact
                ? new RowDefinitions(
                    "Auto,Auto,*")
                : new RowDefinitions("Auto,*");
        Grid.SetColumnSpan(
            LibraryVisualFilterHeader,
            compact ? 1 : 2);
        Grid.SetRow(
            LibraryVisualFilterConditionPane,
            1);
        Grid.SetColumn(
            LibraryVisualFilterConditionPane,
            0);
        Grid.SetRow(
            LibraryVisualFilterConditionEditorScroll,
            compact ? 2 : 1);
        Grid.SetColumn(
            LibraryVisualFilterConditionEditorScroll,
            compact ? 0 : 1);
        VisualFilterConditionList.MaxHeight =
            compact ? 96 : 360;
    }

    private sealed class LibraryNumericColumnComparer(
        Func<LibraryRow, string?> value) :
        IComparer
    {
        public int Compare(
            object? x,
            object? y)
        {
            string? left =
                x is LibraryRow leftRow
                    ? value(leftRow)
                    : null;
            string? right =
                y is LibraryRow rightRow
                    ? value(rightRow)
                    : null;
            bool hasLeft = decimal.TryParse(
                left,
                NumberStyles.Number,
                CultureInfo.CurrentCulture,
                out decimal leftNumber);
            bool hasRight = decimal.TryParse(
                right,
                NumberStyles.Number,
                CultureInfo.CurrentCulture,
                out decimal rightNumber);
            if (hasLeft && hasRight)
                return leftNumber.CompareTo(
                    rightNumber);
            if (hasLeft != hasRight)
                return hasLeft ? 1 : -1;
            return string.Compare(
                left,
                right,
                StringComparison
                    .CurrentCultureIgnoreCase);
        }
    }

    private sealed class LibraryMetadataColumnComparer(
        Func<LibraryRow, string?> value,
        MetadataGridColumnSortType sortType) :
        IComparer
    {
        private static readonly Regex Number = new(
            @"[-+]?\d+(?:[.,]\d+)?",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));

        public int Compare(
            object? x,
            object? y)
        {
            string left =
                x is LibraryRow leftRow
                    ? value(leftRow) ?? ""
                    : "";
            string right =
                y is LibraryRow rightRow
                    ? value(rightRow) ?? ""
                    : "";
            if (left.Length == 0 ||
                right.Length == 0)
                return left.Length.CompareTo(
                    right.Length);
            return sortType switch
            {
                MetadataGridColumnSortType.Numeric =>
                    CompareNumbers(
                        left,
                        right),
                MetadataGridColumnSortType.Date =>
                    CompareDates(
                        left,
                        right),
                _ => string.Compare(
                    left,
                    right,
                    StringComparison
                        .CurrentCultureIgnoreCase),
            };
        }

        private static int CompareNumbers(
            string left,
            string right)
        {
            decimal leftNumber =
                ParseNumber(left);
            decimal rightNumber =
                ParseNumber(right);
            return leftNumber.CompareTo(
                rightNumber);
        }

        private static decimal ParseNumber(
            string value)
        {
            Match match = Number.Match(value);
            if (!match.Success)
                return decimal.MinValue;
            return decimal.TryParse(
                       match.Value,
                       NumberStyles.Number,
                       CultureInfo.CurrentCulture,
                       out decimal parsed) ||
                   decimal.TryParse(
                       match.Value,
                       NumberStyles.Number,
                       CultureInfo.InvariantCulture,
                       out parsed)
                ? parsed
                : decimal.MinValue;
        }

        private static int CompareDates(
            string left,
            string right)
        {
            bool hasLeft =
                DateTimeOffset.TryParse(
                    left,
                    CultureInfo.CurrentCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out DateTimeOffset leftDate);
            bool hasRight =
                DateTimeOffset.TryParse(
                    right,
                    CultureInfo.CurrentCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out DateTimeOffset rightDate);
            if (hasLeft && hasRight)
                return leftDate.CompareTo(
                    rightDate);
            if (hasLeft != hasRight)
                return hasLeft ? 1 : -1;
            return string.Compare(
                left,
                right,
                StringComparison
                    .CurrentCultureIgnoreCase);
        }
    }
}
