using System.Text.Json;
using System.Xml.Linq;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

/// <inheritdoc cref="IAppSettings"/>
public sealed class AppSettings : IAppSettings
{
    private const int MaxRecentConfigs = 12;
    private const int CurrentStateSchemaVersion = 2;

    private sealed record PersistedState(
        int SchemaVersion,
        string? ConfigPath,
        Dictionary<string, string>? Preferences,
        List<string>? RecentConfigs);

    private sealed record LegacyPersistedState(
        string? ConfigPath,
        Dictionary<string, string>? Preferences,
        List<string>? RecentConfigs);

    private enum StateReadKind
    {
        Missing,
        Invalid,
        Legacy,
        Current,
        Future,
    }

    private sealed record StateReadResult(
        StateReadKind Kind,
        PersistedState? State = null,
        byte[]? SourceBytes = null);

    private static readonly string DefaultStateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MusicLibraryTools", "app-settings.json");

    private readonly object _sync = new();
    private readonly string _stateFile;
    private readonly Dictionary<string, string> _preferences;
    private readonly List<string> _recentConfigs;
    private readonly bool _persistenceReadOnly;
    private string? _rememberedConfigPath;
    private string? _configPath;
    private LibraryConfiguration? _configuration;
    private long _configurationVersion;

    public AppSettings() : this(DefaultStateFile)
    {
    }

