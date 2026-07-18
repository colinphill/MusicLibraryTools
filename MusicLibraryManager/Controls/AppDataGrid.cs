using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Controls.Templates;
using global::Avalonia.Data;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Controls;

public sealed record AppGridColumnDefinition(
    string Key,
    string Header,
    string? BindingPath,
    double Width,
    double MinWidth = 60,
    bool Visible = true,
    IDataTemplate? CellTemplate = null,
    bool Sortable = true);

public sealed class AppDataGrid : DataGrid
{
    private readonly Dictionary<DataGridColumn, AppGridColumnDefinition> _definitions = [];
    private bool _configuring;

    // Avalonia styles custom subclasses by their concrete type. Reuse DataGrid's style key so
    // the official Fluent control theme supplies the native header, rows, scrollbars, and cells.
    protected override Type StyleKeyOverride => typeof(DataGrid);

    public AppDataGrid()
    {
        Classes.Add("primary-grid");
        AutoGenerateColumns = false;
        CanUserReorderColumns = true;
        CanUserResizeColumns = true;
        CanUserSortColumns = true;
        IsReadOnly = true;
        SelectionMode = DataGridSelectionMode.Extended;
        HeadersVisibility = DataGridHeadersVisibility.Column;
        GridLinesVisibility = DataGridGridLinesVisibility.All;
        RowHeight = 38;
        ColumnHeaderHeight = 39;
        ColumnDisplayIndexChanged += (_, _) => NotifyLayoutChanged();
        ColumnReordered += (_, _) => NotifyLayoutChanged();
        Sorting += OnSorting;
    }

    public event EventHandler? LayoutChanged;
    public event EventHandler? SortChanged;
    public string? CurrentSortKey { get; private set; }
    public bool CurrentSortDescending { get; private set; }

    public void ConfigureColumns(IEnumerable<AppGridColumnDefinition> definitions)
    {
        _configuring = true;
        try
        {
            foreach (DataGridColumn column in _definitions.Keys)
                column.PropertyChanged -= OnColumnPropertyChanged;
            _definitions.Clear();
            Columns.Clear();
            foreach (AppGridColumnDefinition definition in definitions.Where(item => item.Visible))
            {
                DataGridColumn column = definition.CellTemplate is not null
                    ? new DataGridTemplateColumn { CellTemplate = definition.CellTemplate }
                    : new DataGridTextColumn
                    {
                        Binding = new Binding(definition.BindingPath ?? definition.Key),
                    };
                column.Header = definition.Header;
                column.Width = new DataGridLength(definition.Width);
                column.MinWidth = definition.MinWidth;
                column.CanUserSort = definition.Sortable;
                Columns.Add(column);
                _definitions[column] = definition;
                column.PropertyChanged += OnColumnPropertyChanged;
            }
        }
        finally
        {
            _configuring = false;
        }
    }

    public IReadOnlyList<LibraryColumnState> CaptureColumnLayout() => Columns
        .Select(column =>
        {
            AppGridColumnDefinition definition = _definitions[column];
            double width = column.ActualWidth > 0 ? column.ActualWidth : definition.Width;
            return new LibraryColumnState(definition.Key, width, column.DisplayIndex, true);
        })
        .OrderBy(state => state.DisplayIndex)
        .ToArray();

    public string? KeyFor(DataGridColumn column) =>
        _definitions.TryGetValue(column, out AppGridColumnDefinition? definition) ? definition.Key : null;

    private void NotifyLayoutChanged()
    {
        if (!_configuring)
            LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnColumnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == DataGridColumn.WidthProperty)
            NotifyLayoutChanged();
    }

    private void OnSorting(object? sender, DataGridColumnEventArgs e)
    {
        string? key = KeyFor(e.Column);
        if (key is null)
            return;
        CurrentSortDescending = CurrentSortKey == key && !CurrentSortDescending;
        CurrentSortKey = key;
        SortChanged?.Invoke(this, EventArgs.Empty);
    }
}
