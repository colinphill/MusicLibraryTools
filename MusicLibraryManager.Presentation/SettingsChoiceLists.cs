using MusicLibrary.Core.Services;
using MusicLibraryTools;

namespace MusicLibraryManager.Presentation;

public sealed record SettingsChannelChoice(
    LibraryChannelSelection Value,
    string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Immutable choices used by Settings selectors. These sources must not depend on the visual tree:
/// Avalonia detaches inactive tab content, and a transiently empty ComboBox ItemsSource writes a
/// null selection back through two-way bindings.
/// </summary>
public static class SettingsChoiceLists
{
    public static IReadOnlyList<LibraryPathCollisionPolicy> CollisionPolicies { get; } =
        EnumValues<LibraryPathCollisionPolicy>();
    public static IReadOnlyList<LibraryUnicodeNormalization> UnicodeNormalizations { get; } =
        EnumValues<LibraryUnicodeNormalization>();
    public static IReadOnlyList<LibraryDiscStrategy> DiscStrategies { get; } =
        EnumValues<LibraryDiscStrategy>();
    public static IReadOnlyList<LibraryTrackTotalScope> TrackTotalScopes { get; } =
        EnumValues<LibraryTrackTotalScope>();
    public static IReadOnlyList<LibraryHealthSeverity> HealthSeverities { get; } =
        EnumValues<LibraryHealthSeverity>();
    public static IReadOnlyList<LibrarySourceDisposition> SourceDispositions { get; } =
        EnumValues<LibrarySourceDisposition>();
    public static IReadOnlyList<LibraryIngestAction> IngestActions { get; } =
        EnumValues<LibraryIngestAction>();
    public static IReadOnlyList<LibraryIngestAlbumCondition> IngestAlbumConditions { get; } =
        EnumValues<LibraryIngestAlbumCondition>();
    public static IReadOnlyList<LibraryIngestSourceSelection> IngestSourceSelections { get; } =
        EnumValues<LibraryIngestSourceSelection>();
    public static IReadOnlyList<SettingsChannelChoice> ChannelChoices { get; } =
        Array.AsReadOnly<SettingsChannelChoice>([
            new(LibraryChannelSelection.Stereo, "Stereo"),
            new(LibraryChannelSelection.Multi, "Multi"),
        ]);
    public static IReadOnlyList<LibraryArtworkStorage> ArtworkStorageChoices { get; } =
        EnumValues<LibraryArtworkStorage>();
    public static IReadOnlyList<LibraryArtworkRoleSelection> ArtworkRoleChoices { get; } =
        EnumValues<LibraryArtworkRoleSelection>();
    public static IReadOnlyList<LibraryArtworkEncoding> ArtworkEncodingChoices { get; } =
        EnumValues<LibraryArtworkEncoding>();
    public static IReadOnlyList<LibrarySidecarDisposition> SidecarDispositions { get; } =
        EnumValues<LibrarySidecarDisposition>();
    public static IReadOnlyList<ExportSelectionKind> ExportSelectionKinds { get; } =
        EnumValues<ExportSelectionKind>();
    public static IReadOnlyList<ExportTransformMode> ExportTransformModes { get; } =
        EnumValues<ExportTransformMode>();
    public static IReadOnlyList<ExportArtworkMode> ExportArtworkModes { get; } =
        EnumValues<ExportArtworkMode>();
    public static IReadOnlyList<ExportExtraFileDisposition> ExportExtraFileDispositions { get; } =
        EnumValues<ExportExtraFileDisposition>();

    public static IReadOnlyList<string> PlaylistTypes { get; } =
        ReadOnly("m3u", "m3u8", "wpl");
    public static IReadOnlyList<string> PlaylistSourceTypes { get; } = ReadOnly("m3u");
    public static IReadOnlyList<string> PlaylistPathStyles { get; } =
        ReadOnly("legacy", "provided", "absolute", "relative");
    public static IReadOnlyList<string> PlaylistEncodings { get; } =
        ReadOnly("utf-8", "utf-16", "utf-16be", "ascii");
    public static IReadOnlyList<string> PlaylistLineEndings { get; } =
        ReadOnly("platform", "crlf", "lf");
    public static IReadOnlyList<string> PlaylistFileNameTransforms { get; } =
        ReadOnly("legacy", "preserve", "sanitize", "sonos");

    private static IReadOnlyList<T> EnumValues<T>() where T : struct, Enum =>
        Array.AsReadOnly(Enum.GetValues<T>());

    private static IReadOnlyList<string> ReadOnly(params string[] values) =>
        Array.AsReadOnly(values);

    public static SettingsChannelChoice ChannelChoice(
        LibraryChannelSelection? value) => ChannelChoices.First(choice =>
            choice.Value == (value ?? LibraryChannelSelection.Stereo));
}
