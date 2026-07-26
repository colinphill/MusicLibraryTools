using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Views;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

public sealed class DevicesLifecycleUiTests
{
    [AvaloniaFact]
    public async Task
        Initialize_preview_and_apply_are_the_only_next_primary_actions_and_restore_stays_in_more()
    {
        var sync = new RecordingDeviceSyncService();
        var dialogs = new AcceptingDialogs();
        using ServiceProvider services =
            Composition.BuildServices(
                collection =>
                {
                    collection.AddSingleton<
                        IAppSettings>(
                        new TestSettings());
                    collection.AddSingleton<
                        IDeviceSyncService>(
                        sync);
                    collection.AddSingleton<
                        IDialogCoordinator>(
                        dialogs);
                });
        App.UseServicesForTests(services);
        var devices = new DevicesView();
        DevicesViewModel model =
            Assert.IsType<DevicesViewModel>(
                devices.DataContext);
        var window = new Window
        {
            Width = 900,
            Height = 600,
            Content = devices,
        };
        try
        {
            window.Show();
            Render();

            model.DestinationPath = "";
            Render();
            Assert.DoesNotContain(
                LifecycleButtons(),
                button =>
                    button
                        .IsEffectivelyVisible);
            model.DestinationPath =
                "music";
            Render();
            AssertPrimary(
                "InitializeButton");
            Assert.False(
                model.PreviewCommand
                    .CanExecute(null));
            Assert.False(
                model.ApplyCommand
                    .CanExecute(null));

            await model.InitializeCommand
                .ExecuteAsync(null);
            Render();
            Assert.Equal(
                1,
                sync.InitializeCalls);
            Assert.Equal(
                "music",
                sync.LastInitialization?
                    .Destination);

            model.SourcePath =
                Path.GetFullPath(
                    "device-source");
            Render();
            AssertPrimary(
                "PreviewButton");
            Assert.True(
                model.PreviewCommand
                    .CanExecute(null));

            await model.PreviewCommand
                .ExecuteAsync(null);
            Render();
            Assert.Equal(
                1,
                sync.PreviewCalls);
            Assert.True(
                model.HasApplicablePreview);
            Assert.Single(model.Actions);
            AssertPrimary(
                "ApplyButton");

            await model.ApplyCommand
                .ExecuteAsync(null);
            Render();
            Assert.Equal(
                1,
                sync.ApplyCalls);
            Assert.False(
                model.HasApplicablePreview);
            Assert.True(
                model.RestoreCommand
                    .CanExecute(null));
            AssertPrimary(
                "PreviewButton");

            Button directRestore =
                devices.FindControl<Button>(
                    "RestoreButton")!;
            Assert.False(
                directRestore.IsVisible);
            Assert.False(
                directRestore
                    .IsEffectivelyVisible);

            Button more =
                devices.FindControl<Button>(
                    "DeviceMoreButton")!;
            ISolidColorBrush quietBackground =
                Assert.IsAssignableFrom<
                    ISolidColorBrush>(
                    Assert.Single(
                            more
                                .GetVisualDescendants()
                                .OfType<
                                    ContentPresenter>())
                        .Background);
            Assert.Equal(
                0,
                quietBackground.Color.A);
            MenuFlyout flyout =
                Assert.IsType<MenuFlyout>(
                    more.Flyout);
            flyout.ShowAt(more);
            Render();
            MenuItem restore =
                Assert.Single(
                    flyout.Items
                        .OfType<MenuItem>(),
                    item =>
                        ReferenceEquals(
                            item.Command,
                            model
                                .RestoreCommand));
            Assert.True(
                restore.Command!
                    .CanExecute(null));

            Assert.True(
                restore.IsEffectivelyVisible);
            Assert.True(restore.IsEnabled);
            flyout.Hide();

            Assert.Equal(
                2,
                dialogs.Confirmations);
        }
        finally
        {
            window.Hide();
        }

        void AssertPrimary(
            string expectedName)
        {
            Button visible =
                Assert.Single(
                    LifecycleButtons(),
                    button =>
                        button
                            .IsEffectivelyVisible);
            Assert.Equal(
                expectedName,
                visible.Name);
            Assert.Contains(
                "primary",
                visible.Classes);
            Assert.True(visible.IsEnabled);
            Assert.False(
                visible.IsPointerOver,
                $"{visible.Name} unexpectedly retained pointer-over state.");
            Assert.True(
                Application.Current!
                    .TryGetResource(
                        "AppAccentBrush",
                        window.ActualThemeVariant,
                        out object? accent));
            ContentPresenter presenter =
                Assert.Single(
                    visible
                        .GetVisualDescendants()
                        .OfType<
                            ContentPresenter>());
            Assert.Equal(
                accent?.ToString(),
                presenter.Background?
                    .ToString());
        }

        Button[] LifecycleButtons() =>
        [
            devices.FindControl<Button>(
                "InitializeButton")!,
            devices.FindControl<Button>(
                "PreviewButton")!,
            devices.FindControl<Button>(
                "ApplyButton")!,
        ];
    }

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform
            .ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class RecordingDeviceSyncService :
        IDeviceSyncService
    {
        public int InitializeCalls {
            get;
            private set;
        }
        public int PreviewCalls {
            get;
            private set;
        }
        public int ApplyCalls {
            get;
            private set;
        }
        public DeviceSyncInitializationRequest?
            LastInitialization {
                get;
                private set;
            }

        public Task<
            IReadOnlyList<DeviceSyncDevice>>
            EnumerateDevicesAsync(
                string? adbPath = null,
                CancellationToken ct =
                    default) =>
            Task.FromResult<
                IReadOnlyList<
                    DeviceSyncDevice>>([]);

        public Task<
            DeviceSyncInitializationResult>
            InitializeAsync(
                DeviceSyncInitializationRequest
                    request,
                IProgress<OperationProgress>?
                    progress = null,
                CancellationToken ct =
                    default)
        {
            ct.ThrowIfCancellationRequested();
            InitializeCalls++;
            LastInitialization = request;
            return Task.FromResult(
                new DeviceSyncInitializationResult(
                    request.Destination,
                    request.DeviceSerial,
                    request.Adopt,
                    "Initialized fixture"));
        }

        public Task<DeviceSyncPlan>
            PreviewAsync(
                DeviceSyncRequest request,
                IProgress<OperationProgress>?
                    progress = null,
                CancellationToken ct =
                    default)
        {
            ct.ThrowIfCancellationRequested();
            PreviewCalls++;
            return Task.FromResult(
                new DeviceSyncPlan(
                    request,
                    "fixture-device",
                    "FIXTURE-DIGEST",
                    "fixture.plan",
                    [
                        new DeviceSyncAction(
                            DeviceSyncMutationKind
                                .AddFile,
                            "Artist/Track.flac",
                            "New fixture file",
                            false,
                            128,
                            1),
                    ],
                    0,
                    1,
                    0,
                    128,
                    [],
                    DateTimeOffset
                        .UtcNow));
        }

        public Task<DeviceSyncResult>
            ApplyAsync(
                DeviceSyncPlan plan,
                IProgress<OperationProgress>?
                    progress = null,
                CancellationToken ct =
                    default)
        {
            ct.ThrowIfCancellationRequested();
            ApplyCalls++;
            return Task.FromResult(
                new DeviceSyncResult(
                    0,
                    1,
                    0,
                    128,
                    "RECOVERY-1",
                    plan.DeviceSerial,
                    []));
        }

        public Task<
            DeviceSyncRestoreResult>
            RestoreAsync(
                DeviceSyncRestoreRequest request,
                IProgress<OperationProgress>?
                    progress = null,
                CancellationToken ct =
                    default) =>
            Task.FromResult(
                new DeviceSyncRestoreResult(
                    request.Destination,
                    request.RecoveryId,
                    request.DeviceSerial ??
                    ""));
    }

