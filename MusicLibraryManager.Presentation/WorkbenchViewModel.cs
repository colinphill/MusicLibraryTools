using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

public enum WorkbenchOperationKind
{
    Assign,
    Remove,
    Copy,
    ReplaceText,
    ChangeCase,
    TrimWhitespace,
    Sequence,
}

public sealed record MetadataFieldChoice(TagFields Field, string Label);

public sealed record MetadataPreviewRow(
    string File,
    string Field,
    string Before,
    string After);

public partial class WorkbenchViewModel : ObservableObject, INavigationGuard
{
    private const string RecentLocationsPreference = "manager.workbench.recentLocations.v1";
    private const int RecentLocationLimit = 12;
    private readonly IWorkbenchService _workbench;
    private readonly IMetadataOperationService _operations;
    private readonly IEditHistoryService _history;
    private readonly IFilePickerService _files;
    private readonly IDialogCoordinator _dialogs;
    private readonly IAppSettings _settings;
    private MetadataOperationPlan? _plan;
    private CancellationTokenSource? _cancellation;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BrowseFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowseFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewEditsCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewOperationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCurrentCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    private WorkbenchTrackViewModel? _selectedFile;

    [ObservableProperty]
    private bool _recursive = true;

    [ObservableProperty]
    private string _statusText =
        "Add files, folders, playlists, or cuesheets. No library configuration is required.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _hasApplicablePreview;

    [ObservableProperty]
    private WorkbenchOperationKind _selectedOperationKind = WorkbenchOperationKind.Assign;

    [ObservableProperty]
    private MetadataFieldChoice? _selectedField;

    [ObservableProperty]
    private MetadataFieldChoice? _destinationField;

    [ObservableProperty]
    private string? _operationValue;

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private string? _replacementText;

    [ObservableProperty]
    private bool _useRegularExpression;

    [ObservableProperty]
    private MetadataCaseMode _selectedCaseMode = MetadataCaseMode.Title;

    [ObservableProperty]
    private int _sequenceStart = 1;

    [ObservableProperty]
    private int _sequencePadding = 2;

    public WorkbenchViewModel(
        IWorkbenchService workbench,
        IMetadataOperationService operations,
        IEditHistoryService history,
        IFilePickerService files,
        IDialogCoordinator dialogs,
        IAppSettings settings)
    {
        _workbench = workbench;
        _operations = operations;
        _history = history;
        _files = files;
        _dialogs = dialogs;
        _settings = settings;
        Fields =
        [
            new(TagFields.Title, "Title"),
            new(TagFields.Artist, "Artist"),
            new(TagFields.AlbumArtist, "Album artist"),
            new(TagFields.Album, "Album"),
            new(TagFields.Genre, "Genre"),
            new(TagFields.Composer, "Composer"),
            new(TagFields.Date, "Date"),
            new(TagFields.TrackNumber, "Track"),
            new(TagFields.TotalTracks, "Track total"),
            new(TagFields.DiscNumber, "Disc"),
            new(TagFields.TotalDiscs, "Disc total"),
            new(TagFields.Comment, "Comment"),
        ];
        SelectedField = Fields[0];
        DestinationField = Fields[1];
        LoadRecentLocations();
    }

    public ObservableCollection<WorkbenchTrackViewModel> Files { get; } = [];
    public ObservableCollection<MetadataPreviewRow> PreviewChanges { get; } = [];
    public ObservableCollection<string> RecentLocations { get; } = [];
    public IReadOnlyList<MetadataFieldChoice> Fields { get; }
    public IReadOnlyList<WorkbenchOperationKind> OperationKinds { get; } =
        Enum.GetValues<WorkbenchOperationKind>();
    public IReadOnlyList<MetadataCaseMode> CaseModes { get; } =
        Enum.GetValues<MetadataCaseMode>();
    public bool HasFiles => Files.Count > 0;
    public bool HasPreview => PreviewChanges.Count > 0;
    public bool HasUnsavedChanges =>
        _plan is not null || Files.Any(file => file.HasChanges);
    public bool CanUndoLatest => _history.CanUndo && !IsBusy;

