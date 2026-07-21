using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

public partial class LibraryViewModel : ObservableObject, INavigationGuard
{
    private const string ViewsPreference = "manager.library.views.v1";
    private const string WorkspacePreference = "manager.library.workspace.v1";
    private readonly ILibraryService _library;
    private readonly IReindexService _reindex;
    private readonly IAppSettings _settings;
    private readonly SelectionInspectorViewModel _inspector;
    private readonly INavigationService _navigation;
    private readonly IThumbnailService _thumbnails;
    private readonly SemaphoreSlim _thumbnailGate = new(4, 4);
    private readonly object _thumbnailSync = new();
    private readonly Dictionary<LibraryRow, CancellationTokenSource> _thumbnailLoads = [];
    private readonly Dictionary<string, ThumbnailCacheItem> _thumbnailCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _thumbnailLru = [];
    private CancellationTokenSource _thumbnailLifetime = new();
    private const int ThumbnailCacheLimit = 256;
    private List<LibraryRow> _allRows = [];
    private HashSet<string> _healthFilterPaths = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _selectedPaths = [];
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _filterCancellation;
    private bool _loadingWorkspace;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReloadCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTextFilter))]
    private string? _filterText;

    [ObservableProperty]
    private FilterMode _filterMode = FilterMode.Substring;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilterError))]
    private string? _filterError;

    [ObservableProperty]
    private string _statusText = "Load a configuration to browse your library.";

    [ObservableProperty]
    private string? _newViewName;

    [ObservableProperty]
    private LibraryViewDefinition? _selectedView;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows))]
    [NotifyPropertyChangedFor(nameof(HasEmptyState))]
    [NotifyPropertyChangedFor(nameof(ResultCountText))]
    private IReadOnlyList<LibraryRow> _rows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyState))]
    [NotifyPropertyChangedFor(nameof(EmptyStateTitle))]
    [NotifyPropertyChangedFor(nameof(EmptyStateMessage))]
    [NotifyPropertyChangedFor(nameof(EmptyStateActionLabel))]
    private LibraryPageState _pageState = LibraryPageState.NoConfiguration;

    [ObservableProperty]
    private bool _isInspectorOpen = true;

    public LibraryViewModel(
        ILibraryService library,
        IReindexService reindex,
        IAppSettings settings,
        SelectionInspectorViewModel inspector,
        INavigationService navigation,
        IndexingViewModel indexing,
        IThumbnailService thumbnails)
    {
        _library = library;
        _reindex = reindex;
        _settings = settings;
        _inspector = inspector;
        _navigation = navigation;
        _thumbnails = thumbnails;
        Indexing = indexing;
        foreach (DetailsColumn column in DetailsColumns.All)
            Columns.Add(new LibraryColumnChoice(column.Key, column.Header, DetailsColumns.DefaultVisible.Contains(column.Key)));
        LoadViews();
        LoadWorkspace();
        settings.ConfigurationChanged += OnConfigurationChanged;
        indexing.IndexCompleted += () => _ = ReloadAsync();
        inspector.FilesChanged += () => _ = ReloadAsync();
        inspector.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SelectionInspectorViewModel.HasUnsavedChanges))
                OnPropertyChanged(nameof(HasUnsavedSelectionChanges));
        };
    }

    public ObservableCollection<LibraryViewDefinition> SavedViews { get; } = [];
    public ObservableCollection<LibraryColumnChoice> Columns { get; } = [];
    public IReadOnlyList<FilterMode> FilterModes { get; } = Enum.GetValues<FilterMode>();
    public SelectionInspectorViewModel Inspector => _inspector;
    public IndexingViewModel Indexing { get; }
    public int TotalCount => _allRows.Count;
    public int HealthFilterCount => _healthFilterPaths.Count;
    public bool HasHealthFilter => _healthFilterPaths.Count > 0;
    public string HealthFilterSummary => $"Health results: {HealthFilterCount:N0} track(s)";
    public bool HasTextFilter => !string.IsNullOrWhiteSpace(FilterText);
    public bool HasRows => Rows.Count > 0;
    public bool HasEmptyState => Rows.Count == 0 && PageState != LibraryPageState.Loading;
    public bool HasFilterError => !string.IsNullOrWhiteSpace(FilterError);
    public string ResultCountText => Rows.Count == TotalCount
        ? $"{Rows.Count:N0} tracks"
        : $"{Rows.Count:N0} of {TotalCount:N0}";
    public IReadOnlyList<string> SelectedPaths => _selectedPaths;
    public bool HasUnsavedSelectionChanges => Inspector.HasUnsavedChanges;
    public bool HasUnsavedChanges => HasUnsavedSelectionChanges;
    public event Action? HealthFilterClearRequested;
    public string EmptyStateTitle => PageState switch
    {
        LibraryPageState.NoConfiguration => "Choose a library configuration",
        LibraryPageState.NotIndexed => "This library has not been indexed",
        LibraryPageState.FilteredToZero => "No tracks match this filter",
        LibraryPageState.NoResults => "No tracks match the Health results",
        LibraryPageState.Error => "The library could not be loaded",
        _ => "No tracks to show",
    };
    public string EmptyStateMessage => PageState switch
    {
        LibraryPageState.NoConfiguration => "Open Settings to choose or create a configuration before browsing.",
        LibraryPageState.NotIndexed => "Index the configured music roots to populate the cached library.",
        LibraryPageState.FilteredToZero => "Clear or revise the filter to show tracks again.",
        LibraryPageState.NoResults => "Clear the Health filter to return to the full library.",
        LibraryPageState.Error => StatusText,
        _ => "Adjust this view or reload the library.",
    };
    public string EmptyStateActionLabel => PageState switch
    {
        LibraryPageState.NoConfiguration => "Open Settings",
        LibraryPageState.NotIndexed => "Index library",
        LibraryPageState.FilteredToZero => "Clear filter",
        LibraryPageState.NoResults => "Clear Health filter",
        LibraryPageState.Error => "Try again",
        _ => "Reload",
    };

    private async void OnConfigurationChanged(object? sender, EventArgs args)
    {
        // Restore cached browsing first, then return to the UI loop before starting the root scan.
        // This mirrors the portable app and ensures Home is painted before progress begins.
        await ReloadAsync();
        await Task.Yield();
        await Indexing.StartAutomaticIndexAsync();
    }

    partial void OnFilterTextChanged(string? value)
    {
        if (!_loadingWorkspace)
            SaveWorkspace();
        QueueFilter();
    }

    partial void OnFilterModeChanged(FilterMode value)
    {
        if (!_loadingWorkspace)
            SaveWorkspace();
        QueueFilter();
    }

    partial void OnIsInspectorOpenChanged(bool value)
    {
        if (!_loadingWorkspace)
            SaveWorkspace();
    }

    partial void OnSelectedViewChanged(LibraryViewDefinition? value)
    {
        if (value is null)
            return;
        FilterMode = value.FilterMode;
        FilterText = value.Filter;
    }

    public void SetGlobalFilter(string? text)
    {
        FilterText = text;
        _navigation.Navigate(ShellDestination.Library);
    }

    public void SetHealthFilter(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var next = paths.Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_healthFilterPaths.SetEquals(next))
            return;
        _healthFilterPaths = next;
        OnPropertyChanged(nameof(HealthFilterCount));
        OnPropertyChanged(nameof(HasHealthFilter));
        OnPropertyChanged(nameof(HealthFilterSummary));
        QueueFilter();
    }

    [RelayCommand]
    private void ClearFilter() => FilterText = null;

    [RelayCommand]
    private void ClearHealthFilter()
    {
        if (!HasHealthFilter)
            return;
        SetHealthFilter([]);
        HealthFilterClearRequested?.Invoke();
    }

    [RelayCommand]
    private async Task EmptyStateActionAsync()
    {
        switch (PageState)
        {
            case LibraryPageState.NoConfiguration:
                _navigation.Navigate(ShellDestination.Settings);
                break;
            case LibraryPageState.NotIndexed:
                if (Indexing.IndexCommand.CanExecute(null))
                    await Indexing.IndexCommand.ExecuteAsync(null);
                break;
            case LibraryPageState.FilteredToZero:
                ClearFilter();
                break;
            case LibraryPageState.NoResults:
                ClearHealthFilter();
                break;
            default:
                await ReloadAsync();
                break;
        }
    }

    private bool CanReload() => _library.IsReady && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanReload))]
    public async Task ReloadAsync()
    {
        _loadCancellation?.Cancel();
        ResetThumbnails();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        if (!_library.IsReady)
        {
            _allRows = [];
            Rows = [];
            PageState = LibraryPageState.NoConfiguration;
            StatusText = "Choose a library configuration in Settings.";
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(ResultCountText));
            return;
        }
        IsBusy = true;
        PageState = LibraryPageState.Loading;
        StatusText = "Loading the cached library…";
        try
        {
            var records = await _library.GetAllRecordsAsync(cancellation.Token);
            var rows = await Task.Run(() => records.Select(record => new LibraryRow(record)).ToList(), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_inspector.HasUnsavedChanges && _selectedPaths.Count > 0)
            {
                var loadedPaths = rows.Select(row => row.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                rows.AddRange(_allRows.Where(row =>
                    _selectedPaths.Contains(row.Path, StringComparer.OrdinalIgnoreCase) &&
                    loadedPaths.Add(row.Path)));
            }
            _allRows = rows;
            await ApplyFilterAsync(immediate: true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            StatusText = $"Could not load the cached library: {error.Message}";
            PageState = LibraryPageState.Error;
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
            {
                _loadCancellation = null;
                IsBusy = false;
            }
            cancellation.Dispose();
        }
    }

    [RelayCommand]
    private void Cancel() => _loadCancellation?.Cancel();

    public async Task<bool> SelectAsync(IReadOnlyList<LibraryRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var selection = new SelectionContext(
            rows.Select(row => row.Path).ToArray(),
            rows.Select(row => row.Record).ToArray());
        if (!await _inspector.TryLoadAsync(selection))
            return false;
        SetSelectedPaths(selection.Paths);
        return true;
    }

    /// <summary>Navigation hosts call this before replacing the Library view.</summary>
    public Task<bool> ConfirmCanNavigateAwayAsync() => _inspector.ConfirmDiscardChangesAsync();

    public Task<bool> ConfirmNavigationAsync() => ConfirmCanNavigateAwayAsync();

    public IReadOnlyList<LibraryRow> GetVisibleSelectedRows()
    {
        if (_selectedPaths.Count == 0)
            return [];
        var selected = _selectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Rows.Where(row => selected.Contains(row.Path)).ToArray();
    }

    private void SetSelectedPaths(IReadOnlyList<string> paths)
    {
        string[] distinct = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (_selectedPaths.SequenceEqual(distinct, StringComparer.OrdinalIgnoreCase))
            return;
        _selectedPaths = distinct;
        OnPropertyChanged(nameof(SelectedPaths));
    }

    public Task ApplyFilterNowAsync(CancellationToken cancellationToken = default)
        => ApplyFilterAsync(immediate: true, cancellationToken);

    public async Task ReindexAsync(IReadOnlyList<string> paths)
    {
        foreach (string path in paths)
            await _reindex.ReindexFileAsync(path);
        await ReloadAsync();
    }

    /// <summary>Loads artwork only for a row that the virtualized table has realized.</summary>
    public async Task LoadThumbnailAsync(LibraryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        CancellationTokenSource cancellation;
        object? cached = null;
        bool hasCachedValue;
        lock (_thumbnailSync)
        {
            if (row.ThumbnailLoaded || _thumbnailLoads.ContainsKey(row))
                return;
            hasCachedValue = TryGetCachedThumbnail(row.Path, out cached);
            if (!hasCachedValue)
            {
                cancellation = CancellationTokenSource.CreateLinkedTokenSource(_thumbnailLifetime.Token);
                _thumbnailLoads[row] = cancellation;
            }
            else
            {
                cancellation = null!;
            }
        }

        if (hasCachedValue)
        {
            row.ThumbnailSource = cached;
            row.ThumbnailLoaded = true;
            return;
        }

        bool enteredGate = false;
        try
        {
            await _thumbnailGate.WaitAsync(cancellation.Token);
            enteredGate = true;
            byte[]? bytes = await _library.GetFirstImageAsync(row.Path, cancellation.Token);
            object? image = bytes is { Length: > 0 }
                ? await _thumbnails.CreateImageSourceAsync(bytes, 56, cancellation.Token)
                : null;
            cancellation.Token.ThrowIfCancellationRequested();
            lock (_thumbnailSync)
            {
                if (!_thumbnailLoads.TryGetValue(row, out CancellationTokenSource? active) ||
                    !ReferenceEquals(active, cancellation))
                    return;
                AddCachedThumbnail(row.Path, image);
            }
            row.ThumbnailSource = image;
            row.ThumbnailLoaded = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch
        {
            // A malformed image should leave a blank thumbnail without affecting the library grid.
            if (!cancellation.IsCancellationRequested)
                row.ThumbnailLoaded = true;
        }
        finally
        {
            if (enteredGate)
                _thumbnailGate.Release();
            lock (_thumbnailSync)
            {
                if (_thumbnailLoads.TryGetValue(row, out CancellationTokenSource? active) &&
                    ReferenceEquals(active, cancellation))
                    _thumbnailLoads.Remove(row);
            }
            cancellation.Dispose();
        }
    }

    /// <summary>Stops work for a recycled row and releases its image reference.</summary>
    public void ReleaseThumbnail(LibraryRow row)
    {
        lock (_thumbnailSync)
        {
            if (_thumbnailLoads.TryGetValue(row, out CancellationTokenSource? cancellation))
                cancellation.Cancel();
        }
        row.ThumbnailSource = null;
        row.ThumbnailLoaded = false;
    }

    private bool TryGetCachedThumbnail(string path, out object? image)
    {
        if (!_thumbnailCache.TryGetValue(path, out ThumbnailCacheItem? item))
        {
            image = null;
            return false;
        }
        _thumbnailLru.Remove(item.Node);
        _thumbnailLru.AddFirst(item.Node);
        image = item.Image;
        return true;
    }

    private void AddCachedThumbnail(string path, object? image)
    {
        if (_thumbnailCache.Remove(path, out ThumbnailCacheItem? old))
            _thumbnailLru.Remove(old.Node);
        var node = new LinkedListNode<string>(path);
        _thumbnailLru.AddFirst(node);
        _thumbnailCache[path] = new ThumbnailCacheItem(image, node);
        while (_thumbnailCache.Count > ThumbnailCacheLimit && _thumbnailLru.Last is { } last)
        {
            _thumbnailCache.Remove(last.Value);
            _thumbnailLru.RemoveLast();
        }
    }

    private void ResetThumbnails()
    {
        lock (_thumbnailSync)
        {
            _thumbnailLifetime.Cancel();
            _thumbnailLifetime.Dispose();
            _thumbnailLifetime = new CancellationTokenSource();
            foreach (CancellationTokenSource cancellation in _thumbnailLoads.Values)
                cancellation.Cancel();
            _thumbnailCache.Clear();
            _thumbnailLru.Clear();
        }
    }

    [RelayCommand]
    private void SaveView()
    {
        string name = NewViewName?.Trim() ?? "";
        if (name.Length == 0)
            return;
        var columns = Columns.Select((column, index) =>
            new LibraryColumnState(column.Key, null, index, column.IsVisible)).ToArray();
        SaveNamedView(name, columns, null);
    }

    /// <summary>
    /// Saves a named view using layout details supplied by a platform-specific grid. The original
    /// parameterless command remains available to XAML shells that only expose visibility choices.
    /// </summary>
    public void SaveNamedView(
        string name,
        IReadOnlyList<LibraryColumnState> columns,
        LibrarySortState? sort)
    {
        name = name.Trim();
        if (name.Length == 0)
            return;
        var view = new LibraryViewDefinition(name, FilterText, FilterMode, columns, sort);
        LibraryViewDefinition? existing = SavedViews.FirstOrDefault(item =>
            item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            SavedViews.Remove(existing);
        SavedViews.Add(view);
        SelectedView = view;
        NewViewName = null;
        PersistViews();
    }

    [RelayCommand]
    private void DeleteView()
    {
        if (SelectedView is null)
            return;
        SavedViews.Remove(SelectedView);
        SelectedView = null;
        PersistViews();
    }

    private void QueueFilter()
    {
        _filterCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _filterCancellation = cancellation;
        _ = ApplyFilterAfterDelayAsync(cancellation);
    }

    private async Task ApplyFilterAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(180, cancellation.Token);
            await ApplyFilterAsync(immediate: false, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_filterCancellation, cancellation))
                _filterCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task ApplyFilterAsync(bool immediate, CancellationToken cancellationToken = default)
    {
        LibraryFilterQuery query = LibraryFilterQuery.Create(FilterText, FilterMode);
        FilterError = query.Error;
        if (!query.IsValid)
        {
            StatusText = query.Error ?? "Invalid filter.";
            return;
        }
        List<LibraryRow> source = _allRows;
        HashSet<string>? healthPaths = _healthFilterPaths.Count == 0
            ? null
            : new HashSet<string>(_healthFilterPaths, StringComparer.OrdinalIgnoreCase);
        List<LibraryRow> filtered = await Task.Run(() => source
            .Where(row => (healthPaths is null || healthPaths.Contains(row.Path)) &&
                query.IsMatch(row.Details, row.SearchText))
            .ToList(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        int preservedSelectionCount = 0;
        if (_inspector.HasUnsavedChanges && _selectedPaths.Count > 0)
        {
            var includedPaths = filtered.Select(row => row.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            LibraryRow[] preserved = source.Where(row =>
                _selectedPaths.Contains(row.Path, StringComparer.OrdinalIgnoreCase) &&
                includedPaths.Add(row.Path)).ToArray();
            preservedSelectionCount = preserved.Length;
            filtered.AddRange(preserved);
        }

        SelectionContext? updatedSelection = null;
        if (!_inspector.HasUnsavedChanges && _selectedPaths.Count > 0)
        {
            var selectedPaths = _selectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            LibraryRow[] visibleSelection = filtered.Where(row => selectedPaths.Contains(row.Path)).ToArray();
            if (visibleSelection.Length != _selectedPaths.Count)
            {
                SetSelectedPaths(visibleSelection.Select(row => row.Path).ToArray());
                updatedSelection = new SelectionContext(
                    visibleSelection.Select(row => row.Path).ToArray(),
                    visibleSelection.Select(row => row.Record).ToArray());
            }
        }

        // Replace the view once. Raising one collection notification per cached track makes a
        // virtualized table spend seconds processing changes on the UI thread and also starves
        // live window layout while a large library is loading or being filtered.
        Rows = filtered;
        StatusText = healthPaths is not null
            ? $"{filtered.Count:N0} Health-filtered track(s) of {source.Count:N0} total"
            : filtered.Count == source.Count
                ? $"{source.Count:N0} tracks"
                : $"{filtered.Count:N0} of {source.Count:N0} tracks";
        if (preservedSelectionCount > 0)
            StatusText += $" · {preservedSelectionCount:N0} selected with unsaved changes kept visible";
        PageState = source.Count == 0
            ? LibraryPageState.NotIndexed
            : filtered.Count > 0
                ? LibraryPageState.Ready
                : HasTextFilter
                    ? LibraryPageState.FilteredToZero
                    : healthPaths is not null
                        ? LibraryPageState.NoResults
                        : LibraryPageState.NotIndexed;
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(ResultCountText));
        if (updatedSelection is not null)
            await _inspector.LoadAsync(updatedSelection);
    }

    private void LoadViews()
    {
        try
        {
            string? json = _settings.GetPreference(ViewsPreference);
            foreach (LibraryViewDefinition view in string.IsNullOrWhiteSpace(json)
                         ? []
                         : JsonSerializer.Deserialize<List<LibraryViewDefinition>>(json) ?? [])
                SavedViews.Add(view);
        }
        catch
        {
        }
    }

    private void PersistViews()
        => _settings.SetPreference(ViewsPreference, JsonSerializer.Serialize(SavedViews));

    private void LoadWorkspace()
    {
        _loadingWorkspace = true;
        try
        {
            string? json = _settings.GetPreference(WorkspacePreference);
            if (string.IsNullOrWhiteSpace(json))
                return;
            var state = JsonSerializer.Deserialize<LibraryWorkspaceSnapshot>(json);
            if (state is not null)
            {
                FilterText = state.Filter;
                FilterMode = state.Mode;
                IsInspectorOpen = state.InspectorOpen ?? true;
            }
        }
        catch
        {
        }
        finally
        {
            _loadingWorkspace = false;
        }
    }

    private void SaveWorkspace()
        => _settings.SetPreference(WorkspacePreference,
            JsonSerializer.Serialize(new LibraryWorkspaceSnapshot(FilterText, FilterMode, IsInspectorOpen)));

    private sealed record LibraryWorkspaceSnapshot(string? Filter, FilterMode Mode, bool? InspectorOpen = null);
    private sealed record ThumbnailCacheItem(object? Image, LinkedListNode<string> Node);
}
