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
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Target { get; set; } = "";
    public string? ProfileId { get; set; }
    public LibraryRootPermissions? Permissions { get; set; }
    public string? DefaultOffset { get; set; }
    public List<IndexTargetSetEntry> Memberships { get; set; } = [];
    public List<string> IndexFormats { get; set; } = [];
    public List<string> IndexIncludePatterns { get; set; } = [];
    public List<string> IndexExcludePatterns { get; set; } = [];
    public LibraryRepresentationRole RepresentationRole { get; set; } =
        LibraryRepresentationRole.LegacyAutomatic;
    public string? Filter { get; set; }
    public bool Organize { get; set; } = true;
    public bool UseItunesCanonicalNaming { get; set; }
    public LibraryIngestRole IngestRole { get; set; }
    public bool IsSyncTarget { get; set; }

    internal XElement? SourceElement { get; set; }
}

/// <summary>One repeatable playlist export destination.</summary>
public sealed class PlaylistTargetEntry
{
    public string Target { get; set; } = "";
    public string Type { get; set; } = "m3u";
    public List<string> Sets { get; set; } = [];
    public string PathStyle { get; set; } = "legacy";
    public string Encoding { get; set; } = "utf-8";
    public bool EmitByteOrderMark { get; set; } = true;
    public string LineEnding { get; set; } = "platform";
    public bool IncludeExtendedInfo { get; set; } = true;
    public string FileNameTransform { get; set; } = "legacy";
    public int MaxTrackCount { get; set; } = 500;
    public LibraryPathCollisionPolicy CollisionPolicy { get; set; } =
        LibraryPathCollisionPolicy.Stop;

    internal XElement? SourceElement { get; set; }
}

/// <summary>One catalog-independent playlist input file or directory.</summary>
public sealed class PlaylistSourceEntry
{
    public string Location { get; set; } = "";
    public string Type { get; set; } = "m3u";
    public bool Recursive { get; set; }

    internal XElement? SourceElement { get; set; }
}

public enum LibraryConfigurationIssueSeverity
{
    Warning,
    Error,
}

public sealed record LibraryConfigurationIssue(
    string Code,
    string Message,
    LibraryConfigurationIssueSeverity Severity);

/// <summary>
/// A read/write view of the LibraryConfiguration XML (the read-only <c>LibraryConfiguration</c> in
/// MusicFileUtilities can't create or edit files). Round-trips the elements the GUI edits and
/// preserves any unknown top-level elements that were already in the file.
/// </summary>
public sealed class EditableLibraryConfig
{
    /// <summary>
    /// The default constructor keeps the historical programmatic behavior. GUI-created libraries
    /// should use <see cref="CreateNew"/> to start with preservation-first defaults.
    /// </summary>
    public EditableLibraryConfig()
    {
    }

    public int SchemaVersion { get; private set; } = LibraryConfigurationSchema.CurrentVersion;
    public Guid LibraryId { get; set; } = Guid.NewGuid();
    public string ActiveProfileId { get; set; } = LibraryProfilePresets.LegacyId;
    public List<LibraryProfile> Profiles { get; set; } = [.. LibraryProfilePresets.All];
    /// <summary>
    /// Explicitly configured export profiles. Disabled built-in definitions are intentionally not
    /// copied here until a user configures one.
    /// </summary>
    public List<LibraryExportProfile> ExportProfiles { get; set; } = [];
    /// <summary>
    /// Optional companion file for machine-local root, database, and tool paths. Relative values
    /// are resolved beside the portable configuration file.
    /// </summary>
    public string? MachineBindingsFile { get; set; }
    public string DatabaseFile { get; set; } = "cache.db";
    public string? ItunesLibraryPath { get; set; }
    public string FfmpegPath { get; set; } = "ffmpeg";
    public int LengthLimit { get; set; } = 255;
    public int DiscNumLengthLimit { get; set; } = 255;
    public string AacEncoder { get; set; } = "libfdk_aac";
    public int AacBitrateKbps { get; set; } = 256;
    public int OversizedArtworkByteThreshold { get; set; } =
        LibraryArtworkHealthSettings.DefaultOversizedByteThreshold;
    public int OversizedArtworkDimensionThreshold { get; set; } =
        LibraryArtworkHealthSettings.DefaultOversizedDimensionThreshold;
    public int ArtworkRepairTargetByteSize { get; set; } =
        LibraryArtworkHealthSettings.DefaultRepairTargetByteSize;
    public int ArtworkRepairTargetDimension { get; set; } =
        LibraryArtworkHealthSettings.DefaultRepairTargetDimension;
    public bool DeleteSourcesAfterIngest { get; set; }
    public bool RemoveNonMusicAfterIngest { get; set; }
    public bool DeleteStaleCrossSyncFiles { get; set; }
    public bool CleanCrossSyncPlaylists { get; set; }
    public List<string> SyncPlaylists { get; set; } = [];
    public List<PlaylistSourceEntry> PlaylistSources { get; set; } = [];
    public List<PlaylistTargetEntry> PlaylistTargets { get; set; } = [];
    public List<IndexTargetEntry> IndexTargets { get; set; } = [];

    public LibraryProfile ActiveProfile => Profiles.SingleOrDefault(profile => string.Equals(
        profile.Id, ActiveProfileId, StringComparison.OrdinalIgnoreCase)) ??
        throw new InvalidDataException(
            $"ActiveProfileId '{ActiveProfileId}' does not identify a configured profile.");

    /// <summary>Creates a new library with the catalog-only profile active.</summary>
    public static EditableLibraryConfig CreateNew()
    {
        var configuration = new EditableLibraryConfig
        {
            ActiveProfileId = LibraryProfilePresets.CatalogOnlyId,
        };
        configuration.Profiles.RemoveAll(profile =>
            profile.Preset == LibraryProfilePreset.LegacyMusicLibraryTools);
        return configuration;
    }

    public IndexTargetEntry CreateIndexTarget(string target = "") => new()
    {
        Target = target,
        ProfileId = ActiveProfileId,
        Permissions = ActiveProfile.DefaultRootPermissions,
        Organize = ActiveProfile.DefaultRootPermissions.HasFlag(
            LibraryRootPermissions.OrganizeFiles),
        UseItunesCanonicalNaming = ActiveProfile.Naming.UseItunesCanonicalNaming,
        RepresentationRole = ActiveProfile.Preset ==
            LibraryProfilePreset.LegacyMusicLibraryTools
                ? LibraryRepresentationRole.LegacyAutomatic
                : LibraryRepresentationRole.Ignore,
    };

