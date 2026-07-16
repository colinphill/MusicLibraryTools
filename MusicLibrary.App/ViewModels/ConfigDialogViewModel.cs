using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.App.Services;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;

namespace MusicLibrary.App.ViewModels;

/// <summary>An editable &lt;IndexTarget&gt; row (observable so folder-browse updates the text box).</summary>
public partial class IndexTargetRow : ObservableObject
{
    [ObservableProperty] private string _target = "";
    [ObservableProperty] private string? _defaultOffset;
    [ObservableProperty] private string? _filter;
    [ObservableProperty] private bool _organize = true;
    [ObservableProperty] private bool _useItunesCanonicalNaming;
    [ObservableProperty] private LibraryIngestRole _ingestRole;
    [ObservableProperty] private bool _isSyncTarget;
    public ObservableCollection<IndexTargetSetRow> Memberships { get; } = [];
}

public partial class IndexTargetSetRow : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string? _offset;
}

/// <summary>An editable repeatable playlist export destination.</summary>
public partial class PlaylistTargetRow : ObservableObject
{
    [ObservableProperty] private string _target = "";
    [ObservableProperty] private string _type = "m3u";
    [ObservableProperty] private string? _sets;
}

public partial class SyncPlaylistRow : ObservableObject
{
    [ObservableProperty] private string _name = "";
}

/// <summary>
/// Create or edit a LibraryConfiguration XML (scan roots, cache DB, path limits, sync/playlist
/// targets) and save it. Backed by <see cref="EditableLibraryConfig"/>.
/// </summary>
public partial class ConfigDialogViewModel : ViewModelBase
{
    private readonly IFileDialogService _dialogs;

    [ObservableProperty] private string _databaseFile = "cache.db";
    [ObservableProperty] private string? _itunesLibraryPath;
    [ObservableProperty] private string _ffmpegPath = "ffmpeg";
    [ObservableProperty] private int _lengthLimit = 255;
    [ObservableProperty] private int _discNumLengthLimit = 255;
    [ObservableProperty] private string _aacEncoder = "libfdk_aac";
    [ObservableProperty] private int _aacBitrateKbps = 256;
    [ObservableProperty] private bool _deleteSourcesAfterIngest;
    [ObservableProperty] private bool _removeNonMusicAfterIngest;
    [ObservableProperty] private string? _currentPath;
    [ObservableProperty] private string? _statusMessage;

    public ObservableCollection<IndexTargetRow> IndexTargets { get; } = [];
    public ObservableCollection<SyncPlaylistRow> SyncPlaylists { get; } = [];
    public ObservableCollection<PlaylistTargetRow> PlaylistTargets { get; } = [];
    public IReadOnlyList<LibraryIngestRole> IngestRoles { get; } =
        Enum.GetValues<LibraryIngestRole>();

    public string Title => CurrentPath is null ? "New library configuration" : $"Edit configuration — {System.IO.Path.GetFileName(CurrentPath)}";

    /// <summary>Raised to close the dialog; the string is the saved path, or null if cancelled.</summary>
    public event Action<string?>? CloseRequested;

    public ConfigDialogViewModel(IFileDialogService dialogs, string? existingPath)
    {
        _dialogs = dialogs;
        if (existingPath is not null && File.Exists(existingPath))
            LoadFrom(existingPath);
        else
            IndexTargets.Add(new IndexTargetRow());
    }

