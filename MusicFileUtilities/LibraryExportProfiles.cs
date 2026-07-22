#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using MusicLibrary.Core.Services;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services
{

public enum ExportSelectionKind
{
    EntireLibrary,
    Playlists,
    SavedView,
    ExplicitTracks,
}

public sealed record ExportSelectionPolicy(
    ExportSelectionKind Kind,
    ImmutableArray<string> Values,
    string? Query = null)
{
    public static ExportSelectionPolicy EntireLibrary { get; } =
        new(ExportSelectionKind.EntireLibrary, []);

    public static ExportSelectionPolicy FromPlaylists(IEnumerable<string> playlists) =>
        new(ExportSelectionKind.Playlists, Normalize(playlists));

    internal static ImmutableArray<string> Normalize(IEnumerable<string> values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
        .ToImmutableArray();
}

public enum ExportTransformMode
{
    Preserve,
    Copy,
    Remux,
    Transcode,
    SpecializedProvider,
}

public sealed record ExportTransformPolicy(
    ExportTransformMode Mode = ExportTransformMode.Preserve,
    string? RecipeId = null,
    string? ProviderId = null,
    string? Codec = null,
    string? Container = null);

/// <summary>
/// Selects either an existing library naming profile or a self-contained layout. Export services
/// resolve this policy through the same path-layout resolver used by organization and ingest.
/// </summary>
public sealed record ExportNamingPolicy(
    string? LibraryProfileId = null,
    bool PreserveSourceLayout = false,
    string? FolderTemplate = null,
    string? FileNameTemplate = null,
    LibraryPathCollisionPolicy? CollisionPolicy = null);

public enum ExportArtworkMode
{
    None,
    Embedded,
    Sidecar,
    EmbeddedAndSidecar,
}

public sealed record ExportArtworkPolicy(
    ExportArtworkMode Mode = ExportArtworkMode.Embedded,
    bool FrontCoverOnly = true,
    bool PreserveEncoding = true,
    int? MaximumDimension = null,
    int? MaximumBytes = null);

public sealed record ExportPlaylistPolicy(
    bool Enabled = false,
    string Format = "m3u8",
    bool RelativePaths = true,
    bool IncludeExtendedInfo = true,
    string EncodingName = "utf-8",
    bool WriteByteOrderMark = false,
    string LineEnding = "platform",
    int? MaximumTracks = null);

public enum ExportExtraFileDisposition
{
    Preserve,
    Quarantine,
    Delete,
}

public sealed record ExportReconciliationPolicy(
    ExportExtraFileDisposition ExtraFiles = ExportExtraFileDisposition.Preserve,
    bool ReplaceChangedFiles = true,
    bool RemoveEmptyDirectories = false,
    int? MaximumRemovals = null);

public sealed record ExportTransportConfiguration(
    string ProviderId,
    string Destination,
    ImmutableDictionary<string, string> Options)
{
    public ExportTransportConfiguration(string providerId, string destination)
        : this(providerId, destination,
            ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase))
    {
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ProviderId) &&
        !string.IsNullOrWhiteSpace(Destination);
}

/// <summary>
/// One immutable description of an export. It deliberately contains portable policy only;
/// machine-specific credentials and executable paths belong in transport bindings or options.
/// </summary>
public sealed record LibraryExportProfile(
    string Id,
    string Name,
    bool Enabled,
    ExportSelectionPolicy Selection,
    ExportTransformPolicy Transform,
    ExportNamingPolicy Naming,
    ExportArtworkPolicy Artwork,
    ExportPlaylistPolicy Playlists,
    ExportTransportConfiguration Transport,
    ExportReconciliationPolicy Reconciliation)
{
    public bool IsVisible => Enabled && Transport.IsConfigured;

    public string Fingerprint => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(CanonicalText()))).ToLowerInvariant();

    public static LibraryExportProfile LegacyCrossLibrary(
        string destination,
        IEnumerable<string> playlists,
        string? namingProfileId,
        bool deleteExtras) => new(
            "legacy-cross-library",
            "Cross-library synchronization",
            true,
            ExportSelectionPolicy.FromPlaylists(playlists),
            new(ExportTransformMode.Copy),
            new(namingProfileId),
            new(ExportArtworkMode.Embedded),
            new(),
            new("local-filesystem", destination),
            new(deleteExtras
                ? ExportExtraFileDisposition.Delete
                : ExportExtraFileDisposition.Quarantine));

    private string CanonicalText()
    {
        static string Text(string? value) => value?.Trim() ?? "";
        static string Bool(bool value) => value ? "1" : "0";
        var result = new StringBuilder();
        void Add(string name, object? value) => result.Append(name).Append('=')
            .Append(value is IFormattable formattable
                ? formattable.ToString(null, CultureInfo.InvariantCulture)
                : value?.ToString() ?? "")
            .Append('\n');

        Add("id", Text(Id));
        Add("name", Text(Name));
        Add("enabled", Bool(Enabled));
        Add("selection.kind", Selection.Kind);
        foreach (string value in Selection.Values.IsDefault ? [] : Selection.Values)
            Add("selection.value", Text(value));
        Add("selection.query", Text(Selection.Query));
        Add("transform.mode", Transform.Mode);
        Add("transform.recipe", Text(Transform.RecipeId));
        Add("transform.provider", Text(Transform.ProviderId));
        Add("transform.codec", Text(Transform.Codec));
        Add("transform.container", Text(Transform.Container));
        Add("naming.profile", Text(Naming.LibraryProfileId));
        Add("naming.preserve", Bool(Naming.PreserveSourceLayout));
        Add("naming.folder", Text(Naming.FolderTemplate));
        Add("naming.file", Text(Naming.FileNameTemplate));
        Add("naming.collision", Naming.CollisionPolicy);
        Add("artwork.mode", Artwork.Mode);
        Add("artwork.frontOnly", Bool(Artwork.FrontCoverOnly));
        Add("artwork.preserveEncoding", Bool(Artwork.PreserveEncoding));
        Add("artwork.maxDimension", Artwork.MaximumDimension);
        Add("artwork.maxBytes", Artwork.MaximumBytes);
        Add("playlists.enabled", Bool(Playlists.Enabled));
        Add("playlists.format", Text(Playlists.Format));
        Add("playlists.relative", Bool(Playlists.RelativePaths));
        Add("playlists.extended", Bool(Playlists.IncludeExtendedInfo));
        Add("playlists.encoding", Text(Playlists.EncodingName));
        Add("playlists.bom", Bool(Playlists.WriteByteOrderMark));
        Add("playlists.eol", Text(Playlists.LineEnding));
        Add("playlists.maximum", Playlists.MaximumTracks);
        Add("transport.provider", Text(Transport.ProviderId));
        Add("transport.destination", Text(Transport.Destination));
        foreach ((string key, string value) in Transport.Options
                     .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            Add("transport.option." + key.ToUpperInvariant(), Text(value));
        Add("reconcile.extras", Reconciliation.ExtraFiles);
        Add("reconcile.replace", Bool(Reconciliation.ReplaceChangedFiles));
        Add("reconcile.removeEmpty", Bool(Reconciliation.RemoveEmptyDirectories));
        Add("reconcile.maximum", Reconciliation.MaximumRemovals);
        return result.ToString();
    }
}

}

