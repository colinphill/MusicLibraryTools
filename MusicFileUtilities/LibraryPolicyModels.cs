#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MusicFileUtilities;
using MusicLibrary.Core.Services;

namespace MusicLibraryTools
{
    /// <summary>Configuration format versions understood by MusicLibraryTools.</summary>
    public static class LibraryConfigurationSchema
    {
        public const int LegacyVersion = 1;
        public const int CurrentVersion = 2;

        /// <summary>
        /// Creates a repeatable identifier for an unversioned file until its first v2 save writes
        /// the identifier explicitly.
        /// </summary>
        public static Guid CreateStableId(string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            var guidBytes = new byte[16];
            Array.Copy(bytes, guidBytes, guidBytes.Length);
            guidBytes[7] = (byte)((guidBytes[7] & 0x0f) | 0x50);
            guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
            return new Guid(guidBytes);
        }
    }

    /// <summary>The built-in behavior on which a named library profile is based.</summary>
    public enum LibraryProfilePreset
    {
        LegacyMusicLibraryTools,
        CatalogOnly,
        PreserveLayoutAndTags,
        ArtistAlbum,
        ItunesMedia,
        Custom,
    }

    /// <summary>
    /// File-system mutations that a configured library root permits. Indexing and playback are
    /// always read-only and therefore do not require a flag.
    /// </summary>
    [Flags]
    public enum LibraryRootPermissions
    {
        None = 0,
        WriteMetadata = 1 << 0,
        WriteArtwork = 1 << 1,
        OrganizeFiles = 1 << 2,
        IngestOutput = 1 << 3,
        SynchronizeOutput = 1 << 4,
        All = WriteMetadata | WriteArtwork | OrganizeFiles | IngestOutput | SynchronizeOutput,
    }

    public enum LibraryPathCollisionPolicy
    {
        Stop,
        Suffix,
        Hash,
        PreserveExisting,
    }

    public enum LibraryUnicodeNormalization
    {
        None,
        FormC,
        FormD,
        FormKC,
        FormKD,
    }

    public sealed record LibraryNamingPolicy(
        string DirectoryTemplate,
        string FileNameTemplate,
        int TrackPadding,
        int DiscPadding,
        LibraryPathCollisionPolicy CollisionPolicy,
        bool PreserveUnicode,
        string InvalidCharacterReplacement,
        bool UseItunesCanonicalNaming,
        bool LegacySanitization,
        bool StripFormatSuffixes)
    {
        public string MissingArtistFallback { get; init; } = "Unknown Artist";
        public string MissingAlbumFallback { get; init; } = "Unknown Album";
        public string MissingTitleFallback { get; init; } = "Untitled";
        public string CompilationValue { get; init; } = "Compilations";
        public LibraryUnicodeNormalization UnicodeNormalization { get; init; } =
            LibraryUnicodeNormalization.FormC;
        public int? ComponentLengthLimit { get; init; }
        public int? CompletePathLengthLimit { get; init; }
    }

    public enum LibraryDiscStrategy
    {
        PreserveTags,
        AlbumSuffix,
        DiscFolder,
        FileNamePrefix,
        FlattenContinuous,
    }

    public enum LibraryTrackTotalScope
    {
        PerDisc,
        Album,
    }

    public sealed record LibraryDiscPolicy(
        LibraryDiscStrategy Strategy,
        LibraryTrackTotalScope TrackTotalScope,
        bool InferAlbumSuffix,
        bool PreserveDiscTags);

    /// <summary>Controls exact album grouping without collapsing edition qualifiers.</summary>
    public sealed record LibraryAlbumIdentityPolicy(
        bool UseAlbumArtist,
        bool StripFormatSuffixes,
        bool StripDiscSuffixes,
        bool IncludeReleaseYear);

    /// <summary>
    /// Controls metadata families that are carried across when a recipe requests metadata
    /// preservation. Core title/artist/album/track fields are always projected from the preview;
    /// these switches cover fidelity metadata that some users intentionally omit.
    /// </summary>
    public sealed record LibraryMetadataPolicy(
        bool PreserveReplayGain,
        bool PreserveMusicBrainzIdentifiers,
        bool PreserveCustomFields,
        bool PreserveCompilationSemantics)
    {
        public bool PreservesAllSupportedMetadata =>
            PreserveReplayGain && PreserveMusicBrainzIdentifiers &&
            PreserveCustomFields && PreserveCompilationSemantics;
    }

    public enum LibraryHealthSeverity
    {
        Information,
        Warning,
        Error,
    }

    /// <summary>One configurable health rule and its independent repair permissions.</summary>
    public sealed record LibraryHealthRulePolicy(
        string Id,
        bool Enabled,
        LibraryHealthSeverity Severity,
        bool ProposeRepair,
        bool ApplyRepair);

