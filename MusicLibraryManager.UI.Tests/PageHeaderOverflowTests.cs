using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
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

public sealed class PageHeaderOverflowTests
{
    [AvaloniaFact]
    public void Secondary_command_overflow_preserves_primary_keyboard_access_and_focus()
    {
        var executeCount = 0;
        var command = new TestCommand(
            () => executeCount++);
        var primary = new Button
        {
            Width = 160,
            Content = "Primary action",
        };
        var secondary = new Button
        {
            Width = 260,
            Content = "A localized secondary action",
            Command = command,
        };
        PageHeader.SetOverflowHeader(
            secondary,
            "A localized secondary action");
        var header = new PageHeader
        {
            Title = "Measured command page",
            PrimaryAction = primary,
            SecondaryActions = secondary,
            SecondaryOverflowEnabled = true,
            SecondaryOverflowLabel = "More",
            SecondaryOverflowAutomationName =
                "More page actions",
        };
        var window = new Window
        {
            Width = 1000,
            Height = 160,
            Content = header,
        };
        try
        {
            window.Show();
            Render();
            Button overflow =
                header.FindControl<Button>(
                    "SecondaryOverflowButton")!;
            Assert.False(
                overflow.IsEffectivelyVisible);
            Assert.True(secondary.Focus());
            Assert.Same(
                secondary,
                window.FocusManager?
                    .GetFocusedElement());

            window.Width = 600;
            Render();

            Assert.True(
                primary.IsEffectivelyVisible);
            Assert.False(
                secondary.IsEffectivelyVisible);
            Assert.True(
                overflow.IsEffectivelyVisible);
            Assert.Equal(
                "More page actions",
                AutomationProperties.GetName(
                    overflow));
            Assert.Same(
                overflow,
                window.FocusManager?
                    .GetFocusedElement());

            MenuFlyout flyout =
                Assert.IsType<MenuFlyout>(
                    overflow.Flyout);
            MenuItem item =
                Assert.Single(
                    flyout.Items
                        .OfType<MenuItem>());
            Assert.Equal(
                "A localized secondary action",
                item.Header);
            Assert.Same(
                command,
                item.Command);

            window.KeyPress(
                Key.Enter,
                RawInputModifiers.None,
                PhysicalKey.Enter,
                null);
            window.KeyRelease(
                Key.Enter,
                RawInputModifiers.None,
                PhysicalKey.Enter,
                null);
            Render();
            Assert.True(flyout.IsOpen);

            item.RaiseEvent(
                new RoutedEventArgs(
                    MenuItem.ClickEvent));
            Render();
            Assert.Equal(1, executeCount);
            flyout.Hide();
            Render();

            overflow.Focus();
            window.Width = 1000;
            Render();
            Assert.True(
                secondary.IsEffectivelyVisible);
            Assert.False(
                overflow.IsEffectivelyVisible);
            Assert.Same(
                secondary,
                window.FocusManager?
                    .GetFocusedElement());
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Disappearing_inline_command_restores_focus_to_the_primary_action()
    {
        var primary = new Button
        {
            Width = 160,
            Content = "Primary action",
        };
        var secondary = new Button
        {
            Width = 260,
            Content = "Busy action",
        };
        PageHeader.SetOverflowHeader(
            secondary,
            "Busy action");
        var header = new PageHeader
        {
            Title = "Inline focus fallback",
            PrimaryAction = primary,
            SecondaryActions = secondary,
            SecondaryOverflowEnabled = true,
            SecondaryOverflowLabel = "More",
            SecondaryOverflowAutomationName =
                "More page actions",
        };
        var window = new Window
        {
            Width = 1000,
            Height = 160,
            Content = header,
        };
        try
        {
            window.Show();
            Render();
            Button overflow =
                header.FindControl<Button>(
                    "SecondaryOverflowButton")!;
            Assert.False(
                overflow.IsEffectivelyVisible);
            Assert.True(
                secondary.IsEffectivelyVisible);
            Assert.True(secondary.Focus());
            Assert.Same(
                secondary,
                window.FocusManager?
                    .GetFocusedElement());

            secondary.IsVisible = false;
            Render();

            Assert.False(
                overflow.IsEffectivelyVisible);
            Assert.Same(
                primary,
                window.FocusManager?
                    .GetFocusedElement());
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Disappearing_compact_command_restores_focus_to_the_primary_action()
    {
        var primary = new Button
        {
            Width = 160,
            Content = "Primary action",
        };
        var secondary = new Button
        {
            Width = 260,
            Content = "Busy action",
        };
        PageHeader.SetOverflowHeader(
            secondary,
            "Busy action");
        var header = new PageHeader
        {
            Title = "Focus fallback",
            PrimaryAction = primary,
            SecondaryActions = secondary,
            SecondaryOverflowEnabled = true,
            SecondaryOverflowLabel = "More",
            SecondaryOverflowAutomationName =
                "More page actions",
        };
        var window = new Window
        {
            Width = 600,
            Height = 160,
            Content = header,
        };
        try
        {
            window.Show();
            Render();
            Button overflow =
                header.FindControl<Button>(
                    "SecondaryOverflowButton")!;
            Assert.True(
                overflow.IsEffectivelyVisible);
            Assert.True(overflow.Focus());
            window.KeyPress(
                Key.Enter,
                RawInputModifiers.None,
                PhysicalKey.Enter,
                null);
            window.KeyRelease(
                Key.Enter,
                RawInputModifiers.None,
                PhysicalKey.Enter,
                null);
            Render();
            MenuFlyout flyout =
                Assert.IsType<MenuFlyout>(
                    overflow.Flyout);
            Assert.True(flyout.IsOpen);
            MenuItem item =
                Assert.Single(
                    flyout.Items
                        .OfType<MenuItem>());
            Assert.True(item.Focus());
            Assert.Same(
                item,
                window.FocusManager?
                    .GetFocusedElement());

            secondary.IsVisible = false;
            Render();

            Assert.False(flyout.IsOpen);
            Assert.False(
                overflow.IsEffectivelyVisible);
            Assert.Same(
                primary,
                window.FocusManager?
                    .GetFocusedElement());
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Compact_busy_consumers_expose_localized_cancel_through_the_shared_overflow()
    {
        var settings = new TestSettings();
        var neutral =
            new ResourceLocalizationService(
                settings);
        var localization =
            new TestPseudoLocalizationService(
                neutral,
                expanded: true);
        using ServiceProvider services =
            Composition.BuildServices(
                collection =>
                {
                    collection.AddSingleton<
                        IAppSettings>(settings);
                    collection.AddSingleton<
                        ILocalizationService>(
                            localization);
                });
        App.UseServicesForTests(services);
        CultureInfo previousCulture =
            CultureInfo.CurrentUICulture;
        try
        {
            localization.SetCulture("en-US");
            var cases = new[]
            {
                new ConsumerCase(
                    new OrganizeView(),
                    "OrganizeHeader",
                    "OrganizeCancelButton",
                    "Workbench.Action.MoreAutomation",
                    HasPrimary: true),
                new ConsumerCase(
                    new IngestView(),
                    "IngestHeader",
                    "IngestCancelButton",
                    "Ingest.Action.MoreAutomation",
                    HasPrimary: true),
                new ConsumerCase(
                    new OperationsView(),
                    "OperationsHeader",
                    "OperationsCancelButton",
                    "Workbench.Action.MoreAutomation",
                    HasPrimary: false),
            };

            foreach (ConsumerCase consumer in cases)
            {
                var cancel = new TestCommand();
                consumer.View.DataContext =
                    new BusyConsumerContext(
                        cancel);
                var window = new Window
                {
                    Width = 700,
                    Height = 600,
                    Content = consumer.View,
                    FontSize = 18,
                };
                try
                {
                    window.Show();
                    Render();
                    PageHeader header =
                        consumer.View
                            .FindControl<PageHeader>(
                                consumer.HeaderName)!;
                    Button cancelButton =
                        consumer.View
                            .FindControl<Button>(
                                consumer.CancelName)!;
                    Button overflow =
                        header.FindControl<Button>(
                            "SecondaryOverflowButton")!;

                    for (double width = 700;
                         width >= 160 &&
                         !overflow
                             .IsEffectivelyVisible;
                         width -= 20)
                    {
                        window.Width = width;
                        Render();
                    }

                    Assert.True(
                        overflow
                            .IsEffectivelyVisible,
                        $"{consumer.View.GetType().Name} never handed its busy Cancel command to More.");
                    Assert.False(
                        cancelButton
                            .IsEffectivelyVisible);
                    Assert.Equal(
                        localization.Get(
                            consumer
                                .MoreAutomationKey),
                        AutomationProperties
                            .GetName(overflow));
                    Assert.Equal(
                        localization.Get(
                            "Common.Cancel"),
                        cancelButton.Content);
                    if (consumer.HasPrimary)
                    {
                        Assert.True(
                            header
                                .FindControl<
                                    ContentPresenter>(
                                    "PrimaryActionPresenter")!
                                .IsEffectivelyVisible,
                            $"{consumer.View.GetType().Name} hid its primary action while compact.");
                    }

                    MenuItem item =
                        Assert.Single(
                            Assert.IsType<
                                    MenuFlyout>(
                                    overflow.Flyout)
                                .Items
                                .OfType<MenuItem>());
                    Assert.Equal(
                        localization.Get(
                            "Common.Cancel"),
                        item.Header);
                    Assert.Same(
                        cancel,
                        item.Command);
                    Assert.True(item.IsEnabled);
                    item.Command!.Execute(
                        item.CommandParameter);
                    Assert.Equal(
                        1,
                        cancel.ExecutionCount);

                    localization.SetCulture(
                        "de-DE");
                    Render();
                    Assert.Equal(
                        localization.Get(
                            "Common.Cancel"),
                        item.Header);
                    localization.SetCulture(
                        "en-US");
                    Render();
                }
                finally
                {
                    window.Hide();
                }
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture =
                previousCulture;
        }
    }

    private static void Render()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform
            .ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class TestCommand :
        ICommand
    {
        private readonly Action? _execute;

        public TestCommand(
            Action? execute = null)
        {
            _execute = execute;
        }

        public int ExecutionCount
        {
            get;
            private set;
        }

        public event EventHandler?
            CanExecuteChanged
        {
            add
            {
            }
            remove
            {
            }
        }

        public bool CanExecute(
            object? parameter) =>
            true;

        public void Execute(
            object? parameter)
        {
            ExecutionCount++;
            _execute?.Invoke();
        }
    }

    private sealed class BusyConsumerContext
    {
        public BusyConsumerContext(
            ICommand cancelCommand)
        {
            CancelCommand =
                cancelCommand;
        }

        public bool IsBusy => true;
        public bool HasPreview => false;
        public bool HasApplicablePreview =>
            false;
        public bool IsPreviewPrimary => true;
        public bool IsConfigurationReady =>
            true;
        public double PreviewActionOpacity =>
            1;
        public ICommand CancelCommand
        {
            get;
        }
        public ICommand PreviewCommand
        {
            get;
        } = new TestCommand();
        public ICommand ApplyCommand
        {
            get;
        } = new TestCommand();
    }

    private sealed record ConsumerCase(
        UserControl View,
        string HeaderName,
        string CancelName,
        string MoreAutomationKey,
        bool HasPrimary);

    private sealed class TestSettings :
        IAppSettings
    {
        private readonly Dictionary<
            string,
            string> _preferences = [];

        public string? ConfigPath => null;
        public LibraryConfiguration?
            Configuration => null;
        public event EventHandler?
            ConfigurationChanged;

        public AppConfigurationSnapshot
            GetSnapshot() =>
            new(null, null, 0);

        public void LoadConfig(
            string path) =>
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
            _preferences
                .GetValueOrDefault(key);

        public void SetPreference(
            string key,
            string? value)
        {
            if (value is null)
                _preferences.Remove(key);
            else
                _preferences[key] =
                    value;
        }
    }
}