namespace MusicLibraryTools
{

/// <summary>Schema-v2 XML reader, writer, and validator for portable export profiles.</summary>
public static class LibraryExportProfileXml
{
    public static LibraryExportProfile Parse(
        XElement element,
        bool allowUnboundTransportDestination = false)
    {
        ArgumentNullException.ThrowIfNull(element);
        string id = Required(element, "Id");
        string name = Required(element, "Name");
        var selectionElement = element.Element("Selection");
        var transformElement = element.Element("Transform");
        var namingElement = element.Element("Naming");
        var artworkElement = element.Element("Artwork");
        var playlistsElement = element.Element("Playlists");
        var transportElement = element.Element("Transport");
        var reconciliationElement = element.Element("Reconciliation");

        var options = ImmutableDictionary.CreateBuilder<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (XElement option in transportElement?.Elements("Option") ?? [])
        {
            string optionName = Required(option, "Name");
            if (options.ContainsKey(optionName))
                throw new InvalidDataException(
                    $"Export profile '{id}' contains duplicate transport option '{optionName}'.");
            options.Add(optionName, Required(option, "Value", allowEmpty: true));
        }

        var profile = new LibraryExportProfile(
            id,
            name,
            Boolean(element, "Enabled", false),
            new(
                EnumValue(selectionElement, "Kind", ExportSelectionKind.EntireLibrary),
                ExportSelectionPolicy.Normalize(
                    selectionElement?.Elements("Value").Select(value => value.Value) ?? []),
                Optional(selectionElement, "Query")),
            new(
                EnumValue(transformElement, "Mode", ExportTransformMode.Preserve),
                Optional(transformElement, "RecipeId"),
                Optional(transformElement, "ProviderId"),
                Optional(transformElement, "Codec"),
                Optional(transformElement, "Container")),
            new(
                Optional(namingElement, "LibraryProfileId"),
                Boolean(namingElement, "PreserveSourceLayout", false),
                Optional(namingElement, "FolderTemplate"),
                Optional(namingElement, "FileNameTemplate"),
                OptionalEnum<LibraryPathCollisionPolicy>(namingElement, "CollisionPolicy")),
            new(
                EnumValue(artworkElement, "Mode", ExportArtworkMode.Embedded),
                Boolean(artworkElement, "FrontCoverOnly", true),
                Boolean(artworkElement, "PreserveEncoding", true),
                OptionalInteger(artworkElement, "MaximumDimension"),
                OptionalInteger(artworkElement, "MaximumBytes")),
            new(
                Boolean(playlistsElement, "Enabled", false),
                Optional(playlistsElement, "Format") ?? "m3u8",
                Boolean(playlistsElement, "RelativePaths", true),
                Boolean(playlistsElement, "IncludeExtendedInfo", true),
                Optional(playlistsElement, "EncodingName") ?? "utf-8",
                Boolean(playlistsElement, "WriteByteOrderMark", false),
                Optional(playlistsElement, "LineEnding") ?? "platform",
                OptionalInteger(playlistsElement, "MaximumTracks")),
            new(
                Optional(transportElement, "ProviderId") ?? "",
                Optional(transportElement, "Destination") ?? "",
                options.ToImmutable()),
            new(
                EnumValue(reconciliationElement, "ExtraFiles",
                    ExportExtraFileDisposition.Preserve),
                Boolean(reconciliationElement, "ReplaceChangedFiles", true),
                Boolean(reconciliationElement, "RemoveEmptyDirectories", false),
                OptionalInteger(reconciliationElement, "MaximumRemovals")));
        Validate(profile, allowUnboundTransportDestination);
        return profile;
    }

