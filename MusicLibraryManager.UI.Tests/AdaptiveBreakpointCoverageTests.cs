using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Views;
using MusicLibraryManager.Views.WorkbenchSections;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

public sealed class AdaptiveBreakpointCoverageTests
{
    [AvaloniaFact]
    public void Shell_toolbar_gutters_and_activity_switch_at_exact_content_boundaries()
    {
        var settings = new TestSettings();
        settings.SetPreference(
            AppearancePreferences
                .ShellRailExpandedPreference,
            bool.FalseString);
        using ServiceProvider services =
            BuildServices(settings);
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<
                MainWindow>();
        try
        {
            window.WindowState =
                WindowState.Normal;
            window.Height = 701;
            window.Show();
            Render();

            AssertToolbar(
                899,
                compact: true);
            AssertToolbar(
                900,
                compact: true);
            AssertToolbar(
                901,
                compact: false);

            AssertGutter(
                999,
                16);
            AssertGutter(
                1000,
                24);
            AssertGutter(
                1001,
                24);

            AssertActivityHeight(
                699,
                compact: true);
            AssertActivityHeight(
                700,
                compact: true);
            AssertActivityHeight(
                701,
                compact: false);
        }
        finally
        {
            window.Hide();
        }

        void ResizeToContent(
            double contentWidth,
            double height)
        {
            window.Width = contentWidth + 64;
            window.Height = height;
            Render();
            Grid body =
                window.FindControl<Grid>(
                    "BodyGrid")!;
            Assert.Equal(
                64,
                body.ColumnDefinitions[0]
                    .ActualWidth,
                precision: 2);
            Assert.Equal(
                contentWidth,
                body.Bounds.Width -
                body.ColumnDefinitions[0]
                    .ActualWidth,
                precision: 2);
        }

        void AssertToolbar(
            double contentWidth,
            bool compact)
        {
            ResizeToContent(
                contentWidth,
                701);
            Assert.Equal(
                !compact,
                window.FindControl<TextBlock>(
                    "ConfigurationChipText")!
                    .IsVisible);
            Assert.Equal(
                !compact,
                window.FindControl<Border>(
                    "SearchShortcut")!
                    .IsVisible);
        }

        void AssertGutter(
            double contentWidth,
            double expected)
        {
            ResizeToContent(
                contentWidth,
                701);
            Assert.Equal(
                new Thickness(
                    expected,
                    8),
                window.FindControl<Grid>(
                    "TopBarContent")!
                    .Margin);
        }

        void AssertActivityHeight(
            double height,
            bool compact)
        {
            ResizeToContent(
                1001,
                height);
            Border activity =
                window.FindControl<Border>(
                    "ActivityBanner")!;
            Assert.Equal(
                !compact,
                window.FindControl<TextBlock>(
                    "ActivityMessageText")!
                    .IsVisible);
            Assert.Equal(
                !compact,
                window.FindControl<TextBlock>(
                    "ActivityStateLabel")!
                    .IsVisible);
            Assert.Equal(
                new Thickness(
                    12,
                    compact ? 8 : 12),
                activity.Padding);
            Assert.Equal(
                new Thickness(
                    compact ? 12 : 24,
                    compact ? 4 : 8,
                    compact ? 12 : 24,
                    0),
                activity.Margin);
        }
    }

    [AvaloniaFact]
    public void About_fields_and_reviewed_file_operations_switch_at_exact_boundaries()
    {
        using ServiceProvider services =
            BuildServices();
        App.UseServicesForTests(services);

        var about = new AboutView();
        (Window aboutWindow,
            Border aboutHost) =
            ShowInFixedHost(
                about,
                1000,
                800);
        try
        {
            AssertPackageLayout(
                959,
                stacked: true);
            AssertPackageLayout(
                960,
                stacked: false);
            AssertPackageLayout(
                961,
                stacked: false);

            AssertHeroWidth(
                719,
                compact: true);
            AssertHeroWidth(
                720,
                compact: false);
            AssertHeroWidth(
                721,
                compact: false);

            AssertHeroHeight(
                699,
                compact: true);
            AssertHeroHeight(
                700,
                compact: true);
            AssertHeroHeight(
                701,
                compact: false);
        }
        finally
        {
            aboutWindow.Hide();
        }

        var fields = new FieldsEditorView();
        (Window fieldsWindow,
            Border fieldsHost) =
            ShowInFixedHost(
                fields,
                800,
                700);
        try
        {
            AssertFieldsLayout(
                759,
                stacked: true);
            AssertFieldsLayout(
                760,
                stacked: false);
            AssertFieldsLayout(
                761,
                stacked: false);
        }
        finally
        {
            fieldsWindow.Hide();
        }

        var reviewed =
            new ReviewedFileOperationEditorView();
        (Window reviewedWindow,
            Border reviewedHost) =
            ShowInFixedHost(
                reviewed,
                900,
                701);
        try
        {
            AssertReviewedWidth(
                879,
                narrow: true);
            AssertReviewedWidth(
                880,
                narrow: false);
            AssertReviewedWidth(
                881,
                narrow: false);

            AssertReviewedHeight(
                699,
                compact: true);
            AssertReviewedHeight(
                700,
                compact: true);
            AssertReviewedHeight(
                701,
                compact: false);
        }
        finally
        {
            reviewedWindow.Hide();
        }

        void AssertPackageLayout(
            double width,
            bool stacked)
        {
            ResizeHost(
                aboutHost,
                about,
                width,
                800);
            Grid packages =
                about.FindControl<Grid>(
                    "PackageGrid")!;
            Border avalonia =
                about.FindControl<Border>(
                    "AvaloniaPackageCard")!;
            Border skia =
                about.FindControl<Border>(
                    "SkiaSharpPackageCard")!;
            Assert.Equal(
                stacked ? 1 : 3,
                packages.ColumnDefinitions
                    .Count);
            Assert.Equal(
                stacked ? 3 : 1,
                packages.RowDefinitions
                    .Count);
            Assert.Equal(
                stacked ? 0 : 2,
                Grid.GetColumn(skia));
            Assert.Equal(
                stacked ? 2 : 0,
                Grid.GetRow(skia));
            Assert.Equal(
                0,
                Grid.GetColumn(avalonia));
            Assert.Equal(
                0,
                Grid.GetRow(avalonia));
        }

        void AssertHeroWidth(
            double width,
            bool compact)
        {
            ResizeHost(
                aboutHost,
                about,
                width,
                800);
            AssertHero(compact);
        }

        void AssertHeroHeight(
            double height,
            bool compact)
        {
            ResizeHost(
                aboutHost,
                about,
                960,
                height);
            AssertHero(compact);
        }

        void AssertHero(bool compact)
        {
            Border hero =
                about.FindControl<Border>(
                    "AboutHero")!;
            Control mark =
                about.FindControl<Control>(
                    "AboutBrandMark")!;
            TextBlock product =
                about.FindControl<TextBlock>(
                    "AboutProductName")!;
            Grid layout =
                about.FindControl<Grid>(
                    "HeroLayout")!;
            double markSize =
                compact ? 80 : 116;
            Assert.Equal(
                new Thickness(
                    compact ? 16 : 24),
                hero.Padding);
            Assert.Equal(
                markSize,
                mark.Width);
            Assert.Equal(
                markSize,
                mark.Height);
            Assert.Equal(
                compact ? 28 : 34,
                product.FontSize);
            Assert.Equal(
                markSize,
                layout.ColumnDefinitions[0]
                    .Width.Value);
            Assert.Equal(
                compact ? 16 : 24,
                layout.ColumnDefinitions[1]
                    .Width.Value);
        }

        void AssertFieldsLayout(
            double width,
            bool stacked)
        {
            ResizeHost(
                fieldsHost,
                fields,
                width,
                700);
            UniformGrid choices =
                fields.FindControl<UniformGrid>(
                    "FieldAdditionChoices")!;
            Assert.Equal(
                stacked ? 1 : 2,
                choices.Columns);
            Assert.Equal(
                stacked
                    ? new Thickness(0, 0, 0, 4)
                    : new Thickness(0, 0, 4, 0),
                Assert.IsAssignableFrom<Control>(
                    choices.Children[0])
                    .Margin);
            Assert.Equal(
                stacked
                    ? new Thickness(0, 4, 0, 0)
                    : new Thickness(4, 0, 0, 0),
                Assert.IsAssignableFrom<Control>(
                    choices.Children[1])
                    .Margin);
        }

        void AssertReviewedWidth(
            double width,
            bool narrow)
        {
            ResizeHost(
                reviewedHost,
                reviewed,
                width,
                701);
            Grid source =
                reviewed.FindControl<Grid>(
                    "SourceOptionsLayout")!;
            Grid template =
                reviewed.FindControl<Grid>(
                    "TemplateOptionsLayout")!;
            Control kind =
                reviewed.FindControl<Control>(
                    "ReviewedFileOperationKindField")!;
            Control destination =
                reviewed.FindControl<Control>(
                    "DestinationLayout")!;
            Control summary =
                reviewed.FindControl<Control>(
                    "TargetSummaryText")!;
            Assert.Equal(
                narrow ? 1 : 2,
                source.ColumnDefinitions.Count);
            Assert.Equal(
                narrow ? 2 : 1,
                source.RowDefinitions.Count);
            Assert.Equal(
                narrow ? 3 : 1,
                template.RowDefinitions.Count);
            Assert.Equal(
                0,
                Grid.GetRow(kind));
            Assert.Equal(
                narrow ? 1 : 0,
                Grid.GetRow(destination));
            Assert.Equal(
                0,
                Grid.GetRow(summary));
            Assert.Same(
                reviewed.FindControl<Grid>(
                    "EditorLayout"),
                summary.Parent);
            Assert.DoesNotContain(
                reviewed.FindControl<ScrollViewer>(
                        "ReviewedFileOperationFormScroll")!
                    .GetVisualDescendants(),
                descendant =>
                    ReferenceEquals(
                        descendant,
                        summary));
            Assert.Equal(
                narrow ? 8 : 0,
                source.RowSpacing);
            Assert.Equal(
                narrow ? 8 : 0,
                template.RowSpacing);
        }

        void AssertReviewedHeight(
            double height,
            bool compact)
        {
            ResizeHost(
                reviewedHost,
                reviewed,
                880,
                height);
            Assert.Equal(
                compact ? 7 : 10,
                reviewed.FindControl<Grid>(
                    "EditorLayout")!
                    .RowSpacing);
        }
    }

