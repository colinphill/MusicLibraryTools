using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Services;

namespace MusicLibrary.App.ViewModels;

public sealed record LibraryColumnLayout(string Key, double? Width);

public sealed record LibrarySortLayout(string Key, ListSortDirection Direction);

public sealed record SavedLibraryView(
    string Name,
    string? FilterText,
    FilterMode FilterMode,
    string? ScopeKey,
    IReadOnlyList<LibraryColumnLayout> Columns)
{
    public LibrarySortLayout? Sort { get; init; }
}

/// <summary>
/// A tabular details view of every track with user-selectable columns, realtime filtering
/// (substring / glob / regex), and click-to-sort columns. Rows are held in a
/// <see cref="DataGridCollectionView"/> so filtering (via a predicate) and sorting coexist and
/// survive each keystroke. Column values come from the cache via <see cref="DetailsRow"/>.
/// </summary>
public partial class DetailsGridViewModel : ViewModelBase
{
    private const string ColumnLayoutKey = "table.columns";
    private const string SortLayoutKey = "table.sort";
    private const string SavedViewsKey = "table.savedViews";

    private readonly ILibraryService _library;
    private readonly IReindexService _reindex;
    private readonly IAppSettings _settings;
    private List<DetailsRow> _allRows = [];
    private PatternMatcher _matcher = PatternMatcher.Create(null, FilterMode.Substring);
    private CancellationTokenSource? _cts;

    // The persisted column layout: visible columns, in display order, with their (absolute) widths.
    // A column absent from this list is hidden. Updated on toggle/reorder/resize and saved immediately.
    private List<LibraryColumnLayout> _layout = [];
    private LibrarySortLayout? _sortLayout;
    private bool _loadingLayout;
    private bool _applyingSavedView;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusText = "Load to populate the table.";

    [ObservableProperty]
    private string? _filterText;

    [ObservableProperty]
    private bool _filterValid = true;

    [ObservableProperty]
    private FilterMode _selectedFilterMode = FilterMode.Substring;

    [ObservableProperty]
    private DataGridCollectionView? _view;

