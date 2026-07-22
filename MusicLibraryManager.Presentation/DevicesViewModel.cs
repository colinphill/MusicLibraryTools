using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

public sealed partial class DeviceSyncActionRow(DeviceSyncAction action) : ObservableObject
{
    public DeviceSyncMutationKind Kind => action.Kind;
    public string RelativePath => action.RelativePath;
    public string Reason => action.Reason;
    public bool IsDirectory => action.IsDirectory;
    public long Length => action.Length;
    public long ModifiedSeconds => action.ModifiedSeconds;

    [ObservableProperty] private string _status = "";

    public bool IsInProgress => Status == "In progress";

    public void SetStatus(OperationItemStatus status)
    {
        string next = status switch
        {
            OperationItemStatus.InProgress => "In progress",
            OperationItemStatus.Complete => "Complete",
            OperationItemStatus.Failed => "Failed",
            _ => "",
        };
        if ((Status == "Complete" || Status == "Failed") && next == "In progress") return;
        Status = next;
    }
}

public sealed record DeviceSelectionOption(
    string? Id,
    string? Serial,
    string DisplayName,
    string State,
    bool IsReady,
    string? Connection = null,
    bool IsRemembered = false)
{
    public bool IsManual => Id is null;
    public bool IsAvailable => IsManual || IsReady;
    public string Details => IsManual
        ? "Leave blank to use the only ready device"
        : IsRemembered
            ? $"{Serial} · not currently reported by ADB"
            : IsReady
            ? string.IsNullOrWhiteSpace(Connection) ? Serial! : $"{Serial} · {Connection}"
            : $"{Serial} · {State}";

    public static DeviceSelectionOption Manual { get; } =
        new(null, null, "Automatic or manual serial", "manual", true);

    public static DeviceSelectionOption FromDevice(DeviceSyncDevice device) => new(
        device.Id,
        device.Serial,
        string.IsNullOrWhiteSpace(device.DisplayName) ? device.Id : device.DisplayName,
        device.State,
        device.IsReady,
        device.Connection);

    public static DeviceSelectionOption Remembered(string id, string serial)
    {
        int separator = id.LastIndexOf('|');
        string model = separator > 0 ? id[..separator] : "Previously selected device";
        if (StringComparer.Ordinal.Equals(model, "unknown")) model = "Previously selected device";
        else model = model.Replace('_', ' ') + $" ({serial})";
        return new(id, serial, model, "not connected", false, IsRemembered: true);
    }
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
    private CancellationTokenSource? _cts;
    private DeviceSyncPlan? _plan;
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

    [ObservableProperty] private string _statusText =
        "Choose a local source and initialize a managed Android destination before previewing.";

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
    public ObservableCollection<OperationIssue> Issues { get; } = [];
    public ObservableCollection<DeviceSelectionOption> AvailableDevices { get; } = [];
    public bool IsConfigurationEnabled => !IsBusy && !IsLoadingDevices;
    public bool IsActionListEmpty => Actions.Count == 0;
    public bool IsManualDeviceSelected => SelectedDevice?.IsManual != false;
    public bool IsSelectedDeviceUnavailable => SelectedDevice is { IsAvailable: false };
    public bool HasDeviceEnumerationError => !string.IsNullOrWhiteSpace(DeviceEnumerationError);
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
        IActivityService activities)
    {
        _sync = sync;
        _settings = settings;
        _files = files;
        _dialogs = dialogs;
        _activities = activities;
        LoadProfile();
        settings.ConfigurationChanged += (_, _) =>
        {
            InvalidatePreview();
            LoadProfile();
        };
        _updatingDeviceSelection = true;
        AvailableDevices.Add(DeviceSelectionOption.Manual);
        SelectedDevice = DeviceSelectionOption.Manual;
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
            DeviceEnumerationError = error.Message;
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
        string? path = await _files.PickFolderAsync("Choose the local folder to mirror");
        if (path is not null) SourcePath = path;
    }

    [RelayCommand]
    private async Task BrowseAdbAsync()
    {
        string? path = await _files.PickFileAsync("Choose adb executable");
        if (path is not null) AdbPath = path;
    }

    [RelayCommand(CanExecute = nameof(CanInitialize))]
    private async Task InitializeAsync()
    {
        string action = Adopt ? "Adopt" : "Initialize";
        string warning = Adopt
            ? $"Adopt '{DestinationPath}' as a managed syncer destination? Existing contents will be included in the first preview and may be quarantined when Apply is allowed."
            : $"Initialize '{DestinationPath}' as a managed syncer destination? The folder must be new or empty.";
        if (!await _dialogs.ConfirmAsync($"{action} Android destination", warning, action))
            return;

        await RunAsync("Initialize Android destination", async (progress, ct) =>
        {
            DeviceSyncInitializationResult result = await _sync.InitializeAsync(
                new(DestinationPath.Trim(), Clean(DeviceSerial), Clean(AdbPath), Adopt), progress, ct);
            StatusText = string.IsNullOrWhiteSpace(result.Message)
                ? "Android destination initialized."
                : result.Message;
        });
    }

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewAsync()
    {
        InvalidatePreview();
        await RunAsync("Preview Android synchronization", async (progress, ct) =>
        {
            DeviceSyncPlan plan = await _sync.PreviewAsync(CreateRequest(), progress, ct);
            _plan = plan;
            foreach (DeviceSyncAction action in plan.Actions) Actions.Add(new(action));
            OnPropertyChanged(nameof(IsActionListEmpty));
            foreach (OperationIssue issue in plan.Issues) Issues.Add(issue);
            HasApplicablePreview = plan.CanApply;
            StatusText = plan.CanApply
                ? $"Review {plan.Actions.Count:N0} action(s), {plan.RemovalCount:N0} removal(s), and {plan.TransferBytes:N0} transfer byte(s)."
                : plan.Issues.FirstOrDefault()?.Message ?? "Preview is blocked.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        DeviceSyncPlan plan = _plan ?? throw new InvalidOperationException("Preview is required.");
        string recovery = plan.Request.Direct
            ? "Recovery is not available because Direct transfer bypasses staging."
            : "Recovery is available: replaced and removed destination items will be quarantined in a recovery run.";
        if (!await _dialogs.ConfirmAsync(
                "Apply Android synchronization",
                $"Apply {plan.Actions.Count:N0} planned action(s), including " +
                $"{plan.RemovalCount:N0} removal(s), and transfer {plan.TransferBytes:N0} byte(s)?\n\n{recovery}",
                plan.Request.Direct ? "Apply without recovery" : "Synchronize"))
            return;
        await RunAsync("Synchronize Android device", async (progress, ct) =>
        {
            DeviceSyncResult result;
            try
            {
                result = await _sync.ApplyAsync(plan, progress, ct);
            }
            catch
            {
                foreach (DeviceSyncActionRow row in Actions.Where(row => row.IsInProgress))
                    row.SetStatus(OperationItemStatus.Failed);
                throw;
            }
            foreach (DeviceSyncActionRow row in Actions)
                row.SetStatus(OperationItemStatus.Complete);
            StatusText = $"Synchronized {result.CopiedFileCount:N0} file(s), " +
                $"quarantined {result.QuarantinedCount:N0} path(s), and transferred " +
                $"{result.TransferredBytes:N0} byte(s)." +
                (result.RecoveryId is null ? "" : $" Recovery run: {result.RecoveryId}.");
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
        string recoveryId = _recoveryId ?? throw new InvalidOperationException("No recovery run is available.");
        string destination = _recoveryDestination ?? throw new InvalidOperationException("No recovery destination is available.");
        if (!await _dialogs.ConfirmAsync("Restore previous Android synchronization",
            $"Restore recovery run '{recoveryId}' at '{destination}'? The synchronized files will be removed, quarantined originals will be restored, and displaced current content will remain preserved in the recovery run.",
            "Restore"))
            return;

        await RunAsync("Restore Android synchronization", async (progress, ct) =>
        {
            DeviceSyncRestoreResult result = await _sync.RestoreAsync(
                new(destination, recoveryId, _recoveryDeviceSerial, Clean(AdbPath)), progress, ct);
            ClearRecovery();
            InvalidatePreview();
            StatusText = $"Restored recovery run {result.RecoveryId} on {result.DeviceSerial}.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private async Task RunAsync(
        string activityTitle,
        Func<IProgress<OperationProgress>, CancellationToken, Task> operation)
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        Guid activity = _activities.Start(
            activityTitle, "Starting…", ShellDestination.Devices, Cancel);
        var progress = new Progress<OperationProgress>(value =>
        {
            UpdateActionStatus(value);
            string message = value.Message ?? value.Phase.ToString();
            StatusText = message;
            double? fraction = value.Total is > 0 ? (double)value.Completed / value.Total : null;
            _activities.Report(activity, message, fraction);
        });
        try
        {
            await operation(progress, _cts.Token);
            _activities.Finish(activity, StatusText);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Operation cancelled. The native server will release the destination lock.";
            _activities.Finish(activity, StatusText, AppActivityState.Cancelled);
        }
        catch (Exception error)
        {
            StatusText = error.Message;
            _activities.Finish(activity, error.Message, AppActivityState.Failed);
            await _dialogs.ShowMessageAsync($"{activityTitle} failed", error.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
        }
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
            .Select(group => DeviceSelectionOption.FromDevice(group.First()))
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
                selectedId, Clean(DeviceSerial) ?? SerialFromIdentity(selectedId));
            target = remembered;
        }
        target ??= _allowLegacySerialMigration && Clean(DeviceSerial) is { } serial
            ? choices.FirstOrDefault(device => StringComparer.Ordinal.Equals(device.Serial, serial))
            : null;
        target ??= _manualSelectionPreference != true && Clean(DeviceSerial) is null &&
            choices.Count(device => device.IsReady) == 1
            ? choices.Single(device => device.IsReady)
            : DeviceSelectionOption.Manual;

        _updatingDeviceSelection = true;
        try
        {
            AvailableDevices.Clear();
            foreach (DeviceSelectionOption choice in choices) AvailableDevices.Add(choice);
            if (remembered is not null) AvailableDevices.Add(remembered);
            AvailableDevices.Add(DeviceSelectionOption.Manual);
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
                    selectedId, Clean(DeviceSerial) ?? SerialFromIdentity(selectedId))
                : null;
            if (remembered is not null) AvailableDevices.Add(remembered);
            AvailableDevices.Add(DeviceSelectionOption.Manual);
            SelectedDevice = remembered ?? DeviceSelectionOption.Manual;
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