    public sealed record LibraryHealthPolicy(IReadOnlyList<LibraryHealthRulePolicy> Rules)
    {
        public LibraryHealthRulePolicy? Find(string id) => Rules.FirstOrDefault(rule =>
            string.Equals(rule.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Stable identifiers for the first set of profile-controlled health rules.</summary>
    public static class LibraryHealthRuleIds
    {
        public const string LossyFile = "lossy-file";
        public const string MissingAlbumArtist = "missing-album-artist";
        public const string MissingTrackTotal = "missing-track-total";
        public const string DiscMetadata = "disc-metadata";
        public const string Id3Version = "id3-version";
        public const string NormalizeWhitespace = "normalize-whitespace";
        public const string DiscAlbumTitle = "disc-album-title";
    }

    /// <summary>
    /// A representation is high resolution when either value meets its configured minimum.
    /// </summary>
    public sealed record LibraryQualityPolicy(
        int HighResolutionMinimumSampleRateHz,
        int HighResolutionMinimumBitsPerSample);

    public enum LibrarySourceDisposition
    {
        Preserve,
        Quarantine,
        Delete,
    }

    public enum LibraryIngestAction
    {
        Copy,
        Remux,
        Transcode,
    }

    public enum LibraryChannelSelection
    {
        Stereo,
        Multi,
    }

    public enum LibraryIngestAlbumCondition
    {
        Any,
        HasHighResolution,
        HasNoHighResolution,
    }

    public enum LibraryIngestSourceSelection
    {
        HighestQuality,
        PreferCdQuality,
    }

    /// <summary>One ordered input match and output operation in an ingest profile.</summary>
    public sealed record LibraryIngestRecipe(
        string Id,
        string Name,
        bool Enabled,
        IReadOnlyList<string> InputExtensions,
        bool? RequireLossless,
        int? MinimumSampleRateHz,
        int? MinimumBitsPerSample,
        LibraryChannelSelection? InputChannels,
        bool MatchAnyQualityMinimum,
        LibraryIngestAction Action,
        Guid? DestinationRootId,
        LibraryIngestRole DestinationLegacyRole,
        string? OutputExtension,
        string? Codec,
        string? Encoder,
        int? BitrateKbps,
        int? SampleRateHz,
        int? BitsPerSample,
        LibraryChannelSelection? OutputChannels,
        string? NamingProfileId,
        bool PreserveMetadata,
        bool PreserveArtwork,
        LibraryPathCollisionPolicy? CollisionPolicy)
    {
        public LibraryRepresentationRole OutputRepresentationRole { get; init; } =
            LibraryRepresentationRole.Ignore;
        public string? ExtraFfmpegOptions { get; init; }
        public bool AddToMediaCatalog { get; init; }
        public LibraryIngestAlbumCondition AlbumCondition { get; init; } =
            LibraryIngestAlbumCondition.Any;
        public LibraryIngestSourceSelection SourceSelection { get; init; } =
            LibraryIngestSourceSelection.HighestQuality;
        public bool RequireFallbackApproval { get; init; }
    }

    /// <summary>
    /// Profile-level ingest safety defaults. Individual output recipes can refine these values.
    /// </summary>
    public sealed record LibraryIngestPolicy(
        bool Enabled,
        LibrarySourceDisposition SourceDisposition,
        bool PreserveSidecars,
        IReadOnlyList<LibraryIngestRecipe> Recipes);

    /// <summary>Where artwork managed by a profile is stored.</summary>
    public enum LibraryArtworkStorage
    {
        None,
        Embedded,
        Sidecar,
        Both,
    }

    /// <summary>Whether non-front artwork such as back covers, booklets, and disc images is kept.</summary>
    public enum LibraryArtworkRoleSelection
    {
        FrontCoverOnly,
        AllRoles,
    }

    /// <summary>The encoding used when artwork has to be written by an artwork workflow.</summary>
    public enum LibraryArtworkEncoding
    {
        PreserveSource,
        Jpeg,
        Png,
    }

    /// <summary>
    /// Artwork storage and fidelity choices. Zero limits mean unlimited. The sidecar template
    /// supports {Role} (cover, back, booklet, disc, or artwork-N) and {Extension}.
    /// </summary>
    public sealed record LibraryArtworkPolicy(
        LibraryArtworkStorage Storage,
        LibraryArtworkRoleSelection Roles,
        LibraryArtworkEncoding Encoding,
        int MaximumDimension,
        int MaximumEncodedBytes,
        int JpegQuality,
        string SidecarFileNameTemplate);

    /// <summary>Disposition for a file selected by a sidecar rule during source cleanup.</summary>
    public enum LibrarySidecarDisposition
    {
        Preserve,
        Quarantine,
        Delete,
        FollowSourceDisposition,
    }

    /// <summary>One ordered set of case-insensitive file-name or relative-path glob patterns.</summary>
    public sealed record LibrarySidecarRule(
        string Id,
        string Name,
        bool Enabled,
        IReadOnlyList<string> Patterns,
        LibrarySidecarDisposition Disposition);

    /// <summary>
    /// Explicit handling for non-audio files. The first matching enabled rule wins; unknown files
    /// use <see cref="UnknownFileDisposition"/> and therefore remain preserved in generic profiles.
    /// </summary>
    public sealed record LibrarySidecarPolicy(
        LibrarySidecarDisposition UnknownFileDisposition,
        IReadOnlyList<LibrarySidecarRule> Rules)
    {
        public LibrarySidecarDisposition ResolveDisposition(
            string relativePath,
            LibrarySourceDisposition sourceDisposition)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            string normalized = relativePath.Replace('\\', '/');
            string fileName = Path.GetFileName(normalized);
            LibrarySidecarDisposition selected = Rules.FirstOrDefault(rule =>
                rule.Enabled && rule.Patterns.Any(pattern =>
                    GlobMatches(pattern, normalized, fileName)))?.Disposition ??
                UnknownFileDisposition;
            if (selected != LibrarySidecarDisposition.FollowSourceDisposition)
                return selected;
            return sourceDisposition switch
            {
                LibrarySourceDisposition.Delete => LibrarySidecarDisposition.Delete,
                LibrarySourceDisposition.Quarantine => LibrarySidecarDisposition.Quarantine,
                _ => LibrarySidecarDisposition.Preserve,
            };
        }

        private static bool GlobMatches(string pattern, string relativePath, string fileName)
        {
            string normalizedPattern = pattern.Trim().Replace('\\', '/');
            string candidate = normalizedPattern.Contains('/') ? relativePath : fileName;
            return FileSystemName.MatchesSimpleExpression(
                normalizedPattern, candidate, ignoreCase: true);
        }
    }

    /// <summary>An immutable, named collection of library behavior choices.</summary>
    public sealed record LibraryProfile(
        string Id,
        string Name,
        LibraryProfilePreset Preset,
        LibraryRootPermissions DefaultRootPermissions,
        LibraryNamingPolicy Naming,
        LibraryDiscPolicy Disc,
        LibraryHealthPolicy Health,
        LibraryQualityPolicy Quality,
        LibraryIngestPolicy Ingest,
        LibraryArtworkPolicy Artwork,
        LibrarySidecarPolicy Sidecars)
    {
        public LibraryAlbumIdentityPolicy AlbumIdentity { get; init; } =
            new(true, false, true, false);
        public LibraryMetadataPolicy Metadata { get; init; } =
            new(true, true, true, true);
    }

    /// <summary>Built-in profile definitions and stable IDs used by configuration files.</summary>
    public static class LibraryProfilePresets
    {
        public const string LegacyId = "legacy";
        public const string CatalogOnlyId = "catalog-only";
        public const string PreserveLayoutAndTagsId = "preserve-layout-tags";
        public const string ArtistAlbumId = "artist-album";
        public const string ItunesMediaId = "itunes-media";

        private const string DefaultDirectoryTemplate = "{AlbumArtist}/{Album}";
        private const string DefaultFileTemplate = "{Track:00} {Title}{Extension}";

        public static IReadOnlyList<LibraryProfile> All =>
        [
            Create(LibraryProfilePreset.LegacyMusicLibraryTools),
            Create(LibraryProfilePreset.CatalogOnly),
            Create(LibraryProfilePreset.PreserveLayoutAndTags),
            Create(LibraryProfilePreset.ArtistAlbum),
            Create(LibraryProfilePreset.ItunesMedia),
        ];

        public static LibraryProfile Create(
            LibraryProfilePreset preset,
            string? id = null,
            string? name = null)
        {
            LibraryProfile profile = preset switch
            {
                LibraryProfilePreset.LegacyMusicLibraryTools => new(
                    LegacyId,
                    "Legacy MusicLibraryTools",
                    preset,
                    LibraryRootPermissions.WriteMetadata |
                    LibraryRootPermissions.WriteArtwork |
                    LibraryRootPermissions.OrganizeFiles,
                    new(DefaultDirectoryTemplate, DefaultFileTemplate, 2, 1,
                        LibraryPathCollisionPolicy.Suffix, true, "_", false, true, true),
                    new(LibraryDiscStrategy.AlbumSuffix, LibraryTrackTotalScope.PerDisc,
                        true, false),
                    LegacyHealth(),
                    new(44_101, 17),
                    new(true, LibrarySourceDisposition.Quarantine, false, LegacyRecipes()),
                    LegacyArtwork(),
                    LegacySidecars()),
                LibraryProfilePreset.CatalogOnly => new(
                    CatalogOnlyId,
                    "Catalog only",
                    preset,
                    LibraryRootPermissions.None,
                    GenericNaming(),
                    GenericDisc(),
                    InformationalHealth(),
                    new(48_000, 24),
                    new(false, LibrarySourceDisposition.Preserve, true, []),
                    GenericArtwork(LibraryArtworkStorage.None),
                    PreservationSidecars()),
                LibraryProfilePreset.PreserveLayoutAndTags => new(
                    PreserveLayoutAndTagsId,
                    "Preserve layout + tag editing",
                    preset,
                    LibraryRootPermissions.WriteMetadata | LibraryRootPermissions.WriteArtwork,
                    GenericNaming(),
                    GenericDisc(),
                    InformationalHealth(),
                    new(48_000, 24),
                    new(false, LibrarySourceDisposition.Preserve, true, []),
                    GenericArtwork(LibraryArtworkStorage.Embedded),
                    PreservationSidecars()),
                LibraryProfilePreset.ArtistAlbum => new(
                    ArtistAlbumId,
                    "Artist/Album organizer",
                    preset,
                    LibraryRootPermissions.WriteMetadata |
                    LibraryRootPermissions.WriteArtwork |
                    LibraryRootPermissions.OrganizeFiles,
                    GenericNaming(),
                    GenericDisc(),
                    InformationalHealth(),
                    new(48_000, 24),
                    new(false, LibrarySourceDisposition.Preserve, true, []),
                    GenericArtwork(LibraryArtworkStorage.Embedded),
                    PreservationSidecars()),
                LibraryProfilePreset.ItunesMedia => new(
                    ItunesMediaId,
                    "iTunes Media",
                    preset,
                    LibraryRootPermissions.WriteMetadata |
                    LibraryRootPermissions.WriteArtwork |
                    LibraryRootPermissions.OrganizeFiles,
                    GenericNaming() with { UseItunesCanonicalNaming = true },
                    GenericDisc(),
                    InformationalHealth(),
                    new(48_000, 24),
                    new(false, LibrarySourceDisposition.Preserve, true, []),
                    GenericArtwork(LibraryArtworkStorage.Embedded),
                    PreservationSidecars()),
                LibraryProfilePreset.Custom => new(
                    "custom",
                    "Custom",
                    preset,
                    LibraryRootPermissions.None,
                    GenericNaming(),
                    GenericDisc(),
                    InformationalHealth(),
                    new(48_000, 24),
                    new(false, LibrarySourceDisposition.Preserve, true, []),
                    GenericArtwork(LibraryArtworkStorage.None),
                    PreservationSidecars()),
                _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null),
            };
            profile = profile with
            {
                AlbumIdentity = preset == LibraryProfilePreset.LegacyMusicLibraryTools
                    ? new(true, true, true, false)
                    : new(true, false, true, false),
                Metadata = new(true, true, true, true),
            };
            LibraryProfile renamed = profile with
            {
                Id = string.IsNullOrWhiteSpace(id) ? profile.Id : id.Trim(),
                Name = string.IsNullOrWhiteSpace(name) ? profile.Name : name.Trim(),
            };
            if (renamed.Preset == LibraryProfilePreset.LegacyMusicLibraryTools &&
                !string.Equals(renamed.Id, LegacyId, StringComparison.OrdinalIgnoreCase))
                renamed = renamed with
                {
                    Ingest = renamed.Ingest with
                    {
                        Recipes = renamed.Ingest.Recipes.Select(recipe => recipe with
                        {
                            NamingProfileId = renamed.Id,
                        }).ToArray(),
                    },
                };
            return renamed;
        }

        private static LibraryNamingPolicy GenericNaming() => new(
            DefaultDirectoryTemplate,
            DefaultFileTemplate,
            2,
            1,
            LibraryPathCollisionPolicy.Stop,
            true,
            "_",
            false,
            false,
            false);

        private static LibraryDiscPolicy GenericDisc() => new(
            LibraryDiscStrategy.PreserveTags,
            LibraryTrackTotalScope.PerDisc,
            false,
            true);

        private static LibraryArtworkPolicy LegacyArtwork() => new(
            LibraryArtworkStorage.Embedded,
            LibraryArtworkRoleSelection.AllRoles,
            LibraryArtworkEncoding.Jpeg,
            0,
            0,
            90,
            "{Role}{Extension}");

        private static LibraryArtworkPolicy GenericArtwork(LibraryArtworkStorage storage) => new(
            storage,
            LibraryArtworkRoleSelection.AllRoles,
            LibraryArtworkEncoding.PreserveSource,
            0,
            0,
            90,
            "{Role}{Extension}");

        private static LibrarySidecarPolicy LegacySidecars() => new(
            LibrarySidecarDisposition.FollowSourceDisposition,
            DefaultSidecarRules(LibrarySidecarDisposition.FollowSourceDisposition));

        private static LibrarySidecarPolicy PreservationSidecars() => new(
            LibrarySidecarDisposition.Preserve,
            DefaultSidecarRules(LibrarySidecarDisposition.Preserve));

        private static IReadOnlyList<LibrarySidecarRule> DefaultSidecarRules(
            LibrarySidecarDisposition disposition) =>
        [
            new("cover-images", "Cover images", true,
                ["cover.*", "folder.*", "front.*", "back.*", "*.jpg", "*.jpeg",
                    "*.png", "*.webp", "*.gif", "*.bmp"], disposition),
            new("cue-sheets", "Cue sheets", true, ["*.cue"], disposition),
            new("logs", "Logs", true, ["*.log"], disposition),
            new("lyrics", "Lyrics", true, ["*.lrc", "*.lyrics", "*.txt"], disposition),
            new("booklets", "Booklets and documents", true, ["*.pdf"], disposition),
            new("checksums", "Checksums", true,
                ["*.md5", "*.sha1", "*.sha256", "*.sfv"], disposition),
        ];

        private static LibraryHealthPolicy LegacyHealth() => new(
        [
            new(LibraryHealthRuleIds.LossyFile, true, LibraryHealthSeverity.Warning, false, false),
            new(LibraryHealthRuleIds.MissingAlbumArtist, true, LibraryHealthSeverity.Warning, true, true),
            new(LibraryHealthRuleIds.MissingTrackTotal, true, LibraryHealthSeverity.Warning, true, true),
            new(LibraryHealthRuleIds.DiscMetadata, true, LibraryHealthSeverity.Warning, true, true),
            new(LibraryHealthRuleIds.Id3Version, true, LibraryHealthSeverity.Warning, true, true),
            new(LibraryHealthRuleIds.NormalizeWhitespace, true, LibraryHealthSeverity.Warning, true, true),
            new(LibraryHealthRuleIds.DiscAlbumTitle, true, LibraryHealthSeverity.Warning, true, true),
        ]);

        private static LibraryHealthPolicy InformationalHealth() => new(
        [
            new(LibraryHealthRuleIds.LossyFile, true, LibraryHealthSeverity.Information, false, false),
            new(LibraryHealthRuleIds.MissingAlbumArtist, true, LibraryHealthSeverity.Information, false, false),
            new(LibraryHealthRuleIds.MissingTrackTotal, true, LibraryHealthSeverity.Information, false, false),
            new(LibraryHealthRuleIds.DiscMetadata, true, LibraryHealthSeverity.Information, false, false),
            new(LibraryHealthRuleIds.Id3Version, true, LibraryHealthSeverity.Information, false, false),
            new(LibraryHealthRuleIds.NormalizeWhitespace, true, LibraryHealthSeverity.Information, false, false),
            new(LibraryHealthRuleIds.DiscAlbumTitle, true, LibraryHealthSeverity.Information, false, false),
        ]);

        private static IReadOnlyList<LibraryIngestRecipe> LegacyRecipes() =>
        [
            new(
                "legacy-hires-flac",
                "High-resolution FLAC",
                true,
                [".flac", ".m4a"],
                true,
                44_101,
                17,
                LibraryChannelSelection.Stereo,
                true,
                LibraryIngestAction.Transcode,
                null,
                LibraryIngestRole.HiRes,
                ".flac",
                "flac",
                null,
                null,
                null,
                null,
                LibraryChannelSelection.Stereo,
                LegacyId,
                true,
                true,
                LibraryPathCollisionPolicy.Suffix),
            new(
                "legacy-cd-flac",
                "CD-quality FLAC",
                true,
                [".flac", ".m4a"],
                true,
                44_100,
                16,
                LibraryChannelSelection.Stereo,
                false,
                LibraryIngestAction.Transcode,
                null,
                LibraryIngestRole.Cd,
                ".flac",
                "flac",
                null,
                null,
                44_100,
                16,
                LibraryChannelSelection.Stereo,
                LegacyId,
                true,
                true,
                LibraryPathCollisionPolicy.Suffix)
            {
                AlbumCondition = LibraryIngestAlbumCondition.HasNoHighResolution,
                SourceSelection = LibraryIngestSourceSelection.PreferCdQuality,
            },
            new(
                "legacy-paired-cd-flac",
                "Paired CD-quality FLAC",
                true,
                [".flac", ".m4a"],
                true,
                44_100,
                16,
                LibraryChannelSelection.Stereo,
                false,
                LibraryIngestAction.Transcode,
                null,
                LibraryIngestRole.CdFallback,
                ".flac",
                "flac",
                null,
                null,
                44_100,
                16,
                LibraryChannelSelection.Stereo,
                LegacyId,
                true,
                true,
                LibraryPathCollisionPolicy.Suffix)
            {
                AlbumCondition = LibraryIngestAlbumCondition.HasHighResolution,
                SourceSelection = LibraryIngestSourceSelection.PreferCdQuality,
                RequireFallbackApproval = true,
            },
            new(
                "legacy-aac",
                "Portable AAC",
                true,
                [".flac", ".m4a"],
                true,
                44_100,
                16,
                LibraryChannelSelection.Stereo,
                false,
                LibraryIngestAction.Transcode,
                null,
                LibraryIngestRole.AacFallback,
                ".m4a",
                "aac",
                null,
                256,
                44_100,
                null,
                LibraryChannelSelection.Stereo,
                LegacyId,
                true,
                true,
                LibraryPathCollisionPolicy.Suffix)
            {
                SourceSelection = LibraryIngestSourceSelection.PreferCdQuality,
            },
        ];
    }