    [ObservableProperty]
    private FilterScopeOption? _selectedScope;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveView))]
    private string? _savedViewName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSavedViewCommand))]
    private SavedLibraryView? _selectedSavedView;

    public ObservableCollection<ColumnToggle> Columns { get; } = [];

    public ObservableCollection<SavedLibraryView> SavedViews { get; } = [];

    public bool CanSaveView => !string.IsNullOrWhiteSpace(SavedViewName);

    /// <summary>Which column the filter applies to; the first entry (Key = null) means all visible.</summary>
    public ObservableCollection<FilterScopeOption> FilterScopes { get; } = [];

    public IReadOnlyList<FilterMode> FilterModes { get; } = Enum.GetValues<FilterMode>();

    /// <summary>Raised when the set of visible columns changes so the view can rebuild grid columns.</summary>
    public event Action? VisibleColumnsChanged;

    /// <summary>Raised after a view/layout change so the window can apply the typed grid comparer.</summary>
    public event Action<LibrarySortLayout?>? SortLayoutChanged;

    public IReadOnlyList<(string Key, string Header)> VisibleColumns =>
        Columns.Where(c => c.IsSelected).Select(c => (c.Key, c.Header)).ToList();

    /// <summary>Find the loaded row for a path (regardless of the current filter), or null.</summary>
    public DetailsRow? RowForPath(string path) =>
        _allRows.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));

    public DetailsGridViewModel(ILibraryService library, IReindexService reindex, IAppSettings settings)
    {
        _library = library;
        _reindex = reindex;
        _settings = settings;
        // The grid is the primary browse surface, so it auto-populates whenever a config loads.
        settings.ConfigurationChanged += async (_, _) =>
        {
            LoadCommand.NotifyCanExecuteChanged();
            await ReloadAsync();
        };
        foreach (var col in DetailsColumns.All)
        {
            var toggle = new ColumnToggle(col.Key, col.Header, DetailsColumns.DefaultVisible.Contains(col.Key));
            toggle.Changed += OnColumnsChanged;
            Columns.Add(toggle);
        }
        LoadColumnLayout();
        LoadSortLayout();
        RebuildScopes();
        LoadSavedViews();
    }

    /// <summary>The saved (absolute) width for a column, or null to size it automatically.</summary>
    public double? WidthFor(string key) => _layout.FirstOrDefault(l => l.Key == key)?.Width;

    // Restore the persisted column layout: visibility, display order, and widths. Falls back to the
    // default visible set when there's nothing saved (or it can't be parsed).
    private void LoadColumnLayout()
    {
        var saved = ReadSavedLayout();
        if (saved.Count == 0)
        {
            _layout = CurrentVisibleLayout();
            return;
        }

        _loadingLayout = true;
        try
        {
            var savedKeys = saved.Select(l => l.Key).ToList();
            foreach (var toggle in Columns)
                toggle.IsSelected = savedKeys.Contains(toggle.Key);

            // Reorder the toggle list so display order = saved order (visible first), hidden after.
            var order = savedKeys.Concat(Columns.Select(c => c.Key).Where(k => !savedKeys.Contains(k))).ToList();
            for (var i = 0; i < order.Count; i++)
            {
                var idx = IndexOfKey(order[i]);
                if (idx >= 0 && idx != i)
                    Columns.Move(idx, i);
            }
            _layout = saved;
        }
        finally
        {
            _loadingLayout = false;
        }
    }

    private List<LibraryColumnLayout> ReadSavedLayout()
    {
        var json = _settings.GetPreference(ColumnLayoutKey);
        if (string.IsNullOrEmpty(json))
            return [];
        try
        {
            var parsed = JsonSerializer.Deserialize<List<LibraryColumnLayout>>(json) ?? [];
            // Drop any keys that no longer exist in the catalog.
            return parsed.Where(l => Columns.Any(c => c.Key == l.Key)).ToList();
        }
        catch
        {
            return [];
        }
    }

    private int IndexOfKey(string key)
    {
        for (var i = 0; i < Columns.Count; i++)
            if (Columns[i].Key == key)
                return i;
        return -1;
    }

    private List<LibraryColumnLayout> CurrentVisibleLayout()
    {
        var widths = _layout.ToDictionary(l => l.Key, l => l.Width);
        return VisibleColumns.Select(c => new LibraryColumnLayout(c.Key, widths.GetValueOrDefault(c.Key))).ToList();
    }

    private void SaveColumnLayout() => _settings.SetPreference(ColumnLayoutKey, JsonSerializer.Serialize(_layout));

    /// <summary>
    /// Record the grid's current column layout (display order + widths for the visible columns) and
    /// persist it. Called from the view when columns are reordered/resized or the window closes.
    /// </summary>
    public void SaveGridLayout(IReadOnlyList<(string Key, double? Width)> orderedVisible,
        LibrarySortLayout? sort = null)
    {
        _layout = orderedVisible.Select(o => new LibraryColumnLayout(o.Key, o.Width)).ToList();
        SaveSortLayout(sort);
        SaveColumnLayout();
    }

    private void LoadSortLayout()
    {
        string? json = _settings.GetPreference(SortLayoutKey);
        if (string.IsNullOrWhiteSpace(json))
            return;
        try
        {
            var sort = JsonSerializer.Deserialize<LibrarySortLayout>(json);
            if (sort is not null && Columns.Any(column => column.Key == sort.Key))
                _sortLayout = sort;
        }
        catch
        {
            // Ignore obsolete or corrupt workspace state.
        }
    }

    /// <summary>Record a sort change reported by the grid and persist it as workspace state.</summary>
    public void SaveSortLayout(LibrarySortLayout? sort)
    {
        if (_sortLayout == sort)
            return;
        MarkSavedViewCustomized();
        _sortLayout = sort;
        _settings.SetPreference(SortLayoutKey, sort is null ? null : JsonSerializer.Serialize(sort));
    }

    private void LoadSavedViews()
    {
        string? json = _settings.GetPreference(SavedViewsKey);
        if (string.IsNullOrWhiteSpace(json))
            return;
        try
        {
            var views = JsonSerializer.Deserialize<List<SavedLibraryView>>(json) ?? [];
            foreach (var view in views.Where(IsUsableSavedView))
                SavedViews.Add(view);
        }
        catch
        {
            // A bad UI preference should not prevent browsing the library.
        }
    }

    private bool IsUsableSavedView(SavedLibraryView view) =>
        !string.IsNullOrWhiteSpace(view.Name) &&
        view.Columns.Count > 0 &&
        view.Columns.All(layout => Columns.Any(column => column.Key == layout.Key));

    private void PersistSavedViews() =>
        _settings.SetPreference(SavedViewsKey, JsonSerializer.Serialize(SavedViews));

    /// <summary>
    /// Save the current filter and the most recently captured grid layout. The view calls
    /// <see cref="SaveGridLayout"/> immediately before this method so resized widths are current.
    /// Saving an existing name replaces it in place.
    /// </summary>
    public void SaveCurrentView()
    {
        string name = SavedViewName?.Trim() ?? "";
        if (name.Length == 0)
            return;

        var saved = new SavedLibraryView(
            name,
            FilterText,
            SelectedFilterMode,
            SelectedScope?.Key,
            _layout.ToList())
        {
            Sort = _sortLayout,
        };
        int existing = -1;
        for (int index = 0; index < SavedViews.Count; index++)
        {
            if (string.Equals(SavedViews[index].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                existing = index;
                break;
            }
        }
        if (existing >= 0)
            SavedViews[existing] = saved;
        else
            SavedViews.Add(saved);
        PersistSavedViews();

        _applyingSavedView = true;
        try { SelectedSavedView = saved; }
        finally { _applyingSavedView = false; }
        StatusText = $"Saved library view ‘{name}’.";
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSavedView))]
    private void DeleteSavedView()
    {
        if (SelectedSavedView is not { } selected)
            return;
        SavedViews.Remove(selected);
        SelectedSavedView = null;
        PersistSavedViews();
        StatusText = $"Deleted library view ‘{selected.Name}’.";
    }

    private bool CanDeleteSavedView() => SelectedSavedView is not null;

    partial void OnSelectedSavedViewChanged(SavedLibraryView? value)
    {
        if (value is null || _applyingSavedView)
            return;
        ApplySavedView(value);
    }

    private void ApplySavedView(SavedLibraryView saved)
    {
        _applyingSavedView = true;
        _loadingLayout = true;
        try
        {
            SavedViewName = saved.Name;
            _layout = saved.Columns.ToList();
            _sortLayout = saved.Sort;
            var keys = saved.Columns.Select(layout => layout.Key).ToList();
            foreach (var column in Columns)
                column.IsSelected = keys.Contains(column.Key);
            for (int index = 0; index < keys.Count; index++)
            {
                int current = IndexOfKey(keys[index]);
                if (current >= 0 && current != index)
                    Columns.Move(current, index);
            }

            RebuildSearchText();
            RebuildScopes();
            SelectedFilterMode = saved.FilterMode;
            FilterText = saved.FilterText;
            SelectedScope = FilterScopes.FirstOrDefault(scope => scope.Key == saved.ScopeKey) ?? FilterScopes[0];
            SaveColumnLayout();
            _settings.SetPreference(SortLayoutKey,
                _sortLayout is null ? null : JsonSerializer.Serialize(_sortLayout));
            VisibleColumnsChanged?.Invoke();
            SortLayoutChanged?.Invoke(_sortLayout);
            ApplyFilter();
        }
        finally
        {
            _loadingLayout = false;
            _applyingSavedView = false;
        }
    }

    // The scope list offers "All visible columns" plus each currently visible column, so the filter
    // can target a single field. Rebuilt whenever the visible set changes.
    private void RebuildScopes()
    {
        var previous = SelectedScope?.Key;
        FilterScopes.Clear();
        FilterScopes.Add(new FilterScopeOption(null, "All visible columns"));
        foreach (var (key, header) in VisibleColumns)
            FilterScopes.Add(new FilterScopeOption(key, header));

        SelectedScope = FilterScopes.FirstOrDefault(s => s.Key == previous) ?? FilterScopes[0];
    }

    private bool CanLoad() => _library.IsReady && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task LoadAsync()
    {
        IsBusy = true;
        LoadCommand.NotifyCanExecuteChanged();
        _cts = new CancellationTokenSource();
        StatusText = "Loading…";
        try
        {
            var records = await _library.GetAllRecordsAsync(_cts.Token);
            _allRows = records.Select(r => new DetailsRow(r)).ToList();
            RebuildSearchText();

            var view = new DataGridCollectionView(_allRows) { Filter = FilterPredicate };
            View = view;
            ApplyFilter();
            VisibleColumnsChanged?.Invoke();
            SortLayoutChanged?.Invoke(_sortLayout);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Load cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Load failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            LoadCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Reload the table from the cache if the library is ready and not already loading.</summary>
    public async Task ReloadAsync()
    {
        if (_library.IsReady && !IsBusy)
            await LoadAsync();
    }

    /// <summary>Refresh selected cache entries from their source files, then reload the table.</summary>
    public async Task ReindexPathsAsync(IReadOnlyList<string> paths)
    {
        var distinct = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!_library.IsReady || IsBusy || distinct.Count == 0)
            return;

        IsBusy = true;
        LoadCommand.NotifyCanExecuteChanged();
        _cts = new CancellationTokenSource();
        int completed = 0;
        string? outcome = null;
        try
        {
            foreach (string path in distinct)
            {
                _cts.Token.ThrowIfCancellationRequested();
                StatusText = $"Reindexing selected files… {completed:N0}/{distinct.Count:N0}";
                await _reindex.ReindexFileAsync(path, _cts.Token);
                completed++;
            }
        }
        catch (OperationCanceledException)
        {
            outcome = $"Reindex cancelled after {completed:N0} file(s).";
        }
        catch (Exception ex)
        {
            outcome = $"Reindex failed after {completed:N0} file(s): {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            LoadCommand.NotifyCanExecuteChanged();
        }

        await LoadAsync();
        StatusText = outcome ?? $"Reindexed {completed:N0} selected file(s).";
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private bool FilterPredicate(object o)
    {
        var row = (DetailsRow)o;
        var key = SelectedScope?.Key;
        // Scoped to one column → match that column's text; otherwise the visible-columns search text.
        var text = key is null ? row.SearchText : row[key];
        return _matcher.IsMatch(text);
    }

    partial void OnFilterTextChanged(string? value)
    {
        MarkSavedViewCustomized();
        ApplyFilter();
    }

    partial void OnSelectedFilterModeChanged(FilterMode value)
    {
        MarkSavedViewCustomized();
        ApplyFilter();
    }

    partial void OnSelectedScopeChanged(FilterScopeOption? value)
    {
        MarkSavedViewCustomized();
        ApplyFilter();
    }

    private void MarkSavedViewCustomized()
    {
        if (!_applyingSavedView && SelectedSavedView is not null)
            SelectedSavedView = null;
    }

    private void OnColumnsChanged()
    {
        if (_loadingLayout)
            return;          // bulk toggle changes while restoring the saved layout

        MarkSavedViewCustomized();
        _layout = CurrentVisibleLayout();   // visibility changed → refresh + persist the layout
        if (_sortLayout is not null && !VisibleColumns.Any(column => column.Key == _sortLayout.Key))
        {
            SaveSortLayout(null);
            SortLayoutChanged?.Invoke(null);
        }
        SaveColumnLayout();

        RebuildSearchText();
        RebuildScopes();     // a hidden column shouldn't remain a filter scope
        ApplyFilter();       // re-applies the predicate against the new visible set
        VisibleColumnsChanged?.Invoke();
    }

    private void RebuildSearchText()
    {
        var keys = VisibleColumns.Select(c => c.Key).ToList();
        foreach (var row in _allRows)
            row.RebuildSearchText(keys);
    }

    private void ApplyFilter()
    {
        _matcher = PatternMatcher.Create(FilterText, SelectedFilterMode);
        FilterValid = _matcher.IsValid;
        View?.Refresh();     // re-applies FilterPredicate; keeps any active column sort

        var shown = View?.Count ?? 0;
        StatusText = _allRows.Count == 0
            ? "No rows — Load to populate the table."
            : _matcher.IsEmpty
                ? $"{_allRows.Count:N0} rows"
                : $"{shown:N0} of {_allRows.Count:N0} rows"
                    + (_matcher.IsValid ? "" : "  ·  invalid pattern");
    }
}