    [AvaloniaFact]
    public void Home_content_breakpoints_switch_at_minus_one_exact_and_plus_one()
    {
        using ServiceProvider services = BuildServices();
        App.UseServicesForTests(services);
        var view = new HomeView();
        (Window window, Border host) =
            ShowInFixedHost(view, 1200, 800);
        try
        {
            AssertMetricColumns(619, 1);
            AssertMetricColumns(620, 2);
            AssertMetricColumns(621, 2);
            AssertMetricColumns(1119, 2);
            AssertMetricColumns(1120, 4);
            AssertMetricColumns(1121, 4);

            AssertTwoPaneBreakpoint(
                view.FindControl<Grid>("SetupLayout")!,
                760);
            AssertTwoPaneBreakpoint(
                view.FindControl<Grid>("HealthMetricLayout")!,
                700);
            AssertTwoPaneBreakpoint(
                view.FindControl<Grid>("LibraryActionLayout")!,
                820);
        }
        finally
        {
            window.Hide();
        }

        void AssertMetricColumns(
            double width,
            int expectedColumns)
        {
            ResizeHost(host, view, width, 800);
            Assert.Equal(
                width,
                view.FindControl<AdaptivePage>(
                    "PageScaffold")!.ContentWidth);
            Assert.Equal(
                expectedColumns,
                view.FindControl<UniformGrid>(
                    "MetricGrid")!.Columns);
        }

        void AssertTwoPaneBreakpoint(
            Grid layout,
            double threshold)
        {
            AssertLayout(
                threshold - 1,
                columns: 1,
                rows: 3);
            AssertLayout(
                threshold,
                columns: 3,
                rows: 1);
            AssertLayout(
                threshold + 1,
                columns: 3,
                rows: 1);

            void AssertLayout(
                double width,
                int columns,
                int rows)
            {
                ResizeHost(host, view, width, 800);
                Assert.Equal(
                    columns,
                    layout.ColumnDefinitions.Count);
                Assert.Equal(
                    rows,
                    layout.RowDefinitions.Count);
                if (columns == 3)
                {
                    Assert.Equal(
                        new[]
                        {
                            0,
                            2,
                        },
                        layout.Children
                            .OfType<Control>()
                            .Select(
                                Grid.GetColumn)
                            .Distinct()
                            .Order()
                            .ToArray());
                }
            }
        }
    }

    [AvaloniaFact]
    public void Health_breakpoints_use_the_action_and_result_content_hosts()
    {
        using ServiceProvider services = BuildServices();
        App.UseServicesForTests(services);
        var view = new HealthView();
        AnalyzerViewModel model =
            Assert.IsType<AnalyzerViewModel>(
                view.DataContext);
        DuplicateGroup[] groups =
        [
            new(
                "Fixture duplicates",
                [
                    new TrackRecord
                    {
                        Path =
                            @"X:\Fixture\duplicate.flac",
                        Title = "Fixture",
                    },
                ]),
        ];
        AnalysisRunViewModel run =
            AnalysisRunViewModel.ForDuplicates(
                "Duplicates",
                groups,
                "One duplicate group");
        model.Runs.Add(run);
        model.SelectedRun = run;

        (Window window, Border host) =
            ShowInFixedHost(view, 1100, 760);
        Grid actions =
            view.FindControl<Grid>(
                "HealthActionLayout")!;
        Grid navigation =
            view.FindControl<Grid>(
                "HealthResultNavigationLayout")!;
        Grid masterDetail =
            view.FindControl<Grid>(
                "DuplicateMasterDetailLayout")!;
        try
        {
            AssertActionLayout(719, 1, 3);
            AssertActionLayout(720, 2, 1);
            AssertActionLayout(721, 2, 1);

            navigation.HorizontalAlignment =
                HorizontalAlignment.Left;
            AssertNavigationLayout(
                759,
                picker: true);
            AssertNavigationLayout(
                760,
                picker: false);
            AssertNavigationLayout(
                761,
                picker: false);

            masterDetail.HorizontalAlignment =
                HorizontalAlignment.Left;
            AssertMasterDetailLayout(
                679,
                columns: 1,
                rows: 2);
            AssertMasterDetailLayout(
                680,
                columns: 2,
                rows: 1);
            AssertMasterDetailLayout(
                681,
                columns: 2,
                rows: 1);

            AssertCompactHeight(
                699,
                compact: true);
            AssertCompactHeight(
                700,
                compact: true);
            AssertCompactHeight(
                701,
                compact: false);
        }
        finally
        {
            window.Hide();
        }

        void AssertActionLayout(
            double width,
            int columns,
            int rows)
        {
            ResizeHost(host, view, width, 760);
            Assert.Equal(
                columns,
                actions.ColumnDefinitions.Count);
            Assert.Equal(
                rows,
                actions.RowDefinitions.Count);
        }

        void AssertNavigationLayout(
            double width,
            bool picker)
        {
            navigation.Width = width;
            Render();
            Assert.Equal(
                width,
                navigation.Bounds.Width);
            Assert.Equal(
                picker,
                view.FindControl<Border>(
                    "HealthResultPickerHost")!
                    .IsVisible);
            Assert.Equal(
                !picker,
                view.FindControl<Border>(
                    "HealthResultNavigationRail")!
                    .IsVisible);
        }

        void AssertMasterDetailLayout(
            double width,
            int columns,
            int rows)
        {
            masterDetail.Width = width;
            Render();
            Assert.Equal(
                width,
                masterDetail.Bounds.Width);
            Assert.Equal(
                columns,
                masterDetail
                    .ColumnDefinitions.Count);
            Assert.Equal(
                rows,
                masterDetail.RowDefinitions.Count);
        }

        void AssertCompactHeight(
            double height,
            bool compact)
        {
            ResizeHost(
                host,
                view,
                1100,
                height);
            Assert.Equal(
                !compact,
                view.FindControl<TextBlock>(
                        "HealthAuditTitle")!
                    .IsVisible);
            Assert.Equal(
                !compact,
                view.FindControl<TextBlock>(
                        "HealthAuditDescription")!
                    .IsVisible);
            Assert.Equal(
                !compact,
                view.FindControl<Border>(
                        "HealthStatusBanner")!
                    .IsVisible);
            Assert.Equal(
                compact ? 8 : 16,
                view.FindControl<Border>(
                        "HealthActionCard")!
                    .Padding.Left);
        }
    }