    /// <summary>XML reader/writer for named profile definitions in schema v2.</summary>
    public static class LibraryProfileXml
    {
        private static readonly Regex IdPattern = new(
            "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
            RegexOptions.CultureInvariant);
        private static readonly Regex TemplateTokenPattern = new(
            @"\{(?<name>[A-Za-z]+)(?::(?<format>[^{}]+))?\}",
            RegexOptions.CultureInvariant);
        private static readonly HashSet<string> TemplateTokens = new(
            ["AlbumArtist", "Artist", "Album", "Title", "Compilation", "Year", "Genre",
                "Disc", "Track", "OriginalName", "Extension"],
            StringComparer.OrdinalIgnoreCase);

        public static LibraryProfile Parse(XElement element)
        {
            ArgumentNullException.ThrowIfNull(element);
            string id = Required(element, "Id");
            ValidateId(id, "profile");
            string name = Required(element, "Name");
            LibraryProfilePreset preset = ParseEnum(
                element, "Preset", LibraryProfilePreset.Custom);
            LibraryProfile fallback = LibraryProfilePresets.Create(preset, id, name);

            LibraryRootPermissions permissions = ParseFlags(
                element, "DefaultRootPermissions", fallback.DefaultRootPermissions);
            XElement? namingElement = element.Element("Naming");
            LibraryNamingPolicy naming = fallback.Naming with
            {
                DirectoryTemplate = Optional(namingElement, "DirectoryTemplate") ??
                    fallback.Naming.DirectoryTemplate,
                FileNameTemplate = Optional(namingElement, "FileNameTemplate") ??
                    fallback.Naming.FileNameTemplate,
                TrackPadding = ParsePositiveInteger(
                    namingElement, "TrackPadding", fallback.Naming.TrackPadding),
                DiscPadding = ParsePositiveInteger(
                    namingElement, "DiscPadding", fallback.Naming.DiscPadding),
                CollisionPolicy = ParseEnum(
                    namingElement, "CollisionPolicy", fallback.Naming.CollisionPolicy),
                PreserveUnicode = ParseBoolean(
                    namingElement, "PreserveUnicode", fallback.Naming.PreserveUnicode),
                InvalidCharacterReplacement =
                    (string?)namingElement?.Attribute("InvalidCharacterReplacement") ??
                    fallback.Naming.InvalidCharacterReplacement,
                UseItunesCanonicalNaming = ParseBoolean(
                    namingElement, "UseItunesCanonicalNaming",
                    fallback.Naming.UseItunesCanonicalNaming),
                LegacySanitization = ParseBoolean(
                    namingElement, "LegacySanitization", fallback.Naming.LegacySanitization),
                StripFormatSuffixes = ParseBoolean(
                    namingElement, "StripFormatSuffixes", fallback.Naming.StripFormatSuffixes),
                MissingArtistFallback = Optional(namingElement,
                    "MissingArtistFallback") ?? fallback.Naming.MissingArtistFallback,
                MissingAlbumFallback = Optional(namingElement,
                    "MissingAlbumFallback") ?? fallback.Naming.MissingAlbumFallback,
                MissingTitleFallback = Optional(namingElement,
                    "MissingTitleFallback") ?? fallback.Naming.MissingTitleFallback,
                CompilationValue = Optional(namingElement,
                    "CompilationValue") ?? fallback.Naming.CompilationValue,
                UnicodeNormalization = ParseEnum(namingElement,
                    "UnicodeNormalization", fallback.Naming.UnicodeNormalization),
                ComponentLengthLimit = ParseOptionalPositiveInteger(namingElement,
                    "ComponentLengthLimit"),
                CompletePathLengthLimit = ParseOptionalPositiveInteger(namingElement,
                    "CompletePathLengthLimit"),
            };

            XElement? discElement = element.Element("Disc");
            LibraryDiscPolicy disc = fallback.Disc with
            {
                Strategy = ParseEnum(discElement, "Strategy", fallback.Disc.Strategy),
                TrackTotalScope = ParseEnum(
                    discElement, "TrackTotalScope", fallback.Disc.TrackTotalScope),
                InferAlbumSuffix = ParseBoolean(
                    discElement, "InferAlbumSuffix", fallback.Disc.InferAlbumSuffix),
                PreserveDiscTags = ParseBoolean(
                    discElement, "PreserveDiscTags", fallback.Disc.PreserveDiscTags),
            };

            XElement? identityElement = element.Element("AlbumIdentity");
            LibraryAlbumIdentityPolicy albumIdentity = fallback.AlbumIdentity with
            {
                UseAlbumArtist = ParseBoolean(identityElement, "UseAlbumArtist",
                    fallback.AlbumIdentity.UseAlbumArtist),
                StripFormatSuffixes = ParseBoolean(identityElement,
                    "StripFormatSuffixes", fallback.AlbumIdentity.StripFormatSuffixes),
                StripDiscSuffixes = ParseBoolean(identityElement,
                    "StripDiscSuffixes", fallback.AlbumIdentity.StripDiscSuffixes),
                IncludeReleaseYear = ParseBoolean(identityElement,
                    "IncludeReleaseYear", fallback.AlbumIdentity.IncludeReleaseYear),
            };

            XElement? metadataElement = element.Element("Metadata");
            LibraryMetadataPolicy metadata = fallback.Metadata with
            {
                PreserveReplayGain = ParseBoolean(metadataElement,
                    "PreserveReplayGain", fallback.Metadata.PreserveReplayGain),
                PreserveMusicBrainzIdentifiers = ParseBoolean(metadataElement,
                    "PreserveMusicBrainzIdentifiers",
                    fallback.Metadata.PreserveMusicBrainzIdentifiers),
                PreserveCustomFields = ParseBoolean(metadataElement,
                    "PreserveCustomFields", fallback.Metadata.PreserveCustomFields),
                PreserveCompilationSemantics = ParseBoolean(metadataElement,
                    "PreserveCompilationSemantics",
                    fallback.Metadata.PreserveCompilationSemantics),
            };

            XElement? healthElement = element.Element("Health");
            LibraryHealthPolicy health = healthElement is null
                ? fallback.Health
                : new(healthElement.Elements("Rule").Select(ParseHealthRule).ToArray());

            XElement? qualityElement = element.Element("Quality");
            LibraryQualityPolicy quality = fallback.Quality with
            {
                HighResolutionMinimumSampleRateHz = ParsePositiveInteger(
                    qualityElement,
                    "HighResolutionMinimumSampleRateHz",
                    fallback.Quality.HighResolutionMinimumSampleRateHz),
                HighResolutionMinimumBitsPerSample = ParsePositiveInteger(
                    qualityElement,
                    "HighResolutionMinimumBitsPerSample",
                    fallback.Quality.HighResolutionMinimumBitsPerSample),
            };

            XElement? ingestElement = element.Element("Ingest");
            XElement[] recipeElements = ingestElement?.Elements("Recipe").ToArray() ?? [];
            LibraryIngestPolicy ingest = fallback.Ingest with
            {
                Enabled = ParseBoolean(ingestElement, "Enabled", fallback.Ingest.Enabled),
                SourceDisposition = ParseEnum(
                    ingestElement, "SourceDisposition", fallback.Ingest.SourceDisposition),
                PreserveSidecars = ParseBoolean(
                    ingestElement, "PreserveSidecars", fallback.Ingest.PreserveSidecars),
                Recipes = ingestElement is null
                    ? fallback.Ingest.Recipes
                    : recipeElements.Select(ParseIngestRecipe).ToArray(),
            };

            XElement? artworkElement = element.Element("Artwork");
            LibraryArtworkPolicy artwork = fallback.Artwork with
            {
                Storage = ParseEnum(
                    artworkElement, "Storage", fallback.Artwork.Storage),
                Roles = ParseEnum(
                    artworkElement, "Roles", fallback.Artwork.Roles),
                Encoding = ParseEnum(
                    artworkElement, "Encoding", fallback.Artwork.Encoding),
                MaximumDimension = ParseNonNegativeInteger(
                    artworkElement, "MaximumDimension", fallback.Artwork.MaximumDimension),
                MaximumEncodedBytes = ParseNonNegativeInteger(
                    artworkElement, "MaximumEncodedBytes", fallback.Artwork.MaximumEncodedBytes),
                JpegQuality = ParsePositiveInteger(
                    artworkElement, "JpegQuality", fallback.Artwork.JpegQuality),
                SidecarFileNameTemplate =
                    Optional(artworkElement, "SidecarFileNameTemplate") ??
                    fallback.Artwork.SidecarFileNameTemplate,
            };

            XElement? sidecarsElement = element.Element("Sidecars");
            LibrarySidecarPolicy sidecars = sidecarsElement is null
                ? fallback.Sidecars
                : new(
                    ParseEnum(sidecarsElement, "UnknownFileDisposition",
                        fallback.Sidecars.UnknownFileDisposition),
                    sidecarsElement.Elements("Rule").Select(ParseSidecarRule).ToArray());

            var result = new LibraryProfile(
                id, name, preset, permissions, naming, disc, health, quality, ingest,
                artwork, sidecars)
            {
                AlbumIdentity = albumIdentity,
                Metadata = metadata,
            };
            Validate(result);
            return result;
        }

