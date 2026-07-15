using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MusicLibrary.App.Services;
using MusicLibrary.App.ViewModels;
using MusicLibrary.Core.Services;

namespace MusicLibrary.App.Views;

public partial class MainWindow : Window
{
    private readonly DataGrid? _detailsGrid;
    private readonly TextBox? _filterTextBox;
    private DetailsGridViewModel? _table;
    private MainWindowViewModel? _vm;
    private bool _syncingGrid;
    private bool _restoringWindowState;
    private WindowWorkspaceState? _normalWindowState;

    // Maps each built grid column back to its details-column key, for persisting the layout.
    private readonly Dictionary<DataGridColumn, string> _columnKeys = new();

    public MainWindow()
    {
        InitializeComponent();

        _detailsGrid = this.FindControl<DataGrid>("DetailsGrid");
        _filterTextBox = this.FindControl<TextBox>("LibraryFilterTextBox");
        if (_detailsGrid is not null)
        {
            _detailsGrid.SelectionChanged += OnGridSelectionChanged;
            _detailsGrid.Sorting += OnGridSorting;
        }

        DataContextChanged += (_, _) => Hook();
        Opened += (_, _) => RestoreWindowWorkspace();
        PositionChanged += (_, _) => CaptureNormalWindowState();
        SizeChanged += (_, _) => CaptureNormalWindowState();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
                CaptureNormalWindowState();
        };
        KeyDown += OnWindowKeyDown;
        Closing += (_, _) => PersistWorkspace();
    }

    private void OnIngestSourceDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFiles()?.Any(item => item is IStorageFolder) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnIngestSourceDrop(object? sender, DragEventArgs e)
    {
        var folder = e.DataTransfer.TryGetFiles()?.OfType<IStorageFolder>().FirstOrDefault();
        if (folder is not null && _vm is not null)
            _vm.Ingest.SetDroppedSource(folder.Path.LocalPath);
        e.Handled = true;
    }

    private void PersistWorkspace()
    {
        PersistColumnLayout();
        _table?.SaveWorkspaceState();
        if (_vm is null)
            return;
        CaptureNormalWindowState();
        if (_normalWindowState is { } state)
            _vm.WorkspaceState.SaveWindowState(state with { Maximized = WindowState == WindowState.Maximized });
    }

    private void RestoreWindowWorkspace()
    {
        if (_vm?.WorkspaceState.LoadWindowState() is not { } state)
        {
            CaptureNormalWindowState();
            return;
        }

        _restoringWindowState = true;
        try
        {
            var screens = Screens.All;
            var screen = screens.FirstOrDefault(candidate =>
                    WindowWorkspacePlacementCalculator.IsMeaningfullyVisible(
                        state, candidate.WorkingArea, candidate.Scaling))
                ?? Screens.Primary
                ?? screens.FirstOrDefault();
            if (screen is null)
                return;

            var area = screen.WorkingArea;
            var placement = WindowWorkspacePlacementCalculator.Fit(
                state, area, screen.Scaling, MinWidth, MinHeight);

            WindowState = WindowState.Normal;
            Width = placement.Width;
            Height = placement.Height;
            Position = new PixelPoint(placement.X, placement.Y);
            _normalWindowState = state with
            {
                Width = placement.Width,
                Height = placement.Height,
                X = placement.X,
                Y = placement.Y,
                Maximized = false,
            };
            if (state.Maximized)
                WindowState = WindowState.Maximized;
        }
        finally
        {
            _restoringWindowState = false;
        }
    }

    private void CaptureNormalWindowState()
    {
        if (_restoringWindowState || WindowState != WindowState.Normal || Bounds.Width < MinWidth || Bounds.Height < MinHeight)
            return;
        _normalWindowState = new WindowWorkspaceState(
            WorkspaceStateService.CurrentVersion,
            Bounds.Width,
            Bounds.Height,
            Position.X,
            Position.Y,
            false);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null)
            return;
        var shortcut = WorkspaceShortcutMap.Resolve(e.Key, e.KeyModifiers);
        switch (shortcut.Kind)
        {
            case WorkspaceShortcutKind.SelectTab:
                _vm.SelectTabCommand.Execute(shortcut.Argument);
                e.Handled = true;
                return;
            case WorkspaceShortcutKind.MoveTab:
                _vm.MoveTabCommand.Execute(shortcut.Argument);
                break;
            case WorkspaceShortcutKind.FocusFilter:
                _vm.SelectTabCommand.Execute(0);
                Dispatcher.UIThread.Post(() => _filterTextBox?.Focus(), DispatcherPriority.Input);
                break;
            case WorkspaceShortcutKind.ReloadLibrary:
                _vm.ReloadLibraryCommand.Execute(null);
                break;
            case WorkspaceShortcutKind.IndexLibrary:
                _vm.IndexLibraryCommand.Execute(null);
                break;
            case WorkspaceShortcutKind.SaveActiveEditor:
                _vm.SaveActiveEditorCommand.Execute(null);
                break;
            case WorkspaceShortcutKind.CancelActiveOperation:
                _vm.CancelActiveOperationCommand.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // Capture the grid's current visible columns in display order, with their pixel widths, and hand
    // them to the VM to persist. The fixed thumbnail column (no key) is skipped.
    private void PersistColumnLayout()
    {
        if (_detailsGrid is null || _table is null)
            return;

        var ordered = _detailsGrid.Columns
            .Where(c => _columnKeys.ContainsKey(c))
            .OrderBy(c => c.DisplayIndex)
            .Select(c => (_columnKeys[c], c.ActualWidth > 0 ? (double?)c.ActualWidth : null))
            .ToList();

        if (ordered.Count > 0)
            _table.SaveGridLayout(ordered, CurrentSortLayout());
    }

    private LibrarySortLayout? CurrentSortLayout()
    {
        var description = _table?.View?.SortDescriptions.FirstOrDefault();
        return description is DataGridComparerSortDescription
            {
                SourceComparer: DetailsRowComparer comparer,
            }
            ? new LibrarySortLayout(comparer.Key, description.Direction)
            : null;
    }

    private void OnGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        // Sorting is raised before the DataGrid updates its collection view. Capture on the next
        // dispatcher pass so saved views see the resulting direction (including "not sorted").
        Dispatcher.UIThread.Post(
            () => _table?.SaveSortLayout(CurrentSortLayout()),
            DispatcherPriority.Background);
    }

    private void ApplySortLayout(LibrarySortLayout? sort)
    {
        if (_table?.View is not { } view)
            return;

        view.SortDescriptions.Clear();
        if (sort is null)
            return;

        var column = _columnKeys.FirstOrDefault(pair => pair.Value == sort.Key).Key;
        if (column?.CustomSortComparer is { } comparer)
            view.SortDescriptions.Add(new DataGridComparerSortDescription(comparer, sort.Direction));
    }

    private void Hook()
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (_table is not null)
        {
            _table.VisibleColumnsChanged -= RebuildDetailsColumns;
            _table.SortLayoutChanged -= ApplySortLayout;
        }
        if (_vm is not null)
            _vm.SelectGridRequested -= SelectGridRows;

        _vm = vm;
        _table = vm.Table;
        _table.VisibleColumnsChanged += RebuildDetailsColumns;
        _table.SortLayoutChanged += ApplySortLayout;
        _vm.SelectGridRequested += SelectGridRows;
        RebuildDetailsColumns();
    }

    // User changed the grid's selection → push the set of paths to the shared selection.
    private void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingGrid || _detailsGrid is null || _vm is null)
            return;

        _vm.OnGridSelectionChanged(SelectedPaths());
    }

    private IReadOnlyList<string> SelectedPaths() => _detailsGrid?.SelectedItems
        .OfType<DetailsRow>()
        .Select(row => row.Path)
        .ToList() ?? [];

    private void OnSaveLibraryView(object? sender, RoutedEventArgs e)
    {
        PersistColumnLayout();
        _table?.SaveCurrentView();
    }

    private void OnOpenDetails(object? sender, RoutedEventArgs e) => OpenSelectionTab(2);
    private void OnEditTags(object? sender, RoutedEventArgs e) => OpenSelectionTab(1);
    private void OnEditArtwork(object? sender, RoutedEventArgs e) => OpenSelectionTab(3);

    private void OpenSelectionTab(int index)
    {
        if (_vm is not null && SelectedPaths().Count > 0)
            _vm.SelectedTabIndex = index;
    }

    private async void OnReindexSelection(object? sender, RoutedEventArgs e)
    {
        if (_table is not null)
            await _table.ReindexPathsAsync(SelectedPaths());
    }

    private async void OnCopyPaths(object? sender, RoutedEventArgs e)
    {
        var paths = SelectedPaths();
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (paths.Count > 0 && clipboard is not null)
        {
            try { await clipboard.SetValueAsync(DataFormat.Text, string.Join(Environment.NewLine, paths)); }
            catch { /* Clipboard availability is platform/session dependent. */ }
        }
    }

    private void OnRevealSelection(object? sender, RoutedEventArgs e)
    {
        string? path = SelectedPaths().FirstOrDefault();
        if (path is null)
            return;

        var start = new ProcessStartInfo { UseShellExecute = false };
        if (OperatingSystem.IsWindows())
        {
            start.FileName = "explorer.exe";
            start.ArgumentList.Add("/select,");
            start.ArgumentList.Add(path);
        }
        else if (OperatingSystem.IsMacOS())
        {
            start.FileName = "open";
            start.ArgumentList.Add("-R");
            start.ArgumentList.Add(path);
        }
        else
        {
            start.FileName = "xdg-open";
            start.ArgumentList.Add(Path.GetDirectoryName(path) ?? path);
        }

        try { Process.Start(start); }
        catch { /* The shell integration is optional; keep the app usable if it is unavailable. */ }
    }

    // Mirror an external selection (from the tree) into the grid, without echoing back.
    private void SelectGridRows(IReadOnlyList<string> paths)
    {
        if (_detailsGrid is null || _table is null)
            return;

        _syncingGrid = true;
        try
        {
            _detailsGrid.SelectedItems.Clear();
            DetailsRow? first = null;
            foreach (var path in paths)
            {
                var row = _table.RowForPath(path);
                if (row is null)
                    continue;
                try { _detailsGrid.SelectedItems.Add(row); first ??= row; }
                catch { /* row filtered out of the current view — skip */ }
            }
            if (first is not null)
                _detailsGrid.ScrollIntoView(first, null);
        }
        finally
        {
            _syncingGrid = false;
        }
    }

    // The details grid's columns are user-selectable, so they're (re)built in code as the visible
    // set changes — DataGrid columns aren't a bindable collection.
    private void RebuildDetailsColumns()
    {
        if (_detailsGrid is null || _table is null)
            return;

        _detailsGrid.Columns.Clear();
        _columnKeys.Clear();

        // A fixed leading column showing the file's first embedded artwork. The Image lazily pulls
        // its thumbnail via the ThumbnailLoader attached property (bound to the row's path), so only
        // the rows the grid actually realizes (virtualized) ever decode art.
        _detailsGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "",
            Width = new DataGridLength(52),
            CanUserSort = false,
            CanUserResize = false,
            CanUserReorder = false,
            IsReadOnly = true,
            CellTemplate = new FuncDataTemplate<DetailsRow>((_, _) =>
            {
                var img = new Image
                {
                    Width = 40,
                    Height = 40,
                    Stretch = Stretch.UniformToFill,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                img.Bind(ThumbnailLoader.PathProperty, new Binding(nameof(DetailsRow.Path)));
                return img;
            }),
        });

        foreach (var (key, header) in _table.VisibleColumns)
        {
            var width = _table.WidthFor(key);
            var column = new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding($"[{key}]"),
                CanUserSort = true,
                CustomSortComparer = new DetailsRowComparer(key),
                IsReadOnly = true,
                Width = width is double px ? new DataGridLength(px) : DataGridLength.Auto,
            };
            _detailsGrid.Columns.Add(column);
            _columnKeys[column] = key;
        }
    }
}
