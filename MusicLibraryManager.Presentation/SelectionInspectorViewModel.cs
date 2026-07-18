using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

public partial class SelectionInspectorViewModel : ObservableObject
{
    private const int MaxCommonValueSample = 200;
    private readonly IMediaFileService _media;
    private readonly ITagWriteService _tags;
    private readonly IArtworkService _artwork;
    private readonly IFilePickerService _files;
    private readonly IDialogCoordinator _dialogs;
    private readonly IFieldsEditorService _fieldsEditor;
    private readonly IThumbnailService _thumbnails;
    private readonly IActivityService _activities;
    private int _generation;
    private CancellationTokenSource? _cancellation;

    [ObservableProperty] private SelectionContext _selection = SelectionContext.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _overview = "Select a track to inspect its metadata.";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string? _statusMessage;
    [ObservableProperty] private object? _artworkSource;
    [ObservableProperty] private string _artworkSummary = "No artwork loaded.";
    [ObservableProperty] private int _artworkMaxDimension = 1000;

    public SelectionInspectorViewModel(
        IMediaFileService media,
        ITagWriteService tags,
        IArtworkService artwork,
        IFilePickerService files,
        IDialogCoordinator dialogs,
        IFieldsEditorService fieldsEditor,
        IThumbnailService thumbnails,
        IActivityService activities)
    {
        _media = media;
        _tags = tags;
        _artwork = artwork;
        _files = files;
        _dialogs = dialogs;
        _fieldsEditor = fieldsEditor;
        _thumbnails = thumbnails;
        _activities = activities;
        foreach (var (field, label) in FieldDefinitions)
            Fields.Add(new EditableTagField(field, label));
    }

    public ObservableCollection<EditableTagField> Fields { get; } = [];
    public bool HasSelection => Selection.HasSelection;
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public string SelectionSummary => Selection.Summary;
    public event Action? FilesChanged;

    private static readonly (TagFields Field, string Label)[] FieldDefinitions =
    [
        (TagFields.Title, "Title"),
        (TagFields.Artist, "Artist"),
        (TagFields.AlbumArtist, "Album artist"),
        (TagFields.Album, "Album"),
        (TagFields.TrackNumber, "Track"),
        (TagFields.TotalTracks, "Track total"),
        (TagFields.DiscNumber, "Disc"),
        (TagFields.TotalDiscs, "Disc total"),
        (TagFields.Date, "Release date"),
        (TagFields.Genre, "Genre"),
        (TagFields.Composer, "Composer"),
        (TagFields.Comment, "Comment"),
    ];

