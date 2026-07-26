using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MetadataCaching;
using Microsoft.Extensions.DependencyInjection;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Views;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

public sealed class UnconfiguredDestinationContractTests
{
    [AvaloniaFact]
    public void
        Every_destination_remains_navigable_and_configuration_dependencies_have_one_quiet_explanation()
    {
        var settings = new TestSettings();
        var libraryService =
            new RecordingLibraryService();
        using ServiceProvider services =
            Composition.BuildServices(
                collection =>
                {
                    collection.AddSingleton<
                        IAppSettings>(settings);
                    collection.AddSingleton<
                        ILibraryService>(
                        libraryService);
                    collection.AddSingleton<
                        IDeviceSyncService>(
                        new DeviceSyncStub());
                });
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<
                MainWindow>();
        INavigationService navigation =
            services.GetRequiredService<
                INavigationService>();
        try
        {
            window.Width = 900;
            window.Height = 600;
            window.Show();
            Render();

            foreach (ShellDestination destination in
                     Enum.GetValues<
                         ShellDestination>())
            {
                Button navigationButton =
                    window.FindControl<Button>(
                        $"{destination}Nav")!;
                Assert.True(
                    navigationButton.IsEnabled,
                    $"{destination} navigation was disabled before configuration.");

                navigation.Navigate(destination);
                Render();
                Assert.Equal(
                    destination,
                    navigation.Current);
                Control page =
                    Assert.IsAssignableFrom<
                        Control>(
                        window.FindControl<
                                ContentControl>(
                                "ContentHost")!
                            .Content);
                Assert.True(
                    page.IsEffectivelyVisible);
                Assert.True(
                    page.IsEnabled);

                switch (page)
                {
                    case HomeView home:
                        AssertHomeSetup(home);
                        break;
                    case LibraryView library:
                        AssertLibrarySetup(
                            library);
                        break;
                    case HealthView health:
                        AssertHealthSetup(
                            health);
                        break;
                    case IngestView ingest:
                        AssertIngestSetup(
                            ingest);
                        break;
                    case OrganizeView organize:
                        AssertOrganizeSetup(
                            organize);
                        break;
                    case DevicesView devices:
                        Assert.True(
                            devices.FindControl<
                                    Button>(
                                    "InitializeButton")!
                                .IsEffectivelyVisible,
                            "Device initialization should remain usable without a library configuration.");
                        break;
                    case WorkbenchView workbench:
                        Assert.True(
                            workbench.FindControl<
                                    SplitButton>(
                                    "AddWorkbenchSourceButton")!
                                .IsEnabled,
                            "Session sources should remain addable without a library configuration.");
                        break;
                    case SettingsView:
                    case OperationsView:
                    case AboutView:
                        break;
                    default:
                        throw new Xunit.Sdk
                            .XunitException(
                                $"No unconfigured-destination contract is registered for {page.GetType().Name}.");
                }
            }

            Assert.Equal(
                0,
                libraryService.IndexCalls);
        }
        finally
        {
            window.Hide();
        }

        static void AssertHomeSetup(
            HomeView home)
        {
            Grid setup =
                home.FindControl<Grid>(
                    "SetupLayout")!;
            Assert.True(
                setup.IsEffectivelyVisible);
            Button action =
                Assert.Single(
                    setup.GetVisualDescendants()
                        .OfType<Button>(),
                    button =>
                        button
                            .IsEffectivelyVisible);
            Assert.True(action.IsEnabled);
            Assert.False(
                string.IsNullOrWhiteSpace(
                    ActionName(action)));
        }

        static void AssertLibrarySetup(
            LibraryView library)
        {
            LibraryViewModel model =
                Assert.IsType<
                    LibraryViewModel>(
                    library.DataContext);
            Assert.Equal(
                LibraryPageState
                    .NoConfiguration,
                model.PageState);
            Border empty =
                library.FindControl<Border>(
                    "LibraryEmptyState")!;
            Assert.True(
                empty.IsEffectivelyVisible);
            Assert.Single(
                empty.GetVisualDescendants()
                    .OfType<Button>(),
                button =>
                    button
                        .IsEffectivelyVisible);
            Assert.False(
                library.FindControl<
                        StackPanel>(
                        "LibraryFooterGuidance")!
                    .IsEffectivelyVisible);
            Assert.False(
                library.FindControl<
                        TextBlock>(
                        "LibraryIndexingFooterGuidance")!
                    .IsEffectivelyVisible);
            Button index =
                Assert.Single(
                    library.GetVisualDescendants()
                        .OfType<Button>(),
                    button =>
                        ReferenceEquals(
                            button.Command,
                            model.Indexing
                                .IndexCommand));
            AssertQuietDisabled(
                index,
                "Library index");
        }

        static void AssertHealthSetup(
            HealthView health)
        {
            Border setup =
                health.FindControl<Border>(
                    "HealthSetupCard")!;
            Assert.True(
                setup.IsEffectivelyVisible);
            Assert.False(
                health.FindControl<Border>(
                        "HealthActionCard")!
                    .IsEffectivelyVisible);
            Assert.False(
                health.FindControl<
                        PersistedSplitView>(
                        "HealthResultsHost")!
                    .IsEffectivelyVisible);
            Button action =
                Assert.Single(
                    setup.GetVisualDescendants()
                        .OfType<Button>(),
                    button =>
                        button
                            .IsEffectivelyVisible);
            Assert.True(action.IsEnabled);
        }

        static void AssertIngestSetup(
            IngestView ingest)
        {
            IngestViewModel model =
                Assert.IsType<
                    IngestViewModel>(
                    ingest.DataContext);
            model.SourceDirectory =
                Path.GetFullPath(
                    "unconfigured-ingest");
            Render();

            Assert.False(
                model.IsConfigurationReady);
            Assert.False(
                string.IsNullOrWhiteSpace(
                    model
                        .ConfigurationReadinessText));
            Assert.False(
                string.IsNullOrWhiteSpace(
                    model
                        .ConfigurationDiagnosticDetail));
            Border setup =
                ingest.FindControl<Border>(
                    "SetupCard")!;
            Assert.Equal(
                1,
                setup.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Count(text =>
                        text.IsEffectivelyVisible &&
                        string.Equals(
                            text.Text,
                            model
                                .ConfigurationReadinessText,
                            StringComparison
                                .Ordinal)));
            Assert.Equal(
                1,
                setup.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Count(text =>
                        text.IsEffectivelyVisible &&
                        string.Equals(
                            text.Text,
                            model
                                .ConfigurationDiagnosticDetail,
                            StringComparison
                                .Ordinal)));
            AssertQuietDisabled(
                Assert.Single(
                    ingest.GetVisualDescendants()
                        .OfType<Button>(),
                    button =>
                        ReferenceEquals(
                            button.Command,
                            model.PreviewCommand)),
                "Ingest preview");
            foreach (Button preflight in
                     ingest.GetVisualDescendants()
                         .OfType<Button>()
                         .Where(button =>
                             ReferenceEquals(
                                 button.Command,
                                 model
                                     .PreflightCommand)))
            {
                AssertQuietDisabled(
                    preflight,
                    "Ingest preflight");
            }
        }

        static void AssertOrganizeSetup(
            OrganizeView organize)
        {
            Border setup =
                organize.FindControl<Border>(
                    "OrganizeSetupCard")!;
            Assert.True(
                setup.IsEffectivelyVisible);
            Assert.False(
                organize.FindControl<Border>(
                        "OrganizeSummaryCard")!
                    .IsEffectivelyVisible);
            Assert.False(
                organize.FindControl<Border>(
                        "OrganizeResultsCard")!
                    .IsEffectivelyVisible);
            OrganizeViewModel model =
                Assert.IsType<
                    OrganizeViewModel>(
                    organize.DataContext);
            Button preview =
                Assert.Single(
                    organize.GetVisualDescendants()
                        .OfType<Button>(),
                    button =>
                        ReferenceEquals(
                            button.Command,
                            model.PreviewCommand));
            AssertQuietDisabled(
                preview,
                "Organize preview");
        }

        static void AssertQuietDisabled(
            Button action,
            string context)
        {
            Assert.False(
                action
                    .IsEffectivelyEnabled,
                $"{context} remained enabled.");
            Assert.True(
                action.Opacity <= 0.52,
                $"{context} retained full emphasis while unavailable " +
                $"(opacity {action.Opacity:0.##}; local enabled " +
                $"{action.IsEnabled}; classes " +
                $"[{string.Join(", ", action.Classes)}]).");
        }

        static string? ActionName(
            Button action) =>
            AutomationProperties
                .GetName(action) ??
            action.Content as string;
    }

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform
            .ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
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