    /// <summary>
    /// Creates settings backed by a caller-selected state file. Primarily useful to isolate tests or
    /// portable deployments from the user's normal roaming profile.
    /// </summary>
    public AppSettings(string stateFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFile);
        _stateFile = Path.GetFullPath(stateFile);
        StateReadResult loaded = TryLoad();
        PersistedState? state = loaded.State;
        _persistenceReadOnly =
            loaded.Kind == StateReadKind.Future;
        _rememberedConfigPath = state?.ConfigPath;
        _preferences = state?.Preferences ?? new();
        _recentConfigs = state?.RecentConfigs ?? new();
        if (loaded.Kind == StateReadKind.Legacy &&
            loaded.SourceBytes is not null)
            TryPersistMigration(
                state!,
                loaded.SourceBytes);
    }

    public string? ConfigPath { get { lock (_sync) return _configPath; } }
    public LibraryConfiguration? Configuration { get { lock (_sync) return _configuration; } }

    public AppConfigurationSnapshot GetSnapshot()
    {
        lock (_sync)
            return new AppConfigurationSnapshot(_configPath, _configuration, _configurationVersion);
    }

    public event EventHandler? ConfigurationChanged;

    public void LoadConfig(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var configuration = LoadValidatedConfiguration(fullPath);

        // Commit all observable state together only after validation has succeeded.
        lock (_sync)
        {
            _configuration = configuration;
            _configPath = fullPath;
            _configurationVersion++;
            _rememberedConfigPath = fullPath;
            AddRecent(fullPath);
            Persist();
        }
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    private static LibraryConfiguration LoadValidatedConfiguration(string path)
    {
        var root = XDocument.Load(path).Element("LibraryConfiguration")
            ?? throw new InvalidDataException("Missing <LibraryConfiguration> root element.");

        var database = (string?)root.Element("DatabaseFile");
        if (database is not null && string.IsNullOrWhiteSpace(database))
            throw new InvalidDataException("<DatabaseFile> cannot be empty.");
        if (database?.Equals("sqlite:", StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidDataException("A sqlite: database specification must include a path.");

        ValidatePositiveInteger(root, "LengthLimit");
        ValidatePositiveInteger(root, "DiscNumLengthLimit");

        // Eagerly materialize the deferred parser API so malformed target attributes fail before
        // the active configuration is replaced and remembered.
        var configuration = new LibraryConfiguration(path);
        var indexLocations = configuration.IndexLocations.ToList();
        _ = configuration.PlaylistSources;
        var playlistTargets = configuration.PlaylistTargets;
        var configuredSets = indexLocations.SelectMany(location => location.Sets)
            .ToHashSet(LibraryConfiguration.ScanSetComparer);
        foreach (var target in playlistTargets)
        {
            string[] unknownSets = target.Sets.Where(set => !configuredSets.Contains(set)).ToArray();
            if (unknownSets.Length > 0)
                throw new InvalidDataException(
                    $"Playlist target '{target.Target}' references scan set(s) with no IndexTarget: " +
                    string.Join(",", unknownSets));

            var selectedSets = target.Sets.ToHashSet(LibraryConfiguration.ScanSetComparer);
            foreach (var group in indexLocations.GroupBy(
                         location => Path.TrimEndingDirectorySeparator(location.Target),
                         OperatingSystem.IsWindows()
                             ? StringComparer.OrdinalIgnoreCase
                             : StringComparer.Ordinal))
            {
                int offsetCount = group.SelectMany(location => location.Memberships
                        .Where(membership => selectedSets.Contains(membership.Name))
                        .Select(membership => membership.Offset ?? location.DefaultOffset))
                    .Distinct(StringComparer.Ordinal)
                    .Take(2)
                    .Count();
                if (offsetCount > 1)
                    throw new InvalidDataException(
                        $"Playlist target '{target.Target}' selects scan sets with different offsets " +
                        $"for index target '{group.Key}'.");
            }
        }
        if (configuration.ItunesLibraryPath is { } library &&
            !Path.GetExtension(library).Equals(".itl", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("<ItunesLibrary> must identify an .itl file.");
        _ = configuration.FfmpegPath;
        _ = configuration.WavpackPath;
        _ = configuration.IngestSettings;
        _ = configuration.ArtworkHealthSettings;
        _ = configuration.CrossSyncTarget;
        _ = configuration.DeleteStaleCrossSyncFiles;
        _ = configuration.CleanCrossSyncPlaylists;
        _ = configuration.DatabaseFile;
        _ = configuration.ActiveProfile;
        _ = configuration.ActiveIngestProfile;
        _ = configuration.PolicySnapshot;
        return configuration;
    }

    private static void ValidatePositiveInteger(XElement root, string elementName)
    {
        if (root.Element(elementName) is not { } element)
            return;
        if (!int.TryParse(element.Value, out var value) || value <= 0)
            throw new InvalidDataException($"<{elementName}> must be a positive integer.");
    }

    public string? GetRememberedConfigPath()
    {
        lock (_sync)
            return _rememberedConfigPath is not null && File.Exists(_rememberedConfigPath) ? _rememberedConfigPath : null;
    }

    public IReadOnlyList<string> RecentConfigPaths
    {
        get { lock (_sync) return _recentConfigs.ToArray(); }
    }

    public void ClearRecentConfigs()
    {
        lock (_sync)
        {
            _recentConfigs.Clear();
            Persist();
        }
    }

    // Move the path to the most-recent slot (case-insensitive de-dupe) and cap the list.
    private void AddRecent(string path)
    {
        _recentConfigs.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _recentConfigs.Insert(0, path);
        if (_recentConfigs.Count > MaxRecentConfigs)
            _recentConfigs.RemoveRange(MaxRecentConfigs, _recentConfigs.Count - MaxRecentConfigs);
    }

    public string? GetPreference(string key)
    {
        lock (_sync)
            return _preferences.TryGetValue(key, out var value) ? value : null;
    }

    public void SetPreference(string key, string? value)
    {
        lock (_sync)
        {
            if (value is null)
                _preferences.Remove(key);
            else
                _preferences[key] = value;
            Persist();
        }
    }

    private StateReadResult TryLoad()
    {
        StateReadResult primary =
            ReadStateFile(_stateFile);
        if (primary.Kind is StateReadKind.Legacy or
            StateReadKind.Current or
            StateReadKind.Future)
            return primary;

        StateReadResult rollback =
            ReadStateFile(RollbackPath);
        if (rollback.Kind is StateReadKind.Legacy or
            StateReadKind.Current)
            return rollback;
        return primary;
    }

    private static StateReadResult ReadStateFile(
        string path)
    {
        if (!File.Exists(path))
            return new(StateReadKind.Missing);
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            using JsonDocument json =
                JsonDocument.Parse(bytes);
            JsonElement root = json.RootElement;
            if (root.ValueKind !=
                JsonValueKind.Object)
                return new(
                    StateReadKind.Invalid);
            if (!root.TryGetProperty(
                    nameof(
                        PersistedState.SchemaVersion),
                    out JsonElement schema))
            {
                LegacyPersistedState? legacy =
                    JsonSerializer.Deserialize<
                        LegacyPersistedState>(bytes);
                return legacy is null
                    ? new(StateReadKind.Invalid)
                    : new(
                        StateReadKind.Legacy,
                        new(
                            CurrentStateSchemaVersion,
                            legacy.ConfigPath,
                            legacy.Preferences,
                            legacy.RecentConfigs),
                        bytes);
            }

            if (!schema.TryGetInt32(
                    out int version) ||
                version < 1)
                return new(StateReadKind.Invalid);
            if (version >
                CurrentStateSchemaVersion)
                return new(
                    StateReadKind.Future);
            PersistedState? state =
                JsonSerializer.Deserialize<
                    PersistedState>(bytes);
            return state is null
                ? new(StateReadKind.Invalid)
                : new(
                    version ==
                    CurrentStateSchemaVersion
                        ? StateReadKind.Current
                        : StateReadKind.Legacy,
                    state with
                    {
                        SchemaVersion =
                            CurrentStateSchemaVersion,
                    },
                    bytes);
        }
        catch
        {
            return new(StateReadKind.Invalid);
        }
    }

    private void Persist()
    {
        if (_persistenceReadOnly)
            return;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                CurrentState());
            var writes =
                new List<(
                    string Path,
                    Action<Stream> Write)>
                {
                    (_stateFile,
                        stream =>
                            stream.Write(bytes)),
                };
            StateReadResult existing =
                ReadStateFile(_stateFile);
            if (existing.Kind is
                    StateReadKind.Legacy or
                    StateReadKind.Current &&
                existing.SourceBytes is
                    { } previous)
                writes.Add(
                    (RollbackPath,
                        stream =>
                            stream.Write(previous)));
            AtomicFile.WriteMany(writes);
        }
        catch
        {
            // Persistence is best-effort; a failure here shouldn't break loading a config.
        }
    }

    private void TryPersistMigration(
        PersistedState state,
        byte[] legacyBytes)
    {
        try
        {
            byte[] current =
                JsonSerializer.SerializeToUtf8Bytes(
                    state);
            var writes =
                new List<(
                    string Path,
                    Action<Stream> Write)>
                {
                    (_stateFile,
                        stream =>
                            stream.Write(current)),
                };
            string legacyBackup =
                _stateFile + ".v1.bak";
            if (!File.Exists(legacyBackup))
                writes.Add(
                    (legacyBackup,
                        stream =>
                            stream.Write(
                                legacyBytes)));
            AtomicFile.WriteMany(writes);
        }
        catch
        {
            // Keep the legacy source intact if migration cannot be committed.
        }
    }

    private PersistedState CurrentState() =>
        new(
            CurrentStateSchemaVersion,
            _rememberedConfigPath,
            new(_preferences),
            new(_recentConfigs));

    private string RollbackPath =>
        _stateFile + ".rollback.bak";
}
