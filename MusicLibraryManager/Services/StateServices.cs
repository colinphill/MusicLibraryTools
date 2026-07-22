using System.Text.Json;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Services;

public sealed record GridSnapshot(
    IReadOnlyList<LibraryColumnState> Columns,
    LibrarySortState? Sort);

public sealed class GridStateService(IAppSettings settings)
{
    private const string LibraryPreference = "manager.library.grid.v2";
    private const string PreferencePrefix = "manager.grid.";

    public GridSnapshot? Load() => LoadPreference(LibraryPreference);

    public GridSnapshot? Load(string key) =>
        LoadPreference($"{PreferencePrefix}{key}.v1");

    private GridSnapshot? LoadPreference(string preference)
    {
        try
        {
            string? json = settings.GetLibraryPreference(preference);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<GridSnapshot>(json);
        }
        catch
        {
            return null;
        }
    }

    public void Save(GridSnapshot state) =>
        SavePreference(LibraryPreference, state);

    public void Save(string key, GridSnapshot state) =>
        SavePreference($"{PreferencePrefix}{key}.v1", state);

    private void SavePreference(string preference, GridSnapshot state) =>
        settings.SetLibraryPreference(preference, JsonSerializer.Serialize(state));
}

public sealed class SplitStateService(IAppSettings settings)
{
    private const string PreferencePrefix = "manager.split.";

    public double? Load(string key)
    {
        try
        {
            string? json = settings.GetLibraryPreference(PreferenceKey(key));
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<double>(json);
        }
        catch
        {
            return null;
        }
    }

    public void Save(string key, double width) =>
        settings.SetLibraryPreference(PreferenceKey(key), JsonSerializer.Serialize(width));

    private static string PreferenceKey(string key) => $"{PreferencePrefix}{key}.v1";
}

public sealed class DropService
{
    public event Action<string>? SourceDropped;
    public void SetDroppedSource(string path) => SourceDropped?.Invoke(path);
}
