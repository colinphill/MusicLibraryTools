#nullable enable

using MusicFileUtilities;
using MusicLibrary.Core.Services;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MusicLibraryTools
{

    /// <summary>
    /// One configured index root. A root can participate in any number of logical comparison sets;
    /// the empty collection means it is indexed but is not a member of a logical set.
    /// </summary>
    public sealed record LibraryIndexSetMembership(string Name, string? Offset);

    public enum LibraryIngestRole
    {
        None,
        Cd,
        CdFallback,
        HiRes,
        AacFallback,
    }

    /// <summary>
    /// Explicit representation identity for comparison/repair workflows. LegacyAutomatic retains
    /// the pre-profile path/codec heuristics only for migrated legacy libraries.
    /// </summary>
    public enum LibraryRepresentationRole
    {
        LegacyAutomatic,
        Ignore,
        LosslessByQuality,
        CdLossless,
        HighResolutionLossless,
        Purchased,
        GeneratedLossy,
    }

    public sealed record LibraryIngestSettings(
        string AacEncoder,
        int AacBitrateKbps,
        bool DeleteSourcesAfterIngest,
        bool RemoveNonMusicAfterIngest);

    public sealed record LibraryArtworkHealthSettings(
        int OversizedByteThreshold,
        int OversizedDimensionThreshold,
        int RepairTargetByteSize = LibraryArtworkHealthSettings.DefaultRepairTargetByteSize,
        int RepairTargetDimension = LibraryArtworkHealthSettings.DefaultRepairTargetDimension)
    {
        public const int DefaultOversizedByteThreshold = 2 * 1024 * 1024;
        public const int DefaultOversizedDimensionThreshold = 2_000;
        public const int DefaultRepairTargetByteSize = 225 * 1024;
        public const int DefaultRepairTargetDimension = 600;
    }

    public sealed record LibraryIndexLocation(
        string Target,
        string? DefaultOffset,
        IReadOnlyList<LibraryIndexSetMembership> Memberships,
        string? Filter,
        bool Organize = true,
        LibraryIngestRole IngestRole = LibraryIngestRole.None,
        bool IsSyncTarget = false,
        bool UseItunesCanonicalNaming = false,
        Guid RootId = default,
        string ProfileId = LibraryProfilePresets.LegacyId,
        LibraryRootPermissions Permissions = LibraryRootPermissions.All)
    {
        public IReadOnlyList<string> IndexFormats { get; init; } = [];
        public IReadOnlyList<string> IndexIncludePatterns { get; init; } = [];
        public IReadOnlyList<string> IndexExcludePatterns { get; init; } = [];
        public LibraryRepresentationRole RepresentationRole { get; init; } =
            LibraryRepresentationRole.LegacyAutomatic;

        public IReadOnlyList<string> Sets { get; } =
            Memberships.Select(membership => membership.Name).ToArray();

        public string? OffsetFor(string setName)
        {
            LibraryIndexSetMembership? membership = Memberships.FirstOrDefault(candidate =>
                LibraryConfiguration.ScanSetComparer.Equals(candidate.Name, setName));
            return membership is null ? null : membership.Offset ?? DefaultOffset;
        }
    }

    /// <summary>
    /// One playlist export destination. Every destination must select at least one logical scan
    /// set so an export can never accidentally use the entire indexed library.
    /// </summary>
    public sealed record LibraryPlaylistTarget(
        string Target,
        string Type,
        IReadOnlyList<string> Sets)
    {
        public string PathStyle { get; init; } = "legacy";
        public string Encoding { get; init; } = "utf-8";
        public bool EmitByteOrderMark { get; init; } = true;
        public string LineEnding { get; init; } = "platform";
        public bool IncludeExtendedInfo { get; init; } = true;
        public string FileNameTransform { get; init; } = "legacy";
        public int MaxTrackCount { get; init; } = 500;
        public LibraryPathCollisionPolicy CollisionPolicy { get; init; } =
            LibraryPathCollisionPolicy.Stop;
    }

    /// <summary>
    /// A catalog-independent playlist input. Locations may identify one playlist file or a
    /// directory of playlist files; relative locations are resolved beside the configuration.
    /// </summary>
    public sealed record LibraryPlaylistSource(
        string Type,
        string Location,
        bool Recursive = false);

    public enum MFEType { Directory, MusicFile, Other }

    public class MusicFileEnumerator : FileSystemEnumerator<(string Name, DateTime Modified, long Size, MFEType FileType)>, IEnumerable<(string Name, DateTime Modified, long Size, MFEType FileType)>
    {
        private readonly bool _skipItlpPackages;
        private readonly IMediaFormatRegistry _formats;

        // The 64KB buffer sizes each directory-query round-trip; the default is small enough
        // that large folders take several round-trips per directory on a network share.
        // recurse:false enumerates just the immediate children (used to split a scan root
        // into per-subtree units).
        public MusicFileEnumerator(
            string directory,
            bool recurse = true,
            bool skipItlpPackages = true,
            IMediaFormatRegistry? formats = null)
            : base(directory, new EnumerationOptions { RecurseSubdirectories = recurse, BufferSize = 64 * 1024 })
        {
            _skipItlpPackages = skipItlpPackages;
            _formats = formats ?? MediaFormatRegistry.Default;
        }

        public IEnumerator<(string Name, DateTime Modified, long Size, MFEType FileType)> GetEnumerator()
        {
            return this;
        }

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            return true;
        }

        protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry)
        {
            return !_skipItlpPackages || !entry.FileName.Contains(".itlp", StringComparison.OrdinalIgnoreCase);
        }

        protected override (string Name, DateTime Modified, long Size, MFEType FileType) TransformEntry(ref FileSystemEntry entry)
        {
            return (entry.ToFullPath(), entry.LastWriteTimeUtc.UtcDateTime, entry.Length,
                entry.IsDirectory
                    ? MFEType.Directory
                    : (_formats.SupportsExtension(Path.GetExtension(entry.FileName),
                        MediaFormatCapabilities.LibraryIndex)
                        ? MFEType.MusicFile
                        : MFEType.Other));
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    public class LibraryConfiguration
    {
        private static readonly Regex ScanSetNamePattern = new("^[A-Za-z0-9]+$", RegexOptions.CultureInvariant);
        public static readonly StringComparer ScanSetComparer = StringComparer.OrdinalIgnoreCase;
        private readonly XElement root_;
        private readonly string configurationPath_;
        private readonly string configurationDirectory_;
        private readonly LibraryMachineBindings? machineBindings_;

        public LibraryConfiguration(string filename)
        {
            string fullPath = Path.GetFullPath(filename);
            configurationPath_ = fullPath;
            configurationDirectory_ = Path.GetDirectoryName(fullPath)!;
            root_ = XDocument.Load(fullPath).Element("LibraryConfiguration")
                ?? throw new InvalidDataException("Missing <LibraryConfiguration> root element.");
            machineBindings_ = LibraryMachineBindings.LoadReferenced(
                root_, fullPath, LibraryId);
        }

        public LibraryMachineBindings? MachineBindings => machineBindings_;

        public int SchemaVersion
        {
            get
            {
                string? value = CleanOptional((string?)root_.Attribute("SchemaVersion"));
                if (value is null)
                    return LibraryConfigurationSchema.LegacyVersion;
                if (!int.TryParse(value, out int parsed) ||
                    parsed is < LibraryConfigurationSchema.LegacyVersion or
                        > LibraryConfigurationSchema.CurrentVersion)
                    throw new InvalidDataException(
                        $"Unsupported LibraryConfiguration SchemaVersion '{value}'. " +
                        $"This application supports versions 1 through " +
                        $"{LibraryConfigurationSchema.CurrentVersion}.");
                return parsed;
            }
        }

        public Guid LibraryId
        {
            get
            {
                string? value = CleanOptional((string?)root_.Attribute("LibraryId"));
                if (value is null)
                {
                    if (SchemaVersion >= LibraryConfigurationSchema.CurrentVersion)
                        throw new InvalidDataException(
                            "Schema v2 requires a LibraryId attribute on <LibraryConfiguration>.");
                    return LibraryConfigurationSchema.CreateStableId(
                        "legacy-library|" + NormalizeIdentityPath(configurationPath_));
                }
                if (!Guid.TryParse(value, out Guid parsed) || parsed == Guid.Empty)
                    throw new InvalidDataException(
                        "LibraryId on <LibraryConfiguration> must be a non-empty GUID.");
                return parsed;
            }
        }

        public string ActiveProfileId
        {
            get
            {
                if (SchemaVersion == LibraryConfigurationSchema.LegacyVersion)
                    return LibraryProfilePresets.LegacyId;
                string? value = CleanOptional((string?)root_.Attribute("ActiveProfileId"));
                if (value is null)
                    throw new InvalidDataException(
                        "Schema v2 requires an ActiveProfileId attribute on <LibraryConfiguration>.");
                LibraryProfileXml.ValidateId(value, "active profile");
                return value;
            }
        }

        public IReadOnlyList<LibraryProfile> Profiles
        {
            get
            {
                if (SchemaVersion == LibraryConfigurationSchema.LegacyVersion)
                    return LibraryProfilePresets.All;
                LibraryProfile[] profiles = root_.Elements("LibraryProfile")
                    .Select(LibraryProfileXml.Parse)
                    .ToArray();
                if (profiles.Length == 0)
                    throw new InvalidDataException(
                        "Schema v2 requires at least one <LibraryProfile>.");
                string[] duplicateIds = profiles
                    .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .ToArray();
                if (duplicateIds.Length > 0)
                    throw new InvalidDataException(
                        "Library profile IDs must be unique: " + string.Join(", ", duplicateIds));
                return profiles;
            }
        }

        public LibraryProfile ActiveProfile => Profiles.SingleOrDefault(profile =>
            string.Equals(profile.Id, ActiveProfileId, StringComparison.OrdinalIgnoreCase)) ??
            throw new InvalidDataException(
                $"ActiveProfileId '{ActiveProfileId}' does not identify a configured LibraryProfile.");

        /// <summary>
        /// Portable export definitions explicitly configured for this library. Specialized built-in
        /// exports are not implicitly enabled; they appear here only after being configured.
        /// </summary>
        public IReadOnlyList<LibraryExportProfile> ExportProfiles
        {
            get
            {
                if (SchemaVersion == LibraryConfigurationSchema.LegacyVersion)
                    return [];
                LibraryExportProfile[] profiles = root_.Elements("ExportProfile")
                    .Select(element =>
                    {
                        string id = ((string?)element.Attribute("Id") ?? "").Trim();
                        LibraryExportTransportBinding? binding = machineBindings_?
                            .ExportTransports.GetValueOrDefault(id);
                        LibraryExportProfile profile = LibraryExportProfileXml.Parse(
                            element, allowUnboundTransportDestination: binding is not null);
                        if (binding is not null)
                        {
                            profile = profile with
                            {
                                Transport = profile.Transport with
                                {
                                    Destination = binding.Destination,
                                    Options = binding.Options.ToImmutableDictionary(
                                        StringComparer.OrdinalIgnoreCase),
                                },
                            };
                            LibraryExportProfileXml.Validate(profile);
                        }
                        return profile;
                    }).ToArray();
                string[] duplicateIds = profiles
                    .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .ToArray();
                if (duplicateIds.Length > 0)
                    throw new InvalidDataException(
                        "Export profile IDs must be unique: " + string.Join(", ", duplicateIds));
                machineBindings_?.ValidateExportReferences(
                    profiles.Select(profile => profile.Id));
                return profiles;
            }
        }

        public LibraryProfile GetEffectiveProfile(LibraryIndexLocation location)
        {
            ArgumentNullException.ThrowIfNull(location);
            LibraryProfile profile = Profiles.SingleOrDefault(candidate => string.Equals(
                       candidate.Id, location.ProfileId, StringComparison.OrdinalIgnoreCase)) ??
                   throw new InvalidDataException(
                       $"Index target '{location.Target}' references unknown profile " +
                       $"'{location.ProfileId}'.");
            // The root-level switch predates profiles and must continue to override the selected
            // profile after a legacy file is migrated to v2.
            return location.UseItunesCanonicalNaming && !profile.Naming.UseItunesCanonicalNaming
                ? profile with
                {
                    Naming = profile.Naming with { UseItunesCanonicalNaming = true },
                }
                : profile;
        }

        public LibraryPolicySnapshot PolicySnapshot => LibraryPolicySnapshot.Create(this);
        
        public LibraryIndexLocation? CrossSyncTarget
        {
            get
            {
                LibraryIndexLocation[] targets = IndexLocations
                    .Where(location => location.IsSyncTarget)
                    .ToArray();
                if (targets.Length > 1)
                    throw new InvalidDataException(
                        "Only one <IndexTarget> may have SyncTarget=\"true\".");
                return targets.SingleOrDefault();
            }
        }

        public string CrossSyncTargetLibraryPath =>
            CrossSyncTarget?.Target ??
            CleanOptional((string?)root_.Element("SyncTarget")) ??
            throw new InvalidDataException(
                "Missing an <IndexTarget SyncTarget=\"true\"> synchronization destination.");

        public IReadOnlyList<string> SyncPlaylists => root_.Elements("SyncPlaylist")
            .Select(element => element.Value.Trim())
            .Where(value => value.Length > 0)
            .ToArray();

        public IReadOnlyList<LibraryPlaylistSource> PlaylistSources =>
            root_.Elements("PlaylistSource").Select(ParsePlaylistSource).ToArray();

        private LibraryPlaylistSource ParsePlaylistSource(XElement element)
        {
            string location = element.Value.Trim();
            if (location.Length == 0)
                throw new InvalidDataException("<PlaylistSource> cannot be empty.");
            string type = CleanOptional((string?)element.Attribute("Type"))?
                .ToLowerInvariant() ?? "";
            if (type != "m3u")
                throw new InvalidDataException(
                    $"PlaylistSource '{location}' must have a Type attribute of 'm3u'.");
            bool recursive = ParseOptionalBoolean(element, "Recursive", defaultValue: false);
            return new(type, Path.GetFullPath(location, configurationDirectory_), recursive);
        }

        public bool DeleteStaleCrossSyncFiles => ParseOptionalBoolean(
            root_.Element("CrossSyncMusicSettings"), "DeleteStaleFiles", defaultValue: false);

        public bool CleanCrossSyncPlaylists => ParseOptionalBoolean(
            root_.Element("CrossSyncPlaylistsSettings"), "Clean", defaultValue: false);

        public IEnumerable<LibraryIndexLocation> IndexLocations
        {
            get
            {
                LibraryIndexLocation[] locations = root_.Elements("IndexTarget")
                    .Select(ParseIndexLocation)
                    .ToArray();
                Guid[] duplicateIds = locations
                    .GroupBy(location => location.RootId)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .ToArray();
                if (duplicateIds.Length > 0)
                    throw new InvalidDataException(
                        "IndexTarget IDs must be unique: " +
                        string.Join(", ", duplicateIds.Select(id => id.ToString("D"))));
                machineBindings_?.ValidateRootReferences(
                    locations.Select(location => location.RootId));
                return locations;
            }
        }

        private LibraryIndexLocation ParseIndexLocation(XElement element, int index)
        {
            string? structuredPath = ((string?)element.Attribute("Path"))?.Trim();
            bool structured = structuredPath is not null || element.Elements("Set").Any();
            string target = structured ? structuredPath ?? "" : element.Value.Trim();
            if (SchemaVersion == LibraryConfigurationSchema.LegacyVersion &&
                string.IsNullOrWhiteSpace(target))
                throw new InvalidDataException("<IndexTarget> must specify a non-empty Path.");

            string? defaultOffset = CleanOptional((string?)element.Attribute("Offset"));
            IReadOnlyList<LibraryIndexSetMembership> memberships;
            if (structured)
            {
                if (element.Attribute("Set") is not null)
                    throw new InvalidDataException(
                        $"IndexTarget '{target}' cannot combine the legacy Set attribute with child Set elements.");
                var seen = new HashSet<string>(ScanSetComparer);
                memberships = element.Elements("Set").Select(child =>
                {
                    string name = ParseScanSetName((string?)child.Attribute("Name"));
                    if (!seen.Add(name))
                        throw new InvalidDataException(
                            $"IndexTarget '{target}' contains duplicate scan set '{name}'.");
                    return new LibraryIndexSetMembership(name,
                        CleanOptional((string?)child.Attribute("Offset")));
                }).ToArray();
            }
            else
            {
                memberships = ParseScanSets((string?)element.Attribute("Set"))
                    .Select(name => new LibraryIndexSetMembership(name, null)).ToArray();
            }
            bool organize = ParseOptionalBoolean(element, "Organize", defaultValue: true);
            LibraryIngestRole ingestRole = ParseIngestRole(
                (string?)element.Attribute("IngestRole"));
            LibraryRepresentationRole representationRole = ParseRepresentationRole(
                (string?)element.Attribute("RepresentationRole"),
                SchemaVersion == LibraryConfigurationSchema.LegacyVersion
                    ? LibraryRepresentationRole.LegacyAutomatic
                    : LibraryRepresentationRole.Ignore);
            bool syncTarget = ParseOptionalBoolean(element, "SyncTarget", defaultValue: false);
            bool itunesNaming = ParseOptionalBoolean(
                element, "ItunesCanonicalNaming", defaultValue: false);

            Guid rootId;
            string? rootIdValue = CleanOptional((string?)element.Attribute("Id"));
            if (rootIdValue is null)
            {
                if (SchemaVersion >= LibraryConfigurationSchema.CurrentVersion)
                    throw new InvalidDataException(
                        $"Schema v2 IndexTarget '{target}' requires an Id attribute.");
                rootId = LibraryConfigurationSchema.CreateStableId(
                    $"legacy-root|{LibraryId:D}|{index}|{NormalizeIdentityPath(target)}");
            }
            else if (!Guid.TryParse(rootIdValue, out rootId) || rootId == Guid.Empty)
            {
                throw new InvalidDataException(
                    $"Id on IndexTarget '{target}' must be a non-empty GUID.");
            }

            if (machineBindings_?.RootPaths.TryGetValue(rootId, out string? boundPath) == true)
                target = boundPath;
            if (string.IsNullOrWhiteSpace(target))
                throw new InvalidDataException(
                    $"IndexTarget '{rootId:D}' has no inline Path and no RootBinding.");

            string profileId;
            LibraryRootPermissions permissions;
            if (SchemaVersion == LibraryConfigurationSchema.LegacyVersion)
            {
                profileId = LibraryProfilePresets.LegacyId;
                permissions = LibraryRootPermissions.WriteMetadata |
                              LibraryRootPermissions.WriteArtwork;
                if (organize)
                    permissions |= LibraryRootPermissions.OrganizeFiles;
                if (ingestRole != LibraryIngestRole.None)
                    permissions |= LibraryRootPermissions.IngestOutput;
                if (syncTarget)
                    permissions |= LibraryRootPermissions.SynchronizeOutput;
            }
            else
            {
                profileId = CleanOptional((string?)element.Attribute("ProfileId")) ??
                    throw new InvalidDataException(
                        $"Schema v2 IndexTarget '{target}' requires a ProfileId attribute.");
                LibraryProfileXml.ValidateId(profileId, "profile");
                LibraryProfile profile = Profiles.SingleOrDefault(candidate => string.Equals(
                                             candidate.Id, profileId,
                                             StringComparison.OrdinalIgnoreCase)) ??
                                         throw new InvalidDataException(
                                             $"Index target '{target}' references unknown profile " +
                                             $"'{profileId}'.");
                if (element.Attribute("RepresentationRole") is null &&
                    profile.Preset == LibraryProfilePreset.LegacyMusicLibraryTools)
                    representationRole = LibraryRepresentationRole.LegacyAutomatic;
                itunesNaming |= profile.Naming.UseItunesCanonicalNaming;
                string? permissionValue =
                    CleanOptional((string?)element.Attribute("Permissions"));
                if (permissionValue is null)
                    throw new InvalidDataException(
                        $"Schema v2 IndexTarget '{target}' requires a Permissions attribute.");
                permissions = LibraryProfileXml.ParsePermissions(
                    permissionValue, profile.DefaultRootPermissions);
                if (organize && !permissions.HasFlag(LibraryRootPermissions.OrganizeFiles))
                    throw new InvalidDataException(
                        $"Index target '{target}' enables Organize but does not permit OrganizeFiles.");
                if (ingestRole != LibraryIngestRole.None &&
                    !permissions.HasFlag(LibraryRootPermissions.IngestOutput))
                    throw new InvalidDataException(
                        $"Index target '{target}' has an IngestRole but does not permit IngestOutput.");
                if (syncTarget &&
                    !permissions.HasFlag(LibraryRootPermissions.SynchronizeOutput))
                    throw new InvalidDataException(
                        $"Index target '{target}' enables SyncTarget but does not permit " +
                        "SynchronizeOutput.");
            }

            return new(target, defaultOffset, memberships,
                CleanOptional((string?)element.Attribute("Filter")),
                organize,
                ingestRole,
                syncTarget,
                itunesNaming,
                rootId,
                profileId,
                permissions)
            {
                IndexFormats = ParseIndexFormats(
                    (string?)element.Attribute("IndexFormats")),
                IndexIncludePatterns = ParseIndexPatterns(
                    (string?)element.Attribute("IndexInclude")),
                IndexExcludePatterns = ParseIndexPatterns(
                    (string?)element.Attribute("IndexExclude")),
                RepresentationRole = representationRole,
            };
        }

        private static LibraryRepresentationRole ParseRepresentationRole(
            string? value,
            LibraryRepresentationRole fallback)
        {
            value = CleanOptional(value);
            if (value is null)
                return fallback;
            return Enum.TryParse(value, ignoreCase: true,
                out LibraryRepresentationRole parsed)
                ? parsed
                : throw new InvalidDataException(
                    $"Invalid RepresentationRole '{value}'.");
        }

        private static LibraryIngestRole ParseIngestRole(string? value)
        {
            value = CleanOptional(value);
            if (value is null)
                return LibraryIngestRole.None;
            return value.ToLowerInvariant() switch
            {
                "cd" => LibraryIngestRole.Cd,
                "cdfallback" => LibraryIngestRole.CdFallback,
                "hires" => LibraryIngestRole.HiRes,
                "aacfallback" => LibraryIngestRole.AacFallback,
                _ => throw new InvalidDataException(
                    $"Invalid IngestRole '{value}'. Expected Cd, CdFallback, HiRes, or AacFallback."),
            };
        }

        /// <summary>Parse a comma, semicolon, or whitespace separated logical-set list.</summary>
        public static IReadOnlyList<string> ParseScanSets(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return [];

            var sets = value.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseScanSetName)
                .Distinct(ScanSetComparer)
                .OrderBy(set => set, ScanSetComparer)
                .ToArray();
            return sets;
        }

        public static string ParseScanSetName(string? value)
        {
            string name = value?.Trim() ?? "";
            if (!ScanSetNamePattern.IsMatch(name))
                throw new InvalidDataException(
                    $"Invalid scan-set name '{name}'. Set names may contain ASCII letters and digits only.");
            return name;
        }

        public static IReadOnlyList<string> ParseIndexFormats(string? value) =>
            NormalizeIndexFormats(string.IsNullOrWhiteSpace(value)
                ? []
                : value.Split(',', StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries));

        public static IReadOnlyList<string> NormalizeIndexFormats(
            IEnumerable<string> formats)
        {
            ArgumentNullException.ThrowIfNull(formats);
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string source in formats)
            {
                string value = source?.Trim() ?? "";
                if (value.Length == 0)
                    continue;
                if (!MediaFormatRegistry.Default.TryGetByExtension(
                        value, out MediaFormatDefinition format) ||
                    !format.Supports(MediaFormatCapabilities.LibraryIndex))
                    throw new InvalidDataException(
                        $"Index format '{value}' is not registered for library indexing.");
                if (seen.Add(format.Extension))
                    result.Add(format.Extension);
            }
            return result;
        }

        public static IReadOnlyList<string> ParseIndexPatterns(string? value) =>
            NormalizeIndexPatterns(string.IsNullOrWhiteSpace(value)
                ? []
                : value.Split(';', StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries));

        public static IReadOnlyList<string> NormalizeIndexPatterns(
            IEnumerable<string> patterns)
        {
            ArgumentNullException.ThrowIfNull(patterns);
            return patterns
                .Select(pattern => pattern?.Trim() ?? "")
                .Where(pattern => pattern.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public IReadOnlyList<LibraryPlaylistTarget> PlaylistTargets
        {
            get
            {
                if (root_.Element("PlaylistType") is not null)
                    throw new InvalidDataException(
                        "<PlaylistType> is obsolete; add a Type attribute to each <PlaylistTarget>.");
                return root_.Elements("PlaylistTarget").Select(ParsePlaylistTarget).ToArray();
            }
        }

        private static LibraryPlaylistTarget ParsePlaylistTarget(XElement element)
        {
            string target = element.Value.Trim();
            if (string.IsNullOrWhiteSpace(target))
                throw new InvalidDataException("<PlaylistTarget> cannot be empty.");

            string? type = ((string?)element.Attribute("Type"))?.Trim().ToLowerInvariant();
            if (type is not ("m3u" or "m3u8" or "wpl"))
                throw new InvalidDataException(
                    $"PlaylistTarget '{target}' must have a Type attribute of 'm3u', 'm3u8', or 'wpl'.");

            var sets = ParseScanSets((string?)element.Attribute("Set"));
            if (sets.Count == 0)
                throw new InvalidDataException(
                    $"PlaylistTarget '{target}' must select at least one scan set with its Set attribute.");

            string pathStyle = ParsePlaylistOption(element, "PathStyle", "legacy",
                "legacy", "provided", "absolute", "relative");
            string encoding = ParsePlaylistOption(element, "Encoding", "utf-8",
                "utf-8", "utf-16", "utf-16be", "ascii");
            string lineEnding = ParsePlaylistOption(element, "LineEnding", "platform",
                "platform", "crlf", "lf");
            string fileNameTransform = ParsePlaylistOption(element, "FileNameTransform",
                "legacy", "legacy", "preserve", "sanitize", "sonos");
            int maxTrackCount = ParsePositiveInteger(element, "MaxTracks", 500);
            string? collisionValue = CleanOptional((string?)element.Attribute("Collision"));
            LibraryPathCollisionPolicy collision = LibraryPathCollisionPolicy.Stop;
            if (collisionValue is not null && !Enum.TryParse(collisionValue,
                    ignoreCase: true, out collision))
                throw new InvalidDataException(
                    $"PlaylistTarget '{target}' has invalid Collision value " +
                    $"'{collisionValue}'.");

            return new LibraryPlaylistTarget(target, type, sets)
            {
                PathStyle = pathStyle,
                Encoding = encoding,
                EmitByteOrderMark = ParseOptionalBoolean(element, "Bom", defaultValue: true),
                LineEnding = lineEnding,
                IncludeExtendedInfo = ParseOptionalBoolean(
                    element, "ExtInf", defaultValue: true),
                FileNameTransform = fileNameTransform,
                MaxTrackCount = maxTrackCount,
                CollisionPolicy = collision,
            };
        }

        private static string ParsePlaylistOption(XElement element, string attributeName,
            string defaultValue, params string[] allowed)
        {
            string value = CleanOptional((string?)element.Attribute(attributeName))?
                .ToLowerInvariant() ?? defaultValue;
            if (!allowed.Contains(value, StringComparer.Ordinal))
                throw new InvalidDataException(
                    $"PlaylistTarget '{element.Value.Trim()}' has invalid {attributeName} " +
                    $"value '{value}'. Expected one of: {string.Join(", ", allowed)}.");
            return value;
        }

        [Obsolete("Use PlaylistTargets; playlist export configurations may contain multiple targets.")]
        public string PlaylistTargetFolder => PlaylistTargets.First().Target;

        [Obsolete("Use PlaylistTargets; Type is now an attribute of each PlaylistTarget.")]
        public string PlaylistType => PlaylistTargets.First().Type;

        public string DatabaseFile
        {
            get
            {
                if (machineBindings_?.DatabaseFile is { } boundDatabase)
                    return boundDatabase;
                try
                {
                    return root_.Element("DatabaseFile")!.Value;
                }
                catch
                {
                    return "cache.db";
                }
            }
        }

        public string? ItunesLibraryPath => machineBindings_?.ItunesLibraryPath ??
            ResolveOptionalPath((string?)root_.Element("ItunesLibrary"));

        public string FfmpegPath
        {
            get
            {
                if (machineBindings_?.FfmpegPath is { } boundFfmpeg)
                    return boundFfmpeg;
                string? value = CleanOptional((string?)root_.Element("FfmpegPath"));
                if (value is null) return "ffmpeg";
                return Path.IsPathRooted(value) || value.Contains(Path.DirectorySeparatorChar) ||
                       value.Contains(Path.AltDirectorySeparatorChar)
                    ? Path.GetFullPath(value, configurationDirectory_)
                    : value;
            }
        }

        public LibraryIngestSettings IngestSettings
        {
            get
            {
                XElement? element = root_.Element("IngestSettings");
                return new(
                    CleanOptional((string?)element?.Attribute("AacEncoder")) ?? "libfdk_aac",
                    ParsePositiveInteger(element, "AacBitrateKbps", 256),
                    ParseOptionalBoolean(element, "DeleteSourcesAfterIngest", defaultValue: false),
                    ParseOptionalBoolean(element, "RemoveNonMusicAfterIngest", defaultValue: false));
            }
        }

        public LibraryArtworkHealthSettings ArtworkHealthSettings
        {
            get
            {
                XElement? element = root_.Element("ArtworkHealthSettings");
                return new(
                    ParsePositiveInteger(element, "OversizedByteThreshold",
                        LibraryArtworkHealthSettings.DefaultOversizedByteThreshold),
                    ParsePositiveInteger(element, "OversizedDimensionThreshold",
                        LibraryArtworkHealthSettings.DefaultOversizedDimensionThreshold),
                    ParsePositiveInteger(element, "RepairTargetByteSize",
                        LibraryArtworkHealthSettings.DefaultRepairTargetByteSize),
                    ParsePositiveInteger(element, "RepairTargetDimension",
                        LibraryArtworkHealthSettings.DefaultRepairTargetDimension));
            }
        }

        public IReadOnlyDictionary<LibraryIngestRole, LibraryIndexLocation> IngestTargets
        {
            get
            {
                var result = new Dictionary<LibraryIngestRole, LibraryIndexLocation>();
                foreach (LibraryIndexLocation location in IndexLocations.Where(location =>
                             location.IngestRole != LibraryIngestRole.None))
                {
                    if (!result.TryAdd(location.IngestRole, location))
                        throw new InvalidDataException(
                            $"More than one IndexTarget is assigned IngestRole '{location.IngestRole}'.");
                }
                return result;
            }
        }

        public string [] this[string key] => root_.Elements(key).Select(e => e.Value).ToArray();

        public int LengthLimit => int.Parse(root_.Element("LengthLimit")!.Value);

        public int DiscNumLengthLimit => int.Parse(root_.Element("DiscNumLengthLimit")!.Value);

        private string? ResolveOptionalPath(string? value)
        {
            value = CleanOptional(value);
            return value is null ? null : Path.GetFullPath(value, configurationDirectory_);
        }

        private static string? CleanOptional(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private string NormalizeIdentityPath(string value)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(value, configurationDirectory_);
            }
            catch
            {
                fullPath = value.Trim();
            }
            fullPath = Path.TrimEndingDirectorySeparator(fullPath);
            return OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
        }

        private static bool ParseOptionalBoolean(
            XElement? element, string attributeName, bool defaultValue)
        {
            if (element is null)
                return defaultValue;
            string? value = CleanOptional((string?)element.Attribute(attributeName));
            if (value is null)
                return defaultValue;
            if (bool.TryParse(value, out bool parsed))
                return parsed;
            throw new InvalidDataException(
                $"Attribute '{attributeName}' on <{element.Name.LocalName}> must be true or false.");
        }

        private static int ParsePositiveInteger(XElement? element, string attributeName, int fallback)
        {
            if (element is null)
                return fallback;
            string? value = CleanOptional((string?)element.Attribute(attributeName));
            if (value is null)
                return fallback;
            if (int.TryParse(value, out int parsed) && parsed > 0)
                return parsed;
            throw new InvalidDataException(
                $"Attribute '{attributeName}' on <{element.Name.LocalName}> must be a positive integer.");
        }
 
    }
}