    private sealed class AcceptingDialogs :
        IDialogCoordinator
    {
        public int Confirmations {
            get;
            private set;
        }

        public Task<bool> ConfirmAsync(
            string title,
            string message,
            string primaryText)
        {
            Confirmations++;
            return Task.FromResult(true);
        }

        public Task ShowMessageAsync(
            string title,
            string message) =>
            Task.CompletedTask;
    }

    private sealed class TestSettings :
        IAppSettings
    {
        private readonly Dictionary<string, string>
            _preferences = [];

        public string? ConfigPath => null;
        public LibraryConfiguration?
            Configuration => null;
        public event EventHandler?
            ConfigurationChanged;

        public AppConfigurationSnapshot
            GetSnapshot() =>
            new(null, null, 0);

        public void LoadConfig(string path) =>
            ConfigurationChanged?.Invoke(
                this,
                EventArgs.Empty);

        public string?
            GetRememberedConfigPath() =>
            null;

        public IReadOnlyList<string>
            RecentConfigPaths => [];

        public void ClearRecentConfigs()
        {
        }

        public string? GetPreference(
            string key) =>
            _preferences.GetValueOrDefault(
                key);

        public void SetPreference(
            string key,
            string? value)
        {
            if (value is null)
                _preferences.Remove(key);
            else
                _preferences[key] = value;
        }
    }
}
