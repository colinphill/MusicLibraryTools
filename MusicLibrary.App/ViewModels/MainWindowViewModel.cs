namespace MusicLibrary.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
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
        DetailsGridViewModel table)
    {
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
        // Editing artwork should refresh the read-only details view.
        Artwork.ArtworkChanged += async () =>
        {
            if (Inspector.FilePath is { } p)
                await Inspector.LoadFromPathAsync(p);
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
        var focused = paths.Count > 0 ? paths[0] : null;

        await Editor.SetTargetsAsync(paths);
        if (gen != _selectionGen)
            return;
        if (focused is not null)
            await Inspector.LoadFromPathAsync(focused);
        if (gen != _selectionGen)
            return;
        await Artwork.SetTargetAsync(focused);
    }

    /// <summary>Called once the UI is up so we can restore the last-used configuration.</summary>
    public void OnLoaded()
    {
        Settings.RestoreRememberedConfig();
    }
}
