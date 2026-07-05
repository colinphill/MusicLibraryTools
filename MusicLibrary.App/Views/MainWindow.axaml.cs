using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using MusicLibrary.App.ViewModels;
using MusicLibrary.Core.Services;

namespace MusicLibrary.App.Views;

public partial class MainWindow : Window
{
    private readonly DataGrid? _detailsGrid;
    private DetailsGridViewModel? _table;
    private MainWindowViewModel? _vm;
    private bool _syncingGrid;

    // Maps each built grid column back to its details-column key, for persisting the layout.
    private readonly Dictionary<DataGridColumn, string> _columnKeys = new();

    public MainWindow()
    {
        InitializeComponent();

        _detailsGrid = this.FindControl<DataGrid>("DetailsGrid");
        if (_detailsGrid is not null)
            _detailsGrid.SelectionChanged += OnGridSelectionChanged;

        DataContextChanged += (_, _) => Hook();
        // Persist the final column layout (order + widths) when the window closes.
        Closing += (_, _) => PersistColumnLayout();
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
            _table.SaveGridLayout(ordered);
    }

    private void Hook()
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (_table is not null)
            _table.VisibleColumnsChanged -= RebuildDetailsColumns;
        if (_vm is not null)
            _vm.SelectGridRequested -= SelectGridRows;

        _vm = vm;
        _table = vm.Table;
        _table.VisibleColumnsChanged += RebuildDetailsColumns;
        _vm.SelectGridRequested += SelectGridRows;
        RebuildDetailsColumns();
    }

    // User changed the grid's selection → push the set of paths to the shared selection.
    private void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingGrid || _detailsGrid is null || _vm is null)
            return;

        var paths = _detailsGrid.SelectedItems
            .OfType<DetailsRow>()
            .Select(r => r.Path)
            .ToList();
        _vm.OnGridSelectionChanged(paths);
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
