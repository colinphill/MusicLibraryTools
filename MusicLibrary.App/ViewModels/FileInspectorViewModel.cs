using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.App.Services;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibrary.App.ViewModels;

/// <summary>
/// Read-only inspection of a single music file's parsed metadata (M1). The write path arrives in M3.
/// </summary>
public partial class FileInspectorViewModel : ViewModelBase
{
    private readonly IMediaFileService _media;
    private readonly IFileDialogService _dialogs;

    // Guards against a slower, superseded load overwriting a newer selection's result.
    private int _generation;

    [ObservableProperty]
    private string? _filePath;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private MediaFileModel? _file;

    [ObservableProperty]
    private Bitmap? _artwork;

    [ObservableProperty]
    private string? _artworkCaption;

    public ObservableCollection<TagFieldValue> KnownFields { get; } = [];
    public ObservableCollection<TextField> TextFields { get; } = [];

    public FileInspectorViewModel(IMediaFileService media, IFileDialogService dialogs)
    {
        _media = media;
        _dialogs = dialogs;
    }

    private static readonly IReadOnlyList<FilePickerFilter> AudioFilters =
    [
        new FilePickerFilter("Audio files", ["*.mp3", "*.flac", "*.ogg", "*.m4a", "*.mp4", "*.wv", "*.dsf"]),
        new FilePickerFilter("All files", ["*"]),
    ];

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = await _dialogs.PickOpenFileAsync("Select a music file", AudioFilters);
        if (path is not null)
        {
            FilePath = path;
            await LoadAsync();
        }
    }

    /// <summary>Load a specific file (e.g. from a library-browser selection).</summary>
    public async Task LoadFromPathAsync(string path)
    {
        FilePath = path;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        var path = FilePath;
        var gen = ++_generation;
        if (string.IsNullOrWhiteSpace(path))
            return;

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await _media.LoadAsync(path);
            if (gen != _generation)
                return;   // a newer selection has taken over

            if (!result.Success)
            {
                Reset();
                ErrorMessage = result.Error;
                return;
            }

            var model = result.Value!;
            File = model;

            KnownFields.Clear();
            foreach (var f in model.KnownFields)
                KnownFields.Add(f);

            TextFields.Clear();
            foreach (var t in model.TextFields)
                TextFields.Add(t);

            LoadArtwork(model);
        }
        finally
        {
            if (gen == _generation)
                IsLoading = false;
        }
    }

    private void LoadArtwork(MediaFileModel model)
    {
        Artwork?.Dispose();
        Artwork = null;
        ArtworkCaption = null;

        var art = model.Artwork.FirstOrDefault();
        if (art is null || art.Data.Length == 0)
            return;

        try
        {
            using var ms = new MemoryStream(art.Data);
            Artwork = new Bitmap(ms);
            ArtworkCaption = $"{art.ImageType} {art.Width}x{art.Height}, {art.Size:N0} bytes";
        }
        catch
        {
            // Undecodable artwork shouldn't break the inspector.
            ArtworkCaption = "(unreadable image)";
        }
    }

    private void Reset()
    {
        File = null;
        KnownFields.Clear();
        TextFields.Clear();
        Artwork?.Dispose();
        Artwork = null;
        ArtworkCaption = null;
    }
}
