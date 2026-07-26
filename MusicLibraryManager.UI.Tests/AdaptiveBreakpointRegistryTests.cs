using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

/// <summary>
/// Keeps the set of literal adaptive comparisons reviewable. Adding or
/// changing a shipping width/height breakpoint requires both a registry entry
/// and an executable boundary test, so an untested comparison cannot silently
/// enter a view code-behind.
/// </summary>
public sealed class AdaptiveBreakpointRegistryTests
{
    private static readonly Regex NumericComparison =
        new(
            """
            \b(?<left>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)
            \s*(?<operator><=|>=|<|>)\s*
            (?<value>\d{3,4})(?!\d)
            """,
            RegexOptions.CultureInvariant |
            RegexOptions.IgnorePatternWhitespace);

    private static readonly BreakpointContract[] Contracts =
    [
        new(
            "MusicLibraryManager/MainWindow.axaml.cs",
            typeof(AdaptiveBreakpointCoverageTests),
            nameof(AdaptiveBreakpointCoverageTests
                .Shell_toolbar_gutters_and_activity_switch_at_exact_content_boundaries),
            "contentWidth <= 900",
            "shellHeight <= 700",
            "contentWidth < 1000"),
        new(
            "MusicLibraryManager/MainWindow.axaml.cs",
            typeof(AdaptiveBreakpointCoverageTests),
            nameof(AdaptiveBreakpointCoverageTests
                .Workbench_shell_and_section_breakpoints_switch_at_minus_one_exact_and_plus_one),
            "contentWidth <= 1100",
            "contentWidth <= 1100"),
        new(
            "MusicLibraryManager/Views/AboutView.axaml.cs",
            typeof(AdaptiveBreakpointCoverageTests),
            nameof(AdaptiveBreakpointCoverageTests
                .About_fields_and_reviewed_file_operations_switch_at_exact_boundaries),
            "AboutPageScaffold.ContentWidth < 960",
            "Bounds.Height <= 700",
            "AboutPageScaffold.ContentWidth < 720"),
        new(
            "MusicLibraryManager/Views/FieldsEditorView.axaml.cs",
            typeof(AdaptiveBreakpointCoverageTests),
            nameof(AdaptiveBreakpointCoverageTests
                .About_fields_and_reviewed_file_operations_switch_at_exact_boundaries),
            "Bounds.Width < 760"),
        new(
            "MusicLibraryManager/Views/ReviewedFileOperationEditorView.axaml.cs",
            typeof(AdaptiveBreakpointCoverageTests),
            nameof(AdaptiveBreakpointCoverageTests
                .About_fields_and_reviewed_file_operations_switch_at_exact_boundaries),
            "Bounds.Width < 880",
            "Bounds.Height <= 700"),
        new(
            "MusicLibraryManager/Views/HomeView.axaml.cs",
            typeof(AdaptiveBreakpointCoverageTests),
            nameof(AdaptiveBreakpointCoverageTests
                .Home_content_breakpoints_switch_at_minus_one_exact_and_plus_one),
            "width >= 1120",
            "width >= 620",
            "width >= 760",
            "width >= 700",
            "width >= 820"),
        new(
            "MusicLibraryManager/Views/HealthView.axaml.cs",
            typeof(AdaptiveBreakpointCoverageTests),
            nameof(AdaptiveBreakpointCoverageTests
                .Health_breakpoints_use_the_action_and_result_content_hosts),
            "width < 720",
            "Bounds.Height <= 700",
            "availableWidth < 680",
            "contentWidth < 760"),
        new(
            "MusicLibraryManager/Views/IngestView.axaml.cs",
            typeof(AdaptiveBreakpointCoverageTests),
            nameof(AdaptiveBreakpointCoverageTests
                .Ingest_breakpoints_switch_source_summary_and_compact_height_at_the_boundary),
            "Bounds.Height <= 560",
            "width < 760",
            "width < 700"),
        new(
            "MusicLibraryManager/Views/DevicesView.axaml.cs",
            typeof(AdaptiveBreakpointCoverageTests),
            nameof(AdaptiveBreakpointCoverageTests
                .Devices_and_operations_breakpoints_switch_at_minus_one_exact_and_plus_one),
            "width < 920"),
        new(
            "MusicLibraryManager/Views/OperationsView.axaml.cs",
            typeof(AdaptiveBreakpointCoverageTests),
            nameof(AdaptiveBreakpointCoverageTests
                .Devices_and_operations_breakpoints_switch_at_minus_one_exact_and_plus_one),
            "width < 780",
            "width < 860"),
        new(
            "MusicLibraryManager/Views/LibraryView.axaml.cs",
            typeof(EditorSourceReconciliationUiTests),
            nameof(EditorSourceReconciliationUiTests
                .Context_labels_refresh_without_changing_semantic_operation_or_column_ids),
            "visualFilterWidth < 600"),
        new(
            "MusicLibraryManager/Views/WorkbenchView.axaml.cs",
            typeof(AdaptiveBreakpointCoverageTests),
            nameof(AdaptiveBreakpointCoverageTests
                .Workbench_shell_and_section_breakpoints_switch_at_minus_one_exact_and_plus_one),
            "Bounds.Width <= 1100",
            "height <= 700",
            "contentWidth < 700"),
        Section(
            "WorkbenchAllFieldsSectionView.axaml.cs",
            "Bounds.Width < 880",
            "Bounds.Height < 430"),
        Section(
            "WorkbenchBulkOperationSectionView.axaml.cs",
            "Bounds.Width < 880",
            "Bounds.Height < 430"),
        Section(
            "WorkbenchOnlineMetadataSectionView.axaml.cs",
            "Bounds.Width < 880",
            "Bounds.Height < 620"),
        Section(
            "WorkbenchPendingChangesDrawerView.axaml.cs",
            "Bounds.Height < 430"),
        Section(
            "WorkbenchPlaylistsSectionView.axaml.cs",
            "Bounds.Width < 880",
            "Bounds.Height < 430"),
        Section(
            "WorkbenchReportsSectionView.axaml.cs",
            "Bounds.Width < 880",
            "Bounds.Height < 430",
            "Bounds.Height < 430"),
        Section(
            "WorkbenchSessionSectionView.axaml.cs",
            "Bounds.Height < 360"),
        Section(
            "WorkbenchShortcutsSectionView.axaml.cs",
            "Bounds.Width < 880",
            "Bounds.Height < 430"),
        Section(
            "WorkbenchToolsSectionView.axaml.cs",
            "Bounds.Width < 880",
            "Bounds.Height < 430"),
    ];