    partial void OnSelectedFileChanged(WorkbenchTrackViewModel? value)
    {
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private async Task BrowseFilesAsync()
    {
        IReadOnlyList<string> paths = await _files.PickFilesAsync(
            "Add media, playlists, or cuesheets",
            [new("Supported sources",
                [".mp3", ".flac", ".ogg", ".wv", ".m4a", ".mp4", ".m4p", ".m4r",
                 ".dsf", ".m3u", ".m3u8", ".cue"])]);
        if (paths.Count > 0)
            await AddSourcesAsync(paths);
    }

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private async Task BrowseFolderAsync()
    {
        string? path = await _files.PickFolderAsync("Add a media folder");
        if (path is not null)
            await AddSourcesAsync([path]);
    }

    [RelayCommand]
    private async Task AddRecentAsync(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            await AddSourcesAsync([path]);
    }

    public async Task AddSourcesAsync(IReadOnlyList<string> sources)
    {
        if (sources.Count == 0 || IsBusy)
            return;
        CancelPlan();
        IsBusy = true;
        _cancellation = new();
        try
        {
            WorkbenchLoadResult loaded = await _workbench.LoadAsync(
                new(sources, Recursive), _cancellation.Token);
            var existing = Files.Select(file => file.Path)
                .ToHashSet(PathComparer);
            int added = 0;
            foreach (MediaDocument document in loaded.Documents)
            {
                if (!existing.Add(document.Path))
                    continue;
                AddTrack(new(document));
                added++;
            }
            foreach (string source in sources)
                AddRecentLocation(source);
            StatusText = loaded.Issues.Length == 0
                ? $"Added {added:N0} file(s); {Files.Count:N0} in this Workbench session."
                : $"Added {added:N0} file(s) with {loaded.Issues.Length:N0} warning(s): " +
                  loaded.Issues[0].Message;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Loading cancelled.";
        }
        catch (Exception error)
        {
            StatusText = $"Could not load the selected sources: {error.Message}";
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            IsBusy = false;
            NotifySessionChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveCurrent))]
    private void RemoveCurrent()
    {
        if (SelectedFile is not { } current)
            return;
        int index = Files.IndexOf(current);
        current.PropertyChanged -= OnTrackChanged;
        Files.Remove(current);
        SelectedFile = Files.Count == 0
            ? null
            : Files[Math.Min(index, Files.Count - 1)];
        CancelPlan();
        StatusText = $"{Files.Count:N0} file(s) in this Workbench session.";
        NotifySessionChanged();
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (Files.Count == 0)
            return;
        if (HasUnsavedChanges && !await _dialogs.ConfirmAsync(
                "Clear Workbench?",
                "This removes the current session and its uncommitted edits. Files on disk are not changed.",
                "Clear"))
            return;
        foreach (WorkbenchTrackViewModel file in Files)
            file.PropertyChanged -= OnTrackChanged;
        Files.Clear();
        PreviewChanges.Clear();
        _plan = null;
        SelectedFile = null;
        StatusText = "Workbench cleared. Files on disk were not changed.";
        NotifySessionChanged();
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp()
    {
        int index = SelectedFile is null ? -1 : Files.IndexOf(SelectedFile);
        if (index <= 0)
            return;
        Files.Move(index, index - 1);
        CancelPlan();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown()
    {
        int index = SelectedFile is null ? -1 : Files.IndexOf(SelectedFile);
        if (index < 0 || index >= Files.Count - 1)
            return;
        Files.Move(index, index + 1);
        CancelPlan();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanPreviewEdits))]
    private async Task PreviewEditsAsync()
    {
        var edits = Files.Where(file => file.HasChanges)
            .ToDictionary(
                file => file.Path,
                file => (IReadOnlyList<TagEdit>)file.CreateEdits(),
                PathComparer);
        if (edits.Count == 0)
            return;
        await PreviewAsync(() => _operations.PreviewEditsAsync(
            edits, "Workbench field edits"));
    }

    [RelayCommand(CanExecute = nameof(CanPreviewOperation))]
    private async Task PreviewOperationAsync()
    {
        MetadataOperation operation = CreateOperation();
        OperationRecipe recipe = OperationRecipe.Create(
            $"{SelectedOperationKind}: {SelectedField!.Label}", operation);
        await PreviewAsync(() => _operations.PreviewAsync(
            Files.Select(file => file.Path).ToArray(), recipe));
    }