        public static XElement Write(LibraryProfile profile)
        {
            Validate(profile);
            return new XElement("LibraryProfile",
                new XAttribute("Id", profile.Id),
                new XAttribute("Name", profile.Name),
                new XAttribute("Preset", profile.Preset),
                new XAttribute("DefaultRootPermissions", FormatFlags(profile.DefaultRootPermissions)),
                new XElement("Naming",
                    new XAttribute("DirectoryTemplate", profile.Naming.DirectoryTemplate),
                    new XAttribute("FileNameTemplate", profile.Naming.FileNameTemplate),
                    new XAttribute("TrackPadding", profile.Naming.TrackPadding),
                    new XAttribute("DiscPadding", profile.Naming.DiscPadding),
                    new XAttribute("CollisionPolicy", profile.Naming.CollisionPolicy),
                    new XAttribute("PreserveUnicode", profile.Naming.PreserveUnicode),
                    new XAttribute("InvalidCharacterReplacement",
                        profile.Naming.InvalidCharacterReplacement),
                    new XAttribute("UseItunesCanonicalNaming",
                        profile.Naming.UseItunesCanonicalNaming),
                    new XAttribute("LegacySanitization", profile.Naming.LegacySanitization),
                    new XAttribute("StripFormatSuffixes", profile.Naming.StripFormatSuffixes),
                    new XAttribute("MissingArtistFallback",
                        profile.Naming.MissingArtistFallback),
                    new XAttribute("MissingAlbumFallback",
                        profile.Naming.MissingAlbumFallback),
                    new XAttribute("MissingTitleFallback",
                        profile.Naming.MissingTitleFallback),
                    new XAttribute("CompilationValue", profile.Naming.CompilationValue),
                    new XAttribute("UnicodeNormalization",
                        profile.Naming.UnicodeNormalization),
                    profile.Naming.ComponentLengthLimit is { } componentLimit
                        ? new XAttribute("ComponentLengthLimit", componentLimit)
                        : null,
                    profile.Naming.CompletePathLengthLimit is { } pathLimit
                        ? new XAttribute("CompletePathLengthLimit", pathLimit)
                        : null),
                new XElement("Disc",
                    new XAttribute("Strategy", profile.Disc.Strategy),
                    new XAttribute("TrackTotalScope", profile.Disc.TrackTotalScope),
                    new XAttribute("InferAlbumSuffix", profile.Disc.InferAlbumSuffix),
                    new XAttribute("PreserveDiscTags", profile.Disc.PreserveDiscTags)),
                new XElement("AlbumIdentity",
                    new XAttribute("UseAlbumArtist", profile.AlbumIdentity.UseAlbumArtist),
                    new XAttribute("StripFormatSuffixes",
                        profile.AlbumIdentity.StripFormatSuffixes),
                    new XAttribute("StripDiscSuffixes",
                        profile.AlbumIdentity.StripDiscSuffixes),
                    new XAttribute("IncludeReleaseYear",
                        profile.AlbumIdentity.IncludeReleaseYear)),
                new XElement("Metadata",
                    new XAttribute("PreserveReplayGain",
                        profile.Metadata.PreserveReplayGain),
                    new XAttribute("PreserveMusicBrainzIdentifiers",
                        profile.Metadata.PreserveMusicBrainzIdentifiers),
                    new XAttribute("PreserveCustomFields",
                        profile.Metadata.PreserveCustomFields),
                    new XAttribute("PreserveCompilationSemantics",
                        profile.Metadata.PreserveCompilationSemantics)),
                new XElement("Health", profile.Health.Rules.Select(rule =>
                    new XElement("Rule",
                        new XAttribute("Id", rule.Id),
                        new XAttribute("Enabled", rule.Enabled),
                        new XAttribute("Severity", rule.Severity),
                        new XAttribute("ProposeRepair", rule.ProposeRepair),
                        new XAttribute("ApplyRepair", rule.ApplyRepair)))),
                new XElement("Quality",
                    new XAttribute("HighResolutionMinimumSampleRateHz",
                        profile.Quality.HighResolutionMinimumSampleRateHz),
                    new XAttribute("HighResolutionMinimumBitsPerSample",
                        profile.Quality.HighResolutionMinimumBitsPerSample)),
                new XElement("Ingest",
                    new XAttribute("Enabled", profile.Ingest.Enabled),
                    new XAttribute("SourceDisposition", profile.Ingest.SourceDisposition),
                    new XAttribute("PreserveSidecars", profile.Ingest.PreserveSidecars),
                    profile.Ingest.Recipes.Select(WriteIngestRecipe)),
                new XElement("Artwork",
                    new XAttribute("Storage", profile.Artwork.Storage),
                    new XAttribute("Roles", profile.Artwork.Roles),
                    new XAttribute("Encoding", profile.Artwork.Encoding),
                    new XAttribute("MaximumDimension", profile.Artwork.MaximumDimension),
                    new XAttribute("MaximumEncodedBytes", profile.Artwork.MaximumEncodedBytes),
                    new XAttribute("JpegQuality", profile.Artwork.JpegQuality),
                    new XAttribute("SidecarFileNameTemplate",
                        profile.Artwork.SidecarFileNameTemplate)),
                new XElement("Sidecars",
                    new XAttribute("UnknownFileDisposition",
                        profile.Sidecars.UnknownFileDisposition),
                    profile.Sidecars.Rules.Select(rule =>
                        new XElement("Rule",
                            new XAttribute("Id", rule.Id),
                            new XAttribute("Name", rule.Name),
                            new XAttribute("Enabled", rule.Enabled),
                            new XAttribute("Patterns", string.Join(",", rule.Patterns)),
                            new XAttribute("Disposition", rule.Disposition)))));
        }

