using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

/// <summary>
/// Immutable, non-persisting configuration state for command-line adapters. It exposes the same
/// Core service context as the desktop app without touching recent-file or UI preference storage.
/// </summary>
public sealed class CommandLineAppSettings : IAppSettings
{
    private readonly AppConfigurationSnapshot _snapshot;

    public CommandLineAppSettings(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        string fullPath = Path.GetFullPath(configurationPath);
        _snapshot = new(fullPath, new LibraryConfiguration(fullPath), 1);
    }

    public string? ConfigPath => _snapshot.ConfigPath;
    public LibraryConfiguration? Configuration => _snapshot.Configuration;
    public AppConfigurationSnapshot GetSnapshot() => _snapshot;
    public event EventHandler? ConfigurationChanged { add { } remove { } }
    public void LoadConfig(string path) =>
        throw new NotSupportedException("Command-line library settings are immutable.");
    public string? GetRememberedConfigPath() => null;
    public IReadOnlyList<string> RecentConfigPaths => [];
    public void ClearRecentConfigs() { }
    public string? GetPreference(string key) => null;
    public void SetPreference(string key, string? value) { }
}