    [AvaloniaFact]
    public void Ingest_breakpoints_switch_source_summary_and_compact_height_at_the_boundary()
    {
        using ServiceProvider services = BuildServices();
        App.UseServicesForTests(services);
        var view = new IngestView();
        (Window window, Border host) =
            ShowInFixedHost(view, 900, 700);
        Grid source =
            view.FindControl<Grid>(
                "SourcePickerLayout")!;
        Grid summary =
            view.FindControl<Grid>(
                "PreviewSummaryLayout")!;
        try
        {
            AssertSourceLayout(759, 2, 2);
            AssertSourceLayout(760, 3, 1);
            AssertSourceLayout(761, 3, 1);

            AssertSummaryLayout(699, 2, 3);
            AssertSummaryLayout(700, 6, 1);
            AssertSummaryLayout(701, 6, 1);

            AssertCompactHeight(
                559,
                compact: true);
            AssertCompactHeight(
                560,
                compact: true);
            AssertCompactHeight(
                561,
                compact: false);
        }
        finally
        {
            window.Hide();
        }

        void AssertSourceLayout(
            double width,
            int columns,
            int rows)
        {
            ResizeHost(host, view, width, 700);
            Assert.Equal(
                columns,
                source.ColumnDefinitions.Count);
            Assert.Equal(
                rows,
                source.RowDefinitions.Count);
        }

        void AssertSummaryLayout(
            double width,
            int columns,
            int rows)
        {
            ResizeHost(host, view, width, 700);
            Assert.Equal(
                columns,
                summary.ColumnDefinitions.Count);
            Assert.Equal(
                rows,
                summary.RowDefinitions.Count);
        }

        void AssertCompactHeight(
            double height,
            bool compact)
        {
            ResizeHost(host, view, 900, height);
            Assert.Equal(
                new Thickness(
                    compact ? 12 : 16),
                view.FindControl<Border>(
                    "SetupCard")!.Padding);
            Assert.Equal(
                !compact,
                view.FindControl<TextBlock>(
                    "PreviewEmptyDescription")!
                    .IsVisible);
            Assert.Equal(
                !compact,
                view.FindControl<TextBlock>(
                    "HistoryEmptyDescription")!
                    .IsVisible);
        }
    }

    [AvaloniaFact]
    public void Devices_and_operations_breakpoints_switch_at_minus_one_exact_and_plus_one()
    {
        using ServiceProvider services = BuildServices();
        App.UseServicesForTests(services);

        var devices = new DevicesView();
        (Window devicesWindow, Border devicesHost) =
            ShowInFixedHost(devices, 1000, 700);
        try
        {
            Grid layout =
                devices.FindControl<Grid>(
                    "DevicesContentLayout")!;
            AssertDevicesLayout(
                919,
                columns: 1,
                rows: 3);
            AssertDevicesLayout(
                920,
                columns: 3,
                rows: 1);
            AssertDevicesLayout(
                921,
                columns: 3,
                rows: 1);

            void AssertDevicesLayout(
                double width,
                int columns,
                int rows)
            {
                ResizeHost(
                    devicesHost,
                    devices,
                    width,
                    700);
                Assert.Equal(
                    columns,
                    layout.ColumnDefinitions.Count);
                Assert.Equal(
                    rows,
                    layout.RowDefinitions.Count);
            }
        }
        finally
        {
            devicesWindow.Hide();
        }

        var operations = new OperationsView();
        (Window operationsWindow,
            Border operationsHost) =
            ShowInFixedHost(
                operations,
                1000,
                700);
        try
        {
            Grid jobs =
                operations.FindControl<Grid>(
                    "JobsLayout")!;
            AssertOperationsMode(
                779,
                jobs,
                pickerName: "JobPicker",
                paneName: "JobListPane",
                compact: true);
            AssertOperationsMode(
                780,
                jobs,
                pickerName: "JobPicker",
                paneName: "JobListPane",
                compact: false);
            AssertOperationsMode(
                781,
                jobs,
                pickerName: "JobPicker",
                paneName: "JobListPane",
                compact: false);

            Grid recovery =
                operations.FindControl<Grid>(
                    "RecoveryLayout")!;
            AssertOperationsMode(
                859,
                recovery,
                pickerName: "RecoveryRunPicker",
                paneName: "RecoveryRunPane",
                compact: true);
            AssertOperationsMode(
                860,
                recovery,
                pickerName: "RecoveryRunPicker",
                paneName: "RecoveryRunPane",
                compact: false);
            AssertOperationsMode(
                861,
                recovery,
                pickerName: "RecoveryRunPicker",
                paneName: "RecoveryRunPane",
                compact: false);

            void AssertOperationsMode(
                double width,
                Grid target,
                string pickerName,
                string paneName,
                bool compact)
            {
                ResizeHost(
                    operationsHost,
                    operations,
                    width,
                    700);
                Assert.Equal(
                    compact ? 1 : 3,
                    target.ColumnDefinitions
                        .Count(definition =>
                            definition.Width.Value >
                            0));
                Assert.Equal(
                    compact,
                    operations.FindControl<Control>(
                        pickerName)!.IsVisible);
                Assert.Equal(
                    !compact,
                    operations.FindControl<Control>(
                        paneName)!.IsVisible);
            }
        }
        finally
        {
            operationsWindow.Hide();
        }
    }

    [AvaloniaFact]
    public void Library_docking_and_settings_navigation_and_form_modes_use_their_actual_content_hosts()
    {
        using ServiceProvider services = BuildServices();
        App.UseServicesForTests(services);

        var library = new LibraryView();
        LibraryViewModel libraryModel =
            Assert.IsType<LibraryViewModel>(
                library.DataContext);
        libraryModel.SetInspectorPreference(
            LibraryInspectorPreference.Pinned);
        (Window libraryWindow,
            Border libraryHost) =
            ShowInFixedHost(
                library,
                1300,
                760);
        try
        {
            PersistedSplitView split =
                library.FindControl<
                    PersistedSplitView>(
                    "WorkspaceSplit")!;
            GridSplitter splitter =
                split.FindControl<GridSplitter>(
                    "Splitter")!;
            ContentPresenter right =
                split.FindControl<
                    ContentPresenter>(
                    "RightPresenter")!;

            AssertLibraryDocking(
                1137,
                docked: false);
            AssertLibraryDocking(
                1138,
                docked: true);
            AssertLibraryDocking(
                1139,
                docked: true);

            void AssertLibraryDocking(
                double viewWidth,
                bool docked)
            {
                ResizeHost(
                    libraryHost,
                    library,
                    viewWidth,
                    760);
                Assert.Equal(
                    viewWidth - 48,
                    split.Bounds.Width,
                    precision: 2);
                Assert.Equal(
                    docked,
                    splitter.IsVisible);
                Assert.Equal(
                    docked,
                    right.IsVisible);
                if (docked)
                {
                    Assert.True(
                        split.FindControl<
                                ContentPresenter>(
                                "LeftPresenter")!
                            .Bounds.Width >= 760);
                    Assert.True(
                        right.Bounds.Width >= 320);
                }
            }
        }
        finally
        {
            libraryWindow.Hide();
        }

        var settings = new SettingsView();
        (Window settingsWindow,
            Border settingsHost) =
            ShowInFixedHost(
                settings,
                1000,
                760);
        try
        {
            Border categoryRail =
                settings.FindControl<Border>(
                    "SettingsCategoryRail")!;
            ComboBox categoryPicker =
                settings.FindControl<ComboBox>(
                    "SettingsCategoryPicker")!;
            Grid navigationLayout =
                settings.FindControl<Grid>(
                    "SettingsNavigationLayout")!;
            AssertSettingsNavigation(
                SettingsView
                    .CategoryRailActivationWidth -
                1,
                rail: false);
            AssertSettingsNavigation(
                SettingsView
                    .CategoryRailActivationWidth,
                rail: true);
            AssertSettingsNavigation(
                SettingsView
                    .CategoryRailActivationWidth +
                1,
                rail: true);
            navigationLayout.Width =
                double.NaN;
            navigationLayout.HorizontalAlignment =
                HorizontalAlignment.Stretch;
            ResizeHost(
                settingsHost,
                settings,
                1000,
                760);

            TabControl tabs =
                settings.FindControl<TabControl>(
                    "SettingsTabs")!;
            tabs.SelectedIndex = 9;
            Render();
            UniformGrid themeGrid =
                settings.GetVisualDescendants()
                    .OfType<UniformGrid>()
                    .Single(grid =>
                        grid.Classes.Contains(
                            "responsive-theme-grid"));
            AssertSettingsColumns(599, 1);
            AssertSettingsColumns(600, 2);
            AssertSettingsColumns(601, 2);
            AssertSettingsColumns(919, 2);
            AssertSettingsColumns(920, 4);
            AssertSettingsColumns(921, 4);

            SettingsViewModel settingsModel =
                Assert.IsType<SettingsViewModel>(
                    settings.DataContext);
            settingsModel.AddFieldMappingCommand
                .Execute(null);
            tabs.SelectedIndex = 8;
            Render();
            AssertFieldMappingColumns(
                759,
                1);
            AssertFieldMappingColumns(
                760,
                2);
            AssertFieldMappingColumns(
                761,
                2);

            void AssertSettingsNavigation(
                double width,
                bool rail)
            {
                settingsHost.Width = 1200;
                navigationLayout.Width = width;
                navigationLayout
                    .HorizontalAlignment =
                    HorizontalAlignment.Left;
                settingsHost.Height =
                    settingsHost.Height == 760
                        ? 759
                        : 760;
                Render();
                Assert.Equal(
                    width,
                    navigationLayout.Bounds.Width);
                Assert.Equal(
                    rail,
                    categoryRail.IsVisible);
                Assert.Equal(
                    !rail,
                    categoryPicker.IsVisible);
            }

            void AssertSettingsColumns(
                double pageWidth,
                int expected)
            {
                ResizeSettingsPageToWidth(
                    settingsHost,
                    settings,
                    pageWidth);
                Assert.Equal(
                    expected,
                    themeGrid.Columns);
            }

            void AssertFieldMappingColumns(
                double pageWidth,
                int expected)
            {
                ResizeSettingsPageToWidth(
                    settingsHost,
                    settings,
                    pageWidth);
                Grid fields =
                    Assert.Single(
                        settings
                            .GetVisualDescendants()
                            .OfType<Grid>(),
                        grid =>
                            grid.IsEffectivelyVisible &&
                            grid.Classes.Contains(
                                "field-mapping-fields"));
                Assert.Equal(
                    expected,
                    fields.ColumnDefinitions.Count);
            }
        }
        finally
        {
            settingsWindow.Hide();
        }
    }

