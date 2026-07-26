using System.Collections.Immutable;

namespace MusicLibraryManager.Presentation;

internal readonly record struct FieldsStatusCountArgument(
    string ResourceKey,
    long Count);

/// <summary>
/// Retains the semantic inputs for Fields status text so every count can be
/// pluralized independently and the complete message can be rendered again
/// after a live UI-culture change.
/// </summary>
internal sealed class FieldsStatusText
{
    private readonly string _resourceKey;
    private readonly long? _count;
    private readonly ImmutableArray<object?> _arguments;
    private readonly ImmutableArray<FieldsStatusCountArgument>
        _countArguments;

    private FieldsStatusText(
        string resourceKey,
        long? count,
        ImmutableArray<object?> arguments,
        ImmutableArray<FieldsStatusCountArgument>
            countArguments)
    {
        _resourceKey = resourceKey;
        _count = count;
        _arguments = arguments;
        _countArguments = countArguments;
    }

    public static FieldsStatusText Format(
        string resourceKey,
        params object?[] arguments) =>
        new(
            resourceKey,
            null,
            [.. arguments],
            []);

    public static FieldsStatusText FormatCount(
        string resourceKey,
        long count,
        params object?[] arguments) =>
        new(
            resourceKey,
            count,
            [.. arguments],
            []);

    public static FieldsStatusText Compose(
        string resourceKey,
        params FieldsStatusCountArgument[]
            countArguments) =>
        new(
            resourceKey,
            null,
            [],
            [.. countArguments]);

    public string Render(
        ILocalizationService? localization)
    {
        object?[] arguments = _countArguments.IsDefaultOrEmpty
            ? [.. _arguments]
            :
            [
                .. _countArguments.Select(argument =>
                    FormatCount(
                        localization,
                        argument.ResourceKey,
                        argument.Count)),
            ];
        return _count is { } count
            ? FormatCount(
                localization,
                _resourceKey,
                count,
                arguments)
            : Format(
                localization,
                _resourceKey,
                arguments);
    }

    private static string Format(
        ILocalizationService? localization,
        string resourceKey,
        params object?[] arguments) =>
        localization?.Format(
            resourceKey,
            arguments) ??
        LocalizedText.Format(
            resourceKey,
            arguments);

    private static string FormatCount(
        ILocalizationService? localization,
        string resourceKey,
        long count,
        params object?[] arguments) =>
        localization?.FormatCount(
            resourceKey,
            count,
            arguments) ??
        LocalizedText.FormatCount(
            resourceKey,
            count,
            arguments);
}

internal static class FieldsStatusTexts
{
    public static FieldsStatusText PreviewStarting(
        long fieldChangeCount,
        long selectedFileCount) =>
        FieldsStatusText.Compose(
            "Fields.Activity.Preview.Starting",
            new(
                "Fields.Count.FieldChanges",
                fieldChangeCount),
            new(
                "Fields.Count.SelectedFiles",
                selectedFileCount));

    public static FieldsStatusText PreviewReady(
        long filesWithChangesCount,
        long fieldChangeCount) =>
        FieldsStatusText.Compose(
            "Fields.Status.PreviewReady",
            new(
                "Fields.Count.FilesWithChanges",
                filesWithChangesCount),
            new(
                "Fields.Count.FieldChanges",
                fieldChangeCount));
}
