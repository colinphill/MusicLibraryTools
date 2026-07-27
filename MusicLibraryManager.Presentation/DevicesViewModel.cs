using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

public sealed class DeviceSyncActionRow : ObservableObject
{
    private readonly DeviceSyncAction _action;
    private readonly ILocalizationService? _localization;
    private OperationItemStatus? _statusValue;

    public DeviceSyncMutationKind KindValue =>
        _action.Kind;
    public string Kind => L(
        $"Devices.ActionKind.{KindValue}");
    public string RelativePath => _action.RelativePath;
    public string Reason => L(
        $"Devices.ActionReason.{KindValue}");
    public string DiagnosticDetail => _action.Reason;
    public bool IsDirectory => _action.IsDirectory;
    public long Length => _action.Length;
    public long ModifiedSeconds => _action.ModifiedSeconds;
    public OperationItemStatus? StatusValue =>
        _statusValue;
    public string Status => _statusValue is { } status
        ? L($"Devices.ActionStatus.{status}")
        : "";
    public bool IsInProgress =>
        _statusValue ==
        OperationItemStatus.InProgress;

    public DeviceSyncActionRow(
        DeviceSyncAction action,
        ILocalizationService? localization = null)
    {
        _action = action;
        _localization = localization;
    }

    public void SetStatus(OperationItemStatus status)
    {
        if (_statusValue is
                OperationItemStatus.Complete or
                OperationItemStatus.Failed &&
            status ==
            OperationItemStatus.InProgress)
            return;
        if (_statusValue == status)
            return;
        _statusValue = status;
        OnPropertyChanged(nameof(StatusValue));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsInProgress));
    }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(Reason));
        OnPropertyChanged(nameof(Status));
    }

    private string L(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);
}

public sealed class DeviceSelectionOption : ObservableObject
{
    private readonly ILocalizationService? _localization;
    private readonly string _displayName;
    private readonly string _state;

    public string? Id { get; }
    public string? Serial { get; }
    public bool IsReady { get; }
    public string? Connection { get; }
    public bool IsRemembered { get; }
    public bool IsManual => Id is null;
    public bool IsAvailable => IsManual || IsReady;
    public string DisplayName => IsManual
        ? L("Devices.Selection.Manual")
        : IsRemembered
            ? RememberedDisplayName()
            : _displayName;
    public string StateValue => _state;
    public string State => IsManual
        ? L("Devices.Selection.State.Manual")
        : IsRemembered
            ? L("Devices.Selection.State.NotConnected")
            : LocalizeState(_state);
    public string Details => IsManual
        ? L("Devices.Selection.ManualDetails")
        : IsRemembered
            ? LF(
                "Devices.Selection.RememberedDetails",
                Serial)
            : IsReady
                ? string.IsNullOrWhiteSpace(Connection)
                    ? Serial!
                    : LF(
                        "Devices.Selection.ReadyDetails",
                        Serial,
                        Connection)
                : LF(
                    "Devices.Selection.UnavailableDetails",
                    Serial,
                    State);

    private DeviceSelectionOption(
        string? id,
        string? serial,
        string displayName,
        string state,
        bool isReady,
        string? connection,
        bool isRemembered,
        ILocalizationService? localization)
    {
        Id = id;
        Serial = serial;
        _displayName = displayName;
        _state = state;
        IsReady = isReady;
        Connection = connection;
        IsRemembered = isRemembered;
        _localization = localization;
    }

    public static DeviceSelectionOption Manual =>
        ManualFor();

    public static DeviceSelectionOption ManualFor(
        ILocalizationService? localization = null) =>
        new(
            null,
            null,
            "",
            "manual",
            true,
            null,
            false,
            localization);

    public static DeviceSelectionOption FromDevice(
        DeviceSyncDevice device,
        ILocalizationService? localization = null) =>
        new(
            device.Id,
            device.Serial,
            string.IsNullOrWhiteSpace(
                device.DisplayName)
                ? device.Id
                : device.DisplayName,
            device.State,
            device.IsReady,
            device.Connection,
            false,
            localization);

    public static DeviceSelectionOption Remembered(
        string id,
        string serial,
        ILocalizationService? localization = null) =>
        new(
            id,
            serial,
            "",
            "not connected",
            false,
            null,
            true,
            localization);

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Details));
    }

    private string RememberedDisplayName()
    {
        int separator = Id!.LastIndexOf('|');
        string model = separator > 0
            ? Id[..separator]
            : "";
        if (string.IsNullOrWhiteSpace(model) ||
            StringComparer.Ordinal.Equals(
                model,
                "unknown"))
            return L(
                "Devices.Selection.PreviouslySelected");
        return LF(
            "Devices.Selection.RememberedName",
            model.Replace('_', ' '),
            Serial);
    }

    private string LocalizeState(
        string state) =>
        state.Trim()
            .ToLowerInvariant() switch
        {
            "device" =>
                L("Devices.Selection.State.Ready"),
            "offline" =>
                L("Devices.Selection.State.Offline"),
            "unauthorized" =>
                L("Devices.Selection.State.Unauthorized"),
            "no permissions" =>
                L("Devices.Selection.State.NoPermissions"),
            _ =>
                L("Devices.Selection.State.Unavailable"),
        };

    private string L(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);

    private string LF(
        string key,
        params object?[] arguments) =>
        _localization?.Format(
            key,
            arguments) ??
        LocalizedText.Format(
            key,
            arguments);
}

