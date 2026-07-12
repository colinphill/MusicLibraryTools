using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MusicLibrary.App.Services;
using MusicLibrary.Core.Services;

namespace MusicLibrary.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private const string SelectedTabPreference = "MainWindow.SelectedTab";
    private readonly IThumbnailProvider _thumbnails;
    private readonly IAppSettings _settings;

    [ObservableProperty]
    private int _selectedTabIndex;

    public SettingsViewModel Settings { get; }
    public LibraryViewModel Library { get; }
    public FileInspectorViewModel Inspector { get; }
    public TagEditorViewModel Editor { get; }
    public AnalyzerViewModel Analyzer { get; }
    public OrganizeViewModel Organize { get; }
    public IngestViewModel Ingest { get; }
    public ArtworkViewModel Artwork { get; }
    public DetailsGridViewModel Table { get; }

    public MainWindowViewModel(
        SettingsViewModel settings,
        LibraryViewModel library,
        FileInspectorViewModel inspector,
        TagEditorViewModel editor,
        AnalyzerViewModel analyzer,
        OrganizeViewModel organize,
        IngestViewModel ingest,
        ArtworkViewModel artwork,
        DetailsGridViewModel table,
        IThumbnailProvider thumbnails,
        IAppSettings appSettings)
    {
        _thumbnails = thumbnails;
        _settings = appSettings;
        if (int.TryParse(appSettings.GetPreference(SelectedTabPreference), NumberStyles.None,
                CultureInfo.InvariantCulture, out int selectedTab) && selectedTab is >= 0 and <= 6)
            _selectedTabIndex = selectedTab;
        Settings = settings;
        Library = library;
        Inspector = inspector;
        Editor = editor;
        Analyzer = analyzer;
        Organize = organize;
        Ingest = ingest;
        Artwork = artwork;
        Table = table;

        // Selection lives entirely in the details grid now. The grid's multi-selection drives the
        // editor / inspector / artwork panes; an analyzer finding steers the grid to highlight a row.
        Analyzer.OpenRequested += Open;
        // Re-scan finished → refresh the grid so new/changed files show up.
        Library.IndexCompleted += () => _ = Table.ReloadAsync();
        // Organize already re-syncs the cache to the moves; refresh the grid to show the new locations.
        Organize.MovesApplied += () => _ = Table.ReloadAsync();
        Ingest.IngestCompleted += () => _ = Table.ReloadAsync();
        Editor.TagsChanged += async _ =>
        {
            await Table.ReloadAsync();
            await Inspector.ReloadAsync();
        };
        // Editing artwork (possibly across a whole album) refreshes the details view + grid thumbnails.
        Artwork.ArtworkChanged += async affected =>
        {
            foreach (var p in affected)
                _thumbnails.Invalidate(p);
            await Table.ReloadAsync();
            await Inspector.ReloadAsync();
        };
    }

    partial void OnSelectedTabIndexChanged(int value)
        => _settings.SetPreference(SelectedTabPreference, value.ToString(CultureInfo.InvariantCulture));

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
