using System.Text.Json;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Services;

public sealed record GridSnapshot(
    IReadOnlyList<LibraryColumnState> Columns,
    LibrarySortState? Sort);

public sealed class GridStateService(IAppSettings settings)
{
    private const string Preference = "manager.library.grid.v2";

    public GridSnapshot? Load()
    {
        try
        {
            string? json = settings.GetPreference(Preference);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<GridSnapshot>(json);
        }
        catch
        {
            return null;
        }
    }

    public void Save(GridSnapshot state) =>
        settings.SetPreference(Preference, JsonSerializer.Serialize(state));
}

public sealed class SplitStateService(IAppSettings settings)
{
    private const string PreferencePrefix = "manager.split.";

    public double? Load(string key)
    {
        try
        {
            string? json = settings.GetPreference(PreferenceKey(key));
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<double>(json);
        }
        catch
        {
            return null;
        }
    }

    public void Save(string key, double width) =>
        settings.SetPreference(PreferenceKey(key), JsonSerializer.Serialize(width));

    private static string PreferenceKey(string key) => $"{PreferencePrefix}{key}.v1";
}

public sealed class DropService
{
    public event Action<string>? SourceDropped;
    public void SetDroppedSource(string path) => SourceDropped?.Invoke(path);
}
