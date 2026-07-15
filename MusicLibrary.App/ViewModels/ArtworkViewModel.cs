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

    /// <summary>For a multi-selection, how many of the selected files carry this image (else null).</summary>
    [ObservableProperty] private string? _usage;
    [ObservableProperty] private bool _isCanonical;

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
    // Comparing artwork loads each file's image bytes, so bound how many we pull for a huge selection.
    private const int MaxCompareFiles = 60;

    private readonly IArtworkService _artwork;
    private readonly IMediaFileService _media;
    private readonly IFileDialogService _dialogs;

    // Guards against a slower, superseded target load overwriting a newer selection's gallery.
    private int _generation;

    // The current multi-selection. The gallery is loaded from the first (representative) file, and a
    // Save writes the whole image set to every target — e.g. re-cover a whole album at once.
    private IReadOnlyList<string> _targets = [];
    private long _loadedArtworkBytes;
    private int _loadedArtworkFiles;
    private ArtworkInput? _normalizationInput;

    [ObservableProperty] private string? _currentPath;
    [ObservableProperty] private bool _supportsWrite;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;

    /// <summary>Non-empty when more than one file is selected; describes the batch scope.</summary>
    [ObservableProperty] private string? _targetSummary;

    /// <summary>Max dimension in px for resize on add/scrub (0 = keep original size).</summary>
    [ObservableProperty] private int _maxDimension = 1000;
    [ObservableProperty] private string? _normalizationPreviewText;
    [ObservableProperty] private bool _hasNormalizationPreview;

    public ObservableCollection<ArtworkSlot> Images { get; } = [];

    public IReadOnlyList<ID3v2Util.APICType> PictureTypes { get; } = Enum.GetValues<ID3v2Util.APICType>();

    /// <summary>Raised after the embedded artwork changes, with the affected files, so other panes can refresh.</summary>
    public event Action<IReadOnlyList<string>>? ArtworkChanged;

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

    /// <summary>Point the artwork tab at one or more files; the gallery compares/aggregates their images.</summary>
    public async Task SetTargetsAsync(IReadOnlyList<string> paths)
    {
        var gen = ++_generation;
        _targets = paths;
        CurrentPath = paths.Count > 0 ? paths[0] : null;
        // Only offer writing when every selected file's format supports it (so a batch Save is all-or-nothing).
        SupportsWrite = paths.Count > 0 && paths.All(_artwork.SupportsWrite);
        StatusMessage = null;
        NotifyCommands();
        await ReloadAsync(gen);
    }

    partial void OnMaxDimensionChanged(int value) => InvalidateNormalizationPreview();

    // Loads the artwork across the selection: the gallery holds the DISTINCT images (deduped by hash),
    // each badged with how many selected files carry it, plus an aggregate uniform/mixed summary. A
    // Save writes the shown set to every selected file.
    private async Task ReloadAsync(int? gen = null)
    {
        foreach (var slot in Images)
            slot.Preview?.Dispose();
        Images.Clear();
        TargetSummary = null;
        _loadedArtworkBytes = 0;
        _loadedArtworkFiles = 0;
        InvalidateNormalizationPreview();

        if (_targets.Count == 0)
            return;

        var total = _targets.Count;
        var sample = total > MaxCompareFiles ? _targets.Take(MaxCompareFiles).ToList() : _targets;

        var distinct = new Dictionary<string, (ArtworkModel Image, int Count)>();
        var order = new List<string>();
        var perFileSets = new List<HashSet<string>>();
        int loaded = 0;

        foreach (var path in sample)
        {
            var result = await _media.LoadAsync(path, includeArtwork: true);
            if (gen is int g && g != _generation)
                return;   // a newer selection has taken over
            if (!result.Success)
                continue;
            loaded++;
            _loadedArtworkFiles++;
            _loadedArtworkBytes += result.Value!.Artwork.Sum(image => (long)image.Size);

            var set = new HashSet<string>();
            foreach (var art in result.Value.Artwork)
            {
                var key = string.IsNullOrEmpty(art.Hash) ? Guid.NewGuid().ToString("N") : art.Hash;
                if (set.Add(key) && !distinct.ContainsKey(key))
                {
                    distinct[key] = (art, 0);
                    order.Add(key);
                }
            }
            // Count each distinct image once per file that carries it.
            foreach (var key in set)
            {
                var (image, count) = distinct[key];
                distinct[key] = (image, count + 1);
            }
            perFileSets.Add(set);
        }

        foreach (var key in order)
        {
            var (image, count) = distinct[key];
            var slot = new ArtworkSlot(MapType(image.Category), image.Data, image.ImageType ?? "image/jpeg");
            SetPreview(slot, image.Width, image.Height, image.Size);
            if (total > 1)
                slot.Usage = $"in {count} of {loaded} file(s)";
            Images.Add(slot);
        }
        if (Images.Count == 1)
            Images[0].IsCanonical = true;

        if (total > 1)
        {
            bool uniform = perFileSets.Count > 0 && perFileSets.All(s => s.SetEquals(perFileSets[0]));
            var scope = total > MaxCompareFiles ? $"first {loaded:N0} of {total:N0} files" : $"{total:N0} files";
            TargetSummary = uniform
                ? $"{scope} selected — all share this artwork. Save writes it to every file."
                : $"{scope} selected — {distinct.Count} different image(s) across them (badged below). Save sets every file to the images shown.";
        }
        NotifyCommands();
    }

    private bool CanEdit() => SupportsWrite && !IsBusy && CurrentPath is not null;

    [RelayCommand]
    private void SelectCanonical(ArtworkSlot? slot)
    {
        if (slot is null)
            return;
        foreach (var image in Images)
            image.IsCanonical = ReferenceEquals(image, slot);
        InvalidateNormalizationPreview();
        StatusMessage = "Canonical image selected. Preview normalization before applying.";
        NotifyCommands();
    }

    private bool CanPreviewNormalization() => CanEdit() && Images.Any(image => image.IsCanonical);

    [RelayCommand(CanExecute = nameof(CanPreviewNormalization))]
    private async Task PreviewNormalizationAsync()
    {
        var canonical = Images.FirstOrDefault(image => image.IsCanonical);
        if (canonical is null)
            return;
        IsBusy = true;
        NotifyCommands();
        try
        {
            var prepared = await _artwork.PrepareFromBytesAsync(canonical.Data, MaxDimension);
            if (prepared is null)
            {
                StatusMessage = "The selected canonical image could not be decoded.";
                return;
            }
            _normalizationInput = new ArtworkInput(canonical.Type, prepared.MimeType, prepared.Data);
            long estimatedCurrent = _loadedArtworkFiles == 0 ? 0
                : (long)Math.Round((double)_loadedArtworkBytes / _loadedArtworkFiles * _targets.Count);
            long projected = (long)prepared.Data.Length * _targets.Count;
            long savings = estimatedCurrent - projected;
            string estimate = _loadedArtworkFiles < _targets.Count ? "Estimated " : "";
            NormalizationPreviewText = $"{estimate}normalization preview: {_targets.Count:N0} file(s), " +
                $"one {prepared.MimeType} {prepared.Width:N0}x{prepared.Height:N0} image each, " +
                $"{FormatBytes(projected)} projected ({DescribeSavings(savings)}).";
            HasNormalizationPreview = true;
            StatusMessage = "Normalization preview ready. No files have changed.";
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private bool CanApplyNormalization() => CanEdit() && HasNormalizationPreview && _normalizationInput is not null;

    [RelayCommand(CanExecute = nameof(CanApplyNormalization))]
    private async Task ApplyNormalizationAsync()
    {
        if (_normalizationInput is not { } input)
            return;
        IsBusy = true;
        NotifyCommands();
        try
        {
            int saved = 0;
            string? firstError = null;
            foreach (string path in _targets)
            {
                var result = await _artwork.SaveImagesAsync(path, [input]);
                if (result.Success) saved++;
                else firstError ??= result.Error;
            }
            StatusMessage = saved == _targets.Count
                ? $"Normalized artwork across {saved:N0} file(s)."
                : $"Normalized {saved:N0}/{_targets.Count:N0} file(s). {firstError}";
            if (saved > 0)
            {
                await ReloadAsync();
                ArtworkChanged?.Invoke(_targets.ToList());
            }
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

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
        InvalidateNormalizationPreview();
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
            var targets = _targets.Count > 0 ? _targets : (CurrentPath is null ? [] : new[] { CurrentPath });

            int saved = 0;
            string? firstError = null;
            foreach (var path in targets)
            {
                var result = await _artwork.SaveImagesAsync(path, inputs);
                if (result.Success)
                    saved++;
                else
                    firstError ??= result.Error;
            }

            StatusMessage = saved == targets.Count
                ? (targets.Count > 1
                    ? $"Saved {inputs.Count} image(s) to {saved:N0} files."
                    : $"Saved {inputs.Count} image(s).")
                : $"Saved {saved:N0}/{targets.Count:N0} files. {firstError}";

            if (saved > 0)
            {
                await ReloadAsync();
                ArtworkChanged?.Invoke(targets.ToList());
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
        PreviewNormalizationCommand.NotifyCanExecuteChanged();
        ApplyNormalizationCommand.NotifyCanExecuteChanged();
    }

    private void InvalidateNormalizationPreview()
    {
        _normalizationInput = null;
        HasNormalizationPreview = false;
        NormalizationPreviewText = null;
        PreviewNormalizationCommand.NotifyCanExecuteChanged();
        ApplyNormalizationCommand.NotifyCanExecuteChanged();
    }

    private static string DescribeSavings(long savings) => savings >= 0
        ? $"save {FormatBytes(savings)}"
        : $"increase by {FormatBytes(-savings)}";

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}