public sealed class DeviceIssueRow : ObservableObject
{
    private readonly ILocalizationService? _localization;

    public OperationIssue Issue { get; }
    public string Code => Issue.Code;
    public OperationIssueSeverity Severity =>
        Issue.Severity;
    public string? Path => Issue.Path;
    public string Message => L(
        Issue.Code switch
        {
            "removal-limit" =>
                "Devices.Issue.RemovalLimit",
            "direct-mode" =>
                "Devices.Issue.DirectMode",
            _ when Issue.Severity ==
                   OperationIssueSeverity.Blocker =>
                "Devices.Issue.GenericBlocker",
            _ when Issue.Severity ==
                   OperationIssueSeverity.Warning =>
                "Devices.Issue.GenericWarning",
            _ =>
                "Devices.Issue.GenericInformation",
        });
    public string DiagnosticDetail =>
        Issue.Message;

    public DeviceIssueRow(
        OperationIssue issue,
        ILocalizationService? localization = null)
    {
        Issue = issue;
        _localization = localization;
    }

    public void RefreshLocalization() =>
        OnPropertyChanged(nameof(Message));

    private string L(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);
}

public partial class DevicesViewModel : ViewModelBase
{
    private const string ProfilePreference = "manager.devices.profile.v1";
    private const string ManualProfileKey = "$manual";
    private readonly IDeviceSyncService _sync;
    private readonly IAppSettings _settings;
    private readonly IFilePickerService _files;
    private readonly IDialogCoordinator _dialogs;
    private readonly IActivityService _activities;
    private readonly ILocalizationService? _localization;
    private readonly DeviceSelectionOption _manualDeviceOption;
    private CancellationTokenSource? _cts;
    private DeviceSyncPlan? _plan;
    private string? _statusKey =
        "Devices.Status.Ready";
    private object?[] _statusArguments = [];
    private bool _loadingProfile;
    private bool _updatingDeviceSelection;
    private readonly Dictionary<string, DeviceDirectories> _deviceDirectories =
        new(StringComparer.Ordinal);
    private string _activeDeviceKey = ManualProfileKey;
    private string _manualDeviceSerial = "";
    private string? _selectedDeviceId;
    private bool _allowLegacySerialMigration;
    private bool? _manualSelectionPreference;
    private string? _recoveryId;
    private string? _recoveryDestination;
    private string? _recoveryDeviceSerial;
    private string? _recoveryDeviceId;

