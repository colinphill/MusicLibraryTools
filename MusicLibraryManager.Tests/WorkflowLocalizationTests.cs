using System.Globalization;
using System.Text.RegularExpressions;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

[Collection(LocalizationTestCollection.Name)]
public sealed class WorkflowLocalizationTests
{
    [Theory]
    [InlineData(
        "MusicLibraryManager.Presentation",
        "Workflows",
        "IngestViewModel.cs")]
    [InlineData(
        "MusicLibraryManager.Presentation",
        "Workflows",
        "OrganizeViewModel.cs")]
    [InlineData(
        "MusicLibraryManager.Presentation",
        "DevicesViewModel.cs",
        "")]
    public void Workflow_runtime_text_uses_localization(
        string first,
        string second,
        string third)
    {
        string source = File.ReadAllText(
            FindRepositoryFile(
                string.IsNullOrEmpty(third)
                    ? Path.Combine(first, second)
                    : Path.Combine(
                        first,
                        second,
                        third)));

        Assert.DoesNotMatch(
            @"(?:StatusText|HistoryStatus|DeviceEnumerationError)\s*=\s*\$?""[^""]*[A-Za-z]",
            source);
        Assert.DoesNotMatch(
            @"(?:PickFolderAsync|PickFileAsync|ConfirmAsync|ConfirmApplyAsync)\(\s*\$?""",
            source);
        Assert.DoesNotContain(
            "file(s)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "warning(s)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "root(s)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "error(s)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Ingest_filter_choices_refresh_in_place_without_changing_enum_values()
    {
        var localization =
            new SwitchingLocalizationService();
        var viewModel = new IngestViewModel(
            new StubIngest(),
            new StubIngestFiles(),
            new StubIngestDialogs(),
            new FakeSettings(),
            new FakeLibrary([]),
            localization: localization);
        LocalizedChoice<IngestPreviewFilter>[] choices =
            [.. viewModel.PreviewFilterChoices];
        IngestPreviewFilter[] values =
            choices.Select(choice => choice.Value)
                .ToArray();
        string[] labels =
            choices.Select(choice => choice.Label)
                .ToArray();

        localization.SetCulture("fr-FR");

        Assert.Equal(
            Enum.GetValues<IngestPreviewFilter>().Length,
            choices.Length);
        Assert.Equal(
            values,
            viewModel.PreviewFilterChoices.Select(
                choice => choice.Value));
        Assert.All(
            choices.Select(
                (choice, index) =>
                    (choice, index)),
            pair => Assert.Same(
                pair.choice,
                viewModel.PreviewFilterChoices[
                    pair.index]));
        Assert.All(
            choices.Select(
                (choice, index) =>
                    (choice, index)),
            pair => Assert.NotEqual(
                labels[pair.index],
                pair.choice.Label));
    }

    [Fact]
    public void Count_variants_and_device_identity_remain_semantic()
    {
        var localization =
            new SwitchingLocalizationService();
        var one = new IngestHistoryItemViewModel(
            Journal(1),
            localization);
        var many = new IngestHistoryItemViewModel(
            Journal(2),
            localization);
        var action = new DeviceSyncActionRow(
            new DeviceSyncAction(
                DeviceSyncMutationKind.UpdateFile,
                "Artist/Album/01.flac",
                "content hash differs",
                false,
                42,
                7),
            localization);
        action.SetStatus(
            OperationItemStatus.InProgress);
        var option =
            DeviceSelectionOption.FromDevice(
                new DeviceSyncDevice(
                    "Pixel|serial-1",
                    "serial-1",
                    "Pixel",
                    "device",
                    true,
                    Connection: "usb"),
                localization);
        string actionLabel = action.Kind;
        string optionState = option.State;

        Assert.Contains(".One", one.AffectedItems);
        Assert.Contains(".Other", many.AffectedItems);

        localization.SetCulture("fr-FR");
        action.RefreshLocalization();
        option.RefreshLocalization();

        Assert.Equal(
            DeviceSyncMutationKind.UpdateFile,
            action.KindValue);
        Assert.Equal(
            OperationItemStatus.InProgress,
            action.StatusValue);
        Assert.Equal(
            "content hash differs",
            action.DiagnosticDetail);
        Assert.NotEqual(actionLabel, action.Kind);
        Assert.Equal("Pixel|serial-1", option.Id);
        Assert.Equal("serial-1", option.Serial);
        Assert.Equal("device", option.StateValue);
        Assert.NotEqual(optionState, option.State);
    }

    [Fact]
    public void Workbench_editor_default_names_follow_culture_until_user_edits()
    {
        var localization =
            new SwitchingLocalizationService();
        var report =
            new ReportEditorViewModel(localization);
        var playlist =
            new PlaylistEditorViewModel(localization);
        var tool =
            new ExternalToolEditorViewModel(
                localization: localization);

        Assert.Equal(
            "en-US:Workbench.Reports.DefaultName",
            report.Name);
        Assert.Equal(
            "en-US:Workbench.Playlists.DefaultName",
            playlist.Name);
        Assert.Equal(
            "en-US:Workbench.Tools.DefaultName",
            tool.Name);

        localization.SetCulture("fr-FR");

        Assert.Equal(
            "fr-FR:Workbench.Reports.DefaultName",
            report.Name);
        Assert.Equal(
            "fr-FR:Workbench.Playlists.DefaultName",
            playlist.Name);
        Assert.Equal(
            "fr-FR:Workbench.Tools.DefaultName",
            tool.Name);

        report.Name = "Quarterly archive";
        playlist.Name = "Road trip";
        tool.Name = "Waveform renderer";
        localization.SetCulture("de-DE");

        Assert.Equal("Quarterly archive", report.Name);
        Assert.Equal("Road trip", playlist.Name);
        Assert.Equal("Waveform renderer", tool.Name);

        tool.NewToolCommand.Execute(null);
        Assert.Equal(
            "de-DE:Workbench.Tools.DefaultName",
            tool.Name);
        localization.SetCulture("ja-JP");
        Assert.Equal(
            "ja-JP:Workbench.Tools.DefaultName",
            tool.Name);
        Assert.Equal("Quarterly archive", report.Name);
        Assert.Equal("Road trip", playlist.Name);
    }

    private static OperationJournalSummary Journal(
        int affectedItems) =>
        new(
            "Ingest",
            OperationJournalKind.Ingest,
            OperationJournalState.Completed,
            "run",
            null,
            DateTimeOffset.UtcNow,
            affectedItems);

    private static string FindRepositoryFile(
        string relativePath)
    {
        DirectoryInfo? directory =
            new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate =
                Path.Combine(
                    directory.FullName,
                    relativePath);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file '{relativePath}'.");
    }

    private sealed class SwitchingLocalizationService
        : ILocalizationService
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
            Get(key);

        public string FormatCount(
            string key,
            long count,
            params object?[] arguments) =>
            $"{Get(
                $"{key}.{(
                    count == 1
                        ? "One"
                        : "Other")}")}:{count}";

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

    private sealed class StubIngest
        : IIngestMusicService
    {
        public Task<IngestPlan> PreviewAsync(
            IngestRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IngestResult> ApplyAsync(
            IngestPlan plan,
            IReadOnlyList<IngestApprovalDecision> approvals,
            IProgress<IngestProgress>? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubIngestFiles
        : IFileDialogService
    {
        public Task<string?> PickOpenFileAsync(
            string title,
            IReadOnlyList<FilePickerFilter>? filters = null) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickFolderAsync(
            string title) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(
            string title,
            string? suggestedName = null,
            string? defaultExtension = null,
            IReadOnlyList<FilePickerFilter>? filters = null) =>
            Task.FromResult<string?>(null);
    }

    private sealed class StubIngestDialogs
        : IDialogService
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
}
