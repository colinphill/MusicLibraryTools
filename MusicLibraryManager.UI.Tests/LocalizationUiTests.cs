using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
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

public sealed class LocalizationUiTests
{
    [Fact]
    public void Test_pseudo_locale_expands_visible_text_and_preserves_placeholders()
    {
        var inner =
            new MutableLocalizationService();
        var pseudo =
            new TestPseudoLocalizationService(
                inner);

        string neutral =
            inner.Get(
                LocalizationKeys
                    .DisplayLanguage);
        string expanded =
            pseudo.Get(
                LocalizationKeys
                    .DisplayLanguage);

        Assert.StartsWith(
            "\u27E6",
            expanded,
            StringComparison.Ordinal);
        Assert.EndsWith(
            "\u27E7",
            expanded,
            StringComparison.Ordinal);
        Assert.True(
            expanded.Length >=
            Math.Floor(
                neutral.Length * 1.4) +
            2);
        Assert.Contains(
            "{0:N0}",
            pseudo.Get(
                "Count.Files.Other"),
            StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Culture_change_updates_dynamic_resources_and_preserves_choice_value()
    {
        var settings = new TestSettings();
        var localization =
            new MutableLocalizationService();
        using ServiceProvider services =
            Composition.BuildServices(collection =>
            {
                collection.AddSingleton<IAppSettings>(
                    settings);
                collection.AddSingleton<
                    ILocalizationService>(
                    localization);
            });
        App.UseServicesForTests(services);
        var view = new SettingsView();
        var window = new Window
        {
            Content = view,
        };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            TextBlock label =
                view.FindControl<TextBlock>(
                    "DisplayLanguageLabel")!;
            ComboBox picker =
                view.FindControl<ComboBox>(
                    "DisplayLanguagePicker")!;
            var choice = Assert.IsType<
                LocalizedChoice<string>>(
                picker.SelectedItem);
            Assert.Equal(
                "Display language A",
                label.Text);
            Assert.Equal(
                "English (United States)",
                choice.Label);
            Assert.Equal("en-US", choice.Value);

            localization.UpdateLabels();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(
                "Display language B",
                label.Text);
            Assert.Equal(
                "English (United States)",
                choice.Label);
            Assert.Equal("en-US", choice.Value);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Localized_grid_headers_refresh_without_rebuilding_layout()
    {
        var settings = new TestSettings();
        var localization =
            new MutableLocalizationService();
        using ServiceProvider services =
            Composition.BuildServices(collection =>
            {
                collection.AddSingleton<IAppSettings>(
                    settings);
                collection.AddSingleton<ILocalizationService>(
                    localization);
            });
        App.UseServicesForTests(services);
        var grid = new AppDataGrid();
        grid.ConfigureColumns(
        [
            new(
                "Title",
                new LocalizedGridHeader(
                    "Title fallback",
                    "Grid.Header.Title"),
                "Title",
                180),
            new(
                "Artist",
                "Artist",
                "Artist",
                160),
        ]);
        var window = new Window
        {
            Content = grid,
        };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            grid.Columns[0].Width =
                new DataGridLength(245);
            grid.Columns[0].DisplayIndex = 1;

            Assert.Equal(
                "Localized title A",
                grid.Columns.Single(column =>
                    grid.KeyFor(column) == "Title").Header);

            localization.UpdateLabels();
            Dispatcher.UIThread.RunJobs();

            DataGridColumn title =
                grid.Columns.Single(column =>
                    grid.KeyFor(column) == "Title");
            Assert.Equal(
                "Localized title B",
                title.Header);
            Assert.Equal(1, title.DisplayIndex);
            Assert.Equal(
                245,
                title.Width.Value);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Workbench_tools_headers_use_preselected_culture_and_refresh_without_rebuilding_layout()
    {
        CultureInfo previousUICulture =
            CultureInfo.CurrentUICulture;
        var settings = new TestSettings();
        settings.SetPreference(
            LocalizationPreferences.DisplayLanguage,
            "en-US");
        var localization =
            new ResourceLocalizationService(settings);
        using ServiceProvider services =
            Composition.BuildServices(collection =>
            {
                collection.AddSingleton<IAppSettings>(
                    settings);
                collection.AddSingleton<
                    ILocalizationService>(
                    localization);
            });
        App.UseServicesForTests(services);
        localization.SetCulture("de-DE");
        Dispatcher.UIThread.RunJobs();
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            services.GetRequiredService<
                    INavigationService>()
                .Navigate(
                    ShellDestination.Workbench);
            Dispatcher.UIThread.RunJobs();

            WorkbenchView view =
                Assert.IsType<WorkbenchView>(
                    window.FindControl<
                            ContentControl>(
                            "ContentHost")!
                        .Content);
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            LibraryViewModel library =
                services.GetRequiredService<
                    LibraryViewModel>();
            model.SelectedSection =
                WorkbenchSection.Tools;
            Dispatcher.UIThread.RunJobs();
            AppDataGrid grid =
                view.FindControl<AppDataGrid>(
                    "ExternalToolInvocationGrid")!;
            DataGridColumn number =
                grid.Columns.Single(column =>
                    grid.KeyFor(column) == "Number");
            DataGridColumn executable =
                grid.Columns.Single(column =>
                    grid.KeyFor(column) ==
                    "Executable");
            DataGridColumn arguments =
                grid.Columns.Single(column =>
                    grid.KeyFor(column) ==
                    "Arguments");

            Assert.Equal(
                "#",
                number.Header);
            Assert.Equal(
                "Programmdatei",
                executable.Header);
            Assert.Equal(
                localization.Get(
                    "Workbench.Grid.Header.Arguments"),
                arguments.Header);
            Assert.Equal(
                localization.Get(
                    "Workbench.Reports.DefaultName"),
                library.ReportEditor.Name);
            Assert.Equal(
                localization.Get(
                    "Workbench.Playlists.DefaultName"),
                library.PlaylistEditor.Name);
            Assert.Equal(
                localization.Get(
                    "Workbench.Tools.DefaultName"),
                library.ExternalToolEditor.Name);

            arguments.DisplayIndex = 0;
            arguments.Width =
                new DataGridLength(333);
            int argumentsDisplayIndex =
                arguments.DisplayIndex;
            Assert.True(
                grid.ApplySort(
                    new LibrarySortState(
                        "Executable",
                        true)));
            Dispatcher.UIThread.RunJobs();

            localization.SetCulture("fr-FR");
            Dispatcher.UIThread.RunJobs();

            Assert.Same(
                number,
                grid.Columns.Single(column =>
                    grid.KeyFor(column) == "Number"));
            Assert.Same(
                executable,
                grid.Columns.Single(column =>
                    grid.KeyFor(column) ==
                    "Executable"));
            Assert.Same(
                arguments,
                grid.Columns.Single(column =>
                    grid.KeyFor(column) ==
                    "Arguments"));
            Assert.Equal(
                "#",
                number.Header);
            Assert.Equal(
                localization.Get(
                    "Workbench.Grid.Header.Executable"),
                executable.Header);
            Assert.Equal(
                localization.Get(
                    "Workbench.Grid.Header.Arguments"),
                arguments.Header);
            Assert.Equal(
                argumentsDisplayIndex,
                arguments.DisplayIndex);
            Assert.Equal(
                333,
                arguments.Width.Value);
            Assert.Equal(
                "Executable",
                grid.CurrentSortKey);
            Assert.True(
                grid.CurrentSortDescending);

            Assert.Equal(
                localization.Get(
                    "Workbench.Reports.DefaultName"),
                library.ReportEditor.Name);
            Assert.Equal(
                localization.Get(
                    "Workbench.Playlists.DefaultName"),
                library.PlaylistEditor.Name);
            Assert.Equal(
                localization.Get(
                    "Workbench.Tools.DefaultName"),
                library.ExternalToolEditor.Name);
            library.ReportEditor.Name =
                "Library report";
            library.PlaylistEditor.Name =
                "Library playlist";
            library.ExternalToolEditor.Name =
                "Library tool";

            localization.SetCulture("ja-JP");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(
                "Library report",
                library.ReportEditor.Name);
            Assert.Equal(
                "Library playlist",
                library.PlaylistEditor.Name);
            Assert.Equal(
                "Library tool",
                library.ExternalToolEditor.Name);
            Assert.Same(
                executable,
                grid.Columns.Single(column =>
                    grid.KeyFor(column) ==
                    "Executable"));
            Assert.Equal(
                localization.Get(
                    "Workbench.Grid.Header.Executable"),
                executable.Header);
            Assert.Equal(
                argumentsDisplayIndex,
                arguments.DisplayIndex);
            Assert.Equal(
                333,
                arguments.Width.Value);
            Assert.Equal(
                "Executable",
                grid.CurrentSortKey);
            Assert.True(
                grid.CurrentSortDescending);
        }
        finally
        {
            window.Hide();
            CultureInfo.CurrentUICulture =
                previousUICulture;
        }
    }

    [AvaloniaFact]
    public void Missing_localized_grid_header_is_visibly_marked()
    {
        var grid = new AppDataGrid();
        grid.ConfigureColumns(
        [
            new(
                "Missing",
                "Silent fallback",
                "Missing",
                120,
                HeaderResourceKey:
                    "Grid.Header.TrulyMissing"),
        ]);

        Assert.Equal(
            "\u27E6Grid.Header.TrulyMissing\u27E7",
            grid.Columns[0].Header);
    }

    [AvaloniaFact]
    public void Localized_count_text_refreshes_for_value_and_culture_changes()
    {
        var localization =
            new MutableLocalizationService();
        using ServiceProvider services =
            Composition.BuildServices(collection =>
                collection.AddSingleton<
                    ILocalizationService>(
                    localization));
        App.UseServicesForTests(services);
        var text = new LocalizedFormatTextBlock
        {
            ResourceKey = "Count.Files",
            UseCountVariant = true,
            Value = 1,
        };
        var window = new Window
        {
            Content = text,
        };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("1 file A", text.Text);

            text.Value = 2;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("2 files A", text.Text);

            localization.UpdateLabels();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("2 files B", text.Text);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Workbench_culture_change_preserves_semantic_and_layout_state()
    {
        var settings = new TestSettings();
        var localization =
            new MutableLocalizationService();
        using ServiceProvider services =
            Composition.BuildServices(collection =>
            {
                collection.AddSingleton<IAppSettings>(
                    settings);
                collection.AddSingleton<
                    ILocalizationService>(
                    localization);
            });
        App.UseServicesForTests(services);
        ThemeVariant? previousTheme =
            Application.Current!.RequestedThemeVariant;
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            Application.Current.RequestedThemeVariant =
                ThemeVariant.Dark;
            window.Show();
            window.Width = 1200;
            window.Height = 700;
            services.GetRequiredService<
                    INavigationService>()
                .Navigate(
                    ShellDestination.Workbench);
            Dispatcher.UIThread.RunJobs();

            WorkbenchView view =
                Assert.IsType<WorkbenchView>(
                    window.FindControl<ContentControl>(
                        "ContentHost")!.Content);
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            model.SelectedSection =
                WorkbenchSection.BulkOperation;
            model.IsInspectorOpen = false;

            var track = new WorkbenchTrackViewModel(
                new MediaDocument(
                    "culture-state.flac",
                    [new(
                        "VorbisComment",
                        [new(
                            MetadataFieldKey.Known(
                                TagFields.Title),
                            ["A title"])],
                        true,
                        true,
                        true,
                        true)],
                    [],
                    null,
                    new(
                        "culture-state.flac",
                        10,
                        DateTime.UtcNow,
                        "hash"),
                    true));
            model.Files.Add(track);
            model.SetSelectedFiles([track]);

            model.OperationEditor.SelectedOperation =
                model.OperationEditor
                    .OperationDescriptors
                    .Single(descriptor =>
                        descriptor.Kind ==
                        MetadataOperationKind
                            .ReplaceText);
            model.OperationEditor.SearchText = "title";
            model.OperationEditor.ReplacementText =
                "name";
            model.OperationEditor
                .AddCurrentOperationCommand
                .Execute(null);
            MetadataRecipeStepViewModel recipeStep =
                Assert.Single(
                    model.OperationEditor.Steps);
            Guid recipeStepId = recipeStep.Id;

            AppDataGrid grid =
                view.FindControl<AppDataGrid>(
                    "WorkbenchGrid")!;
            DataGridColumn album =
                grid.Columns.Single(column =>
                    grid.KeyFor(column) == "Album");
            album.DisplayIndex = 0;
            album.Width = new DataGridLength(287);
            Assert.True(
                grid.ApplySort(
                    new LibrarySortState(
                        "Album",
                        true)));
            Dispatcher.UIThread.RunJobs();
            Assert.True(
                grid.CurrentSortDescending,
                "The descending sort must be established before changing culture.");

            Assert.Equal(
                "Reports A",
                model.SectionOptions.Single(option =>
                    option.Section ==
                    WorkbenchSection.Reports).Label);

            localization.UpdateLabels();
            Dispatcher.UIThread.RunJobs();

            Assert.Same(
                view,
                window.FindControl<ContentControl>(
                    "ContentHost")!.Content);
            Assert.Equal(
                WorkbenchSection.BulkOperation,
                model.SelectedSection);
            Assert.False(model.IsInspectorOpen);
            Assert.Equal(
                ThemeVariant.Dark,
                Application.Current
                    .RequestedThemeVariant);
            Assert.Same(
                track,
                Assert.Single(model.SelectedFiles));
            MetadataRecipeStepViewModel refreshedStep =
                Assert.Single(
                    model.OperationEditor.Steps);
            Assert.Equal(
                recipeStepId,
                refreshedStep.Id);
            ReplaceTextOperation operation =
                Assert.IsType<ReplaceTextOperation>(
                    refreshedStep.Operation);
            Assert.Equal("title", operation.Search);
            Assert.Equal("name", operation.Replacement);
            Assert.Equal(
                "Reports B",
                model.SectionOptions.Single(option =>
                    option.Section ==
                    WorkbenchSection.Reports).Label);

            DataGridColumn refreshedAlbum =
                grid.Columns.Single(column =>
                    grid.KeyFor(column) == "Album");
            Assert.Same(album, refreshedAlbum);
            Assert.Equal(0, refreshedAlbum.DisplayIndex);
            Assert.Equal(287, refreshedAlbum.Width.Value);
            Assert.Equal("Album", grid.CurrentSortKey);
            Assert.True(grid.CurrentSortDescending);
        }
        finally
        {
            window.Hide();
            Application.Current.RequestedThemeVariant =
                previousTheme;
        }
    }

    [AvaloniaFact]
    public void Every_shipping_locale_preserves_workbench_navigation_selection_recipe_theme_and_grid_layout()
    {
        CultureInfo previousUICulture =
            CultureInfo.CurrentUICulture;
        CultureInfo previousCulture =
            CultureInfo.CurrentCulture;
        var settings = new TestSettings();
        settings.SetPreference(
            LocalizationPreferences.DisplayLanguage,
            "en-US");
        var localization =
            new ResourceLocalizationService(settings);
        using ServiceProvider services =
            Composition.BuildServices(collection =>
            {
                collection.AddSingleton<IAppSettings>(
                    settings);
                collection.AddSingleton<
                    ILocalizationService>(
                    localization);
            });
        App.UseServicesForTests(services);
        ThemeVariant? previousTheme =
            Application.Current!.RequestedThemeVariant;
        MainWindow window =
            services.GetRequiredService<MainWindow>();
        try
        {
            Application.Current.RequestedThemeVariant =
                ThemeVariant.Dark;
            window.Show();
            window.Width = 1200;
            window.Height = 700;
            INavigationService navigation =
                services.GetRequiredService<
                    INavigationService>();
            navigation.Navigate(
                ShellDestination.Workbench);
            Dispatcher.UIThread.RunJobs();

            WorkbenchView view =
                Assert.IsType<WorkbenchView>(
                    window.FindControl<ContentControl>(
                        "ContentHost")!.Content);
            WorkbenchViewModel model =
                services.GetRequiredService<
                    WorkbenchViewModel>();
            model.SelectedSection =
                WorkbenchSection.BulkOperation;
            model.IsInspectorOpen = false;

            var track = new WorkbenchTrackViewModel(
                new MediaDocument(
                    "all-locale-state.flac",
                    [new(
                        "VorbisComment",
                        [new(
                            MetadataFieldKey.Known(
                                TagFields.Title),
                            ["A title"])],
                        true,
                        true,
                        true,
                        true)],
                    [],
                    null,
                    new(
                        "all-locale-state.flac",
                        10,
                        DateTime.UtcNow,
                        "hash"),
                    true));
            model.Files.Add(track);
            model.SetSelectedFiles([track]);

            model.OperationEditor.SelectedOperation =
                model.OperationEditor
                    .OperationDescriptors
                    .Single(descriptor =>
                        descriptor.Kind ==
                        MetadataOperationKind
                            .ReplaceText);
            model.OperationEditor.SearchText = "title";
            model.OperationEditor.ReplacementText =
                "name";
            model.OperationEditor
                .AddCurrentOperationCommand
                .Execute(null);
            MetadataRecipeStepViewModel recipeStep =
                Assert.Single(
                    model.OperationEditor.Steps);
            Guid recipeStepId = recipeStep.Id;

            AppDataGrid grid =
                view.FindControl<AppDataGrid>(
                    "WorkbenchGrid")!;
            DataGridColumn album =
                grid.Columns.Single(column =>
                    grid.KeyFor(column) == "Album");
            album.DisplayIndex = 0;
            album.Width = new DataGridLength(287);
            Assert.True(
                grid.ApplySort(
                    new LibrarySortState(
                        "Album",
                        true)));
            Dispatcher.UIThread.RunJobs();

            foreach (
                LocalizationCultureDescriptor locale in
                LocalizationCultureRegistry
                    .ShippingLocales)
            {
                localization.SetCulture(locale.Name);
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(
                    locale.Name,
                    localization
                        .CurrentUICulture.Name);
                Assert.Equal(
                    locale.Name,
                    CultureInfo
                        .CurrentUICulture.Name);
                Assert.Equal(
                    previousCulture,
                    CultureInfo.CurrentCulture);
                Assert.Equal(
                    ShellDestination.Workbench,
                    navigation.Current);
                Assert.Same(
                    view,
                    window.FindControl<
                            ContentControl>(
                            "ContentHost")!
                        .Content);
                Assert.Equal(
                    WorkbenchSection.BulkOperation,
                    model.SelectedSection);
                Assert.False(model.IsInspectorOpen);
                Assert.Equal(
                    ThemeVariant.Dark,
                    Application.Current
                        .RequestedThemeVariant);
                Assert.Same(
                    track,
                    Assert.Single(
                        model.SelectedFiles));

                MetadataRecipeStepViewModel
                    localizedStep =
                        Assert.Single(
                            model.OperationEditor
                                .Steps);
                Assert.Equal(
                    recipeStepId,
                    localizedStep.Id);
                ReplaceTextOperation operation =
                    Assert.IsType<
                        ReplaceTextOperation>(
                        localizedStep.Operation);
                Assert.Equal(
                    "title",
                    operation.Search);
                Assert.Equal(
                    "name",
                    operation.Replacement);
                Assert.Equal(
                    localization.Get(
                        "Workbench.Navigation.Section.Reports"),
                    model.SectionOptions.Single(
                        option =>
                            option.Section ==
                            WorkbenchSection
                                .Reports)
                        .Label);

                DataGridColumn localizedAlbum =
                    grid.Columns.Single(column =>
                        grid.KeyFor(column) ==
                        "Album");
                Assert.Same(
                    album,
                    localizedAlbum);
                Assert.Equal(
                    0,
                    localizedAlbum.DisplayIndex);
                Assert.Equal(
                    287,
                    localizedAlbum.Width.Value);
                Assert.Equal(
                    "Album",
                    grid.CurrentSortKey);
                Assert.True(
                    grid.CurrentSortDescending);
            }
        }
        finally
        {
            window.Hide();
            Application.Current.RequestedThemeVariant =
                previousTheme;
            CultureInfo.CurrentUICulture =
                previousUICulture;
        }
    }

    private sealed class MutableLocalizationService :
        ILocalizationService
    {
        private readonly CultureInfo[] _cultures =
            [CultureInfo.GetCultureInfo("en-US")];
        private readonly Dictionary<string, string>
            _values = new()
            {
                [LocalizationKeys.DisplayLanguage] =
                    "Display language A",
                [LocalizationKeys
                    .DisplayLanguageDescription] =
                    "Description A",
                [LocalizationKeys.CultureName(
                    "en-US")] =
                    "English A",
                ["Grid.Header.Title"] =
                    "Localized title A",
                ["Count.Files.One"] =
                    "{0:N0} file A",
                ["Count.Files.Other"] =
                    "{0:N0} files A",
                ["Workbench.Navigation.Group.Workspace"] =
                    "Workspace A",
                ["Workbench.Navigation.Section.Reports"] =
                    "Reports A",
            };

        public CultureInfo CurrentUICulture =>
            _cultures[0];

        public IReadOnlyList<CultureInfo>
            SupportedCultures => _cultures;

        public event EventHandler? CultureChanged;

        public string Get(string key) =>
            _values.TryGetValue(
                key,
                out string? value)
                ? value
                : $"\u27E6{key}\u27E7";

        public string Format(
            string key,
            params object?[] arguments) =>
            string.Format(
                CultureInfo.CurrentCulture,
                Get(key),
                arguments);

        public string FormatCount(
            string key,
            long count,
            params object?[] arguments)
        {
            object?[] formatArguments =
                [count, .. arguments];
            return Format(
                CardinalPluralResolver.ResourceKey(
                    key,
                    count,
                    CurrentUICulture),
                formatArguments);
        }

        public IReadOnlyDictionary<string, string>
            Snapshot() =>
            new Dictionary<string, string>(_values);

        public void SetCulture(string cultureName)
        {
        }

        public void UpdateLabels()
        {
            _values[
                LocalizationKeys.DisplayLanguage] =
                "Display language B";
            _values[
                LocalizationKeys.CultureName(
                    "en-US")] =
                "English B";
            _values["Grid.Header.Title"] =
                "Localized title B";
            _values["Count.Files.One"] =
                "{0:N0} file B";
            _values["Count.Files.Other"] =
                "{0:N0} files B";
            _values[
                "Workbench.Navigation.Group.Workspace"] =
                "Workspace B";
            _values[
                "Workbench.Navigation.Section.Reports"] =
                "Reports B";
            CultureChanged?.Invoke(
                this,
                EventArgs.Empty);
        }
    }

    private sealed class TestSettings : IAppSettings
    {
        private readonly Dictionary<string, string>
            _preferences = [];

        public string? ConfigPath => null;
        public LibraryConfiguration? Configuration =>
            null;
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
            GetRememberedConfigPath() => null;

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