    // Top-level elements we don't model explicitly, kept so a save doesn't drop them.
    private readonly List<XElement> _passthrough = [];
    private XElement? _sourceRoot;
    private XElement? _sourceBindingsRoot;
    private string? _loadedPath;
    private string? _loadedBindingsPath;

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "DatabaseFile", "ItunesLibrary", "FfmpegPath", "LengthLimit", "DiscNumLengthLimit",
        "SyncTarget", "SyncPlaylist", "PlaylistSource", "PlaylistTarget", "PlaylistType", "IndexTarget",
        "IngestSettings", "ArtworkHealthSettings", "CrossSyncMusicSettings",
        "CrossSyncPlaylistsSettings", "LibraryProfile", "ExportProfile", "MachineBindings",
    };

    public static EditableLibraryConfig Load(string path)
    {
        var root = XDocument.Load(path).Element("LibraryConfiguration")
            ?? throw new InvalidDataException("Missing <LibraryConfiguration> root element.");

        var parsed = new LibraryConfiguration(path);
        var config = new EditableLibraryConfig
        {
            SchemaVersion = parsed.SchemaVersion,
            LibraryId = parsed.LibraryId,
            ActiveProfileId = parsed.ActiveProfileId,
            Profiles = [.. parsed.Profiles],
            ExportProfiles = [.. parsed.ExportProfiles],
            MachineBindingsFile = CleanOptional(
                (string?)root.Element("MachineBindings")?.Attribute("File")),
            DatabaseFile = parsed.MachineBindings?.DatabaseFile ??
                (string?)root.Element("DatabaseFile") ?? "cache.db",
            ItunesLibraryPath = parsed.MachineBindings?.ItunesLibraryPath ??
                (string?)root.Element("ItunesLibrary"),
            FfmpegPath = parsed.MachineBindings?.FfmpegPath ??
                (string?)root.Element("FfmpegPath") ?? "ffmpeg",
            _sourceRoot = new XElement(root),
            _loadedPath = Path.GetFullPath(path),
        };
        if (parsed.MachineBindings is { } bindings)
        {
            config._loadedBindingsPath = bindings.SourcePath;
            config._sourceBindingsRoot = XDocument.Load(bindings.SourcePath)
                .Element("LibraryBindings") is { } bindingsRoot
                    ? new XElement(bindingsRoot)
                    : null;
        }

        if (int.TryParse((string?)root.Element("LengthLimit"), out var ll)) config.LengthLimit = ll;
        if (int.TryParse((string?)root.Element("DiscNumLengthLimit"), out var dl)) config.DiscNumLengthLimit = dl;

        LibraryIngestSettings ingest = parsed.IngestSettings;
        config.AacEncoder = ingest.AacEncoder;
        config.AacBitrateKbps = ingest.AacBitrateKbps;
        config.DeleteSourcesAfterIngest = ingest.DeleteSourcesAfterIngest;
        config.RemoveNonMusicAfterIngest = ingest.RemoveNonMusicAfterIngest;
        // In schema v1 the standalone boolean was the source-disposition policy. Once the
        // configuration becomes v2, ingest reads that policy from the named profile instead.
        // Project the old opt-in into the cloned legacy preset before the first save so migration
        // cannot turn permanent deletion back into quarantine.
        if (parsed.SchemaVersion == LibraryConfigurationSchema.LegacyVersion &&
            ingest.DeleteSourcesAfterIngest)
        {
            int legacyIndex = config.Profiles.FindIndex(profile => string.Equals(
                profile.Id, LibraryProfilePresets.LegacyId,
                StringComparison.OrdinalIgnoreCase));
            if (legacyIndex >= 0)
            {
                LibraryProfile legacy = config.Profiles[legacyIndex];
                config.Profiles[legacyIndex] = legacy with
                {
                    Ingest = legacy.Ingest with
                    {
                        SourceDisposition = LibrarySourceDisposition.Delete,
                    },
                };
            }
        }
        LibraryArtworkHealthSettings artworkHealth = parsed.ArtworkHealthSettings;
        config.OversizedArtworkByteThreshold = artworkHealth.OversizedByteThreshold;
        config.OversizedArtworkDimensionThreshold = artworkHealth.OversizedDimensionThreshold;
        config.ArtworkRepairTargetByteSize = artworkHealth.RepairTargetByteSize;
        config.ArtworkRepairTargetDimension = artworkHealth.RepairTargetDimension;
        config.DeleteStaleCrossSyncFiles = parsed.DeleteStaleCrossSyncFiles;
        config.CleanCrossSyncPlaylists = parsed.CleanCrossSyncPlaylists;
        XElement[] sourceTargets = root.Elements("IndexTarget").ToArray();
        int targetIndex = 0;
        foreach (LibraryIndexLocation location in parsed.IndexLocations)
        {
            config.IndexTargets.Add(new IndexTargetEntry
            {
                Id = location.RootId,
                Target = location.Target,
                ProfileId = location.ProfileId,
                Permissions = location.Permissions,
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
                IndexFormats = [.. location.IndexFormats],
                IndexIncludePatterns = [.. location.IndexIncludePatterns],
                IndexExcludePatterns = [.. location.IndexExcludePatterns],
                RepresentationRole = location.RepresentationRole,
                Filter = location.Filter,
                SourceElement = targetIndex < sourceTargets.Length
                    ? new XElement(sourceTargets[targetIndex])
                    : null,
            });
            targetIndex++;
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
                target = new IndexTargetEntry
                {
                    Id = LibraryConfigurationSchema.CreateStableId(
                        $"legacy-sync-root|{config.LibraryId:D}|{legacySyncTarget}"),
                    Target = legacySyncTarget,
                    ProfileId = LibraryProfilePresets.LegacyId,
                    Permissions = LibraryRootPermissions.WriteMetadata |
                                  LibraryRootPermissions.WriteArtwork |
                                  LibraryRootPermissions.OrganizeFiles |
                                  LibraryRootPermissions.SynchronizeOutput,
                };
                config.IndexTargets.Add(target);
            }
            target.IsSyncTarget = true;
            target.Permissions = (target.Permissions ?? LibraryRootPermissions.None) |
                                 LibraryRootPermissions.SynchronizeOutput;
        }
        config.SyncPlaylists.AddRange(root.Elements("SyncPlaylist")
            .Select(element => element.Value.Trim())
            .Where(value => value.Length > 0));
        XElement[] sourcePlaylists = root.Elements("PlaylistSource").ToArray();
        foreach (XElement source in sourcePlaylists)
        {
            config.PlaylistSources.Add(new PlaylistSourceEntry
            {
                Location = source.Value.Trim(),
                Type = ((string?)source.Attribute("Type"))?.Trim() ?? "m3u",
                Recursive = bool.TryParse((string?)source.Attribute("Recursive"), out bool recursive) &&
                    recursive,
                SourceElement = new XElement(source),
            });
        }

        // Preserve the old standalone PlaylistType value in the editor so a legacy row is easy to
        // migrate. Saving always writes Type and Set attributes on each PlaylistTarget.
        string? legacyPlaylistType = (string?)root.Element("PlaylistType");
        foreach (var e in root.Elements("PlaylistTarget"))
        {
            LibraryPlaylistTarget? parsedTarget = legacyPlaylistType is null
                ? parsed.PlaylistTargets[config.PlaylistTargets.Count]
                : null;
            config.PlaylistTargets.Add(new PlaylistTargetEntry
            {
                Target = e.Value,
                Type = (string?)e.Attribute("Type") ?? legacyPlaylistType ?? "m3u",
                Sets = [.. LibraryConfiguration.ParseScanSets((string?)e.Attribute("Set"))],
                PathStyle = parsedTarget?.PathStyle ?? "legacy",
                Encoding = parsedTarget?.Encoding ?? "utf-8",
                EmitByteOrderMark = parsedTarget?.EmitByteOrderMark ?? true,
                LineEnding = parsedTarget?.LineEnding ?? "platform",
                IncludeExtendedInfo = parsedTarget?.IncludeExtendedInfo ?? true,
                FileNameTransform = parsedTarget?.FileNameTransform ?? "legacy",
                MaxTrackCount = parsedTarget?.MaxTrackCount ?? 500,
                CollisionPolicy = parsedTarget?.CollisionPolicy ??
                    LibraryPathCollisionPolicy.Stop,
                SourceElement = new XElement(e),
            });
        }

        foreach (var e in root.Elements())
            if (e.Name.Namespace != XNamespace.None || !Known.Contains(e.Name.LocalName))
                config._passthrough.Add(new XElement(e));

        config.MigrateLegacyRoleAssignments();
        return config;
    }

    public void Save(string path)
    {
        MigrateLegacyRoleAssignments();
        IReadOnlyList<LibraryConfigurationIssue> validation = Validate();
        LibraryConfigurationIssue? firstError = validation.FirstOrDefault(issue =>
            issue.Severity == LibraryConfigurationIssueSeverity.Error);
        if (firstError is not null)
            throw new InvalidDataException(firstError.Message);

        string? bindingsReference = CleanOptional(MachineBindingsFile);
        string fullConfigurationPath = Path.GetFullPath(path);
        string? fullBindingsPath = bindingsReference is null
            ? null
            : LibraryMachineBindings.ResolveReferencePath(
                fullConfigurationPath, bindingsReference);
        if (fullBindingsPath is not null &&
            PathComparer.Equals(fullBindingsPath, fullConfigurationPath))
            throw new InvalidDataException(
                "The machine bindings file must be different from the portable configuration file.");

        var root = new XElement("LibraryConfiguration",
            new XAttribute("SchemaVersion", LibraryConfigurationSchema.CurrentVersion),
            new XAttribute("LibraryId", LibraryId.ToString("D")),
            new XAttribute("ActiveProfileId", ActiveProfileId));

        foreach (LibraryProfile profile in Profiles)
        {
            XElement element = LibraryProfileXml.Write(profile);
            XElement? source = _sourceRoot?.Elements("LibraryProfile").FirstOrDefault(candidate =>
                string.Equals((string?)candidate.Attribute("Id"), profile.Id,
                    StringComparison.OrdinalIgnoreCase));
            if (source is not null)
                MergeUnknownElementData(element, source);
            root.Add(element);
        }

        foreach (LibraryExportProfile profile in ExportProfiles)
        {
            XElement element = LibraryExportProfileXml.Write(profile);
            if (bindingsReference is not null &&
                element.Element("Transport") is { } portableTransport)
            {
                portableTransport.SetAttributeValue("Destination", null);
                portableTransport.Elements("Option").Remove();
            }
            XElement? source = _sourceRoot?.Elements("ExportProfile").FirstOrDefault(candidate =>
                string.Equals((string?)candidate.Attribute("Id"), profile.Id,
                    StringComparison.OrdinalIgnoreCase));
            if (source is not null)
                MergeUnknownElementData(element, source);
            root.Add(element);
        }

        if (bindingsReference is not null)
            root.Add(new XElement("MachineBindings",
                new XAttribute("File", bindingsReference)));
        else
        {
            root.Add(new XElement("DatabaseFile", DatabaseFile));
            if (!string.IsNullOrWhiteSpace(ItunesLibraryPath))
                root.Add(new XElement("ItunesLibrary", ItunesLibraryPath.Trim()));
            root.Add(new XElement("FfmpegPath",
                string.IsNullOrWhiteSpace(FfmpegPath) ? "ffmpeg" : FfmpegPath.Trim()));
        }

        foreach (var t in IndexTargets)
        {
            if (string.IsNullOrWhiteSpace(t.Target))
                continue;
            string profileId = CleanOptional(t.ProfileId) ?? ActiveProfileId;
            LibraryProfile profile = Profiles.Single(candidate => string.Equals(
                candidate.Id, profileId, StringComparison.OrdinalIgnoreCase));
            LibraryRootPermissions permissions = EffectivePermissions(t, profile);
            var e = new XElement("IndexTarget",
                new XAttribute("Id", t.Id.ToString("D")),
                new XAttribute("ProfileId", profileId),
                new XAttribute("Permissions", LibraryProfileXml.FormatFlags(permissions)));
            if (bindingsReference is null)
                e.SetAttributeValue("Path", t.Target.Trim());
            if (!string.IsNullOrWhiteSpace(t.DefaultOffset))
                e.SetAttributeValue("Offset", t.DefaultOffset.Trim());
            IReadOnlyList<string> indexFormats =
                LibraryConfiguration.NormalizeIndexFormats(t.IndexFormats);
            IReadOnlyList<string> indexIncludes =
                LibraryConfiguration.NormalizeIndexPatterns(t.IndexIncludePatterns);
            IReadOnlyList<string> indexExcludes =
                LibraryConfiguration.NormalizeIndexPatterns(t.IndexExcludePatterns);
            if (indexFormats.Count > 0)
                e.SetAttributeValue("IndexFormats", string.Join(',', indexFormats));
            if (indexIncludes.Count > 0)
                e.SetAttributeValue("IndexInclude", string.Join(';', indexIncludes));
            if (indexExcludes.Count > 0)
                e.SetAttributeValue("IndexExclude", string.Join(';', indexExcludes));
            if (!string.IsNullOrEmpty(t.Filter)) e.SetAttributeValue("Filter", t.Filter);
            if (!t.Organize) e.SetAttributeValue("Organize", false);
            if (t.UseItunesCanonicalNaming)
                e.SetAttributeValue("ItunesCanonicalNaming", true);
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
            if (t.SourceElement is not null)
                MergeUnknownElementData(e, t.SourceElement);
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
        {
            var element = new XElement("SyncPlaylist", playlist);
            XElement? source = _sourceRoot?.Elements("SyncPlaylist").FirstOrDefault(candidate =>
                string.Equals(candidate.Value.Trim(), playlist,
                    StringComparison.OrdinalIgnoreCase));
            if (source is not null)
                MergeUnknownElementData(element, source);
            root.Add(element);
        }

        foreach (PlaylistSourceEntry source in PlaylistSources.Where(source =>
                     !string.IsNullOrWhiteSpace(source.Location)))
        {
            string type = source.Type?.Trim().ToLowerInvariant() ?? "";
            if (type != "m3u")
                throw new InvalidDataException(
                    $"Playlist source '{source.Location}' must have a type of 'm3u'.");
            var element = new XElement("PlaylistSource",
                new XAttribute("Type", type),
                new XAttribute("Recursive", source.Recursive),
                source.Location.Trim());
            if (source.SourceElement is not null)
                MergeUnknownElementData(element, source.SourceElement);
            root.Add(element);
        }

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
            if (type is not ("m3u" or "m3u8" or "wpl"))
                throw new InvalidDataException(
                    $"Playlist target '{target.Target}' must have a type of 'm3u', 'm3u8', or 'wpl'.");
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

            var element = new XElement("PlaylistTarget",
                new XAttribute("Type", type),
                new XAttribute("Set", string.Join(",", normalizedSets)),
                new XAttribute("PathStyle", NormalizePlaylistOption(target.PathStyle,
                    "legacy", "provided", "absolute", "relative")),
                new XAttribute("Encoding", NormalizePlaylistOption(target.Encoding,
                    "utf-8", "utf-16", "utf-16be", "ascii")),
                new XAttribute("Bom", target.EmitByteOrderMark),
                new XAttribute("LineEnding", NormalizePlaylistOption(target.LineEnding,
                    "platform", "crlf", "lf")),
                new XAttribute("ExtInf", target.IncludeExtendedInfo),
                new XAttribute("FileNameTransform", NormalizePlaylistOption(
                    target.FileNameTransform, "legacy", "preserve", "sanitize", "sonos")),
                new XAttribute("MaxTracks", target.MaxTrackCount),
                new XAttribute("Collision", target.CollisionPolicy),
                target.Target);
            if (target.SourceElement is not null)
                MergeUnknownElementData(element, target.SourceElement);
            root.Add(element);
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

        if (OversizedArtworkByteThreshold <= 0)
            throw new InvalidDataException("Oversized artwork byte threshold must be a positive integer.");
        if (OversizedArtworkDimensionThreshold <= 0)
            throw new InvalidDataException("Oversized artwork dimension threshold must be a positive integer.");
        if (ArtworkRepairTargetByteSize <= 0)
            throw new InvalidDataException("Artwork repair byte target must be a positive integer.");
        if (ArtworkRepairTargetDimension <= 0)
            throw new InvalidDataException("Artwork repair dimension target must be a positive integer.");
        root.Add(new XElement("ArtworkHealthSettings",
            new XAttribute("OversizedByteThreshold", OversizedArtworkByteThreshold),
            new XAttribute("OversizedDimensionThreshold", OversizedArtworkDimensionThreshold),
            new XAttribute("RepairTargetByteSize", ArtworkRepairTargetByteSize),
            new XAttribute("RepairTargetDimension", ArtworkRepairTargetDimension)));

        root.Add(new XElement("LengthLimit", LengthLimit));
        root.Add(new XElement("DiscNumLengthLimit", DiscNumLengthLimit));

        foreach (var e in _passthrough)
            root.Add(e);

        MergeUnknownRootData(root);

        var document = new XDocument(root);
        CreateLegacyBackupIfNeeded(path);
        XDocument? bindingsDocument = fullBindingsPath is null
            ? null
            : CreateMachineBindingsDocument();
        if (fullBindingsPath is null)
        {
            AtomicFile.Write(path, document.Save);
        }
        else
        {
            AtomicFile.WriteMany(
            [
                (fullBindingsPath, bindingsDocument!.Save),
                (path, document.Save),
            ]);
            _loadedBindingsPath = Path.GetFullPath(fullBindingsPath);
            _sourceBindingsRoot = new XElement(bindingsDocument.Root!);
        }
        SchemaVersion = LibraryConfigurationSchema.CurrentVersion;
        _loadedPath = Path.GetFullPath(path);
        _sourceRoot = new XElement(root);
    }

    /// <summary>
    /// Validates the complete editable model without changing the file system. Offline roots are
    /// warnings when requested and never prevent a configuration from being saved.
    /// </summary>
    public IReadOnlyList<LibraryConfigurationIssue> Validate(
        bool includePathAvailabilityWarnings = false)
    {
        var issues = new List<LibraryConfigurationIssue>();
        void Error(string code, string message) => issues.Add(new(
            code, message, LibraryConfigurationIssueSeverity.Error));
        void Warning(string code, string message) => issues.Add(new(
            code, message, LibraryConfigurationIssueSeverity.Warning));

        if (LibraryId == Guid.Empty)
            Error("library-id", "LibraryId must be a non-empty GUID.");
        if (Profiles.Count == 0)
            Error("profiles-empty", "At least one library profile is required.");

        foreach (LibraryProfile profile in Profiles)
        {
            try
            {
                LibraryProfileXml.Validate(profile);
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            {
                Error("profile-invalid", exception.Message);
            }
        }

        string[] duplicateProfiles = Profiles
            .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateProfiles.Length > 0)
            Error("profile-duplicate",
                "Library profile IDs must be unique: " + string.Join(", ", duplicateProfiles));

        foreach (LibraryExportProfile exportProfile in ExportProfiles)
        {
            try
            {
                LibraryExportProfileXml.Validate(exportProfile);
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            {
                Error("export-profile-invalid", exception.Message);
            }
        }

        string[] duplicateExportProfiles = ExportProfiles
            .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateExportProfiles.Length > 0)
            Error("export-profile-duplicate",
                "Export profile IDs must be unique: " +
                string.Join(", ", duplicateExportProfiles));

        if (string.IsNullOrWhiteSpace(ActiveProfileId) || !Profiles.Any(profile =>
                string.Equals(profile.Id, ActiveProfileId, StringComparison.OrdinalIgnoreCase)))
            Error("profile-active",
                $"ActiveProfileId '{ActiveProfileId}' does not identify a configured profile.");

        var rootIds = new HashSet<Guid>();
        var rootsById = new Dictionary<Guid, (IndexTargetEntry Target,
            LibraryRootPermissions Permissions)>();
        int syncTargetCount = 0;
        var configuredSets = new HashSet<string>(LibraryConfiguration.ScanSetComparer);
        foreach (IndexTargetEntry target in IndexTargets.Where(target =>
                     !string.IsNullOrWhiteSpace(target.Target)))
        {
            if (target.Id == Guid.Empty)
                Error("root-id", $"Index target '{target.Target}' must have a non-empty ID.");
            else if (!rootIds.Add(target.Id))
                Error("root-id-duplicate",
                    $"More than one IndexTarget uses root ID '{target.Id:D}'.");
            try
            {
                _ = LibraryConfiguration.NormalizeIndexFormats(target.IndexFormats);
                _ = LibraryConfiguration.NormalizeIndexPatterns(
                    target.IndexIncludePatterns);
                _ = LibraryConfiguration.NormalizeIndexPatterns(
                    target.IndexExcludePatterns);
            }
            catch (InvalidDataException exception)
            {
                Error("root-index-format", exception.Message);
            }

            string profileId = CleanOptional(target.ProfileId) ?? ActiveProfileId;
            LibraryProfile? profile = Profiles.FirstOrDefault(candidate => string.Equals(
                candidate.Id, profileId, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                Error("root-profile",
                    $"Index target '{target.Target}' references unknown profile '{profileId}'.");
            }
            else
            {
                LibraryRootPermissions permissions = EffectivePermissions(target, profile);
                if ((permissions & ~LibraryRootPermissions.All) != 0)
                    Error("root-permissions",
                        $"Index target '{target.Target}' contains unknown root permissions.");
                if (target.Organize &&
                    !permissions.HasFlag(LibraryRootPermissions.OrganizeFiles))
                    Error("root-organize-permission",
                        $"Index target '{target.Target}' enables Organize but does not permit " +
                        "OrganizeFiles.");
                if (target.IsSyncTarget &&
                    !permissions.HasFlag(LibraryRootPermissions.SynchronizeOutput))
                    Error("root-sync-permission",
                        $"Index target '{target.Target}' is the synchronization target but does " +
                        "not permit SynchronizeOutput.");
                if (target.Id != Guid.Empty)
                    rootsById[target.Id] = (target, permissions);
            }

            if (target.IsSyncTarget)
                syncTargetCount++;

            var memberships = new HashSet<string>(LibraryConfiguration.ScanSetComparer);
            foreach (IndexTargetSetEntry membership in target.Memberships)
            {
                try
                {
                    string name = LibraryConfiguration.ParseScanSetName(membership.Name);
                    configuredSets.Add(name);
                    if (!memberships.Add(name))
                        Error("root-set-duplicate",
                            $"Index target '{target.Target}' contains duplicate scan set '{name}'.");
                }
                catch (InvalidDataException exception)
                {
                    Error("root-set-invalid", exception.Message);
                }
            }

            if (includePathAvailabilityWarnings && !Directory.Exists(target.Target))
                Warning("root-offline",
                    $"Index target '{target.Target}' is currently offline or unavailable.");
        }

        foreach (IGrouping<string, IndexTargetEntry> duplicatePath in IndexTargets
                     .Where(target => !string.IsNullOrWhiteSpace(target.Target))
                     .GroupBy(target => Path.TrimEndingDirectorySeparator(target.Target.Trim()),
                         PathComparer)
                     .Where(group => group.Count() > 1))
        {
            IndexTargetEntry first = duplicatePath.First();
            bool conflict = duplicatePath.Skip(1).Any(target =>
                !SetEquals(first.IndexFormats, target.IndexFormats) ||
                !SetEquals(first.IndexIncludePatterns, target.IndexIncludePatterns) ||
                !SetEquals(first.IndexExcludePatterns, target.IndexExcludePatterns));
            if (conflict)
                Error("root-index-policy-conflict",
                    $"Duplicate index target '{duplicatePath.Key}' must use the same formats, " +
                    "include patterns, and exclude patterns on every declaration.");
        }

        var profileIds = Profiles.Select(profile => profile.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recipeIds = Profiles.SelectMany(profile => profile.Ingest.Recipes)
            .Select(recipe => recipe.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (LibraryProfile profile in Profiles)
        {
            foreach (LibraryIngestRecipe recipe in profile.Ingest.Recipes)
            {
                if (recipe.DestinationRootId is { } destinationRootId &&
                    !rootIds.Contains(destinationRootId))
                    Error("recipe-root",
                        $"Ingest recipe '{recipe.Id}' in profile '{profile.Id}' references " +
                        $"unknown destination root '{destinationRootId:D}'.");
                else if (recipe.DestinationRootId is { } configuredRootId &&
                         rootsById.TryGetValue(configuredRootId, out var destination) &&
                         !destination.Permissions.HasFlag(
                             LibraryRootPermissions.IngestOutput))
                    Error("recipe-root-permission",
                        $"Ingest recipe '{recipe.Id}' in profile '{profile.Id}' targets " +
                        $"'{destination.Target.Target}', which does not permit IngestOutput.");
                if (recipe.NamingProfileId is { } namingProfileId &&
                    !profileIds.Contains(namingProfileId))
                    Error("recipe-naming-profile",
                        $"Ingest recipe '{recipe.Id}' in profile '{profile.Id}' references " +
                        $"unknown naming profile '{namingProfileId}'.");
            }
        }

        foreach (LibraryExportProfile exportProfile in ExportProfiles)
        {
            if (exportProfile.Naming.LibraryProfileId is { } namingProfileId &&
                !profileIds.Contains(namingProfileId))
                Error("export-naming-profile",
                    $"Export profile '{exportProfile.Id}' references unknown library profile " +
                    $"'{namingProfileId}'.");
            if (exportProfile.Transform.RecipeId is { } recipeId &&
                !recipeIds.Contains(recipeId))
                Error("export-transform-recipe",
                    $"Export profile '{exportProfile.Id}' references unknown transform recipe " +
                    $"'{recipeId}'.");
        }

        if (syncTargetCount > 1)
            Error("sync-target-duplicate",
                "Only one IndexTarget may be selected as the cross-library sync target.");

        foreach (PlaylistSourceEntry source in PlaylistSources)
        {
            if (string.IsNullOrWhiteSpace(source.Location))
            {
                Error("playlist-source-location", "Playlist source location cannot be empty.");
                continue;
            }
            if (!string.Equals(source.Type?.Trim(), "m3u",
                    StringComparison.OrdinalIgnoreCase))
                Error("playlist-source-type",
                    $"Playlist source '{source.Location}' must have a type of 'm3u'.");
            if (includePathAvailabilityWarnings && !File.Exists(source.Location) &&
                !Directory.Exists(source.Location))
                Warning("playlist-source-offline",
                    $"Playlist source '{source.Location}' is currently offline or unavailable.");
        }

        foreach (PlaylistTargetEntry target in PlaylistTargets.Where(target =>
                     !string.IsNullOrWhiteSpace(target.Target)))
        {
            string type = target.Type?.Trim().ToLowerInvariant() ?? "";
            if (type is not ("m3u" or "m3u8" or "wpl"))
                Error("playlist-type",
                    $"Playlist target '{target.Target}' must have a type of 'm3u', 'm3u8', or 'wpl'.");
            try
            {
                _ = NormalizePlaylistOption(target.PathStyle,
                    "legacy", "provided", "absolute", "relative");
                _ = NormalizePlaylistOption(target.Encoding,
                    "utf-8", "utf-16", "utf-16be", "ascii");
                _ = NormalizePlaylistOption(target.LineEnding,
                    "platform", "crlf", "lf");
                _ = NormalizePlaylistOption(target.FileNameTransform,
                    "legacy", "preserve", "sanitize", "sonos");
            }
            catch (InvalidDataException exception)
            {
                Error("playlist-options", exception.Message);
            }
            if (target.MaxTrackCount <= 0)
                Error("playlist-max-tracks",
                    $"Playlist target '{target.Target}' must allow at least one track.");
            if (target.Sets.Count == 0)
            {
                Error("playlist-sets",
                    $"Playlist target '{target.Target}' must select at least one scan set.");
                continue;
            }
            try
            {
                string[] unknownSets = target.Sets
                    .Select(LibraryConfiguration.ParseScanSetName)
                    .Distinct(LibraryConfiguration.ScanSetComparer)
                    .Where(set => !configuredSets.Contains(set))
                    .ToArray();
                if (unknownSets.Length > 0)
                    Error("playlist-sets-unknown",
                        $"Playlist target '{target.Target}' references scan set(s) with no " +
                        "IndexTarget: " + string.Join(",", unknownSets));
            }
            catch (InvalidDataException exception)
            {
                Error("playlist-set-invalid", exception.Message);
            }
        }

        if (LengthLimit <= 0)
            Error("length-limit", "Path length limit must be a positive integer.");
        if (DiscNumLengthLimit <= 0)
            Error("disc-length-limit", "Disc number length limit must be a positive integer.");
        if (AacBitrateKbps <= 0)
            Error("aac-bitrate", "AAC bitrate must be a positive integer.");
        if (OversizedArtworkByteThreshold <= 0)
            Error("artwork-byte-threshold",
                "Oversized artwork byte threshold must be a positive integer.");
        if (OversizedArtworkDimensionThreshold <= 0)
            Error("artwork-dimension-threshold",
                "Oversized artwork dimension threshold must be a positive integer.");
        if (ArtworkRepairTargetByteSize <= 0)
            Error("artwork-repair-bytes",
                "Artwork repair byte target must be a positive integer.");
        if (ArtworkRepairTargetDimension <= 0)
            Error("artwork-repair-dimension",
                "Artwork repair dimension target must be a positive integer.");
        if (ItunesLibraryPath is { Length: > 0 } library &&
            !Path.GetExtension(library).Equals(".itl", StringComparison.OrdinalIgnoreCase))
            Error("itunes-path", "iTunes library path must identify an .itl file.");
        if (string.IsNullOrWhiteSpace(DatabaseFile))
            Error("database-file", "DatabaseFile cannot be empty.");
        else if (DatabaseFile.Trim().Equals("sqlite:", StringComparison.OrdinalIgnoreCase))
            Error("database-file", "A sqlite: database specification must include a path.");

        try
        {
            ValidatePlaylistOffsets();
        }
        catch (InvalidDataException exception)
        {
            Error("playlist-offsets", exception.Message);
        }

        return issues;
    }

    private XDocument CreateMachineBindingsDocument()
    {
        var root = new XElement("LibraryBindings",
            new XAttribute("SchemaVersion", LibraryMachineBindings.CurrentSchemaVersion),
            new XAttribute("LibraryId", LibraryId.ToString("D")),
            new XElement("DatabaseBinding",
                new XAttribute("Path", DatabaseFile.Trim())),
            new XElement("ToolBinding",
                new XAttribute("Name", "Ffmpeg"),
                new XAttribute("Path",
                    string.IsNullOrWhiteSpace(FfmpegPath) ? "ffmpeg" : FfmpegPath.Trim())));

        if (!string.IsNullOrWhiteSpace(ItunesLibraryPath))
            root.Add(new XElement("ToolBinding",
                new XAttribute("Name", "ItunesLibrary"),
                new XAttribute("Path", ItunesLibraryPath.Trim())));

        foreach (IndexTargetEntry target in IndexTargets.Where(target =>
                     !string.IsNullOrWhiteSpace(target.Target)))
            root.Add(new XElement("RootBinding",
                new XAttribute("RootId", target.Id.ToString("D")),
                new XAttribute("Path", target.Target.Trim())));

        foreach (LibraryExportProfile profile in ExportProfiles.Where(profile =>
                     !string.IsNullOrWhiteSpace(profile.Transport.Destination)))
        {
            var binding = new XElement("ExportBinding",
                new XAttribute("ProfileId", profile.Id),
                new XAttribute("Destination", profile.Transport.Destination.Trim()),
                profile.Transport.Options.OrderBy(option => option.Key,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(option => new XElement("Option",
                        new XAttribute("Name", option.Key),
                        new XAttribute("Value", option.Value))));
            root.Add(binding);
        }

        MergeUnknownBindingsData(root);
        return new XDocument(root);
    }

    private void MergeUnknownBindingsData(XElement root)
    {
        if (_sourceBindingsRoot is null)
            return;

        CopyUnknownAttributes(root, _sourceBindingsRoot);
        foreach (XElement target in root.Elements())
        {
            XElement? source = FindMatchingChild(_sourceBindingsRoot, target);
            if (source is not null)
                MergeUnknownElementData(target, source);
        }

        foreach (XElement source in _sourceBindingsRoot.Elements())
        {
            bool unknownElement = source.Name.Namespace != XNamespace.None ||
                source.Name.LocalName is not ("DatabaseBinding" or "ToolBinding" or
                    "RootBinding" or "ExportBinding");
            bool unknownTool = source.Name.LocalName == "ToolBinding" &&
                (string?)source.Attribute("Name") is { } name &&
                !name.Equals("Ffmpeg", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("ItunesLibrary", StringComparison.OrdinalIgnoreCase);
            if (unknownElement || unknownTool)
                root.Add(new XElement(source));
        }
    }

    private LibraryRootPermissions EffectivePermissions(
        IndexTargetEntry target,
        LibraryProfile profile)
    {
        if (target.Permissions is { } explicitPermissions)
            return explicitPermissions;

        if (profile.Preset != LibraryProfilePreset.LegacyMusicLibraryTools)
        {
            LibraryRootPermissions result = profile.DefaultRootPermissions;
            if (!target.Organize)
                result &= ~LibraryRootPermissions.OrganizeFiles;
            return result;
        }

        LibraryRootPermissions legacy = LibraryRootPermissions.WriteMetadata |
                                        LibraryRootPermissions.WriteArtwork;
        if (target.Organize)
            legacy |= LibraryRootPermissions.OrganizeFiles;
        if (target.IsSyncTarget)
            legacy |= LibraryRootPermissions.SynchronizeOutput;
        return legacy;
    }

    private void MigrateLegacyRoleAssignments()
    {
        var targets = new Dictionary<LibraryIngestRole, IndexTargetEntry>();
        foreach (IndexTargetEntry target in IndexTargets.Where(target =>
                     target.IngestRole != LibraryIngestRole.None))
        {
            if (!targets.TryAdd(target.IngestRole, target))
                throw new InvalidDataException(
                    $"Only one IndexTarget may be assigned legacy ingest role " +
                    $"'{target.IngestRole}'.");
            LibraryRootPermissions permissions = target.Permissions ??
                (LibraryRootPermissions.WriteMetadata |
                 LibraryRootPermissions.WriteArtwork);
            if (target.Organize)
                permissions |= LibraryRootPermissions.OrganizeFiles;
            if (target.IsSyncTarget)
                permissions |= LibraryRootPermissions.SynchronizeOutput;
            target.Permissions = permissions | LibraryRootPermissions.IngestOutput;
        }

        for (int profileIndex = 0; profileIndex < Profiles.Count; profileIndex++)
        {
            LibraryProfile profile = Profiles[profileIndex];
            LibraryIngestRecipe[] recipes = profile.Ingest.Recipes.Select(recipe =>
            {
                bool catalogFallback = recipe.DestinationLegacyRole ==
                    LibraryIngestRole.AacFallback &&
                    !string.IsNullOrWhiteSpace(ItunesLibraryPath);
                Guid? destinationRootId = recipe.DestinationRootId;
                if (catalogFallback)
                    destinationRootId = null;
                else if (destinationRootId is null &&
                         recipe.DestinationLegacyRole != LibraryIngestRole.None &&
                         targets.TryGetValue(recipe.DestinationLegacyRole, out var target))
                    destinationRootId = target.Id;
                bool unresolvedLegacyDestination = destinationRootId is null &&
                    !catalogFallback &&
                    recipe.DestinationLegacyRole != LibraryIngestRole.None;
                return recipe with
                {
                    Enabled = recipe.Enabled && !unresolvedLegacyDestination,
                    DestinationRootId = destinationRootId,
                    DestinationLegacyRole = LibraryIngestRole.None,
                    OutputRepresentationRole = LibraryRepresentationRole.Ignore,
                    AddToMediaCatalog = recipe.AddToMediaCatalog || catalogFallback,
                };
            }).ToArray();
            Profiles[profileIndex] = profile with
            {
                Ingest = profile.Ingest with
                {
                    Enabled = profile.Ingest.Enabled && recipes.Any(recipe => recipe.Enabled),
                    Recipes = recipes,
                },
            };
        }

        foreach (IndexTargetEntry target in IndexTargets)
        {
            target.IngestRole = LibraryIngestRole.None;
            target.RepresentationRole = LibraryRepresentationRole.Ignore;
        }
    }

    private void CreateLegacyBackupIfNeeded(string path)
    {
        if (SchemaVersion != LibraryConfigurationSchema.LegacyVersion ||
            _loadedPath is null || !File.Exists(path))
            return;
        string fullPath = Path.GetFullPath(path);
        if (!PathComparer.Equals(fullPath, _loadedPath))
            return;
        string backup = fullPath + ".v1.bak";
        if (!File.Exists(backup))
            File.Copy(fullPath, backup);
    }

    private void MergeUnknownRootData(XElement root)
    {
        if (_sourceRoot is null)
            return;
        CopyUnknownAttributes(root, _sourceRoot);
        string[] repeatable = ["LibraryProfile", "ExportProfile", "IndexTarget", "PlaylistSource",
            "PlaylistTarget", "SyncPlaylist"];
        foreach (XElement target in root.Elements().Where(element =>
                     !repeatable.Contains(element.Name.LocalName, StringComparer.Ordinal)))
        {
            XElement? source = _sourceRoot.Elements(target.Name).FirstOrDefault();
            if (source is not null)
                MergeUnknownElementData(target, source);
        }
    }

    private static void MergeUnknownElementData(XElement target, XElement source)
    {
        CopyUnknownAttributes(target, source);
        HashSet<string> knownChildren = KnownChildNames(target.Name.LocalName);
        foreach (XElement sourceChild in source.Elements())
        {
            if (sourceChild.Name.Namespace != XNamespace.None ||
                !knownChildren.Contains(sourceChild.Name.LocalName))
            {
                target.Add(new XElement(sourceChild));
                continue;
            }

            XElement? targetChild = FindMatchingChild(target, sourceChild);
            if (targetChild is not null)
                MergeUnknownElementData(targetChild, sourceChild);
        }
    }

    private static void CopyUnknownAttributes(XElement target, XElement source)
    {
        HashSet<string> knownAttributes = KnownAttributeNames(target);
        foreach (XAttribute attribute in source.Attributes())
        {
            if ((attribute.Name.Namespace != XNamespace.None ||
                 !knownAttributes.Contains(attribute.Name.LocalName)) &&
                target.Attribute(attribute.Name) is null)
                target.Add(new XAttribute(attribute));
        }
    }

    private static XElement? FindMatchingChild(XElement target, XElement sourceChild)
    {
        string? identityAttribute = sourceChild.Name.LocalName switch
        {
            "LibraryProfile" or "ExportProfile" or "Rule" or "Recipe" => "Id",
            "Set" => "Name",
            "Option" => "Name",
            "RootBinding" => "RootId",
            "ExportBinding" => "ProfileId",
            "ToolBinding" => "Name",
            _ => null,
        };
        if (sourceChild.Name.LocalName == "Value")
            return target.Elements(sourceChild.Name).FirstOrDefault(candidate => string.Equals(
                candidate.Value, sourceChild.Value, StringComparison.Ordinal));
        if (identityAttribute is null)
            return target.Elements(sourceChild.Name).FirstOrDefault();
        string? identity = (string?)sourceChild.Attribute(identityAttribute);
        return target.Elements(sourceChild.Name).FirstOrDefault(candidate => string.Equals(
            (string?)candidate.Attribute(identityAttribute), identity,
            StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string> KnownAttributeNames(XElement element) => new(
        (element.Name.LocalName, element.Parent?.Name.LocalName) switch
        {
            ("LibraryConfiguration", _) => ["SchemaVersion", "LibraryId", "ActiveProfileId"],
            ("LibraryBindings", _) => ["SchemaVersion", "LibraryId"],
            ("MachineBindings", _) => ["File"],
            ("DatabaseBinding", _) => ["Path"],
            ("ToolBinding", _) => ["Name", "Path"],
            ("RootBinding", _) => ["RootId", "Path"],
            ("ExportBinding", _) => ["ProfileId", "Destination"],
            ("LibraryProfile", _) => ["Id", "Name", "Preset", "DefaultRootPermissions"],
            ("ExportProfile", _) => ["Id", "Name", "Enabled"],
            ("Selection", _) => ["Kind", "Query"],
            ("Transform", _) => ["Mode", "RecipeId", "ProviderId", "Codec", "Container"],
            ("Naming", "LibraryProfile") => ["DirectoryTemplate", "FileNameTemplate",
                "TrackPadding", "DiscPadding",
                "CollisionPolicy", "PreserveUnicode", "InvalidCharacterReplacement",
                "UseItunesCanonicalNaming", "LegacySanitization", "StripFormatSuffixes",
                "MissingArtistFallback", "MissingAlbumFallback", "MissingTitleFallback",
                "CompilationValue", "UnicodeNormalization", "ComponentLengthLimit",
                "CompletePathLengthLimit"],
            ("Naming", "ExportProfile") => ["LibraryProfileId", "PreserveSourceLayout",
                "FolderTemplate", "FileNameTemplate", "CollisionPolicy"],
            ("Disc", _) => ["Strategy", "TrackTotalScope", "InferAlbumSuffix", "PreserveDiscTags"],
            ("AlbumIdentity", _) => ["UseAlbumArtist", "StripFormatSuffixes",
                "StripDiscSuffixes", "IncludeReleaseYear"],
            ("Metadata", _) => ["PreserveReplayGain", "PreserveMusicBrainzIdentifiers",
                "PreserveCustomFields", "PreserveCompilationSemantics"],
            ("Rule", "Health") => ["Id", "Enabled", "Severity", "ProposeRepair", "ApplyRepair"],
            ("Rule", "Sidecars") => ["Id", "Name", "Enabled", "Patterns", "Disposition"],
            ("Quality", _) => ["HighResolutionMinimumSampleRateHz",
                "HighResolutionMinimumBitsPerSample"],
            ("Ingest", _) => ["Enabled", "SourceDisposition", "PreserveSidecars"],
            ("Artwork", "LibraryProfile") => ["Storage", "Roles", "Encoding",
                "MaximumDimension", "MaximumEncodedBytes", "JpegQuality",
                "SidecarFileNameTemplate"],
            ("Artwork", "ExportProfile") => ["Mode", "FrontCoverOnly", "PreserveEncoding",
                "MaximumDimension", "MaximumBytes"],
            ("Playlists", _) => ["Enabled", "Format", "RelativePaths", "IncludeExtendedInfo",
                "EncodingName", "WriteByteOrderMark", "LineEnding", "MaximumTracks"],
            ("Transport", _) => ["ProviderId", "Destination"],
            ("Option", _) => ["Name", "Value"],
            ("Reconciliation", _) => ["ExtraFiles", "ReplaceChangedFiles",
                "RemoveEmptyDirectories", "MaximumRemovals"],
            ("Sidecars", _) => ["UnknownFileDisposition"],
            ("Recipe", _) => ["Id", "Name", "Enabled", "Action"],
            ("Match", _) => ["InputExtensions", "RequireLossless", "MinimumSampleRateHz",
                "MinimumBitsPerSample", "InputChannels", "MatchAnyQualityMinimum",
                "AlbumCondition", "SourceSelection", "RequireFallbackApproval"],
            ("Output", _) => ["DestinationRootId", "DestinationLegacyRole", "OutputExtension",
                "Codec", "Encoder", "ExtraFfmpegOptions", "AddToMediaCatalog", "BitrateKbps", "SampleRateHz", "BitsPerSample",
                "OutputChannels", "NamingProfileId", "PreserveMetadata", "PreserveArtwork",
                "CollisionPolicy", "RepresentationRole"],
            ("IndexTarget", _) => ["Id", "ProfileId", "Permissions", "Path", "Offset", "Filter",
                "Organize", "ItunesCanonicalNaming", "IngestRole", "SyncTarget", "Set",
                "IndexFormats", "IndexInclude", "IndexExclude", "RepresentationRole"],
            ("Set", _) => ["Name", "Offset"],
            ("PlaylistSource", _) => ["Type", "Recursive"],
            ("PlaylistTarget", _) => ["Type", "Set", "PathStyle", "Encoding", "Bom",
                "LineEnding", "ExtInf", "FileNameTransform", "MaxTracks", "Collision"],
            ("IngestSettings", _) => ["AacEncoder", "AacBitrateKbps", "DeleteSourcesAfterIngest",
                "RemoveNonMusicAfterIngest"],
            ("ArtworkHealthSettings", _) => ["OversizedByteThreshold", "OversizedDimensionThreshold",
                "RepairTargetByteSize", "RepairTargetDimension"],
            ("CrossSyncMusicSettings", _) => ["DeleteStaleFiles"],
            ("CrossSyncPlaylistsSettings", _) => ["Clean"],
            _ => [],
        }, StringComparer.Ordinal);

    private static HashSet<string> KnownChildNames(string elementName) => new(
        elementName switch
        {
            "LibraryConfiguration" => Known,
            "LibraryProfile" => ["Naming", "Disc", "AlbumIdentity", "Health", "Quality", "Ingest",
                "Artwork", "Sidecars", "Metadata"],
            "ExportProfile" => ["Selection", "Transform", "Naming", "Artwork", "Playlists",
                "Transport", "Reconciliation"],
            "Selection" => ["Value"],
            "Transport" => ["Option"],
            "Health" => ["Rule"],
            "Sidecars" => ["Rule"],
            "Ingest" => ["Recipe"],
            "Recipe" => ["Match", "Output"],
            "IndexTarget" => ["Set"],
            "LibraryBindings" => ["DatabaseBinding", "ToolBinding", "RootBinding",
                "ExportBinding"],
            "ExportBinding" => ["Option"],
            _ => [],
        }, StringComparer.Ordinal);

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

    private static string NormalizePlaylistOption(string? value, params string[] allowed)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? "";
        if (!allowed.Contains(normalized, StringComparer.Ordinal))
            throw new InvalidDataException(
                $"Invalid playlist option '{value}'. Expected one of: " +
                string.Join(", ", allowed) + ".");
        return normalized;
    }

    private static bool SetEquals(IEnumerable<string> left, IEnumerable<string> right) =>
        new HashSet<string>(left.Select(value => value.Trim()),
            StringComparer.OrdinalIgnoreCase).SetEquals(
            right.Select(value => value.Trim()));

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
