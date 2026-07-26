using System.Globalization;
using System.Text.RegularExpressions;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

[Collection(LocalizationTestCollection.Name)]
public sealed class WorkbenchStatusLocalizationTests
{
    [Fact]
    public void Composite_statuses_pluralize_every_count_independently()
    {
        var localization =
            new TracingLocalizationService();
        foreach (CompositeStatusCase statusCase in
                 CompositeStatusCases())
        {
            int combinations = 1 << statusCase.CountKeys.Length;
            for (int mask = 0; mask < combinations; mask++)
            {
                long[] counts =
                [
                    .. Enumerable.Range(
                            0,
                            statusCase.CountKeys.Length)
                        .Select(index =>
                            (mask & (1 << index)) == 0
                                ? 1L
                                : 2L),
                ];

                string text =
                    statusCase.Create(counts)
                        .Render(localization);

                Assert.Contains(
                    $"en-US:{statusCase.TemplateKey}[",
                    text,
                    StringComparison.Ordinal);
                for (int index = 0;
                     index < statusCase.CountKeys.Length;
                     index++)
                {
                    string category =
                        counts[index] == 1
                            ? "One"
                            : "Other";
                    Assert.Contains(
                        $"en-US:{statusCase.CountKeys[index]}." +
                        $"{category}({counts[index]})",
                        text,
                        StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void Localized_status_state_rerenders_the_same_composite_on_culture_change()
    {
        var localization =
            new TracingLocalizationService();
        var state =
            new WorkbenchLocalizedStatusState(
                localization);
        int changes = 0;
        state.TextChanged += (_, _) => changes++;

        state.Set(
            WorkbenchStatusTexts
                .SourcesAddedWithWarnings(
                    1,
                    2,
                    1));
        string english = state.Text;

        localization.SetCulture("fr-FR");

        Assert.Equal(2, changes);
        Assert.StartsWith(
            "en-US:Workbench.Status.SourcesAddedWithWarnings[",
            english,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "fr-FR:Workbench.Status.SourcesAddedWithWarnings[",
            state.Text,
            StringComparison.Ordinal);
        Assert.Contains(
            "fr-FR:Workbench.Status.SourcesAddedWithWarnings." +
            "AddedFileCount.One(1)",
            state.Text,
            StringComparison.Ordinal);
        Assert.Contains(
            "fr-FR:Workbench.Status.SourcesAddedWithWarnings." +
            "SessionFileCount.Other(2)",
            state.Text,
            StringComparison.Ordinal);
        Assert.Contains(
            "fr-FR:Workbench.Status.SourcesAddedWithWarnings." +
            "WarningCount.One(1)",
            state.Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Workbench_source_uses_reason_specific_conflicts_and_complete_tag_layer_labels()
    {
        string source = File.ReadAllText(
            FindRepositoryFile(
                "MusicLibraryManager.Presentation",
                "WorkbenchViewModel.cs"));

        Assert.DoesNotContain(
            "Workbench.Dialog.PendingConflict.Message",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Workbench.Operation.TagLayer",
            source,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            CountOccurrences(
                source,
                "Workbench.Dialog.PendingConflict." +
                "PendingMutationRejected"));
        foreach (string suffix in new[]
                 {
                     "MetadataPlan",
                     "FileOperationServiceUnavailable",
                     "TranscodeServiceUnavailable",
                     "FileOperationCannotApply",
                     "TranscodeCannotApply",
                     "ReplaceOriginalConflict",
                     "FileOperationRefreshFailed",
                 })
        {
            Assert.Equal(
                1,
                CountOccurrences(
                    source,
                    $"Workbench.Dialog.PendingConflict.{suffix}"));
        }
        Assert.Equal(
            1,
            CountOccurrences(
                source,
                "Workbench.Operation.AddTagLayer"));
        Assert.Equal(
            1,
            CountOccurrences(
                source,
                "Workbench.Operation.RemoveTagLayer"));
        foreach (string formatter in new[]
                 {
                     "PreviewReady",
                     "PlaylistWritten",
                     "ReportWritten",
                     "SourcesAddedWithWarnings",
                     "FingerprintDiscovery",
                     "ImportMapped",
                     "ImportMappedWithWarnings",
                     "DiscogsMappingReady",
                     "ReleaseMappingReady",
                 })
        {
            Assert.Equal(
                1,
                CountFormatterCalls(
                    source,
                    formatter));
        }
    }

    private static IReadOnlyList<CompositeStatusCase>
        CompositeStatusCases() =>
    [
        new(
            "PreviewReady",
            "Workbench.Status.PreviewReady",
            counts =>
                WorkbenchStatusTexts.PreviewReady(
                    counts[0],
                    counts[1]),
            [
                "Workbench.Status.PreviewReady.ChangeCount",
                "Workbench.Status.PreviewReady.FileCount",
            ]),
        new(
            "PlaylistWritten",
            "Workbench.Status.PlaylistWritten",
            counts =>
                WorkbenchStatusTexts.PlaylistWritten(
                    counts[0],
                    counts[1]),
            [
                "Workbench.Status.PlaylistWritten.FileCount",
                "Workbench.Status.PlaylistWritten." +
                "TrackReferenceCount",
            ]),
        new(
            "ReportWritten",
            "Workbench.Status.ReportWritten",
            counts =>
                WorkbenchStatusTexts.ReportWritten(
                    counts[0],
                    counts[1]),
            [
                "Workbench.Status.ReportWritten.FileCount",
                "Workbench.Status.ReportWritten.RowCount",
            ]),
        new(
            "SourcesAddedWithWarnings",
            "Workbench.Status.SourcesAddedWithWarnings",
            counts =>
                WorkbenchStatusTexts
                    .SourcesAddedWithWarnings(
                        counts[0],
                        counts[1],
                        counts[2]),
            [
                "Workbench.Status.SourcesAddedWithWarnings." +
                "AddedFileCount",
                "Workbench.Status.SourcesAddedWithWarnings." +
                "SessionFileCount",
                "Workbench.Status.SourcesAddedWithWarnings." +
                "WarningCount",
            ]),
        new(
            "FingerprintDiscovery",
            "Workbench.Status.FingerprintDiscovery",
            counts =>
                WorkbenchStatusTexts.FingerprintDiscovery(
                    counts[0],
                    counts[1],
                    counts[2]),
            [
                "Workbench.Status.FingerprintDiscovery.FileCount",
                "Workbench.Status.FingerprintDiscovery." +
                "CandidateCount",
                "Workbench.Status.FingerprintDiscovery." +
                "WarningCount",
            ]),
        new(
            "ImportMapped",
            "Workbench.Status.ImportMapped",
            counts =>
                WorkbenchStatusTexts.ImportMapped(
                    counts[0],
                    counts[1]),
            [
                "Workbench.Status.ImportMapping.MatchedRowCount",
                "Workbench.Status.ImportMapping.TotalRowCount",
            ]),
        new(
            "ImportMappedWithWarnings",
            "Workbench.Status.ImportMappedWithWarnings",
            counts =>
                WorkbenchStatusTexts
                    .ImportMappedWithWarnings(
                        counts[0],
                        counts[1],
                        counts[2]),
            [
                "Workbench.Status.ImportMapping.MatchedRowCount",
                "Workbench.Status.ImportMapping.TotalRowCount",
                "Workbench.Status.ImportMapping.WarningCount",
            ]),
        new(
            "DiscogsMappingReady",
            "Workbench.Status.DiscogsMappingReady",
            counts =>
                WorkbenchStatusTexts.DiscogsMappingReady(
                    counts[0],
                    counts[1],
                    counts[2]),
            [
                "Workbench.Status.MappingReady.SuggestedCount",
                "Workbench.Status.MappingReady.FileCount",
                "Workbench.Status.MappingReady.ReviewCount",
            ]),
        new(
            "ReleaseMappingReady",
            "Workbench.Status.ReleaseMappingReady",
            counts =>
                WorkbenchStatusTexts.ReleaseMappingReady(
                    counts[0],
                    counts[1],
                    counts[2]),
            [
                "Workbench.Status.MappingReady.SuggestedCount",
                "Workbench.Status.MappingReady.FileCount",
                "Workbench.Status.MappingReady.ReviewCount",
            ]),
    ];

    private static int CountOccurrences(
        string source,
        string value) =>
        Regex.Matches(
            source,
            Regex.Escape(value),
            RegexOptions.CultureInvariant)
            .Count;

    private static int CountFormatterCalls(
        string source,
        string formatter) =>
        Regex.Matches(
            source,
            $@"WorkbenchStatusTexts\s*\.\s*" +
            Regex.Escape(formatter) +
            @"\s*\(",
            RegexOptions.CultureInvariant)
            .Count;

    private static string FindRepositoryFile(
        params string[] segments)
    {
        DirectoryInfo? directory =
            new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate =
                Path.Combine(
                    directory.FullName,
                    Path.Combine(segments));
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file " +
            $"'{Path.Combine(segments)}'.");
    }

    private sealed record CompositeStatusCase(
        string Name,
        string TemplateKey,
        Func<long[], WorkbenchStatusText> Create,
        string[] CountKeys);

    private sealed class TracingLocalizationService :
        ILocalizationService
    {
        private CultureInfo _culture =
            CultureInfo.GetCultureInfo("en-US");

        public CultureInfo CurrentUICulture =>
            _culture;

        public IReadOnlyList<CultureInfo>
            SupportedCultures { get; } =
        [
            CultureInfo.GetCultureInfo("en-US"),
            CultureInfo.GetCultureInfo("fr-FR"),
        ];

        public event EventHandler? CultureChanged;

        public string Get(string key) =>
            $"{_culture.Name}:{key}";

        public string Format(
            string key,
            params object?[] arguments) =>
            $"{Get(key)}[" +
            $"{string.Join(
                "|",
                arguments.Select(argument =>
                    Convert.ToString(
                        argument,
                        CultureInfo.InvariantCulture)))}]";

        public string FormatCount(
            string key,
            long count,
            params object?[] arguments) =>
            $"{Get(
                $"{key}.{(
                    count == 1
                        ? "One"
                        : "Other")}")}({count})";

        public IReadOnlyDictionary<string, string>
            Snapshot() =>
            new Dictionary<string, string>();

        public void SetCulture(string cultureName)
        {
            _culture =
                CultureInfo.GetCultureInfo(
                    cultureName);
            CultureChanged?.Invoke(
                this,
                EventArgs.Empty);
        }
    }
}
