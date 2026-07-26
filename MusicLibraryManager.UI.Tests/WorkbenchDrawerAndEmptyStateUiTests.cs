using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Views;
using MusicLibraryManager.Views.WorkbenchSections;
using MusicFileUtilities;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

public sealed class
    WorkbenchDrawerAndEmptyStateUiTests
{
    [AvaloniaFact]
    public void Output_and_automate_sections_remove_the_inspector_command_and_restore_its_preference()
    {
        using ServiceProvider services =
            BuildServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            WorkbenchView view =
                ShowWorkbench(
                    window,
                    services,
                    1800,
                    900);
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            ILocalizationService localization =
                services.GetRequiredService<
                    ILocalizationService>();
            Button toggle =
                view.FindControl<Button>(
                    "WorkbenchInspectorToggle")!;
            MenuFlyout more =
                Assert.IsType<MenuFlyout>(
                    view.FindControl<Button>(
                            "WorkbenchMoreButton")!
                        .Flyout);
            MenuItem menuInspector =
                Assert.IsType<MenuItem>(
                    more.Items[0]);
            Control drawer =
                view.FindControl<Control>(
                    "WorkbenchInspectorDrawer")!;

            Assert.True(model.IsInspectorOpen);
            Assert.True(drawer.IsVisible);
            Assert.True(toggle.IsVisible);
            Assert.True(toggle.IsEnabled);
            Assert.True(menuInspector.IsVisible);
            Assert.True(menuInspector.IsEnabled);
            Assert.Equal(
                localization.Get(
                    "Library.Action.InspectorTooltip"),
                ToolTip.GetTip(toggle)?.ToString());

            foreach (WorkbenchSection section in
                     new[]
                     {
                         WorkbenchSection.Reports,
                         WorkbenchSection.Playlists,
                         WorkbenchSection.Tools,
                         WorkbenchSection.Shortcuts,
                     })
            {
                model.SelectedSection = section;
                Render();

                Assert.True(
                    model.IsInspectorOpen);
                Assert.False(drawer.IsVisible);
                Assert.False(toggle.IsVisible);
                Assert.False(toggle.IsEnabled);
                Assert.False(
                    menuInspector.IsVisible);
                Assert.False(
                    menuInspector.IsEnabled);
            }

            model.SelectedSection =
                WorkbenchSection.AllFields;
            Render();

            Assert.True(model.IsInspectorOpen);
            Assert.True(drawer.IsVisible);
            Assert.True(toggle.IsVisible);
            Assert.True(toggle.IsEnabled);
            Assert.True(menuInspector.IsVisible);
            Assert.True(menuInspector.IsEnabled);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void All_fields_and_file_operations_show_reachable_localized_source_setup_states()
    {
        using ServiceProvider services =
            BuildServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            WorkbenchView view =
                ShowWorkbench(
                    window,
                    services,
                    900,
                    600);
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            ILocalizationService localization =
                services.GetRequiredService<
                    ILocalizationService>();

            AssertSetupState(
                WorkbenchSection.AllFields,
                "AllFieldsEmptyState",
                "AllFieldsEmptyContext",
                "AllFieldsEmptyTitle",
                "AllFieldsEmptyDescription",
                "AllFieldsAddFilesButton",
                "Workbench.AllFields.Title");
            Assert.False(
                view.FindControl<Button>(
                        "AllFieldsNewButton")!
                    .IsEffectivelyEnabled);
            AssertSetupState(
                WorkbenchSection.Files,
                "FileOperationsEmptyState",
                "FileOperationsEmptyContext",
                "FileOperationsEmptyTitle",
                "FileOperationsEmptyDescription",
                "FileOperationsAddFilesButton",
                "Workbench.Section.FilesAutomation");

            void AssertSetupState(
                WorkbenchSection section,
                string stateName,
                string contextName,
                string titleName,
                string descriptionName,
                string actionName,
                string contextResource)
            {
                model.SelectedSection = section;
                Render();

                Border state =
                    view.FindControl<Border>(
                        stateName)!;
                TextBlock context =
                    view.FindControl<TextBlock>(
                        contextName)!;
                TextBlock title =
                    view.FindControl<TextBlock>(
                        titleName)!;
                TextBlock description =
                    view.FindControl<TextBlock>(
                        descriptionName)!;
                Button action =
                    view.FindControl<Button>(
                        actionName)!;
                Assert.True(
                    state.IsEffectivelyVisible);
                Assert.Equal(
                    localization.Get(
                        contextResource),
                    context.Text);
                Assert.Equal(
                    localization.Get(
                        "Workbench.Session.EmptyTitle"),
                    title.Text);
                Assert.Equal(
                    localization.Get(
                        "Workbench.Session.EmptyDescription"),
                    description.Text);
                Assert.Equal(
                    localization.Get(
                        "Workbench.Action.AddFiles"),
                    action.Content?.ToString());
                Assert.True(
                    action.IsEffectivelyVisible);
                Assert.True(
                    action.IsEffectivelyEnabled);
                Assert.True(
                    state.Bounds.Width <=
                    view.Bounds.Width + 1);
                Assert.True(
                    state.Bounds.Height <=
                    view.Bounds.Height + 1);
            }
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Workbench_inspector_keeps_one_scroll_owner_and_its_footer_reachable_while_scrolling()
    {
        using ServiceProvider services =
            BuildServices();
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            WorkbenchTrackViewModel track =
                Track("inspector-scroll.flac");
            model.Files.Add(track);
            model.SelectedFile = track;
            model.SetSelectedFiles([track]);
            await model.Inspector!.LoadAsync(
                new SelectionContext(
                    [track.Path],
                    ReadArtworkDirectly: false));

            WorkbenchView view =
                ShowWorkbench(
                    window,
                    services,
                    1440,
                    700);
            Control drawer =
                view.FindControl<Control>(
                    "WorkbenchInspectorDrawer")!;
            ScrollViewer scroll =
                view.FindControl<ScrollViewer>(
                    "InspectorContent")!;
            Border footer =
                view.FindControl<Border>(
                    "InspectorStickyFooter")!;
            Button review =
                view.FindControl<Button>(
                    "InspectorReviewChangesButton")!;
            Button discard =
                view.FindControl<Button>(
                    "InspectorDiscardEditsButton")!;

            Assert.True(drawer.IsVisible);
            Assert.True(scroll.IsEffectivelyVisible);
            Assert.True(footer.IsEffectivelyVisible);
            Assert.True(review.IsEffectivelyVisible);
            Assert.True(discard.IsEffectivelyVisible);
            Assert.Single(
                drawer
                    .GetVisualDescendants()
                    .OfType<ScrollViewer>(),
                candidate =>
                    candidate.Name ==
                    "InspectorContent");

            Point footerTopBefore =
                footer.TranslatePoint(
                    new Point(0, 0),
                    drawer) ??
                throw new InvalidOperationException(
                    "The Inspector footer was not attached.");
            scroll.Offset =
                new Vector(
                    0,
                    Math.Max(
                        0,
                        scroll.Extent.Height -
                        scroll.Viewport.Height));
            Render();
            Point footerTopAfter =
                footer.TranslatePoint(
                    new Point(0, 0),
                    drawer) ??
                throw new InvalidOperationException(
                    "The Inspector footer was not attached after scrolling.");
            Point footerBottom =
                footer.TranslatePoint(
                    new Point(
                        0,
                        footer.Bounds.Height),
                    drawer) ??
                throw new InvalidOperationException(
                    "The Inspector footer bounds were unavailable.");

            Assert.InRange(
                Math.Abs(
                    footerTopAfter.Y -
                    footerTopBefore.Y),
                0,
                0.5);
            Assert.InRange(
                footerBottom.Y,
                0,
                drawer.Bounds.Height + 1);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaTheory]
    [InlineData(1440, "1")]
    [InlineData(1440, "99999")]
    [InlineData(1920, "1")]
    [InlineData(1920, "99999")]
    [InlineData(2560, "1")]
    [InlineData(2560, "99999")]
    public void Workbench_drawer_stays_within_its_bounds_for_overlay_docked_and_extreme_persisted_widths(
        double width,
        string persistedWidth)
    {
        var settings =
            new TestSettings();
        settings.SetPreference(
            "manager.split.workbench",
            persistedWidth);
        using ServiceProvider services =
            BuildServices(settings);
        App.UseServicesForTests(services);
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            WorkbenchView view =
                ShowWorkbench(
                    window,
                    services,
                    width,
                    900);
            PersistedSplitView split =
                view.FindControl<PersistedSplitView>(
                    "WorkbenchSplit")!;
            ContentPresenter presenter =
                split.FindControl<ContentPresenter>(
                    "RightPresenter")!;
            Border drawer =
                view.FindControl<Border>(
                    "WorkbenchDrawerPane")!;
            if (!drawer.IsEffectivelyVisible)
            {
                view.FindControl<Button>(
                        "WorkbenchInspectorToggle")!
                    .RaiseEvent(
                        new Avalonia.Interactivity
                            .RoutedEventArgs(
                                Button.ClickEvent));
                Render();
            }

            Assert.True(
                drawer.IsEffectivelyVisible,
                $"Drawer did not open at {width:0}px.");
            Assert.InRange(
                presenter.Bounds.Width,
                300,
                430);
            Assert.InRange(
                drawer.Bounds.Width,
                300,
                430);
            Assert.True(
                presenter.Bounds.Right <=
                split.Bounds.Width + 1,
                $"Drawer exceeded host at {width:0}px: " +
                $"{presenter.Bounds.Right:0}/" +
                $"{split.Bounds.Width:0}.");
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Every_workbench_section_has_a_contextual_empty_and_populated_minimum_size_contract()
    {
        using (ServiceProvider emptyServices =
               BuildServices())
        {
            App.UseServicesForTests(
                emptyServices);
            MainWindow emptyWindow =
                emptyServices.GetRequiredService<
                    MainWindow>();
            try
            {
                WorkbenchView emptyView =
                    ShowWorkbench(
                        emptyWindow,
                        emptyServices,
                        900,
                        600);
                WorkbenchViewModel model =
                    emptyServices.GetRequiredService<
                        WorkbenchViewModel>();
                (
                    WorkbenchSection Section,
                    string State)[]
                    emptyContracts =
                [
                    (
                        WorkbenchSection.Session,
                        "SessionEmptyState"),
                    (
                        WorkbenchSection.BulkOperation,
                        "BulkEmptyState"),
                    (
                        WorkbenchSection.AllFields,
                        "AllFieldsEmptyState"),
                    (
                        WorkbenchSection.Files,
                        "FileOperationsEmptyState"),
                    (
                        WorkbenchSection.OnlineMetadata,
                        "OnlineMetadataSourceEmptyState"),
                    (
                        WorkbenchSection.Reports,
                        "ReportsSourceEmptyState"),
                    (
                        WorkbenchSection.Playlists,
                        "PlaylistsSourceEmptyState"),
                    (
                        WorkbenchSection.Tools,
                        "ToolsSourceEmptyState"),
                    (
                        WorkbenchSection.Shortcuts,
                        "ShortcutsEmptyState"),
                ];
                foreach (var contract in
                         emptyContracts)
                {
                    model.SelectedSection =
                        contract.Section;
                    Render();
                    Control state =
                        emptyView.FindControl<Control>(
                            contract.State)!;
                    Assert.True(
                        state.IsEffectivelyVisible,
                        $"{contract.Section} did not expose " +
                        $"its empty state at 900x600.");
                    Assert.True(
                        state.Bounds.Width <=
                        emptyView.Bounds.Width + 1);
                    Assert.True(
                        state.Bounds.Height <=
                        emptyView.Bounds.Height + 1);
                }
            }
            finally
            {
                emptyWindow.Hide();
            }
        }

        using ServiceProvider populatedServices =
            BuildServices();
        App.UseServicesForTests(
            populatedServices);
        WorkbenchViewModel populatedModel =
            populatedServices.GetRequiredService<
                WorkbenchViewModel>();
        WorkbenchTrackViewModel track =
            Track("section-contract.flac");
        populatedModel.Files.Add(track);
        populatedModel.SelectedFile = track;
        populatedModel.SetSelectedFiles([track]);
        MainWindow populatedWindow =
            populatedServices.GetRequiredService<
                MainWindow>();
        try
        {
            WorkbenchView populatedView =
                ShowWorkbench(
                    populatedWindow,
                    populatedServices,
                    900,
                    600);
            populatedModel.SelectedMetadataField =
                new(
                    MetadataFieldKey.Known(
                        TagFields.Title),
                    "Vorbis comments",
                    ["Contract title"]);
            (
                WorkbenchSection Section,
                string Footer,
                string? EmptyResult)[]
                populatedContracts =
            [
                (
                    WorkbenchSection.Session,
                    "SessionStatusFooter",
                    null),
                (
                    WorkbenchSection.BulkOperation,
                    "BulkStickyFooter",
                    "BulkPreviewEmptyState"),
                (
                    WorkbenchSection.AllFields,
                    "AllFieldsStickyFooter",
                    null),
                (
                    WorkbenchSection.Files,
                    "ReviewedFileOperationStickyFooter",
                    "ReviewedFileOperationPreviewEmptyState"),
                (
                    WorkbenchSection.OnlineMetadata,
                    "OnlineMetadataStickyFooter",
                    "AudioResultsEmptyState"),
                (
                    WorkbenchSection.Reports,
                    "ReportsStickyFooter",
                    "PreviewReportButton"),
                (
                    WorkbenchSection.Playlists,
                    "PlaylistsStickyFooter",
                    "PreviewPlaylistButton"),
                (
                    WorkbenchSection.Tools,
                    "ToolsStickyFooter",
                    "ExternalToolPreviewEmptyState"),
                (
                    WorkbenchSection.Shortcuts,
                    "ShortcutsStickyFooter",
                    null),
            ];
            foreach (var contract in
                     populatedContracts)
            {
                populatedModel.SelectedSection =
                    contract.Section;
                Render();
                if (contract.Section ==
                    WorkbenchSection.Shortcuts)
                {
                    populatedView.FindControl<Button>(
                            "NewShortcutEmptyButton")!
                        .RaiseEvent(
                            new Avalonia.Interactivity
                                .RoutedEventArgs(
                                    Button.ClickEvent));
                    Render();
                }

                Control footer =
                    populatedView.FindControl<Control>(
                        contract.Footer)!;
                Assert.True(
                    footer.IsEffectivelyVisible,
                    $"{contract.Section} did not keep " +
                    $"its current action reachable at " +
                    "900x600.");
                Point footerBottom =
                    footer.TranslatePoint(
                        new Point(
                            0,
                            footer.Bounds.Height),
                        populatedView) ??
                    throw new
                        InvalidOperationException(
                            $"{contract.Section} footer " +
                            "was detached.");
                Assert.InRange(
                    footerBottom.Y,
                    0,
                    populatedView.Bounds.Height + 1);

                if (contract.EmptyResult is not null)
                {
                    Control resultState =
                        populatedView.FindControl<Control>(
                            contract.EmptyResult)!;
                    Assert.True(
                        resultState.IsVisible,
                        $"{contract.Section} did not " +
                        "retain its contextual pre-preview " +
                        "state.");
                }
            }
        }
        finally
        {
            populatedWindow.Hide();
        }
    }

    [AvaloniaFact]
    public void External_tools_keeps_configuration_scrollable_and_review_surfaces_separate_at_expanded_minimum_size()
    {
        using ServiceProvider services =
            BuildServices(
                pseudoExpanded: true,
                externalTools:
                    new ManyInvocationExternalToolService());
        App.UseServicesForTests(services);
        WorkbenchViewModel model =
            services.GetRequiredService<
                WorkbenchViewModel>();
        WorkbenchTrackViewModel track =
            Track("tools-minimum.flac");
        model.Files.Add(track);
        model.SelectedFile = track;
        model.SetSelectedFiles([track]);
        MainWindow window =
            services.GetRequiredService<
                MainWindow>();
        window.FontSize = 18;
        try
        {
            WorkbenchView view =
                ShowWorkbench(
                    window,
                    services,
                    900,
                    600);
            model.SelectedSection =
                WorkbenchSection.Tools;
            Render();

            WorkbenchToolsSectionView tools =
                view.FindControl<
                    WorkbenchToolsSectionView>(
                    "WorkbenchToolsSection")!;
            Grid layout =
                tools.FindControl<Grid>(
                    "SectionLayout")!;
            ScrollViewer editor =
                tools.FindControl<ScrollViewer>(
                    "EditorScroll")!;
            Grid reviewed =
                tools.FindControl<Grid>(
                    "ReviewedPanel")!;
            Border empty =
                tools.FindControl<Border>(
                    "ExternalToolPreviewEmptyState")!;
            TextBlock description =
                tools.FindControl<TextBlock>(
                    "ExternalToolPreviewEmptyDescription")!;
            TextBlock emptyTitle =
                tools.FindControl<TextBlock>(
                    "ExternalToolPreviewEmptyTitle")!;
            Border status =
                tools.FindControl<Border>(
                    "ToolsStatusBanner")!;
            Border footer =
                tools.FindControl<Border>(
                    "ToolsStickyFooter")!;

            Assert.True(
                empty
                    .IsEffectivelyVisible);
            Assert.True(
                emptyTitle
                    .IsEffectivelyVisible);
            Assert.False(
                description
                    .IsEffectivelyVisible);
            Assert.True(
                editor.Bounds.Height >= 170,
                $"The configuration viewport collapsed to {editor.Bounds.Height:0.#} px. " +
                $"Layout={layout.Bounds.Height:0.#}; review={reviewed.Bounds.Height:0.#}; " +
                $"empty={empty.Bounds.Height:0.#}; status={status.Bounds.Height:0.#}; " +
                $"footer={footer.Bounds.Height:0.#}.");
            AssertBefore(
                editor,
                reviewed,
                layout,
                "External tools editor/review");
            AssertBefore(
                status,
                footer,
                reviewed,
                "External tools status/footer");

            TextBox arguments =
                tools.FindControl<TextBox>(
                    "ExternalToolArgumentsInput")!;
            Assert.True(
                editor.Extent.Height >
                editor.Viewport.Height,
                "The compact editor did not expose its required fields through a scroll owner.");
            arguments.BringIntoView();
            Render();
            Point argumentsTop =
                arguments.TranslatePoint(
                    default,
                    editor) ??
                throw new InvalidOperationException(
                    "The arguments field was detached.");
            Assert.InRange(
                argumentsTop.Y,
                -1,
                editor.Bounds.Height + 1);
            Assert.InRange(
                argumentsTop.Y +
                arguments.Bounds.Height,
                -1,
                editor.Bounds.Height + 1);

            model.ExternalToolEditor
                .Executable =
                "fixture-tool.exe";
            Assert.True(
                model.PreviewExternalToolCommand
                    .CanExecute(null));
            model.PreviewExternalToolCommand
                .Execute(null);
            Render();

            AppDataGrid invocationGrid =
                tools.FindControl<
                    AppDataGrid>(
                    "ExternalToolInvocationGrid")!;
            Button run =
                tools.FindControl<Button>(
                    "RunExternalToolButton")!;
            Assert.False(
                empty
                    .IsEffectivelyVisible);
            Assert.Equal(
                ManyInvocationExternalToolService
                    .InvocationCount,
                model.ExternalToolInvocations
                    .Count);
            Assert.True(
                run.IsEffectivelyVisible);
            Assert.True(
                run.IsEffectivelyEnabled);
            Assert.True(
                invocationGrid.Bounds.Height + 1 >=
                invocationGrid
                    .ColumnHeaderHeight +
                invocationGrid.RowHeight,
                $"The populated compact preview retained only {invocationGrid.Bounds.Height:0.#} px.");
            DataGridRow[] realizedRows =
            [
                .. invocationGrid
                    .GetVisualDescendants()
                    .OfType<DataGridRow>(),
            ];
            Assert.NotEmpty(
                realizedRows);
            Assert.True(
                realizedRows.Length <
                ManyInvocationExternalToolService
                    .InvocationCount,
                "The populated preview did not virtualize its many rows.");
            Assert.All(
                realizedRows,
                row =>
                {
                    Point rowTop =
                        row.TranslatePoint(
                            default,
                            invocationGrid) ??
                        throw new InvalidOperationException(
                            "A realized invocation row was detached.");
                    Assert.True(
                        rowTop.Y +
                        row.Bounds.Height <=
                        invocationGrid
                            .Bounds.Height + 1,
                        $"A realized invocation row was clipped at {rowTop.Y:0.#}+{row.Bounds.Height:0.#}/{invocationGrid.Bounds.Height:0.#} px.");
                });
            AssertInside(
                footer,
                reviewed,
                "External tools footer");
            AssertInside(
                run,
                reviewed,
                "External tools Run action");
            Assert.True(
                editor.Bounds.Height >= 130,
                $"The populated preview reduced the editor to {editor.Bounds.Height:0.#} px.");
        }
        finally
        {
            window.Hide();
        }

        static void AssertBefore(
            Control first,
            Control second,
            Control viewport,
            string description)
        {
            Point firstTop =
                first.TranslatePoint(
                    default,
                    viewport) ??
                throw new InvalidOperationException(
                    $"{description}: first control was detached.");
            Point secondTop =
                second.TranslatePoint(
                    default,
                    viewport) ??
                throw new InvalidOperationException(
                    $"{description}: second control was detached.");
            Assert.True(
                firstTop.Y +
                first.Bounds.Height <=
                secondTop.Y + 1,
                $"{description} overlapped: " +
                $"{firstTop.Y + first.Bounds.Height:0.#} > " +
                $"{secondTop.Y:0.#}.");
        }

        static void AssertInside(
            Control control,
            Control viewport,
            string description)
        {
            Point top =
                control.TranslatePoint(
                    default,
                    viewport) ??
                throw new InvalidOperationException(
                    $"{description} was detached.");
            Assert.True(
                top.Y >= -1 &&
                top.Y +
                control.Bounds.Height <=
                viewport.Bounds.Height + 1,
                $"{description} escaped its viewport: {top.Y:0.#}+{control.Bounds.Height:0.#}/{viewport.Bounds.Height:0.#} px.");
        }
    }

    [AvaloniaFact]
    public void Report_and_playlist_empty_previews_collapse_until_reviewed_outputs_exist()
    {
        using ServiceProvider services =
            BuildServices();
        App.UseServicesForTests(services);
        WorkbenchViewModel model =
            services.GetRequiredService<
                WorkbenchViewModel>();
        WorkbenchTrackViewModel track =
            Track("output-layout.flac");
        model.Files.Add(track);
        model.SelectedFile = track;
        model.SetSelectedFiles([track]);
        MainWindow window =
            services.GetRequiredService<
                MainWindow>();
        try
        {
            WorkbenchView view =
                ShowWorkbench(
                    window,
                    services,
                    1440,
                    900);
            Carousel sections =
                view.FindControl<Carousel>(
                    "WorkbenchTabs")!;

            model.SelectedSection =
                WorkbenchSection.Reports;
            Render();
            WorkbenchReportsSectionView reports =
                Assert.IsType<
                    WorkbenchReportsSectionView>(
                    sections.SelectedItem);
            Grid reportLayout =
                reports.FindControl<Grid>(
                    "SectionLayout")!;
            Grid reportPanel =
                reports.FindControl<Grid>(
                    "ReviewedPanel")!;
            Assert.Single(
                reportLayout.ColumnDefinitions);
            Assert.Equal(
                2,
                Grid.GetRow(reportPanel));
            Assert.False(
                reports.FindControl<Border>(
                        "ReportPreviewEmptyState")!
                    .IsEffectivelyVisible);
            Assert.True(
                reports.FindControl<Border>(
                        "ReportsStickyFooter")!
                    .IsEffectivelyVisible);
            Assert.True(
                reports.FindControl<Button>(
                        "PreviewReportButton")!
                    .IsEffectivelyVisible);

            services.GetRequiredService<
                    ILocalizationService>()
                .SetCulture("de-DE");
            window.FontSize = 18;
            window.Width = 900;
            window.Height = 600;
            Render();
            Assert.True(
                reports.FindControl<ScrollViewer>(
                        "EditorScroll")!
                    .Bounds.Height >=
                140,
                $"The collapsed Reports preview left only {reports.FindControl<ScrollViewer>("EditorScroll")!.Bounds.Height:0.#} px for the editor.");
            string longDiagnostic =
                string.Join(
                    Environment.NewLine,
                    Enumerable.Range(1, 120)
                        .Select(index =>
                            $"Report failure detail {index:000}: the diagnostic remains available without displacing the workflow."));
            model.StatusDiagnosticDetail =
                longDiagnostic;
            Render();
            LocalizedFormatTextBlock
                reportDiagnostic =
                    reports.FindControl<
                        LocalizedFormatTextBlock>(
                        "ReportsStatusDiagnostic")!;
            Assert.True(
                reportDiagnostic
                    .IsEffectivelyVisible);
            Assert.Equal(
                3,
                reportDiagnostic.MaxLines);
            Assert.Equal(
                longDiagnostic,
                ToolTip.GetTip(
                    reportDiagnostic));
            Assert.Equal(
                longDiagnostic,
                global::Avalonia.Automation
                    .AutomationProperties
                    .GetHelpText(
                        reportDiagnostic));
            Assert.True(
                reports.FindControl<ScrollViewer>(
                        "EditorScroll")!
                    .Bounds.Height >=
                96,
                $"A long Reports diagnostic collapsed the editor to {reports.FindControl<ScrollViewer>("EditorScroll")!.Bounds.Height:0.#} px.");
            AssertControlInside(
                reports.FindControl<Border>(
                    "ReportsStatusBanner")!,
                reports,
                "Reports status");
            AssertControlInside(
                reports.FindControl<Border>(
                    "ReportsStickyFooter")!,
                reports,
                "Reports footer");

            services.GetRequiredService<
                    ILocalizationService>()
                .SetCulture("en-US");
            window.FontSize = 14;
            window.Width = 1440;
            window.Height = 900;
            Render();
            model.ReportOutputs.Add(
                new(
                    "All files",
                    "report.csv",
                    1,
                    64));
            Render();

            Assert.Equal(
                3,
                reportLayout.ColumnDefinitions
                    .Count);
            Assert.Equal(
                2,
                Grid.GetColumn(reportPanel));
            Assert.False(
                reports.FindControl<Border>(
                        "ReportPreviewEmptyState")!
                    .IsVisible);
            Assert.True(
                reports.FindControl<AppDataGrid>(
                        "ReportOutputGrid")!
                    .IsEffectivelyVisible);

            model.SelectedSection =
                WorkbenchSection.Playlists;
            Render();
            WorkbenchPlaylistsSectionView playlists =
                Assert.IsType<
                    WorkbenchPlaylistsSectionView>(
                    sections.SelectedItem);
            Grid playlistLayout =
                playlists.FindControl<Grid>(
                    "SectionLayout")!;
            Grid playlistPanel =
                playlists.FindControl<Grid>(
                    "ReviewedPanel")!;
            Assert.Single(
                playlistLayout.ColumnDefinitions);
            Assert.Equal(
                2,
                Grid.GetRow(playlistPanel));
            Assert.False(
                playlists.FindControl<Border>(
                        "PlaylistPreviewEmptyState")!
                    .IsEffectivelyVisible);
            Assert.True(
                playlists.FindControl<Border>(
                        "PlaylistsStickyFooter")!
                    .IsEffectivelyVisible);
            Assert.True(
                playlists.FindControl<Button>(
                        "PreviewPlaylistButton")!
                    .IsEffectivelyVisible);
            window.Width = 900;
            window.Height = 600;
            window.FontSize = 18;
            Render();
            LocalizedFormatTextBlock
                playlistDiagnostic =
                    playlists.FindControl<
                        LocalizedFormatTextBlock>(
                        "PlaylistsStatusDiagnostic")!;
            Assert.True(
                playlistDiagnostic
                    .IsEffectivelyVisible);
            Assert.Equal(
                3,
                playlistDiagnostic.MaxLines);
            Assert.Equal(
                longDiagnostic,
                ToolTip.GetTip(
                    playlistDiagnostic));
            Assert.Equal(
                longDiagnostic,
                global::Avalonia.Automation
                    .AutomationProperties
                    .GetHelpText(
                        playlistDiagnostic));
            Assert.True(
                playlists.FindControl<ScrollViewer>(
                        "EditorScroll")!
                    .Bounds.Height >=
                96,
                $"A long Playlists diagnostic collapsed the editor to {playlists.FindControl<ScrollViewer>("EditorScroll")!.Bounds.Height:0.#} px.");
            AssertControlInside(
                playlists.FindControl<Border>(
                    "PlaylistsStatusBanner")!,
                playlists,
                "Playlists status");
            AssertControlInside(
                playlists.FindControl<Border>(
                    "PlaylistsStickyFooter")!,
                playlists,
                "Playlists footer");
            window.Width = 1440;
            window.Height = 900;
            window.FontSize = 14;
            Render();

            model.PlaylistOutputs.Add(
                new(
                    "All files",
                    "playlist.m3u8",
                    1,
                    64));
            Render();

            Assert.Equal(
                3,
                playlistLayout.ColumnDefinitions
                    .Count);
            Assert.Equal(
                2,
                Grid.GetColumn(playlistPanel));
            Assert.False(
                playlists.FindControl<Border>(
                        "PlaylistPreviewEmptyState")!
                    .IsVisible);
            Assert.True(
                playlists.FindControl<AppDataGrid>(
                        "PlaylistOutputGrid")!
                    .IsEffectivelyVisible);
        }
        finally
        {
            window.Hide();
        }
    }

    private static void AssertControlInside(
        Control control,
        Control viewport,
        string description)
    {
        Point topLeft =
            control.TranslatePoint(
                default,
                viewport) ??
            throw new
                InvalidOperationException(
                    $"{description} is detached.");
        Assert.InRange(
            topLeft.X,
            -1,
            viewport.Bounds.Width + 1);
        Assert.InRange(
            topLeft.Y,
            -1,
            viewport.Bounds.Height + 1);
        Assert.InRange(
            topLeft.X +
            control.Bounds.Width,
            -1,
            viewport.Bounds.Width + 1);
        Assert.InRange(
            topLeft.Y +
            control.Bounds.Height,
            -1,
            viewport.Bounds.Height + 1);
    }

    [AvaloniaFact]
    public async Task Online_metadata_summaries_and_sticky_action_advance_only_after_completed_steps()
    {
        using ServiceProvider services =
            BuildServices();
        App.UseServicesForTests(services);
        WorkbenchViewModel model =
            services.GetRequiredService<
                WorkbenchViewModel>();
        WorkbenchTrackViewModel track =
            Track("online-step-contract.flac");
        model.Files.Add(track);
        model.SelectedFile = track;
        model.SetSelectedFiles([track]);
        MainWindow window =
            services.GetRequiredService<
                MainWindow>();
        try
        {
            WorkbenchView view =
                ShowWorkbench(
                    window,
                    services,
                    900,
                    600);
            model.SelectedSection =
                WorkbenchSection.OnlineMetadata;
            Render();
            Expander discovery =
                view.FindControl<Expander>(
                    "DiscoveryStep")!;
            Expander search =
                view.FindControl<Expander>(
                    "SearchStep")!;
            TextBlock discoverySummary =
                view.FindControl<TextBlock>(
                    "DiscoveryStepSummary")!;
            StackPanel searchSummary =
                view.FindControl<StackPanel>(
                    "SearchStepSummary")!;
            Border footer =
                view.FindControl<Border>(
                    "OnlineMetadataStickyFooter")!;

            Assert.True(
                discovery.IsExpanded,
                "Discovery was not the initial active step.");
            Assert.False(
                discoverySummary.IsVisible);
            discovery.IsExpanded = false;
            Render();
            Assert.False(
                discoverySummary.IsVisible);
            discovery.IsExpanded = true;
            Render();
            Assert.Single(
                footer
                    .GetVisualDescendants()
                    .OfType<Button>(),
                button =>
                    button.IsEffectivelyVisible &&
                    button.Classes.Contains(
                        "primary"));

            await model.DiscoverOnlineAudioCommand
                .ExecuteAsync(null);
            Render();
            Assert.True(
                model.HasCompletedOnlineDiscovery,
                "The completed discovery was not recorded.");
            Assert.False(
                discovery.IsExpanded,
                "Discovery did not collapse after completion.");
            Assert.True(
                discoverySummary.IsVisible,
                "The completed discovery summary was hidden.");
            Assert.True(
                view.FindControl<TabControl>(
                        "OnlineMetadataResultsTabs")!
                    .Bounds.Height > 100,
                "Results did not retain the remaining viewport.");

            search.IsExpanded = true;
            Render();
            Assert.False(
                searchSummary.IsVisible);
            search.IsExpanded = false;
            Render();
            Assert.False(
                searchSummary.IsVisible);
            model.ReleaseSearch.Artist =
                "Contract artist";
            search.IsExpanded = true;
            model.HasCompletedOnlineSearch =
                true;
            Render();

            Assert.True(
                model.HasCompletedOnlineSearch,
                "The completed search was not recorded.");
            Assert.False(
                search.IsExpanded,
                "Search did not collapse after completion.");
            Assert.True(
                searchSummary.IsVisible,
                "The completed search summary was hidden.");
            Assert.Single(
                footer
                    .GetVisualDescendants()
                    .OfType<Button>(),
                button =>
                    button.IsEffectivelyVisible &&
                    button.Classes.Contains(
                        "primary"));
        }
        finally
        {
            window.Hide();
        }
    }

    private static WorkbenchView ShowWorkbench(
        MainWindow window,
        IServiceProvider services,
        double width,
        double height)
    {
        window.Show();
        window.WindowState =
            WindowState.Normal;
        window.Width = width;
        window.Height = height;
        services
            .GetRequiredService<
                INavigationService>()
            .Navigate(
                ShellDestination.Workbench);
        Render();
        return Assert.IsType<WorkbenchView>(
            window.FindControl<ContentControl>(
                "ContentHost")!.Content);
    }

    private static WorkbenchTrackViewModel Track(
        string path)
    {
        string fullPath =
            Path.GetFullPath(path);
        return new(
            new MediaDocument(
                fullPath,
                [],
                [],
                null,
                new(
                    fullPath,
                    10,
                    DateTime.UtcNow,
                    "hash"),
                true));
    }

    private static ServiceProvider
        BuildServices(
            TestSettings? settings = null,
            bool pseudoExpanded = false,
            IExternalToolService?
                externalTools = null)
    {
        settings ??=
            new TestSettings();
        settings.SetPreference(
            AppearancePreferences
                .ShellRailExpandedPreference,
            bool.FalseString);
        var neutral =
            new ResourceLocalizationService(
                settings);
        ILocalizationService localization =
            pseudoExpanded
                ? new TestPseudoLocalizationService(
                    neutral)
                : neutral;
        return Composition.BuildServices(
            services =>
            {
                services.AddSingleton<
                    IAppSettings>(
                        settings);
                services.AddSingleton<
                    ILocalizationService>(
                    localization);
                if (externalTools is not null)
                {
                    services.AddSingleton<
                        IExternalToolService>(
                        externalTools);
                }
            });
    }

    private sealed class
        ManyInvocationExternalToolService :
        IExternalToolService
    {
        public const int InvocationCount = 24;

        public ExternalToolPlan Preview(
            ExternalToolDefinition definition,
            IReadOnlyList<string> paths) =>
            new(
                definition,
                Enumerable.Range(
                        1,
                        InvocationCount)
                    .Select(index =>
                        new ExternalToolInvocation(
                            definition.Executable,
                            [$"--fixture={index}"],
                            null,
                            paths))
                    .ToArray(),
                [],
                DateTimeOffset.UtcNow);

        public Task<ExternalToolRunResult>
            RunAsync(
                ExternalToolPlan plan,
                IProgress<OperationProgress>?
                    progress = null,
                CancellationToken ct =
                    default) =>
            Task.FromResult(
                new ExternalToolRunResult(
                    []));
    }

    private static void Render()
    {
        Avalonia.Threading.Dispatcher
            .UIThread.RunJobs();
        AvaloniaHeadlessPlatform
            .ForceRenderTimerTick(2);
        Avalonia.Threading.Dispatcher
            .UIThread.RunJobs();
    }

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
