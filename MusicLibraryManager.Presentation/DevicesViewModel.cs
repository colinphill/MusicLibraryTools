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

public partial class DevicesViewModel : ViewModelBase
{
    private const string ProfilePreference = "manager.devices.profile.v1";
    private readonly IDeviceSyncService _sync;
    private readonly IAppSettings _settings;
    private readonly IFilePickerService _files;
    private readonly IDialogCoordinator _dialogs;
    private readonly IActivityService _activities;
    private CancellationTokenSource? _cts;
    private DeviceSyncPlan? _plan;
    private bool _loadingProfile;
    private string? _recoveryId;
    private string? _recoveryDestination;
    private string? _recoveryDeviceSerial;

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
    [ObservableProperty] private string _statusText =
        "Choose a local source and initialize a managed Android destination before previewing.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(InitializeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(IsConfigurationEnabled))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _hasApplicablePreview;

    public ObservableCollection<DeviceSyncActionRow> Actions { get; } = [];
    public ObservableCollection<OperationIssue> Issues { get; } = [];
    public bool IsConfigurationEnabled => !IsBusy;

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
    }

    private bool CanPreview() => !IsBusy && !string.IsNullOrWhiteSpace(SourcePath) &&
        !string.IsNullOrWhiteSpace(DestinationPath);
    private bool CanApply() => !IsBusy && HasApplicablePreview && _plan?.CanApply == true;
    private bool CanInitialize() => !IsBusy && !string.IsNullOrWhiteSpace(DestinationPath);
    private bool CanRestore() => !IsBusy && !string.IsNullOrWhiteSpace(_recoveryId) &&
        StringComparer.Ordinal.Equals(DestinationPath.Trim(), _recoveryDestination) &&
        (Clean(DeviceSerial) is null ||
         StringComparer.Ordinal.Equals(Clean(DeviceSerial), _recoveryDeviceSerial));
    private bool CanCancel() => IsBusy;

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
                    Clean(result.DeviceSerial));
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
        Guid activity = _activities.Start(activityTitle, "Starting…");
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
    partial void OnDeviceSerialChanged(string value) => InputChanged();
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
            Profile? profile = JsonSerializer.Deserialize<Profile>(
                _settings.GetPreference(ProfilePreference) ?? "null");
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
        }
        catch { }
        finally { _loadingProfile = false; }
    }

    private void SaveProfile() => _settings.SetPreference(ProfilePreference, JsonSerializer.Serialize(new Profile(
        SourcePath, DestinationPath, DeviceSerial, AdbPath, Exclusions,
        MtimeToleranceSeconds, MaxRemovals, DeleteExtras, Direct, Adopt,
        _recoveryId, _recoveryDestination, _recoveryDeviceSerial)));

    private void SetRecovery(string recoveryId, string destination, string? deviceSerial)
    {
        _recoveryId = recoveryId;
        _recoveryDestination = destination;
        _recoveryDeviceSerial = deviceSerial;
        SaveProfile();
        RestoreCommand.NotifyCanExecuteChanged();
    }

    private void ClearRecovery()
    {
        _recoveryId = null;
        _recoveryDestination = null;
        _recoveryDeviceSerial = null;
        SaveProfile();
        RestoreCommand.NotifyCanExecuteChanged();
    }

    private static string? Clean(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
        string? RecoveryDeviceSerial = null);
}