    private async Task PreviewAsync(Func<Task<MetadataOperationPlan>> action)
    {
        IsBusy = true;
        try
        {
            MetadataOperationPlan plan = await action();
            _plan = plan;
            PreviewChanges.Clear();
            foreach (MetadataFilePlan file in plan.Files)
            foreach (MetadataFieldDifference difference in file.Differences)
                PreviewChanges.Add(new(
                    Path.GetFileName(file.Path),
                    difference.Field.DisplayName,
                    string.Join("; ", difference.Before),
                    string.Join("; ", difference.After)));
            HasApplicablePreview = plan.CanApply;
            int blockers = plan.Files.SelectMany(file => file.Issues)
                .Count(issue => issue.Severity == OperationIssueSeverity.Blocker);
            StatusText = blockers > 0
                ? $"Preview has {blockers:N0} blocker(s). No files have been changed."
                : $"Preview: {plan.ChangeCount:N0} field change(s) in " +
                  $"{plan.ChangedFileCount:N0} file(s). No files have been changed.";
        }
        catch (Exception error)
        {
            CancelPlan();
            StatusText = $"Preview failed: {error.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifySessionChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (_plan is null)
            return;
        IsBusy = true;
        try
        {
            MetadataApplyResult result = await _operations.ApplyAsync(_plan);
            string[] paths = _plan.Files.Where(file => file.HasChanges)
                .Select(file => file.Path).ToArray();
            await ReloadAsync(paths);
            _plan = null;
            PreviewChanges.Clear();
            HasApplicablePreview = false;
            StatusText = $"Applied {result.ChangedFiles:N0} file(s). Originals are retained " +
                "in Workbench recovery and can be restored from Operations.";
        }
        catch (Exception error)
        {
            StatusText = $"Apply stopped safely: {error.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifySessionChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (!await _dialogs.ConfirmAsync(
                "Restore the last Workbench operation?",
                "Current files will be replaced by the retained originals. A collision backup is created when required.",
                "Restore"))
            return;
        IsBusy = true;
        try
        {
            int restored = await _history.UndoLatestAsync();
            await ReloadAsync(Files.Select(file => file.Path).ToArray());
            StatusText = $"Restored {restored:N0} file(s) from the latest Workbench operation.";
        }
        catch (Exception error)
        {
            StatusText = $"Restore failed: {error.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifySessionChanged();
        }
    }

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    public Task<bool> ConfirmNavigationAsync()
    {
        if (!HasUnsavedChanges)
            return Task.FromResult(true);
        return _dialogs.ConfirmAsync(
            "Leave the Workbench?",
            "The current preview and inline edits remain in this session, but have not been applied to disk.",
            "Leave");
    }

    private MetadataOperation CreateOperation()
    {
        MetadataFieldKey field = MetadataFieldKey.Known(SelectedField!.Field);
        return SelectedOperationKind switch
        {
            WorkbenchOperationKind.Assign =>
                new AssignFieldOperation(field, OperationValue ?? ""),
            WorkbenchOperationKind.Remove =>
                new RemoveFieldOperation(field),
            WorkbenchOperationKind.Copy =>
                new CopyFieldOperation(
                    field,
                    MetadataFieldKey.Known(
                        DestinationField?.Field ?? TagFields.Title)),
            WorkbenchOperationKind.ReplaceText =>
                new ReplaceTextOperation(
                    field,
                    SearchText ?? "",
                    ReplacementText ?? "",
                    UseRegularExpression),
            WorkbenchOperationKind.ChangeCase =>
                new ChangeCaseOperation(field, SelectedCaseMode),
            WorkbenchOperationKind.TrimWhitespace =>
                new TrimFieldOperation(field, NormalizeInternalWhitespace: true),
            WorkbenchOperationKind.Sequence =>
                new SequenceNumberOperation(
                    field,
                    Math.Max(0, SequenceStart),
                    PadWidth: Math.Max(0, SequencePadding)),
            _ => throw new NotSupportedException(
                $"Unsupported Workbench operation '{SelectedOperationKind}'."),
        };
    }

    private async Task ReloadAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;
        WorkbenchLoadResult loaded = await _workbench.LoadAsync(
            new(paths, Recursive: false));
        var documents = loaded.Documents.ToDictionary(document => document.Path, PathComparer);
        for (int index = 0; index < Files.Count; index++)
        {
            WorkbenchTrackViewModel previous = Files[index];
            if (!documents.TryGetValue(previous.Path, out MediaDocument? document))
                continue;
            previous.PropertyChanged -= OnTrackChanged;
            WorkbenchTrackViewModel replacement = new(document);
            replacement.PropertyChanged += OnTrackChanged;
            Files[index] = replacement;
            if (ReferenceEquals(SelectedFile, previous))
                SelectedFile = replacement;
        }
    }

    private void AddTrack(WorkbenchTrackViewModel track)
    {
        track.PropertyChanged += OnTrackChanged;
        Files.Add(track);
        SelectedFile ??= track;
    }

    private void OnTrackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkbenchTrackViewModel.HasChanges))
        {
            CancelPlan();
            NotifySessionChanged();
        }
    }

    private void CancelPlan()
    {
        _plan = null;
        PreviewChanges.Clear();
        HasApplicablePreview = false;
    }

    private void NotifySessionChanged()
    {
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(CanUndoLatest));
        PreviewEditsCommand.NotifyCanExecuteChanged();
        PreviewOperationCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RemoveCurrentCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    private void LoadRecentLocations()
    {
        try
        {
            string? json = _settings.GetPreference(RecentLocationsPreference);
            foreach (string path in string.IsNullOrWhiteSpace(json)
                         ? []
                         : JsonSerializer.Deserialize<string[]>(json) ?? [])
                if (File.Exists(path) || Directory.Exists(path))
                    RecentLocations.Add(path);
        }
        catch { }
    }

    private void AddRecentLocation(string path)
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch { return; }
        string? previous = RecentLocations.FirstOrDefault(item =>
            PathComparer.Equals(item, fullPath));
        if (previous is not null)
            RecentLocations.Remove(previous);
        RecentLocations.Insert(0, fullPath);
        while (RecentLocations.Count > RecentLocationLimit)
            RecentLocations.RemoveAt(RecentLocations.Count - 1);
        try
        {
            _settings.SetPreference(
                RecentLocationsPreference,
                JsonSerializer.Serialize(RecentLocations.ToArray()));
        }
        catch { }
    }

    private bool CanBrowse() => !IsBusy;
    private bool CanRemoveCurrent() => !IsBusy && SelectedFile is not null;
    private bool CanMoveUp() =>
        !IsBusy && SelectedFile is not null && Files.IndexOf(SelectedFile) > 0;
    private bool CanMoveDown() =>
        !IsBusy && SelectedFile is not null &&
        Files.IndexOf(SelectedFile) is var index && index >= 0 && index < Files.Count - 1;
    private bool CanPreviewEdits() =>
        !IsBusy && Files.Any(file => file.HasChanges);
    private bool CanPreviewOperation() =>
        !IsBusy && Files.Count > 0 && SelectedField is not null;
    private bool CanApply() => !IsBusy && HasApplicablePreview && _plan is not null;
    private bool CanUndo() => !IsBusy && _history.CanUndo;

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

