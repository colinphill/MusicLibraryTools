using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

public partial class LibraryViewModel : ObservableObject
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
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _filterCancellation;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReloadCommand))]
    private bool _isBusy;

    [ObservableProperty]
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
    private IReadOnlyList<LibraryRow> _rows = [];

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
        settings.ConfigurationChanged += (_, _) => _ = ReloadAsync();
        indexing.IndexCompleted += () => _ = ReloadAsync();
        inspector.FilesChanged += () => _ = ReloadAsync();
    }

    public ObservableCollection<LibraryViewDefinition> SavedViews { get; } = [];
    public ObservableCollection<LibraryColumnChoice> Columns { get; } = [];
    public IReadOnlyList<FilterMode> FilterModes { get; } = Enum.GetValues<FilterMode>();
    public SelectionInspectorViewModel Inspector => _inspector;
    public IndexingViewModel Indexing { get; }
    public int TotalCount => _allRows.Count;
    public bool HasFilterError => !string.IsNullOrWhiteSpace(FilterError);

    partial void OnFilterTextChanged(string? value)
    {
        SaveWorkspace();
        QueueFilter();
    }

    partial void OnFilterModeChanged(FilterMode value)
    {
        SaveWorkspace();
        QueueFilter();
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
            StatusText = "Choose a library configuration in Settings.";
            return;
        }
        IsBusy = true;
        StatusText = "Loading the cached library…";
        try
        {
            var records = await _library.GetAllRecordsAsync(cancellation.Token);
            var rows = await Task.Run(() => records.Select(record => new LibraryRow(record)).ToList(), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _allRows = rows;
            await ApplyFilterAsync(immediate: true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            StatusText = $"Could not load the cached library: {error.Message}";
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

    public async Task SelectAsync(IReadOnlyList<string> paths)
        => await _inspector.LoadAsync(new SelectionContext(paths));

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
        var view = new LibraryViewDefinition(name, FilterText, FilterMode, columns, null);
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
        List<LibraryRow> filtered = await Task.Run(() => source
            .Where(row => query.IsMatch(row.Details, row.SearchText))
            .ToList(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Replace the view once. Raising one collection notification per cached track makes a
        // virtualized table spend seconds processing changes on the UI thread and also starves
        // live window layout while a large library is loading or being filtered.
        Rows = filtered;
        StatusText = filtered.Count == source.Count
            ? $"{source.Count:N0} tracks"
            : $"{filtered.Count:N0} of {source.Count:N0} tracks";
        OnPropertyChanged(nameof(TotalCount));
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
            }
        }
        catch
        {
        }
    }

    private void SaveWorkspace()
        => _settings.SetPreference(WorkspacePreference,
            JsonSerializer.Serialize(new LibraryWorkspaceSnapshot(FilterText, FilterMode)));

    private sealed record LibraryWorkspaceSnapshot(string? Filter, FilterMode Mode);
    private sealed record ThumbnailCacheItem(object? Image, LinkedListNode<string> Node);
}
