using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFileUtilities;
using MusicLibrary.App.Services;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibrary.App.ViewModels;

/// <summary>One image in the artwork gallery: its picture type, bytes, and a decoded preview.</summary>
public partial class ArtworkSlot : ObservableObject
{
    [ObservableProperty] private ID3v2Util.APICType _type;
    public byte[] Data { get; set; }
    public string MimeType { get; set; }

    [ObservableProperty] private Bitmap? _preview;
    [ObservableProperty] private string? _caption;

    public ArtworkSlot(ID3v2Util.APICType type, byte[] data, string mimeType)
    {
        _type = type;
        Data = data;
        MimeType = mimeType;
    }
}

/// <summary>
/// Artwork tool for the focused file: a gallery of embedded images, each with a picture type
/// (Front Cover / Back Cover / Media / …). Add from an image file, remove, scrub (re-encode/
/// downscale), change types, then Save writes the whole set. Supported for MP3/DSF/FLAC/Ogg/MP4/WavPack.
/// </summary>
public partial class ArtworkViewModel : ViewModelBase
{
    private readonly IArtworkService _artwork;
    private readonly IMediaFileService _media;
    private readonly IFileDialogService _dialogs;

    // Guards against a slower, superseded target load overwriting a newer selection's gallery.
    private int _generation;

    [ObservableProperty] private string? _currentPath;
    [ObservableProperty] private bool _supportsWrite;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;

    /// <summary>Max dimension in px for resize on add/scrub (0 = keep original size).</summary>
    [ObservableProperty] private int _maxDimension = 1000;

    public ObservableCollection<ArtworkSlot> Images { get; } = [];

    public IReadOnlyList<ID3v2Util.APICType> PictureTypes { get; } = Enum.GetValues<ID3v2Util.APICType>();

    /// <summary>Raised after the embedded artwork changes so other panes can refresh.</summary>
    public event Action? ArtworkChanged;

    private static readonly IReadOnlyList<FilePickerFilter> ImageFilters =
    [
        new FilePickerFilter("Images", ["*.jpg", "*.jpeg", "*.png", "*.gif", "*.bmp", "*.webp"]),
        new FilePickerFilter("All files", ["*"]),
    ];

    public ArtworkViewModel(IArtworkService artwork, IMediaFileService media, IFileDialogService dialogs)
    {
        _artwork = artwork;
        _media = media;
        _dialogs = dialogs;
    }

    public async Task SetTargetAsync(string? path)
    {
        var gen = ++_generation;
        CurrentPath = path;
        SupportsWrite = path is not null && _artwork.SupportsWrite(path);
        StatusMessage = null;
        NotifyCommands();
        await ReloadAsync(gen);
    }

    private async Task ReloadAsync(int? gen = null)
    {
        foreach (var slot in Images)
            slot.Preview?.Dispose();
        Images.Clear();

        if (CurrentPath is null)
            return;

        var result = await _media.LoadAsync(CurrentPath);
        if (gen is int g && g != _generation)
            return;   // a newer selection has taken over
        if (!result.Success)
            return;

        foreach (var art in result.Value!.Artwork)
        {
            var slot = new ArtworkSlot(MapType(art.Category), art.Data, art.ImageType ?? "image/jpeg");
            SetPreview(slot, art.Width, art.Height, art.Size);
            Images.Add(slot);
        }
    }

    private bool CanEdit() => SupportsWrite && !IsBusy && CurrentPath is not null;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task AddAsync()
    {
        var image = await _dialogs.PickOpenFileAsync("Choose an image", ImageFilters);
        if (image is null)
            return;

        IsBusy = true;
        NotifyCommands();
        try
        {
            var prepared = await _artwork.PrepareFromFileAsync(image, MaxDimension);
            if (prepared is null)
            {
                StatusMessage = "Couldn't read that image.";
                return;
            }
            // Default new images to Front Cover unless one already exists, then Back Cover.
            var type = Images.Any(s => s.Type == ID3v2Util.APICType.FrontCover)
                ? ID3v2Util.APICType.BackCover
                : ID3v2Util.APICType.FrontCover;
            var slot = new ArtworkSlot(type, prepared.Data, prepared.MimeType);
            SetPreview(slot, prepared.Width, prepared.Height, prepared.Data.Length);
            Images.Add(slot);
            StatusMessage = "Added — Save to write.";
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    [RelayCommand]
    private void Remove(ArtworkSlot slot)
    {
        slot.Preview?.Dispose();
        Images.Remove(slot);
        StatusMessage = "Removed — Save to write.";
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task ScrubAllAsync()
    {
        IsBusy = true;
        NotifyCommands();
        try
        {
            foreach (var slot in Images)
            {
                var prepared = await _artwork.PrepareFromBytesAsync(slot.Data, MaxDimension);
                if (prepared is null)
                    continue;
                slot.Preview?.Dispose();
                slot.Data = prepared.Data;
                slot.MimeType = prepared.MimeType;
                SetPreview(slot, prepared.Width, prepared.Height, prepared.Data.Length);
            }
            StatusMessage = "Scrubbed — Save to write.";
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task SaveAsync()
    {
        IsBusy = true;
        NotifyCommands();
        try
        {
            var inputs = Images.Select(s => new ArtworkInput(s.Type, s.MimeType, s.Data)).ToList();
            var result = await _artwork.SaveImagesAsync(CurrentPath!, inputs);
            StatusMessage = result.Success ? $"Saved {inputs.Count} image(s)." : $"Failed: {result.Error}";
            if (result.Success)
            {
                await ReloadAsync();
                ArtworkChanged?.Invoke();
            }
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private void SetPreview(ArtworkSlot slot, int width, int height, int size)
    {
        try
        {
            using var ms = new MemoryStream(slot.Data);
            slot.Preview = new Bitmap(ms);
            slot.Caption = $"{slot.MimeType} {width}x{height}, {size:N0} bytes";
        }
        catch
        {
            slot.Caption = "(unreadable image)";
        }
    }

    // The read side reports a category string; map it back to a picture type (ID3/Vorbis use the
    // enum name, APE uses "Cover Art (Front)" etc, MP4 has none).
    private static ID3v2Util.APICType MapType(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return ID3v2Util.APICType.FrontCover;
        if (Enum.TryParse<ID3v2Util.APICType>(category, ignoreCase: true, out var t))
            return t;
        var c = category.ToLowerInvariant();
        if (c.Contains("back")) return ID3v2Util.APICType.BackCover;
        if (c.Contains("media")) return ID3v2Util.APICType.Media;
        if (c.Contains("leaflet")) return ID3v2Util.APICType.LeafletPage;
        return ID3v2Util.APICType.FrontCover;
    }

    private void NotifyCommands()
    {
        AddCommand.NotifyCanExecuteChanged();
        ScrubAllCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }
}