    private void LoadFrom(string path)
    {
        try
        {
            var config = EditableLibraryConfig.Load(path);
            DatabaseFile = config.DatabaseFile;
            ItunesLibraryPath = config.ItunesLibraryPath;
            FfmpegPath = config.FfmpegPath;
            LengthLimit = config.LengthLimit;
            DiscNumLengthLimit = config.DiscNumLengthLimit;
            AacEncoder = config.AacEncoder;
            AacBitrateKbps = config.AacBitrateKbps;
            DeleteSourcesAfterIngest = config.DeleteSourcesAfterIngest;
            RemoveNonMusicAfterIngest = config.RemoveNonMusicAfterIngest;
            foreach (var t in config.IndexTargets)
            {
                var row = new IndexTargetRow
                {
                    Target = t.Target,
                    DefaultOffset = t.DefaultOffset,
                    Filter = t.Filter,
                    Organize = t.Organize,
                    UseItunesCanonicalNaming = t.UseItunesCanonicalNaming,
                    IngestRole = t.IngestRole,
                    IsSyncTarget = t.IsSyncTarget,
                };
                foreach (IndexTargetSetEntry membership in t.Memberships)
                    row.Memberships.Add(new IndexTargetSetRow
                    {
                        Name = membership.Name,
                        Offset = membership.Offset,
                    });
                IndexTargets.Add(row);
            }
            foreach (string playlist in config.SyncPlaylists)
                SyncPlaylists.Add(new SyncPlaylistRow { Name = playlist });
            foreach (var target in config.PlaylistTargets)
                PlaylistTargets.Add(new PlaylistTargetRow
                {
                    Target = target.Target,
                    Type = target.Type,
                    Sets = target.Sets.Count == 0 ? null : string.Join(",", target.Sets),
                });
            CurrentPath = path;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't load: {ex.Message}";
            IndexTargets.Add(new IndexTargetRow());
        }
    }

    [RelayCommand]
    private void AddTarget() => IndexTargets.Add(new IndexTargetRow());

    [RelayCommand]
    private void RemoveTarget(IndexTargetRow row) => IndexTargets.Remove(row);

    [RelayCommand]
    private void ClearSyncTarget()
    {
        foreach (IndexTargetRow target in IndexTargets)
            target.IsSyncTarget = false;
    }

    [RelayCommand]
    private void AddSet(IndexTargetRow row) => row.Memberships.Add(new IndexTargetSetRow());

    [RelayCommand]
    private void RemoveSet(IndexTargetSetRow row)
    {
        foreach (IndexTargetRow target in IndexTargets)
            if (target.Memberships.Remove(row)) return;
    }

    [RelayCommand]
    private void AddPlaylistTarget() => PlaylistTargets.Add(new PlaylistTargetRow());

    [RelayCommand]
    private void RemovePlaylistTarget(PlaylistTargetRow row) => PlaylistTargets.Remove(row);

    [RelayCommand]
    private void AddSyncPlaylist() => SyncPlaylists.Add(new SyncPlaylistRow());

    [RelayCommand]
    private void RemoveSyncPlaylist(SyncPlaylistRow row) => SyncPlaylists.Remove(row);

    [RelayCommand]
    private async Task BrowseTargetAsync(IndexTargetRow row)
    {
        var folder = await _dialogs.PickFolderAsync("Choose a scan root");
        if (folder is not null)
            row.Target = folder;
    }

    [RelayCommand]
    private async Task BrowsePlaylistTargetAsync(PlaylistTargetRow row)
    {
        var folder = await _dialogs.PickFolderAsync("Choose a playlist export folder");
        if (folder is not null)
            row.Target = folder;
    }

    [RelayCommand]
    private async Task BrowseItunesLibraryAsync()
    {
        string? path = await _dialogs.PickOpenFileAsync("Select iTunes library",
            [new FilePickerFilter("iTunes library", ["*.itl"])]);
        if (path is not null) ItunesLibraryPath = path;
    }

    [RelayCommand]
    private async Task BrowseFfmpegAsync()
    {
        string? path = await _dialogs.PickOpenFileAsync("Select ffmpeg executable",
            [new FilePickerFilter("Executable", ["*.exe", "*"])]);
        if (path is not null) FfmpegPath = path;
    }

    [RelayCommand]
    private async Task ImportIngestConfigurationAsync()
    {
        string? path = await _dialogs.PickOpenFileAsync(
            "Import legacy IngestMusic configuration",
            [new FilePickerFilter("XML configuration", ["*.xml"])]);
        if (path is null)
            return;
        try
        {
            IngestMusicConfiguration ingest = IngestMusicConfiguration.Load(path);
            FfmpegPath = ingest.FfmpegPath;
            ItunesLibraryPath = ingest.ItunesLibraryPath;
            LengthLimit = ingest.LengthLimit;
            DiscNumLengthLimit = ingest.DiscNumLengthLimit;
            AacEncoder = ingest.AacEncoder;
            AacBitrateKbps = ingest.AacBitrateKbps;
            DeleteSourcesAfterIngest = ingest.DeleteSourcesAfterIngest;
            RemoveNonMusicAfterIngest = ingest.RemoveNonMusicAfterIngest;
            AssignIngestRole(ingest.CdDestination, LibraryIngestRole.Cd);
            AssignIngestRole(ingest.PairedCdDestination, LibraryIngestRole.CdFallback);
            AssignIngestRole(ingest.HighResolutionDestination, LibraryIngestRole.HiRes);
            AssignIngestRole(ingest.AacDestination, LibraryIngestRole.AacFallback);
            StatusMessage =
                "Legacy ingest settings imported. Review the flagged IndexTargets, then save.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Import failed: {ex.Message}";
        }
    }

