using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Views;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

public sealed class WorkbenchInteractionTests
{
    [AvaloniaFact]
    public void Session_context_menu_opens_for_shift_f10_and_apps_key()
    {
        using ServiceProvider services = BuildServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            WorkbenchView view =
                ShowWorkbench(window, services, 1200, 700);
            ContextMenu menu = Assert.IsType<ContextMenu>(
                view.FindControl<AppDataGrid>(
                    "WorkbenchGrid")!.ContextMenu);

            view.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.F10,
                KeyModifiers = KeyModifiers.Shift,
            });
            Render();
            Assert.True(menu.IsOpen);
            menu.Close();

            view.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Apps,
            });
            Render();
            Assert.True(menu.IsOpen);
            menu.Close();
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Pointer_press_on_scrim_closes_transient_drawer_resumes_inspector_and_restores_focus()
    {
        using ServiceProvider services = BuildServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            WorkbenchView view =
                ShowWorkbench(window, services, 900, 640);
            Button inspectorButton =
                view.FindControl<Button>(
                    "WorkbenchInspectorToggle")!;
            Button pendingButton =
                view.FindControl<Button>(
                    "WorkbenchPendingChangesButton")!;
            Control inspectorDrawer =
                view.FindControl<Control>(
                    "WorkbenchInspectorDrawer")!;
            Control pendingDrawer =
                view.FindControl<Control>(
                    "WorkbenchPendingChangesDrawer")!;
            Border scrim =
                view.FindControl<Border>(
                    "WorkbenchHeaderScrim")!;

            inspectorButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));
            Render();
            pendingButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));
            Render();
            Assert.True(pendingDrawer.IsVisible);
            Assert.False(inspectorDrawer.IsVisible);
            Assert.True(scrim.IsVisible);

            Point clickPoint =
                scrim.TranslatePoint(
                    new Point(
                        Math.Max(2, scrim.Bounds.Width / 2),
                        Math.Max(2, scrim.Bounds.Height / 2)),
                    window) ??
                throw new InvalidOperationException(
                    "The Workbench scrim was not attached.");
            window.MouseDown(
                clickPoint,
                MouseButton.Left,
                RawInputModifiers.None);
            window.MouseUp(
                clickPoint,
                MouseButton.Left,
                RawInputModifiers.None);
            Render();

            Assert.False(pendingDrawer.IsVisible);
            Assert.True(inspectorDrawer.IsVisible);
            Assert.True(scrim.IsVisible);
            Assert.Same(
                pendingButton,
                window.FocusManager!
                    .GetFocusedElement());
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Every_bulk_operation_descriptor_exposes_exactly_its_contextual_panels()
    {
        using ServiceProvider services = BuildServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            WorkbenchView view =
                ShowWorkbench(window, services, 1440, 900);
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            model.SelectedSection =
                WorkbenchSection.BulkOperation;
            Render();

            var panels = new Dictionary<string, Control>
            {
                ["Destination"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationDestinationPanel")!,
                ["Secondary"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationSecondaryPanel")!,
                ["Value"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationValuePanel")!,
                ["Find"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationFindPanel")!,
                ["Replacement"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationReplacementPanel")!,
                ["Case"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationCasePanel")!,
                ["Separator"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationSeparatorPanel")!,
                ["ValueOrder"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationValueOrderPanel")!,
                ["Sequence"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationSequencePanel")!,
                ["Path"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationPathPanel")!,
                ["ParentLevel"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationParentLevelPanel")!,
                ["ExtractionPattern"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationExtractionPatternPanel")!,
                ["CaptureGroup"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationCaptureGroupPanel")!,
                ["RegularExpression"] =
                    view.FindControl<Control>(
                        "WorkbenchOperationRegularExpressionOption")!,
            };

            Assert.NotEmpty(
                model.OperationEditor.OperationDescriptors);
            foreach (MetadataOperationDescriptor descriptor in
                     model.OperationEditor.OperationDescriptors)
            {
                model.OperationEditor.SelectedOperation =
                    descriptor;
                Render();
                HashSet<string> expected =
                    ExpectedPanels(descriptor.Kind);
                foreach ((string name, Control panel) in panels)
                    Assert.True(
                        panel.IsVisible ==
                        expected.Contains(name),
                        $"{descriptor.Kind}: panel {name} was {(panel.IsVisible ? "visible" : "hidden")}.");
            }
        }
        finally
        {
            window.Hide();
        }
    }

    private static HashSet<string> ExpectedPanels(
        MetadataOperationKind kind) => kind switch
        {
            MetadataOperationKind.Assign =>
                ["Value"],
            MetadataOperationKind.Copy =>
                ["Destination"],
            MetadataOperationKind.ReplaceText =>
                ["Find", "Replacement", "RegularExpression"],
            MetadataOperationKind.ChangeCase =>
                ["Case"],
            MetadataOperationKind.Sequence =>
                ["Sequence"],
            MetadataOperationKind.Combine =>
                ["Destination", "Secondary", "Separator"],
            MetadataOperationKind.Split =>
                ["Separator", "RegularExpression"],
            MetadataOperationKind.Join =>
                ["Separator"],
            MetadataOperationKind.Reorder =>
                ["ValueOrder"],
            MetadataOperationKind.ExtractPathComponent =>
            [
                "Path",
                "ParentLevel",
                "ExtractionPattern",
                "CaptureGroup",
            ],
            _ => [],
        };

    private static WorkbenchView ShowWorkbench(
        MainWindow window,
        IServiceProvider services,
        double width,
        double height)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Width = width;
        window.Height = height;
        services.GetRequiredService<INavigationService>()
            .Navigate(ShellDestination.Workbench);
        Render();
        return Assert.IsType<WorkbenchView>(
            window.FindControl<ContentControl>(
                "ContentHost")!.Content);
    }

    private static ServiceProvider BuildServices()
    {
        var settings = new TestSettings();
        return Composition.BuildServices(collection =>
        {
            collection.AddSingleton<IAppSettings>(
                settings);
            collection.AddSingleton<ILocalizationService>(
                new ResourceLocalizationService(
                    settings));
        });
    }

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform
            .ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class TestSettings : IAppSettings
    {
        private readonly Dictionary<string, string>
            _preferences = [];

        public string? ConfigPath => null;
        public LibraryConfiguration? Configuration => null;
        public event EventHandler? ConfigurationChanged;

        public AppConfigurationSnapshot GetSnapshot() =>
            new(null, null, 0);

        public void LoadConfig(string path) =>
            ConfigurationChanged?.Invoke(
                this,
                EventArgs.Empty);

        public string? GetRememberedConfigPath() =>
            null;

        public IReadOnlyList<string>
            RecentConfigPaths => [];

        public void ClearRecentConfigs()
        {
        }

        public string? GetPreference(string key) =>
            _preferences.GetValueOrDefault(key);

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
