using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

[Collection(LocalizationTestCollection.Name)]
public sealed class HealthLocalizationTests
{
    private static readonly string[] RuntimeFiles =
    [
        @"MusicLibraryManager.Presentation\Workflows\AnalyzerViewModel.cs",
        @"MusicLibraryManager.Presentation\Workflows\AnalysisRunViewModel.cs",
        @"MusicLibraryManager.Presentation\Workflows\ArtistGroupViewModel.cs",
        @"MusicLibraryManager.Presentation\Workflows\ArtworkRepairViewModels.cs",
        @"MusicLibraryManager\Views\HealthView.axaml",
        @"MusicLibraryManager\Views\HealthView.axaml.cs",
    ];

    [Fact]
    public void Health_runtime_resource_references_are_catalog_complete()
    {
        string root = FindRepositoryRoot();
        HashSet<string> resources = XDocument.Load(Path.Combine(
                root,
                "MusicLibraryManager.Presentation",
                "Resources",
                "Strings.resx"))
            .Root!
            .Elements("data")
            .Select(element => (string?)element.Attribute("name"))
            .Where(key => key is not null)
            .Select(key => key!)
            .ToHashSet(StringComparer.Ordinal);
        var errors = new List<string>();

        foreach (string relativePath in RuntimeFiles)
        {
            string source = File.ReadAllText(Path.Combine(root, relativePath));
            foreach (Match match in Regex.Matches(
                         source,
                         @"(?:""|ResourceKey=)(?<key>(?:Health|Column)\.[A-Za-z0-9_.]+)"))
            {
                string key = match.Groups["key"].Value;
                if (key.EndsWith(".", StringComparison.Ordinal))
                    continue;
                if (resources.Contains(key))
                    continue;
                if (resources.Contains(key + ".One") &&
                    resources.Contains(key + ".Other"))
                    continue;
                errors.Add($"{relativePath}: missing resource {key}");
            }
        }

        foreach (AnalysisFindingDisposition value in
                 Enum.GetValues<AnalysisFindingDisposition>())
            RequireResource(
                resources,
                $"Health.Choice.FindingDisposition.{value}",
                errors);
        foreach (AnalysisRepairDisposition value in
                 Enum.GetValues<AnalysisRepairDisposition>())
            RequireResource(
                resources,
                $"Health.Choice.RepairDisposition.{value}",
                errors);

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void Health_choices_and_run_kind_keep_stable_semantic_identity()
    {
        AnalysisFindingDisposition[] findingValues =
            HealthLocalizedChoices.AllFindingDispositions
                .Select(choice => choice.Value)
                .ToArray();
        AnalysisRepairDisposition[] repairValues =
            HealthLocalizedChoices.AllRepairDispositions
                .Select(choice => choice.Value)
                .ToArray();
        var localization = new SwitchingLocalizationService();
        HealthRunText text = new(
            AnalysisRunKind.ArtworkHealth,
            "Health.Run.Name.ArtworkHealth",
            "Health.Status.ArtworkHealth.None");
        AnalysisRunViewModel run = AnalysisRunViewModel.ForDuplicates(
            "legacy name",
            [],
            "legacy summary",
            text,
            localization);

        try
        {
            HealthLocalizedChoices.Refresh(key => $"first:{key}");
            string firstName = run.Name;
            localization.SetCulture("fr-FR");
            run.RefreshLocalizedText();
            HealthLocalizedChoices.Refresh(key => $"second:{key}");

            Assert.Equal(AnalysisRunKind.ArtworkHealth, run.Kind);
            Assert.NotEqual(firstName, run.Name);
            Assert.Equal(
                findingValues,
                HealthLocalizedChoices.AllFindingDispositions.Select(choice => choice.Value));
            Assert.Equal(
                repairValues,
                HealthLocalizedChoices.AllRepairDispositions.Select(choice => choice.Value));
            Assert.All(
                HealthLocalizedChoices.AllFindingDispositions,
                choice => Assert.StartsWith("second:", choice.Label, StringComparison.Ordinal));
            Assert.All(
                HealthLocalizedChoices.AllRepairDispositions,
                choice => Assert.StartsWith("second:", choice.Label, StringComparison.Ordinal));
        }
        finally
        {
            HealthLocalizedChoices.Refresh(LocalizedText.Get);
        }
    }

    [Fact]
    public void Health_view_exposes_each_localized_failure_diagnostic()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "MusicLibraryManager",
            "Views",
            "HealthView.axaml"));

        Assert.Contains("StatusDiagnosticDetail", source, StringComparison.Ordinal);
        Assert.Contains("ThumbnailDiagnosticDetail", source, StringComparison.Ordinal);
        Assert.Contains("ResultDiagnosticDetail", source, StringComparison.Ordinal);
        Assert.Contains(
            "ResourceKey=\"Common.DiagnosticDetailFormat\"",
            source,
            StringComparison.Ordinal);
    }

    private static void RequireResource(
        HashSet<string> resources,
        string key,
        List<string> errors)
    {
        if (!resources.Contains(key))
            errors.Add($"Missing choice resource {key}");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "MusicLibraryManager")) &&
                Directory.Exists(Path.Combine(
                    current.FullName,
                    "MusicLibraryManager.Presentation")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find the MusicLibraryTools repository root.");
    }

    private sealed class SwitchingLocalizationService : ILocalizationService
    {
        private CultureInfo _culture = CultureInfo.GetCultureInfo("en-US");

        public CultureInfo CurrentUICulture => _culture;
        public IReadOnlyList<CultureInfo> SupportedCultures { get; } =
        [
            CultureInfo.GetCultureInfo("en-US"),
            CultureInfo.GetCultureInfo("fr-FR"),
        ];
        public event EventHandler? CultureChanged;

        public string Get(string key) => $"{_culture.Name}:{key}";

        public string Format(string key, params object?[] arguments) => Get(key);

        public string FormatCount(
            string key,
            long count,
            params object?[] arguments) =>
            Get($"{key}.{(count == 1 ? "One" : "Other")}");

        public IReadOnlyDictionary<string, string> Snapshot() =>
            new Dictionary<string, string>();

        public void SetCulture(string cultureName)
        {
            _culture = CultureInfo.GetCultureInfo(cultureName);
            CultureChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
