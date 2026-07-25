using System.Collections.Immutable;
using System.Globalization;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class TranscodeEditorViewModelTests
{
    [Fact]
    public async Task OpenCapturesSelectionAndPreviewSendsImmutablePlanToPendingCoordinator()
    {
        string first = Path.GetFullPath("first.flac");
        string second = Path.GetFullPath("second.flac");
        var service = new RecordingTranscodeService();
        var pending = new RecordingPendingCoordinator();
        var editor = new TranscodeEditorViewModel(
            service,
            new CapabilityService(),
            new PresetStore(),
            new Scheduler(),
            new FilePicker(),
            new Dialogs(),
            pending);

        await editor.OpenAsync(
            [second, first, first],
            TestContext.Current.CancellationToken);
        await editor.PreviewCommand.ExecuteAsync(null);

        Assert.Equal(2, service.Request!.SourcePaths.Length);
        Assert.Equal(
            [second, first],
            service.Request.SourcePaths);
        Assert.Equal(
            AudioTranscodeFormatIds.Flac,
            service.Request.Settings.FormatId);
        Assert.NotNull(pending.Plan);
        Assert.Equal(2, pending.Plan.Items.Length);
        Assert.Equal(2, pending.Plan.Request.SourcePaths.Length);
    }

    [Fact]
    public async Task HardwareConcurrencyIsSavedSeparatelyFromPreset()
    {
        var scheduler = new Scheduler();
        var presets = new PresetStore();
        var editor = new TranscodeEditorViewModel(
            new RecordingTranscodeService(),
            new CapabilityService(),
            presets,
            scheduler,
            new FilePicker(),
            new Dialogs(),
            new RecordingPendingCoordinator());
        await editor.OpenAsync(
            [Path.GetFullPath("one.flac")],
            TestContext.Current.CancellationToken);
        editor.AutomaticConcurrency = false;
        editor.MaximumConcurrentProcesses = 3;
        editor.PresetName = "Archive";

        editor.SavePresetCommand.Execute(null);

        AudioTranscodePreset preset = Assert.Single(presets.Values);
        Assert.Equal("Archive", preset.Name);
        Assert.False(scheduler.Settings.Automatic);
        Assert.Equal(3, scheduler.Settings.MaximumProcesses);
        Assert.DoesNotContain(
            "Concurrency",
            System.Text.Json.JsonSerializer.Serialize(preset),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpeningUsesCachedCapabilitiesAndExplicitRefreshForcesProbe()
    {
        var capabilities = new CapabilityService();
        var editor = new TranscodeEditorViewModel(
            new RecordingTranscodeService(),
            capabilities,
            new PresetStore(),
            new Scheduler(),
            new FilePicker(),
            new Dialogs(),
            new RecordingPendingCoordinator());

        await editor.OpenAsync(
            [Path.GetFullPath("one.flac")],
            TestContext.Current.CancellationToken);
        await editor.RefreshCapabilitiesCommand.ExecuteAsync(null);

        Assert.Equal([false, true], capabilities.ForceRefreshValues);
    }

    [Fact]
    public async Task PreviewLocalizesIssueSummaryAndRetainsRawDiagnostic()
    {
        var service = new RecordingTranscodeService
        {
            Issue = new(
                "transcode.source-decoder-unavailable",
                OperationIssueSeverity.Blocker,
                "raw decoder diagnostic"),
        };
        var editor = new TranscodeEditorViewModel(
            service,
            new CapabilityService(),
            new PresetStore(),
            new Scheduler(),
            new FilePicker(),
            new Dialogs(),
            new RecordingPendingCoordinator());
        await editor.OpenAsync(
            [Path.GetFullPath("one.flac")],
            TestContext.Current.CancellationToken);

        await editor.PreviewCommand.ExecuteAsync(null);

        Assert.Contains(
            "configured tools",
            Assert.Single(editor.Issues),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "raw decoder diagnostic",
            editor.StatusDiagnosticDetail);
    }

    [Fact]
    public async Task UnavailablePresetRemainsVisibleWithoutReplacingStableIds()
    {
        var presets = new PresetStore();
        var unavailable = new AudioTranscodePreset(
            Guid.NewGuid(),
            "Unavailable",
            new(
                "future.codec",
                "future:encoder",
                AudioTranscodeRateMode.HybridQuality,
                Quality: 3),
            true,
            true,
            true,
            "{Name}{Extension}",
            AudioTranscodeCollisionPolicy.Stop,
            DateTimeOffset.UtcNow);
        presets.Values.Add(unavailable);
        var editor = new TranscodeEditorViewModel(
            new RecordingTranscodeService(),
            new CapabilityService(),
            presets,
            new Scheduler(),
            new FilePicker(),
            new Dialogs(),
            new RecordingPendingCoordinator());
        await editor.OpenAsync(
            [Path.GetFullPath("one.flac")],
            TestContext.Current.CancellationToken);

        editor.SelectedPreset = unavailable;
        await editor.RefreshCapabilitiesCommand.ExecuteAsync(null);

        Assert.Equal(
            "future.codec",
            editor.SelectedFormatId);
        Assert.Equal(
            "future:encoder",
            editor.SelectedEncoderId);
        Assert.Equal(
            AudioTranscodeRateMode.HybridQuality,
            editor.SelectedRateMode);
        Assert.Contains(
            editor.FormatChoices,
            choice => choice.Value == "future.codec");
        Assert.Contains(
            editor.EncoderChoices,
            choice => choice.Value == "future:encoder");
    }

    [Fact]
    public async Task LiveCultureChangePreservesOpenTranscodeStateAndCapabilityIdentity()
    {
        var localization =
            new SwitchingLocalizationService();
        var presets = new PresetStore();
        var preset = new AudioTranscodePreset(
            Guid.NewGuid(),
            "Archive",
            new(
                AudioTranscodeFormatIds.Flac,
                AudioTranscodeEncoderIds.Ffmpeg("flac"),
                AudioTranscodeRateMode.Lossless,
                SampleRateHz: 48_000,
                BitsPerSample: 24),
            true,
            true,
            false,
            "{Name}-{Codec}{Extension}",
            AudioTranscodeCollisionPolicy.Suffix,
            DateTimeOffset.UtcNow);
        presets.Values.Add(preset);
        var transcodes =
            new RecordingTranscodeService();
        var editor = new TranscodeEditorViewModel(
            transcodes,
            new CapabilityService(),
            presets,
            new Scheduler(),
            new FilePicker(),
            new Dialogs(),
            new RecordingPendingCoordinator(),
            localization);
        string first = Path.GetFullPath("one.flac");
        string second = Path.GetFullPath("two.flac");
        await editor.OpenAsync(
            [first, second],
            TestContext.Current.CancellationToken);
        editor.SelectedPreset = preset;
        editor.SelectedFormatId =
            AudioTranscodeFormatIds.Flac;
        editor.SelectedEncoderId =
            AudioTranscodeEncoderIds.Ffmpeg("flac");
        editor.SelectedRateMode =
            AudioTranscodeRateMode.Lossless;
        editor.SelectedSampleRate = 48_000;
        editor.SelectedBitDepth = 24;
        string formatLabel =
            Assert.Single(editor.FormatChoices).Label;

        localization.SetCulture("fr-FR");
        await editor.PreviewCommand.ExecuteAsync(null);

        Assert.Equal(
            [first, second],
            transcodes.Request!.SourcePaths);
        Assert.Same(preset, editor.SelectedPreset);
        Assert.Equal(
            AudioTranscodeFormatIds.Flac,
            editor.SelectedFormatId);
        Assert.Equal(
            AudioTranscodeEncoderIds.Ffmpeg("flac"),
            editor.SelectedEncoderId);
        Assert.Equal(
            AudioTranscodeRateMode.Lossless,
            editor.SelectedRateMode);
        Assert.Equal(48_000, editor.SelectedSampleRate);
        Assert.Equal(24, editor.SelectedBitDepth);
        Assert.NotEqual(
            formatLabel,
            Assert.Single(editor.FormatChoices).Label);
        Assert.StartsWith(
            "fr-FR:",
            Assert.Single(editor.FormatChoices).Label,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkbenchAccumulatesDisjointPlansAndReplacesOnlyOverlappingSources()
    {
        var dialogs = new TrackingDialogs();
        WorkbenchViewModel workbench =
            CreateWorkbench(dialogs);
        AudioTranscodePlan first = Plan("a", "b");
        AudioTranscodePlan disjoint = Plan("c");
        AudioTranscodePlan replacement = Plan("b");

        Assert.True(
            await workbench.AddPendingTranscodeAsync(
                first,
                TestContext.Current.CancellationToken));
        Assert.True(
            await workbench.AddPendingTranscodeAsync(
                disjoint,
                TestContext.Current.CancellationToken));
        Assert.Equal(3, workbench.PendingChanges.Count);

        Assert.True(
            await workbench.AddPendingTranscodeAsync(
                replacement,
                TestContext.Current.CancellationToken));

        Assert.Equal(1, dialogs.Confirmations);
        Assert.Equal(3, workbench.PendingChanges.Count);
        Assert.Equal(
            3,
            workbench.PendingChanges
                .Select(row => row.File)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.True(workbench.HasUnsavedChanges);
        Assert.True(workbench.HasApplicablePreview);
    }

    [Fact]
    public async Task WorkbenchLeavesExistingIntentWhenOverlapReplacementIsDeclined()
    {
        var dialogs = new TrackingDialogs
        {
            ConfirmationResult = false,
        };
        WorkbenchViewModel workbench =
            CreateWorkbench(dialogs);
        AudioTranscodePlan original = Plan("a", "b");
        await workbench.AddPendingTranscodeAsync(
            original,
            TestContext.Current.CancellationToken);

        bool added = await workbench.AddPendingTranscodeAsync(
            Plan("b"),
            TestContext.Current.CancellationToken);

        Assert.False(added);
        Assert.Equal(1, dialogs.Confirmations);
        Assert.Equal(2, workbench.PendingChanges.Count);
    }

    [Fact]
    public async Task WorkbenchAppliesReadyTranscodesAndRetainsFailedIntent()
    {
        var dialogs = new TrackingDialogs();
        var transcodes = new RecordingTranscodeService
        {
            FailedSourceNames =
                new HashSet<string>(
                    ["b.flac"],
                    StringComparer.OrdinalIgnoreCase),
        };
        WorkbenchViewModel workbench =
            CreateWorkbench(dialogs, transcodes);
        await workbench.AddPendingTranscodeAsync(
            Plan("a", "b"),
            TestContext.Current.CancellationToken);

        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.Confirmations);
        Assert.Equal(1, transcodes.ApplyCalls);
        Assert.Single(transcodes.AppliedItemIds);
        Assert.Single(workbench.PendingChanges);
        Assert.EndsWith(
            "b.flac",
            workbench.PendingChanges[0].File,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkbenchBackFromPartialStageKeepsAllPendingIntent()
    {
        var dialogs = new TrackingDialogs
        {
            ConfirmationResult = false,
        };
        var transcodes = new RecordingTranscodeService
        {
            FailedSourceNames =
                new HashSet<string>(
                    ["b.flac"],
                    StringComparer.OrdinalIgnoreCase),
        };
        WorkbenchViewModel workbench =
            CreateWorkbench(dialogs, transcodes);
        await workbench.AddPendingTranscodeAsync(
            Plan("a", "b"),
            TestContext.Current.CancellationToken);

        await workbench.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.Confirmations);
        Assert.Equal(0, transcodes.ApplyCalls);
        Assert.Equal(1, transcodes.DiscardCalls);
        Assert.Equal(2, workbench.PendingChanges.Count);
    }

    private static WorkbenchViewModel CreateWorkbench(
        IDialogCoordinator dialogs,
        IAudioTranscodeService? transcodes = null)
    {
        var settings = new FakeSettings();
        var journals = new OperationJournalService(
            new FileMutationCoordinator());
        return new(
            new EmptyWorkbenchService(),
            new FakeMetadataOperationService(),
            new MetadataOperationCatalog(),
            new OperationRecipeStore(settings),
            new FakeAcoustIdDiscoveryService(),
            new FakeMusicBrainzMetadataProvider(),
            new MusicBrainzReleaseMappingService(),
            new FakeCoverArtArchiveProvider(),
            new FakeThumbnails(),
            new EditHistoryService(settings, journals),
            new FakeFilePicker(),
            dialogs,
            settings,
            transcodes: transcodes);
    }

    private static AudioTranscodePlan Plan(
        params string[] names)
    {
        string[] paths =
        [
            .. names.Select(name =>
                Path.GetFullPath(name + ".flac")),
        ];
        var settings = new AudioTranscodeSettings(
            AudioTranscodeFormatIds.Flac,
            AudioTranscodeEncoderIds.Automatic,
            AudioTranscodeRateMode.Lossless);
        var request = new AudioTranscodeRequest(
            [.. paths],
            settings,
            new(
                AudioTranscodeDestinationMode.Alongside,
                null,
                true,
                "{Name}{Extension}",
                AudioTranscodeCollisionPolicy.Stop));
        return new(
            Guid.NewGuid(),
            request,
            [
                .. paths.Select(path =>
                    new AudioTranscodePlanItem(
                        Guid.NewGuid(),
                        path,
                        Path.ChangeExtension(
                            path,
                            ".output.flac"),
                        OperationPathSnapshot.Missing(path),
                        OperationPathSnapshot.Missing(
                            Path.ChangeExtension(
                                path,
                                ".output.flac")),
                        "",
                        settings,
                        [])),
            ],
            [],
            DateTimeOffset.UtcNow,
            1);
    }

    private sealed class RecordingPendingCoordinator :
        IWorkbenchPendingChangeCoordinator
    {
        public AudioTranscodePlan? Plan { get; private set; }

        public Task<bool> AddPendingMutationAsync(
            ReviewedMediaMutationIntent intent,
            CancellationToken ct = default)
        {
            if (intent is not ReviewedTranscodeMutationIntent transcode)
                return Task.FromResult(false);

            Plan = transcode.Plan;
            return Task.FromResult(true);
        }

        public Task<bool> AddPendingTranscodeAsync(
            AudioTranscodePlan plan,
            CancellationToken ct = default)
        {
            Plan = plan;
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingTranscodeService :
        IAudioTranscodeService
    {
        public AudioTranscodeRequest? Request { get; private set; }
        public OperationIssue? Issue { get; init; }
        public IReadOnlySet<string> FailedSourceNames { get; init; } =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
        public int ApplyCalls { get; private set; }
        public int DiscardCalls { get; private set; }
        public IReadOnlySet<Guid> AppliedItemIds { get; private set; } =
            new HashSet<Guid>();

        public Task<AudioTranscodePlan> PreviewAsync(
            AudioTranscodeRequest request,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(new AudioTranscodePlan(
                Guid.NewGuid(),
                request,
                [
                    .. request.SourcePaths.Select(path =>
                        new AudioTranscodePlanItem(
                            Guid.NewGuid(),
                            path,
                            Path.ChangeExtension(path, ".flac"),
                            OperationPathSnapshot.Missing(path),
                            OperationPathSnapshot.Missing(
                                Path.ChangeExtension(path, ".flac")),
                            "",
                            request.Settings,
                            Issue is null ? [] : [Issue])),
                ],
                [],
                DateTimeOffset.UtcNow,
                1));
        }

        public Task<AudioTranscodeStageResult> StageAsync(
            AudioTranscodePlan plan,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) =>
            Task.FromResult(
                new AudioTranscodeStageResult(
                    plan,
                    [
                        .. plan.Items.Select(item =>
                            new AudioTranscodeStagedItem(
                                item,
                                FailedSourceNames.Contains(
                                    Path.GetFileName(
                                        item.SourcePath))
                                    ? AudioTranscodeStageState
                                        .Failed
                                    : AudioTranscodeStageState
                                        .Ready,
                                item.DestinationPath + ".stage",
                                "hash",
                                1,
                                FailedSourceNames.Contains(
                                    Path.GetFileName(
                                        item.SourcePath))
                                    ? "test-failure"
                                    : null)),
                    ]));

        public Task<AudioTranscodeStageResult>
            StageWithSourceOverridesAsync(
                AudioTranscodePlan plan,
                IReadOnlyDictionary<string, string>
                    sourceOverrides,
                IProgress<OperationProgress>? progress = null,
                CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AudioTranscodeApplyResult> ApplyAsync(
            AudioTranscodeStageResult stage,
            IReadOnlySet<Guid>? readyItemIds = null,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AudioTranscodeApplyResult> ApplyBatchAsync(
            IReadOnlyList<AudioTranscodeStageResult> stages,
            IReadOnlySet<Guid>? readyItemIds = null,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            ApplyCalls++;
            AppliedItemIds =
                readyItemIds?.ToHashSet() ??
                new HashSet<Guid>();
            return Task.FromResult(
                new AudioTranscodeApplyResult(
                    AppliedItemIds.Count,
                    [],
                    [],
                    [],
                    []));
        }

        public Task DiscardStageAsync(
            AudioTranscodeStageResult stage,
            CancellationToken ct = default)
        {
            DiscardCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class CapabilityService :
        IAudioTranscodeCapabilityService
    {
        public List<bool> ForceRefreshValues { get; } = [];

        public Task<AudioTranscodeCapabilitySnapshot> GetAsync(
            bool forceRefresh = false,
            CancellationToken ct = default)
        {
            ForceRefreshValues.Add(forceRefresh);
            return Task.FromResult(new AudioTranscodeCapabilitySnapshot(
                [],
                [
                    new(
                        AudioTranscodeFormatIds.Flac,
                        "flac",
                        "flac",
                        ".flac",
                        true,
                        [AudioTranscodeEncoderIds.Ffmpeg("flac")]),
                ],
                [
                    new(
                        AudioTranscodeEncoderIds.Ffmpeg("flac"),
                        AudioTranscodeToolKind.Ffmpeg,
                        "flac",
                        AudioEncoderThreadingMode.ThreadCountControllable,
                        [new(AudioTranscodeRateMode.Lossless)],
                        [],
                        [16, 24]),
                ],
                DateTimeOffset.UtcNow,
                1));
        }

        public void Invalidate()
        {
        }
    }

    private sealed class PresetStore : ITranscodePresetStore
    {
        public List<AudioTranscodePreset> Values { get; } = [];
        public IReadOnlyList<AudioTranscodePreset> Load() => Values;
        public AudioTranscodePreset Save(AudioTranscodePreset preset)
        {
            Values.RemoveAll(item => item.Id == preset.Id);
            Values.Add(preset);
            return preset;
        }
        public bool Delete(Guid id) =>
            Values.RemoveAll(item => item.Id == id) > 0;
    }

    private sealed class Scheduler : ITranscodeWorkScheduler
    {
        public TranscodeConcurrencySettings Settings { get; private set; } =
            new(true, 1);

        public void SaveSettings(TranscodeConcurrencySettings settings) =>
            Settings = settings;

        public TranscodeWorkerContext GetWorkerContext(
            int itemCount,
            IReadOnlyCollection<AudioEncoderThreadingMode> threadingModes) =>
            new(1, 1, 1);

        public Task<IReadOnlyList<TranscodeWorkResult<T>>> RunAsync<T>(
            IReadOnlyList<TranscodeWorkItem<T>> items,
            Func<T, int, CancellationToken, Task> action,
            Func<T, string>? describe = null,
            IProgress<TranscodeSchedulerProgress>? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FilePicker : IFilePickerService
    {
        public Task<string?> PickFileAsync(
            string title,
            IReadOnlyList<FilePickerType>? types = null) =>
            Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string title) =>
            Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(
            string title,
            string suggestedName,
            string extension) =>
            Task.FromResult<string?>(null);
    }

    private sealed class Dialogs : IDialogCoordinator
    {
        public Task<bool> ConfirmAsync(
            string title,
            string message,
            string primaryText) =>
            Task.FromResult(true);
        public Task ShowMessageAsync(
            string title,
            string message) =>
            Task.CompletedTask;
    }

    private sealed class TrackingDialogs :
        IDialogCoordinator
    {
        public bool ConfirmationResult { get; init; } = true;
        public int Confirmations { get; private set; }

        public Task<bool> ConfirmAsync(
            string title,
            string message,
            string primaryText)
        {
            Confirmations++;
            return Task.FromResult(
                ConfirmationResult);
        }

        public Task ShowMessageAsync(
            string title,
            string message) =>
            Task.CompletedTask;
    }

    private sealed class SwitchingLocalizationService :
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
            Get(key) + ":" +
            string.Join("|", arguments);

        public string FormatCount(
            string key,
            long count,
            params object?[] arguments) =>
            Format(key, [count, .. arguments]);

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

    private sealed class EmptyWorkbenchService :
        IWorkbenchService
    {
        public Task<WorkbenchLoadResult> LoadAsync(
            WorkbenchLoadRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(
                new WorkbenchLoadResult([], []));
    }
}
