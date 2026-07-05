using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

/// <summary>
/// Holds the currently loaded <see cref="LibraryConfiguration"/> and remembers the last-used
/// config file path across launches (persisted to app-local storage).
/// </summary>
public interface IAppSettings
{
    /// <summary>Path of the currently loaded LibraryConfiguration XML, or null if none loaded.</summary>
    string? ConfigPath { get; }

    /// <summary>The loaded configuration, or null if none has been loaded.</summary>
    LibraryConfiguration? Configuration { get; }

    /// <summary>Raised when <see cref="Configuration"/> changes.</summary>
    event EventHandler? ConfigurationChanged;

    /// <summary>Load a LibraryConfiguration XML file and remember it as the last-used config.</summary>
    void LoadConfig(string path);

    /// <summary>The last-used config path from a previous session, if any and still present.</summary>
    string? GetRememberedConfigPath();

    /// <summary>Read a persisted UI preference by key (e.g. the details-grid column layout), or null.</summary>
    string? GetPreference(string key);

    /// <summary>Persist a UI preference; passing null removes it. Written to app-local storage immediately.</summary>
    void SetPreference(string key, string? value);
}
