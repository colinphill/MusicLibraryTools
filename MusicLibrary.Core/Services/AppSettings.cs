using System.Text.Json;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

/// <inheritdoc cref="IAppSettings"/>
public sealed class AppSettings : IAppSettings
{
    private sealed record PersistedState(string? ConfigPath, Dictionary<string, string>? Preferences);

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MusicLibraryTools", "app-settings.json");

    private readonly Dictionary<string, string> _preferences;
    private string? _rememberedConfigPath;

    public AppSettings()
    {
        var state = TryLoad();
        _rememberedConfigPath = state?.ConfigPath;
        _preferences = state?.Preferences ?? new();
    }

    public string? ConfigPath { get; private set; }
    public LibraryConfiguration? Configuration { get; private set; }

    public event EventHandler? ConfigurationChanged;

    public void LoadConfig(string path)
    {
        // Constructing LibraryConfiguration parses the XML; let exceptions surface to the caller
        // so the UI can show a load error.
        Configuration = new LibraryConfiguration(path);
        ConfigPath = path;
        _rememberedConfigPath = path;
        Persist();
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    public string? GetRememberedConfigPath()
        => _rememberedConfigPath is not null && File.Exists(_rememberedConfigPath) ? _rememberedConfigPath : null;

    public string? GetPreference(string key)
        => _preferences.TryGetValue(key, out var value) ? value : null;

    public void SetPreference(string key, string? value)
    {
        if (value is null)
            _preferences.Remove(key);
        else
            _preferences[key] = value;
        Persist();
    }

    private static PersistedState? TryLoad()
    {
        try
        {
            if (File.Exists(StateFile))
                return JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(StateFile));
        }
        catch
        {
            // A corrupt state file shouldn't stop the app from starting.
        }
        return null;
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(new PersistedState(_rememberedConfigPath, _preferences)));
        }
        catch
        {
            // Persistence is best-effort; a failure here shouldn't break loading a config.
        }
    }
}