    private sealed class RecordingLibraryService :
        ILibraryService
    {
        public int IndexCalls { get; private set; }
        public bool IsReady => false;

        public Task<(
            int Added,
            int Modified,
            int Removed,
            int Unchanged)> IndexAsync(
            IProgress<IndexProgress>?
                progress = null,
            CancellationToken ct = default)
        {
            IndexCalls++;
            return Task.FromResult(
                (0, 0, 0, 0));
        }

        public Task<LibrarySnapshot>
            BuildSnapshotAsync(
                LibraryGrouping grouping =
                    LibraryGrouping.AlbumArtist,
                CancellationToken ct =
                    default) =>
            Task.FromResult(
                new LibrarySnapshot());

        public Task<IReadOnlyList<
            TrackRecord>>
            GetAllRecordsAsync(
                CancellationToken ct =
                    default) =>
            Task.FromResult<
                IReadOnlyList<
                    TrackRecord>>([]);

        public Task<AnalysisReport>
            CheckSetsAsync(
                CancellationToken ct =
                    default) =>
            Task.FromResult(
                new AnalysisReport(
                    "Fixture",
                    []));

        public Task<FileDetails?>
            GetFileDetailsAsync(
                string path,
                bool includeArtwork,
                CancellationToken ct =
                    default) =>
            Task.FromResult<
                FileDetails?>(null);

        public Task<byte[]?>
            GetFirstImageAsync(
                string path,
                CancellationToken ct =
                    default) =>
            Task.FromResult<byte[]?>(null);

        public Task<IReadOnlyList<byte[]?>>
            GetFirstImagesAsync(
                IReadOnlyList<string> paths,
                CancellationToken ct =
                    default) =>
            Task.FromResult<
                IReadOnlyList<byte[]?>>(
                paths.Select(
                        _ => (byte[]?)null)
                    .ToArray());

        public Task<IReadOnlyList<string>>
            GetImageSignaturesAsync(
                IReadOnlyList<string> paths,
                CancellationToken ct =
                    default) =>
            Task.FromResult<
                IReadOnlyList<string>>(
                paths.Select(_ => "")
                    .ToArray());
    }

    private sealed class DeviceSyncStub :
        IDeviceSyncService
    {
        public Task<
            DeviceSyncInitializationResult>
            InitializeAsync(
                DeviceSyncInitializationRequest
                    request,
                IProgress<OperationProgress>?
                    progress = null,
                CancellationToken ct =
                    default) =>
            throw new
                NotSupportedException();

        public Task<DeviceSyncPlan>
            PreviewAsync(
                DeviceSyncRequest request,
                IProgress<OperationProgress>?
                    progress = null,
                CancellationToken ct =
                    default) =>
            throw new
                NotSupportedException();

        public Task<DeviceSyncResult>
            ApplyAsync(
                DeviceSyncPlan plan,
                IProgress<OperationProgress>?
                    progress = null,
                CancellationToken ct =
                    default) =>
            throw new
                NotSupportedException();

        public Task<
            DeviceSyncRestoreResult>
            RestoreAsync(
                DeviceSyncRestoreRequest request,
                IProgress<OperationProgress>?
                    progress = null,
                CancellationToken ct =
                    default) =>
            throw new
                NotSupportedException();
    }
}
