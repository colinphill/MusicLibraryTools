using System.Collections.Immutable;

namespace MusicLibraryManager.Presentation;

internal readonly record struct WorkbenchStatusCountArgument(
    string ResourceKey,
    long Count);

internal sealed class WorkbenchStatusText
{
    private readonly string _resourceKey;
    private readonly long? _count;
    private readonly ImmutableArray<object?> _arguments;
    private readonly ImmutableArray<WorkbenchStatusCountArgument>
        _countArguments;

    private WorkbenchStatusText(
        string resourceKey,
        long? count,
        ImmutableArray<object?> arguments,
        ImmutableArray<WorkbenchStatusCountArgument>
            countArguments)
    {
        _resourceKey = resourceKey;
        _count = count;
        _arguments = arguments;
        _countArguments = countArguments;
    }

    public static WorkbenchStatusText Format(
        string resourceKey,
        params object?[] arguments) =>
        new(
            resourceKey,
            null,
            [.. arguments],
            []);

    public static WorkbenchStatusText FormatCount(
        string resourceKey,
        long count,
        params object?[] arguments) =>
        new(
            resourceKey,
            count,
            [.. arguments],
            []);

    public static WorkbenchStatusText Compose(
        string resourceKey,
        params WorkbenchStatusCountArgument[]
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

internal sealed class WorkbenchLocalizedStatusState
{
    private readonly ILocalizationService? _localization;
    private WorkbenchStatusText? _current;

    public WorkbenchLocalizedStatusState(
        ILocalizationService? localization)
    {
        _localization = localization;
        if (_localization is not null)
            _localization.CultureChanged +=
                OnLocalizationCultureChanged;
    }

    public string Text { get; private set; } = "";

    public event EventHandler? TextChanged;

    public void Set(WorkbenchStatusText status)
    {
        ArgumentNullException.ThrowIfNull(status);
        _current = status;
        Refresh();
    }

    private void OnLocalizationCultureChanged(
        object? sender,
        EventArgs e) =>
        Refresh();

    private void Refresh()
    {
        string text =
            _current?.Render(_localization) ?? "";
        if (string.Equals(
                text,
                Text,
                StringComparison.Ordinal))
            return;
        Text = text;
        TextChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal static class WorkbenchStatusTexts
{
    public static WorkbenchStatusText PreviewReady(
        long changeCount,
        long fileCount) =>
        Compose(
            "Workbench.Status.PreviewReady",
            (
                "Workbench.Status.PreviewReady.ChangeCount",
                changeCount),
            (
                "Workbench.Status.PreviewReady.FileCount",
                fileCount));

    public static WorkbenchStatusText PlaylistWritten(
        long playlistCount,
        long trackReferenceCount) =>
        Compose(
            "Workbench.Status.PlaylistWritten",
            (
                "Workbench.Status.PlaylistWritten.FileCount",
                playlistCount),
            (
                "Workbench.Status.PlaylistWritten.TrackReferenceCount",
                trackReferenceCount));

    public static WorkbenchStatusText ReportWritten(
        long fileCount,
        long rowCount) =>
        Compose(
            "Workbench.Status.ReportWritten",
            (
                "Workbench.Status.ReportWritten.FileCount",
                fileCount),
            (
                "Workbench.Status.ReportWritten.RowCount",
                rowCount));

    public static WorkbenchStatusText
        SourcesAddedWithWarnings(
            long addedFileCount,
            long sessionFileCount,
            long warningCount) =>
        Compose(
            "Workbench.Status.SourcesAddedWithWarnings",
            (
                "Workbench.Status.SourcesAddedWithWarnings.AddedFileCount",
                addedFileCount),
            (
                "Workbench.Status.SourcesAddedWithWarnings.SessionFileCount",
                sessionFileCount),
            (
                "Workbench.Status.SourcesAddedWithWarnings.WarningCount",
                warningCount));

    public static WorkbenchStatusText FingerprintDiscovery(
        long fileCount,
        long candidateCount,
        long warningCount) =>
        Compose(
            "Workbench.Status.FingerprintDiscovery",
            (
                "Workbench.Status.FingerprintDiscovery.FileCount",
                fileCount),
            (
                "Workbench.Status.FingerprintDiscovery.CandidateCount",
                candidateCount),
            (
                "Workbench.Status.FingerprintDiscovery.WarningCount",
                warningCount));

    public static WorkbenchStatusText ImportMapped(
        long matchedRowCount,
        long totalRowCount) =>
        Compose(
            "Workbench.Status.ImportMapped",
            (
                "Workbench.Status.ImportMapping.MatchedRowCount",
                matchedRowCount),
            (
                "Workbench.Status.ImportMapping.TotalRowCount",
                totalRowCount));

    public static WorkbenchStatusText
        ImportMappedWithWarnings(
            long matchedRowCount,
            long totalRowCount,
            long warningCount) =>
        Compose(
            "Workbench.Status.ImportMappedWithWarnings",
            (
                "Workbench.Status.ImportMapping.MatchedRowCount",
                matchedRowCount),
            (
                "Workbench.Status.ImportMapping.TotalRowCount",
                totalRowCount),
            (
                "Workbench.Status.ImportMapping.WarningCount",
                warningCount));

    public static WorkbenchStatusText DiscogsMappingReady(
        long suggestedCount,
        long fileCount,
        long reviewCount) =>
        ComposeMapping(
            "Workbench.Status.DiscogsMappingReady",
            suggestedCount,
            fileCount,
            reviewCount);

    public static WorkbenchStatusText ReleaseMappingReady(
        long suggestedCount,
        long fileCount,
        long reviewCount) =>
        ComposeMapping(
            "Workbench.Status.ReleaseMappingReady",
            suggestedCount,
            fileCount,
            reviewCount);

    private static WorkbenchStatusText ComposeMapping(
        string resourceKey,
        long suggestedCount,
        long fileCount,
        long reviewCount) =>
        Compose(
            resourceKey,
            (
                "Workbench.Status.MappingReady.SuggestedCount",
                suggestedCount),
            (
                "Workbench.Status.MappingReady.FileCount",
                fileCount),
            (
                "Workbench.Status.MappingReady.ReviewCount",
                reviewCount));

    private static WorkbenchStatusText Compose(
        string resourceKey,
        params (string ResourceKey, long Count)[] counts) =>
        WorkbenchStatusText.Compose(
            resourceKey,
            [
                .. counts.Select(count =>
                    new WorkbenchStatusCountArgument(
                        count.ResourceKey,
                        count.Count)),
            ]);
}
