using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.App.Services;
using MusicLibrary.Core.Services;
using MusicLibraryTools;

namespace MusicLibrary.App.ViewModels;

/// <summary>An editable &lt;IndexTarget&gt; row (observable so folder-browse updates the text box).</summary>
public partial class IndexTargetRow : ObservableObject
{
    [ObservableProperty] private string _target = "";
    [ObservableProperty] private string? _offset;
    [ObservableProperty] private string? _sets;
    [ObservableProperty] private string? _filter;
}

/// <summary>An editable repeatable playlist export destination.</summary>
public partial class PlaylistTargetRow : ObservableObject
{
    [ObservableProperty] private string _target = "";
    [ObservableProperty] private string _type = "m3u";
    [ObservableProperty] private string? _sets;
}

/// <summary>
/// Create or edit a LibraryConfiguration XML (scan roots, cache DB, path limits, sync/playlist
/// targets) and save it. Backed by <see cref="EditableLibraryConfig"/>.
/// </summary>
public partial class ConfigDialogViewModel : ViewModelBase
{
    private readonly IFileDialogService _dialogs;

    [ObservableProperty] private string _databaseFile = "cache.db";
    [ObservableProperty] private int _lengthLimit = 255;
    [ObservableProperty] private int _discNumLengthLimit = 255;
    [ObservableProperty] private string? _syncTarget;
    [ObservableProperty] private string? _currentPath;
    [ObservableProperty] private string? _statusMessage;

    public ObservableCollection<IndexTargetRow> IndexTargets { get; } = [];
    public ObservableCollection<PlaylistTargetRow> PlaylistTargets { get; } = [];

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
            LengthLimit = config.LengthLimit;
            DiscNumLengthLimit = config.DiscNumLengthLimit;
            SyncTarget = config.SyncTarget;
            foreach (var t in config.IndexTargets)
                IndexTargets.Add(new IndexTargetRow
                {
                    Target = t.Target,
                    Offset = t.Offset,
                    Sets = t.Sets.Count == 0 ? null : string.Join(",", t.Sets),
                    Filter = t.Filter,
                });
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
    private void AddPlaylistTarget() => PlaylistTargets.Add(new PlaylistTargetRow());

    [RelayCommand]
    private void RemovePlaylistTarget(PlaylistTargetRow row) => PlaylistTargets.Remove(row);

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
                LengthLimit = LengthLimit,
                DiscNumLengthLimit = DiscNumLengthLimit,
                SyncTarget = SyncTarget,
                IndexTargets = IndexTargets
                    .Where(t => !string.IsNullOrWhiteSpace(t.Target))
                    .Select(t => new IndexTargetEntry
                    {
                        Target = t.Target,
                        Offset = t.Offset,
                        Sets = [.. LibraryConfiguration.ParseScanSets(t.Sets)],
                        Filter = t.Filter,
                    })
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
}
