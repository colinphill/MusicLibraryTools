using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.App.Services;
using MusicLibrary.Core.Models;

namespace MusicLibrary.App.ViewModels;

public partial class IngestConfigDialogViewModel : ViewModelBase
{
    private readonly IFileDialogService _dialogs;

    [ObservableProperty] private string _ffmpegPath = "ffmpeg";
    [ObservableProperty] private string _aacDestination = @"Z:\iTunes\AddAAC";
    [ObservableProperty] private string _itunesLibraryPath = string.Empty;
    [ObservableProperty] private string _cdDestination = @"Z:\iTunes\FLAC";
    [ObservableProperty] private string _pairedCdDestination = @"Z:\iTunes\FLAC2";
    [ObservableProperty] private string _highResolutionDestination = @"Z:\iTunes\HiRes\Stereo\PCM";
    [ObservableProperty] private int _lengthLimit = 255;
    [ObservableProperty] private int _discNumLengthLimit = 255;
    [ObservableProperty] private string _aacEncoder = "libfdk_aac";
    [ObservableProperty] private int _aacBitrateKbps = 256;
    [ObservableProperty] private bool _deleteSourcesAfterIngest;
    [ObservableProperty] private string? _currentPath;
    [ObservableProperty] private string? _statusMessage;

    public string Title => CurrentPath is null ? "New ingestion configuration" : $"Edit ingestion configuration — {Path.GetFileName(CurrentPath)}";
    public event Action<string?>? CloseRequested;

    public IngestConfigDialogViewModel(IFileDialogService dialogs, string? existingPath)
    {
        _dialogs = dialogs;
        if (!string.IsNullOrWhiteSpace(existingPath) && File.Exists(existingPath)) LoadFrom(existingPath);
    }

    private void LoadFrom(string path)
    {
        try
        {
            var config = IngestMusicConfiguration.Load(path);
            FfmpegPath = config.FfmpegPath;
            AacDestination = config.AacDestination;
            ItunesLibraryPath = config.ItunesLibraryPath ?? string.Empty;
            CdDestination = config.CdDestination;
            PairedCdDestination = config.PairedCdDestination;
            HighResolutionDestination = config.HighResolutionDestination;
            LengthLimit = config.LengthLimit;
            DiscNumLengthLimit = config.DiscNumLengthLimit;
            AacEncoder = config.AacEncoder;
            AacBitrateKbps = config.AacBitrateKbps;
            DeleteSourcesAfterIngest = config.DeleteSourcesAfterIngest;
            CurrentPath = Path.GetFullPath(path);
            OnPropertyChanged(nameof(Title));
        }
        catch (Exception ex) { StatusMessage = $"Couldn't load: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task BrowseFfmpegAsync()
    {
        string? path = await _dialogs.PickOpenFileAsync("Select ffmpeg executable",
            [new FilePickerFilter("Executable", OperatingSystem.IsWindows() ? ["*.exe"] : ["*"])]);
        if (path is not null) FfmpegPath = path;
    }

    [RelayCommand] private async Task BrowseAacDestinationAsync() => AacDestination = await PickFolder(AacDestination, "Select AAC destination");
    [RelayCommand]
    private async Task BrowseItunesLibraryAsync()
    {
        string? path = await _dialogs.PickOpenFileAsync("Select iTunes library",
            [new FilePickerFilter("iTunes library", ["*.itl"])]);
        if (path is not null) ItunesLibraryPath = path;
    }
    [RelayCommand] private async Task BrowseCdDestinationAsync() => CdDestination = await PickFolder(CdDestination, "Select CD-only FLAC destination");
    [RelayCommand] private async Task BrowsePairedCdDestinationAsync() => PairedCdDestination = await PickFolder(PairedCdDestination, "Select paired CD FLAC destination");
    [RelayCommand] private async Task BrowseHighResolutionDestinationAsync() => HighResolutionDestination = await PickFolder(HighResolutionDestination, "Select high-resolution destination");

    private async Task<string> PickFolder(string current, string title) => await _dialogs.PickFolderAsync(title) ?? current;

    [RelayCommand] private async Task SaveAsync() => await SaveToAsync(CurrentPath);
    [RelayCommand] private async Task SaveAsAsync() => await SaveToAsync(null);

    private async Task SaveToAsync(string? path)
    {
        path ??= await _dialogs.PickSaveFileAsync("Save ingestion configuration", "ingest-music.xml", "xml",
            [new FilePickerFilter("XML files", ["*.xml"])]);
        if (path is null) return;
        try
        {
            var config = new IngestMusicConfiguration
            {
                FfmpegPath = FfmpegPath.Trim(), AacDestination = AacDestination.Trim(),
                ItunesLibraryPath = string.IsNullOrWhiteSpace(ItunesLibraryPath) ? null : ItunesLibraryPath.Trim(),
                CdDestination = CdDestination.Trim(), PairedCdDestination = PairedCdDestination.Trim(),
                HighResolutionDestination = HighResolutionDestination.Trim(), LengthLimit = LengthLimit,
                DiscNumLengthLimit = DiscNumLengthLimit, AacEncoder = AacEncoder.Trim(),
                AacBitrateKbps = AacBitrateKbps, DeleteSourcesAfterIngest = DeleteSourcesAfterIngest,
            };
            config.Save(path);
            _ = IngestMusicConfiguration.Load(path);
            CloseRequested?.Invoke(Path.GetFullPath(path));
        }
        catch (Exception ex) { StatusMessage = $"Save failed: {ex.Message}"; }
    }

    [RelayCommand] private void Cancel() => CloseRequested?.Invoke(null);
}