    public static XElement Write(LibraryExportProfile profile)
    {
        Validate(profile);
        var selection = new XElement("Selection",
            new XAttribute("Kind", profile.Selection.Kind));
        SetOptional(selection, "Query", profile.Selection.Query);
        foreach (string value in ExportSelectionPolicy.Normalize(
                     profile.Selection.Values.IsDefault ? [] : profile.Selection.Values))
            selection.Add(new XElement("Value", value));

        var transform = new XElement("Transform", new XAttribute("Mode", profile.Transform.Mode));
        SetOptional(transform, "RecipeId", profile.Transform.RecipeId);
        SetOptional(transform, "ProviderId", profile.Transform.ProviderId);
        SetOptional(transform, "Codec", profile.Transform.Codec);
        SetOptional(transform, "Container", profile.Transform.Container);

        var naming = new XElement("Naming",
            new XAttribute("PreserveSourceLayout", profile.Naming.PreserveSourceLayout));
        SetOptional(naming, "LibraryProfileId", profile.Naming.LibraryProfileId);
        SetOptional(naming, "FolderTemplate", profile.Naming.FolderTemplate);
        SetOptional(naming, "FileNameTemplate", profile.Naming.FileNameTemplate);
        if (profile.Naming.CollisionPolicy is { } collision)
            naming.SetAttributeValue("CollisionPolicy", collision);

        var artwork = new XElement("Artwork",
            new XAttribute("Mode", profile.Artwork.Mode),
            new XAttribute("FrontCoverOnly", profile.Artwork.FrontCoverOnly),
            new XAttribute("PreserveEncoding", profile.Artwork.PreserveEncoding));
        SetOptionalInteger(artwork, "MaximumDimension", profile.Artwork.MaximumDimension);
        SetOptionalInteger(artwork, "MaximumBytes", profile.Artwork.MaximumBytes);

        var playlists = new XElement("Playlists",
            new XAttribute("Enabled", profile.Playlists.Enabled),
            new XAttribute("Format", profile.Playlists.Format.Trim().ToLowerInvariant()),
            new XAttribute("RelativePaths", profile.Playlists.RelativePaths),
            new XAttribute("IncludeExtendedInfo", profile.Playlists.IncludeExtendedInfo),
            new XAttribute("EncodingName", profile.Playlists.EncodingName.Trim()),
            new XAttribute("WriteByteOrderMark", profile.Playlists.WriteByteOrderMark),
            new XAttribute("LineEnding", profile.Playlists.LineEnding.Trim().ToLowerInvariant()));
        SetOptionalInteger(playlists, "MaximumTracks", profile.Playlists.MaximumTracks);

        var transport = new XElement("Transport",
            new XAttribute("ProviderId", profile.Transport.ProviderId.Trim()),
            new XAttribute("Destination", profile.Transport.Destination.Trim()));
        foreach ((string name, string value) in profile.Transport.Options
                     .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            transport.Add(new XElement("Option",
                new XAttribute("Name", name.Trim()),
                new XAttribute("Value", value)));

        var reconciliation = new XElement("Reconciliation",
            new XAttribute("ExtraFiles", profile.Reconciliation.ExtraFiles),
            new XAttribute("ReplaceChangedFiles", profile.Reconciliation.ReplaceChangedFiles),
            new XAttribute("RemoveEmptyDirectories",
                profile.Reconciliation.RemoveEmptyDirectories));
        SetOptionalInteger(reconciliation, "MaximumRemovals",
            profile.Reconciliation.MaximumRemovals);

        return new XElement("ExportProfile",
            new XAttribute("Id", profile.Id.Trim()),
            new XAttribute("Name", profile.Name.Trim()),
            new XAttribute("Enabled", profile.Enabled),
            selection, transform, naming, artwork, playlists, transport, reconciliation);
    }

    public static void Validate(
        LibraryExportProfile profile,
        bool allowUnboundTransportDestination = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        LibraryProfileXml.ValidateId(profile.Id, "export profile");
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new InvalidDataException(
                $"Export profile '{profile.Id}' must have a name.");
        if (!Enum.IsDefined(profile.Selection.Kind) || !Enum.IsDefined(profile.Transform.Mode) ||
            !Enum.IsDefined(profile.Artwork.Mode) ||
            !Enum.IsDefined(profile.Reconciliation.ExtraFiles) ||
            profile.Naming.CollisionPolicy is { } collisionPolicy &&
            !Enum.IsDefined(collisionPolicy))
            throw new InvalidDataException(
                $"Export profile '{profile.Id}' contains an unsupported policy value.");
        if (profile.Selection.Values.IsDefault)
            throw new InvalidDataException(
                $"Export profile '{profile.Id}' selection values must be initialized.");
        if (profile.Selection.Values.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException(
                $"Export profile '{profile.Id}' contains an empty selection value.");
        if (profile.Selection.Kind is ExportSelectionKind.Playlists or
                ExportSelectionKind.ExplicitTracks && profile.Selection.Values.Length == 0)
            throw new InvalidDataException(
                $"Export profile '{profile.Id}' selection requires at least one value.");
        if (profile.Selection.Kind == ExportSelectionKind.SavedView &&
            string.IsNullOrWhiteSpace(profile.Selection.Query) &&
            profile.Selection.Values.Length == 0)
            throw new InvalidDataException(
                $"Export profile '{profile.Id}' saved-view selection requires a query or view ID.");
        if (profile.Transform.Mode == ExportTransformMode.SpecializedProvider &&
            string.IsNullOrWhiteSpace(profile.Transform.ProviderId))
            throw new InvalidDataException(
                $"Export profile '{profile.Id}' requires a transform provider.");
        if (profile.Transform.Mode is ExportTransformMode.Remux or ExportTransformMode.Transcode &&
            string.IsNullOrWhiteSpace(profile.Transform.RecipeId) &&
            string.IsNullOrWhiteSpace(profile.Transform.ProviderId) &&
            string.IsNullOrWhiteSpace(profile.Transform.Codec) &&
            string.IsNullOrWhiteSpace(profile.Transform.Container))
            throw new InvalidDataException(
                $"Export profile '{profile.Id}' transform must select a recipe, provider, codec, " +
                "or container.");
        if (!profile.Naming.PreserveSourceLayout &&
            string.IsNullOrWhiteSpace(profile.Naming.LibraryProfileId) &&
            (string.IsNullOrWhiteSpace(profile.Naming.FolderTemplate) ||
             string.IsNullOrWhiteSpace(profile.Naming.FileNameTemplate)))
            throw new InvalidDataException(
                $"Export profile '{profile.Id}' must preserve source layout, select a library " +
                "profile, or provide both naming templates.");
        LibraryProfileXml.ValidateNamingTemplateOverrides(profile.Id,
            profile.Naming.FolderTemplate, profile.Naming.FileNameTemplate);
        Positive(profile.Artwork.MaximumDimension, profile.Id, "artwork maximum dimension");
        Positive(profile.Artwork.MaximumBytes, profile.Id, "artwork maximum bytes");
        Positive(profile.Playlists.MaximumTracks, profile.Id, "playlist maximum tracks");
        NonNegative(profile.Reconciliation.MaximumRemovals, profile.Id,
            "maximum removals");
        if (profile.Playlists.Enabled)
        {
            string format = profile.Playlists.Format.Trim().ToLowerInvariant();
            if (format is not ("m3u" or "m3u8" or "wpl"))
                throw new InvalidDataException(
                    $"Export profile '{profile.Id}' playlist format must be m3u, m3u8, or wpl.");
            if (string.IsNullOrWhiteSpace(profile.Playlists.EncodingName))
                throw new InvalidDataException(
                    $"Export profile '{profile.Id}' playlist encoding cannot be empty.");
            string lineEnding = profile.Playlists.LineEnding.Trim().ToLowerInvariant();
            if (lineEnding is not ("platform" or "lf" or "crlf"))
                throw new InvalidDataException(
                    $"Export profile '{profile.Id}' playlist line ending must be platform, lf, " +
                    "or crlf.");
        }
        if (profile.Enabled && (string.IsNullOrWhiteSpace(profile.Transport.ProviderId) ||
            !allowUnboundTransportDestination &&
            string.IsNullOrWhiteSpace(profile.Transport.Destination)))
            throw new InvalidDataException(
                $"Enabled export profile '{profile.Id}' requires a transport provider and destination.");
        if (profile.Transport.Options.Keys.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException(
                $"Export profile '{profile.Id}' contains an unnamed transport option.");
    }

    private static void Positive(int? value, string id, string description)
    {
        if (value is <= 0)
            throw new InvalidDataException(
                $"Export profile '{id}' {description} must be positive.");
    }

    private static void NonNegative(int? value, string id, string description)
    {
        if (value is < 0)
            throw new InvalidDataException(
                $"Export profile '{id}' {description} cannot be negative.");
    }

    private static string Required(XElement? element, string attribute, bool allowEmpty = false)
    {
        string? raw = (string?)element?.Attribute(attribute);
        if (raw is null || (!allowEmpty && string.IsNullOrWhiteSpace(raw)))
            throw new InvalidDataException(
                $"<{element?.Name.LocalName ?? "ExportProfile"}> requires a non-empty " +
                $"{attribute} attribute.");
        return allowEmpty ? raw : raw.Trim();
    }

    private static string? Optional(XElement? element, string attribute)
    {
        string? value = (string?)element?.Attribute(attribute);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool Boolean(XElement? element, string attribute, bool fallback)
    {
        string? value = Optional(element, attribute);
        if (value is null)
            return fallback;
        if (bool.TryParse(value, out bool parsed))
            return parsed;
        throw new InvalidDataException(
            $"Attribute '{attribute}' on <{element?.Name.LocalName}> must be true or false.");
    }

    private static int? OptionalInteger(XElement? element, string attribute)
    {
        string? value = Optional(element, attribute);
        if (value is null)
            return null;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int parsed))
            return parsed;
        throw new InvalidDataException(
            $"Attribute '{attribute}' on <{element?.Name.LocalName}> must be an integer.");
    }

    private static T EnumValue<T>(XElement? element, string attribute, T fallback)
        where T : struct, Enum
    {
        string? value = Optional(element, attribute);
        if (value is null)
            return fallback;
        if (Enum.TryParse(value, true, out T parsed) && Enum.IsDefined(parsed))
            return parsed;
        throw new InvalidDataException(
            $"Invalid {attribute} '{value}' on <{element?.Name.LocalName}>.");
    }

    private static T? OptionalEnum<T>(XElement? element, string attribute)
        where T : struct, Enum
    {
        string? value = Optional(element, attribute);
        if (value is null)
            return null;
        if (Enum.TryParse(value, true, out T parsed) && Enum.IsDefined(parsed))
            return parsed;
        throw new InvalidDataException(
            $"Invalid {attribute} '{value}' on <{element?.Name.LocalName}>.");
    }

    private static void SetOptional(XElement element, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            element.SetAttributeValue(name, value.Trim());
    }

    private static void SetOptionalInteger(XElement element, string name, int? value)
    {
        if (value is not null)
            element.SetAttributeValue(name, value.Value.ToString(CultureInfo.InvariantCulture));
    }
}

}