        public static void Validate(LibraryProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ValidateId(profile.Id, "profile");
            if (string.IsNullOrWhiteSpace(profile.Name))
                throw new InvalidDataException($"Library profile '{profile.Id}' must have a name.");
            ValidateEnum(profile.Preset, profile.Id, "profile preset");
            ValidateEnum(profile.Naming.CollisionPolicy, profile.Id, "naming collision policy");
            ValidateEnum(profile.Naming.UnicodeNormalization, profile.Id,
                "Unicode normalization policy");
            ValidateEnum(profile.Disc.Strategy, profile.Id, "disc strategy");
            ValidateEnum(profile.Disc.TrackTotalScope, profile.Id, "track-total scope");
            ValidateEnum(profile.Ingest.SourceDisposition, profile.Id,
                "ingest source disposition");
            ValidateEnum(profile.Artwork.Storage, profile.Id, "artwork storage policy");
            ValidateEnum(profile.Artwork.Roles, profile.Id, "artwork role policy");
            ValidateEnum(profile.Artwork.Encoding, profile.Id, "artwork encoding policy");
            ValidateEnum(profile.Sidecars.UnknownFileDisposition, profile.Id,
                "unknown-sidecar disposition");
            if (string.IsNullOrWhiteSpace(profile.Naming.DirectoryTemplate))
                throw new InvalidDataException(
                    $"Library profile '{profile.Id}' must have a directory template.");
            if (string.IsNullOrWhiteSpace(profile.Naming.FileNameTemplate))
                throw new InvalidDataException(
                    $"Library profile '{profile.Id}' must have a file-name template.");
            ValidateNamingTemplateOverrides(profile.Id, profile.Naming.DirectoryTemplate,
                profile.Naming.FileNameTemplate);
            if (profile.Naming.TrackPadding <= 0 || profile.Naming.DiscPadding <= 0)
                throw new InvalidDataException(
                    $"Library profile '{profile.Id}' track and disc padding must be positive.");
            if (profile.Quality.HighResolutionMinimumSampleRateHz <= 0 ||
                profile.Quality.HighResolutionMinimumBitsPerSample <= 0)
                throw new InvalidDataException(
                    $"Library profile '{profile.Id}' quality thresholds must be positive.");
            if ((profile.DefaultRootPermissions & ~LibraryRootPermissions.All) != 0)
                throw new InvalidDataException(
                    $"Library profile '{profile.Id}' contains unknown root permissions.");
            if (profile.Naming.InvalidCharacterReplacement is null)
                throw new InvalidDataException(
                    $"Library profile '{profile.Id}' invalid-character replacement cannot be null.");
            if (profile.Naming.InvalidCharacterReplacement.IndexOfAny(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
                throw new InvalidDataException(
                    $"Library profile '{profile.Id}' invalid-character replacement cannot " +
                    "contain a directory separator.");
            if (string.IsNullOrWhiteSpace(profile.Naming.MissingArtistFallback) ||
                string.IsNullOrWhiteSpace(profile.Naming.MissingAlbumFallback) ||
                string.IsNullOrWhiteSpace(profile.Naming.MissingTitleFallback) ||
                string.IsNullOrWhiteSpace(profile.Naming.CompilationValue))
                throw new InvalidDataException(
                    $"Library profile '{profile.Id}' naming fallbacks cannot be blank.");
            ValidateOptionalPositive(profile.Naming.ComponentLengthLimit,
                profile.Id, "component length limit");
            ValidateOptionalPositive(profile.Naming.CompletePathLengthLimit,
                profile.Id, "complete path length limit");

            string[] duplicateRules = profile.Health.Rules
                .GroupBy(rule => rule.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            if (duplicateRules.Length > 0)
                throw new InvalidDataException(
                    $"Library profile '{profile.Id}' contains duplicate health rule(s): " +
                    string.Join(", ", duplicateRules));
            foreach (LibraryHealthRulePolicy rule in profile.Health.Rules)
            {
                ValidateId(rule.Id, "health rule");
                ValidateEnum(rule.Severity, profile.Id,
                    $"health rule '{rule.Id}' severity");
            }

            string[] duplicateRecipes = profile.Ingest.Recipes
                .GroupBy(recipe => recipe.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            if (duplicateRecipes.Length > 0)
                throw new InvalidDataException(
                    $"Library profile '{profile.Id}' contains duplicate ingest recipe(s): " +
                    string.Join(", ", duplicateRecipes));
            foreach (LibraryIngestRecipe recipe in profile.Ingest.Recipes)
            {
                ValidateEnum(recipe.Action, profile.Id,
                    $"ingest recipe '{recipe.Id}' action");
                ValidateEnum(recipe.DestinationLegacyRole, profile.Id,
                    $"ingest recipe '{recipe.Id}' legacy destination role");
                if (recipe.CollisionPolicy is { } collisionPolicy)
                    ValidateEnum(collisionPolicy, profile.Id,
                        $"ingest recipe '{recipe.Id}' collision policy");
                ValidateEnum(recipe.OutputRepresentationRole, profile.Id,
                    $"ingest recipe '{recipe.Id}' representation role");
                ValidateIngestRecipe(profile.Id, recipe, profile.Artwork);
            }

            if (profile.Artwork.MaximumDimension < 0 ||
                profile.Artwork.MaximumEncodedBytes < 0)
                throw new InvalidDataException(
                    $"Library profile '{profile.Id}' artwork limits cannot be negative.");
            if (profile.Artwork.JpegQuality is < 1 or > 100)
                throw new InvalidDataException(
                    $"Library profile '{profile.Id}' JPEG quality must be from 1 through 100.");
            ValidateArtworkTemplate(profile.Id, profile.Artwork.SidecarFileNameTemplate);

            string[] duplicateSidecarRules = profile.Sidecars.Rules
                .GroupBy(rule => rule.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            if (duplicateSidecarRules.Length > 0)
                throw new InvalidDataException(
                    $"Library profile '{profile.Id}' contains duplicate sidecar rule(s): " +
                    string.Join(", ", duplicateSidecarRules));
            foreach (LibrarySidecarRule rule in profile.Sidecars.Rules)
            {
                ValidateId(rule.Id, "sidecar rule");
                ValidateEnum(rule.Disposition, profile.Id,
                    $"sidecar rule '{rule.Id}' disposition");
                if (string.IsNullOrWhiteSpace(rule.Name))
                    throw new InvalidDataException(
                        $"Sidecar rule '{rule.Id}' in profile '{profile.Id}' must have a name.");
                if (rule.Patterns.Count == 0 || rule.Patterns.Any(string.IsNullOrWhiteSpace))
                    throw new InvalidDataException(
                        $"Sidecar rule '{rule.Id}' in profile '{profile.Id}' must have at least " +
                        "one non-empty pattern.");
            }
        }

        private static void ValidateEnum<T>(T value, string profileId, string description)
            where T : struct, Enum
        {
            if (!Enum.IsDefined(value))
                throw new InvalidDataException(
                    $"Library profile '{profileId}' contains an unsupported {description} value.");
        }

        private static void ValidateArtworkTemplate(string profileId, string template)
        {
            if (string.IsNullOrWhiteSpace(template))
                throw new InvalidDataException(
                    $"Library profile '{profileId}' must have an artwork sidecar template.");
            string remaining = template
                .Replace("{Role}", "", StringComparison.OrdinalIgnoreCase)
                .Replace("{Extension}", "", StringComparison.OrdinalIgnoreCase);
            if (remaining.Contains('{') || remaining.Contains('}') ||
                template.IndexOfAny(['/', '\\']) >= 0)
                throw new InvalidDataException(
                    $"Library profile '{profileId}' has an invalid artwork sidecar template. " +
                    "Only {Role} and {Extension} are supported, without directory separators.");
            if (!template.Contains("{Role}", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Library profile '{profileId}' artwork sidecar template must contain " +
                    "{Role}.");
        }

        public static void ValidateNamingTemplateOverrides(
            string ownerId,
            string? directoryTemplate,
            string? fileNameTemplate)
        {
            ValidateId(ownerId, "naming policy owner");
            if (!string.IsNullOrWhiteSpace(directoryTemplate))
                ValidateNamingTemplate(ownerId, "directory", directoryTemplate,
                    allowSeparators: true);
            if (!string.IsNullOrWhiteSpace(fileNameTemplate))
                ValidateNamingTemplate(ownerId, "file-name", fileNameTemplate,
                    allowSeparators: false);
        }

        private static void ValidateNamingTemplate(
            string profileId,
            string description,
            string template,
            bool allowSeparators)
        {
            foreach (Match match in TemplateTokenPattern.Matches(template))
            {
                string name = match.Groups["name"].Value;
                if (!TemplateTokens.Contains(name))
                    throw new InvalidDataException(
                        $"Library profile '{profileId}' contains unknown naming token " +
                        $"'{{{name}}}'.");
                if (match.Groups["format"].Success &&
                    !name.Equals("Track", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("Disc", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Library profile '{profileId}' can format only Track and Disc tokens.");
                if (match.Groups["format"].Success)
                {
                    try
                    {
                        _ = 1.ToString(match.Groups["format"].Value, CultureInfo.InvariantCulture);
                    }
                    catch (FormatException exception)
                    {
                        throw new InvalidDataException(
                            $"Library profile '{profileId}' contains an invalid numeric token " +
                            "format.", exception);
                    }
                }
            }

            string withoutTokens = TemplateTokenPattern.Replace(template, "");
            if (withoutTokens.Contains('{') || withoutTokens.Contains('}'))
                throw new InvalidDataException(
                    $"Library profile '{profileId}' has unmatched braces in its {description} " +
                    "template.");
            int optionalDepth = 0;
            foreach (char character in template)
            {
                if (character == '[' && ++optionalDepth > 1)
                    throw new InvalidDataException(
                        $"Library profile '{profileId}' cannot nest optional template fragments.");
                if (character == ']' && --optionalDepth < 0)
                    throw new InvalidDataException(
                        $"Library profile '{profileId}' has unmatched brackets in its " +
                        $"{description} template.");
            }
            if (optionalDepth != 0)
                throw new InvalidDataException(
                    $"Library profile '{profileId}' has unmatched brackets in its {description} " +
                    "template.");
            if (!allowSeparators && template.IndexOfAny(['/', '\\']) >= 0)
                throw new InvalidDataException(
                    $"Library profile '{profileId}' file-name template cannot contain a " +
                    "directory separator.");
        }

        public static void ValidateId(string? id, string description)
        {
            if (!IdPattern.IsMatch(id ?? ""))
                throw new InvalidDataException(
                    $"Invalid {description} ID '{id}'. IDs must start with an ASCII letter or " +
                    "digit and contain at most 64 letters, digits, dots, underscores, or hyphens.");
        }

        public static LibraryRootPermissions ParsePermissions(
            string? value,
            LibraryRootPermissions fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;
            if (!Enum.TryParse(value, true, out LibraryRootPermissions parsed) ||
                (parsed & ~LibraryRootPermissions.All) != 0)
                throw new InvalidDataException(
                    $"Invalid root Permissions '{value}'. Expected a comma-separated combination " +
                    "of WriteMetadata, WriteArtwork, OrganizeFiles, IngestOutput, or SynchronizeOutput.");
            return parsed;
        }

        public static string FormatFlags(LibraryRootPermissions permissions) =>
            permissions == LibraryRootPermissions.None ? "None" : permissions.ToString();

        private static LibraryHealthRulePolicy ParseHealthRule(XElement element)
        {
            string id = Required(element, "Id");
            ValidateId(id, "health rule");
            return new(
                id,
                ParseBoolean(element, "Enabled", true),
                ParseEnum(element, "Severity", LibraryHealthSeverity.Warning),
                ParseBoolean(element, "ProposeRepair", false),
                ParseBoolean(element, "ApplyRepair", false));
        }

        private static LibrarySidecarRule ParseSidecarRule(XElement element)
        {
            string id = Required(element, "Id");
            ValidateId(id, "sidecar rule");
            string[] patterns = (Optional(element, "Patterns") ?? "")
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new(
                id,
                Optional(element, "Name") ?? id,
                ParseBoolean(element, "Enabled", true),
                patterns,
                ParseEnum(element, "Disposition", LibrarySidecarDisposition.Preserve));
        }

        private static LibraryIngestRecipe ParseIngestRecipe(XElement element)
        {
            string id = Required(element, "Id");
            ValidateId(id, "ingest recipe");
            string name = Optional(element, "Name") ?? id;
            XElement? match = element.Element("Match");
            XElement? output = element.Element("Output");
            string[] extensions = (Optional(match, "InputExtensions") ?? "")
                .Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeExtension)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Guid? destinationRootId = ParseOptionalGuid(output, "DestinationRootId");
            LibraryIngestRole destinationRole = ParseEnum(
                output, "DestinationLegacyRole", LibraryIngestRole.None);
            LibraryPathCollisionPolicy? collision = ParseOptionalEnum<LibraryPathCollisionPolicy>(
                output, "CollisionPolicy");
            return new(
                id,
                name,
                ParseBoolean(element, "Enabled", true),
                extensions,
                ParseOptionalBoolean(match, "RequireLossless"),
                ParseOptionalPositiveInteger(match, "MinimumSampleRateHz"),
                ParseOptionalPositiveInteger(match, "MinimumBitsPerSample"),
                ParseOptionalChannelSelection(match, "InputChannels"),
                ParseBoolean(match, "MatchAnyQualityMinimum", false),
                ParseEnum(element, "Action", LibraryIngestAction.Copy),
                destinationRootId,
                destinationRole,
                NormalizeOptionalExtension(Optional(output, "OutputExtension")),
                Optional(output, "Codec"),
                Optional(output, "Encoder"),
                ParseOptionalPositiveInteger(output, "BitrateKbps"),
                ParseOptionalPositiveInteger(output, "SampleRateHz"),
                ParseOptionalPositiveInteger(output, "BitsPerSample"),
                ParseOptionalChannelSelection(output, "OutputChannels"),
                Optional(output, "NamingProfileId"),
                ParseBoolean(output, "PreserveMetadata", true),
                ParseBoolean(output, "PreserveArtwork", true),
                collision)
            {
                OutputRepresentationRole = ParseEnum(output,
                    "RepresentationRole", LibraryRepresentationRole.Ignore),
                AlbumCondition = ParseEnum(match, "AlbumCondition",
                    LibraryIngestAlbumCondition.Any),
                SourceSelection = ParseEnum(match, "SourceSelection",
                    LibraryIngestSourceSelection.HighestQuality),
                RequireFallbackApproval = ParseBoolean(match,
                    "RequireFallbackApproval", false),
                ExtraFfmpegOptions = Optional(output, "ExtraFfmpegOptions"),
                AddToMediaCatalog = ParseBoolean(output, "AddToMediaCatalog", false),
            };
        }

        private static XElement WriteIngestRecipe(LibraryIngestRecipe recipe)
        {
            var match = new XElement("Match");
            if (recipe.InputExtensions.Count > 0)
                match.SetAttributeValue("InputExtensions", string.Join(",", recipe.InputExtensions));
            SetOptional(match, "RequireLossless", recipe.RequireLossless);
            SetOptional(match, "MinimumSampleRateHz", recipe.MinimumSampleRateHz);
            SetOptional(match, "MinimumBitsPerSample", recipe.MinimumBitsPerSample);
            SetChannelSelection(match, "InputChannels", recipe.InputChannels);
            if (recipe.MatchAnyQualityMinimum)
                match.SetAttributeValue("MatchAnyQualityMinimum", true);
            if (recipe.AlbumCondition != LibraryIngestAlbumCondition.Any)
                match.SetAttributeValue("AlbumCondition", recipe.AlbumCondition);
            if (recipe.SourceSelection != LibraryIngestSourceSelection.HighestQuality)
                match.SetAttributeValue("SourceSelection", recipe.SourceSelection);
            if (recipe.RequireFallbackApproval)
                match.SetAttributeValue("RequireFallbackApproval", true);

            var output = new XElement("Output");
            SetOptional(output, "DestinationRootId", recipe.DestinationRootId);
            SetOptional(output, "OutputExtension", recipe.OutputExtension);
            SetOptional(output, "Codec", recipe.Codec);
            SetOptional(output, "Encoder", recipe.Encoder);
            SetOptional(output, "ExtraFfmpegOptions", recipe.ExtraFfmpegOptions);
            if (recipe.AddToMediaCatalog)
                output.SetAttributeValue("AddToMediaCatalog", true);
            SetOptional(output, "BitrateKbps", recipe.BitrateKbps);
            SetOptional(output, "SampleRateHz", recipe.SampleRateHz);
            SetOptional(output, "BitsPerSample", recipe.BitsPerSample);
            SetChannelSelection(output, "OutputChannels", recipe.OutputChannels);
            SetOptional(output, "NamingProfileId", recipe.NamingProfileId);
            output.SetAttributeValue("PreserveMetadata", recipe.PreserveMetadata);
            output.SetAttributeValue("PreserveArtwork", recipe.PreserveArtwork);
            SetOptional(output, "CollisionPolicy", recipe.CollisionPolicy);

            return new XElement("Recipe",
                new XAttribute("Id", recipe.Id),
                new XAttribute("Name", recipe.Name),
                new XAttribute("Enabled", recipe.Enabled),
                new XAttribute("Action", recipe.Action),
                match,
                output);
        }

        private static void ValidateIngestRecipe(
            string profileId,
            LibraryIngestRecipe recipe,
            LibraryArtworkPolicy artworkPolicy)
        {
            ValidateId(recipe.Id, "ingest recipe");
            if (string.IsNullOrWhiteSpace(recipe.Name))
                throw new InvalidDataException(
                    $"Ingest recipe '{recipe.Id}' in profile '{profileId}' must have a name.");
            string[] inputExtensions = recipe.InputExtensions
                .Select(NormalizeExtension).ToArray();
            if (recipe.Enabled && inputExtensions.Length == 0)
                throw new InvalidDataException(
                    $"Enabled ingest recipe '{recipe.Id}' must select at least one input extension.");
            foreach (string extension in inputExtensions)
                if (!MediaFormatRegistry.Default.TryGetByExtension(extension, out _))
                    throw new InvalidDataException(
                        $"Ingest recipe '{recipe.Id}' uses unsupported input extension " +
                        $"'{extension}'. Install a media adapter before enabling this recipe.");
            if (recipe.DestinationRootId == Guid.Empty)
                throw new InvalidDataException(
                    $"Ingest recipe '{recipe.Id}' has an empty DestinationRootId.");
            if (recipe.Enabled && recipe.DestinationRootId is null &&
                recipe.DestinationLegacyRole == LibraryIngestRole.None &&
                !recipe.AddToMediaCatalog)
                throw new InvalidDataException(
                    $"Enabled ingest recipe '{recipe.Id}' must select a destination root or " +
                    "the configured media catalog.");
            if (recipe.NamingProfileId is { } namingProfileId)
                ValidateId(namingProfileId, "naming profile");
            ValidateOptionalPositive(recipe.MinimumSampleRateHz, recipe.Id, "minimum sample rate");
            ValidateOptionalPositive(recipe.MinimumBitsPerSample, recipe.Id, "minimum bit depth");
            ValidateOptionalPositive(recipe.BitrateKbps, recipe.Id, "bitrate");
            ValidateOptionalPositive(recipe.SampleRateHz, recipe.Id, "output sample rate");
            ValidateOptionalPositive(recipe.BitsPerSample, recipe.Id, "output bit depth");
            _ = FfmpegOptionTokenizer.Parse(recipe.ExtraFfmpegOptions);
            if (recipe.InputChannels is { } inputChannels && !Enum.IsDefined(inputChannels))
                throw new InvalidDataException(
                    $"Ingest recipe '{recipe.Id}' has an invalid input channel selection.");
            if (recipe.OutputChannels is { } outputChannels && !Enum.IsDefined(outputChannels))
                throw new InvalidDataException(
                    $"Ingest recipe '{recipe.Id}' has an invalid output channel selection.");
            if (!Enum.IsDefined(recipe.AlbumCondition) || !Enum.IsDefined(recipe.SourceSelection))
                throw new InvalidDataException(
                    $"Ingest recipe '{recipe.Id}' has an invalid source-selection policy.");
            string? outputExtension = NormalizeOptionalExtension(recipe.OutputExtension);
            switch (recipe.Action)
            {
                case LibraryIngestAction.Copy when outputExtension is not null &&
                    inputExtensions.Any(extension => !string.Equals(
                        extension, outputExtension, StringComparison.OrdinalIgnoreCase)):
                    throw new InvalidDataException(
                        $"Copy recipe '{recipe.Id}' cannot change a file extension. " +
                        "Use remux or transcode instead.");
                case LibraryIngestAction.Remux:
                    if (outputExtension is null)
                        throw new InvalidDataException(
                            $"Remux recipe '{recipe.Id}' must specify an output extension.");
                    if (inputExtensions.Any(extension => !MediaFormatRegistry.Default
                            .SupportsExtension(extension, MediaFormatCapabilities.Remux)) ||
                        !MediaFormatRegistry.Default.SupportsExtension(
                            outputExtension, MediaFormatCapabilities.Remux))
                        throw new InvalidDataException(
                            $"Remux recipe '{recipe.Id}' requires a registered remux adapter.");
                    MediaFormatRegistry.Default.TryGetByExtension(
                        outputExtension, out MediaFormatDefinition outputFormat);
                    if (inputExtensions.Any(extension =>
                        !MediaFormatRegistry.Default.TryGetByExtension(
                            extension, out MediaFormatDefinition inputFormat) ||
                        inputFormat.Family != outputFormat.Family))
                        throw new InvalidDataException(
                            $"Remux recipe '{recipe.Id}' cannot change between unrelated " +
                            "container families.");
                    break;
                case LibraryIngestAction.Transcode:
                    if (outputExtension is null)
                        throw new InvalidDataException(
                            $"Transcode recipe '{recipe.Id}' must specify an output extension.");
                    foreach (string extension in inputExtensions)
                        if (!MediaFormatRegistry.Default.SupportsExtension(
                                extension, MediaFormatCapabilities.TranscodeSource))
                            throw new InvalidDataException(
                                $"Transcode recipe '{recipe.Id}' cannot decode '{extension}'.");
                    if (!MediaFormatRegistry.Default.SupportsExtension(
                            outputExtension, MediaFormatCapabilities.TranscodeDestination))
                        throw new InvalidDataException(
                            $"Transcode recipe '{recipe.Id}' cannot encode '{outputExtension}'.");
                    string codec = (recipe.Codec ?? outputExtension.TrimStart('.'))
                        .Trim().ToLowerInvariant();
                    bool compatible = outputExtension switch
                    {
                        ".flac" => codec == "flac",
                        ".m4a" => codec is "aac" or "m4a" or "alac",
                        ".wv" => codec is "wv" or "wavpack",
                        _ => false,
                    };
                    if (!compatible)
                        throw new InvalidDataException(
                            $"Transcode recipe '{recipe.Id}' codec '{codec}' is not compatible " +
                            $"with container '{outputExtension}'.");
                    if (outputExtension == ".wv")
                    {
                        if (inputExtensions.Any(extension => extension != ".dsf"))
                            throw new InvalidDataException(
                                $"WavPack DSD recipe '{recipe.Id}' accepts only .dsf inputs.");
                        if (recipe.BitrateKbps is not null ||
                            !string.IsNullOrWhiteSpace(recipe.Encoder) ||
                            !string.IsNullOrWhiteSpace(recipe.ExtraFfmpegOptions))
                            throw new InvalidDataException(
                                $"WavPack DSD recipe '{recipe.Id}' cannot specify a bitrate, " +
                                "encoder, or extra FFmpeg options.");
                    }
                    break;
            }

            string[] outputExtensions = outputExtension is null
                ? inputExtensions
                : [outputExtension];
            if (recipe.PreserveMetadata &&
                (inputExtensions.Any(extension => !MediaFormatRegistry.Default
                     .SupportsExtension(extension, MediaFormatCapabilities.ReadMetadata)) ||
                 outputExtensions.Any(extension => !MediaFormatRegistry.Default
                     .SupportsExtension(extension, MediaFormatCapabilities.WriteMetadata))))
                throw new InvalidDataException(
                    $"Ingest recipe '{recipe.Id}' requests metadata preservation, but one or " +
                    "more source/destination formats cannot transfer metadata.");
            if (recipe.PreserveArtwork &&
                (inputExtensions.Any(extension => !MediaFormatRegistry.Default
                     .SupportsExtension(extension, MediaFormatCapabilities.ReadArtwork)) ||
                 (artworkPolicy.Storage is LibraryArtworkStorage.Embedded or
                     LibraryArtworkStorage.Both) &&
                 outputExtensions.Any(extension => !MediaFormatRegistry.Default
                     .SupportsExtension(extension, MediaFormatCapabilities.WriteArtwork))))
                throw new InvalidDataException(
                    $"Ingest recipe '{recipe.Id}' requests artwork preservation, but one or " +
                    "more source/destination formats cannot transfer artwork.");
        }

        private static void ValidateOptionalPositive(int? value, string recipeId, string field)
        {
            if (value is <= 0)
                throw new InvalidDataException(
                    $"Ingest recipe '{recipeId}' {field} must be positive when specified.");
        }

        private static string NormalizeExtension(string value)
        {
            string extension = value.Trim().ToLowerInvariant();
            if (!extension.StartsWith(".", StringComparison.Ordinal))
                extension = "." + extension;
            if (extension.Length < 2 || extension.IndexOfAny(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
                throw new InvalidDataException($"Invalid media extension '{value}'.");
            return extension;
        }

        private static string? NormalizeOptionalExtension(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : NormalizeExtension(value);

        private static void SetOptional(XElement element, string name, object? value)
        {
            if (value is not null)
                element.SetAttributeValue(name, value);
        }

        private static void SetChannelSelection(
            XElement element,
            string name,
            LibraryChannelSelection? value)
        {
            if (value is not null)
                element.SetAttributeValue(name,
                    value == LibraryChannelSelection.Stereo ? "Stereo" : "Multi");
        }

        private static string Required(XElement element, string attributeName)
        {
            string? result = Optional(element, attributeName);
            return result ?? throw new InvalidDataException(
                $"<{element.Name.LocalName}> must have a non-empty {attributeName} attribute.");
        }

        private static string? Optional(XElement? element, string attributeName)
        {
            string? value = ((string?)element?.Attribute(attributeName))?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static bool ParseBoolean(XElement? element, string attributeName, bool fallback)
        {
            string? value = Optional(element, attributeName);
            if (value is null)
                return fallback;
            if (bool.TryParse(value, out bool parsed))
                return parsed;
            throw new InvalidDataException(
                $"Attribute '{attributeName}' on <{element!.Name.LocalName}> must be true or false.");
        }

        private static bool? ParseOptionalBoolean(XElement? element, string attributeName)
        {
            string? value = Optional(element, attributeName);
            if (value is null)
                return null;
            if (bool.TryParse(value, out bool parsed))
                return parsed;
            throw new InvalidDataException(
                $"Attribute '{attributeName}' on <{element!.Name.LocalName}> must be true or false.");
        }

        private static int ParsePositiveInteger(
            XElement? element,
            string attributeName,
            int fallback)
        {
            string? value = Optional(element, attributeName);
            if (value is null)
                return fallback;
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
                    out int parsed) && parsed > 0)
                return parsed;
            throw new InvalidDataException(
                $"Attribute '{attributeName}' on <{element!.Name.LocalName}> must be a positive integer.");
        }

        private static int ParseNonNegativeInteger(
            XElement? element,
            string attributeName,
            int fallback)
        {
            string? value = Optional(element, attributeName);
            if (value is null)
                return fallback;
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
                    out int parsed) && parsed >= 0)
                return parsed;
            throw new InvalidDataException(
                $"Attribute '{attributeName}' on <{element!.Name.LocalName}> must be a " +
                "non-negative integer.");
        }

        private static int? ParseOptionalPositiveInteger(XElement? element, string attributeName)
        {
            string? value = Optional(element, attributeName);
            if (value is null)
                return null;
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
                    out int parsed) && parsed > 0)
                return parsed;
            throw new InvalidDataException(
                $"Attribute '{attributeName}' on <{element!.Name.LocalName}> must be a positive integer.");
        }

        private static LibraryChannelSelection? ParseOptionalChannelSelection(
            XElement? element,
            string attributeName)
        {
            string? value = Optional(element, attributeName);
            if (value is null)
                return null;
            if (value.Equals("2", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Stereo", StringComparison.OrdinalIgnoreCase))
                return LibraryChannelSelection.Stereo;
            if (value.Equals("Multi", StringComparison.OrdinalIgnoreCase))
                return LibraryChannelSelection.Multi;
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
                    out int legacyChannels) && legacyChannels > 2)
                return LibraryChannelSelection.Multi;
            throw new InvalidDataException(
                $"Attribute '{attributeName}' on <{element!.Name.LocalName}> must be Stereo or Multi.");
        }

        private static Guid? ParseOptionalGuid(XElement? element, string attributeName)
        {
            string? value = Optional(element, attributeName);
            if (value is null)
                return null;
            if (Guid.TryParse(value, out Guid parsed) && parsed != Guid.Empty)
                return parsed;
            throw new InvalidDataException(
                $"Attribute '{attributeName}' on <{element!.Name.LocalName}> must be a non-empty GUID.");
        }

        private static T ParseEnum<T>(XElement? element, string attributeName, T fallback)
            where T : struct, Enum
        {
            string? value = Optional(element, attributeName);
            if (value is null)
                return fallback;
            if (Enum.TryParse(value, true, out T parsed) && Enum.IsDefined(parsed))
                return parsed;
            throw new InvalidDataException(
                $"Invalid {attributeName} '{value}' on <{element!.Name.LocalName}>.");
        }

        private static T? ParseOptionalEnum<T>(XElement? element, string attributeName)
            where T : struct, Enum
        {
            string? value = Optional(element, attributeName);
            if (value is null)
                return null;
            if (Enum.TryParse(value, true, out T parsed) && Enum.IsDefined(parsed))
                return parsed;
            throw new InvalidDataException(
                $"Invalid {attributeName} '{value}' on <{element!.Name.LocalName}>.");
        }

        private static LibraryRootPermissions ParseFlags(
            XElement element,
            string attributeName,
            LibraryRootPermissions fallback) =>
            ParsePermissions((string?)element.Attribute(attributeName), fallback);
    }

    public sealed record LibraryRootPolicySnapshot(
        Guid RootId,
        string ProfileId,
        LibraryRootPermissions Permissions);

    /// <summary>
    /// Immutable effective policy captured when a preview is made. The fingerprint lets Apply
    /// reject a preview made against different configuration settings.
    /// </summary>
    public sealed record LibraryPolicySnapshot(
        Guid LibraryId,
        string ActiveProfileId,
        LibraryProfile ActiveProfile,
        IReadOnlyDictionary<Guid, LibraryRootPolicySnapshot> Roots,
        string Fingerprint)
    {
        public static LibraryPolicySnapshot Create(LibraryConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            LibraryIndexLocation[] locations = configuration.IndexLocations.ToArray();
            var rootIds = locations.Select(location => location.RootId).ToHashSet();
            var profileIds = configuration.Profiles.Select(profile => profile.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var recipeIds = configuration.Profiles.SelectMany(profile => profile.Ingest.Recipes)
                .Select(recipe => recipe.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (LibraryProfile profile in configuration.Profiles)
            {
                foreach (LibraryIngestRecipe recipe in profile.Ingest.Recipes)
                {
                    if (recipe.DestinationRootId is { } destinationRootId &&
                        !rootIds.Contains(destinationRootId))
                        throw new InvalidDataException(
                            $"Ingest recipe '{recipe.Id}' in profile '{profile.Id}' references " +
                            $"unknown destination root '{destinationRootId:D}'.");
                    if (recipe.NamingProfileId is { } namingProfileId &&
                        !profileIds.Contains(namingProfileId))
                        throw new InvalidDataException(
                            $"Ingest recipe '{recipe.Id}' in profile '{profile.Id}' references " +
                            $"unknown naming profile '{namingProfileId}'.");
                }
            }
            foreach (LibraryExportProfile exportProfile in configuration.ExportProfiles)
            {
                if (exportProfile.Naming.LibraryProfileId is { } namingProfileId &&
                    !profileIds.Contains(namingProfileId))
                    throw new InvalidDataException(
                        $"Export profile '{exportProfile.Id}' references unknown library profile " +
                        $"'{namingProfileId}'.");
                if (exportProfile.Transform.RecipeId is { } recipeId &&
                    !recipeIds.Contains(recipeId))
                    throw new InvalidDataException(
                        $"Export profile '{exportProfile.Id}' references unknown transform recipe " +
                        $"'{recipeId}'.");
            }
            var roots = new ReadOnlyDictionary<Guid, LibraryRootPolicySnapshot>(
                locations.ToDictionary(
                location => location.RootId,
                location => new LibraryRootPolicySnapshot(
                    location.RootId, location.ProfileId, location.Permissions)));
            string fingerprint = ComputeFingerprint(configuration, locations);
            return new(configuration.LibraryId, configuration.ActiveProfileId,
                configuration.ActiveProfile, roots, fingerprint);
        }

        private static string ComputeFingerprint(
            LibraryConfiguration configuration,
            IEnumerable<LibraryIndexLocation> locations)
        {
            var value = new StringBuilder()
                .Append(configuration.SchemaVersion).Append('|')
                .Append(configuration.LibraryId.ToString("D")).Append('|')
                .Append(configuration.ActiveProfileId).AppendLine();
            LibraryIngestSettings ingestSettings = configuration.IngestSettings;
            LibraryArtworkHealthSettings artworkHealth = configuration.ArtworkHealthSettings;
            value.Append("path-limits|")
                .Append(OptionalPositiveInteger(configuration, "LengthLimit", 255)).Append('|')
                .Append(OptionalPositiveInteger(
                    configuration, "DiscNumLengthLimit", 255)).AppendLine()
                .Append("machine-bindings|")
                .Append(configuration.DatabaseFile).Append('|')
                .Append(configuration.ItunesLibraryPath).Append('|')
                .Append(configuration.FfmpegPath).Append('|')
                .Append(configuration.WavpackPath).AppendLine()
                .Append("ingest-settings|")
                .Append(ingestSettings.AacEncoder).Append('|')
                .Append(ingestSettings.AacBitrateKbps).Append('|')
                .Append(ingestSettings.DeleteSourcesAfterIngest).Append('|')
                .Append(ingestSettings.RemoveNonMusicAfterIngest).AppendLine()
                .Append("artwork-health|")
                .Append(artworkHealth.OversizedByteThreshold).Append('|')
                .Append(artworkHealth.OversizedDimensionThreshold).Append('|')
                .Append(artworkHealth.RepairTargetByteSize).Append('|')
                .Append(artworkHealth.RepairTargetDimension).AppendLine()
                .Append("cross-sync|")
                .Append(configuration.DeleteStaleCrossSyncFiles).Append('|')
                .Append(configuration.CleanCrossSyncPlaylists).AppendLine()
                .Append("legacy-file-policy|")
                .Append(configuration["DeleteNonMusic"].Length != 0).Append('|')
                .Append(configuration["KeepFolderImages"].Length != 0).AppendLine();
            foreach (string playlist in configuration.SyncPlaylists)
                value.Append("sync-playlist|").Append(playlist).AppendLine();
            foreach (string target in configuration["SyncTarget"])
                value.Append("legacy-sync-target|").Append(target).AppendLine();
            foreach (LibraryProfile profile in configuration.Profiles.OrderBy(
                         item => item.Id, StringComparer.OrdinalIgnoreCase))
            {
                value.Append(profile.Id).Append('|').Append(profile.Name).Append('|')
                    .Append(profile.Preset).Append('|').Append((int)profile.DefaultRootPermissions)
                    .Append('|').Append(profile.Naming.DirectoryTemplate).Append('|')
                    .Append(profile.Naming.FileNameTemplate).Append('|')
                    .Append(profile.Naming.TrackPadding).Append('|')
                    .Append(profile.Naming.DiscPadding).Append('|')
                    .Append(profile.Naming.CollisionPolicy).Append('|')
                    .Append(profile.Naming.PreserveUnicode).Append('|')
                    .Append(profile.Naming.InvalidCharacterReplacement).Append('|')
                    .Append(profile.Naming.UseItunesCanonicalNaming).Append('|')
                    .Append(profile.Naming.LegacySanitization).Append('|')
                    .Append(profile.Naming.StripFormatSuffixes).Append('|')
                    .Append(profile.Naming.MissingArtistFallback).Append('|')
                    .Append(profile.Naming.MissingAlbumFallback).Append('|')
                    .Append(profile.Naming.MissingTitleFallback).Append('|')
                    .Append(profile.Naming.CompilationValue).Append('|')
                    .Append(profile.Naming.UnicodeNormalization).Append('|')
                    .Append(profile.Naming.ComponentLengthLimit).Append('|')
                    .Append(profile.Naming.CompletePathLengthLimit).Append('|')
                    .Append(profile.Disc.Strategy).Append('|')
                    .Append(profile.Disc.TrackTotalScope).Append('|')
                    .Append(profile.Disc.InferAlbumSuffix).Append('|')
                    .Append(profile.Disc.PreserveDiscTags).Append('|')
                    .Append(profile.AlbumIdentity.UseAlbumArtist).Append('|')
                    .Append(profile.AlbumIdentity.StripFormatSuffixes).Append('|')
                    .Append(profile.AlbumIdentity.StripDiscSuffixes).Append('|')
                    .Append(profile.AlbumIdentity.IncludeReleaseYear).Append('|')
                    .Append(profile.Metadata.PreserveReplayGain).Append('|')
                    .Append(profile.Metadata.PreserveMusicBrainzIdentifiers).Append('|')
                    .Append(profile.Metadata.PreserveCustomFields).Append('|')
                    .Append(profile.Metadata.PreserveCompilationSemantics).Append('|')
                    .Append(profile.Quality.HighResolutionMinimumSampleRateHz).Append('|')
                    .Append(profile.Quality.HighResolutionMinimumBitsPerSample).Append('|')
                    .Append(profile.Ingest.Enabled).Append('|')
                    .Append(profile.Ingest.SourceDisposition).Append('|')
                    .Append(profile.Ingest.PreserveSidecars).Append('|')
                    .Append(profile.Artwork.Storage).Append('|')
                    .Append(profile.Artwork.Roles).Append('|')
                    .Append(profile.Artwork.Encoding).Append('|')
                    .Append(profile.Artwork.MaximumDimension).Append('|')
                    .Append(profile.Artwork.MaximumEncodedBytes).Append('|')
                    .Append(profile.Artwork.JpegQuality).Append('|')
                    .Append(profile.Artwork.SidecarFileNameTemplate).Append('|')
                    .Append(profile.Sidecars.UnknownFileDisposition).AppendLine();
                foreach (LibraryHealthRulePolicy rule in profile.Health.Rules.OrderBy(
                             item => item.Id, StringComparer.OrdinalIgnoreCase))
                    value.Append(rule.Id).Append('|').Append(rule.Enabled).Append('|')
                        .Append(rule.Severity).Append('|').Append(rule.ProposeRepair).Append('|')
                        .Append(rule.ApplyRepair).AppendLine();
                foreach (LibraryIngestRecipe recipe in profile.Ingest.Recipes)
                {
                    value.Append(recipe.Id).Append('|').Append(recipe.Name).Append('|')
                        .Append(recipe.Enabled).Append('|')
                        .Append(string.Join(',', recipe.InputExtensions)).Append('|')
                        .Append(recipe.RequireLossless).Append('|')
                        .Append(recipe.MinimumSampleRateHz).Append('|')
                        .Append(recipe.MinimumBitsPerSample).Append('|')
                        .Append(recipe.InputChannels).Append('|')
                        .Append(recipe.MatchAnyQualityMinimum).Append('|')
                        .Append(recipe.AlbumCondition).Append('|')
                        .Append(recipe.SourceSelection).Append('|')
                        .Append(recipe.RequireFallbackApproval).Append('|')
                        .Append(recipe.Action).Append('|').Append(recipe.DestinationRootId).Append('|')
                        .Append(recipe.DestinationLegacyRole).Append('|')
                        .Append(recipe.OutputExtension).Append('|').Append(recipe.Codec).Append('|')
                        .Append(recipe.Encoder).Append('|').Append(recipe.ExtraFfmpegOptions)
                        .Append('|').Append(recipe.AddToMediaCatalog).Append('|')
                        .Append(recipe.BitrateKbps).Append('|')
                        .Append(recipe.SampleRateHz).Append('|').Append(recipe.BitsPerSample).Append('|')
                        .Append(recipe.OutputChannels).Append('|').Append(recipe.NamingProfileId)
                        .Append('|').Append(recipe.PreserveMetadata).Append('|')
                        .Append(recipe.PreserveArtwork).Append('|').Append(recipe.CollisionPolicy)
                        .Append('|').Append(recipe.OutputRepresentationRole)
                        .AppendLine();
                }
                foreach (LibrarySidecarRule rule in profile.Sidecars.Rules)
                    value.Append(rule.Id).Append('|').Append(rule.Name).Append('|')
                        .Append(rule.Enabled).Append('|')
                        .Append(string.Join(',', rule.Patterns)).Append('|')
                        .Append(rule.Disposition).AppendLine();
            }
            foreach (LibraryExportProfile exportProfile in configuration.ExportProfiles.OrderBy(
                         item => item.Id, StringComparer.OrdinalIgnoreCase))
                value.Append("export|").Append(exportProfile.Id).Append('|')
                    .Append(exportProfile.Fingerprint).AppendLine();
            foreach (LibraryIndexLocation location in locations.OrderBy(item => item.RootId))
                value.Append(location.RootId.ToString("D")).Append('|')
                    .Append(location.Target).Append('|')
                    .Append(location.ProfileId).Append('|')
                    .Append((int)location.Permissions).Append('|')
                    .Append(location.Organize).Append('|')
                    .Append(location.UseItunesCanonicalNaming).Append('|')
                    .Append(location.IngestRole).Append('|')
                    .Append(location.RepresentationRole).Append('|')
                    .Append(location.IsSyncTarget).Append('|')
                    .Append(location.Filter).Append('|')
                    .Append(string.Join(',', location.IndexFormats)).Append('|')
                    .Append(string.Join(';', location.IndexIncludePatterns)).Append('|')
                    .Append(string.Join(';', location.IndexExcludePatterns)).Append('|')
                    .Append(location.DefaultOffset).Append('|')
                    .Append(string.Join(';', location.Memberships
                        .OrderBy(item => item.Name, LibraryConfiguration.ScanSetComparer)
                    .Select(item => item.Name + "=" + item.Offset)))
                    .AppendLine();
            foreach (LibraryPlaylistSource source in configuration.PlaylistSources
                         .OrderBy(item => item.Type, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item => item.Location, StringComparer.OrdinalIgnoreCase))
                value.Append("playlist-source|").Append(source.Type).Append('|')
                    .Append(source.Location).Append('|').Append(source.Recursive).AppendLine();
            foreach (LibraryPlaylistTarget target in configuration.PlaylistTargets
                         .OrderBy(item => item.Target, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item => item.Type, StringComparer.OrdinalIgnoreCase))
                value.Append("playlist-target|").Append(target.Target).Append('|')
                    .Append(target.Type).Append('|')
                    .Append(string.Join(',', target.Sets.OrderBy(
                        item => item, LibraryConfiguration.ScanSetComparer))).Append('|')
                    .Append(target.PathStyle).Append('|').Append(target.Encoding).Append('|')
                    .Append(target.EmitByteOrderMark).Append('|').Append(target.LineEnding)
                    .Append('|').Append(target.IncludeExtendedInfo).Append('|')
                    .Append(target.FileNameTransform).Append('|').Append(target.MaxTrackCount)
                    .Append('|').Append(target.CollisionPolicy).AppendLine();
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())))
                .ToLowerInvariant();
        }

        private static int OptionalPositiveInteger(
            LibraryConfiguration configuration,
            string elementName,
            int fallback)
        {
            string? value = configuration[elementName].FirstOrDefault();
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                       out int parsed) && parsed > 0
                ? parsed
                : fallback;
        }
    }
}