    [AvaloniaFact]
    public void Every_raw_adaptive_comparison_is_registered_to_executable_boundary_evidence()
    {
        string repositoryRoot =
            FindRepositoryRoot();
        BreakpointOccurrence[] expected =
        [
            .. Contracts
                .SelectMany(contract =>
                    contract.Expressions.Select(
                        expression =>
                            new BreakpointOccurrence(
                                contract.RelativePath,
                                expression)))
                .OrderBy(
                    occurrence =>
                        occurrence.RelativePath,
                    StringComparer.Ordinal)
                .ThenBy(
                    occurrence =>
                        occurrence.Expression,
                    StringComparer.Ordinal),
        ];
        BreakpointOccurrence[] actual =
        [
            .. EnumerateShippingComparisons(
                    repositoryRoot)
                .OrderBy(
                    occurrence =>
                        occurrence.RelativePath,
                    StringComparer.Ordinal)
                .ThenBy(
                    occurrence =>
                        occurrence.Expression,
                    StringComparer.Ordinal),
        ];

        Assert.Equal(expected, actual);
        foreach (BreakpointContract contract in
                 Contracts)
        {
            MethodInfo? evidence =
                contract.EvidenceType.GetMethod(
                    contract.EvidenceMethod,
                    BindingFlags.Instance |
                    BindingFlags.Public);
            Assert.NotNull(evidence);
            Assert.Contains(
                evidence.GetCustomAttributes(),
                attribute =>
                    attribute.GetType().Name.EndsWith(
                        "FactAttribute",
                        StringComparison.Ordinal));
        }
    }

    private static BreakpointContract Section(
        string fileName,
        params string[] expressions) =>
        new(
            "MusicLibraryManager/Views/WorkbenchSections/" +
            fileName,
            typeof(AdaptiveBreakpointCoverageTests),
            nameof(AdaptiveBreakpointCoverageTests
                .Workbench_shell_and_section_breakpoints_switch_at_minus_one_exact_and_plus_one),
            expressions);

    private static IEnumerable<BreakpointOccurrence>
        EnumerateShippingComparisons(
            string repositoryRoot)
    {
        string applicationRoot =
            Path.Combine(
                repositoryRoot,
                "MusicLibraryManager");
        IEnumerable<string> paths =
        [
            Path.Combine(
                applicationRoot,
                "MainWindow.axaml.cs"),
            .. Directory.EnumerateFiles(
                Path.Combine(
                    applicationRoot,
                    "Views"),
                "*.cs",
                SearchOption.AllDirectories),
        ];
        foreach (string path in paths)
        {
            string source =
                File.ReadAllText(path);
            string relativePath =
                Path.GetRelativePath(
                        repositoryRoot,
                        path)
                    .Replace(
                        Path.DirectorySeparatorChar,
                        '/');
            foreach (Match match in
                     NumericComparison.Matches(
                         source))
            {
                string left =
                    match.Groups["left"].Value;
                if (!left.Contains(
                        "width",
                        StringComparison
                            .OrdinalIgnoreCase) &&
                    !left.Contains(
                        "height",
                        StringComparison
                            .OrdinalIgnoreCase))
                    continue;
                yield return new(
                    relativePath,
                    $"{left} " +
                    $"{match.Groups["operator"].Value} " +
                    $"{match.Groups["value"].Value}");
            }
        }
    }

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

    private sealed record BreakpointContract(
        string RelativePath,
        Type EvidenceType,
        string EvidenceMethod,
        params string[] Expressions);

    private sealed record BreakpointOccurrence(
        string RelativePath,
        string Expression);
}
