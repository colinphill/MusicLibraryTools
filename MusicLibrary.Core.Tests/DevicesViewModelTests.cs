using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class DevicesViewModelTests
{
    [Fact]
    public async Task PreviewLaunchFailureShowsAnExplicitDialog()
    {
        using var temp = new TempDirectory();
        var dialogs = new RecordingDialogs();
        var viewModel = new DevicesViewModel(
            new FailingDeviceSyncService(),
            new AppSettings(Path.Combine(temp.Path, "settings.json")),
            new StubFiles(), dialogs, new AppActivityService())
        {
            SourcePath = temp.Path,
            DestinationPath = "/sdcard/Music",
        };

        await viewModel.PreviewCommand.ExecuteAsync(null);

        Assert.Equal("Preview Android synchronization failed", dialogs.Title);
        Assert.Contains("syncer executable was not found", dialogs.Message);
        Assert.Equal(dialogs.Message, viewModel.StatusText);
    }

    [Fact]
    public async Task SuccessfulApplyEnablesOneClickRestore()
    {
        using var temp = new TempDirectory();
        var sync = new RestorableDeviceSyncService(temp.Path);
        var viewModel = new DevicesViewModel(sync,
            new AppSettings(Path.Combine(temp.Path, "settings.json")),
            new StubFiles(), new RecordingDialogs(), new AppActivityService())
        {
            SourcePath = temp.Path,
            DestinationPath = "/sdcard/Music",
        };

        await viewModel.PreviewCommand.ExecuteAsync(null);
        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.True(viewModel.RestoreCommand.CanExecute(null));
        await viewModel.RestoreCommand.ExecuteAsync(null);

        Assert.NotNull(sync.RestoreRequest);
        Assert.Equal("run-1", sync.RestoreRequest!.RecoveryId);
        Assert.Equal("/sdcard/Music", sync.RestoreRequest.Destination);
        Assert.False(viewModel.RestoreCommand.CanExecute(null));
        Assert.Contains("Restored recovery run run-1", viewModel.StatusText);
    }

    [Fact]
    public async Task ApplyRequiresAPlanAndRecoverySummaryConfirmation()
    {
        using var temp = new TempDirectory();
        var sync = new ProgressDeviceSyncService(temp.Path);
        var dialogs = new RecordingDialogs { ConfirmResult = false };
        var viewModel = new DevicesViewModel(sync,
            new AppSettings(Path.Combine(temp.Path, "settings.json")),
            new StubFiles(), dialogs, new AppActivityService())
        {
            SourcePath = temp.Path,
            DestinationPath = "/sdcard/Music",
        };

        await viewModel.PreviewCommand.ExecuteAsync(null);
        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.False(sync.ApplyStarted.Task.IsCompleted);
        Assert.Contains("2 planned action", dialogs.ConfirmationMessage);
        Assert.Contains("Recovery is available", dialogs.ConfirmationMessage);
        Assert.True(viewModel.ApplyCommand.CanExecute(null));
        Assert.False(viewModel.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task DirectApplyDoesNotSaveRecoveryInformation()
    {
        using var temp = new TempDirectory();
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        var sync = new DirectDeviceSyncService(temp.Path);
        var dialogs = new RecordingDialogs();
        var viewModel = new DevicesViewModel(sync,
            new AppSettings(settingsPath), new StubFiles(),
            dialogs, new AppActivityService())
        {
            SourcePath = temp.Path,
            DestinationPath = "/sdcard/Music",
        };

        await viewModel.PreviewCommand.ExecuteAsync(null);
        await viewModel.ApplyCommand.ExecuteAsync(null);
        Assert.True(viewModel.RestoreCommand.CanExecute(null));

        viewModel.Direct = true;
        await viewModel.PreviewCommand.ExecuteAsync(null);
        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.False(viewModel.RestoreCommand.CanExecute(null));
        Assert.DoesNotContain("Recovery run:", viewModel.StatusText);
        Assert.Contains("Recovery is not available", dialogs.ConfirmationMessage);
        var reloaded = new DevicesViewModel(sync, new AppSettings(settingsPath),
            new StubFiles(), new RecordingDialogs(), new AppActivityService());
        Assert.False(reloaded.RestoreCommand.CanExecute(null));
    }

    [Fact]
    public void MaximumRemovalsPersistsBlankAndZeroAsDifferentValues()
    {
        using var temp = new TempDirectory();
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        var sync = new DirectDeviceSyncService(temp.Path);
        var blank = new DevicesViewModel(sync, new AppSettings(settingsPath),
            new StubFiles(), new RecordingDialogs(), new AppActivityService());
        Assert.Null(blank.MaxRemovals);

        blank.MaxRemovals = 0;
        var reloaded = new DevicesViewModel(sync, new AppSettings(settingsPath),
            new StubFiles(), new RecordingDialogs(), new AppActivityService());

        Assert.Equal(0, reloaded.MaxRemovals);
    }

    [Fact]
    public async Task ApplyShowsBlankInProgressAndCompleteStatusesPerAction()
    {
        using var temp = new TempDirectory();
        var sync = new ProgressDeviceSyncService(temp.Path);
        var viewModel = new DevicesViewModel(sync,
            new AppSettings(Path.Combine(temp.Path, "settings.json")),
            new StubFiles(), new RecordingDialogs(), new AppActivityService())
        {
            SourcePath = temp.Path,
            DestinationPath = "/sdcard/Music",
        };

        await viewModel.PreviewCommand.ExecuteAsync(null);

        Assert.All(viewModel.Actions, row => Assert.Equal("", row.Status));
        Task apply = viewModel.ApplyCommand.ExecuteAsync(null);
        await sync.ApplyStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => viewModel.Actions[0].Status == "In progress");
        Assert.False(viewModel.IsConfigurationEnabled);
        Assert.Equal("", viewModel.Actions[1].Status);

        sync.ReleaseApply.TrySetResult();
        await apply;

        Assert.True(viewModel.IsConfigurationEnabled);
        Assert.All(viewModel.Actions, row => Assert.Equal("Complete", row.Status));
    }

    [Fact]
    public async Task ApplyMarksTheActiveActionFailedAndLeavesFutureActionsBlank()
    {
        using var temp = new TempDirectory();
        var sync = new ProgressDeviceSyncService(temp.Path) { FailApply = true };
        var viewModel = new DevicesViewModel(sync,
            new AppSettings(Path.Combine(temp.Path, "settings.json")),
            new StubFiles(), new RecordingDialogs(), new AppActivityService())
        {
            SourcePath = temp.Path,
            DestinationPath = "/sdcard/Music",
        };

        await viewModel.PreviewCommand.ExecuteAsync(null);
        Task apply = viewModel.ApplyCommand.ExecuteAsync(null);
        await sync.ApplyStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => viewModel.Actions[0].Status == "In progress");

        sync.ReleaseApply.TrySetResult();
        await apply;
        await WaitUntilAsync(() => viewModel.Actions[0].Status == "Failed");

        Assert.True(viewModel.IsConfigurationEnabled);
        Assert.Equal("", viewModel.Actions[1].Status);
    }

    [Fact]
    public async Task CancellingApplyReenablesConfiguration()
    {
        using var temp = new TempDirectory();
        var sync = new ProgressDeviceSyncService(temp.Path);
        var viewModel = new DevicesViewModel(sync,
            new AppSettings(Path.Combine(temp.Path, "settings.json")),
            new StubFiles(), new RecordingDialogs(), new AppActivityService())
        {
            SourcePath = temp.Path,
            DestinationPath = "/sdcard/Music",
        };

        await viewModel.PreviewCommand.ExecuteAsync(null);
        Task apply = viewModel.ApplyCommand.ExecuteAsync(null);
        await sync.ApplyStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(viewModel.IsConfigurationEnabled);

        viewModel.CancelCommand.Execute(null);
        await apply;

        Assert.True(viewModel.IsConfigurationEnabled);
        Assert.Contains("cancelled", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SwitchingDevicesRestoresEachDevicesDirectories()
    {
        using var temp = new TempDirectory();
        var sync = new EnumeratingDeviceSyncService(temp.Path);
        var viewModel = new DevicesViewModel(sync,
            new AppSettings(Path.Combine(temp.Path, "settings.json")),
            new StubFiles(), new RecordingDialogs(), new AppActivityService());

        await viewModel.RefreshDevicesCommand.ExecuteAsync(null);
        DeviceSelectionOption phone = viewModel.AvailableDevices.Single(device => device.Id == "Pixel 9|phone");
        DeviceSelectionOption tablet = viewModel.AvailableDevices.Single(device => device.Id == "Pixel Tablet|tablet");

        viewModel.SelectedDevice = phone;
        viewModel.SourcePath = @"C:\Music\Phone";
        viewModel.DestinationPath = "/sdcard/Phone Music";
        viewModel.SelectedDevice = tablet;
        viewModel.SourcePath = @"D:\Music\Tablet";
        viewModel.DestinationPath = "music";

        viewModel.SelectedDevice = phone;
        Assert.Equal(@"C:\Music\Phone", viewModel.SourcePath);
        Assert.Equal("/sdcard/Phone Music", viewModel.DestinationPath);
        Assert.Equal("phone", viewModel.DeviceSerial);

        viewModel.SelectedDevice = tablet;
        Assert.Equal(@"D:\Music\Tablet", viewModel.SourcePath);
        Assert.Equal("music", viewModel.DestinationPath);
        Assert.Equal("tablet", viewModel.DeviceSerial);
    }

    [Fact]
    public async Task SelectedDeviceAndDirectoriesPersistAcrossViewModelRecreation()
    {
        using var temp = new TempDirectory();
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        var sync = new EnumeratingDeviceSyncService(temp.Path);
        var first = new DevicesViewModel(sync, new AppSettings(settingsPath),
            new StubFiles(), new RecordingDialogs(), new AppActivityService());
        await first.RefreshDevicesCommand.ExecuteAsync(null);
        first.SelectedDevice = first.AvailableDevices.Single(device => device.Id == "Pixel Tablet|tablet");
        first.SourcePath = @"D:\Collection";
        first.DestinationPath = "/sdcard/Tablet Music";

        var reloaded = new DevicesViewModel(sync, new AppSettings(settingsPath),
            new StubFiles(), new RecordingDialogs(), new AppActivityService());
        await reloaded.RefreshDevicesCommand.ExecuteAsync(null);

        Assert.Equal("Pixel Tablet|tablet", reloaded.SelectedDevice?.Id);
        Assert.Equal("tablet", reloaded.DeviceSerial);
        Assert.Equal(@"D:\Collection", reloaded.SourcePath);
        Assert.Equal("/sdcard/Tablet Music", reloaded.DestinationPath);
    }

    [Fact]
    public async Task LegacySerialProfileMigratesToCompositeDeviceIdentity()
    {
        using var temp = new TempDirectory();
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        var settings = new AppSettings(settingsPath);
        settings.SetPreference("manager.devices.profile.v1", System.Text.Json.JsonSerializer.Serialize(new
        {
            SourcePath = @"C:\Legacy Music",
            DestinationPath = "/sdcard/Legacy",
            DeviceSerial = "phone",
            AdbPath = "",
            Exclusions = "",
            MtimeToleranceSeconds = 60,
            MaxRemovals = (int?)null,
            DeleteExtras = true,
            Direct = false,
            Adopt = false,
        }));
        var sync = new EnumeratingDeviceSyncService(temp.Path);
        var migrated = new DevicesViewModel(sync, new AppSettings(settingsPath),
            new StubFiles(), new RecordingDialogs(), new AppActivityService());

        await migrated.RefreshDevicesCommand.ExecuteAsync(null);

        Assert.Equal("Pixel 9|phone", migrated.SelectedDevice?.Id);
        Assert.Equal("phone", migrated.DeviceSerial);
        Assert.Equal(@"C:\Legacy Music", migrated.SourcePath);
        Assert.Equal("/sdcard/Legacy", migrated.DestinationPath);

        var reloaded = new DevicesViewModel(sync, new AppSettings(settingsPath),
            new StubFiles(), new RecordingDialogs(), new AppActivityService());
        await reloaded.RefreshDevicesCommand.ExecuteAsync(null);
        Assert.Equal("Pixel 9|phone", reloaded.SelectedDevice?.Id);
        Assert.Equal(@"C:\Legacy Music", reloaded.SourcePath);
    }

    [Fact]
    public async Task SelectedRawSerialReachesInitializeAndPreviewRequests()
    {
        using var temp = new TempDirectory();
        var sync = new EnumeratingDeviceSyncService(temp.Path);
        var viewModel = new DevicesViewModel(sync,
            new AppSettings(Path.Combine(temp.Path, "settings.json")),
            new StubFiles(), new RecordingDialogs(), new AppActivityService());
        await viewModel.RefreshDevicesCommand.ExecuteAsync(null);
        viewModel.SelectedDevice = viewModel.AvailableDevices.Single(device => device.Id == "Pixel 9|phone");
        viewModel.SourcePath = temp.Path;
        viewModel.DestinationPath = "/sdcard/Music";

        await viewModel.InitializeCommand.ExecuteAsync(null);
        await viewModel.PreviewCommand.ExecuteAsync(null);

        Assert.Equal("phone", sync.InitializationRequest?.DeviceSerial);
        Assert.Equal("phone", sync.PreviewRequest?.DeviceSerial);
    }

    [Fact]
    public async Task DiscoveryFailureStaysInlineAndKeepsManualFallback()
    {
        using var temp = new TempDirectory();
        var sync = new EnumeratingDeviceSyncService(temp.Path)
        {
            EnumerationError = new InvalidOperationException("adb server is unavailable"),
        };
        var dialogs = new RecordingDialogs();
        var viewModel = new DevicesViewModel(sync,
            new AppSettings(Path.Combine(temp.Path, "settings.json")),
            new StubFiles(), dialogs, new AppActivityService());

        await viewModel.RefreshDevicesCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasDeviceEnumerationError);
        Assert.Contains("adb server is unavailable", viewModel.DeviceEnumerationError);
        Assert.True(viewModel.IsManualDeviceSelected);
        Assert.Single(viewModel.AvailableDevices);
        Assert.Null(dialogs.Title);
    }

    [Fact]
    public async Task EmptyDiscoveryShowsManualFallbackState()
    {
        using var temp = new TempDirectory();
        var viewModel = new DevicesViewModel(
            new EnumeratingDeviceSyncService(temp.Path) { Devices = [] },
            new AppSettings(Path.Combine(temp.Path, "settings.json")),
            new StubFiles(), new RecordingDialogs(), new AppActivityService());

        await viewModel.RefreshDevicesCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasCompletedDeviceEnumeration);
        Assert.True(viewModel.HasNoEnumeratedDevices);
        Assert.True(viewModel.IsManualDeviceSelected);
        Assert.Single(viewModel.AvailableDevices);
    }

    [Fact]
    public async Task UnavailableEnumeratedDeviceCannotInitializeOrPreview()
    {
        using var temp = new TempDirectory();
        var sync = new EnumeratingDeviceSyncService(temp.Path)
        {
            Devices =
            [
                new("Pixel 9|phone", "phone", "Pixel 9", "unauthorized", false,
                    Model: "Pixel 9", Connection: "usb"),
            ],
        };
        var viewModel = new DevicesViewModel(sync,
            new AppSettings(Path.Combine(temp.Path, "settings.json")),
            new StubFiles(), new RecordingDialogs(), new AppActivityService());
        await viewModel.RefreshDevicesCommand.ExecuteAsync(null);
        viewModel.SelectedDevice = viewModel.AvailableDevices.Single(device => device.Id == "Pixel 9|phone");
        viewModel.SourcePath = temp.Path;
        viewModel.DestinationPath = "/sdcard/Music";

        Assert.True(viewModel.IsSelectedDeviceUnavailable);
        Assert.False(viewModel.InitializeCommand.CanExecute(null));
        Assert.False(viewModel.PreviewCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeviceBecomingUnavailableBlocksAReviewedPlan()
    {
        using var temp = new TempDirectory();
        var sync = new EnumeratingDeviceSyncService(temp.Path);
        var viewModel = new DevicesViewModel(sync,
            new AppSettings(Path.Combine(temp.Path, "settings.json")),
            new StubFiles(), new RecordingDialogs(), new AppActivityService());
        await viewModel.RefreshDevicesCommand.ExecuteAsync(null);
        viewModel.SelectedDevice = viewModel.AvailableDevices.Single(device => device.Id == "Pixel 9|phone");
        viewModel.SourcePath = temp.Path;
        viewModel.DestinationPath = "/sdcard/Music";
        await viewModel.PreviewCommand.ExecuteAsync(null);
        Assert.True(viewModel.ApplyCommand.CanExecute(null));

        sync.Devices =
        [
            new("Pixel 9|phone", "phone", "Pixel 9", "offline", false,
                Model: "Pixel 9", Connection: "usb"),
        ];
        await viewModel.RefreshDevicesCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsSelectedDeviceUnavailable);
        Assert.False(viewModel.ApplyCommand.CanExecute(null));
        Assert.False(viewModel.HasApplicablePreview);
    }

    [Fact]
    public async Task MissingPersistedIdentityStaysSelectedAndDoesNotInheritSameSerialModelPaths()
    {
        using var temp = new TempDirectory();
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        var originalSync = new EnumeratingDeviceSyncService(temp.Path)
        {
            Devices = [Device("Model A", "same")],
        };
        var original = new DevicesViewModel(originalSync, new AppSettings(settingsPath),
            new StubFiles(), new RecordingDialogs(), new AppActivityService());
        await original.RefreshDevicesCommand.ExecuteAsync(null);
        original.SourcePath = @"C:\Model A";
        original.DestinationPath = "/sdcard/ModelA";

        var changedSync = new EnumeratingDeviceSyncService(temp.Path)
        {
            Devices = [Device("Model B", "same")],
        };
        var reloaded = new DevicesViewModel(changedSync, new AppSettings(settingsPath),
            new StubFiles(), new RecordingDialogs(), new AppActivityService());
        await reloaded.RefreshDevicesCommand.ExecuteAsync(null);

        Assert.Equal("Model A|same", reloaded.SelectedDevice?.Id);
        Assert.True(reloaded.SelectedDevice?.IsRemembered);
        Assert.True(reloaded.IsSelectedDeviceUnavailable);
        Assert.Equal(@"C:\Model A", reloaded.SourcePath);
        reloaded.SelectedDevice = reloaded.AvailableDevices.Single(device => device.Id == "Model B|same");
        Assert.Equal("", reloaded.SourcePath);
        Assert.Equal("music", reloaded.DestinationPath);
    }

    [Fact]
    public async Task DiscoveryErrorRetainsPersistedCompositeIdentity()
    {
        using var temp = new TempDirectory();
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        var first = new DevicesViewModel(
            new EnumeratingDeviceSyncService(temp.Path) { Devices = [Device("Model A", "same")] },
            new AppSettings(settingsPath), new StubFiles(), new RecordingDialogs(), new AppActivityService());
        await first.RefreshDevicesCommand.ExecuteAsync(null);
        first.SourcePath = @"C:\Model A";

        var failed = new DevicesViewModel(
            new EnumeratingDeviceSyncService(temp.Path)
            {
                EnumerationError = new InvalidOperationException("adb failed"),
            },
            new AppSettings(settingsPath), new StubFiles(), new RecordingDialogs(), new AppActivityService());
        await failed.RefreshDevicesCommand.ExecuteAsync(null);

        Assert.True(failed.HasDeviceEnumerationError);
        Assert.Equal("Model A|same", failed.SelectedDevice?.Id);
        Assert.True(failed.SelectedDevice?.IsRemembered);
        Assert.Equal(@"C:\Model A", failed.SourcePath);
    }

    [Fact]
    public async Task ManualSerialSwitchRestoresDirectoriesWithoutCopyingThem()
    {
        using var temp = new TempDirectory();
        var viewModel = new DevicesViewModel(
            new EnumeratingDeviceSyncService(temp.Path) { Devices = [] },
            new AppSettings(Path.Combine(temp.Path, "settings.json")),
            new StubFiles(), new RecordingDialogs(), new AppActivityService());
        await viewModel.RefreshDevicesCommand.ExecuteAsync(null);

        viewModel.DeviceSerial = "manual-a";
        viewModel.SourcePath = @"C:\Manual A";
        viewModel.DestinationPath = "/sdcard/A";
        viewModel.DeviceSerial = "manual-b";
        Assert.Equal("", viewModel.SourcePath);
        Assert.Equal("music", viewModel.DestinationPath);
        viewModel.SourcePath = @"D:\Manual B";
        viewModel.DestinationPath = "/sdcard/B";

        viewModel.DeviceSerial = "manual-a";
        Assert.Equal(@"C:\Manual A", viewModel.SourcePath);
        Assert.Equal("/sdcard/A", viewModel.DestinationPath);
        viewModel.DeviceSerial = "manual-b";
        Assert.Equal(@"D:\Manual B", viewModel.SourcePath);
        Assert.Equal("/sdcard/B", viewModel.DestinationPath);
    }

    [Fact]
    public async Task ExplicitManualSelectionDoesNotAutoMatchItsRawSerialAfterReload()
    {
        using var temp = new TempDirectory();
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        var first = new DevicesViewModel(
            new EnumeratingDeviceSyncService(temp.Path) { Devices = [] },
            new AppSettings(settingsPath), new StubFiles(), new RecordingDialogs(), new AppActivityService());
        await first.RefreshDevicesCommand.ExecuteAsync(null);
        first.DeviceSerial = "same";
        first.SourcePath = @"C:\Manual";

        var reloaded = new DevicesViewModel(
            new EnumeratingDeviceSyncService(temp.Path) { Devices = [Device("Model A", "same")] },
            new AppSettings(settingsPath), new StubFiles(), new RecordingDialogs(), new AppActivityService());
        await reloaded.RefreshDevicesCommand.ExecuteAsync(null);

        Assert.True(reloaded.IsManualDeviceSelected);
        Assert.Equal("same", reloaded.DeviceSerial);
        Assert.Equal(@"C:\Manual", reloaded.SourcePath);
        reloaded.SourcePath = @"C:\Manual Updated";
        reloaded.DeviceSerial = "other";
        reloaded.SourcePath = @"D:\Other";
        reloaded.DeviceSerial = "same";
        Assert.Equal(@"C:\Manual Updated", reloaded.SourcePath);
    }

    [Fact]
    public async Task MultipleReadyDevicesRequireAnExplicitSelection()
    {
        using var temp = new TempDirectory();
        var viewModel = new DevicesViewModel(new EnumeratingDeviceSyncService(temp.Path),
            new AppSettings(Path.Combine(temp.Path, "settings.json")),
            new StubFiles(), new RecordingDialogs(), new AppActivityService())
        {
            SourcePath = temp.Path,
        };

        await viewModel.RefreshDevicesCommand.ExecuteAsync(null);

        Assert.True(viewModel.NeedsDeviceSelection);
        Assert.False(viewModel.InitializeCommand.CanExecute(null));
        Assert.False(viewModel.PreviewCommand.CanExecute(null));
    }

    [Fact]
    public async Task PendingEnumerationDisablesConfigurationAndEveryMutationCommand()
    {
        using var temp = new TempDirectory();
        var sync = new EnumeratingDeviceSyncService(temp.Path)
        {
            Devices = [Device("Model A", "same")],
            AppliedRecoveryId = "run-1",
        };
        var viewModel = new DevicesViewModel(sync,
            new AppSettings(Path.Combine(temp.Path, "settings.json")),
            new StubFiles(), new RecordingDialogs(), new AppActivityService());
        await viewModel.RefreshDevicesCommand.ExecuteAsync(null);
        viewModel.SourcePath = temp.Path;
        viewModel.DestinationPath = "/sdcard/Music";
        await viewModel.PreviewCommand.ExecuteAsync(null);
        await viewModel.ApplyCommand.ExecuteAsync(null);
        await viewModel.PreviewCommand.ExecuteAsync(null);
        Assert.True(viewModel.ApplyCommand.CanExecute(null));
        Assert.True(viewModel.RestoreCommand.CanExecute(null));

        sync.EnumerationGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task refresh = viewModel.RefreshDevicesCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsLoadingDevices);
        Assert.False(viewModel.IsConfigurationEnabled);
        Assert.False(viewModel.InitializeCommand.CanExecute(null));
        Assert.False(viewModel.PreviewCommand.CanExecute(null));
        Assert.False(viewModel.ApplyCommand.CanExecute(null));
        Assert.False(viewModel.RestoreCommand.CanExecute(null));

        sync.EnumerationGate.SetResult(sync.Devices);
        await refresh;
        Assert.False(viewModel.IsLoadingDevices);
        Assert.True(viewModel.IsConfigurationEnabled);
    }

    [Fact]
    public async Task RecoveryUsesCompositeIdentityWhileLegacyRecoveryFallsBackToSerial()
    {
        using var temp = new TempDirectory();
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        var sync = new EnumeratingDeviceSyncService(temp.Path)
        {
            Devices = [Device("Model A", "same")],
            AppliedRecoveryId = "run-composite",
        };
        var first = new DevicesViewModel(sync, new AppSettings(settingsPath),
            new StubFiles(), new RecordingDialogs(), new AppActivityService());
        await first.RefreshDevicesCommand.ExecuteAsync(null);
        first.SourcePath = temp.Path;
        first.DestinationPath = "/sdcard/Music";
        await first.PreviewCommand.ExecuteAsync(null);
        await first.ApplyCommand.ExecuteAsync(null);

        var reloadedSync = new EnumeratingDeviceSyncService(temp.Path)
        {
            Devices = [Device("Model A", "same"), Device("Model B", "same")],
        };
        var reloaded = new DevicesViewModel(reloadedSync, new AppSettings(settingsPath),
            new StubFiles(), new RecordingDialogs(), new AppActivityService());
        await reloaded.RefreshDevicesCommand.ExecuteAsync(null);
        Assert.True(reloaded.RestoreCommand.CanExecute(null));
        reloaded.SelectedDevice = reloaded.AvailableDevices.Single(device => device.Id == "Model B|same");
        reloaded.DestinationPath = "/sdcard/Music";
        Assert.False(reloaded.RestoreCommand.CanExecute(null));

        string legacySettingsPath = Path.Combine(temp.Path, "legacy-settings.json");
        var legacySettings = new AppSettings(legacySettingsPath);
        legacySettings.SetPreference("manager.devices.profile.v1", System.Text.Json.JsonSerializer.Serialize(new
        {
            SourcePath = temp.Path,
            DestinationPath = "/sdcard/Music",
            DeviceSerial = "same",
            AdbPath = "",
            Exclusions = "",
            MtimeToleranceSeconds = 60,
            DeleteExtras = true,
            Direct = false,
            Adopt = false,
            RecoveryId = "legacy-run",
            RecoveryDestination = "/sdcard/Music",
            RecoveryDeviceSerial = "same",
        }));
        var legacy = new DevicesViewModel(reloadedSync, new AppSettings(legacySettingsPath),
            new StubFiles(), new RecordingDialogs(), new AppActivityService());
        await legacy.RefreshDevicesCommand.ExecuteAsync(null);
        Assert.True(legacy.RestoreCommand.CanExecute(null));
    }

    private static DeviceSyncDevice Device(string model, string serial) => new(
        $"{model}|{serial}", serial, model, "device", true, Model: model, Connection: "usb");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(condition());
    }

    private sealed class FailingDeviceSyncService : IDeviceSyncService
    {
        public Task<DeviceSyncInitializationResult> InitializeAsync(
            DeviceSyncInitializationRequest request,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => throw Failure();

        public Task<DeviceSyncPlan> PreviewAsync(
            DeviceSyncRequest request,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => throw Failure();

        public Task<DeviceSyncResult> ApplyAsync(
            DeviceSyncPlan plan,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => throw Failure();

        public Task<DeviceSyncRestoreResult> RestoreAsync(
            DeviceSyncRestoreRequest request,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => throw Failure();

        private static FileNotFoundException Failure() =>
            new("The packaged syncer executable was not found.");
    }

    private sealed class RestorableDeviceSyncService(string source) : IDeviceSyncService
    {
        public DeviceSyncRestoreRequest? RestoreRequest { get; private set; }

        public Task<DeviceSyncInitializationResult> InitializeAsync(
            DeviceSyncInitializationRequest request, IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => Task.FromResult(
                new DeviceSyncInitializationResult(request.Destination, "phone", request.Adopt, "Initialized"));

        public Task<DeviceSyncPlan> PreviewAsync(
            DeviceSyncRequest request, IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => Task.FromResult(new DeviceSyncPlan(request, "phone", "digest",
                Path.Combine(source, "plan.json"), [], 0, 1, 0, 10, [], DateTimeOffset.UtcNow));

        public Task<DeviceSyncResult> ApplyAsync(
            DeviceSyncPlan plan, IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => Task.FromResult(
                new DeviceSyncResult(0, 1, 0, 10, "run-1", "phone", []));

        public Task<DeviceSyncRestoreResult> RestoreAsync(
            DeviceSyncRestoreRequest request, IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            RestoreRequest = request;
            return Task.FromResult(new DeviceSyncRestoreResult(
                request.Destination, request.RecoveryId, request.DeviceSerial ?? "phone"));
        }
    }

    private sealed class ProgressDeviceSyncService(string source) : IDeviceSyncService
    {
        public TaskCompletionSource ApplyStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseApply { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailApply { get; init; }

        public Task<DeviceSyncInitializationResult> InitializeAsync(
            DeviceSyncInitializationRequest request, IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => Task.FromResult(
                new DeviceSyncInitializationResult(request.Destination, "phone", request.Adopt, "Initialized"));

        public Task<DeviceSyncPlan> PreviewAsync(
            DeviceSyncRequest request, IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => Task.FromResult(new DeviceSyncPlan(request, "phone", "digest",
                Path.Combine(source, "plan.json"),
                [
                    new(DeviceSyncMutationKind.AddFile, "one.flac", "missing", false, 10, 1),
                    new(DeviceSyncMutationKind.AddFile, "two.flac", "missing", false, 20, 2),
                ],
                0, 2, 0, 30, [], DateTimeOffset.UtcNow));

        public async Task<DeviceSyncResult> ApplyAsync(
            DeviceSyncPlan plan, IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            progress?.Report(new(OperationPhase.Applying, CurrentPath: "one.flac",
                ItemStatus: OperationItemStatus.InProgress));
            ApplyStarted.TrySetResult();
            await ReleaseApply.Task.WaitAsync(ct);
            if (FailApply)
            {
                progress?.Report(new(OperationPhase.Applying, CurrentPath: "one.flac",
                    ItemStatus: OperationItemStatus.Failed));
                throw new InvalidOperationException("transfer failed");
            }
            progress?.Report(new(OperationPhase.Applying, CurrentPath: "one.flac",
                ItemStatus: OperationItemStatus.Complete));
            progress?.Report(new(OperationPhase.Applying, CurrentPath: "two.flac",
                ItemStatus: OperationItemStatus.InProgress));
            progress?.Report(new(OperationPhase.Applying, CurrentPath: "two.flac",
                ItemStatus: OperationItemStatus.Complete));
            return new(0, 2, 0, 30, "run-1", "phone", []);
        }

        public Task<DeviceSyncRestoreResult> RestoreAsync(
            DeviceSyncRestoreRequest request, IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => Task.FromResult(
                new DeviceSyncRestoreResult(request.Destination, request.RecoveryId, "phone"));
    }

    private sealed class DirectDeviceSyncService(string source) : IDeviceSyncService
    {
        public Task<DeviceSyncInitializationResult> InitializeAsync(
            DeviceSyncInitializationRequest request, IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => Task.FromResult(
                new DeviceSyncInitializationResult(request.Destination, "phone", request.Adopt, "Initialized"));

        public Task<DeviceSyncPlan> PreviewAsync(
            DeviceSyncRequest request, IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => Task.FromResult(new DeviceSyncPlan(request, "phone", "digest",
                Path.Combine(source, "plan.json"), [], 0, 1, 0, 10, [], DateTimeOffset.UtcNow));

        public Task<DeviceSyncResult> ApplyAsync(
            DeviceSyncPlan plan, IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => Task.FromResult(
                new DeviceSyncResult(0, 1, plan.Request.Direct ? 0 : 1, 10,
                    plan.Request.Direct ? null : "run-before-direct", "phone", []));

        public Task<DeviceSyncRestoreResult> RestoreAsync(
            DeviceSyncRestoreRequest request, IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => throw new InvalidOperationException("No recovery is available.");
    }

    private sealed class EnumeratingDeviceSyncService(string source) : IDeviceSyncService
    {
        public IReadOnlyList<DeviceSyncDevice> Devices { get; set; } =
        [
            new("Pixel 9|phone", "phone", "Pixel 9", "device", true,
                Model: "Pixel 9", Connection: "usb"),
            new("Pixel Tablet|tablet", "tablet", "Pixel Tablet", "device", true,
                Model: "Pixel Tablet", Connection: "usb"),
        ];
        public Exception? EnumerationError { get; init; }
        public TaskCompletionSource<IReadOnlyList<DeviceSyncDevice>>? EnumerationGate { get; set; }
        public string? AppliedRecoveryId { get; init; }
        public DeviceSyncInitializationRequest? InitializationRequest { get; private set; }
        public DeviceSyncRequest? PreviewRequest { get; private set; }
        public DeviceSyncRestoreRequest? RestoreRequest { get; private set; }

        public Task<IReadOnlyList<DeviceSyncDevice>> EnumerateDevicesAsync(
            string? adbPath = null,
            CancellationToken ct = default) => EnumerationGate?.Task ??
                (EnumerationError is null
                    ? Task.FromResult(Devices)
                    : Task.FromException<IReadOnlyList<DeviceSyncDevice>>(EnumerationError));

        public Task<DeviceSyncInitializationResult> InitializeAsync(
            DeviceSyncInitializationRequest request,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            InitializationRequest = request;
            return Task.FromResult(new DeviceSyncInitializationResult(
                request.Destination, request.DeviceSerial, request.Adopt, "Initialized"));
        }

        public Task<DeviceSyncPlan> PreviewAsync(
            DeviceSyncRequest request,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            PreviewRequest = request;
            return Task.FromResult(new DeviceSyncPlan(request, request.DeviceSerial ?? "", "digest",
                Path.Combine(source, "plan.json"), [], 0, 0, 0, 0, [], DateTimeOffset.UtcNow));
        }

        public Task<DeviceSyncResult> ApplyAsync(
            DeviceSyncPlan plan,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default) => Task.FromResult(new DeviceSyncResult(
                0, 0, AppliedRecoveryId is null ? 0 : 1, 0, AppliedRecoveryId,
                plan.Request.DeviceSerial ?? "", []));

        public Task<DeviceSyncRestoreResult> RestoreAsync(
            DeviceSyncRestoreRequest request,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            RestoreRequest = request;
            return Task.FromResult(new DeviceSyncRestoreResult(
                request.Destination, request.RecoveryId, request.DeviceSerial ?? ""));
        }
    }

    private sealed class RecordingDialogs : IDialogCoordinator
    {
        public bool ConfirmResult { get; init; } = true;
        public string? Title { get; private set; }
        public string? Message { get; private set; }
        public string? ConfirmationMessage { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, string primaryText)
        {
            ConfirmationMessage = message;
            return Task.FromResult(ConfirmResult);
        }

        public Task ShowMessageAsync(string title, string message)
        {
            Title = title;
            Message = message;
            return Task.CompletedTask;
        }
    }

    private sealed class StubFiles : IFilePickerService
    {
        public Task<string?> PickFileAsync(string title, IReadOnlyList<FilePickerType>? types = null) =>
            Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string suggestedName, string extension) =>
            Task.FromResult<string?>(null);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "DevicesViewModelTests", Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
