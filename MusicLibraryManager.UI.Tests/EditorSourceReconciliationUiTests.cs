using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Views;
using MusicLibraryManager.Views.WorkbenchSections;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

public sealed class EditorSourceReconciliationUiTests
{
    [AvaloniaFact]
    public void Context_labels_refresh_without_changing_semantic_operation_or_column_ids()
    {
        var localization =
            new ContextLocalizationService();
        using ServiceProvider services =
            Composition.BuildServices(collection =>
                collection.AddSingleton<
                    ILocalizationService>(
                    localization));
        App.UseServicesForTests(services);

        var allFields =
            new WorkbenchAllFieldsSectionView();
        var library =
            new LibraryView();
        var session =
            new WorkbenchSessionSectionView();
        var fileOperation =
            new ReviewedFileOperationEditorView();
        var fileOperationModel =
            new ReviewedFileOperationEditorViewModel(
                services.GetRequiredService<
                    IReviewedFileOperationService>(),
                services.GetRequiredService<
                    IFilePickerService>(),
                () => [],
                _ => Task.FromResult(true),
                localization: localization)
            {
                SelectedKind =
                    ReviewedFileOperationKind
                        .Quarantine,
            };
        fileOperation.DataContext =
            fileOperationModel;
        LocalizedChoice<
            ReviewedFileOperationKind>
            quarantineChoice =
                fileOperationModel
                    .OperationKindChoices
                    .Single(choice =>
                        choice.Value ==
                        ReviewedFileOperationKind
                            .Quarantine);

        var host = new Grid();
        host.Children.Add(allFields);
        host.Children.Add(library);
        host.Children.Add(session);
        host.Children.Add(fileOperation);
        var window = new Window
        {
            Width = 1200,
            Height = 700,
            Content = host,
        };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(
                "en-US:Back",
                allFields.FindControl<Button>(
                    "AllFieldsBackButton")!
                    .Content);
            Assert.Equal(
                "en-US:Display label",
                library.FindControl<TextBlock>(
                    "LibraryColumnDisplayLabel")!
                    .Text);
            Assert.Equal(
                "en-US:Display label",
                AutomationProperties.GetName(
                    library.FindControl<TextBox>(
                        "LibraryColumnDisplayLabelEditor")!));
            Assert.Equal(
                "en-US:Condition group number",
                library.FindControl<TextBlock>(
                    "LibraryVisualFilterGroupNumberLabel")!
                    .Text);
            Assert.Equal(
                "en-US:Condition group number",
                AutomationProperties.GetName(
                    library.FindControl<NumericUpDown>(
                        "LibraryVisualFilterGroupNumberEditor")!));

            AppGridColumnDefinition definition =
                session.ColumnDefinitions.Single(
                    column =>
                        column.Key == "Format");
            Assert.Equal(
                "Column.Format",
                definition.HeaderResourceKey);
            DataGridColumn formatColumn =
                session.SessionGrid.Columns.Single(
                    column =>
                        session.SessionGrid.KeyFor(
                            column) == "Format");
            Assert.Equal(
                "en-US:Format",
                formatColumn.Header);
            Assert.Equal(
                "en-US:Quarantine folder",
                fileOperation.FindControl<TextBox>(
                    "ReviewedFileOperationDestination")!
                    .PlaceholderText);

            localization.SetCulture("fr-FR");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(
                "fr-FR:Back",
                allFields.FindControl<Button>(
                    "AllFieldsBackButton")!
                    .Content);
            Assert.Equal(
                "fr-FR:Display label",
                library.FindControl<TextBlock>(
                    "LibraryColumnDisplayLabel")!
                    .Text);
            Assert.Equal(
                "fr-FR:Display label",
                AutomationProperties.GetName(
                    library.FindControl<TextBox>(
                        "LibraryColumnDisplayLabelEditor")!));
            Assert.Equal(
                "fr-FR:Condition group number",
                library.FindControl<TextBlock>(
                    "LibraryVisualFilterGroupNumberLabel")!
                    .Text);
            Assert.Equal(
                "fr-FR:Condition group number",
                AutomationProperties.GetName(
                    library.FindControl<NumericUpDown>(
                        "LibraryVisualFilterGroupNumberEditor")!));
            Assert.Same(
                formatColumn,
                session.SessionGrid.Columns.Single(
                    column =>
                        session.SessionGrid.KeyFor(
                            column) == "Format"));
            Assert.Equal(
                "fr-FR:Format",
                formatColumn.Header);
            Assert.Equal(
                ReviewedFileOperationKind
                    .Quarantine,
                fileOperationModel.SelectedKind);
            Assert.Same(
                quarantineChoice,
                fileOperationModel
                    .OperationKindChoices
                    .Single(choice =>
                        choice.Value ==
                        ReviewedFileOperationKind
                            .Quarantine));
            Assert.Equal(
                "fr-FR:Quarantine folder",
                fileOperation.FindControl<TextBox>(
                    "ReviewedFileOperationDestination")!
                    .PlaceholderText);

            fileOperationModel.SelectedKind =
                ReviewedFileOperationKind.Move;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(
                "fr-FR:Destination folder",
                fileOperation.FindControl<TextBox>(
                    "ReviewedFileOperationDestination")!
                    .PlaceholderText);

            Border visualFilterSurface =
                library.FindControl<Border>(
                    "LibraryVisualFilterSurface")!;
            Grid visualFilterLayout =
                library.FindControl<Grid>(
                    "LibraryVisualFilterLayout")!;
            foreach (
                double surfaceWidth in
                new[] { 599d, 600d, 601d })
            {
                window.Width =
                    surfaceWidth + 24;
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(
                    surfaceWidth,
                    visualFilterSurface.Width);
                Assert.Equal(
                    surfaceWidth < 600 ? 1 : 2,
                    visualFilterLayout
                        .ColumnDefinitions.Count);
                Assert.Equal(
                    surfaceWidth < 600 ? 3 : 2,
                    visualFilterLayout
                        .RowDefinitions.Count);
            }

            localization.SetCulture("de-DE");
            window.Width = 580;
            Dispatcher.UIThread.RunJobs();
            library.FindControl<Button>(
                    "VisualFilterButton")!
                .RaiseEvent(
                    new RoutedEventArgs(
                        Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            TextBlock groupLabel =
                library.FindControl<TextBlock>(
                    "LibraryVisualFilterGroupNumberLabel")!;
            Assert.Equal(
                "Nummer der Bedingungsgruppe",
                groupLabel.Text);
            Assert.Single(
                visualFilterLayout
                    .ColumnDefinitions);
            Assert.Equal(
                3,
                visualFilterLayout
                    .RowDefinitions.Count);
            ScrollViewer conditionEditor =
                library.FindControl<ScrollViewer>(
                    "LibraryVisualFilterConditionEditorScroll")!;
            Assert.Equal(
                2,
                Grid.GetRow(
                    conditionEditor));
            Assert.Equal(
                0,
                Grid.GetColumn(
                    conditionEditor));
            Assert.True(
                groupLabel.Bounds.Width > 110);
            Assert.True(
                groupLabel.DesiredSize.Width <=
                groupLabel.Bounds.Width + 0.5);
            Assert.True(
                groupLabel.DesiredSize.Height <=
                groupLabel.Bounds.Height + 0.5);
        }
        finally
        {
            window.Hide();
        }
    }

    private sealed class ContextLocalizationService :
        ILocalizationService
    {
        private static readonly string[] ContextKeys =
        [
            "Common.Back",
            "Library.Columns.DisplayLabel",
            "Library.VisualFilter.GroupNumber",
            "Column.Format",
            "ReviewedFileOperation.DestinationFolderPlaceholder",
            "ReviewedFileOperation.QuarantineFolderPlaceholder",
        ];

        private CultureInfo _culture =
            CultureInfo.GetCultureInfo("en-US");

        public CultureInfo CurrentUICulture =>
            _culture;

        public IReadOnlyList<CultureInfo>
            SupportedCultures { get; } =
        [
            CultureInfo.GetCultureInfo("en-US"),
            CultureInfo.GetCultureInfo("fr-FR"),
            CultureInfo.GetCultureInfo("de-DE"),
        ];

        public event EventHandler? CultureChanged;

        public string Get(string key) =>
            key switch
            {
                "Common.Back" =>
                    $"{_culture.Name}:Back",
                "Library.Columns.DisplayLabel" =>
                    $"{_culture.Name}:Display label",
                "Library.VisualFilter.GroupNumber" =>
                    _culture.Name == "de-DE"
                        ? "Nummer der Bedingungsgruppe"
                        : $"{_culture.Name}:Condition group number",
                "Column.Format" =>
                    $"{_culture.Name}:Format",
                "ReviewedFileOperation.DestinationFolderPlaceholder" =>
                    $"{_culture.Name}:Destination folder",
                "ReviewedFileOperation.QuarantineFolderPlaceholder" =>
                    $"{_culture.Name}:Quarantine folder",
                _ => $"{_culture.Name}:{key}",
            };

        public string Format(
            string key,
            params object?[] arguments) =>
            $"{Get(key)}:{string.Join("|", arguments)}";

        public string FormatCount(
            string key,
            long count,
            params object?[] arguments) =>
            $"{Get(
                $"{key}.{(
                    count == 1
                        ? "One"
                        : "Other")}")}:{count}";

        public IReadOnlyDictionary<string, string>
            Snapshot() =>
            ContextKeys.ToDictionary(
                key => key,
                Get,
                StringComparer.Ordinal);

        public void SetCulture(
            string cultureName)
        {
            _culture =
                CultureInfo.GetCultureInfo(
                    cultureName);
            CultureChanged?.Invoke(
                this,
                EventArgs.Empty);
        }
    }
}
