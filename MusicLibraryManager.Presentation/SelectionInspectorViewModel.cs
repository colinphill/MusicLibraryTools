using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private readonly ILibraryService _library;
    private readonly ITagWriteService _tags;
    private readonly IArtworkService _artwork;
    private readonly IFilePickerService _files;
    private readonly IDialogCoordinator _dialogs;
    private readonly IFieldsEditorService _fieldsEditor;
    private readonly IThumbnailService _thumbnails;
    private readonly IActivityService _activities;
    private int _generation;
    private CancellationTokenSource? _cancellation;
    private bool _artworkSetModified;

    [ObservableProperty] private SelectionContext _selection = SelectionContext.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _overview = "Select a track to inspect its metadata.";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string? _statusMessage;
    [ObservableProperty] private object? _artworkSource;
    [ObservableProperty] private bool _isArtworkMixed;
    [ObservableProperty] private string _artworkSummary = "No artwork loaded.";
    [ObservableProperty] private int _artworkMaxDimension = 1000;

    public SelectionInspectorViewModel(
        IMediaFileService media,
        ILibraryService library,
        ITagWriteService tags,
        IArtworkService artwork,
        IFilePickerService files,
        IDialogCoordinator dialogs,
        IFieldsEditorService fieldsEditor,
        IThumbnailService thumbnails,
        IActivityService activities)
    {
        _media = media;
        _library = library;
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
    public ObservableCollection<ArtworkPreviewItem> ArtworkItems { get; } = [];
    public IReadOnlyList<ID3v2Util.APICType> ArtworkTypes { get; } =
        Enum.GetValues<ID3v2Util.APICType>();
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
        ClearArtworkItems();
        _artworkSetModified = false;
        IsArtworkMixed = false;
        ArtworkSummary = "No embedded artwork.";
        NotifyCommands();

        if (!selection.HasSelection)
        {
            Overview = "Select a track to inspect its metadata.";
            foreach (EditableTagField field in Fields)
                field.SetLoaded([], false);
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
            Overview = DescribeOverview(selection, models);

            IReadOnlyList<string> artworkSignatures = await _library.GetImageSignaturesAsync(
                selection.Paths, cancellation.Token);
            string[] distinctArtwork = artworkSignatures.Distinct(StringComparer.Ordinal).ToArray();
            IsArtworkMixed = distinctArtwork.Length > 1;
            if (IsArtworkMixed)
            {
                ArtworkSummary = "Mixed values — selected files have different embedded artwork.";
                return;
            }

            if (distinctArtwork.Length == 0 || string.IsNullOrEmpty(distinctArtwork[0]))
                return;

            OperationResult<MediaFileModel> artwork = await _media.LoadAsync(
                selection.Paths[0], includeArtwork: true, cancellation.Token);
            if (generation != _generation)
                return;
            ArtworkModel[] embeddedArtwork = artwork.Value?.Artwork.ToArray() ?? [];
            if (embeddedArtwork.Length == 0)
                return;
            for (int index = 0; index < embeddedArtwork.Length; index++)
            {
                ArtworkModel image = embeddedArtwork[index];
                object? source = await _thumbnails.CreateImageSourceAsync(
                    image.Data, cancellationToken: cancellation.Token);
                if (generation != _generation)
                    return;
                var preview = new ArtworkPreviewItem(
                    source,
                    ArtworkType(image, index),
                    ArtworkMimeType(image),
                    image.Data,
                    ArtworkDetails(image),
                    image.Description);
                preview.PropertyChanged += OnArtworkItemChanged;
                ArtworkItems.Add(preview);
            }
            ArtworkSource = ArtworkItems.FirstOrDefault()?.Source;
            ArtworkSummary = embeddedArtwork.Length == 1
                ? ArtworkItems[0].Summary
                : $"{embeddedArtwork.Length:N0} embedded artworks";
            if (selection.Paths.Count > 1)
                ArtworkSummary += $" · shared by {selection.Paths.Count:N0} tracks";
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
            .ToDictionary(
                group => group.Key,
                group => group.Select(value => value.Value)
                    .Where(value => !string.IsNullOrEmpty(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray())).ToArray();
        foreach (EditableTagField field in Fields)
        {
            string[][] valuesByFile = maps
                .Select(map => map.GetValueOrDefault(field.Field) ?? [])
                .ToArray();
            bool mixed = valuesByFile.Skip(1).Any(values =>
                !values.SequenceEqual(valuesByFile[0], StringComparer.Ordinal));
            mixed |= valuesByFile.Any(values => values.Length > 1);
            string[] displayValues = valuesByFile
                .SelectMany(values => values.Length == 0 ? ["(missing)"] : values)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (displayValues.Length == 1 && displayValues[0] == "(missing)")
                displayValues = [];
            field.SetLoaded(displayValues, mixed);
        }
    }

    private bool CanEdit() => HasSelection && !IsBusy;
    private bool CanEditArtworkSet() => CanEdit() && !IsArtworkMixed;
    private bool CanSaveArtworkSet() => CanEditArtworkSet() &&
        (_artworkSetModified || ArtworkItems.Any(item => item.IsModified));

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

    [RelayCommand(CanExecute = nameof(CanEditArtworkSet))]
    private async Task AddArtworkAsync()
    {
        string? path = await _files.PickFileAsync("Choose artwork",
            [new FilePickerType("Images", [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"])]);
        if (path is null)
            return;

        PreparedImage? prepared = await _artwork.PrepareFromFileAsync(path, ArtworkMaxDimension);
        if (prepared is null)
        {
            StatusMessage = "The selected image could not be prepared for embedding.";
            return;
        }

        ID3v2Util.APICType type = ArtworkItems.Any(item => item.Type == ID3v2Util.APICType.FrontCover)
            ? ID3v2Util.APICType.Other
            : ID3v2Util.APICType.FrontCover;
        object? source = await _thumbnails.CreateImageSourceAsync(prepared.Data);
        var item = new ArtworkPreviewItem(
            source,
            type,
            prepared.MimeType,
            prepared.Data,
            $"{prepared.MimeType} · {prepared.Width:N0} × {prepared.Height:N0} · {FormatBytes(prepared.Data.LongLength)}",
            null);
        item.PropertyChanged += OnArtworkItemChanged;
        ArtworkItems.Add(item);
        ArtworkSource ??= item.Source;
        _artworkSetModified = true;
        UpdateArtworkSummary();
        SaveArtworkSetCommand.NotifyCanExecuteChanged();
    }

    public void RemoveArtworkItem(ArtworkPreviewItem item)
    {
        if (!CanEditArtworkSet() || !ArtworkItems.Contains(item))
            return;
        item.PropertyChanged -= OnArtworkItemChanged;
        ArtworkItems.Remove(item);
        ArtworkSource = ArtworkItems.FirstOrDefault()?.Source;
        _artworkSetModified = true;
        UpdateArtworkSummary();
        SaveArtworkSetCommand.NotifyCanExecuteChanged();
    }

    public async Task ReplaceArtworkItemAsync(ArtworkPreviewItem item)
    {
        if (!CanEditArtworkSet() || !ArtworkItems.Contains(item))
            return;
        string? path = await _files.PickFileAsync("Choose replacement artwork",
            [new FilePickerType("Images", [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"])]);
        if (path is null)
            return;

        PreparedImage? prepared = await _artwork.PrepareFromFileAsync(path, ArtworkMaxDimension);
        if (prepared is null)
        {
            StatusMessage = "The selected image could not be prepared for embedding.";
            return;
        }

        object? source = await _thumbnails.CreateImageSourceAsync(prepared.Data);
        item.ReplaceContent(
            source,
            prepared.MimeType,
            prepared.Data,
            $"{prepared.MimeType} · {prepared.Width:N0} × {prepared.Height:N0} · {FormatBytes(prepared.Data.LongLength)}");
        if (ReferenceEquals(ArtworkItems.FirstOrDefault(), item))
            ArtworkSource = item.Source;
        _artworkSetModified = true;
        UpdateArtworkSummary();
        StatusMessage = "Artwork replacement ready to save.";
        SaveArtworkSetCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSaveArtworkSet))]
    private async Task SaveArtworkSetAsync()
    {
        if (Selection.Paths.Count > 1 &&
            !await _dialogs.ConfirmAsync(
                "Save artwork changes",
                $"Replace the embedded artwork set on {Selection.Paths.Count:N0} selected tracks with these {ArtworkItems.Count:N0} image(s)?",
                "Save"))
            return;

        ArtworkInput[] images = ArtworkItems.Select(item => new ArtworkInput(
            item.Type,
            item.MimeType,
            item.Data,
            string.IsNullOrWhiteSpace(item.Description) ? null : item.Description.Trim())).ToArray();
        await ApplyArtworkAsync("Save artwork changes", musicPath =>
            _artwork.SaveImagesAsync(musicPath, images));
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
        AddArtworkCommand.NotifyCanExecuteChanged();
        SaveArtworkSetCommand.NotifyCanExecuteChanged();
        ScrubArtworkCommand.NotifyCanExecuteChanged();
        RemoveArtworkCommand.NotifyCanExecuteChanged();
    }

    private void OnArtworkItemChanged(object? sender, PropertyChangedEventArgs e)
        => SaveArtworkSetCommand.NotifyCanExecuteChanged();

    private void ClearArtworkItems()
    {
        foreach (ArtworkPreviewItem item in ArtworkItems)
            item.PropertyChanged -= OnArtworkItemChanged;
        ArtworkItems.Clear();
    }

    private void UpdateArtworkSummary()
    {
        ArtworkSummary = ArtworkItems.Count switch
        {
            0 => "No embedded artwork.",
            1 => ArtworkItems[0].Summary,
            _ => $"{ArtworkItems.Count:N0} embedded artworks",
        };
        if (Selection.Paths.Count > 1 && ArtworkItems.Count > 0)
            ArtworkSummary += $" · shared by {Selection.Paths.Count:N0} tracks";
    }

    private static string DescribeOverview(
        SelectionContext selection,
        IReadOnlyList<MediaFileModel> loadedModels)
    {
        int count = selection.Paths.Count;
        Dictionary<string, string?> codecsByPath = (selection.Records is { Count: > 0 }
                ? selection.Records.Select(record => (record.Path, record.CodecName))
                : loadedModels.Select(model => (model.Path, model.Codec?.CodecName)))
            .GroupBy(value => value.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Item2, StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> fileFormats = selection.Paths.Select(path =>
            FormatFileFormat(path, codecsByPath.GetValueOrDefault(path)));
        string[] knownTagFormats = (selection.Records is { Count: > 0 }
            ? selection.Records.Select(record => NormalizeTagFormat(record.TagType))
            : loadedModels.Select(model => NormalizeTagFormat(model.TagType)))
            .Take(count)
            .ToArray();
        IEnumerable<string> tagFormats = knownTagFormats.Concat(
            Enumerable.Repeat("Unknown", Math.Max(0, count - knownTagFormats.Length)));
        string scope = count == 1 ? "1 track selected" : $"{count:N0} tracks selected";

        return $"{scope}{Environment.NewLine}{Environment.NewLine}" +
               $"File formats{Environment.NewLine}{FormatDistribution(fileFormats, count)}" +
               $"{Environment.NewLine}{Environment.NewLine}Tag formats{Environment.NewLine}" +
               FormatDistribution(tagFormats, count);
    }

    private static string FormatFileFormat(string path, string? codec)
    {
        string extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(extension))
            return "Unknown";
        bool isMp4Family = extension is "MP4" or "M4A" or "M4P" or "M4R";
        return isMp4Family && !string.IsNullOrWhiteSpace(codec)
            ? $"{extension} ({codec})"
            : extension;
    }

    private static string NormalizeTagFormat(string? value) => value switch
    {
        null or "" => "Unknown",
        "Vorbis" => "Vorbis comments",
        _ => value,
    };

    private static string FormatDistribution(IEnumerable<string> values, int total)
    {
        string[] lines = values
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}: {group.Count():N0} ({(double)group.Count() / total:P0})")
            .ToArray();
        return lines.Length == 0 ? "Unknown: 0" : string.Join(Environment.NewLine, lines);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:N1} MB",
        >= 1024 => $"{bytes / 1024d:N0} KB",
        _ => $"{bytes:N0} bytes",
    };

    private static ID3v2Util.APICType ArtworkType(ArtworkModel image, int index) =>
        Enum.TryParse(image.Category, true, out ID3v2Util.APICType type)
            ? type
            : index == 0 ? ID3v2Util.APICType.FrontCover : ID3v2Util.APICType.Other;

    private static string ArtworkMimeType(ArtworkModel image)
    {
        if (string.IsNullOrWhiteSpace(image.ImageType))
            return "image/jpeg";
        if (image.ImageType.Contains('/'))
            return image.ImageType;
        string subtype = string.Equals(image.ImageType, "jpg", StringComparison.OrdinalIgnoreCase)
            ? "jpeg"
            : image.ImageType;
        return $"image/{subtype.ToLowerInvariant()}";
    }

    private static string ArtworkDetails(ArtworkModel image)
    {
        return $"{image.ImageType ?? "image"} · {image.Width:N0} × {image.Height:N0} · {FormatBytes(image.Size)}";
    }
}
