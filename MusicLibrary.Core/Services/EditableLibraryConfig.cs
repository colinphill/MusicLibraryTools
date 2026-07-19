using System.Xml.Linq;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

public sealed class IndexTargetSetEntry
{
    public string Name { get; set; } = "";
    public string? Offset { get; set; }
}

/// <summary>One scan root and its set-specific playlist path mappings.</summary>
public sealed class IndexTargetEntry
{
    public string Target { get; set; } = "";
    public string? DefaultOffset { get; set; }
    public List<IndexTargetSetEntry> Memberships { get; set; } = [];
    public string? Filter { get; set; }
    public bool Organize { get; set; } = true;
    public bool UseItunesCanonicalNaming { get; set; }
    public LibraryIngestRole IngestRole { get; set; }
    public bool IsSyncTarget { get; set; }
}

/// <summary>One repeatable playlist export destination.</summary>
public sealed class PlaylistTargetEntry
{
    public string Target { get; set; } = "";
    public string Type { get; set; } = "m3u";
    public List<string> Sets { get; set; } = [];
}

/// <summary>
/// A read/write view of the LibraryConfiguration XML (the read-only <c>LibraryConfiguration</c> in
/// MusicFileUtilities can't create or edit files). Round-trips the elements the GUI edits and
/// preserves any unknown top-level elements that were already in the file.
/// </summary>
public sealed class EditableLibraryConfig
{
    public string DatabaseFile { get; set; } = "cache.db";
    public string? ItunesLibraryPath { get; set; }
    public string FfmpegPath { get; set; } = "ffmpeg";
    public int LengthLimit { get; set; } = 255;
    public int DiscNumLengthLimit { get; set; } = 255;
    public string AacEncoder { get; set; } = "libfdk_aac";
    public int AacBitrateKbps { get; set; } = 256;
    public bool DeleteSourcesAfterIngest { get; set; }
    public bool RemoveNonMusicAfterIngest { get; set; }
    public bool DeleteStaleCrossSyncFiles { get; set; }
    public bool CleanCrossSyncPlaylists { get; set; }
    public List<string> SyncPlaylists { get; set; } = [];
    public List<PlaylistTargetEntry> PlaylistTargets { get; set; } = [];
    public List<IndexTargetEntry> IndexTargets { get; set; } = [];