    public async Task LoadAsync(SelectionContext selection)
    {
        int generation = ++_generation;
        _cancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        Selection = selection;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionSummary));
        StatusMessage = null;
        ArtworkSource = null;
        ArtworkSummary = "No embedded artwork.";
        NotifyCommands();

        if (!selection.HasSelection)
        {
            Overview = "Select a track to inspect its metadata.";
            foreach (EditableTagField field in Fields)
                field.SetLoaded(null, false);
            return;
        }

        IsBusy = true;
        NotifyCommands();
        try
        {
            IReadOnlyList<string> sample = selection.Paths.Count > MaxCommonValueSample
                ? selection.Paths.Take(MaxCommonValueSample).ToArray()
                : selection.Paths;
            var models = new List<MediaFileModel>();
            foreach (string path in sample)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                OperationResult<MediaFileModel> result = await _media.LoadAsync(path, includeArtwork: false, cancellation.Token);
                if (result.Success && result.Value is not null)
                    models.Add(result.Value);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation != _generation)
                return;

            LoadFields(models);
            Overview = DescribeOverview(models.FirstOrDefault(), selection.Paths.Count);

            OperationResult<MediaFileModel> artwork = await _media.LoadAsync(
                selection.Paths[0], includeArtwork: true, cancellation.Token);
            if (generation != _generation)
                return;
            ArtworkModel? first = artwork.Value?.Artwork.FirstOrDefault();
            if (first is not null)
            {
                ArtworkSource = await _thumbnails.CreateImageSourceAsync(
                    first.Data, cancellationToken: cancellation.Token);
                ArtworkSummary = $"{first.ImageType ?? "image"} · {first.Width:N0} × {first.Height:N0} · {FormatBytes(first.Size)}";
                if (selection.Paths.Count > 1)
                    ArtworkSummary += $" · previewing the first of {selection.Paths.Count:N0} tracks";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            StatusMessage = error.Message;
        }
        finally
        {
            if (generation == _generation)
            {
                _cancellation = null;
                IsBusy = false;
                NotifyCommands();
            }
            cancellation.Dispose();
        }
    }

    private void LoadFields(IReadOnlyList<MediaFileModel> models)
    {
        var maps = models.Select(model => model.KnownFields
            .GroupBy(value => value.Field)
            .ToDictionary(group => group.Key, group => group.First().Value)).ToArray();
        foreach (EditableTagField field in Fields)
        {
            string[] values = maps.Select(map => map.GetValueOrDefault(field.Field) ?? "")
                .Distinct(StringComparer.Ordinal).ToArray();
            field.SetLoaded(values.Length == 1 ? values[0] : null, values.Length > 1);
        }
    }

    private bool CanEdit() => HasSelection && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task SaveTagsAsync()
    {
        TagEdit[] edits = Fields.Where(field => field.IsModified)
            .Select(field => new TagEdit(field.Field, string.IsNullOrWhiteSpace(field.Value) ? null : field.Value))
            .ToArray();
        if (edits.Length == 0)
        {
            StatusMessage = "No tag changes to save.";
            return;
        }
        IsBusy = true;
        NotifyCommands();
        Guid activity = _activities.Start("Save tags", $"Updating {Selection.Paths.Count:N0} track(s)");
        try
        {
            BatchWriteResult result = await _tags.ApplyAsync(Selection.Paths, edits);
            StatusMessage = result.Summary;
            _activities.Finish(activity, result.Summary,
                result.FailedCount > 0 ? AppActivityState.Failed : AppActivityState.Completed);
            if (result.SavedCount > 0)
                FilesChanged?.Invoke();
            await LoadAsync(Selection);
        }
        catch (Exception error)
        {
            StatusMessage = error.Message;
            _activities.Finish(activity, error.Message, AppActivityState.Failed);
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task EditAllFieldsAsync()
    {
        if (!await _fieldsEditor.ShowAsync(Selection.Paths))
            return;
        StatusMessage = "Metadata fields updated.";
        FilesChanged?.Invoke();
        await LoadAsync(Selection);
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task ReplaceArtworkAsync()
    {
        string? path = await _files.PickFileAsync("Choose cover artwork",
            [new FilePickerType("Images", [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"])]);
        if (path is null)
            return;
        if (!await _dialogs.ConfirmAsync("Replace artwork",
                $"Replace the front cover on {Selection.Paths.Count:N0} selected track(s)?",
                "Replace"))
            return;
        await ApplyArtworkAsync("Replace artwork", async musicPath =>
            await _artwork.SetCoverFromFileAsync(musicPath, path, ArtworkMaxDimension));
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task ScrubArtworkAsync()
    {
        if (!await _dialogs.ConfirmAsync("Optimize artwork",
                $"Re-encode and limit artwork to {ArtworkMaxDimension:N0}px on {Selection.Paths.Count:N0} track(s)?",
                "Optimize"))
            return;
        await ApplyArtworkAsync("Optimize artwork", async musicPath =>
            await _artwork.ScrubAsync(musicPath, ArtworkMaxDimension));
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task RemoveArtworkAsync()
    {
        if (!await _dialogs.ConfirmAsync("Remove artwork",
                $"Remove all embedded artwork from {Selection.Paths.Count:N0} selected track(s)?",
                "Remove"))
            return;
        await ApplyArtworkAsync("Remove artwork", async musicPath =>
            await _artwork.RemoveAsync(musicPath));
    }

    [RelayCommand]
    private async Task RevertAsync() => await LoadAsync(Selection);

    private async Task ApplyArtworkAsync(string title, Func<string, Task<ArtworkOpResult>> apply)
    {
        IsBusy = true;
        NotifyCommands();
        Guid activity = _activities.Start(title, $"Updating {Selection.Paths.Count:N0} track(s)");
        int saved = 0;
        string? firstError = null;
        try
        {
            foreach (string path in Selection.Paths)
            {
                ArtworkOpResult result = await apply(path);
                if (result.Success)
                    saved++;
                else
                    firstError ??= result.Error;
                _activities.Report(activity, $"{saved:N0} of {Selection.Paths.Count:N0} updated",
                    (double)saved / Selection.Paths.Count);
            }
            StatusMessage = saved == Selection.Paths.Count
                ? $"Updated artwork on {saved:N0} track(s)."
                : $"Updated {saved:N0} of {Selection.Paths.Count:N0}. {firstError}";
            _activities.Finish(activity, StatusMessage,
                saved == Selection.Paths.Count ? AppActivityState.Completed : AppActivityState.Failed);
            if (saved > 0)
                FilesChanged?.Invoke();
            await LoadAsync(Selection);
        }
        catch (Exception error)
        {
            StatusMessage = error.Message;
            _activities.Finish(activity, error.Message, AppActivityState.Failed);
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private void NotifyCommands()
    {
        SaveTagsCommand.NotifyCanExecuteChanged();
        EditAllFieldsCommand.NotifyCanExecuteChanged();
        ReplaceArtworkCommand.NotifyCanExecuteChanged();
        ScrubArtworkCommand.NotifyCanExecuteChanged();
        RemoveArtworkCommand.NotifyCanExecuteChanged();
    }

    private static string DescribeOverview(MediaFileModel? model, int count)
    {
        if (model is null)
            return "Metadata could not be loaded for this selection.";
        string scope = count == 1 ? model.Path : $"{count:N0} tracks · showing representative metadata";
        string codec = model.Codec is null ? "Unknown codec" :
            $"{model.Codec.CodecName} · {model.Codec.Samplerate:N0} Hz · {model.Codec.BitsPerSample:N0}-bit · {model.Codec.Channels:N0} ch";
        string tagFormat = model.TagType switch
        {
            null or "" => "Unknown tag format",
            "Vorbis" => "Vorbis comments",
            string value => value,
        };
        return $"{scope}{Environment.NewLine}{codec}{Environment.NewLine}Tag format: {tagFormat}";
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:N1} MB",
        >= 1024 => $"{bytes / 1024d:N0} KB",
        _ => $"{bytes:N0} bytes",
    };
}
