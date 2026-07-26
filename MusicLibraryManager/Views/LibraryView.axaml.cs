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
using System.ComponentModel;
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
    private readonly IPlatformService _platform;
    private readonly ILocalizationService _localization;
    private readonly List<AppGridColumnDefinition> _columns = [];
    private IReadOnlyList<LibraryRow> _selected = [];
    private LibrarySortState? _sort;
    private bool _restoringSelection;
    private bool _selectionChangePending;
    private bool _shellRequestedCompact;
    private bool _responsiveCompact;
    private bool _drawerOpen;
    private Control? _pendingChangesFocusReturn;
    private Control? _inspectorFocusReturn;

    public LibraryView()
    {
        InitializeComponent();
        InspectorScrim.SetValue(
            Panel.ZIndexProperty,
            10);
        _viewModel = App.GetService<LibraryViewModel>();
        _gridState = App.GetService<GridStateService>();
        _platform = App.GetService<IPlatformService>();
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
                HeaderResourceKey: "Column.Title"),
            new("Artist", "Artist", "Artist", 190, 100,
                HeaderResourceKey: "Column.Artist"),
            new("AlbumArtist", "Album artist", "AlbumArtist", 190, 100, false,
                HeaderResourceKey: "Column.AlbumArtist"),
            new("Album", "Album", "Album", 230, 120,
                HeaderResourceKey: "Column.Album"),
            new("Genre", "Genre", "Genre", 150, 90, false,
                HeaderResourceKey: "Column.Genre"),
            new("Composer", "Composer", "Composer", 190, 100, false,
                HeaderResourceKey: "Column.Composer"),
            new("Grouping", "Grouping", "Grouping", 170, 100, false,
                HeaderResourceKey: "Column.Grouping"),
            new("Year", "Year", "Year", 70, 58, false,
                HeaderResourceKey: "Column.Year"),
            new("Track", "Track", "Track", 70, 58,
                HeaderResourceKey: "Column.Track"),
            new("TrackTotal", "Track total", "TrackTotal", 90, 72, false,
                HeaderResourceKey: "Column.TrackTotal"),
            new("Disc", "Disc", "Disc", 65, 58, false,
                HeaderResourceKey: "Column.Disc"),
            new("DiscTotal", "Disc total", "DiscTotal", 85, 70, false,
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
            _columns.Insert(
                Math.Clamp(
                    descriptor.Order,
                    0,
                    _columns.Count),
                new(
                    descriptor.ColumnKey,
                    descriptor.Label,
                    $"MetadataValues[{descriptor.ValueKey}]",
                    descriptor.Width,
                    70,
                    descriptor.Visible,
                    CustomSortComparer:
                        new MetadataGridRowComparer(
                            descriptor.ValueKey,
                            descriptor.SortType)));
        }
    }

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

        if (e.Key == Key.Tab)
        {
            bool reverse =
                e.KeyModifiers.HasFlag(
                    KeyModifiers.Shift);
            if (LibraryPendingChangesPopover.IsOpen)
            {
                if (TryCyclePendingChangesFocus(
                        reverse))
                    e.Handled = true;
                return;
            }

            if (_responsiveCompact &&
                _drawerOpen &&
                InspectorScrim.IsVisible &&
                TryCycleInspectorFocus(reverse))
            {
                e.Handled = true;
                return;
            }
        }

        if (e.Key != Key.Escape)
            return;
        if (ColumnPopover.IsOpen)
        {
            ColumnPopover.IsOpen = false;
            ColumnsButton.Focus();
            e.Handled = true;
        }
        else if (LibraryPendingChangesPopover.IsOpen)
        {
            ClosePendingChanges(restoreFocus: true);
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
        else if (_responsiveCompact &&
                 _drawerOpen)
        {
            CloseCompactInspector(
                closePreference: false,
                restoreFocus: true);
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
        _pendingChangesFocusReturn =
            TopLevel.GetTopLevel(this)?
                .FocusManager?
                .GetFocusedElement() as Control;
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
        if (!restoreFocus)
            return;
        Control? target =
            _pendingChangesFocusReturn is
            {
                IsEffectivelyEnabled: true,
                IsEffectivelyVisible: true,
            }
                ? _pendingChangesFocusReturn
                : LibraryPendingChangesButton;
        _pendingChangesFocusReturn = null;
        target.Focus();
    }

    private bool TryCyclePendingChangesFocus(
        bool reverse)
    {
        Control[] focusable =
        [
            .. LibraryPendingChangesSurface
                .GetVisualDescendants()
                .OfType<Control>()
                .Where(control =>
                    control.Focusable &&
                    control.IsEffectivelyEnabled &&
                    control.IsEffectivelyVisible),
        ];
        if (focusable.Length == 0)
            return false;

        object? focused =
            TopLevel.GetTopLevel(this)?
                .FocusManager?
                .GetFocusedElement();
        int index = Array.IndexOf(
            focusable,
            focused);
        if (index < 0)
        {
            (reverse
                ? focusable[^1]
                : focusable[0]).Focus();
            return true;
        }

        bool atBoundary =
            reverse
                ? index == 0
                : index == focusable.Length - 1;
        if (!atBoundary)
            return false;
        (reverse
            ? focusable[^1]
            : focusable[0]).Focus();
        return true;
    }

    private bool TryCycleInspectorFocus(
        bool reverse)
    {
        Control[] focusable =
        [
            .. InspectorView
                .GetVisualDescendants()
                .OfType<Control>()
                .Where(control =>
                    control.Focusable &&
                    control.IsEffectivelyEnabled &&
                    control.IsEffectivelyVisible),
        ];
        if (focusable.Length == 0)
            return false;

        object? focused =
            TopLevel.GetTopLevel(this)?
                .FocusManager?
                .GetFocusedElement();
        int index = Array.IndexOf(
            focusable,
            focused);
        if (index < 0)
        {
            (reverse
                ? focusable[^1]
                : focusable[0]).Focus();
            return true;
        }

        bool atBoundary =
            reverse
                ? index == 0
                : index == focusable.Length - 1;
        if (!atBoundary)
            return false;
        (reverse
            ? focusable[^1]
            : focusable[0]).Focus();
        return true;
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
        var items = new List<object>
        {
            CreateHandoffScopeMenu(
                WorkbenchHandoffScopeKind.Selected,
                "Library.Handoff.Scope.Selected",
                _viewModel.HasSelection),
            CreateHandoffScopeMenu(
                WorkbenchHandoffScopeKind.VisibleResults,
                "Library.Handoff.Scope.Visible",
                _viewModel.Rows.Count > 0),
            CreateHandoffScopeMenu(
                WorkbenchHandoffScopeKind.AllResults,
                "Library.Handoff.Scope.All",
                _viewModel.TotalCount > 0),
        };

        if (includeFileActions && _selected.Count > 0)
        {
            items.Add(new Separator());
            var copy = new MenuItem
            {
                Header = _localization.Get(
                    "Library.Action.CopyPaths"),
            };
            copy.Click += OnCopyPaths;
            items.Add(copy);

            var reveal = new MenuItem
            {
                Header = _localization.Get(
                    "Library.Action.Reveal"),
                IsEnabled = _selected.Count == 1,
            };
            reveal.Click += OnReveal;
            items.Add(reveal);

            var refresh = new MenuItem
            {
                Header = _localization.Get(
                    "Library.Action.RefreshAffectedPaths"),
            };
            refresh.Click += OnReindex;
            items.Add(refresh);
        }

        return items;
    }

    private MenuItem CreateHandoffScopeMenu(
        WorkbenchHandoffScopeKind scope,
        string headerKey,
        bool enabled)
    {
        var sections = new List<MenuItem>();
        foreach (WorkbenchSection section in HandoffSections)
        {
            var item = new MenuItem
            {
                Header = _localization.Get(
                    $"Workbench.Navigation.Section.{section}"),
            };
            item.Click += async (_, _) =>
                await _viewModel.HandoffToWorkbenchAsync(
                    section,
                    scope);
            sections.Add(item);
        }
        return new MenuItem
        {
            Header = _localization.Get(headerKey),
            IsEnabled = enabled,
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
            _inspectorFocusReturn =
                TopLevel.GetTopLevel(this)?
                    .FocusManager?
                    .GetFocusedElement() as Control;
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
            _inspectorFocusReturn = null;
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
        bool hasSelection = _selected.Count > 0;
        SelectedCountLabel.IsVisible =
            SelectionWorkbenchButton.IsVisible =
            CopyButton.IsVisible =
            RevealButton.IsVisible =
            ReindexButton.IsVisible =
            hasSelection;
        SelectedCountLabel.Text = _localization.Format(
            "Library.Selection.CountFormat",
            _selected.Count);
    }

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
        if (!restoreFocus)
            return;

        Control target =
            _inspectorFocusReturn is
            {
                IsEffectivelyEnabled: true,
                IsEffectivelyVisible: true,
            }
                ? _inspectorFocusReturn
                : InspectorToggle;
        _inspectorFocusReturn = null;
        target.Focus();
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
        CloseCompactInspector(
            closePreference: false,
            restoreFocus: true);
        e.Handled = true;
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
        double availableWidth = Math.Max(320, Bounds.Width - 24);
        double availableHeight = Math.Max(320, Bounds.Height - 32);
        LibraryPendingChangesSurface.Width = Math.Min(430, availableWidth);
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
}