    // Top-level elements we don't model explicitly, kept so a save doesn't drop them.
    private readonly List<XElement> _passthrough = [];

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "DatabaseFile", "ItunesLibrary", "FfmpegPath", "LengthLimit", "DiscNumLengthLimit",
        "SyncTarget", "SyncPlaylist", "PlaylistTarget", "PlaylistType", "IndexTarget",
        "IngestSettings", "CrossSyncMusicSettings", "CrossSyncPlaylistsSettings",
    };

    public static EditableLibraryConfig Load(string path)
    {
        var root = XDocument.Load(path).Element("LibraryConfiguration")
            ?? throw new InvalidDataException("Missing <LibraryConfiguration> root element.");

        var config = new EditableLibraryConfig
        {
            DatabaseFile = (string?)root.Element("DatabaseFile") ?? "cache.db",
            ItunesLibraryPath = (string?)root.Element("ItunesLibrary"),
            FfmpegPath = (string?)root.Element("FfmpegPath") ?? "ffmpeg",
        };

        if (int.TryParse((string?)root.Element("LengthLimit"), out var ll)) config.LengthLimit = ll;
        if (int.TryParse((string?)root.Element("DiscNumLengthLimit"), out var dl)) config.DiscNumLengthLimit = dl;

        var parsed = new LibraryConfiguration(path);
        LibraryIngestSettings ingest = parsed.IngestSettings;
        config.AacEncoder = ingest.AacEncoder;
        config.AacBitrateKbps = ingest.AacBitrateKbps;
        config.DeleteSourcesAfterIngest = ingest.DeleteSourcesAfterIngest;
        config.RemoveNonMusicAfterIngest = ingest.RemoveNonMusicAfterIngest;
        config.DeleteStaleCrossSyncFiles = parsed.DeleteStaleCrossSyncFiles;
        config.CleanCrossSyncPlaylists = parsed.CleanCrossSyncPlaylists;
        foreach (LibraryIndexLocation location in parsed.IndexLocations)
        {
            config.IndexTargets.Add(new IndexTargetEntry
            {
                Target = location.Target,
                DefaultOffset = location.DefaultOffset,
                Organize = location.Organize,
                UseItunesCanonicalNaming = location.UseItunesCanonicalNaming,
                IngestRole = location.IngestRole,
                IsSyncTarget = location.IsSyncTarget,
                Memberships = location.Memberships.Select(membership => new IndexTargetSetEntry
                {
                    Name = membership.Name,
                    Offset = membership.Offset,
                }).ToList(),
                Filter = location.Filter,
            });
        }

        // Opening and saving a legacy standalone SyncTarget migrates it to an IndexTarget flag.
        string? legacySyncTarget = CleanOptional((string?)root.Element("SyncTarget"));
        if (legacySyncTarget is not null &&
            !config.IndexTargets.Any(target => target.IsSyncTarget))
        {
            IndexTargetEntry? target = config.IndexTargets.FirstOrDefault(candidate =>
                PathComparer.Equals(
                    Path.TrimEndingDirectorySeparator(candidate.Target),
                    Path.TrimEndingDirectorySeparator(legacySyncTarget)));
            if (target is null)
            {
                target = new IndexTargetEntry { Target = legacySyncTarget };
                config.IndexTargets.Add(target);
            }
            target.IsSyncTarget = true;
        }
        config.SyncPlaylists.AddRange(root.Elements("SyncPlaylist")
            .Select(element => element.Value.Trim())
            .Where(value => value.Length > 0));

        // Preserve the old standalone PlaylistType value in the editor so a legacy row is easy to
        // migrate. Saving always writes Type and Set attributes on each PlaylistTarget.
        string? legacyPlaylistType = (string?)root.Element("PlaylistType");
        foreach (var e in root.Elements("PlaylistTarget"))
        {
            config.PlaylistTargets.Add(new PlaylistTargetEntry
            {
                Target = e.Value,
                Type = (string?)e.Attribute("Type") ?? legacyPlaylistType ?? "m3u",
                Sets = [.. LibraryConfiguration.ParseScanSets((string?)e.Attribute("Set"))],
            });
        }

        foreach (var e in root.Elements())
            if (!Known.Contains(e.Name.LocalName))
                config._passthrough.Add(new XElement(e));

        return config;
    }

    public void Save(string path)
    {
        var root = new XElement("LibraryConfiguration");

        root.Add(new XElement("DatabaseFile", DatabaseFile));
        if (!string.IsNullOrWhiteSpace(ItunesLibraryPath))
            root.Add(new XElement("ItunesLibrary", ItunesLibraryPath.Trim()));
        root.Add(new XElement("FfmpegPath",
            string.IsNullOrWhiteSpace(FfmpegPath) ? "ffmpeg" : FfmpegPath.Trim()));

        foreach (var t in IndexTargets)
        {
            if (string.IsNullOrWhiteSpace(t.Target))
                continue;
            var e = new XElement("IndexTarget", new XAttribute("Path", t.Target.Trim()));
            if (!string.IsNullOrWhiteSpace(t.DefaultOffset))
                e.SetAttributeValue("Offset", t.DefaultOffset.Trim());
            if (!string.IsNullOrEmpty(t.Filter)) e.SetAttributeValue("Filter", t.Filter);
            if (!t.Organize) e.SetAttributeValue("Organize", false);
            if (t.UseItunesCanonicalNaming)
                e.SetAttributeValue("ItunesCanonicalNaming", true);
            if (t.IngestRole != LibraryIngestRole.None)
                e.SetAttributeValue("IngestRole", t.IngestRole);
            if (t.IsSyncTarget)
                e.SetAttributeValue("SyncTarget", true);
            var seen = new HashSet<string>(LibraryConfiguration.ScanSetComparer);
            foreach (IndexTargetSetEntry membership in t.Memberships)
            {
                string name = LibraryConfiguration.ParseScanSetName(membership.Name);
                if (!seen.Add(name))
                    throw new InvalidDataException(
                        $"Index target '{t.Target}' contains duplicate scan set '{name}'.");
                var set = new XElement("Set", new XAttribute("Name", name));
                if (!string.IsNullOrWhiteSpace(membership.Offset))
                    set.SetAttributeValue("Offset", membership.Offset.Trim());
                e.Add(set);
            }
            root.Add(e);
        }

        IndexTargetEntry[] syncTargets = IndexTargets
            .Where(target => !string.IsNullOrWhiteSpace(target.Target) && target.IsSyncTarget)
            .ToArray();
        if (syncTargets.Length > 1)
            throw new InvalidDataException(
                "Only one IndexTarget may be selected as the cross-library sync target.");
        foreach (string playlist in SyncPlaylists
                     .Select(value => value?.Trim() ?? "")
                     .Where(value => value.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            root.Add(new XElement("SyncPlaylist", playlist));

        root.Add(new XElement("CrossSyncMusicSettings",
            new XAttribute("DeleteStaleFiles", DeleteStaleCrossSyncFiles)));
        root.Add(new XElement("CrossSyncPlaylistsSettings",
            new XAttribute("Clean", CleanCrossSyncPlaylists)));

        var configuredSets = IndexTargets.SelectMany(indexTarget => indexTarget.Memberships)
            .Select(membership => LibraryConfiguration.ParseScanSetName(membership.Name))
            .ToHashSet(LibraryConfiguration.ScanSetComparer);
        foreach (var target in PlaylistTargets)
        {
            if (string.IsNullOrWhiteSpace(target.Target))
                continue;
            string type = target.Type?.Trim().ToLowerInvariant() ?? "";
            if (type is not ("m3u" or "wpl"))
                throw new InvalidDataException(
                    $"Playlist target '{target.Target}' must have a type of 'm3u' or 'wpl'.");
            if (target.Sets.Count == 0)
                throw new InvalidDataException(
                    $"Playlist target '{target.Target}' must select at least one scan set.");
            string[] normalizedSets = target.Sets.Select(LibraryConfiguration.ParseScanSetName)
                .Distinct(LibraryConfiguration.ScanSetComparer)
                .OrderBy(set => set, LibraryConfiguration.ScanSetComparer).ToArray();
            string[] unknownSets = normalizedSets.Where(set => !configuredSets.Contains(set)).ToArray();
            if (unknownSets.Length > 0)
                throw new InvalidDataException(
                    $"Playlist target '{target.Target}' references scan set(s) with no IndexTarget: " +
                    string.Join(",", unknownSets));

            root.Add(new XElement("PlaylistTarget",
                new XAttribute("Type", type),
                new XAttribute("Set", string.Join(",", normalizedSets)),
                target.Target));
        }

        ValidatePlaylistOffsets();

        if (AacBitrateKbps <= 0)
            throw new InvalidDataException("AAC bitrate must be a positive integer.");
        root.Add(new XElement("IngestSettings",
            new XAttribute("AacEncoder",
                string.IsNullOrWhiteSpace(AacEncoder) ? "libfdk_aac" : AacEncoder.Trim()),
            new XAttribute("AacBitrateKbps", AacBitrateKbps),
            new XAttribute("DeleteSourcesAfterIngest", DeleteSourcesAfterIngest),
            new XAttribute("RemoveNonMusicAfterIngest", RemoveNonMusicAfterIngest)));

        string[] duplicateRoles = IndexTargets
            .Where(target => target.IngestRole != LibraryIngestRole.None)
            .GroupBy(target => target.IngestRole)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key.ToString())
            .ToArray();
        if (duplicateRoles.Length > 0)
            throw new InvalidDataException(
                "Each ingest role may be assigned to only one IndexTarget: " +
                string.Join(", ", duplicateRoles));

        root.Add(new XElement("LengthLimit", LengthLimit));
        root.Add(new XElement("DiscNumLengthLimit", DiscNumLengthLimit));

        foreach (var e in _passthrough)
            root.Add(e);

        var document = new XDocument(root);
        AtomicFile.Write(path, document.Save);
    }

    private void ValidatePlaylistOffsets()
    {
        foreach (PlaylistTargetEntry target in PlaylistTargets.Where(target =>
                     !string.IsNullOrWhiteSpace(target.Target)))
        {
            var selected = target.Sets.ToHashSet(LibraryConfiguration.ScanSetComparer);
            foreach (var roots in IndexTargets.Where(root => !string.IsNullOrWhiteSpace(root.Target))
                         .GroupBy(root => Path.TrimEndingDirectorySeparator(root.Target),
                             OperatingSystem.IsWindows()
                                 ? StringComparer.OrdinalIgnoreCase
                                 : StringComparer.Ordinal))
            {
                string[] offsets = roots.SelectMany(root => root.Memberships
                        .Where(membership => selected.Contains(membership.Name))
                        .Select(membership => string.IsNullOrWhiteSpace(membership.Offset)
                            ? root.DefaultOffset ?? ""
                            : membership.Offset))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (offsets.Length > 1)
                    throw new InvalidDataException(
                        $"Playlist target '{target.Target}' selects scan sets with different offsets " +
                        $"for index target '{roots.Key}'. Select one mapping or make their offsets equal.");
            }
        }
    }

    private static string? CleanOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
