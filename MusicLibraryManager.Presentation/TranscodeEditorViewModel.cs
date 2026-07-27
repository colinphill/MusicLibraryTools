using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

public sealed partial class TranscodeEditorViewModel :
    ObservableObject
{
    private readonly IAudioTranscodeService _transcodes;
    private readonly IAudioTranscodeCapabilityService _capabilities;
    private readonly ITranscodePresetStore _presets;
    private readonly ITranscodeWorkScheduler _scheduler;
    private readonly IFilePickerService _files;
    private readonly IDialogCoordinator _dialogs;
    private readonly IWorkbenchPendingChangeCoordinator _pending;
    private readonly ILocalizationService? _localization;
    private AudioTranscodeCapabilitySnapshot? _capabilitySnapshot;
    private ImmutableArray<string> _capturedPaths = [];
    private bool _loadingSettings;
    private bool _refreshingCapabilityChoices;

    public TranscodeEditorViewModel(
        IAudioTranscodeService transcodes,
        IAudioTranscodeCapabilityService capabilities,
        ITranscodePresetStore presets,
        ITranscodeWorkScheduler scheduler,
        IFilePickerService files,
        IDialogCoordinator dialogs,
        IWorkbenchPendingChangeCoordinator pending,
        ILocalizationService? localization = null)
    {
        _transcodes = transcodes;
        _capabilities = capabilities;
        _presets = presets;
        _scheduler = scheduler;
        _files = files;
        _dialogs = dialogs;
        _pending = pending;
        _localization = localization;
        RefreshStaticChoices();
        RefreshPresets();
        AutomaticConcurrency = scheduler.Settings.Automatic;
        MaximumConcurrentProcesses =
            scheduler.Settings.MaximumProcesses;
        if (_localization is not null)
            _localization.CultureChanged += OnCultureChanged;
    }

    public event EventHandler? PreviewCompleted;

    public ObservableCollection<LocalizedChoice<string>>
        FormatChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<string>>
        EncoderChoices { get; } = [];
    public ObservableCollection<
        LocalizedChoice<AudioTranscodeRateMode>>
        RateModeChoices { get; } = [];
    public ObservableCollection<
        LocalizedChoice<AudioTranscodeDestinationMode>>
        DestinationModeChoices { get; } = [];
    public ObservableCollection<
        LocalizedChoice<AudioTranscodeCollisionPolicy>>
        CollisionPolicyChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<int?>>
        SampleRateChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<int?>>
        BitDepthChoices { get; } = [];
    public ObservableCollection<AudioTranscodePreset>
        PresetChoices { get; } = [];
    public ObservableCollection<string> Issues { get; } = [];

    public int MaximumProcessorCount =>
        Math.Max(1, Environment.ProcessorCount);

    public string CapturedSelectionSummary =>
        _capturedPaths.Length == 0
            ? L("Transcode.Selection.None")
            : LC(
                "Transcode.Selection.Files",
                _capturedPaths.Length);

    public bool HasSelection => _capturedPaths.Length > 0;

    public bool NeedsDestinationFolder =>
        SelectedDestinationMode ==
        AudioTranscodeDestinationMode.ChosenFolder;

    public bool HasBitrateControl =>
        SelectedRateMode is
            AudioTranscodeRateMode.ConstantBitrate or
            AudioTranscodeRateMode.AverageBitrate or
            AudioTranscodeRateMode.ConstrainedVariableBitrate or
            AudioTranscodeRateMode.HybridBitrate;

    public bool HasQualityControl =>
        SelectedRateMode is
            AudioTranscodeRateMode.VariableQuality or
            AudioTranscodeRateMode.HybridQuality;

    public bool IsCorrectionFileSupported
    {
        get
        {
            AudioEncoderDescriptor? encoder =
                ResolveSelectedEncoder();
            return encoder?
                       .SupportsCorrectionFile ==
                   true &&
                   encoder.RateControls.Any(
                       control =>
                           control.Mode ==
                               SelectedRateMode &&
                           control
                               .SupportsCorrectionFile);
        }
    }

    public bool IsCorrectionFileOptionVisible =>
        IsCorrectionFileSupported ||
        CreateCorrectionFile;

    public bool CanEditCorrectionFile =>
        IsCorrectionFileSupported ||
        CreateCorrectionFile;

    public string CorrectionFileHelpText =>
        L(
            IsCorrectionFileSupported
                ? "Transcode.Correction.Help"
                : "Transcode.Issue.CorrectionUnavailable");

    public bool HasSelectedPreset =>
        SelectedPreset is not null;

    public LocalizedChoice<string>?
        SelectedFormatChoice
    {
        get => FormatChoices.FirstOrDefault(
            choice =>
                choice.Value ==
                SelectedFormatId);
        set
        {
            if (value is not null)
                SelectedFormatId = value.Value;
        }
    }

    public LocalizedChoice<string>?
        SelectedEncoderChoice
    {
        get => EncoderChoices.FirstOrDefault(
            choice =>
                choice.Value ==
                SelectedEncoderId);
        set
        {
            if (value is not null)
                SelectedEncoderId = value.Value;
        }
    }

    public LocalizedChoice<AudioTranscodeRateMode>?
        SelectedRateModeChoice
    {
        get => RateModeChoices.FirstOrDefault(
            choice =>
                choice.Value ==
                SelectedRateMode);
        set
        {
            if (value is not null)
                SelectedRateMode = value.Value;
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    private string? _selectedFormatId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    private string _selectedEncoderId =
        AudioTranscodeEncoderIds.Automatic;

    [ObservableProperty]
    private AudioTranscodeRateMode _selectedRateMode =
        AudioTranscodeRateMode.Lossless;

    [ObservableProperty]
    private int _bitrateKbps = 256;

    [ObservableProperty]
    private double _quality = 4;

    [ObservableProperty]
    private int? _selectedSampleRate;

    [ObservableProperty]
    private int? _selectedBitDepth;

    [ObservableProperty]
    private int _compressionEffort = 5;

    [ObservableProperty]
    private bool _createCorrectionFile;

    [ObservableProperty]
    private bool _preserveMetadata = true;

    [ObservableProperty]
    private bool _preserveArtwork = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsDestinationFolder))]
    [NotifyCanExecuteChangedFor(nameof(BrowseDestinationCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    private AudioTranscodeDestinationMode _selectedDestinationMode =
        AudioTranscodeDestinationMode.Alongside;

    [ObservableProperty]
    private string? _destinationDirectory;

    [ObservableProperty]
    private bool _preserveSourceLayout = true;

    [ObservableProperty]
    private bool _flatten;

    [ObservableProperty]
    private string _fileNameTemplate =
        "{Name}{Extension}";

    [ObservableProperty]
    private AudioTranscodeCollisionPolicy _selectedCollisionPolicy =
        AudioTranscodeCollisionPolicy.Stop;

    [ObservableProperty]
    private AudioTranscodePreset? _selectedPreset;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SavePresetCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdatePresetCommand))]
    private string _presetName = "";

    [ObservableProperty]
    private bool _automaticConcurrency = true;

    [ObservableProperty]
    private int _maximumConcurrentProcesses = 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowseDestinationCommand))]
    [NotifyCanExecuteChangedFor(nameof(SavePresetCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdatePresetCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeletePresetCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _toolReadiness = "";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string? _statusDiagnosticDetail;

    public async Task OpenAsync(
        IEnumerable<string> sourcePaths,
        CancellationToken ct = default)
    {
        _capturedPaths =
        [
            .. sourcePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(PathComparer),
        ];
        OnPropertyChanged(nameof(CapturedSelectionSummary));
        OnPropertyChanged(nameof(HasSelection));
        PreviewCommand.NotifyCanExecuteChanged();
        await LoadCapabilitiesAsync(
            forceRefresh: false,
            ct);
    }

    [RelayCommand]
    private Task RefreshCapabilitiesAsync(
        CancellationToken ct = default) =>
        LoadCapabilitiesAsync(
            forceRefresh: true,
            ct);

    private async Task LoadCapabilitiesAsync(
        bool forceRefresh,
        CancellationToken ct)
    {
        IsBusy = true;
        Issues.Clear();
        try
        {
            _capabilitySnapshot =
                await _capabilities.GetAsync(
                    forceRefresh,
                    ct);
            RefreshCapabilityChoices();
            int ready = _capabilitySnapshot.Tools.Count(tool =>
                tool.State == AudioToolProbeState.Ready);
            ToolReadiness = LC(
                "Transcode.Tools.Ready",
                ready,
                _capabilitySnapshot.Tools.Length);
            Status = L("Transcode.Status.Ready");
            StatusDiagnosticDetail = null;
        }
        catch (Exception error)
        {
            Status = L("Transcode.Status.ToolsFailed");
            StatusDiagnosticDetail = error.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanBrowseDestination))]
    private async Task BrowseDestinationAsync()
    {
        string? path = await _files.PickFolderAsync(
            L("Transcode.Destination.PickerTitle"));
        if (path is not null)
            DestinationDirectory = path;
    }

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewAsync(
        CancellationToken ct)
    {
        IsBusy = true;
        Issues.Clear();
        Status = L("Transcode.Status.Previewing");
        try
        {
            _scheduler.SaveSettings(new(
                AutomaticConcurrency,
                MaximumConcurrentProcesses));
            AudioTranscodePlan plan =
                await _transcodes.PreviewAsync(
                    CreateRequest(),
                    ct: ct);
            foreach (OperationIssue issue in
                     plan.Issues.Concat(
                         plan.Items.SelectMany(item =>
                             item.Issues)))
                Issues.Add(LocalizeIssue(issue));
            StatusDiagnosticDetail = string.Join(
                Environment.NewLine,
                plan.Issues.Concat(
                        plan.Items.SelectMany(item =>
                            item.Issues))
                    .Select(issue => issue.Message)
                    .Distinct(StringComparer.Ordinal));
            if (!plan.CanApply)
            {
                Status = L("Transcode.Status.PreviewBlocked");
                return;
            }
            if (!await _pending.AddPendingTranscodeAsync(
                    plan,
                    ct))
            {
                Status = L("Transcode.Status.PreviewUnchanged");
                return;
            }
            Status = LC(
                "Transcode.Status.PreviewReady",
                plan.Items.Length);
            PreviewCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error)
        {
            Status = L("Transcode.Status.PreviewFailed");
            StatusDiagnosticDetail = error.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSavePreset))]
    private void SavePreset()
    {
        AudioTranscodePreset saved = _presets.Save(new(
            Guid.NewGuid(),
            PresetName,
            CreateSettings(),
            PreserveMetadata,
            PreserveArtwork,
            PreserveSourceLayout && !Flatten,
            FileNameTemplate,
            SelectedCollisionPolicy,
            DateTimeOffset.UtcNow));
        RefreshPresets(saved.Id);
        Status = L("Transcode.Status.PresetSaved");
    }

    [RelayCommand(CanExecute = nameof(CanUpdatePreset))]
    private void UpdatePreset()
    {
        if (SelectedPreset is null)
            return;
        AudioTranscodePreset saved = _presets.Save(
            SelectedPreset with
            {
                Name = PresetName,
                Settings = CreateSettings(),
                PreserveMetadata = PreserveMetadata,
                PreserveArtwork = PreserveArtwork,
                PreserveSourceLayout =
                    PreserveSourceLayout && !Flatten,
                FileNameTemplate = FileNameTemplate,
                CollisionPolicy =
                    SelectedCollisionPolicy,
            });
        RefreshPresets(saved.Id);
        Status = L("Transcode.Status.PresetUpdated");
    }

    [RelayCommand(CanExecute = nameof(CanDeletePreset))]
    private async Task DeletePresetAsync()
    {
        if (SelectedPreset is null ||
            !await _dialogs.ConfirmDestructiveAsync(
                L("Transcode.Preset.DeleteTitle"),
                L("Transcode.Preset.DeleteMessage"),
                L("Common.Delete")))
            return;
        _presets.Delete(SelectedPreset.Id);
        RefreshPresets();
        Status = L("Transcode.Status.PresetDeleted");
    }

    partial void OnSelectedFormatIdChanged(string? value)
    {
        OnPropertyChanged(
            nameof(SelectedFormatChoice));
        if (_loadingSettings)
            return;
        RefreshEncoderChoices();
        PreviewCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedEncoderIdChanged(string value)
    {
        OnPropertyChanged(
            nameof(SelectedEncoderChoice));
        if (!_loadingSettings)
            RefreshRateModeChoices();
    }

    partial void OnSelectedRateModeChanged(
        AudioTranscodeRateMode value)
    {
        OnPropertyChanged(
            nameof(SelectedRateModeChoice));
        OnPropertyChanged(nameof(HasBitrateControl));
        OnPropertyChanged(nameof(HasQualityControl));
        RefreshCorrectionFileOption(
            clearInapplicable:
                !_refreshingCapabilityChoices);
    }

    partial void OnCreateCorrectionFileChanged(
        bool value)
    {
        OnPropertyChanged(
            nameof(IsCorrectionFileOptionVisible));
        OnPropertyChanged(
            nameof(CanEditCorrectionFile));
        OnPropertyChanged(
            nameof(CorrectionFileHelpText));
    }

    partial void OnSelectedPresetChanged(
        AudioTranscodePreset? value)
    {
        OnPropertyChanged(nameof(HasSelectedPreset));
        DeletePresetCommand.NotifyCanExecuteChanged();
        UpdatePresetCommand.NotifyCanExecuteChanged();
        if (value is null)
            return;
        LoadPreset(value);
    }

    partial void OnAutomaticConcurrencyChanged(bool value) =>
        SaveConcurrency();

    partial void OnMaximumConcurrentProcessesChanged(int value) =>
        SaveConcurrency();

    private void SaveConcurrency()
    {
        if (_loadingSettings)
            return;
        _scheduler.SaveSettings(new(
            AutomaticConcurrency,
            MaximumConcurrentProcesses));
    }

    private void RefreshStaticChoices()
    {
        Refresh(
            DestinationModeChoices,
            Enum.GetValues<AudioTranscodeDestinationMode>(),
            value => L($"Transcode.Destination.{value}"));
        Refresh(
            CollisionPolicyChoices,
            Enum.GetValues<AudioTranscodeCollisionPolicy>(),
            value => L($"Transcode.Collision.{value}"));
        Refresh(
            SampleRateChoices,
            new int?[] { null, 44100, 48000, 88200, 96000, 176400, 192000 },
            value => value is null
                ? L("Transcode.Value.Preserve")
                : $"{value.Value / 1000d:0.#} kHz");
        Refresh(
            BitDepthChoices,
            new int?[] { null, 16, 24, 32 },
            value => value is null
                ? L("Transcode.Value.Preserve")
                : LC("Transcode.Value.Bits", value.Value));
    }

    private void RefreshCapabilityChoices()
    {
        _refreshingCapabilityChoices = true;
        try
        {
            string? selected = SelectedFormatId;
            var choices =
                new List<(string Value, string Label)>();
            if (_capabilitySnapshot is not null)
            {
                foreach (AudioTranscodeFormatDescriptor format in
                         _capabilitySnapshot.Formats)
                    choices.Add((
                        format.Id,
                        FormatLabel(format)));
            }
            if (!string.IsNullOrWhiteSpace(selected) &&
                !choices.Any(item =>
                    item.Value == selected))
                choices.Add((
                    selected,
                    selected + " — " +
                    L("Transcode.Value.Unavailable")));
            RefreshChoices(
                FormatChoices,
                choices);
            SelectedFormatId = !string.IsNullOrWhiteSpace(selected)
                ? selected
                : FormatChoices.FirstOrDefault(item =>
                    item.Value == AudioTranscodeFormatIds.Flac)?.Value ??
                  FormatChoices.FirstOrDefault()?.Value;
            OnPropertyChanged(
                nameof(SelectedFormatId));
            OnPropertyChanged(
                nameof(SelectedFormatChoice));
            RefreshEncoderChoices();
        }
        finally
        {
            _refreshingCapabilityChoices = false;
            RefreshCorrectionFileOption(
                clearInapplicable: false);
        }
    }

    private void RefreshEncoderChoices()
    {
        string selected = SelectedEncoderId;
        var choices =
            new List<(string Value, string Label)>
            {
                (
                    AudioTranscodeEncoderIds.Automatic,
                    L("Transcode.Encoder.Auto")
                ),
            };
        AudioTranscodeFormatDescriptor? format =
            _capabilitySnapshot?.FindFormat(
                SelectedFormatId ?? "");
        if (format is not null)
        {
            foreach (string encoderId in format.EncoderIds)
                choices.Add((
                    encoderId,
                    EncoderLabel(encoderId)));
        }
        if (!choices.Any(item =>
                item.Value == selected))
            choices.Add((
                selected,
                selected + " — " +
                L("Transcode.Value.Unavailable")));
        RefreshChoices(
            EncoderChoices,
            choices);
        SelectedEncoderId = selected;
        OnPropertyChanged(
            nameof(SelectedEncoderId));
        OnPropertyChanged(
            nameof(SelectedEncoderChoice));
        RefreshRateModeChoices();
    }

    private void RefreshRateModeChoices()
    {
        AudioEncoderDescriptor? encoder =
            ResolveSelectedEncoder();
        AudioTranscodeRateMode[] modes =
            encoder is null
                ? [SelectedRateMode]
                : [.. encoder.RateControls.Select(item =>
                    item.Mode).Distinct()];
        AudioTranscodeRateMode selected =
            SelectedRateMode;
        Refresh(
            RateModeChoices,
            modes,
            value => L($"Transcode.RateMode.{value}"));
        SelectedRateMode = modes.Contains(selected)
            ? selected
            : modes[0];
        OnPropertyChanged(
            nameof(SelectedRateMode));
        OnPropertyChanged(
            nameof(SelectedRateModeChoice));
        RefreshCorrectionFileOption(
            clearInapplicable:
                !_refreshingCapabilityChoices);
    }

    private void RefreshCorrectionFileOption(
        bool clearInapplicable = true)
    {
        bool supported =
            IsCorrectionFileSupported;
        if (clearInapplicable &&
            !supported &&
            !_loadingSettings &&
            CreateCorrectionFile)
        {
            CreateCorrectionFile = false;
        }

        OnPropertyChanged(
            nameof(IsCorrectionFileSupported));
        OnPropertyChanged(
            nameof(IsCorrectionFileOptionVisible));
        OnPropertyChanged(
            nameof(CanEditCorrectionFile));
        OnPropertyChanged(
            nameof(CorrectionFileHelpText));
    }

    private AudioEncoderDescriptor? ResolveSelectedEncoder()
    {
        AudioTranscodeFormatDescriptor? format =
            _capabilitySnapshot?.FindFormat(
                SelectedFormatId ?? "");
        string? id = SelectedEncoderId ==
                     AudioTranscodeEncoderIds.Automatic
            ? format?.EncoderIds.FirstOrDefault()
            : SelectedEncoderId;
        return id is null ||
               format is null ||
               !format.EncoderIds.Contains(
                   id,
                   StringComparer.Ordinal)
            ? null
            : _capabilitySnapshot?.FindEncoder(id);
    }

    private void RefreshPresets(Guid? selectedId = null)
    {
        PresetChoices.Clear();
        foreach (AudioTranscodePreset preset in _presets.Load())
            PresetChoices.Add(preset);
        SelectedPreset = selectedId is { } id
            ? PresetChoices.FirstOrDefault(item => item.Id == id)
            : null;
    }

    private void LoadPreset(AudioTranscodePreset preset)
    {
        _loadingSettings = true;
        try
        {
            PresetName = preset.Name;
            if (!FormatChoices.Any(item =>
                    item.Value == preset.Settings.FormatId))
                FormatChoices.Add(new(
                    preset.Settings.FormatId,
                    preset.Settings.FormatId + " — " +
                    L("Transcode.Value.Unavailable")));
            SelectedFormatId = preset.Settings.FormatId;
            RefreshEncoderChoices();
            if (!EncoderChoices.Any(item =>
                    item.Value == preset.Settings.EncoderId))
            {
                EncoderChoices.Add(new(
                    preset.Settings.EncoderId,
                    preset.Settings.EncoderId + " — " +
                    L("Transcode.Value.Unavailable")));
            }
            SelectedEncoderId = preset.Settings.EncoderId;
            RefreshRateModeChoices();
            SelectedRateMode = preset.Settings.RateMode;
            BitrateKbps = preset.Settings.BitrateKbps ?? 256;
            Quality = preset.Settings.Quality ?? 4;
            SelectedSampleRate = preset.Settings.SampleRateHz;
            SelectedBitDepth = preset.Settings.BitsPerSample;
            CompressionEffort = preset.Settings.CompressionEffort;
            CreateCorrectionFile =
                preset.Settings.CreateCorrectionFile;
            PreserveMetadata = preset.PreserveMetadata;
            PreserveArtwork = preset.PreserveArtwork;
            PreserveSourceLayout =
                preset.PreserveSourceLayout;
            Flatten = !preset.PreserveSourceLayout;
            FileNameTemplate = preset.FileNameTemplate;
            SelectedCollisionPolicy =
                preset.CollisionPolicy;
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private AudioTranscodeRequest CreateRequest() =>
        new(
            _capturedPaths,
            CreateSettings(),
            new(
                SelectedDestinationMode,
                NeedsDestinationFolder
                    ? DestinationDirectory
                    : null,
                PreserveSourceLayout && !Flatten,
                FileNameTemplate,
                SelectedCollisionPolicy),
            PreserveMetadata,
            PreserveArtwork);

    private AudioTranscodeSettings CreateSettings() =>
        new(
            SelectedFormatId ??
            AudioTranscodeFormatIds.Flac,
            SelectedEncoderId,
            SelectedRateMode,
            HasBitrateControl
                ? BitrateKbps
                : null,
            HasQualityControl
                ? Quality
                : null,
            SelectedSampleRate,
            SelectedBitDepth,
            CompressionEffort,
            CreateCorrectionFile);

    private bool CanBrowseDestination() =>
        !IsBusy && NeedsDestinationFolder;

    private bool CanPreview() =>
        !IsBusy &&
        HasSelection &&
        !string.IsNullOrWhiteSpace(SelectedFormatId) &&
        (!NeedsDestinationFolder ||
         !string.IsNullOrWhiteSpace(DestinationDirectory));

    private bool CanSavePreset() =>
        !IsBusy &&
        !string.IsNullOrWhiteSpace(PresetName);

    private bool CanUpdatePreset() =>
        CanSavePreset() &&
        SelectedPreset is not null;

    private bool CanDeletePreset() =>
        !IsBusy &&
        SelectedPreset is not null;

    private string FormatLabel(
        AudioTranscodeFormatDescriptor format) =>
        format.Id switch
        {
            AudioTranscodeFormatIds.Flac => L("Transcode.Format.Flac"),
            AudioTranscodeFormatIds.AlacM4a => L("Transcode.Format.AlacM4a"),
            AudioTranscodeFormatIds.AacM4a => L("Transcode.Format.AacM4a"),
            AudioTranscodeFormatIds.AacAdts => L("Transcode.Format.AacAdts"),
            AudioTranscodeFormatIds.Mp3 => L("Transcode.Format.Mp3"),
            AudioTranscodeFormatIds.OpusOgg => L("Transcode.Format.OpusOgg"),
            AudioTranscodeFormatIds.VorbisOgg => L("Transcode.Format.VorbisOgg"),
            AudioTranscodeFormatIds.WavPack => L("Transcode.Format.WavPack"),
            AudioTranscodeFormatIds.PcmWave => L("Transcode.Format.PcmWave"),
            AudioTranscodeFormatIds.PcmRf64 => L("Transcode.Format.PcmRf64"),
            AudioTranscodeFormatIds.PcmAiff => L("Transcode.Format.PcmAiff"),
            AudioTranscodeFormatIds.TrueAudio => L("Transcode.Format.TrueAudio"),
            AudioTranscodeFormatIds.OptimFrog => L("Transcode.Format.OptimFrog"),
            AudioTranscodeFormatIds.OptimFrogDualStream => L("Transcode.Format.OptimFrogDualStream"),
            AudioTranscodeFormatIds.OptimFrogFloat => L("Transcode.Format.OptimFrogFloat"),
            AudioTranscodeFormatIds.MonkeysAudio =>
                L("Transcode.Format.MonkeysAudio"),
            _ => $"{format.Codec} ({format.Container})",
        };

    private string EncoderLabel(string id) =>
        id.StartsWith("ffmpeg:", StringComparison.Ordinal)
            ? "FFmpeg — " + id["ffmpeg:".Length..]
            : id switch
            {
                AudioTranscodeEncoderIds.WavPackCli =>
                    L("Transcode.Encoder.WavPack"),
                AudioTranscodeEncoderIds.OptimFrogOfr =>
                    L("Transcode.Encoder.OptimFrogOfr"),
                AudioTranscodeEncoderIds.OptimFrogOfs =>
                    L("Transcode.Encoder.OptimFrogOfs"),
                AudioTranscodeEncoderIds.OptimFrogOff =>
                    L("Transcode.Encoder.OptimFrogOff"),
                AudioTranscodeEncoderIds
                    .MonkeysAudioMac =>
                    L("Transcode.Encoder.MonkeysAudioMac"),
                _ => id,
            };

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        RefreshStaticChoices();
        RefreshCapabilityChoices();
        OnPropertyChanged(nameof(CapturedSelectionSummary));
        OnPropertyChanged(
            nameof(CorrectionFileHelpText));
    }

    private static void Refresh<T>(
        ObservableCollection<LocalizedChoice<T>> destination,
        IEnumerable<T> values,
        Func<T, string> label) =>
        RefreshChoices(
            destination,
            values.Select(value =>
                (value, label(value))));

    private static void RefreshChoices<T>(
        ObservableCollection<LocalizedChoice<T>> destination,
        IEnumerable<(T Value, string Label)> values)
    {
        (T Value, string Label)[] desired =
            [.. values];
        var comparer = EqualityComparer<T>.Default;
        for (int index = 0;
             index < desired.Length;
             index++)
        {
            (T value, string label) = desired[index];
            if (index < destination.Count &&
                comparer.Equals(
                    destination[index].Value,
                    value))
            {
                destination[index].Label = label;
                continue;
            }

            int existingIndex = -1;
            for (int candidate = index + 1;
                 candidate < destination.Count;
                 candidate++)
            {
                if (!comparer.Equals(
                        destination[candidate].Value,
                        value))
                    continue;
                existingIndex = candidate;
                break;
            }
            if (existingIndex >= 0)
            {
                destination.Move(
                    existingIndex,
                    index);
                destination[index].Label = label;
            }
            else
            {
                destination.Insert(
                    index,
                    new(value, label));
            }
        }
        while (destination.Count > desired.Length)
            destination.RemoveAt(
                destination.Count - 1);
    }

    private string L(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);

    private string LocalizeIssue(OperationIssue issue) =>
        L(issue.Code switch
        {
            "transcode.format-unavailable" =>
                "Transcode.Issue.FormatUnavailable",
            "transcode.encoder-unavailable" =>
                "Transcode.Issue.EncoderUnavailable",
            "transcode.source-missing" or
            "transcode.source-container-unavailable" or
            "transcode.source-decoder-unavailable" or
            "transcode.source-inspection-failed" =>
                "Transcode.Issue.SourceUnavailable",
            "transcode.rate-mode-unavailable" or
            "transcode.bitrate-out-of-range" or
            "transcode.quality-out-of-range" or
            "transcode.bit-depth-unavailable" =>
                "Transcode.Issue.SettingsInvalid",
            "transcode.destination-required" or
            "transcode.destination-exists" or
            "transcode.destination-collision-exhausted" or
            "transcode.filename-invalid" or
            "transcode.recovery-space" =>
                "Transcode.Issue.DestinationInvalid",
            "transcode.correction-unavailable" =>
                "Transcode.Issue.CorrectionUnavailable",
            "transcode.dsd-pcm-settings-required" =>
                "Transcode.Issue.DsdSettingsRequired",
            "transcode.replace-multiple-audio-programs" =>
                "Transcode.Issue.ReplaceUnsafe",
            "transcode.separate-primary-audio" =>
                "Transcode.Issue.SeparatePrimaryAudio",
            "transcode.output-session-only" =>
                "Transcode.Issue.OutputSessionOnly",
            "transcode.catalog-refresh-failed" =>
                "Transcode.Issue.CatalogRefreshFailed",
            "transcode.stage-failed" or
            "transcode.preview-blocked" =>
                "Transcode.Issue.StageFailed",
            "transcode.metadata-not-representable" or
            "transcode.custom-metadata-not-representable" =>
                "Transcode.Issue.MetadataNotRepresentable",
            "transcode.artwork-not-representable" =>
                "Transcode.Issue.ArtworkNotRepresentable",
            _ => "Transcode.Issue.StageFailed",
        });

    private string LC(
        string key,
        long count,
        params object?[] arguments) =>
        _localization?.FormatCount(
            key,
            count,
            arguments) ??
        LocalizedText.FormatCount(
            key,
            count,
            arguments);

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