    [AvaloniaFact]
    public void
        Workbench_central_task_never_shrinks_across_the_shared_gutter_threshold()
    {
        using ServiceProvider services =
            BuildServices();
        App.UseServicesForTests(services);
        var workbench =
            new WorkbenchView();
        (Window window, Border host) =
            ShowInFixedHost(
                workbench,
                999,
                760);
        try
        {
            double previousWidth = 0;
            foreach (double width in
                     new[]
                     {
                         999d,
                         1000d,
                         1001d,
                     })
            {
                ResizeHost(
                    host,
                    workbench,
                    width,
                    760);
                Assert.Equal(
                    new Thickness(
                        AdaptivePage
                            .NarrowGutter),
                    workbench.FindControl<Grid>(
                        "WorkbenchRoot")!
                        .Margin);
                Assert.False(
                    workbench.FindControl<
                            Border>(
                            "WorkbenchSectionRail")!
                        .IsVisible);
                double currentWidth =
                    workbench.FindControl<
                            Carousel>(
                            "WorkbenchTabs")!
                        .Bounds.Width;
                Assert.True(
                    currentWidth >=
                    previousWidth,
                    $"Growing the Workbench to {width:0}px reduced its central task from {previousWidth:0}px to {currentWidth:0}px.");
                previousWidth =
                    currentWidth;
            }

            ResizeHost(
                host,
                workbench,
                WorkbenchView
                    .SectionRailActivationWidth(
                        compactHeight: false),
                760);
            Assert.Equal(
                new Thickness(
                    AdaptivePage.WideGutter),
                workbench.FindControl<Grid>(
                    "WorkbenchRoot")!
                    .Margin);
            Assert.True(
                workbench.FindControl<Carousel>(
                        "WorkbenchTabs")!
                    .Bounds.Width >=
                WorkbenchView
                    .MinimumSectionTaskWidth);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Workbench_shell_and_section_breakpoints_switch_at_minus_one_exact_and_plus_one()
    {
        using ServiceProvider services = BuildServices();
        App.UseServicesForTests(services);

        var workbench = new WorkbenchView();
        (Window window, Border host) =
            ShowInFixedHost(
                workbench,
                1200,
                760);
        try
        {
            double standardRailBreakpoint =
                WorkbenchView
                    .SectionRailActivationWidth(
                        compactHeight: false);
            double compactRailBreakpoint =
                WorkbenchView
                    .SectionRailActivationWidth(
                        compactHeight: true);
            double standardDockingBreakpoint =
                WorkbenchView
                    .DockedDrawerActivationWidth(
                        compactHeight: false);
            double compactDockingBreakpoint =
                WorkbenchView
                    .DockedDrawerActivationWidth(
                        compactHeight: true);

            AssertWorkbenchRail(
                standardRailBreakpoint - 1,
                visible: false);
            AssertWorkbenchRail(
                standardRailBreakpoint,
                visible: true);
            AssertWorkbenchRail(
                standardRailBreakpoint + 1,
                visible: true);
            AssertWorkbenchRail(
                compactRailBreakpoint - 1,
                visible: false,
                height: 700);
            AssertWorkbenchRail(
                compactRailBreakpoint,
                visible: true,
                height: 700);
            AssertWorkbenchRail(
                compactRailBreakpoint + 1,
                visible: true,
                height: 700);

            ResizeHost(
                host,
                workbench,
                standardDockingBreakpoint -
                1,
                760);
            Button inspector =
                workbench.FindControl<Button>(
                    "WorkbenchInspectorToggle")!;
            if (!workbench.FindControl<Control>(
                    "WorkbenchInspectorDrawer")!
                .IsVisible)
            {
                inspector.RaiseEvent(
                    new Avalonia.Interactivity
                        .RoutedEventArgs(
                        Button.ClickEvent));
                Render();
            }
            AssertWorkbenchDocking(
                standardDockingBreakpoint -
                1,
                docked: false);
            AssertWorkbenchDocking(
                standardDockingBreakpoint,
                docked: true);
            AssertWorkbenchDocking(
                standardDockingBreakpoint +
                1,
                docked: true);
            ResizeHost(
                host,
                workbench,
                compactDockingBreakpoint -
                1,
                700);
            if (!workbench.FindControl<Control>(
                    "WorkbenchInspectorDrawer")!
                .IsVisible)
            {
                inspector.RaiseEvent(
                    new Avalonia.Interactivity
                        .RoutedEventArgs(
                            Button.ClickEvent));
                Render();
            }
            AssertWorkbenchDocking(
                compactDockingBreakpoint -
                1,
                docked: false,
                height: 700);
            AssertWorkbenchDocking(
                compactDockingBreakpoint,
                docked: true,
                height: 700);
            AssertWorkbenchDocking(
                compactDockingBreakpoint +
                1,
                docked: true,
                height: 700);

            AssertWorkbenchHeight(
                699,
                compact: true);
            AssertWorkbenchHeight(
                700,
                compact: true);
            AssertWorkbenchHeight(
                701,
                compact: false);

            AssertWorkbenchPickerWidth(
                699,
                190);
            AssertWorkbenchPickerWidth(
                700,
                240);
            AssertWorkbenchPickerWidth(
                701,
                240);
        }
        finally
        {
            window.Hide();
        }

        AssertSectionBreakpoint(
            new WorkbenchAllFieldsSectionView(),
            "SectionLayout",
            "SupportingText",
            430);
        AssertSectionBreakpoint(
            new WorkbenchBulkOperationSectionView(),
            "RecipeLayout",
            "RepresentativeSupportingText",
            430);
        AssertSectionBreakpoint(
            new WorkbenchOnlineMetadataSectionView(),
            "ArtworkEditorLayout",
            "DiscoverySupportingText",
            620);
        AssertSectionBreakpoint(
            new WorkbenchPlaylistsSectionView(),
            "SectionLayout",
            "SupportingText",
            430);
        AssertSectionBreakpoint(
            new WorkbenchToolsSectionView(),
            "SectionLayout",
            "PlaceholderHelp",
            430);
        AssertSectionBreakpoint(
            new WorkbenchShortcutsSectionView(),
            "SectionLayout",
            "GestureHelp",
            430);
        AssertReportBreakpoint();
        AssertCompactTextBreakpoint(
            new WorkbenchSessionSectionView(),
            "EmptyStateSupportingText",
            360);
        AssertCompactTextBreakpoint(
            new WorkbenchPendingChangesDrawerView(),
            "SupportingText",
            430);

        void AssertWorkbenchRail(
            double width,
            bool visible,
            double height = 760)
        {
            ResizeHost(
                host,
                workbench,
                width,
                height);
            Assert.Equal(
                visible,
                workbench.FindControl<Border>(
                    "WorkbenchSectionRail")!
                    .IsVisible);
            Assert.Equal(
                !visible,
                workbench.FindControl<ComboBox>(
                    "WorkbenchSectionPicker")!
                    .IsVisible);
            if (visible)
            {
                Assert.True(
                    workbench.FindControl<
                            Carousel>(
                            "WorkbenchTabs")!
                        .Bounds.Width >=
                    WorkbenchView
                        .MinimumSectionTaskWidth);
            }
        }

        void AssertWorkbenchDocking(
            double width,
            bool docked,
            double height = 760)
        {
            ResizeHost(
                host,
                workbench,
                width,
                height);
            Assert.Equal(
                docked,
                workbench.FindControl<
                    GridSplitter>(
                    "Splitter")!.IsVisible);
            Assert.Equal(
                !docked,
                workbench.FindControl<Border>(
                    "WorkbenchHeaderScrim")!
                    .IsVisible);
            if (docked)
            {
                Assert.True(
                    workbench.FindControl<
                            Carousel>(
                            "WorkbenchTabs")!
                        .Bounds.Width >=
                    WorkbenchView
                        .MinimumDockedTaskWidth);
            }
        }

        void AssertWorkbenchHeight(
            double height,
            bool compact)
        {
            ResizeHost(
                host,
                workbench,
                1200,
                height);
            PageHeader header =
                workbench.FindControl<PageHeader>(
                    "WorkbenchHeader")!;
            Assert.Equal(
                compact,
                string.IsNullOrEmpty(
                    header.Subtitle));
            Assert.Equal(
                new Thickness(
                    compact ? 12 : 24),
                workbench.FindControl<Grid>(
                    "WorkbenchRoot")!.Margin);
        }

        void AssertWorkbenchPickerWidth(
            double contentWidth,
            double expectedPickerWidth)
        {
            double viewWidth =
                contentWidth +
                AdaptivePage.NarrowGutter * 2;
            ResizeHost(
                host,
                workbench,
                viewWidth,
                760);
            Grid root =
                workbench.FindControl<Grid>(
                    "WorkbenchRoot")!;
            Assert.Equal(
                contentWidth,
                workbench.Bounds.Width -
                root.Margin.Left -
                root.Margin.Right,
                precision: 2);
            ComboBox picker =
                workbench.FindControl<ComboBox>(
                    "WorkbenchSectionPicker")!;
            Assert.True(
                picker.IsVisible);
            Assert.Equal(
                expectedPickerWidth,
                picker.Width);
        }

        void AssertSectionBreakpoint(
            Control section,
            string layoutName,
            string supportingName,
            double heightThreshold)
        {
            (Window sectionWindow,
                Border sectionHost) =
                ShowInFixedHost(
                    section,
                    900,
                    650);
            try
            {
                Grid layout =
                    section.FindControl<Grid>(
                        layoutName)!;
                AssertSectionWidth(
                    879,
                    1);
                AssertSectionWidth(
                    880,
                    3);
                AssertSectionWidth(
                    881,
                    3);

                TextBlock supporting =
                    section.FindControl<TextBlock>(
                        supportingName)!;
                AssertSectionHeight(
                    heightThreshold - 1,
                    visible: false);
                AssertSectionHeight(
                    heightThreshold,
                    visible: true);
                AssertSectionHeight(
                    heightThreshold + 1,
                    visible: true);

                void AssertSectionWidth(
                    double width,
                    int columns)
                {
                    ResizeHost(
                        sectionHost,
                        section,
                        width,
                        650);
                    Assert.Equal(
                        columns,
                        layout.ColumnDefinitions
                            .Count);
                }

                void AssertSectionHeight(
                    double height,
                    bool visible)
                {
                    ResizeHost(
                        sectionHost,
                        section,
                        900,
                        height);
                    Assert.Equal(
                        visible,
                        supporting.IsVisible);
                }
            }
            finally
            {
                sectionWindow.Hide();
            }
        }

        void AssertReportBreakpoint()
        {
            var reports =
                new WorkbenchReportsSectionView();
            (Window reportsWindow,
                Border reportsHost) =
                ShowInFixedHost(
                    reports,
                    900,
                    650);
            try
            {
                Grid layout =
                    reports.FindControl<Grid>(
                        "SectionLayout")!;
                ScrollViewer editor =
                    reports.FindControl<
                        ScrollViewer>(
                        "EditorScroll")!;
                AssertReportWidth(879, 1);
                AssertReportWidth(880, 3);
                AssertReportWidth(881, 3);
                AssertReportHeight(429, 180);
                AssertReportHeight(430, 280);
                AssertReportHeight(431, 280);

                void AssertReportWidth(
                    double width,
                    int columns)
                {
                    ResizeHost(
                        reportsHost,
                        reports,
                        width,
                        650);
                    Assert.Equal(
                        columns,
                        layout.ColumnDefinitions.Count);
                }

                void AssertReportHeight(
                    double height,
                    double maxHeight)
                {
                    ResizeHost(
                        reportsHost,
                        reports,
                        879,
                        height);
                    Assert.Equal(
                        maxHeight,
                        editor.MaxHeight,
                        precision: 1);
                }
            }
            finally
            {
                reportsWindow.Hide();
            }
        }

        void AssertCompactTextBreakpoint(
            Control section,
            string textName,
            double threshold)
        {
            (Window sectionWindow,
                Border sectionHost) =
                ShowInFixedHost(
                    section,
                    900,
                    threshold + 20);
            try
            {
                TextBlock text =
                    section.FindControl<TextBlock>(
                        textName)!;
                AssertHeight(
                    threshold - 1,
                    visible: false);
                AssertHeight(
                    threshold,
                    visible: true);
                AssertHeight(
                    threshold + 1,
                    visible: true);

                void AssertHeight(
                    double height,
                    bool visible)
                {
                    ResizeHost(
                        sectionHost,
                        section,
                        900,
                        height);
                    Assert.Equal(
                        visible,
                        text.IsVisible);
                }
            }
            finally
            {
                sectionWindow.Hide();
            }
        }
    }

    [AvaloniaFact]
    public void Narrow_workbench_uses_shortcut_drill_in_step_summaries_and_contextual_report_grouping()
    {
        using ServiceProvider services =
            BuildServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<
                MainWindow>();
        try
        {
            window.Width = 900;
            window.Height = 600;
            window.WindowState =
                WindowState.Normal;
            window.Show();
            services.GetRequiredService<
                    INavigationService>()
                .Navigate(
                    ShellDestination.Workbench);
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            WorkbenchView workbench =
                Assert.IsType<WorkbenchView>(
                    window.FindControl<
                        ContentControl>(
                        "ContentHost")!.Content);

            model.SelectedSection =
                WorkbenchSection.Shortcuts;
            Render();
            WorkbenchShortcutsSectionView
                shortcuts =
                    workbench.FindControl<
                        WorkbenchShortcutsSectionView>(
                        "WorkbenchShortcutsSection")!;
            Grid bindingList =
                shortcuts.FindControl<Grid>(
                    "BindingsPanel")!;
            ScrollViewer editor =
                shortcuts.FindControl<
                    ScrollViewer>(
                    "EditorScroll")!;
            Assert.True(bindingList.IsVisible);
            Assert.False(editor.IsVisible);

            shortcuts.FindControl<Button>(
                    "NewShortcutEmptyButton")!
                .RaiseEvent(
                    new Avalonia.Interactivity
                        .RoutedEventArgs(
                            Button.ClickEvent));
            Render();
            Assert.False(bindingList.IsVisible);
            Assert.True(editor.IsVisible);
            Button back =
                shortcuts.FindControl<Button>(
                    "ShortcutBackButton")!;
            Assert.True(back.IsVisible);
            back.RaiseEvent(
                new Avalonia.Interactivity
                    .RoutedEventArgs(
                        Button.ClickEvent));
            Render();
            Assert.True(bindingList.IsVisible);
            Assert.False(editor.IsVisible);

            model.SelectedSection =
                WorkbenchSection.OnlineMetadata;
            Render();
            WorkbenchOnlineMetadataSectionView
                online =
                    workbench.FindControl<
                        WorkbenchOnlineMetadataSectionView>(
                        "WorkbenchOnlineMetadataSection")!;
            TextBlock discoverySummary =
                online.FindControl<TextBlock>(
                    "DiscoveryStepSummary")!;
            StackPanel searchSummary =
                online.FindControl<StackPanel>(
                    "SearchStepSummary")!;
            Assert.False(discoverySummary.IsVisible);
            Assert.False(searchSummary.IsVisible);
            online.FindControl<Expander>(
                    "DiscoveryStep")!
                .IsExpanded = false;
            Render();
            Assert.False(discoverySummary.IsVisible);

            model.HasCompletedOnlineDiscovery =
                true;
            Render();
            Assert.True(discoverySummary.IsVisible);
            Assert.Equal(
                model.SelectedOnlineMetadataScope
                    .Label,
                discoverySummary.Text);
            online.FindControl<Expander>(
                    "SearchStep")!
                .IsExpanded = true;
            Render();
            Assert.False(searchSummary.IsVisible);
            online.FindControl<Expander>(
                    "SearchStep")!
                .IsExpanded = false;
            Render();
            Assert.False(searchSummary.IsVisible);
            model.HasCompletedOnlineSearch = true;
            Render();
            Assert.True(searchSummary.IsVisible);
            Assert.Contains(
                searchSummary
                    .GetVisualDescendants()
                    .OfType<TextBlock>(),
                text =>
                    text.Text ==
                    model
                        .SelectedOnlineMetadataProvider
                        .Label);

            model.SelectedSection =
                WorkbenchSection.Reports;
            model.ReportEditor.OneFilePerGroup =
                false;
            model.ReportEditor.SelectedGroupField =
                model.ReportEditor.Fields
                    .FirstOrDefault();
            Render();
            WorkbenchReportsSectionView reports =
                workbench.FindControl<
                    WorkbenchReportsSectionView>(
                    "WorkbenchReportsSection")!;
            StackPanel groupName =
                reports.FindControl<StackPanel>(
                    "GroupFileNameField")!;
            StackPanel groupField =
                reports.FindControl<StackPanel>(
                    "GroupFieldPickerField")!;
            Assert.False(groupName.IsVisible);
            Assert.False(groupField.IsVisible);
            Assert.Null(
                model.ReportEditor
                    .CreateConfiguration()
                    .GroupByFieldId);

            model.ReportEditor.OneFilePerGroup =
                true;
            Render();
            Assert.True(groupName.IsVisible);
            Assert.True(groupField.IsVisible);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Apply_shortcut_opens_review_changes_without_executing_the_pending_batch()
    {
        using ServiceProvider services =
            BuildServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<
                MainWindow>();
        try
        {
            window.Width = 1200;
            window.Height = 700;
            window.Show();
            services.GetRequiredService<
                    INavigationService>()
                .Navigate(
                    ShellDestination.Workbench);
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            WorkbenchView workbench =
                Assert.IsType<WorkbenchView>(
                    window.FindControl<
                        ContentControl>(
                        "ContentHost")!.Content);
            model.PendingChanges.Add(
                new MetadataPreviewRow(
                    "fixture.mp3",
                    "Title",
                    "Before",
                    "After"));
            Render();
            WorkbenchPendingChangesDrawerView
                drawer =
                    workbench.FindControl<
                        WorkbenchPendingChangesDrawerView>(
                        "WorkbenchPendingChangesDrawer")!;
            Assert.False(drawer.IsVisible);

            await model.ExecuteShortcutAsync(
                new WorkbenchShortcutBinding(
                    Guid.NewGuid(),
                    "Ctrl+Enter",
                    WorkbenchShortcutTargetKind
                        .Command,
                    WorkbenchShortcutCommand
                        .ApplyReviewedChanges));
            Render();

            Assert.True(drawer.IsVisible);
            Assert.Single(model.PendingChanges);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Every_online_metadata_and_artwork_tab_fits_the_localized_minimum_viewport()
    {
        var settings = new TestSettings();
        var neutral =
            new ResourceLocalizationService(
                settings);
        var localization =
            new TestPseudoLocalizationService(
                neutral,
                expanded: false);
        using ServiceProvider services =
            Composition.BuildServices(
                collection =>
                {
                    collection
                        .AddSingleton<IAppSettings>(
                            settings);
                    collection.AddSingleton<
                        ILocalizationService>(
                        localization);
                    collection.AddSingleton<
                        IWorkbenchService>(
                        new TestWorkbenchService());
                });
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<
                MainWindow>();
        CultureInfo previousCulture =
            CultureInfo.CurrentUICulture;
        try
        {
            window.Width = 900;
            window.Height = 600;
            window.FontSize = 18;
            window.WindowState =
                WindowState.Normal;
            window.Show();
            services.GetRequiredService<
                    INavigationService>()
                .Navigate(
                    ShellDestination.Workbench);
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            model.SelectedSection =
                WorkbenchSection.OnlineMetadata;
            Render();

            WorkbenchView workbench =
                Assert.IsType<WorkbenchView>(
                    window.FindControl<
                        ContentControl>(
                        "ContentHost")!.Content);
            WorkbenchOnlineMetadataSectionView
                section =
                    workbench.FindControl<
                        WorkbenchOnlineMetadataSectionView>(
                        "WorkbenchOnlineMetadataSection")!;
            TabControl outer =
                section.FindControl<TabControl>(
                    "OnlineMetadataResultsTabs")!;
            TabControl artwork =
                section.FindControl<TabControl>(
                    "OnlineMetadataArtworkTabs")!;
            section.FindControl<Expander>(
                    "DiscoveryStep")!
                .IsExpanded = false;
            section.FindControl<Expander>(
                    "SearchStep")!
                .IsExpanded = false;
            Render();
            Assert.Equal(5, outer.ItemCount);
            Assert.Equal(2, artwork.ItemCount);

            (string Culture, bool Expanded)[]
                presentations =
                [
                    ("de-DE", false),
                    ("ja-JP", false),
                    ("zh-CN", false),
                    ("en-US", true),
                ];
            foreach (
                (string culture,
                    bool expanded) in
                presentations)
            {
                localization.SetExpanded(
                    expanded);
                localization.SetCulture(
                    culture);
                Render();
                Assert.Equal(
                    new Size(900, 600),
                    window.Bounds.Size);

                for (int outerIndex = 0;
                     outerIndex <
                     outer.ItemCount;
                     outerIndex++)
                {
                    outer.SelectedIndex =
                        outerIndex;
                    Render();
                    string context =
                        $"{culture}/expanded={expanded}/outer={outerIndex}";
                    Assert.Equal(
                        outerIndex,
                        outer.SelectedIndex);
                    AssertSelectedTabContentFits(
                        outer,
                        context);
                    AssertNoPageHorizontalOverflow(
                        section,
                        context);
                    AssertAtMostOnePrimaryPerActionRegion(
                        section,
                        context);
                    Assert.True(
                        BoundsRelativeTo(
                                outer,
                                section)
                            .Right <=
                        section.Bounds.Width + 1,
                        $"{context}: results tabs exceeded the Online Metadata section.");

                    if (outerIndex != 4)
                        continue;
                    for (int artworkIndex = 0;
                         artworkIndex <
                         artwork.ItemCount;
                         artworkIndex++)
                    {
                        artwork.SelectedIndex =
                            artworkIndex;
                        Render();
                        string artworkContext =
                            $"{context}/artwork={artworkIndex}";
                        Assert.Equal(
                            artworkIndex,
                            artwork.SelectedIndex);
                        AssertSelectedTabContentFits(
                            artwork,
                            artworkContext);
                        AssertNoPageHorizontalOverflow(
                            section,
                            artworkContext);
                        AssertAtMostOnePrimaryPerActionRegion(
                            section,
                            artworkContext);
                    }
                }

                string[] visibleText =
                    section
                        .GetVisualDescendants()
                        .OfType<TextBlock>()
                        .Where(text =>
                            text.IsEffectivelyVisible)
                        .Select(text =>
                            text.Text ?? "")
                        .Where(text =>
                            text.Length > 0)
                        .ToArray();
                if (expanded)
                {
                    Assert.Contains(
                        visibleText,
                        text =>
                            text.Contains('\u27E6'));
                }
                else
                {
                    Assert.DoesNotContain(
                        visibleText,
                        text =>
                            text.Contains('\u27E6'));
                }
            }
        }
        finally
        {
            window.Hide();
            CultureInfo.CurrentUICulture =
                previousCulture;
        }
    }

    [AvaloniaFact]
    public async Task Minimum_viewport_pages_avoid_sibling_overlap_horizontal_overflow_and_competing_primary_actions()
    {
        using ServiceProvider services = BuildServices();
        App.UseServicesForTests(services);
        AnalyzerViewModel analyzer =
            services.GetRequiredService<
                AnalyzerViewModel>();
        var healthRecord = new TrackRecord
        {
            Path =
                @"X:\Fixture\health.flac",
            Title = "Health fixture",
        };
        AnalysisRunViewModel healthRun =
            AnalysisRunViewModel.ForDuplicates(
                "Duplicates",
                [
                    new DuplicateGroup(
                        "Fixture",
                        [healthRecord]),
                ],
                "One group");
        analyzer.Runs.Add(healthRun);
        analyzer.SelectedRun = healthRun;

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
            window.WindowState =
                WindowState.Normal;
            window.Show();

            AssertPage(
                ShellDestination.Home,
                [
                    "SetupLayout",
                ]);
            AssertPage(
                ShellDestination.Health,
                [
                    "HealthActionLayout",
                    "DuplicateMasterDetailLayout",
                ]);
            AssertPage(
                ShellDestination.Ingest,
                [
                    "SourcePickerLayout",
                    "PreviewSummaryLayout",
                ]);
            AssertPage(
                ShellDestination.Devices,
                [
                    "DevicesContentLayout",
                ]);
            AssertPage(
                ShellDestination.Operations,
                [
                    "JobsLayout",
                ]);

            navigation.Navigate(
                ShellDestination.Operations);
            Render();
            OperationsView operations =
                Assert.IsType<OperationsView>(
                    CurrentPage());
            TabControl operationTabs =
                operations
                    .GetVisualDescendants()
                    .OfType<TabControl>()
                    .First();
            operationTabs.SelectedIndex = 2;
            Render();
            AssertVisibleGridChildrenDoNotOverlap(
                operations.FindControl<Grid>(
                    "RecoveryLayout")!,
                "Operations/Recovery");
            AssertNoPageHorizontalOverflow(
                operations,
                "Operations/Recovery");
            AssertAtMostOnePrimaryPerActionRegion(
                operations,
                "Operations/Recovery");

            AssertPage(
                ShellDestination.Library,
                []);
            AssertPage(
                ShellDestination.Settings,
                [
                    "SettingsNavigationLayout",
                ]);

            navigation.Navigate(
                ShellDestination.Workbench);
            Render();
            WorkbenchView workbench =
                Assert.IsType<WorkbenchView>(
                    CurrentPage());
            await workbench.AddDroppedSourcesAsync(
                [
                    "first.flac",
                    "second.flac",
                ]);
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            model.SelectedSection =
                WorkbenchSection.Files;
            Assert.NotNull(
                model.FileOperations);
            model.FileOperations!.SelectedKind =
                ReviewedFileOperationKind
                    .Quarantine;
            Render();
            WorkbenchFilesSectionView files =
                workbench.FindControl<
                    WorkbenchFilesSectionView>(
                    "WorkbenchFilesSection")!;
            Border scope =
                Assert.Single(
                    files.GetVisualDescendants()
                        .OfType<Border>(),
                    border =>
                        border.Name ==
                        "TargetSummaryText");
            TextBlock scopeText =
                Assert.IsType<TextBlock>(
                    scope.Child);
            Assert.True(
                scope.IsEffectivelyVisible);
            Assert.False(
                string.IsNullOrWhiteSpace(
                    scopeText.Text));
            Assert.Equal(
                model.FileOperations.TargetSummary,
                scopeText.Text);
            Assert.Contains(
                scopeText.Text!,
                character =>
                    char.IsDigit(character));
            Assert.True(
                UiViewportReachability
                    .TryGetFullyVisibleBounds(
                        files,
                        scope,
                        out Rect scopeBounds,
                        out string scopeDetail),
                $"Workbench/File operations destructive scope was clipped: {scopeBounds} within {files.Bounds.Size}. {scopeDetail}");
            AssertVisibleGridChildrenDoNotOverlap(
                Assert.Single(
                    files.GetVisualDescendants()
                        .OfType<Grid>(),
                    grid =>
                        grid.Name ==
                        "SourceOptionsLayout"),
                "Workbench/File operations");
            AssertNoPageHorizontalOverflow(
                files,
                "Workbench/File operations");
            AssertAtMostOnePrimaryPerActionRegion(
                workbench,
                "Workbench/File operations");
        }
        finally
        {
            window.Hide();
        }

        Control CurrentPage() =>
            Assert.IsAssignableFrom<Control>(
                window.FindControl<
                        ContentControl>(
                        "ContentHost")!
                    .Content);

        void AssertPage(
            ShellDestination destination,
            IReadOnlyList<string>
                nonOverlappingGrids)
        {
            navigation.Navigate(destination);
            Render();
            Control page = CurrentPage();
            string context =
                destination.ToString();
            AssertNoPageHorizontalOverflow(
                page,
                context);
            AssertAtMostOnePrimaryPerActionRegion(
                page,
                context);
            foreach (string gridName in
                     nonOverlappingGrids)
            {
                Grid? grid =
                    page.FindControl<Grid>(
                        gridName);
                Assert.NotNull(grid);
                if (grid.IsEffectivelyVisible)
                {
                    AssertVisibleGridChildrenDoNotOverlap(
                        grid,
                        $"{context}/{gridName}");
                }
            }
        }
    }

    [AvaloniaFact]
    public void Page_header_command_bar_preserves_primary_and_more_at_its_measured_breakpoint()
    {
        var primary = new Button
        {
            Width = 160,
            Content = "Primary action",
        };
        var secondary = new Button
        {
            Width = 240,
            Content = "Secondary action",
        };
        var more = new Button
        {
            Width = 90,
            Content = "More",
            Flyout = new MenuFlyout
            {
                Items =
                {
                    new MenuItem
                    {
                        Header =
                            "Secondary action",
                    },
                },
            },
        };
        var header = new PageHeader
        {
            Title = "Page",
            PrimaryAction = primary,
            SecondaryActions = secondary,
            MoreAction = more,
        };
        (Window window, Border host) =
            ShowInFixedHost(
                header,
                968,
                120);
        try
        {
            AssertLayout(
                966,
                compact: true);
            AssertLayout(
                967,
                compact: false);
            AssertLayout(
                968,
                compact: false);

            ResizeHost(
                host,
                header,
                966,
                120);
            MenuFlyout flyout =
                Assert.IsType<MenuFlyout>(
                    more.Flyout);
            flyout.ShowAt(more);
            Render();
            Assert.Equal(
                "Secondary action",
                Assert.Single(
                    flyout.Items
                        .OfType<MenuItem>())
                    .Header);
        }
        finally
        {
            window.Hide();
        }

        void AssertLayout(
            double width,
            bool compact)
        {
            ResizeHost(
                host,
                header,
                width,
                120);
            Grid commandBar =
                header.FindControl<Grid>(
                    "CommandBar")!;
            ContentPresenter legacy =
                header.FindControl<ContentPresenter>(
                    "ActionsPresenter")!;
            ContentPresenter secondaryPresenter =
                header.FindControl<ContentPresenter>(
                    "SecondaryActionsPresenter")!;

            Assert.True(
                primary.IsEffectivelyVisible);
            Assert.True(
                more.IsEffectivelyVisible);
            Assert.Equal(
                !compact,
                secondary.IsEffectivelyVisible);
            Assert.Equal(
                !compact,
                secondaryPresenter.IsVisible);
            Assert.False(
                legacy.IsVisible);
            Assert.Equal(
                0,
                Grid.GetRow(commandBar));
            Assert.Equal(
                compact,
                header.Classes.Contains(
                    "compact-actions"));
            Assert.DoesNotContain(
                "stacked-actions",
                header.Classes);

            Rect primaryBounds =
                BoundsRelativeTo(
                    primary,
                    header);
            Rect moreBounds =
                BoundsRelativeTo(
                    more,
                    header);
            Assert.True(
                primaryBounds.Left >= -1 &&
                primaryBounds.Right <=
                    header.Bounds.Width + 1,
                $"Primary action was clipped at {width:0}px: {primaryBounds} / {header.Bounds}.");
            Assert.True(
                moreBounds.Left >= -1 &&
                moreBounds.Right <=
                    header.Bounds.Width + 1,
                $"More action was clipped at {width:0}px: {moreBounds} / {header.Bounds}.");
            Assert.True(
                primaryBounds.Bottom <=
                    header.Bounds.Height + 1 &&
                moreBounds.Bottom <=
                    header.Bounds.Height + 1,
                $"Header actions exceeded the command row at {width:0}px.");
        }
    }

    [AvaloniaFact]
    public void Page_titles_are_level_one_headings_and_never_keyboard_tab_stops()
    {
        var action = new Button
        {
            Content = "Action",
        };
        var header = new PageHeader
        {
            Title = "Semantic page title",
            Subtitle = "Supporting context",
            Actions = action,
        };
        (Window window, _) =
            ShowInFixedHost(
                header,
                700,
                120);
        try
        {
            TextBlock title =
                header.FindControl<TextBlock>(
                    "TitleBlock")!;
            Assert.Equal(
                1,
                AutomationProperties
                    .GetHeadingLevel(title));
            Assert.False(
                header.Focusable);
            Assert.False(
                title.Focusable);
            Assert.False(
                title.Focus());
            Assert.NotSame(
                title,
                window.FocusManager?
                    .GetFocusedElement());

            Assert.True(
                action.Focus());
            Assert.Same(
                action,
                window.FocusManager?
                    .GetFocusedElement());
        }
        finally
        {
            window.Hide();
        }

        using ServiceProvider services =
            BuildServices();
        App.UseServicesForTests(services);
        var home = new HomeView();
        (Window homeWindow, _) =
            ShowInFixedHost(
                home,
                900,
                600);
        try
        {
            TextBlock title =
                Assert.Single(
                    home.GetVisualDescendants()
                        .OfType<TextBlock>(),
                    text =>
                        text.Classes.Contains(
                            "page-title"));
            Assert.Equal(
                1,
                AutomationProperties
                    .GetHeadingLevel(title));
            Assert.False(title.Focusable);
            Assert.False(title.Focus());
        }
        finally
        {
            homeWindow.Hide();
        }
    }

    private static (
        Window Window,
        Border Host)
        ShowInFixedHost(
            Control view,
            double width,
            double height)
    {
        var host = new Border
        {
            Width = width,
            Height = height,
            HorizontalAlignment =
                HorizontalAlignment.Left,
            VerticalAlignment =
                VerticalAlignment.Top,
            Child = view,
        };
        var window = new Window
        {
            Width = 1600,
            Height = 1000,
            Content = host,
        };
        window.Show();
        Render();
        Assert.Equal(
            width,
            view.Bounds.Width);
        Assert.Equal(
            height,
            view.Bounds.Height);
        return (window, host);
    }

    private static void ResizeHost(
        Border host,
        Control view,
        double width,
        double height)
    {
        host.Width = width;
        host.Height = height;
        Render();
        Assert.Equal(
            width,
            view.Bounds.Width);
        Assert.Equal(
            height,
            view.Bounds.Height);
    }

    private static void ResizeSettingsPageToWidth(
        Border host,
        SettingsView view,
        double targetPageWidth)
    {
        for (int attempt = 0;
             attempt < 8;
             attempt++)
        {
            Render();
            double measured =
                MeasureActiveSettingsPageWidth(
                    view);
            double difference =
                targetPageWidth - measured;
            if (Math.Abs(difference) < 0.01)
                break;
            host.Width = Math.Max(
                400,
                host.Width + difference);
        }
        Render();
        Assert.Equal(
            targetPageWidth,
            MeasureActiveSettingsPageWidth(
                view),
            precision: 2);
    }

    private static double
        MeasureActiveSettingsPageWidth(
            SettingsView view)
    {
        ScrollViewer viewport =
            Assert.Single(
                view.GetVisualDescendants()
                    .OfType<ScrollViewer>(),
                scroll =>
                    scroll.IsEffectivelyVisible &&
                    scroll.Classes.Contains(
                        "settings-scroll"));
        StackPanel content =
            Assert.Single(
                viewport.GetVisualDescendants()
                    .OfType<StackPanel>(),
                panel =>
                    panel.Classes.Contains(
                        "settings-content"));
        return viewport.Bounds.Width -
            content.Margin.Left -
            content.Margin.Right;
    }

    private static void AssertSelectedTabContentFits(
        TabControl tabs,
        string context)
    {
        TabItem selected =
            Assert.IsType<TabItem>(
                tabs.SelectedItem);
        Control content =
            Assert.IsAssignableFrom<Control>(
                selected.Content);
        Assert.True(
            content.IsEffectivelyVisible,
            $"{context}: selected tab content is not visible.");
        Rect bounds =
            BoundsRelativeTo(
                content,
                tabs);
        string ancestry = string.Join(
            " -> ",
            content
                .GetVisualAncestors()
                .OfType<Control>()
                .Take(20)
                .Select(control =>
                    $"{control.Name ?? control.GetType().Name}" +
                    $"[{control.Bounds.Width:0.##}x" +
                    $"{control.Bounds.Height:0.##}]"));
        Assert.True(
            bounds.Left >= -1 &&
            bounds.Right <=
            tabs.Bounds.Width + 1,
            $"{context}: selected tab content overflowed horizontally: {bounds} within {tabs.Bounds.Size}. Ancestors: {ancestry}.");
        Assert.True(
            bounds.Top >= -1 &&
            bounds.Bottom <=
            tabs.Bounds.Height + 1,
            $"{context}: selected tab content overflowed vertically: {bounds} within {tabs.Bounds.Size}. Ancestors: {ancestry}.");
    }

    private static void
        AssertNoPageHorizontalOverflow(
            Control root,
            string context)
    {
        foreach (
            ScrollViewer scroll in
            root.GetVisualDescendants()
                .OfType<ScrollViewer>()
                .Where(control =>
                    control.IsEffectivelyVisible)
                .Where(control =>
                    !control.GetVisualAncestors()
                        .Any(ancestor =>
                            ancestor is
                                AppDataGrid or
                                TextBox or
                                ComboBox or
                                ListBox or
                                NumericUpDown)))
        {
            Assert.True(
                scroll.Extent.Width <=
                scroll.Viewport.Width + 1,
                $"{context}: page-level horizontal overflow in {scroll.Name ?? scroll.GetType().Name}: extent {scroll.Extent.Width:0.##}, viewport {scroll.Viewport.Width:0.##}.");
        }
    }

    private static void
        AssertAtMostOnePrimaryPerActionRegion(
            Control root,
            string context)
    {
        Control[] regions =
        [
            .. root.GetVisualDescendants()
                .OfType<Control>()
                .Where(control =>
                    control.IsEffectivelyVisible &&
                    (control is WrapPanel ||
                     control is PageHeader ||
                     control.Classes.Contains(
                         "sticky-footer") ||
                     control.Classes.Contains(
                         "quiet-toolbar") ||
                     control.Classes.Contains(
                         "toolbar"))),
        ];
        foreach (Control region in regions)
        {
            Control[] primaryActions =
            [
                .. region
                    .GetVisualDescendants()
                    .OfType<Control>()
                    .Where(control =>
                        control.IsEffectivelyVisible &&
                        control.Classes.Contains(
                            "primary"))
                    .Where(control =>
                        ReferenceEquals(
                            NearestActionRegion(
                                control,
                                regions),
                            region)),
            ];
            Assert.True(
                primaryActions.Length <= 1,
                $"{context}: {region.Name ?? region.GetType().Name} contains {primaryActions.Length} primary actions: {string.Join(", ", primaryActions.Select(action => action.Name ?? action.GetType().Name))}.");
        }
    }

    private static Control?
        NearestActionRegion(
            Control control,
            IReadOnlyCollection<Control>
                regions) =>
        control.GetVisualAncestors()
            .OfType<Control>()
            .FirstOrDefault(
                regions.Contains);

    private static void
        AssertVisibleGridChildrenDoNotOverlap(
            Grid grid,
            string context)
    {
        Control[] children =
        [
            .. grid.Children
                .OfType<Control>()
                .Where(control =>
                    control.IsEffectivelyVisible &&
                    control.Bounds.Width > 0 &&
                    control.Bounds.Height > 0),
        ];
        for (int firstIndex = 0;
             firstIndex < children.Length;
             firstIndex++)
        {
            Rect first =
                BoundsRelativeTo(
                    children[firstIndex],
                    grid);
            for (int secondIndex =
                     firstIndex + 1;
                 secondIndex <
                 children.Length;
                 secondIndex++)
            {
                Rect second =
                    BoundsRelativeTo(
                        children[secondIndex],
                        grid);
                double overlapWidth =
                    Math.Min(
                        first.Right,
                        second.Right) -
                    Math.Max(
                        first.Left,
                        second.Left);
                double overlapHeight =
                    Math.Min(
                        first.Bottom,
                        second.Bottom) -
                    Math.Max(
                        first.Top,
                        second.Top);
                Assert.False(
                    overlapWidth > 1 &&
                    overlapHeight > 1,
                    $"{context}: {children[firstIndex].Name ?? children[firstIndex].GetType().Name} {first} overlaps {children[secondIndex].Name ?? children[secondIndex].GetType().Name} {second}.");
            }
        }
    }

    private static Rect BoundsRelativeTo(
        Control control,
        Control ancestor)
    {
        Point? origin =
            control.TranslatePoint(
                default,
                ancestor);
        Assert.NotNull(origin);
        return new Rect(
            origin.Value,
            control.Bounds.Size);
    }

    private static ServiceProvider BuildServices(
        TestSettings? settings = null)
    {
        settings ??= new TestSettings();
        return Composition.BuildServices(
            services =>
            {
                services.AddSingleton<IAppSettings>(
                    settings);
                services.AddSingleton<IWorkbenchService>(
                    new TestWorkbenchService());
            });
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

    private sealed class TestWorkbenchService :
        IWorkbenchService
    {
        public Task<WorkbenchLoadResult> LoadAsync(
            WorkbenchLoadRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                new WorkbenchLoadResult(
                [
                    .. request.Sources.Select(
                        source =>
                            new MediaDocument(
                                Path.GetFullPath(
                                    source),
                                [],
                                [],
                                null,
                                new(
                                    Path.GetFullPath(
                                        source),
                                    10,
                                    DateTime.UtcNow,
                                    "snapshot"),
                                true)),
                ],
                []));
        }
    }
}
