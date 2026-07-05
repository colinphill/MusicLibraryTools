using MusicLibrary.App.Services;

namespace MusicLibrary.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IThumbnailProvider _thumbnails;

    public SettingsViewModel Settings { get; }
    public LibraryViewModel Library { get; }
    public FileInspectorViewModel Inspector { get; }
    public TagEditorViewModel Editor { get; }
    public AnalyzerViewModel Analyzer { get; }
    public ArtistsViewModel Artists { get; }
    public OrganizeViewModel Organize { get; }
    public ArtworkViewModel Artwork { get; }
    public DetailsGridViewModel Table { get; }

    public MainWindowViewModel(
        SettingsViewModel settings,
        LibraryViewModel library,
        FileInspectorViewModel inspector,
        TagEditorViewModel editor,
        AnalyzerViewModel analyzer,
        ArtistsViewModel artists,
        OrganizeViewModel organize,
        ArtworkViewModel artwork,
        DetailsGridViewModel table,
        IThumbnailProvider thumbnails)
    {
        _thumbnails = thumbnails;
        Settings = settings;
        Library = library;
        Inspector = inspector;
        Editor = editor;
        Analyzer = analyzer;
        Artists = artists;
        Organize = organize;
        Artwork = artwork;
        Table = table;

        // Selection lives entirely in the details grid now. The grid's multi-selection drives the
        // editor / inspector / artwork panes; an analyzer finding steers the grid to highlight a row.
        Analyzer.OpenRequested += Open;
        // Re-scan finished → refresh the grid so new/changed files show up.
        Library.IndexCompleted += () => _ = Table.ReloadAsync();
        // Organize already re-syncs the cache to the moves; refresh the grid to show the new locations.
        Organize.MovesApplied += () => _ = Table.ReloadAsync();
        // Editing artwork (possibly across a whole album) refreshes the details view + grid thumbnails.
        Artwork.ArtworkChanged += async affected =>
        {
            foreach (var p in affected)
                _thumbnails.Invalidate(p);
            await Inspector.ReloadAsync();
        };
    }

    private int _selectionGen;

    /// <summary>Raised to ask the view to select these paths' rows in the details grid.</summary>
    public event Action<IReadOnlyList<string>>? SelectGridRequested;

    /// <summary>Called by the view when the grid's multi-selection changes.</summary>
    public void OnGridSelectionChanged(IReadOnlyList<string> paths) => _ = ApplySelection(paths);

    // Open a single path (from an analyzer finding): highlight it in the grid and load its details.
    private void Open(string path)
    {
        SelectGridRequested?.Invoke([path]);
        _ = ApplySelection([path]);
    }

    // Fan the selection out to the editor / inspector / artwork panes. A rapid new selection bumps the
    // generation so a superseded call stops before touching the panes; each pane also self-supersedes.
    private async Task ApplySelection(IReadOnlyList<string> paths)
    {
        var gen = ++_selectionGen;

        await Editor.SetTargetsAsync(paths);
        if (gen != _selectionGen)
            return;
        await Inspector.LoadFromPathsAsync(paths);
        if (gen != _selectionGen)
            return;
        await Artwork.SetTargetsAsync(paths);
    }

    /// <summary>Called once the UI is up so we can restore the last-used configuration.</summary>
    public void OnLoaded()
    {
        Settings.RestoreRememberedConfig();
    }
}