public partial class WorkbenchTrackViewModel : ObservableObject
{
    private readonly Dictionary<TagFields, string?> _original;

    public WorkbenchTrackViewModel(MediaDocument document)
    {
        Document = document;
        _title = document.FirstValue(TagFields.Title);
        _artist = document.FirstValue(TagFields.Artist);
        _albumArtist = document.FirstValue(TagFields.AlbumArtist);
        _album = document.FirstValue(TagFields.Album);
        _genre = document.FirstValue(TagFields.Genre);
        _composer = document.FirstValue(TagFields.Composer);
        _date = document.FirstValue(TagFields.Date);
        _track = document.FirstValue(TagFields.TrackNumber);
        _disc = document.FirstValue(TagFields.DiscNumber);
        _original = CurrentValues();
    }

    public MediaDocument Document { get; }
    public string Path => Document.Path;
    public string FileName => System.IO.Path.GetFileName(Path);
    public string? Format => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
    public string Duration => Document.Codec is null
        ? ""
        : TimeSpan.FromSeconds(Document.Codec.DurationInSeconds).ToString(@"h\:mm\:ss");
    public string Bitrate => Document.Codec?.AverageBitrate is > 0
        ? $"{Document.Codec.AverageBitrate / 1000:N0} kbps"
        : "";
    public string LayerSummary => string.Join(", ",
        Document.TagLayers.Select(layer => layer.TagType));
    public int ArtworkCount => Document.Artwork.Length;
    public int FieldCount => Document.TagLayers.Sum(layer => layer.Fields.Length);

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChanges))]
    private string? _title;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChanges))]
    private string? _artist;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChanges))]
    private string? _albumArtist;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChanges))]
    private string? _album;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChanges))]
    private string? _genre;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChanges))]
    private string? _composer;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChanges))]
    private string? _date;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChanges))]
    private string? _track;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChanges))]
    private string? _disc;

    public bool HasChanges => _original.Any(pair =>
        !StringComparer.Ordinal.Equals(pair.Value, Value(pair.Key)));

    public ImmutableArray<TagEdit> CreateEdits() => _original
        .Where(pair => !StringComparer.Ordinal.Equals(pair.Value, Value(pair.Key)))
        .Select(pair => new TagEdit(pair.Key,
            string.IsNullOrWhiteSpace(Value(pair.Key)) ? null : Value(pair.Key)))
        .ToImmutableArray();

    private Dictionary<TagFields, string?> CurrentValues() => new()
    {
        [TagFields.Title] = Title,
        [TagFields.Artist] = Artist,
        [TagFields.AlbumArtist] = AlbumArtist,
        [TagFields.Album] = Album,
        [TagFields.Genre] = Genre,
        [TagFields.Composer] = Composer,
        [TagFields.Date] = Date,
        [TagFields.TrackNumber] = Track,
        [TagFields.DiscNumber] = Disc,
    };

    private string? Value(TagFields field) => field switch
    {
        TagFields.Title => Title,
        TagFields.Artist => Artist,
        TagFields.AlbumArtist => AlbumArtist,
        TagFields.Album => Album,
        TagFields.Genre => Genre,
        TagFields.Composer => Composer,
        TagFields.Date => Date,
        TagFields.TrackNumber => Track,
        TagFields.DiscNumber => Disc,
        _ => null,
    };
}