    [ObservableProperty] private string _sourcePath = "";
    [ObservableProperty] private string _destinationPath = "music";
    [ObservableProperty] private string _deviceSerial = "";
    [ObservableProperty] private string _adbPath = "";
    [ObservableProperty] private string _exclusions = "";
    [ObservableProperty] private int _mtimeToleranceSeconds = 60;
    [ObservableProperty] private int? _maxRemovals;
    [ObservableProperty] private bool _deleteExtras = true;
    [ObservableProperty] private bool _direct;
    [ObservableProperty] private bool _adopt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManualDeviceSelected))]
    [NotifyPropertyChangedFor(nameof(IsSelectedDeviceUnavailable))]
    [NotifyPropertyChangedFor(nameof(NeedsDeviceSelection))]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(InitializeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    private DeviceSelectionOption? _selectedDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeviceEnumerationError))]
    private string _deviceEnumerationError = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeviceEnumerationDiagnosticDetail))]
    private string? _deviceEnumerationDiagnosticDetail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoEnumeratedDevices))]
    [NotifyPropertyChangedFor(nameof(NeedsDeviceSelection))]
    private bool _hasCompletedDeviceEnumeration;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoEnumeratedDevices))]
    [NotifyPropertyChangedFor(nameof(IsConfigurationEnabled))]
    [NotifyCanExecuteChangedFor(nameof(RefreshDevicesCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(InitializeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    private bool _isLoadingDevices;

    [ObservableProperty]
    private string _statusText =
        LocalizedText.Get("Devices.Status.Ready");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiagnosticDetail))]
    private string? _diagnosticDetail;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(InitializeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshDevicesCommand))]
    [NotifyPropertyChangedFor(nameof(IsConfigurationEnabled))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _hasApplicablePreview;

    public ObservableCollection<DeviceSyncActionRow> Actions { get; } = [];
    public ObservableCollection<DeviceIssueRow> Issues { get; } = [];
    public ObservableCollection<DeviceSelectionOption> AvailableDevices { get; } = [];
    public bool IsConfigurationEnabled => !IsBusy && !IsLoadingDevices;
    public bool IsActionListEmpty => Actions.Count == 0;
    public bool IsManualDeviceSelected => SelectedDevice?.IsManual != false;
    public bool IsSelectedDeviceUnavailable => SelectedDevice is { IsAvailable: false };
    public bool HasDeviceEnumerationError => !string.IsNullOrWhiteSpace(DeviceEnumerationError);
    public bool HasDeviceEnumerationDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(
            DeviceEnumerationDiagnosticDetail);
    public bool HasDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(
            DiagnosticDetail);
    public bool HasNoEnumeratedDevices => HasCompletedDeviceEnumeration &&
        !IsLoadingDevices && !HasDeviceEnumerationError &&
        AvailableDevices.All(device => device.IsManual || device.IsRemembered);
    public bool NeedsDeviceSelection => HasCompletedDeviceEnumeration &&
        IsManualDeviceSelected && Clean(DeviceSerial) is null &&
        AvailableDevices.Count(device => device.IsReady) > 1;

    public DevicesViewModel(
        IDeviceSyncService sync,
        IAppSettings settings,
        IFilePickerService files,
        IDialogCoordinator dialogs,
        IActivityService activities,
        ILocalizationService? localization = null)
    {
        _sync = sync;
        _settings = settings;
        _files = files;
        _dialogs = dialogs;
        _activities = activities;
        _localization = localization;
        _manualDeviceOption =
            DeviceSelectionOption.ManualFor(
                localization);
        SetStatus("Devices.Status.Ready");
        if (_localization is not null)
            _localization.CultureChanged +=
                OnLocalizationCultureChanged;
        LoadProfile();
        settings.ConfigurationChanged += (_, _) =>
        {
            InvalidatePreview();
            LoadProfile();
        };
        _updatingDeviceSelection = true;
        AvailableDevices.Add(_manualDeviceOption);
        SelectedDevice = _manualDeviceOption;
        _updatingDeviceSelection = false;
    }

    private bool CanPreview() => !IsBusy && !IsLoadingDevices &&
        !string.IsNullOrWhiteSpace(SourcePath) &&
        !string.IsNullOrWhiteSpace(DestinationPath) && CanUseSelectedDevice();
    private bool CanApply() => !IsBusy && !IsLoadingDevices && HasApplicablePreview &&
        _plan?.CanApply == true && CanUseSelectedDevice();
    private bool CanInitialize() => !IsBusy && !IsLoadingDevices &&
        !string.IsNullOrWhiteSpace(DestinationPath) && CanUseSelectedDevice();
    private bool CanRestore() => !IsBusy && !IsLoadingDevices && CanUseSelectedDevice() &&
        !string.IsNullOrWhiteSpace(_recoveryId) &&
        StringComparer.Ordinal.Equals(DestinationPath.Trim(), _recoveryDestination) &&
        MatchesRecoveryDevice();
    private bool CanCancel() => IsBusy;
    private bool CanRefreshDevices() => !IsBusy && !IsLoadingDevices;
    private bool CanUseSelectedDevice() => SelectedDevice?.IsAvailable != false &&
        !NeedsDeviceSelection;

    private bool MatchesRecoveryDevice()
    {
        if (_recoveryDeviceId is not null)
            return StringComparer.Ordinal.Equals(SelectedDevice?.Id, _recoveryDeviceId);
        return Clean(DeviceSerial) is null ||
            StringComparer.Ordinal.Equals(Clean(DeviceSerial), _recoveryDeviceSerial);
    }

    [RelayCommand(CanExecute = nameof(CanRefreshDevices))]
    private async Task RefreshDevicesAsync(CancellationToken ct)
    {
        IsLoadingDevices = true;
        HasCompletedDeviceEnumeration = false;
        DeviceEnumerationError = "";
        DeviceEnumerationDiagnosticDetail = null;
        try
        {
            IReadOnlyList<DeviceSyncDevice> devices = await _sync
                .EnumerateDevicesAsync(Clean(AdbPath), ct);
            ReplaceDeviceOptions(devices);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error)
        {
            ShowManualDeviceOption();
            DeviceEnumerationError =
                L("Devices.Enumeration.Failed");
            DeviceEnumerationDiagnosticDetail =
                error.Message;
        }
        finally
        {
            IsLoadingDevices = false;
            HasCompletedDeviceEnumeration = true;
            OnPropertyChanged(nameof(HasNoEnumeratedDevices));
        }
    }

    [RelayCommand]
    private async Task BrowseSourceAsync()
    {
        string? path = await _files.PickFolderAsync(
            L("Devices.Dialog.ChooseSource"));
        if (path is not null) SourcePath = path;
    }

    [RelayCommand]
    private async Task BrowseAdbAsync()
    {
        string? path = await _files.PickFileAsync(
            L("Devices.Dialog.ChooseAdb"));
        if (path is not null) AdbPath = path;
    }

    [RelayCommand(CanExecute = nameof(CanInitialize))]
    private async Task InitializeAsync()
    {
        string actionKey = Adopt
            ? "Devices.Dialog.Initialize.Adopt"
            : "Devices.Dialog.Initialize.Create";
        if (!await _dialogs.ConfirmAsync(
                L(
                    Adopt
                        ? "Devices.Dialog.Initialize.AdoptTitle"
                        : "Devices.Dialog.Initialize.Title"),
                LF(
                    Adopt
                        ? "Devices.Dialog.Initialize.AdoptMessage"
                        : "Devices.Dialog.Initialize.Message",
                    DestinationPath),
                L(actionKey)))
            return;

        await RunAsync(
            "Devices.Activity.Initialize.Title",
            async (progress, ct) =>
        {
            var request = new DeviceSyncInitializationRequest(
                DestinationPath.Trim(), Clean(DeviceSerial), Clean(AdbPath), Adopt);
            DeviceSyncInitializationResult result = await Task.Run(() => _sync.InitializeAsync(
                request, progress, ct), ct);
            SetStatus(
                "Devices.Status.Initialized");
        });
    }

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewAsync()
    {
        InvalidatePreview();
        await RunAsync(
            "Devices.Activity.Preview.Title",
            async (progress, ct) =>
        {
            DeviceSyncRequest request = CreateRequest();
            DeviceSyncPlan plan = await Task.Run(
                () => _sync.PreviewAsync(request, progress, ct), ct);
            _plan = plan;
            foreach (DeviceSyncAction action in
                     plan.Actions)
                Actions.Add(
                    new DeviceSyncActionRow(
                        action,
                        _localization));
            OnPropertyChanged(nameof(IsActionListEmpty));
            foreach (OperationIssue issue in
                     plan.Issues)
                Issues.Add(
                    new DeviceIssueRow(
                        issue,
                        _localization));
            HasApplicablePreview = plan.CanApply;
            SetStatus(
                plan.CanApply
                    ? "Devices.Status.PreviewReady"
                    : "Devices.Status.PreviewBlocked",
                plan.Actions.Count,
                plan.RemovalCount,
                plan.TransferBytes);
            if (!plan.CanApply)
                DiagnosticDetail =
                    plan.Issues.FirstOrDefault()
                        ?.Message;
        });
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        DeviceSyncPlan plan = _plan ??
            throw new InvalidOperationException(
                L("Devices.Error.PreviewRequired"));
        if (!await _dialogs.ConfirmAsync(
                L("Devices.Dialog.Apply.Title"),
                LF(
                    plan.Request.Direct
                        ? "Devices.Dialog.Apply.DirectMessage"
                        : "Devices.Dialog.Apply.Message",
                    plan.Actions.Count,
                    plan.RemovalCount,
                    plan.TransferBytes),
                L(
                    plan.Request.Direct
                        ? "Devices.Dialog.Apply.DirectPrimary"
                        : "Devices.Dialog.Apply.Primary")))
            return;
        await RunAsync(
            "Devices.Activity.Apply.Title",
            async (progress, ct) =>
        {
            DeviceSyncResult result;
            try
            {
                result = await Task.Run(() => _sync.ApplyAsync(plan, progress, ct), ct);
            }
            catch
            {
                foreach (DeviceSyncActionRow row in Actions.Where(row => row.IsInProgress))
                    row.SetStatus(OperationItemStatus.Failed);
                throw;
            }
            foreach (DeviceSyncActionRow row in Actions)
                row.SetStatus(OperationItemStatus.Complete);
            SetStatus(
                result.RecoveryId is null
                    ? "Devices.Status.ApplyComplete"
                    : "Devices.Status.ApplyCompleteWithRecovery",
                result.CopiedFileCount,
                result.QuarantinedCount,
                result.TransferredBytes,
                result.RecoveryId);
            if (!string.IsNullOrWhiteSpace(result.RecoveryId))
                SetRecovery(result.RecoveryId, plan.Request.Destination.Trim(),
                    Clean(result.DeviceSerial), _selectedDeviceId);
            else if (plan.Request.Direct)
                ClearRecovery();
            HasApplicablePreview = false;
            _plan = null;
        });
    }

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        string recoveryId = _recoveryId ??
            throw new InvalidOperationException(
                L("Devices.Error.NoRecovery"));
        string destination = _recoveryDestination ??
            throw new InvalidOperationException(
                L("Devices.Error.NoRecoveryDestination"));
        if (!await _dialogs.ConfirmAsync(
                L("Devices.Dialog.Restore.Title"),
                LF(
                    "Devices.Dialog.Restore.Message",
                    recoveryId,
                    destination),
                L("Devices.Dialog.Restore.Primary")))
            return;

        await RunAsync(
            "Devices.Activity.Restore.Title",
            async (progress, ct) =>
        {
            var request = new DeviceSyncRestoreRequest(
                destination, recoveryId, _recoveryDeviceSerial, Clean(AdbPath));
            DeviceSyncRestoreResult result = await Task.Run(() => _sync.RestoreAsync(
                request, progress, ct), ct);
            ClearRecovery();
            InvalidatePreview();
            SetStatus(
                "Devices.Status.Restored",
                result.RecoveryId,
                result.DeviceSerial);
        });
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private async Task RunAsync(
        string activityTitleKey,
        Func<IProgress<OperationProgress>, CancellationToken, Task> operation)
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        DiagnosticDetail = null;
        Guid activity = _activities.Start(
            L(activityTitleKey),
            L("Devices.Activity.Starting"),
            ShellDestination.Devices,
            Cancel);
        var progress =
            new DispatchingProgress<OperationProgress>(value =>
        {
            UpdateActionStatus(value);
            if (value.Message is { } rawMessage &&
                ProgressMessageKey(rawMessage) is null)
                DiagnosticDetail = rawMessage;
            SetStatus(
                ProgressStatusKey(value),
                value.CurrentPath);
            double? fraction = value.Total is > 0 ? (double)value.Completed / value.Total : null;
            _activities.Report(
                activity,
                StatusText,
                fraction);
        });
        try
        {
            await operation(progress, _cts.Token);
            string? terminalKey = _statusKey;
            object?[] terminalArguments =
            [
                .. _statusArguments,
            ];
            string terminalText = StatusText;
            string? terminalDiagnostic =
                DiagnosticDetail;
            await progress.DrainAsync();
            if (terminalKey is not null)
                SetStatus(
                    terminalKey,
                    terminalArguments);
            else
                StatusText = terminalText;
            DiagnosticDetail =
                terminalDiagnostic;
            _activities.Finish(activity, StatusText);
        }
        catch (OperationCanceledException)
        {
            await progress.DrainAsync();
            SetStatus("Devices.Status.Cancelled");
            _activities.Finish(activity, StatusText, AppActivityState.Cancelled);
        }
        catch (Exception error)
        {
            await progress.DrainAsync();
            SetFailure(error);
            _activities.Finish(
                activity,
                StatusText,
                AppActivityState.Failed);
            await _dialogs.ShowMessageAsync(
                LF(
                    "Devices.Dialog.Failure.Title",
                    L(activityTitleKey)),
                LF(
                    "Devices.Dialog.Failure.Message",
                    L(activityTitleKey)));
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    private string L(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);

    private string LF(
        string key,
        params object?[] arguments) =>
        _localization?.Format(
            key,
            arguments) ??
        LocalizedText.Format(
            key,
            arguments);

    private void SetStatus(
        string key,
        params object?[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        StatusText = LF(
            key,
            arguments);
    }

    private void SetFailure(
        Exception error)
    {
        SetStatus("Devices.Status.Failed");
        DiagnosticDetail = error.Message;
    }

    private static string? ProgressMessageKey(
        string message) =>
        message switch
        {
            "Initializing managed Android destination" =>
                "Devices.Progress.Initializing",
            "Scanning the source and managed Android destination" =>
                "Devices.Progress.ScanningBoth",
            "Validating affected paths from the saved sync plan" =>
                "Devices.Progress.ValidatingPlan",
            "Android synchronization completed" =>
                "Devices.Progress.SynchronizationComplete",
            "Restoring the previous Android synchronization" =>
                "Devices.Progress.Restoring",
            "Android synchronization restored" =>
                "Devices.Progress.RestoreComplete",
            "Scanning the synchronization source" =>
                "Devices.Progress.ScanningSource",
            "Scanning the managed Android destination" =>
                "Devices.Progress.ScanningDestination",
            "Selecting synchronization changes" =>
                "Devices.Progress.SelectingChanges",
            "Staging file on the Android device" =>
                "Devices.Progress.StagingFile",
            "Transferring file to the Android device" =>
                "Devices.Progress.TransferringFile",
            "Applying synchronization changes" =>
                "Devices.Progress.ApplyingChanges",
            "Synchronization change complete" =>
                "Devices.Progress.ChangeComplete",
            "Synchronization change failed" =>
                "Devices.Progress.ChangeFailed",
            _ => null,
        };

    private static string ProgressStatusKey(
        OperationProgress progress) =>
        progress.Message is { } message &&
        ProgressMessageKey(message) is { } messageKey
            ? messageKey
            : $"Devices.Progress.Phase.{progress.Phase}";

    private void OnLocalizationCultureChanged(
        object? sender,
        EventArgs e)
    {
        if (_statusKey is { } key)
            StatusText = LF(
                key,
                _statusArguments);
        if (HasDeviceEnumerationError)
            DeviceEnumerationError =
                L("Devices.Enumeration.Failed");
        foreach (DeviceSelectionOption option in
                 AvailableDevices)
            option.RefreshLocalization();
        foreach (DeviceSyncActionRow action in
                 Actions)
            action.RefreshLocalization();
        foreach (DeviceIssueRow issue in Issues)
            issue.RefreshLocalization();
    }

    private DeviceSyncRequest CreateRequest() => new(
        SourcePath.Trim(), DestinationPath.Trim(), Clean(DeviceSerial), Clean(AdbPath),
        Exclusions.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        MtimeToleranceSeconds, DeleteExtras, Direct, MaxRemovals);

    private void ReplaceDeviceOptions(IReadOnlyList<DeviceSyncDevice> devices)
    {
        RememberCurrentDirectories();
        string? previousDeviceId = _selectedDeviceId;
        DeviceSelectionOption[] choices = devices
            .GroupBy(device => device.Id, StringComparer.Ordinal)
            .Select(group =>
                DeviceSelectionOption.FromDevice(
                    group.First(),
                    _localization))
            .OrderByDescending(device => device.IsReady)
            .ThenBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(device => device.Serial, StringComparer.Ordinal)
            .ToArray();

        string? selectedId = Clean(_selectedDeviceId);
        DeviceSelectionOption? target = selectedId is not null
            ? choices.FirstOrDefault(device => StringComparer.Ordinal.Equals(device.Id, selectedId))
            : null;
        DeviceSelectionOption? remembered = null;
        if (target is null && selectedId is not null)
        {
            remembered = DeviceSelectionOption.Remembered(
                selectedId,
                Clean(DeviceSerial) ??
                SerialFromIdentity(selectedId),
                _localization);
            target = remembered;
        }
        target ??= _allowLegacySerialMigration && Clean(DeviceSerial) is { } serial
            ? choices.FirstOrDefault(device => StringComparer.Ordinal.Equals(device.Serial, serial))
            : null;
        target ??= _manualSelectionPreference != true && Clean(DeviceSerial) is null &&
            choices.Count(device => device.IsReady) == 1
            ? choices.Single(device => device.IsReady)
            : _manualDeviceOption;

        _updatingDeviceSelection = true;
        try
        {
            AvailableDevices.Clear();
            foreach (DeviceSelectionOption choice in choices) AvailableDevices.Add(choice);
            if (remembered is not null) AvailableDevices.Add(remembered);
            AvailableDevices.Add(_manualDeviceOption);
            SelectedDevice = target;

            if (!target.IsManual)
            {
                bool migratedLegacyProfile = string.IsNullOrWhiteSpace(_selectedDeviceId) &&
                    StringComparer.Ordinal.Equals(Clean(DeviceSerial), target.Serial);
                _loadingProfile = true;
                try { DeviceSerial = target.Serial!; }
                finally { _loadingProfile = false; }
                _selectedDeviceId = target.Id;
                _manualSelectionPreference = false;
                _allowLegacySerialMigration = false;
                _activeDeviceKey = target.Id!;
                if (migratedLegacyProfile || !_deviceDirectories.ContainsKey(_activeDeviceKey))
                    RememberCurrentDirectories();
            }
            else
            {
                _selectedDeviceId = null;
                _activeDeviceKey = KeyForManualDevice();
            }
        }
        finally { _updatingDeviceSelection = false; }

        SaveProfile();
        if ((previousDeviceId is not null &&
             !StringComparer.Ordinal.Equals(previousDeviceId, target.Id)) ||
            !target.IsAvailable)
            InvalidatePreview();
        PreviewCommand.NotifyCanExecuteChanged();
        InitializeCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasNoEnumeratedDevices));
        OnPropertyChanged(nameof(NeedsDeviceSelection));
    }

    private void ShowManualDeviceOption()
    {
        RememberCurrentDirectories();
        bool hadEnumeratedSelection = _selectedDeviceId is not null;
        _updatingDeviceSelection = true;
        try
        {
            AvailableDevices.Clear();
            DeviceSelectionOption? remembered = _selectedDeviceId is { } selectedId
                ? DeviceSelectionOption.Remembered(
                    selectedId,
                    Clean(DeviceSerial) ??
                    SerialFromIdentity(selectedId),
                    _localization)
                : null;
            if (remembered is not null) AvailableDevices.Add(remembered);
            AvailableDevices.Add(_manualDeviceOption);
            SelectedDevice =
                remembered ??
                _manualDeviceOption;
            if (remembered is not null)
            {
                _activeDeviceKey = remembered.Id!;
            }
            else
            {
                _manualDeviceSerial = DeviceSerial;
                _activeDeviceKey = KeyForManualDevice();
            }
            RememberCurrentDirectories();
        }
        finally { _updatingDeviceSelection = false; }
        if (hadEnumeratedSelection) InvalidatePreview();
        PreviewCommand.NotifyCanExecuteChanged();
        InitializeCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(NeedsDeviceSelection));
    }

    partial void OnSelectedDeviceChanged(
        DeviceSelectionOption? oldValue,
        DeviceSelectionOption? newValue)
    {
        if (_updatingDeviceSelection || newValue is null) return;

        RememberCurrentDirectories();
        string nextSerial = newValue.IsManual ? _manualDeviceSerial : newValue.Serial!;
        string nextKey = newValue.IsManual
            ? KeyForManualDevice(nextSerial)
            : newValue.Id!;
        _loadingProfile = true;
        try
        {
            DeviceSerial = nextSerial;
            LoadDirectories(nextKey);
        }
        finally { _loadingProfile = false; }

        _selectedDeviceId = newValue.Id;
        _manualSelectionPreference = newValue.IsManual;
        _allowLegacySerialMigration = false;
        _activeDeviceKey = nextKey;
        SaveProfile();
        InvalidatePreview();
        PreviewCommand.NotifyCanExecuteChanged();
        InitializeCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
    }

    private void InputChanged()
    {
        if (_loadingProfile) return;
        SaveProfile();
        InvalidatePreview();
        PreviewCommand.NotifyCanExecuteChanged();
        InitializeCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
    }

    partial void OnSourcePathChanged(string value) => InputChanged();
    partial void OnDestinationPathChanged(string value) => InputChanged();
    partial void OnDeviceSerialChanged(string? oldValue, string newValue)
    {
        if (_loadingProfile) return;
        if (SelectedDevice?.IsManual != false)
        {
            RememberCurrentDirectories();
            _manualDeviceSerial = newValue;
            _manualSelectionPreference = true;
            _allowLegacySerialMigration = false;
            string nextKey = KeyForManualDevice(newValue);
            _activeDeviceKey = nextKey;
            _loadingProfile = true;
            try { LoadDirectories(nextKey); }
            finally { _loadingProfile = false; }
            SaveProfile();
            InvalidatePreview();
            PreviewCommand.NotifyCanExecuteChanged();
            InitializeCommand.NotifyCanExecuteChanged();
            RestoreCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(NeedsDeviceSelection));
            return;
        }
        InputChanged();
    }
    partial void OnAdbPathChanged(string value) => InputChanged();
    partial void OnExclusionsChanged(string value) => InputChanged();
    partial void OnMtimeToleranceSecondsChanged(int value) => InputChanged();
    partial void OnMaxRemovalsChanged(int? value) => InputChanged();
    partial void OnDeleteExtrasChanged(bool value) => InputChanged();
    partial void OnDirectChanged(bool value) => InputChanged();
    partial void OnAdoptChanged(bool value)
    {
        if (!_loadingProfile) SaveProfile();
    }

    private void InvalidatePreview()
    {
        if (_plan is not null)
        {
            try { File.Delete(_plan.PlanFilePath); }
            catch { }
        }
        _plan = null;
        HasApplicablePreview = false;
        Actions.Clear();
        OnPropertyChanged(nameof(IsActionListEmpty));
        Issues.Clear();
    }

    private void UpdateActionStatus(OperationProgress progress)
    {
        if (progress.ItemStatus is not { } status || string.IsNullOrEmpty(progress.CurrentPath))
            return;
        DeviceSyncActionRow? row = Actions.FirstOrDefault(candidate =>
            StringComparer.Ordinal.Equals(candidate.RelativePath, progress.CurrentPath));
        row?.SetStatus(status);
    }

    private void LoadProfile()
    {
        _loadingProfile = true;
        try
        {
            ResetProfile();
            Profile? profile = JsonSerializer.Deserialize<Profile>(
                _settings.GetLibraryPreference(ProfilePreference) ?? "null");
            if (profile is null) return;
            SourcePath = profile.SourcePath ?? "";
            DestinationPath = string.IsNullOrWhiteSpace(profile.DestinationPath) ? "music" : profile.DestinationPath;
            DeviceSerial = profile.DeviceSerial ?? "";
            AdbPath = profile.AdbPath ?? "";
            Exclusions = profile.Exclusions ?? "";
            MtimeToleranceSeconds = Math.Max(0, profile.MtimeToleranceSeconds);
            MaxRemovals = profile.MaxRemovals is { } maximum
                ? Math.Max(0, maximum)
                : null;
            DeleteExtras = profile.DeleteExtras;
            Direct = profile.Direct;
            Adopt = profile.Adopt;
            _recoveryId = Clean(profile.RecoveryId ?? "");
            _recoveryDestination = Clean(profile.RecoveryDestination ?? "");
            _recoveryDeviceSerial = Clean(profile.RecoveryDeviceSerial ?? "");
            _recoveryDeviceId = Clean(profile.RecoveryDeviceId ?? "");
            _selectedDeviceId = Clean(profile.SelectedDeviceId ?? "");
            _manualDeviceSerial = profile.ManualDeviceSerial ?? profile.DeviceSerial ?? "";
            switch (profile.DeviceSelectionMode)
            {
                case "manual":
                    _manualSelectionPreference = true;
                    _allowLegacySerialMigration = false;
                    break;
                case "device":
                    _manualSelectionPreference = false;
                    _allowLegacySerialMigration = false;
                    break;
                case "automatic":
                    _manualSelectionPreference = null;
                    _allowLegacySerialMigration = false;
                    break;
                case "legacy":
                    _manualSelectionPreference = null;
                    _allowLegacySerialMigration = true;
                    break;
                default:
                    _manualSelectionPreference = _selectedDeviceId is not null
                        ? false
                        : profile.ManualDeviceSerial is not null ? true : null;
                    _allowLegacySerialMigration = _selectedDeviceId is null &&
                        profile.ManualDeviceSerial is null;
                    break;
            }
            if (profile.DeviceDirectories is not null)
            {
                foreach ((string key, DeviceDirectories directories) in profile.DeviceDirectories)
                {
                    if (!string.IsNullOrWhiteSpace(key) && directories is not null)
                        _deviceDirectories[key] = directories;
                }
            }
            _activeDeviceKey = _selectedDeviceId ??
                (_allowLegacySerialMigration && Clean(DeviceSerial) is { } legacySerial
                    ? legacySerial
                    : KeyForManualDevice(DeviceSerial));
        }
        catch { }
        finally { _loadingProfile = false; }
    }

    private void ResetProfile()
    {
        SourcePath = "";
        DestinationPath = "music";
        DeviceSerial = "";
        AdbPath = "";
        Exclusions = "";
        MtimeToleranceSeconds = 60;
        MaxRemovals = null;
        DeleteExtras = true;
        Direct = false;
        Adopt = false;
        _deviceDirectories.Clear();
        _activeDeviceKey = ManualProfileKey;
        _manualDeviceSerial = "";
        _selectedDeviceId = null;
        _allowLegacySerialMigration = false;
        _manualSelectionPreference = null;
        _recoveryId = null;
        _recoveryDestination = null;
        _recoveryDeviceSerial = null;
        _recoveryDeviceId = null;
    }

    private void RememberCurrentDirectories() => _deviceDirectories[_activeDeviceKey] =
        new(SourcePath, DestinationPath);

    private void LoadDirectories(string key)
    {
        if (_deviceDirectories.TryGetValue(key, out DeviceDirectories? directories))
        {
            SourcePath = directories.SourcePath ?? "";
            DestinationPath = string.IsNullOrWhiteSpace(directories.DestinationPath)
                ? "music"
                : directories.DestinationPath;
        }
        else
        {
            SourcePath = "";
            DestinationPath = "music";
        }
    }

    private string KeyForManualDevice(string? serial = null) =>
        Clean(serial ?? DeviceSerial) is { } value
            ? $"{ManualProfileKey}|{value}"
            : ManualProfileKey;

    private static string SerialFromIdentity(string id)
    {
        int separator = id.LastIndexOf('|');
        return separator >= 0 && separator < id.Length - 1 ? id[(separator + 1)..] : id;
    }

    private void SaveProfile()
    {
        if (!_loadingProfile) RememberCurrentDirectories();
        _settings.SetLibraryPreference(ProfilePreference, JsonSerializer.Serialize(new Profile(
            SourcePath, DestinationPath, DeviceSerial, AdbPath, Exclusions,
            MtimeToleranceSeconds, MaxRemovals, DeleteExtras, Direct, Adopt,
            _recoveryId, _recoveryDestination, _recoveryDeviceSerial,
            new Dictionary<string, DeviceDirectories>(_deviceDirectories, StringComparer.Ordinal),
            _selectedDeviceId, _manualDeviceSerial, _recoveryDeviceId,
            _allowLegacySerialMigration
                ? "legacy"
                : _manualSelectionPreference switch
            {
                true => "manual",
                false => "device",
                null => "automatic",
            })));
    }

    private void SetRecovery(
        string recoveryId,
        string destination,
        string? deviceSerial,
        string? deviceId)
    {
        _recoveryId = recoveryId;
        _recoveryDestination = destination;
        _recoveryDeviceSerial = deviceSerial;
        _recoveryDeviceId = deviceId;
        SaveProfile();
        RestoreCommand.NotifyCanExecuteChanged();
    }

    private void ClearRecovery()
    {
        _recoveryId = null;
        _recoveryDestination = null;
        _recoveryDeviceSerial = null;
        _recoveryDeviceId = null;
        SaveProfile();
        RestoreCommand.NotifyCanExecuteChanged();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record DeviceDirectories(string? SourcePath, string? DestinationPath);

    private sealed record Profile(
        string? SourcePath,
        string? DestinationPath,
        string? DeviceSerial,
        string? AdbPath,
        string? Exclusions,
        int MtimeToleranceSeconds,
        int? MaxRemovals,
        bool DeleteExtras,
        bool Direct,
        bool Adopt,
        string? RecoveryId = null,
        string? RecoveryDestination = null,
        string? RecoveryDeviceSerial = null,
        Dictionary<string, DeviceDirectories>? DeviceDirectories = null,
        string? SelectedDeviceId = null,
        string? ManualDeviceSerial = null,
        string? RecoveryDeviceId = null,
        string? DeviceSelectionMode = null);
}
