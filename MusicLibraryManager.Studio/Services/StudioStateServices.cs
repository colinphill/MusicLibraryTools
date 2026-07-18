using System.Text.Json;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Studio.Services;

public sealed record StudioGridSnapshot(
    IReadOnlyList<LibraryColumnState> Columns,
    LibrarySortState? Sort);

public sealed class StudioGridStateService(IAppSettings settings)
{
    private const string Preference = "manager.studio.library.grid.v1";

    public StudioGridSnapshot? Load()
    {
        try
        {
            string? json = settings.GetPreference(Preference);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<StudioGridSnapshot>(json);
        }
        catch
        {
            return null;
        }
    }

    public void Save(StudioGridSnapshot state) =>
        settings.SetPreference(Preference, JsonSerializer.Serialize(state));
}

public sealed class StudioSplitStateService(IAppSettings settings)
{
    private const string PreferencePrefix = "manager.studio.split.";

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

public sealed class StudioDropService
{
    public event Action<string>? SourceDropped;
    public void SetDroppedSource(string path) => SourceDropped?.Invoke(path);
}
