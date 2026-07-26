using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using iTunes.Binary;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

[Collection(LocalizationTestCollection.Name)]
public sealed class OperationsLocalizationTests
{
    [Fact]
    public void Operations_runtime_resources_and_count_variants_are_complete()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "MusicLibraryManager.Presentation",
            "Workflows",
            "OperationsViewModel.cs"));
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
        var invariantPreferenceKeys = new HashSet<string>(
            StringComparer.Ordinal)
        {
            "Operations.SearchRoot",
            "Operations.RetentionDays",
            "Operations.JobHistory",
        };

        foreach (Match match in Regex.Matches(
                     source,
                     "\"(?<key>Operations\\.[A-Za-z0-9_.]+)\""))
        {
            string key = match.Groups["key"].Value;
            if (invariantPreferenceKeys.Contains(key) ||
                key.EndsWith(".", StringComparison.Ordinal))
                continue;
            if (!resources.Contains(key) &&
                !(resources.Contains(key + ".One") &&
                  resources.Contains(key + ".Other")))
                errors.Add($"Missing Operations resource {key}");
        }

        foreach (Match match in Regex.Matches(
                     source,
                     @"(?s)(?:LC|SetCountStatus|SetCountJobStatus|SetCountRestorePreview)\s*\(\s*""(?<key>Operations\.[A-Za-z0-9_.]+)"""))
        {
            string key = match.Groups["key"].Value;
            if (!resources.Contains(key + ".One"))
                errors.Add($"Missing singular resource {key}.One");
            if (!resources.Contains(key + ".Other"))
                errors.Add($"Missing plural resource {key}.Other");
        }

        RequireEnumResources<OperationPhase>(
            resources,
            "Operations.Progress.Phase.",
            errors);
        RequireEnumResources<OperationIssueSeverity>(
            resources,
            "Operations.Choice.OperationIssueSeverity.",
            errors);
        RequireEnumResources<FileMutationKind>(
            resources,
            "Operations.Choice.FileMutationKind.",
            errors);
        RequireEnumResources<OperationJournalKind>(
            resources,
            "Operations.Choice.OperationJournalKind.",
            errors,
            value => value ==
                OperationJournalKind.Other
                    ? "OtherKind"
                    : value.ToString());
        RequireEnumResources<OperationJournalState>(
            resources,
            "Operations.Choice.OperationJournalState.",
            errors);
        RequireEnumResources<ItlValidationSeverity>(
            resources,
            "Operations.Choice.ItlValidationSeverity.",
            errors);

        Assert.True(
            errors.Count == 0,
            string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void Operations_runtime_has_no_literal_status_or_dialog_sinks()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "MusicLibraryManager.Presentation",
            "Workflows",
            "OperationsViewModel.cs"));
        MatchCollection literalSinks = Regex.Matches(
            source,
            @"(?ms)(?:StatusText|JobStatus|RestorePreviewText|PurgePreviewText)\s*=\s*\$?""(?<value>[^""]+)""|(?:PickFolderAsync|PickOpenFileAsync|ConfirmApplyAsync)\s*\(\s*\$?""(?<value>[^""]+)""");

        Assert.Empty(literalSinks.Cast<Match>());
        Assert.DoesNotContain("ex.Message}", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Operations_recovery_copy_keeps_scope_and_consequences_explicit()
    {
        Dictionary<string, string> resources = XDocument.Load(Path.Combine(
                FindRepositoryRoot(),
                "MusicLibraryManager.Presentation",
                "Resources",
                "Strings.resx"))
            .Root!
            .Elements("data")
            .ToDictionary(
                element =>
                    (string?)element.Attribute("name") ?? "",
                element =>
                    element.Element("value")?.Value ?? "",
                StringComparer.Ordinal);

        Assert.Equal(
            "Restore selected items",
            resources["Operations.Action.ApplyRestore"]);
        Assert.Equal(
            "Purge eligible recovery history",
            resources["Operations.PurgeReviewed"]);
        Assert.Equal(
            "Preview recovery-history purge",
            resources["Operations.PreviewRetentionPurge"]);
        Assert.Equal(
            "Purge failed.",
            resources["Operations.Status.PurgeFailed"]);
        Assert.Equal(
            "An iTunes Library.itl path is required.",
            resources["Operations.Validation.ItunesLibraryRequired"]);
        Assert.Equal(
            "Plan: library tracks: {0:N0}; to install: {1:N0}; " +
            "unchanged: {2:N0}; to remove: {3:N0}; playlists: {4:N0}.",
            resources["Operations.Output.CarCard.Plan.One"]);
    }

    [Fact]
    public void Culture_refresh_keeps_job_and_recovery_semantic_identity()
    {
        using var temp = new TempDirectory();
        var localization = new SwitchingLocalizationService();
        UnifiedJobDescriptor descriptor = new(
            "smart-storage",
            "Smart storage",
            "Description",
            UnifiedJobApplyMode.ApplyFlag,
            [],
            "",
            0);
        var viewModel = new OperationsViewModel(
            new StubJournals(),
            new StubFiles(),
            new StubDialogs(),
            new AppSettings(Path.Combine(temp.Path, "settings.json")),
            jobs: new StubJobs(descriptor),
            localization: localization)
        {
            SelectedJob = descriptor,
            JobDestinationPath = @"D:\Music",
        };
        UnifiedJobChoiceViewModel selectedChoice =
            Assert.Single(viewModel.JobChoices);
        string firstName = selectedChoice.Name;
        OperationJournalSummary summary = new(
            "IngestMusic",
            OperationJournalKind.Ingest,
            OperationJournalState.Interrupted,
            @"C:\run",
            null,
            DateTimeOffset.UtcNow,
            1);
        var run = new OperationRunViewModel(summary, localization);
        viewModel.Runs.Add(run);
        string firstRunState = run.State;

        localization.SetCulture("fr-FR");

        Assert.Equal("smart-storage", viewModel.SelectedJob!.Id);
        Assert.Same(selectedChoice, viewModel.SelectedJobChoice);
        Assert.Equal(@"D:\Music", viewModel.JobDestinationPath);
        Assert.Same(summary, run.Summary);
        Assert.NotEqual(firstName, selectedChoice.Name);
        Assert.NotEqual(firstRunState, run.State);
        Assert.StartsWith("fr-FR:", selectedChoice.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task External_failure_uses_localized_summary_and_separate_diagnostic()
    {
        using var temp = new TempDirectory();
        var localization = new SwitchingLocalizationService();
        UnifiedJobDescriptor descriptor = new(
            "cross-library-sync",
            "Cross-library sync",
            "Description",
            UnifiedJobApplyMode.ApplyFlag,
            [],
            "",
            0);
        var viewModel = new OperationsViewModel(
            new StubJournals(),
            new StubFiles(),
            new StubDialogs(),
            new AppSettings(Path.Combine(temp.Path, "settings.json")),
            jobs: new StubJobs(descriptor),
            crossLibrarySync: new FailingCrossLibrarySync(),
            localization: localization)
        {
            SelectedJob = descriptor,
        };

        await viewModel.PreviewJobCommand.ExecuteAsync(null);

        Assert.Equal(
            "en-US:Operations.Job.Status.PreviewFailed",
            viewModel.JobStatus);
        Assert.Equal("provider boom", viewModel.JobStatusDiagnosticDetail);
        Assert.True(viewModel.HasJobStatusDiagnosticDetail);

        string xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "MusicLibraryManager",
            "Views",
            "OperationsView.axaml"));
        Assert.Contains("JobStatusDiagnosticDetail", xaml, StringComparison.Ordinal);
        Assert.Contains("StatusDiagnosticDetail", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "ResourceKey=\"Common.DiagnosticDetailFormat\"",
            xaml,
            StringComparison.Ordinal);
    }

    private static void RequireEnumResources<T>(
        HashSet<string> resources,
        string prefix,
        List<string> errors,
        Func<T, string>? keySuffix = null)
        where T : struct, Enum
    {
        foreach (T value in Enum.GetValues<T>())
        {
            string key = prefix +
                (keySuffix?.Invoke(value) ??
                 value.ToString());
            if (!resources.Contains(key))
                errors.Add($"Missing enum resource {key}");
        }
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

    private sealed class StubJobs(
        params UnifiedJobDescriptor[] jobs) : IUnifiedJobService
    {
        public IReadOnlyList<UnifiedJobDescriptor> Catalog { get; } = jobs;
    }

    private sealed class StubJournals : IOperationJournalService
    {
        public Task<OperationJournalDiscoveryResult> DiscoverAsync(
            IReadOnlyList<string> searchRoots,
            CancellationToken ct = default) =>
            Task.FromResult(
                new OperationJournalDiscoveryResult([], []));

        public Task<OperationBrowseResult> BrowseAsync(
            OperationJournalSummary run,
            CancellationToken ct = default) =>
            Task.FromResult(
                new OperationBrowseResult(run.RunPath, [], []));

        public Task<OperationRestorePlan> PreviewRestoreAsync(
            OperationJournalSummary run,
            IReadOnlyList<OperationFileEntry> entries,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<OperationRestoreResult> ApplyRestoreAsync(
            OperationRestorePlan plan,
            IProgress<int>? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<OperationPurgePlan> PreviewPurgeAsync(
            IReadOnlyList<OperationJournalSummary> runs,
            int retentionDays,
            DateTimeOffset? nowUtc = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<OperationPurgeResult> ApplyPurgeAsync(
            OperationPurgePlan plan,
            IProgress<int>? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubFiles : IFileDialogService
    {
        public Task<string?> PickOpenFileAsync(
            string title,
            IReadOnlyList<FilePickerFilter>? filters = null) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickFolderAsync(string title) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(
            string title,
            string? suggestedName = null,
            string? defaultExtension = null,
            IReadOnlyList<FilePickerFilter>? filters = null) =>
            Task.FromResult<string?>(null);
    }

    private sealed class StubDialogs : IDialogService
    {
        public Task<bool> ShowFieldsEditorAsync(
            IReadOnlyList<string> paths) =>
            Task.FromResult(false);

        public Task<string?> ShowConfigEditorAsync(
            string? existingPath) =>
            Task.FromResult<string?>(null);

        public Task<bool> ConfirmApplyAsync(
            string title,
            string message,
            string primaryText) =>
            Task.FromResult(false);

        public Task<bool> ConfirmCdDerivationAsync(
            IngestApprovalItem item) =>
            Task.FromResult(false);

        public Task<bool> ConfirmRestoreAsync(
            OperationRestorePlan plan) =>
            Task.FromResult(false);

        public Task<bool> ConfirmPurgeAsync(
            OperationPurgePlan plan) =>
            Task.FromResult(false);
    }

    private sealed class FailingCrossLibrarySync : ICrossLibrarySyncService
    {
        public Task<CrossLibrarySyncPlan> PreviewAsync(
            CrossLibrarySyncRequest request,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("provider boom");

        public Task<CrossLibrarySyncResult> ApplyAsync(
            CrossLibrarySyncPlan plan,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "operations-localization-" +
            Guid.NewGuid().ToString("N"));

        public TempDirectory() =>
            Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