    private void AssignIngestRole(string path, LibraryIngestRole role)
    {
        foreach (IndexTargetRow existing in IndexTargets.Where(target => target.IngestRole == role))
            existing.IngestRole = LibraryIngestRole.None;
        IndexTargetRow? row = IndexTargets.FirstOrDefault(target =>
            PathComparer.Equals(
                Path.TrimEndingDirectorySeparator(target.Target),
                Path.TrimEndingDirectorySeparator(path)));
        if (row is null)
        {
            row = new IndexTargetRow { Target = path };
            IndexTargets.Add(row);
        }
        row.IngestRole = role;
    }

    [RelayCommand]
    private async Task SaveAsync() => await SaveToAsync(CurrentPath);

    [RelayCommand]
    private async Task SaveAsAsync() => await SaveToAsync(null);

    private async Task SaveToAsync(string? path)
    {
        path ??= await _dialogs.PickSaveFileAsync("Save library configuration", "library.xml", "xml",
            [new FilePickerFilter("XML files", ["*.xml"])]);
        if (path is null)
            return;

        try
        {
            var config = new EditableLibraryConfig
            {
                DatabaseFile = string.IsNullOrWhiteSpace(DatabaseFile) ? "cache.db" : DatabaseFile,
                ItunesLibraryPath = string.IsNullOrWhiteSpace(ItunesLibraryPath)
                    ? null : ItunesLibraryPath.Trim(),
                FfmpegPath = string.IsNullOrWhiteSpace(FfmpegPath) ? "ffmpeg" : FfmpegPath.Trim(),
                LengthLimit = LengthLimit,
                DiscNumLengthLimit = DiscNumLengthLimit,
                AacEncoder = string.IsNullOrWhiteSpace(AacEncoder)
                    ? "libfdk_aac" : AacEncoder.Trim(),
                AacBitrateKbps = AacBitrateKbps,
                DeleteSourcesAfterIngest = DeleteSourcesAfterIngest,
                RemoveNonMusicAfterIngest = RemoveNonMusicAfterIngest,
                IndexTargets = IndexTargets
                    .Where(t => !string.IsNullOrWhiteSpace(t.Target))
                    .Select(t => new IndexTargetEntry
                    {
                        Target = t.Target,
                        DefaultOffset = t.DefaultOffset,
                        Organize = t.Organize,
                        UseItunesCanonicalNaming = t.UseItunesCanonicalNaming,
                        IngestRole = t.IngestRole,
                        IsSyncTarget = t.IsSyncTarget,
                        Memberships = t.Memberships
                            .Where(membership => !string.IsNullOrWhiteSpace(membership.Name))
                            .Select(membership => new IndexTargetSetEntry
                            {
                                Name = membership.Name.Trim(),
                                Offset = string.IsNullOrWhiteSpace(membership.Offset)
                                    ? null : membership.Offset.Trim(),
                            }).ToList(),
                        Filter = t.Filter,
                    })
                    .ToList(),
                SyncPlaylists = SyncPlaylists
                    .Where(playlist => !string.IsNullOrWhiteSpace(playlist.Name))
                    .Select(playlist => playlist.Name.Trim())
                    .ToList(),
                PlaylistTargets = PlaylistTargets
                    .Where(t => !string.IsNullOrWhiteSpace(t.Target))
                    .Select(t => new PlaylistTargetEntry
                    {
                        Target = t.Target,
                        Type = t.Type,
                        Sets = [.. LibraryConfiguration.ParseScanSets(t.Sets)],
                    })
                    .ToList(),
            };
            config.Save(path);
            CloseRequested?.Invoke(path);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
