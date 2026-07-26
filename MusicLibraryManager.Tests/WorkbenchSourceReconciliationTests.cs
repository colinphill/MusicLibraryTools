using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class WorkbenchSourceReconciliationTests
{
    private static readonly string[] ShippingCultures =
    [
        "de-DE",
        "es-ES",
        "fr-FR",
        "it-IT",
        "pt-BR",
        "ja-JP",
        "ko-KR",
        "zh-CN",
        "zh-TW",
    ];

    private static readonly Regex CSharpResourceTokenPattern =
        new(
            "(?:@)?\"(?<key>(?:Workbench|Column)\\." +
            "[A-Za-z0-9_.]+)\"",
            RegexOptions.CultureInvariant);

    private static readonly Regex XamlResourceTokenPattern =
        new(
            "\\{(?:Dynamic|Static)Resource\\s+Loc\\." +
            "(?<key>[A-Za-z0-9_.]+)(?=\\s|\\})",
            RegexOptions.CultureInvariant);

    [Fact]
    public void Deprecated_compatibility_aliases_remain_complete_reviewed_resources_without_runtime_lookups()
    {
        string repositoryRoot = FindRepositoryRoot();
        IReadOnlyDictionary<string, string> neutral =
            LoadCatalog(
                Path.Combine(
                    repositoryRoot,
                    "MusicLibraryManager.Presentation",
                    "Resources",
                    "Strings.resx"));
        IReadOnlyDictionary<string, XElement> manifest =
            XDocument.Load(
                    Path.Combine(
                        repositoryRoot,
                        "BuildTools",
                        "LocalizationCatalogGenerator",
                        "EditorialReviewManifest.xml"))
                .Root!
                .Elements("entry")
                .ToDictionary(
                    entry => (string)entry.Attribute("key")!,
                    StringComparer.Ordinal);

        Assert.Equal(
            86,
            WorkbenchSourceReconciliationContract
                .CompatibilityAliases.Length);
        foreach (string alias in
                 WorkbenchSourceReconciliationContract
                     .CompatibilityAliases)
        {
            Assert.True(
                neutral.TryGetValue(alias, out string? value),
                $"Neutral catalog is missing compatibility alias '{alias}'.");
            Assert.False(
                string.IsNullOrWhiteSpace(value),
                $"Neutral compatibility alias '{alias}' is blank.");
            Assert.True(
                manifest.TryGetValue(alias, out XElement? record),
                $"Review manifest is missing compatibility alias '{alias}'.");
            Assert.NotEqual(
                "Pending",
                (string?)record!.Attribute("status"));
        }

        foreach (string culture in ShippingCultures)
        {
            IReadOnlyDictionary<string, string> satellite =
                LoadCatalog(
                    Path.Combine(
                        repositoryRoot,
                        "MusicLibraryManager.Presentation",
                        "Resources",
                        $"Strings.{culture}.resx"));
            foreach (string alias in
                     WorkbenchSourceReconciliationContract
                         .CompatibilityAliases)
            {
                Assert.True(
                    satellite.TryGetValue(
                        alias,
                        out string? value),
                    $"{culture} is missing compatibility alias '{alias}'.");
                Assert.False(
                    string.IsNullOrWhiteSpace(value),
                    $"{culture} compatibility alias '{alias}' is blank.");
            }
        }

        HashSet<string> runtimeLookups =
            CollectShippingRuntimeLookups(repositoryRoot);
        foreach (string alias in
                 WorkbenchSourceReconciliationContract
                     .CompatibilityAliases)
        {
            Assert.DoesNotContain(alias, runtimeLookups);
        }
        Assert.DoesNotContain(
            runtimeLookups,
            key => key.StartsWith(
                "Workbench.Grid.Header.",
                StringComparison.Ordinal));

        // Exact tokens deliberately distinguish retired aliases such as
        // Workbench.Split.Resize and Workbench.PendingChanges.Empty from
        // their live successors ending in Automation, Title, or Description.
        Assert.Contains(
            "Workbench.Split.ResizeAutomation",
            runtimeLookups);
        Assert.Contains(
            "Workbench.PendingChanges.EmptyTitle",
            runtimeLookups);
    }

    [Fact]
    public void Shipping_source_uses_every_reconciled_resource_contract()
    {
        string repositoryRoot = FindRepositoryRoot();
        HashSet<string> runtimeLookups =
            CollectShippingRuntimeLookups(repositoryRoot);
        string[] expectedRuntimeKeys =
        [
            .. WorkbenchSourceReconciliationContract
                .AddedOrChangedResources
                .Select(key =>
                    key.EndsWith(
                            ".One",
                            StringComparison.Ordinal) ||
                        key.EndsWith(
                            ".Other",
                            StringComparison.Ordinal)
                        ? key[..key.LastIndexOf('.')]
                        : key)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(52, expectedRuntimeKeys.Length);
        foreach (string key in expectedRuntimeKeys)
            Assert.Contains(key, runtimeLookups);
    }

    private static HashSet<string>
        CollectShippingRuntimeLookups(
            string repositoryRoot)
    {
        var lookups =
            new HashSet<string>(StringComparer.Ordinal);
        foreach (string sourceRootName in
                 new[]
                 {
                     "MusicLibraryManager",
                     "MusicLibraryManager.Presentation",
                 })
        {
            string sourceRoot = Path.Combine(
                repositoryRoot,
                sourceRootName);
            foreach (string path in
                     Directory.EnumerateFiles(
                         sourceRoot,
                         "*",
                         SearchOption.AllDirectories))
            {
                string extension = Path.GetExtension(path);
                if (extension is not ".cs" and not ".axaml")
                    continue;
                string relativePath = Path.GetRelativePath(
                    sourceRoot,
                    path);
                string[] segments = relativePath.Split(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                if (segments.Any(segment =>
                        segment is "bin" or "obj" or "Resources"))
                    continue;

                string source = File.ReadAllText(path);
                Regex pattern = extension == ".cs"
                    ? CSharpResourceTokenPattern
                    : XamlResourceTokenPattern;
                foreach (Match match in pattern.Matches(source))
                    lookups.Add(match.Groups["key"].Value);
            }
        }

        return lookups;
    }

    private static IReadOnlyDictionary<string, string>
        LoadCatalog(
            string path) =>
        XDocument.Load(path)
            .Root!
            .Elements("data")
            .ToDictionary(
                element => (string)element.Attribute("name")!,
                element => element.Element("value")?.Value ?? "",
                StringComparer.Ordinal);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory =
            new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(
                    Path.Combine(
                        directory.FullName,
                        "MusicLibraryTools.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the MusicLibraryTools repository root.");
    }
}

internal static class WorkbenchSourceReconciliationContract
{
    public static readonly string[] AddedOrChangedResources =
    [
        "Column.AppliesTo",
        "Column.ArtworkRoles",
        "Column.CoverArtArchiveId",
        "Column.Kind",
        "Column.Operation",
        "Column.SelectedFiles",
        "Column.TagLayers",
        "Column.TargetDetails",
        "Column.Values",
        "Workbench.Bulk.ConditionField",
        "Workbench.Bulk.RecipeName",
        "Workbench.Bulk.SourceField",
        "Workbench.Columns.InlineEditingLabel",
        "Workbench.Dialog.PendingConflict.FileOperationCannotApply",
        "Workbench.Dialog.PendingConflict.FileOperationRefreshFailed",
        "Workbench.Dialog.PendingConflict.FileOperationServiceUnavailable",
        "Workbench.Dialog.PendingConflict.MetadataPlan",
        "Workbench.Dialog.PendingConflict.PendingMutationRejected",
        "Workbench.Dialog.PendingConflict.ReplaceOriginalConflict",
        "Workbench.Dialog.PendingConflict.TranscodeCannotApply",
        "Workbench.Dialog.PendingConflict.TranscodeServiceUnavailable",
        "Workbench.Online.Mapping.IncludeAutomation",
        "Workbench.Online.Mapping.IncludeHeader",
        "Workbench.Operation.AddTagLayer",
        "Workbench.Operation.RemoveTagLayer",
        "Workbench.Status.FingerprintDiscovery.CandidateCount.One",
        "Workbench.Status.FingerprintDiscovery.CandidateCount.Other",
        "Workbench.Status.FingerprintDiscovery.FileCount.One",
        "Workbench.Status.FingerprintDiscovery.FileCount.Other",
        "Workbench.Status.FingerprintDiscovery.WarningCount.One",
        "Workbench.Status.FingerprintDiscovery.WarningCount.Other",
        "Workbench.Status.ImportMapping.MatchedRowCount.One",
        "Workbench.Status.ImportMapping.MatchedRowCount.Other",
        "Workbench.Status.ImportMapping.TotalRowCount.One",
        "Workbench.Status.ImportMapping.TotalRowCount.Other",
        "Workbench.Status.ImportMapping.WarningCount.One",
        "Workbench.Status.ImportMapping.WarningCount.Other",
        "Workbench.Status.MappingReady.FileCount.One",
        "Workbench.Status.MappingReady.FileCount.Other",
        "Workbench.Status.MappingReady.ReviewCount.One",
        "Workbench.Status.MappingReady.ReviewCount.Other",
        "Workbench.Status.MappingReady.SuggestedCount.One",
        "Workbench.Status.MappingReady.SuggestedCount.Other",
        "Workbench.Status.PlaylistWritten",
        "Workbench.Status.PlaylistWritten.FileCount.One",
        "Workbench.Status.PlaylistWritten.FileCount.Other",
        "Workbench.Status.PlaylistWritten.TrackReferenceCount.One",
        "Workbench.Status.PlaylistWritten.TrackReferenceCount.Other",
        "Workbench.Status.PreviewReady",
        "Workbench.Status.PreviewReady.ChangeCount.One",
        "Workbench.Status.PreviewReady.ChangeCount.Other",
        "Workbench.Status.PreviewReady.FileCount.One",
        "Workbench.Status.PreviewReady.FileCount.Other",
        "Workbench.Status.ReportWritten",
        "Workbench.Status.ReportWritten.FileCount.One",
        "Workbench.Status.ReportWritten.FileCount.Other",
        "Workbench.Status.ReportWritten.RowCount.One",
        "Workbench.Status.ReportWritten.RowCount.Other",
        "Workbench.Status.SourcesAddedWithWarnings",
        "Workbench.Status.SourcesAddedWithWarnings.AddedFileCount.One",
        "Workbench.Status.SourcesAddedWithWarnings.AddedFileCount.Other",
        "Workbench.Status.SourcesAddedWithWarnings.SessionFileCount.One",
        "Workbench.Status.SourcesAddedWithWarnings.SessionFileCount.Other",
        "Workbench.Status.SourcesAddedWithWarnings.WarningCount.One",
        "Workbench.Status.SourcesAddedWithWarnings.WarningCount.Other",
        "Workbench.Status.DiscogsMappingReady",
        "Workbench.Status.FingerprintDiscovery",
        "Workbench.Status.ImportMapped",
        "Workbench.Status.ImportMappedWithWarnings",
        "Workbench.Status.ReleaseMappingReady",
    ];

    public static readonly string[] CompatibilityAliases =
    [
        "Workbench.Activity.ReloadingInspectorChanges",
        "Workbench.AllFields.Action.PastePreviewTooltip",
        "Workbench.Bulk.Field",
        "Workbench.Bulk.RecipeNamePlaceholder",
        "Workbench.Bulk.RepresentativeDescription",
        "Workbench.Dialog.PendingConflict.Message",
        "Workbench.FileOperations.PendingMetadataPreflight",
        "Workbench.Grid.Header.After",
        "Workbench.Grid.Header.AppliesTo",
        "Workbench.Grid.Header.Approved",
        "Workbench.Grid.Header.ArchiveId",
        "Workbench.Grid.Header.Arguments",
        "Workbench.Grid.Header.ArtistCredit",
        "Workbench.Grid.Header.Back",
        "Workbench.Grid.Header.Before",
        "Workbench.Grid.Header.Bytes",
        "Workbench.Grid.Header.CatalogNumber",
        "Workbench.Grid.Header.Comment",
        "Workbench.Grid.Header.Confidence",
        "Workbench.Grid.Header.Country",
        "Workbench.Grid.Header.Date",
        "Workbench.Grid.Header.Destination",
        "Workbench.Grid.Header.DiscogsReleaseId",
        "Workbench.Grid.Header.DiscogsTrack",
        "Workbench.Grid.Header.Duration",
        "Workbench.Grid.Header.Executable",
        "Workbench.Grid.Header.Field",
        "Workbench.Grid.Header.File",
        "Workbench.Grid.Header.Files",
        "Workbench.Grid.Header.Formats",
        "Workbench.Grid.Header.Front",
        "Workbench.Grid.Header.Genres",
        "Workbench.Grid.Header.Group",
        "Workbench.Grid.Header.Kind",
        "Workbench.Grid.Header.Label",
        "Workbench.Grid.Header.Labels",
        "Workbench.Grid.Header.MatchedPosition",
        "Workbench.Grid.Header.MusicBrainzRecordingIds",
        "Workbench.Grid.Header.MusicBrainzReleaseId",
        "Workbench.Grid.Header.Operation",
        "Workbench.Grid.Header.Position",
        "Workbench.Grid.Header.Preview",
        "Workbench.Grid.Header.Reason",
        "Workbench.Grid.Header.Release",
        "Workbench.Grid.Header.ReleaseTrack",
        "Workbench.Grid.Header.Rows",
        "Workbench.Grid.Header.SelectedFiles",
        "Workbench.Grid.Header.Source",
        "Workbench.Grid.Header.Status",
        "Workbench.Grid.Header.Styles",
        "Workbench.Grid.Header.TagLayers",
        "Workbench.Grid.Header.TargetDetails",
        "Workbench.Grid.Header.Thumbnail",
        "Workbench.Grid.Header.Tracks",
        "Workbench.Grid.Header.Types",
        "Workbench.Grid.Header.Use",
        "Workbench.Grid.Header.Values",
        "Workbench.Grid.Header.WorkingDirectory",
        "Workbench.Grid.Header.Year",
        "Workbench.Online.Field.DiscogsReleaseId",
        "Workbench.Operation.TagLayer",
        "Workbench.PendingChanges.Empty",
        "Workbench.PendingChanges.RevertTooltip",
        "Workbench.Reports.Safety",
        "Workbench.Section.AllFields",
        "Workbench.Section.AllFieldsAutomation",
        "Workbench.Section.BulkOperation",
        "Workbench.Section.Files",
        "Workbench.Section.OnlineMetadata",
        "Workbench.Section.Session",
        "Workbench.Section.ShortcutsAutomation",
        "Workbench.Section.Tools",
        "Workbench.Split.Resize",
        "Workbench.Status.InspectorChangesReloaded.One",
        "Workbench.Status.InspectorChangesReloaded.Other",
        "Workbench.Status.InspectorReloadCancelled",
        "Workbench.Status.InspectorReloadFailed",
        "Workbench.Status.NoPendingChanges",
        "Workbench.Status.PlaylistWritten.One",
        "Workbench.Status.PlaylistWritten.Other",
        "Workbench.Status.PreviewReady.One",
        "Workbench.Status.PreviewReady.Other",
        "Workbench.Status.ReportWritten.One",
        "Workbench.Status.ReportWritten.Other",
        "Workbench.Status.SourcesAddedWithWarnings.One",
        "Workbench.Status.SourcesAddedWithWarnings.Other",
    ];
}
