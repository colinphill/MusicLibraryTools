using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MetadataCaching;
using Microsoft.Extensions.DependencyInjection;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicFileUtilities;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Views;
using MusicLibraryManager.Controls;
using MusicLibraryManager.Services;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

public sealed class UiControlTests
{
    [AvaloniaFact]
    public void Top_search_focus_is_drawn_by_the_full_composite_control()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            window.Activate();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            Border chrome = window.FindControl<Border>("SearchChrome")!;
            TextBox search = window.FindControl<TextBox>("SearchBox")!;
            search.Focus();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new Thickness(2), chrome.BorderThickness);
            Border inputBorder = search.GetVisualDescendants().OfType<Border>()
                .Single(border => border.Name == "PART_BorderElement");
            Assert.Equal(Colors.Transparent,
                Assert.IsAssignableFrom<ISolidColorBrush>(inputBorder.Background).Color);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Settings_exposes_profile_summary_and_root_write_permissions()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            services.GetRequiredService<INavigationService>().Navigate(ShellDestination.Settings);
            Dispatcher.UIThread.RunJobs();

            SettingsView settings = Assert.IsType<SettingsView>(
                window.FindControl<ContentControl>("ContentHost")!.Content);
            SettingsViewModel viewModel = Assert.IsType<SettingsViewModel>(settings.DataContext);
            await viewModel.NewConfigurationCommand.ExecuteAsync(null);
            TabControl tabs = settings.FindControl<TabControl>("SettingsTabs")!;
            tabs.SelectedIndex = 5;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            ComboBox profilePicker = settings.FindControl<ComboBox>("ProfilePresetPicker")!;
            Assert.Equal(LibraryProfilePresets.CatalogOnlyId,
                Assert.IsType<LibraryProfile>(profilePicker.SelectedItem).Id);
            Assert.Contains("catalog only (read-only)",
                settings.FindControl<TextBlock>("EffectivePolicySummaryText")!.Text!,
                StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(viewModel.AdvancedProfile);
            Assert.Contains(tabs.Items.OfType<TabItem>(),
                item => Equals(item.Header, "Root/Naming policy"));
            Assert.Contains(tabs.Items.OfType<TabItem>(),
                item => Equals(item.Header, "Ingest policy"));
            Assert.Contains(settings.GetVisualDescendants().OfType<Button>(),
                button => Equals(button.Content, "New"));
            Assert.Contains(settings.GetVisualDescendants().OfType<Button>(),
                button => Equals(button.Content, "Duplicate"));
            Assert.Contains(settings.GetVisualDescendants().OfType<Button>(),
                button => Equals(button.Content, "Delete"));
            tabs.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Cross-set comparison extensions");
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Formats to index");
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Include patterns");
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Exclude patterns");
            Assert.DoesNotContain(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text is "Ingest role" or "Representation role");
            Assert.DoesNotContain(settings.GetVisualDescendants().OfType<CheckBox>(),
                checkBox => checkBox.Content as string is
                    "iTunes canonical naming" or "Sync target");
            string[] permissionLabels =
                ["Metadata", "Artwork", "Organize files", "Ingest output", "Sync output"];
            CheckBox[] permissions = settings.GetVisualDescendants().OfType<CheckBox>()
                .Where(checkBox => permissionLabels.Contains(checkBox.Content as string))
                .ToArray();
            Assert.Equal(permissionLabels.Length, permissions.Length);
            Assert.All(permissions, checkBox => Assert.False(checkBox.IsChecked));

            Assert.True(viewModel.StartGuidedSetupCommand.CanExecute(null));
            viewModel.AddPlaylistSourceCommand.Execute(null);
            viewModel.AddPlaylistTargetCommand.Execute(null);
            viewModel.AddExportProfileCommand.Execute(null);
            settings.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "File playlist sources");
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Export profiles");
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Cross-library sync target");

            settings.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 3;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "WavPack");
            Assert.Equal("wavpack", viewModel.WavpackPath);
            Assert.DoesNotContain(settings.GetVisualDescendants().OfType<TextBlock>(), text =>
                text.Text is "Path length limit" or "Disc number length limit" or
                    "AAC encoder" or "AAC bitrate (kbps)");

            settings.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 5;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Metadata fidelity");
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Profile name");
            Assert.Contains(settings.GetVisualDescendants().OfType<CheckBox>(),
                checkBox => Equals(checkBox.Content, "iTunes canonical naming"));
            Assert.DoesNotContain(settings.GetVisualDescendants().OfType<CheckBox>(),
                checkBox => Equals(checkBox.Content, "Preserve disc tags"));
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(), text =>
                text.Text?.StartsWith("The disc strategy is the destination representation",
                    StringComparison.Ordinal) == true);
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Component limit (legacy LengthLimit)");
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Disc-album limit (legacy DiscNumLengthLimit)");
            settings.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 6;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(settings.GetVisualDescendants().OfType<Button>(),
                button => Equals(button.Content, "Add recipe"));
            Assert.DoesNotContain(settings.GetVisualDescendants().OfType<TextBlock>(), text =>
                text.Text is "Legacy destination role" or "Output representation");
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Settings_tab_switching_preserves_required_choice_fields()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            services.GetRequiredService<INavigationService>().Navigate(ShellDestination.Settings);
            Dispatcher.UIThread.RunJobs();

            SettingsView settings = Assert.IsType<SettingsView>(
                window.FindControl<ContentControl>("ContentHost")!.Content);
            SettingsViewModel viewModel = Assert.IsType<SettingsViewModel>(settings.DataContext);
            await viewModel.NewConfigurationCommand.ExecuteAsync(null);

            IndexTargetEditorRow root = Assert.Single(viewModel.IndexTargets);
            root.Path = @"C:\Music";
            root.AllowIngestOutput = true;

            viewModel.AddPlaylistSourceCommand.Execute(null);
            PlaylistSourceEditorRow source = Assert.Single(viewModel.PlaylistSources);
            source.Location = @"C:\Playlists";

            viewModel.AddPlaylistTargetCommand.Execute(null);
            PlaylistTargetEditorRow target = Assert.Single(viewModel.PlaylistTargets);
            target.Target = @"C:\Playlist Exports";
            target.Type = "wpl";
            target.PathStyle = "relative";
            target.Encoding = "utf-16";
            target.LineEnding = "lf";
            target.FileNameTransform = "sanitize";
            target.CollisionPolicy = LibraryPathCollisionPolicy.Hash;

            viewModel.AddExportProfileCommand.Execute(null);
            ExportProfileEditorRow export = Assert.Single(viewModel.ExportProfiles);
            export.SelectionKind = ExportSelectionKind.Playlists;
            export.TransformMode = ExportTransformMode.Remux;
            export.CollisionPolicy = LibraryPathCollisionPolicy.Suffix;
            export.ArtworkMode = ExportArtworkMode.Sidecar;
            export.PlaylistFormat = "m3u";
            export.PlaylistEncoding = "utf-16";
            export.PlaylistLineEnding = "lf";
            export.ExtraFileDisposition = ExportExtraFileDisposition.Quarantine;

            LibraryProfileEditorRow advanced = Assert.IsType<LibraryProfileEditorRow>(
                viewModel.AdvancedProfile);
            advanced.UnicodeNormalization = LibraryUnicodeNormalization.FormKD;
            advanced.DiscStrategy = LibraryDiscStrategy.DiscFolder;
            advanced.TrackTotalScope = LibraryTrackTotalScope.Album;
            IngestProfileEditorRow advancedIngest = Assert.IsType<IngestProfileEditorRow>(
                viewModel.AdvancedIngestProfile);
            advancedIngest.SourceDisposition = LibrarySourceDisposition.Quarantine;
            advanced.ArtworkStorage = LibraryArtworkStorage.Both;
            advanced.ArtworkRoles = LibraryArtworkRoleSelection.AllRoles;
            advanced.ArtworkEncoding = LibraryArtworkEncoding.Png;
            advanced.UnknownSidecarDisposition = LibrarySidecarDisposition.Quarantine;

            viewModel.AddIngestRecipeCommand.Execute(null);
            IngestRecipeEditorRow recipe = Assert.Single(advancedIngest.Recipes);
            recipe.Action = LibraryIngestAction.Transcode;
            recipe.AddToMediaCatalog = true;
            recipe.CollisionPolicy = LibraryPathCollisionPolicy.Suffix;
            recipe.InputChannelChoice = SettingsChoiceLists.ChannelChoice(
                LibraryChannelSelection.Multi);
            recipe.OutputChannelChoice = SettingsChoiceLists.ChannelChoice(
                LibraryChannelSelection.Stereo);
            recipe.AlbumCondition = LibraryIngestAlbumCondition.HasHighResolution;
            recipe.SourceSelection = LibraryIngestSourceSelection.PreferCdQuality;
            recipe.RequireFallbackApproval = true;
            recipe.ExtraFfmpegOptions = "-af \"volume=0.9\"";
            recipe.DestinationRootChoice = Assert.Single(
                recipe.DestinationRootChoices, choice => choice.Id == root.Id);

            viewModel.AddSidecarRuleCommand.Execute(null);
            SidecarRuleEditorRow sidecar = advanced.SidecarRules[^1];
            sidecar.Disposition = LibrarySidecarDisposition.Quarantine;
            HealthRuleEditorRow health = advanced.HealthRules[0];
            health.Severity = LibraryHealthSeverity.Error;

            Dispatcher.UIThread.RunJobs();
            string? validationBefore = viewModel.ValidationSummary;
            TabControl tabs = settings.FindControl<TabControl>("SettingsTabs")!;

            ActivateTab(tabs, 1);
            AssertVisibleChoicesSelected(settings);
            ActivateTab(tabs, 2);
            AssertVisibleChoicesSelected(settings);
            ActivateTab(tabs, 5);
            AssertVisibleChoicesSelected(settings);
            ActivateTab(tabs, 6);
            AssertVisibleChoicesSelected(settings);
            ActivateTab(tabs, 7);
            ActivateTab(tabs, 2);
            AssertVisibleChoicesSelected(settings);

            Assert.Equal("m3u", source.Type);
            Assert.Equal("wpl", target.Type);
            Assert.Equal("relative", target.PathStyle);
            Assert.Equal("utf-16", target.Encoding);
            Assert.Equal("lf", target.LineEnding);
            Assert.Equal("sanitize", target.FileNameTransform);
            Assert.Equal(ExportTransformMode.Remux, export.TransformMode);
            Assert.Equal("m3u", export.PlaylistFormat);
            Assert.Equal("utf-16", export.PlaylistEncoding);
            Assert.Equal("lf", export.PlaylistLineEnding);

            ActivateTab(tabs, 5);
            AssertVisibleChoicesSelected(settings);
            Assert.Same(advanced, viewModel.AdvancedProfile);
            Assert.Equal(LibraryUnicodeNormalization.FormKD, advanced.UnicodeNormalization);
            Assert.Equal(LibraryDiscStrategy.DiscFolder, advanced.DiscStrategy);
            Assert.Equal(LibraryTrackTotalScope.Album, advanced.TrackTotalScope);
            Assert.Equal(LibrarySourceDisposition.Quarantine,
                advancedIngest.SourceDisposition);
            ActivateTab(tabs, 6);
            AssertVisibleChoicesSelected(settings);
            Assert.Equal(LibraryIngestAction.Transcode, recipe.Action);
            Assert.Equal(["Stereo", "Multi"],
                SettingsChoiceLists.ChannelChoices.Select(choice => choice.Label));
            Assert.Equal(LibraryChannelSelection.Multi, recipe.InputChannelChoice.Value);
            Assert.Equal(LibraryChannelSelection.Stereo, recipe.OutputChannelChoice.Value);
            Assert.Equal(LibraryIngestAlbumCondition.HasHighResolution,
                recipe.AlbumCondition);
            Assert.Equal(LibraryIngestSourceSelection.PreferCdQuality,
                recipe.SourceSelection);
            Assert.True(recipe.RequireFallbackApproval);
            Assert.Equal("-af \"volume=0.9\"", recipe.ExtraFfmpegOptions);
            Assert.True(recipe.AddToMediaCatalog);
            Assert.Contains(settings.GetVisualDescendants().OfType<TextBox>(), textBox =>
                AutomationProperties.GetName(textBox) == "Extra FFmpeg options");
            Assert.Equal(root.Id, recipe.DestinationRootId);
            Assert.Equal(@"C:\Music", recipe.DestinationRootChoice?.Label);
            ComboBox destinationRootPicker = Assert.Single(
                settings.GetVisualDescendants().OfType<ComboBox>(), combo =>
                    ReferenceEquals(combo.ItemsSource, recipe.DestinationRootChoices));
            Assert.Equal(recipe.DestinationRootChoice,
                destinationRootPicker.SelectedItem);
            Assert.Equal(LibraryHealthSeverity.Error, health.Severity);
            Assert.Equal(LibrarySidecarDisposition.Quarantine, sidecar.Disposition);

            ActivateTab(tabs, 1);
            AssertVisibleChoicesSelected(settings);
            Assert.Equal(validationBefore, viewModel.ValidationSummary);
        }
        finally
        {
            window.Hide();
        }

        static void ActivateTab(TabControl tabs, int index)
        {
            tabs.SelectedIndex = index;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            Dispatcher.UIThread.RunJobs();
        }

        static void AssertVisibleChoicesSelected(SettingsView settings)
        {
            ComboBox[] choices = settings.GetVisualDescendants().OfType<ComboBox>().ToArray();
            Assert.NotEmpty(choices);
            Assert.All(choices, choice => Assert.NotNull(choice.ItemsSource));
            Assert.All(choices, choice => Assert.NotNull(choice.SelectedItem));
        }
    }

    [AvaloniaFact]
    public async Task Settings_can_duplicate_both_policy_types_while_pickers_are_bound()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            services.GetRequiredService<INavigationService>().Navigate(
                ShellDestination.Settings);
            Dispatcher.UIThread.RunJobs();

            SettingsView settings = Assert.IsType<SettingsView>(
                window.FindControl<ContentControl>("ContentHost")!.Content);
            SettingsViewModel viewModel = Assert.IsType<SettingsViewModel>(
                settings.DataContext);
            await viewModel.NewConfigurationCommand.ExecuteAsync(null);
            TabControl tabs = settings.FindControl<TabControl>("SettingsTabs")!;

            tabs.SelectedIndex = 5;
            Dispatcher.UIThread.RunJobs();
            int namingCount = viewModel.LibraryProfiles.Count;
            viewModel.DuplicateLibraryProfileCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(namingCount + 1, viewModel.LibraryProfiles.Count);
            Assert.Equal(LibraryProfilePreset.Custom,
                viewModel.SelectedLibraryProfile?.Preset);

            tabs.SelectedIndex = 6;
            Dispatcher.UIThread.RunJobs();
            int ingestCount = viewModel.IngestProfiles.Count;
            viewModel.DuplicateIngestProfileCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(ingestCount + 1, viewModel.IngestProfiles.Count);
            Assert.StartsWith("ingest-", viewModel.SelectedIngestProfile?.Id);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Data_grid_preserves_app_metrics_and_column_contract()
    {
        var grid = new AppDataGrid();
        grid.ConfigureColumns([
            new AppGridColumnDefinition("Title", "Title", "Title", 280, 140),
            new AppGridColumnDefinition("Hidden", "Hidden", "Hidden", 120, 80, false),
            new AppGridColumnDefinition("Path", "Path", "Path", 420, 180),
        ]);

        Assert.Equal(38, grid.RowHeight);
        Assert.Equal(39, grid.ColumnHeaderHeight);
        Assert.Equal(DataGridSelectionMode.Extended, grid.SelectionMode);
        Assert.Equal(2, grid.Columns.Count);
        Assert.Equal("Title", grid.KeyFor(grid.Columns[0]));
        Assert.Equal(280, grid.CaptureColumnLayout()[0].Width);
    }

    [AvaloniaFact]
    public void Data_grid_applies_a_typed_persisted_sort_state()
    {
        LibraryRow[] rows =
        [
            new LibraryRow(new TrackRecord { Path = @"C:\b.flac", Title = "B" }),
            new LibraryRow(new TrackRecord { Path = @"C:\a.flac", Title = "A" }),
        ];
        var view = new global::Avalonia.Collections.DataGridCollectionView(rows);
        var grid = new AppDataGrid
        {
            ItemsSource = view,
        };
        grid.ConfigureColumns([
            new AppGridColumnDefinition("Title", "Title", "Title", 280, 140),
        ]);

        Assert.True(grid.ApplySort(new LibrarySortState("Title", true)));
        Assert.Equal("Title", grid.CurrentSortKey);
        Assert.True(grid.CurrentSortDescending);
        Assert.Equal(["B", "A"], view.Cast<LibraryRow>().Select(row => row.Title));
        Assert.False(grid.ApplySort(new LibrarySortState("Missing", false)));
    }

    [AvaloniaFact]
    public void Persisted_grid_layout_restores_widths_and_keeps_keys_isolated()
    {
        var settings = new FakeSettings();
        var state = new GridStateService(settings);
        AppGridColumnDefinition[] definitions =
        [
            new("Path", "Track", "Path", 380, 220),
            new("Reason", "Reason", "Reason", 420, 220),
        ];
        var findings = new AppDataGrid();
        PersistedGridLayout.Configure(findings, state, "health.findings", definitions);
        findings.Columns[0].Width = new DataGridLength(515);

        var restored = new AppDataGrid();
        PersistedGridLayout.Configure(restored, state, "health.findings", definitions);
        var separate = new AppDataGrid();
        PersistedGridLayout.Configure(separate, state, "health.metadata-repairs", definitions);

        Assert.Equal(515, restored.Columns[0].Width.Value);
        Assert.Equal(380, separate.Columns[0].Width.Value);
    }

    [AvaloniaFact]
    public void Page_header_exposes_native_title_subtitle_and_actions()
    {
        var action = new Button { Content = "Run" };
        var header = new PageHeader
        {
            Title = "Health",
            Subtitle = "Audit the collection.",
            Actions = action,
        };

        Assert.Equal("Health", header.Title);
        Assert.Equal("Audit the collection.", header.Subtitle);
        Assert.Same(action, header.Actions);
    }

    [AvaloniaFact]
    public void App_buttons_center_content_while_navigation_remains_stretched()
    {
        var button = new Button { Content = "Run" };
        button.Classes.Add("app");
        var navigation = new Button { Content = "Library" };
        navigation.Classes.Add("nav");
        var window = new Window { Content = new StackPanel { Children = { button, navigation } } };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(global::Avalonia.Layout.HorizontalAlignment.Center, button.HorizontalContentAlignment);
            Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Center, button.VerticalContentAlignment);
            Assert.Equal(global::Avalonia.Layout.HorizontalAlignment.Stretch, navigation.HorizontalContentAlignment);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Split_width_persists_when_the_view_is_recreated()
    {
        var settings = new FakeSettings();
        var state = new SplitStateService(settings);
        state.Save("round-trip", 360);

        var split = new PersistedSplitView(state)
        {
            PersistenceKey = "round-trip",
            InitialLeftWidth = 300,
            MinLeftWidth = 200,
            MaxLeftWidth = 700,
            MinRightWidth = 160,
            Left = new Border(),
            Right = new Border(),
        };
        var window = new Window { Width = 900, Height = 600, Content = split };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(360, split.CurrentLeftWidth);

            split.CommitLeftWidth(384);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(384, state.Load("round-trip"));
        }
        finally
        {
            window.Hide();
        }

        var restored = new PersistedSplitView(state)
        {
            PersistenceKey = "round-trip",
            InitialLeftWidth = 300,
            MinLeftWidth = 200,
            MaxLeftWidth = 700,
            MinRightWidth = 160,
            Left = new Border(),
            Right = new Border(),
        };
        var restoredWindow = new Window { Width = 900, Height = 600, Content = restored };
        try
        {
            restoredWindow.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(384, restored.CurrentLeftWidth);
        }
        finally
        {
            restoredWindow.Hide();
        }
    }

    [AvaloniaFact]
    public void Light_and_dark_palette_tokens_match_the_app_contract()
    {
        Assert.True(Application.Current!.TryGetResource("AppAccentBrush", ThemeVariant.Dark, out object? darkAccent));
        Assert.True(Application.Current.TryGetResource("AppCanvasBrush", ThemeVariant.Dark, out object? darkCanvas));
        Assert.True(Application.Current.TryGetResource("AppAccentBrush", ThemeVariant.Light, out object? lightAccent));
        Assert.True(Application.Current.TryGetResource("AppCanvasBrush", ThemeVariant.Light, out object? lightCanvas));

        Assert.Equal(Color.Parse("#2CC7BC"), Assert.IsType<SolidColorBrush>(darkAccent).Color);
        Assert.Equal(Color.Parse("#0D1417"), Assert.IsType<SolidColorBrush>(darkCanvas).Color);
        Assert.Equal(Color.Parse("#087F8C"), Assert.IsType<SolidColorBrush>(lightAccent).Color);
        Assert.Equal(Color.Parse("#EEF4F3"), Assert.IsType<SolidColorBrush>(lightCanvas).Color);
    }

    [AvaloniaFact]
    public void Steel_blue_theme_overrides_dark_surfaces_and_restores_the_dark_palette()
    {
        Application app = Application.Current!;
        ThemeVariant? previousTheme = app.RequestedThemeVariant;
        var themes = new ThemeService();
        try
        {
            themes.Apply(ThemeService.SteelBlueTheme);

            Assert.Equal(ThemeService.SteelBlueTheme, themes.Current);
            Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
            Assert.True(app.TryGetResource("AppCanvasBrush", ThemeVariant.Dark, out object? canvasValue));
            Assert.True(app.TryGetResource("AppRaisedBrush", ThemeVariant.Dark, out object? raisedValue));
            Assert.True(app.TryGetResource("AppAccentBrush", ThemeVariant.Dark, out object? accentValue));
            Assert.True(app.TryGetResource("AppFaintBrush", ThemeVariant.Dark, out object? faintValue));
            Assert.Equal(Color.Parse("#101C2A"), Assert.IsType<SolidColorBrush>(canvasValue).Color);
            Assert.Equal(Color.Parse("#1D3043"), Assert.IsType<SolidColorBrush>(raisedValue).Color);
            Assert.Equal(Color.Parse("#3AAFB8"), Assert.IsType<SolidColorBrush>(accentValue).Color);
            Assert.True(ContrastRatio(
                Assert.IsType<SolidColorBrush>(faintValue).Color,
                Assert.IsType<SolidColorBrush>(raisedValue).Color) >= 4.5);

            themes.Apply("Dark");
            Assert.True(app.TryGetResource("AppCanvasBrush", ThemeVariant.Dark, out canvasValue));
            Assert.True(app.TryGetResource("AppAccentBrush", ThemeVariant.Dark, out accentValue));
            Assert.Equal(Color.Parse("#0D1417"), Assert.IsType<SolidColorBrush>(canvasValue).Color);
            Assert.Equal(Color.Parse("#2CC7BC"), Assert.IsType<SolidColorBrush>(accentValue).Color);
        }
        finally
        {
            themes.Apply("System");
            app.RequestedThemeVariant = previousTheme;
        }
    }

    [AvaloniaFact]
    public void Faint_text_meets_wcag_contrast_on_app_surfaces()
    {
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Assert.True(Application.Current!.TryGetResource("AppFaintBrush", theme, out object? faintValue));
            Color faint = Assert.IsType<SolidColorBrush>(faintValue).Color;
            foreach (string surfaceKey in new[]
                     {
                         "AppCanvasBrush", "AppPanelBrush", "AppRaisedBrush", "AppInsetBrush",
                     })
            {
                Assert.True(Application.Current.TryGetResource(surfaceKey, theme, out object? surfaceValue));
                Color surface = Assert.IsType<SolidColorBrush>(surfaceValue).Color;
                Assert.True(ContrastRatio(faint, surface) >= 4.5,
                    $"{theme} faint text contrast on {surfaceKey} was {ContrastRatio(faint, surface):0.00}:1");
            }
        }
    }

    [AvaloniaFact]
    public void Larger_text_and_logical_dpi_keep_minimum_header_commands_in_bounds()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        INavigationService navigation = services.GetRequiredService<INavigationService>();
        try
        {
            window.FontSize = 18;
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Width = 900;
            window.Height = 600;
            window.Activate();

            int headersChecked = 0;
            foreach (ShellDestination destination in Enum.GetValues<ShellDestination>())
            {
                navigation.Navigate(destination);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                Control activeView = Assert.IsAssignableFrom<Control>(
                    window.FindControl<ContentControl>("ContentHost")!.Content);
                PageHeader? header = activeView.GetVisualDescendants().OfType<PageHeader>().SingleOrDefault();
                if (header is null)
                    continue;
                headersChecked++;
                foreach (Button button in header.GetVisualDescendants().OfType<Button>()
                             .Where(button => button.IsEffectivelyVisible))
                {
                    Point? corner = button.TranslatePoint(
                        new Point(button.Bounds.Width, button.Bounds.Height), header);
                    Assert.NotNull(corner);
                    Assert.InRange(corner.Value.X, 0, header.Bounds.Width + 1);
                    Assert.InRange(corner.Value.Y, 0, header.Bounds.Height + 1);
                }
            }
            Assert.True(headersChecked >= 7);

            using var frame = window.GetLastRenderedFrame();
            Assert.NotNull(frame);
            Assert.True(window.RenderScaling > 0);
            Assert.Equal(window.RenderScaling,
                frame.PixelSize.Width / window.Bounds.Width, precision: 3);
            Assert.Equal(window.RenderScaling,
                frame.PixelSize.Height / window.Bounds.Height, precision: 3);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Playlist_output_is_available_from_workbench_and_library()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            INavigationService navigation =
                services.GetRequiredService<INavigationService>();

            navigation.Navigate(ShellDestination.Workbench);
            Dispatcher.UIThread.RunJobs();
            WorkbenchView workbench = Assert.IsType<WorkbenchView>(
                window.FindControl<ContentControl>("ContentHost")!.Content);
            Assert.NotNull(workbench.FindControl<AppDataGrid>(
                "PlaylistOutputGrid"));
            Assert.Equal(
                "Preview playlist",
                workbench.FindControl<Button>(
                    "PreviewPlaylistButton")!.Content);
            Assert.NotNull(workbench.FindControl<AppDataGrid>(
                "ExternalToolInvocationGrid"));
            Assert.Equal(
                "Preview tool",
                workbench.FindControl<Button>(
                    "PreviewExternalToolButton")!.Content);
            Assert.NotNull(workbench.FindControl<ListBox>(
                "ShortcutBindingList"));
            Assert.Equal(
                "Save shortcut",
                workbench.FindControl<Button>(
                    "SaveShortcutButton")!.Content);

            navigation.Navigate(ShellDestination.Library);
            Dispatcher.UIThread.RunJobs();
            LibraryView library = Assert.IsType<LibraryView>(
                window.FindControl<ContentControl>("ContentHost")!.Content);
            Assert.NotNull(library.FindControl<AppDataGrid>(
                "LibraryPlaylistOutputGrid"));
            Assert.Equal(
                "Preview playlist",
                library.FindControl<Button>(
                    "PreviewLibraryPlaylistButton")!.Content);
            Assert.NotNull(library.FindControl<AppDataGrid>(
                "LibraryExternalToolInvocationGrid"));
            Assert.Equal(
                "Preview tool",
                library.FindControl<Button>(
                    "PreviewLibraryExternalToolButton")!.Content);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Native_shell_constructs_and_routes_every_destination()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        ContentControl host = window.FindControl<ContentControl>("ContentHost")!;
        INavigationService navigation = services.GetRequiredService<INavigationService>();
        var destinations = new (ShellDestination Destination, Type View)[]
        {
            (ShellDestination.Home, typeof(HomeView)),
            (ShellDestination.Library, typeof(LibraryView)),
            (ShellDestination.Health, typeof(HealthView)),
            (ShellDestination.Ingest, typeof(IngestView)),
            (ShellDestination.Organize, typeof(OrganizeView)),
            (ShellDestination.Devices, typeof(DevicesView)),
            (ShellDestination.Operations, typeof(OperationsView)),
            (ShellDestination.Settings, typeof(SettingsView)),
        };

        Assert.Equal(WindowDecorations.Full, window.WindowDecorations);
        Assert.True(window.CanResize);
        Assert.Equal(900, window.MinWidth);
        Assert.Equal(600, window.MinHeight);

        foreach ((ShellDestination destination, Type view) in destinations)
        {
            navigation.Navigate(destination);
            Assert.IsType(view, host.Content);
            Button navigationButton = window.FindControl<Button>($"{destination}Nav")!;
            Assert.Equal("Selected", global::Avalonia.Automation.AutomationProperties.GetItemStatus(
                navigationButton));
            Assert.Equal(destination.ToString(),
                global::Avalonia.Automation.AutomationProperties.GetName(navigationButton));
            Assert.NotNull(ToolTip.GetTip(navigationButton));
            if (host.Content is HomeView home)
            {
                Grid indexingBanner = home.FindControl<Grid>("IndexingBannerLayout")!;
                Assert.Equal(2, indexingBanner.ColumnDefinitions.Count);
                Assert.Empty(indexingBanner.Children.OfType<Border>());
            }
            else if (host.Content is LibraryView library)
            {
                AppDataGrid grid = library.FindControl<AppDataGrid>("LibraryGrid")!;
                Assert.InRange(grid.Columns.Count, 8, 13);
                Assert.Equal("Artwork", grid.KeyFor(grid.Columns[0]));
                Popup columnPopover = library.FindControl<Popup>("ColumnPopover")!;
                Button columnsButton = library.FindControl<Button>("ColumnsButton")!;
                Button closeColumnsButton = library.FindControl<Button>("CloseColumnsButton")!;
                Assert.Equal("Filter library", global::Avalonia.Automation.AutomationProperties.GetName(
                    library.FindControl<TextBox>("FilterBox")!));
                Assert.True(columnPopover.IsLightDismissEnabled);
                columnsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(columnPopover.IsOpen);
                closeColumnsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(columnPopover.IsOpen);
            }
            else if (host.Content is HealthView health)
            {
                TextBlock matrixExplanation = health.FindControl<TextBlock>("AlbumMatrixExplanation")!;
                Assert.Equal("Read-only album consistency audit", matrixExplanation.Text);
                TextBox artistThreshold = health.FindControl<TextBox>("ArtistThresholdInput")!;
                Assert.NotNull(health.FindControl<ComboBox>("ArtworkRepairRootDisposition"));
                Assert.Equal("Similar artist fuzzy threshold",
                    global::Avalonia.Automation.AutomationProperties.GetName(artistThreshold));
                artistThreshold.Text = "0.13";
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(0.13, services.GetRequiredService<AnalyzerViewModel>().ArtistThreshold,
                    precision: 3);
                artistThreshold.Text = "";
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(0, services.GetRequiredService<AnalyzerViewModel>().ArtistThreshold);
                Assert.NotNull(health.FindControl<ComboBox>("ArtistRootDisposition"));
                Assert.DoesNotContain(health.GetVisualDescendants().OfType<Button>(),
                    button => string.Equals(button.Content as string, "Merge", StringComparison.Ordinal));
                AppDataGrid repairGrid = health.FindControl<AppDataGrid>("RepairGrid")!;
                DataGridColumn before = repairGrid.Columns.Single(column =>
                    repairGrid.KeyFor(column) == "Before");
                DataGridColumn after = repairGrid.Columns.Single(column =>
                    repairGrid.KeyFor(column) == "After");
                DataGridColumn result = repairGrid.Columns.Single(column =>
                    repairGrid.KeyFor(column) == "Result");
                Assert.IsType<DataGridTemplateColumn>(before);
                Assert.IsType<DataGridTemplateColumn>(after);
                Assert.IsType<DataGridTextColumn>(result);

                var item = new AnalysisRepairItemViewModel(new AnalysisTagRepair(
                    @"Z:\Music\Track.flac", TagFields.Title, "Mix\u00A0One", "Mix One",
                    "Normalize whitespace.", 100, DateTime.UtcNow));
                var beforeColumn = Assert.IsType<DataGridTemplateColumn>(before);
                TextBlock beforeCell = Assert.IsType<TextBlock>(
                    beforeColumn.CellTemplate!.Build(item));
                Assert.Contains(beforeCell.Inlines!.OfType<Run>(),
                    run => run.Classes.Contains("text-difference"));
                Assert.Contains("U+00A0 NO-BREAK SPACE",
                    Assert.IsType<string>(ToolTip.GetTip(beforeCell)));
            }
            else if (host.Content is IngestView ingest)
            {
                ComboBox recentFolders = ingest.FindControl<ComboBox>("RecentFoldersCombo")!;
                Assert.Equal("Recent folders", recentFolders.PlaceholderText);
                Assert.Equal("Recent ingest folders",
                    global::Avalonia.Automation.AutomationProperties.GetName(recentFolders));
                Assert.Single(ingest.FindControl<StackPanel>("SetupPanel")!.Children.OfType<Grid>());
                Assert.DoesNotContain(ingest.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Text?.Contains("Preset", StringComparison.OrdinalIgnoreCase) == true);
                Assert.DoesNotContain(ingest.GetVisualDescendants().OfType<Button>(),
                    button => button.Content is string content &&
                        content.Contains("preset", StringComparison.OrdinalIgnoreCase));
            }
            else if (host.Content is OrganizeView organize)
            {
                Grid summary = organize.FindControl<Grid>("SummaryLayout")!;
                TextBlock plannedCount = organize.FindControl<TextBlock>("PlannedCount")!;
                Assert.Same(summary, plannedCount.Parent);
                Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Center,
                    plannedCount.VerticalAlignment);
                Assert.Equal(FontWeight.SemiBold, plannedCount.FontWeight);
                Assert.Contains("summary-label", plannedCount.Classes);
            }
            else if (host.Content is DevicesView devices)
            {
                AppDataGrid grid = devices.FindControl<AppDataGrid>("ActionsGrid")!;
                Button restore = devices.FindControl<Button>("RestoreButton")!;
                ComboBox deviceSelector = devices.FindControl<ComboBox>("DeviceSelector")!;
                Button refreshDevices = devices.FindControl<Button>("RefreshDevicesButton")!;
                StackPanel configuration = devices.FindControl<StackPanel>("ConfigurationPanel")!;
                DevicesViewModel viewModel = Assert.IsType<DevicesViewModel>(devices.DataContext);
                Assert.Equal(5, grid.Columns.Count);
                Assert.Equal("Status", grid.KeyFor(grid.Columns[0]));
                Assert.Equal("Kind", grid.KeyFor(grid.Columns[1]));
                Assert.Equal("Restore", restore.Content);
                Assert.Equal("Android device",
                    global::Avalonia.Automation.AutomationProperties.GetName(deviceSelector));
                Assert.Equal("Refresh Android devices",
                    global::Avalonia.Automation.AutomationProperties.GetName(refreshDevices));
                Assert.NotNull(deviceSelector.ItemTemplate);
                Assert.True(configuration.IsEnabled);
                viewModel.IsBusy = true;
                Dispatcher.UIThread.RunJobs();
                Assert.False(configuration.IsEnabled);
                viewModel.IsBusy = false;
                Dispatcher.UIThread.RunJobs();
                Assert.True(configuration.IsEnabled);
            }
        }
    }

    [AvaloniaFact]
    public void Health_long_result_lists_realize_only_the_visible_containers()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        var health = new HealthView();
        var viewModel = Assert.IsType<AnalyzerViewModel>(health.DataContext);
        DuplicateGroup[] groups = Enumerable.Range(0, 2_000)
            .Select(index => new DuplicateGroup($"Duplicate {index:N0}",
            [
                new TrackRecord
                {
                    Path = $@"C:\Music\Track {index:N0}.flac",
                    Title = $"Track {index:N0}",
                },
            ]))
            .ToArray();
        AnalysisRunViewModel run = AnalysisRunViewModel.ForDuplicates(
            "Duplicates", groups, "2,000 duplicate groups");
        viewModel.Runs.Add(run);
        viewModel.SelectedRun = run;
        var window = new Window { Width = 900, Height = 600, Content = health };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            ItemsControl list = health.FindControl<ItemsControl>("DuplicateResultsList")!;
            var panel = Assert.IsType<VirtualizingStackPanel>(list.ItemsPanelRoot);
            Assert.InRange(panel.Children.Count, 1, 100);
            Assert.True(panel.Children.Count < groups.Length);
            Assert.False(list is SelectingItemsControl);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Health_interactive_artwork_results_are_nonselecting_and_virtualized()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        var health = new HealthView();
        var viewModel = Assert.IsType<AnalyzerViewModel>(health.DataContext);
        ArtworkRepairItemViewModel[] repairs = Enumerable.Range(0, 2_000)
            .Select(index => new ArtworkRepairItemViewModel(
                ArtworkRepairKind.NormalizeFile,
                $"Track {index:N0}.flac",
                "Normalize artwork",
                [$@"C:\Music\Track {index:N0}.flac"],
                [], false, 128_000, 800, "Test item"))
            .ToArray();
        AnalysisRunViewModel run = AnalysisRunViewModel.ForArtwork(
            new AnalysisReport("Artwork health", []), [], repairs,
            "2,000 artwork repairs");
        viewModel.Runs.Add(run);
        viewModel.SelectedRun = run;
        var window = new Window { Width = 1100, Height = 700, Content = health };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            ItemsControl list = health.FindControl<ItemsControl>("ArtworkRepairResultsList")!;
            var panel = Assert.IsType<VirtualizingStackPanel>(list.ItemsPanelRoot);
            Assert.InRange(panel.Children.Count, 1, 100);
            Assert.True(panel.Children.Count < repairs.Length);
            Assert.False(list is SelectingItemsControl);
            Assert.NotNull(health.GetVisualDescendants().OfType<TreeView>().FirstOrDefault(tree =>
                ReferenceEquals(tree.ItemsSource, viewModel.ArtworkRepairGroups)));
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Health_dropdowns_are_not_nested_inside_other_click_targets()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        var health = new HealthView();
        var viewModel = Assert.IsType<AnalyzerViewModel>(health.DataContext);
        var artistGroup = new ArtistGroupViewModel(new SimilarArtistGroup(
        [
            new ArtistVariant("Canonical", [@"C:\Music\one.flac"]),
            new ArtistVariant("Canoncial", [@"C:\Music\two.flac"]),
        ]));
        AnalysisRunViewModel run = AnalysisRunViewModel.ForArtists(
            "Similar artists", [artistGroup], "Similar artists");
        viewModel.Runs.Add(run);
        viewModel.SelectedRun = run;
        var window = new Window { Width = 1100, Height = 700, Content = health };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            ComboBox findingRoot = health.FindControl<ComboBox>("FindingRootDisposition")!;
            Assert.Empty(findingRoot.GetVisualAncestors().OfType<Button>());

            ComboBox variantDisposition = health.GetVisualDescendants().OfType<ComboBox>()
                .First(combo => string.Equals(
                    global::Avalonia.Automation.AutomationProperties.GetName(combo),
                    "Disposition for artist spelling variant", StringComparison.Ordinal));
            Assert.Empty(variantDisposition.GetVisualAncestors().OfType<Expander>());
            Assert.Empty(variantDisposition.GetVisualAncestors().OfType<ListBoxItem>());
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Confirmation_dialog_defaults_to_cancel_and_restores_focus_on_escape()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        DialogService dialogs = services.GetRequiredService<DialogService>();
        Task<bool>? pending = null;
        try
        {
            window.Show();
            window.Width = 900;
            window.Height = 600;
            window.Activate();
            Dispatcher.UIThread.RunJobs();
            TextBox search = window.FindControl<TextBox>("SearchBox")!;
            search.Focus();

            pending = dialogs.ConfirmAsync(
                "Permanently remove files?",
                "Remove 12 files. No recovery is available.",
                "Remove",
                DialogTone.Danger);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            DialogHost host = window.GetVisualDescendants().OfType<DialogHost>().Single();
            Border card = host.FindControl<Border>("DialogCard")!;
            Button cancel = host.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "Cancel"));
            StackPanel buttons = host.FindControl<StackPanel>("DialogButtons")!;
            Assert.True(host.IsVisible);
            Assert.True(card.Bounds.Width <= 560);
            Assert.True(card.Bounds.Height <= window.Bounds.Height - 48);
            Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(card));
            Assert.Equal(["Cancel", "Remove"],
                buttons.Children.OfType<Button>().Select(button => button.Content?.ToString()));
            Assert.Same(cancel, window.FocusManager?.GetFocusedElement());

            host.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Escape,
            });
            Dispatcher.UIThread.RunJobs();

            Assert.False(await pending);
            Assert.False(host.IsVisible);
            Assert.Same(search, window.FocusManager?.GetFocusedElement());
        }
        finally
        {
            if (pending is { IsCompleted: false })
                dialogs.Complete(false);
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Native_close_cancels_a_dismissible_dialog_without_closing_its_owner()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        DialogService dialogs = services.GetRequiredService<DialogService>();
        Task<bool>? pending = null;
        try
        {
            window.Show();
            window.Activate();
            Dispatcher.UIThread.RunJobs();

            pending = dialogs.ConfirmAsync(
                "Apply reviewed changes?",
                "Apply one reviewed change.",
                "Apply");
            Dispatcher.UIThread.RunJobs();
            DialogHost host = window.GetVisualDescendants().OfType<DialogHost>().Single();
            Assert.True(host.IsVisible);

            window.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.IsVisible);
            Assert.False(await pending);
            Assert.Null(dialogs.Current);
            Assert.False(host.IsVisible);
        }
        finally
        {
            if (pending is { IsCompleted: false })
                dialogs.Complete(false);
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Native_close_preserves_a_dirty_fields_dialog()
    {
        using ServiceProvider services = BuildIsolatedServices(
            configureServices: collection =>
                collection.AddSingleton<IMediaFileService>(new FieldsDialogMediaService()));
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        DialogService dialogs = services.GetRequiredService<DialogService>();
        Task<bool>? pending = null;
        try
        {
            window.Show();
            window.Activate();
            Dispatcher.UIThread.RunJobs();

            pending = dialogs.ShowAsync([@"C:\Music\Track.flac"]);
            Dispatcher.UIThread.RunJobs();
            FieldsRequest request = Assert.IsType<FieldsRequest>(dialogs.Current);
            FieldRow title = Assert.Single(request.ViewModel.Rows,
                row => row.Field == TagFields.Title);
            title.Value = "Unsaved title";
            Assert.False(request.DismissalPolicy.CanDismissFromCloseButton);

            DialogHost host = window.GetVisualDescendants().OfType<DialogHost>().Single();
            Assert.True(host.IsVisible);
            window.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.IsVisible);
            Assert.True(host.IsVisible);
            Assert.Same(request, dialogs.Current);
            Assert.False(pending.IsCompleted);
            Assert.Equal("Unsaved title", title.Value);
        }
        finally
        {
            if (pending is { IsCompleted: false })
            {
                dialogs.Complete(false);
                Assert.False(await pending);
            }
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Persistent_activity_strip_routes_and_cancels_at_minimum_size()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        IActivityService activities = services.GetRequiredService<IActivityService>();
        bool cancelled = false;
        Guid activity = activities.Start(
            "Preview operation", "Computing a recoverable plan", ShellDestination.Operations,
            () => cancelled = true);
        MainWindow window = services.GetRequiredService<MainWindow>();
        try
        {
            window.Show();
            window.Width = 900;
            window.Height = 600;
            window.Activate();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            Border banner = window.FindControl<Border>("ActivityBanner")!;
            Assert.True(banner.IsVisible);
            Assert.InRange(banner.Bounds.Height, 36, 56);
            Button open = banner.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "Open"));
            Button cancel = banner.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "Cancel"));

            Assert.True(open.Command!.CanExecute(open.CommandParameter));
            open.Command.Execute(open.CommandParameter);
            Dispatcher.UIThread.RunJobs();
            Assert.IsType<OperationsView>(window.FindControl<ContentControl>("ContentHost")!.Content);

            Assert.True(cancel.Command!.CanExecute(cancel.CommandParameter));
            cancel.Command.Execute(cancel.CommandParameter);
            Dispatcher.UIThread.RunJobs();
            Assert.True(cancelled);
            Assert.False(cancel.IsEffectivelyEnabled);

            activities.Finish(activity, "Preview cancelled", AppActivityState.Cancelled);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("warning", banner.Classes);
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public void Completed_activity_strip_removes_stale_progress_and_cancel_action()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        IActivityService activities = services.GetRequiredService<IActivityService>();
        Guid activity = activities.Start(
            "Index library", "Indexing fixture tracks", ShellDestination.Library,
            () => { });
        activities.Report(activity, "Indexed 2 fixture tracks", 0.75);

        MainWindow window = services.GetRequiredService<MainWindow>();
        ThemeVariant? previousTheme = Application.Current!.RequestedThemeVariant;
        try
        {
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            window.Show();
            window.Width = 2048;
            window.Height = 900;
            window.Activate();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            Grid progress = window.FindControl<Grid>("ActivityProgressHost")!;
            Button cancel = window.FindControl<Button>("ActivityCancelButton")!;
            Button dismiss = window.FindControl<Button>("ActivityDismissButton")!;
            TextBlock state = window.FindControl<TextBlock>("ActivityStateLabel")!;
            Assert.True(progress.IsVisible);
            Assert.True(progress.ClipToBounds);
            Assert.True(cancel.IsVisible);
            Assert.False(dismiss.IsVisible);
            Assert.Equal("In progress", state.Text);
            Assert.True(progress.Bounds.Right <= state.Bounds.Left,
                $"Progress ended at {progress.Bounds.Right}, but the state label began at {state.Bounds.Left}.");

            activities.Finish(activity, "Index complete: 2 unchanged.");
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            Assert.False(progress.IsVisible);
            Assert.False(cancel.IsVisible);
            Assert.True(dismiss.IsVisible);
            Assert.Equal("Completed", state.Text);
            Assert.Contains("success", state.Classes);

            string? captureDirectory = Environment.GetEnvironmentVariable(
                "MUSIC_LIBRARY_MANAGER_CAPTURE_DIR");
            if (!string.IsNullOrWhiteSpace(captureDirectory))
            {
                Directory.CreateDirectory(captureDirectory);
                using var frame = window.GetLastRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(Path.Combine(captureDirectory, "activity-completed-wide.png"),
                    PngBitmapEncoderOptions.Default);
            }
        }
        finally
        {
            window.Hide();
            Application.Current.RequestedThemeVariant = previousTheme;
        }
    }

    [AvaloniaFact]
    public void Every_destination_completes_a_headless_1440_by_900_render_pass()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        INavigationService navigation = services.GetRequiredService<INavigationService>();
        string? captureDirectory = Environment.GetEnvironmentVariable("MUSIC_LIBRARY_MANAGER_CAPTURE_DIR");
        bool isCapturing = !string.IsNullOrWhiteSpace(captureDirectory);
        ThemeVariant? previousTheme = Application.Current!.RequestedThemeVariant;
        if (isCapturing)
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Width = 1440;
        window.Height = 900;
        window.Activate();
        Dispatcher.UIThread.RunJobs();
        if (isCapturing)
            Directory.CreateDirectory(captureDirectory!);

        Button homeNavigation = window.FindControl<Button>("HomeNav")!;
        Grid navigationContent = Assert.IsType<Grid>(homeNavigation.Content);
        Viewbox navigationIcon = navigationContent.Children.OfType<Viewbox>()
            .Single(icon => icon.Classes.Contains("nav-icon"));
        TextBlock navigationLabel = navigationContent.Children.OfType<TextBlock>()
            .Single(text => text.Classes.Contains("nav-label"));
        Assert.Equal(44, homeNavigation.Bounds.Height);
        Assert.True(homeNavigation.Bounds.Width >= 190,
            $"Navigation selection did not fill the rail. Width={homeNavigation.Bounds.Width:0}");
        Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Center, homeNavigation.VerticalContentAlignment);
        Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Center, navigationContent.VerticalAlignment);
        Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Center, navigationIcon.VerticalAlignment);
        Assert.Equal(global::Avalonia.Layout.VerticalAlignment.Center, navigationLabel.VerticalAlignment);
        Assert.Equal(20, navigationIcon.Width);
        Assert.Equal(20, navigationIcon.Height);
        Assert.Equal(15, navigationLabel.FontSize);
        Border navigationMarker = navigationContent.Children.OfType<Border>()
            .Single(marker => marker.Classes.Contains("nav-marker"));
        Assert.Equal(1, navigationMarker.Opacity);
        Assert.Equal(3, navigationMarker.Width);
        Assert.Equal(22, navigationMarker.Height);
        Assert.Equal(
            global::Avalonia.Layout.VerticalAlignment.Center,
            window.FindControl<TextBox>("SearchBox")!.VerticalContentAlignment);
        Assert.Equal(WindowDecorations.Full, window.WindowDecorations);

        try
        {
            foreach (ShellDestination destination in Enum.GetValues<ShellDestination>())
            {
                navigation.Navigate(destination);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                Control activeView = Assert.IsAssignableFrom<Control>(window.FindControl<ContentControl>("ContentHost")!.Content);
                Assert.True(activeView.Bounds.Width >= 1150,
                    $"{destination} did not fill the content host. Width={activeView.Bounds.Width:0}");
                if (destination == ShellDestination.Library)
                {
                    LibraryView library = Assert.IsType<LibraryView>(window.FindControl<ContentControl>("ContentHost")!.Content);
                    AppDataGrid grid = library.FindControl<AppDataGrid>("LibraryGrid")!;
                    if (!isCapturing)
                    {
                        grid.ItemsSource = new[]
                        {
                            new LibraryRow(new TrackRecord
                            {
                                Path = @"C:\Music\Wild Fire.flac",
                                Title = "Wild Fire",
                                Artist = "The Avalonians",
                            }),
                        };
                    }
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                    PersistedSplitView split = library.FindControl<PersistedSplitView>("WorkspaceSplit")!;
                    ContentPresenter leftPresenter = split.FindControl<ContentPresenter>("LeftPresenter")!;
                    string bounds = string.Join(" -> ", new Control[] { grid }.Concat(grid.GetVisualAncestors().OfType<Control>())
                        .Select(control => $"{control.GetType().Name}:{control.Bounds.Width:0}x{control.Bounds.Height:0}"));
                    Assert.True(grid.Bounds.Width >= 500, $"Library grid width collapsed. Left={split.Left?.GetType().Name ?? "null"}; Presented={leftPresenter.Content?.GetType().Name ?? "null"}; Split={split.Bounds.Width:0}x{split.Bounds.Height:0}; Presenter={leftPresenter.Bounds.Width:0}x{leftPresenter.Bounds.Height:0}; {bounds}");
                    Assert.True(grid.Bounds.Height >= 300, $"Library grid height collapsed. {bounds}");
                    Assert.True(split.FindControl<ContentPresenter>("RightPresenter")!.Bounds.Width >= 190,
                        "Library inspector collapsed below its minimum width.");
                    Assert.InRange(grid.Columns.Count, 8, 13);
                    Assert.Contains(grid.GetVisualDescendants(), visual => visual.GetType().Name == "DataGridColumnHeader");
                    if (!isCapturing)
                        Assert.NotNull(grid.ItemsSource);
                    SelectionInspectorView inspector = library.GetVisualDescendants().OfType<SelectionInspectorView>().Single();
                    Assert.True(inspector.FindControl<Border>("EmptyState")!.IsVisible);
                    Assert.False(inspector.FindControl<ScrollViewer>("InspectorContent")!.IsVisible);
                }
                else if (destination == ShellDestination.Settings)
                {
                    SettingsView settings = Assert.IsType<SettingsView>(window.FindControl<ContentControl>("ContentHost")!.Content);
                    settings.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 0;
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                    ScrollViewer scroll = settings.FindControl<ScrollViewer>("ConfigurationSettingsScroll")!;
                    StackPanel content = settings.FindControl<StackPanel>("ConfigurationSettingsContent")!;
                    Assert.Equal(global::Avalonia.Layout.HorizontalAlignment.Stretch, scroll.HorizontalContentAlignment);
                    Assert.Equal(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
                    Assert.Equal(global::Avalonia.Layout.HorizontalAlignment.Stretch, content.HorizontalAlignment);
                    Assert.InRange(content.Bounds.Width, 1000, 1040);
                    Assert.True(content.Bounds.Width <= scroll.Bounds.Width,
                        $"Settings content exceeded its viewport. Content={content.Bounds.Width:0}; Scroll={scroll.Bounds.Width:0}");
                }
                else if (destination == ShellDestination.Health)
                {
                    HealthView health = Assert.IsType<HealthView>(window.FindControl<ContentControl>("ContentHost")!.Content);
                    Assert.Equal("All findings", Assert.IsType<TextBlock>(
                        Assert.IsType<Grid>(health.FindControl<Button>("FindingRootButton")!.Content)
                            .GetVisualDescendants().OfType<TextBlock>().First()).Text);
                }
                using var frame = window.GetLastRenderedFrame();
                Assert.NotNull(frame);
                Assert.Equal(1440, frame.PixelSize.Width);
                Assert.Equal(900, frame.PixelSize.Height);
                if (isCapturing)
                    frame.Save(Path.Combine(captureDirectory!, $"{destination}.png"),
                        PngBitmapEncoderOptions.Default);
            }
        }
        finally
        {
            window.Hide();
            Application.Current.RequestedThemeVariant = previousTheme;
        }
    }

    [AvaloniaFact]
    public void Every_destination_renders_at_the_900_by_600_minimum_in_light_and_dark()
    {
        string? captureDirectory = Environment.GetEnvironmentVariable("MUSIC_LIBRARY_MANAGER_CAPTURE_DIR");
        ThemeVariant? previousTheme = Application.Current!.RequestedThemeVariant;
        try
        {
            foreach ((string name, ThemeVariant theme) in new[]
                     {
                         ("light", ThemeVariant.Light),
                         ("dark", ThemeVariant.Dark),
                     })
            {
                Application.Current.RequestedThemeVariant = theme;
                using ServiceProvider services = BuildIsolatedServices();
                App.UseServicesForTests(services);
                MainWindow window = services.GetRequiredService<MainWindow>();
                INavigationService navigation = services.GetRequiredService<INavigationService>();
                try
                {
                    window.Show();
                    window.WindowState = WindowState.Normal;
                    window.Width = 900;
                    window.Height = 600;
                    window.Activate();
                    Dispatcher.UIThread.RunJobs();

                    Assert.Equal(64, window.FindControl<Grid>("BodyGrid")!.ColumnDefinitions[0].ActualWidth);
                    Assert.False(window.FindControl<TextBlock>("ConfigurationChipText")!.IsVisible);
                    Assert.False(window.FindControl<Border>("SearchShortcut")!.IsVisible);

                    foreach (ShellDestination destination in Enum.GetValues<ShellDestination>())
                    {
                        navigation.Navigate(destination);
                        Dispatcher.UIThread.RunJobs();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

                        Control activeView = Assert.IsAssignableFrom<Control>(
                            window.FindControl<ContentControl>("ContentHost")!.Content);
                        Assert.True(activeView.Bounds.Width >= 800,
                            $"{destination} did not fill the compact content host. Width={activeView.Bounds.Width:0}");
                        Assert.True(activeView.Bounds.Height >= 500,
                            $"{destination} did not fill the compact content host. Height={activeView.Bounds.Height:0}");

                        if (destination == ShellDestination.Library)
                        {
                            LibraryView library = Assert.IsType<LibraryView>(activeView);
                            Button inspectorToggle = library.FindControl<Button>("InspectorToggle")!;
                            Border inspectorScrim = library.FindControl<Border>("InspectorScrim")!;
                            Assert.True(inspectorToggle.IsVisible);
                            Assert.False(inspectorScrim.IsVisible);
                            inspectorToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Dispatcher.UIThread.RunJobs();
                            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                            Assert.True(inspectorScrim.IsVisible);
                            PersistedSplitView split = library.FindControl<PersistedSplitView>("WorkspaceSplit")!;
                            ContentPresenter left = split.FindControl<ContentPresenter>("LeftPresenter")!;
                            ContentPresenter right = split.FindControl<ContentPresenter>("RightPresenter")!;
                            Assert.True(left.Bounds.Width >= split.Bounds.Width - 1,
                                $"Compact Library pane was clipped: {left.Bounds.Width:0}/{split.Bounds.Width:0}");
                            Assert.InRange(right.Bounds.Width, 319, 321);
                            Assert.True(right.Bounds.Right <= split.Bounds.Width + 1,
                                $"Inspector drawer exceeded its host: right={right.Bounds.Right:0}, host={split.Bounds.Width:0}");
                        }
                        else if (destination == ShellDestination.Settings)
                        {
                            SettingsView settings = Assert.IsType<SettingsView>(activeView);
                            SettingsViewModel viewModel = Assert.IsType<SettingsViewModel>(settings.DataContext);
                            viewModel.DatabaseFile += ".changed";
                            settings.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 4;
                            Dispatcher.UIThread.RunJobs();
                            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                            foreach (NumericUpDown input in new[]
                                     {
                                         settings.FindControl<NumericUpDown>("ArtworkSizeThresholdInput")!,
                                         settings.FindControl<NumericUpDown>("ArtworkDimensionThresholdInput")!,
                                         settings.FindControl<NumericUpDown>("ArtworkRepairSizeTargetInput")!,
                                         settings.FindControl<NumericUpDown>("ArtworkRepairDimensionTargetInput")!,
                                     })
                            {
                                Assert.True(input.IsEffectivelyVisible);
                                Point? corner = input.TranslatePoint(
                                    new Point(input.Bounds.Width, input.Bounds.Height), settings);
                                Assert.NotNull(corner);
                                Assert.InRange(corner.Value.X, 0, settings.Bounds.Width + 1);
                                Assert.InRange(corner.Value.Y, 0, settings.Bounds.Height + 1);
                            }
                            PageHeader header = settings.GetVisualDescendants().OfType<PageHeader>().Single();
                            Button discard = header.GetVisualDescendants().OfType<Button>()
                                .Single(button => Equals(button.Content, "Discard"));
                            Assert.True(discard.IsEffectivelyVisible);
                            foreach (Button button in header.GetVisualDescendants().OfType<Button>()
                                         .Where(button => button.IsEffectivelyVisible))
                            {
                                Point? corner = button.TranslatePoint(
                                    new Point(button.Bounds.Width, button.Bounds.Height), header);
                                Assert.NotNull(corner);
                                Assert.InRange(corner.Value.X, 0, header.Bounds.Width + 1);
                                Assert.InRange(corner.Value.Y, 0, header.Bounds.Height + 1);
                            }
                        }

                        using var frame = window.GetLastRenderedFrame();
                        Assert.NotNull(frame);
                        Assert.Equal(900, frame.PixelSize.Width);
                        Assert.Equal(600, frame.PixelSize.Height);
                        if (!string.IsNullOrWhiteSpace(captureDirectory))
                        {
                            Directory.CreateDirectory(captureDirectory);
                            frame.Save(Path.Combine(captureDirectory,
                                $"{name}-900x600-{destination}.png"),
                                PngBitmapEncoderOptions.Default);
                        }
                    }
                }
                finally
                {
                    window.Hide();
                }
            }
        }
        finally
        {
            Application.Current.RequestedThemeVariant = previousTheme;
        }
    }

    [AvaloniaFact]
    public void Populated_health_nested_panes_fit_at_the_900_by_600_minimum()
    {
        using ServiceProvider services = BuildIsolatedServices();
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        AnalyzerViewModel analyzer = services.GetRequiredService<AnalyzerViewModel>();
        INavigationService navigation = services.GetRequiredService<INavigationService>();
        var record = new TrackRecord
        {
            Path = @"X:\Fixture\Artist\Album\Track.flac",
            Artist = "Fixture Artist",
            Album = "Fixture Album",
            Title = "Fixture Track",
        };
        AnalysisRunViewModel findings = AnalysisRunViewModel.ForFindings(
            new AnalysisReport("Fixture findings",
                [new AnalysisFinding(record.Path, "Fixture inconsistency", "Metadata")]),
            [record],
            "One fixture finding.");
        var repair = new AnalysisTagRepair(
            record.Path,
            TagFields.AlbumArtist,
            null,
            record.Artist!,
            "Fill the missing album artist",
            1,
            DateTime.UnixEpoch);
        AnalysisRunViewModel repairs = AnalysisRunViewModel.ForRepairs(
            new AnalysisRepairPlan("Fixture repairs", [repair]),
            [new AnalysisRepairItemViewModel(repair)],
            [record],
            "One fixture metadata repair.");
        analyzer.Runs.Add(findings);
        analyzer.Runs.Add(repairs);
        analyzer.SelectedRun = repairs;

        try
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Width = 900;
            window.Height = 600;
            window.Activate();
            navigation.Navigate(ShellDestination.Health);
            Render();

            HealthView health = Assert.IsType<HealthView>(
                window.FindControl<ContentControl>("ContentHost")!.Content);
            AssertNestedPaneFits(health, "health-metadata-repairs");

            analyzer.SelectedRun = findings;
            Render();
            AssertNestedPaneFits(health, "health-findings");

            using var frame = window.GetLastRenderedFrame();
            Assert.NotNull(frame);
            Assert.Equal(900, frame.PixelSize.Width);
            Assert.Equal(600, frame.PixelSize.Height);
        }
        finally
        {
            window.Hide();
        }

        static void Render()
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        }

        static void AssertNestedPaneFits(HealthView health, string persistenceKey)
        {
            PersistedSplitView split = health.GetVisualDescendants()
                .OfType<PersistedSplitView>()
                .Single(candidate => candidate.PersistenceKey == persistenceKey);
            ContentPresenter left = split.FindControl<ContentPresenter>("LeftPresenter")!;
            ContentPresenter right = split.FindControl<ContentPresenter>("RightPresenter")!;

            Assert.True(split.Bounds.Width >= split.MinLeftWidth + 10 + split.MinRightWidth,
                $"{persistenceKey} cannot satisfy its pane minima: {split.Bounds.Width:0}px.");
            Assert.True(left.Bounds.Right <= split.Bounds.Width + 1,
                $"{persistenceKey} hierarchy exceeded its host: {left.Bounds.Right:0}/{split.Bounds.Width:0}.");
            Assert.True(right.Bounds.Right <= split.Bounds.Width + 1,
                $"{persistenceKey} results exceeded its host: {right.Bounds.Right:0}/{split.Bounds.Width:0}.");
            Assert.True(right.Bounds.Width >= split.MinRightWidth - 1,
                $"{persistenceKey} results collapsed below minimum: {right.Bounds.Width:0}px.");
        }
    }

    [AvaloniaFact]
    public async Task Clearing_library_health_chip_clears_the_originating_disposition()
    {
        var record = new TrackRecord
        {
            Path = @"X:\Fixture\Lossy.mp3",
            Artist = "Fixture Artist",
            AlbumArtist = "Fixture Artist",
            Album = "Fixture Album",
            Title = "Lossy Fixture",
            CodecName = "MP3",
            CodecType = CodecType.Lossy,
        };
        using ServiceProvider services = BuildIsolatedServices([record]);
        WorkflowIntegrationService integration =
            services.GetRequiredService<WorkflowIntegrationService>();
        AnalyzerViewModel health = services.GetRequiredService<AnalyzerViewModel>();
        LibraryViewModel library = services.GetRequiredService<LibraryViewModel>();
        integration.Start();

        await health.RunLossyCommand.ExecuteAsync(null);
        AnalysisFindingViewModel finding = Assert.Single(
            Assert.Single(Assert.Single(health.FindingGroups).Artists).Albums).Findings[0];
        finding.Disposition = AnalysisFindingDisposition.Filter;

        Assert.True(library.HasHealthFilter);
        Assert.Equal([record.Path], health.FilteredPaths);

        library.ClearHealthFilterCommand.Execute(null);

        Assert.False(library.HasHealthFilter);
        Assert.Empty(health.FilteredPaths);
        Assert.Equal(AnalysisFindingDisposition.None, finding.Disposition);
    }

    [AvaloniaFact]
    public async Task Representative_page_states_render_at_both_sizes_in_light_and_dark()
    {
        string? captureDirectory = Environment.GetEnvironmentVariable("MUSIC_LIBRARY_MANAGER_CAPTURE_DIR");
        ThemeVariant? previousTheme = Application.Current!.RequestedThemeVariant;
        LibraryRow[] fixture =
        [
            new(new TrackRecord
            {
                Path = @"X:\Fixture\Aurora.flac",
                Title = "Aurora",
                Artist = "The Fixtures",
                Album = "Deterministic Data",
                CodecName = "FLAC",
            }),
            new(new TrackRecord
            {
                Path = @"X:\Fixture\Harbor.mp3",
                Title = "Harbor",
                Artist = "The Fixtures",
                Album = "Deterministic Data",
                CodecName = "MP3",
            }),
        ];

        try
        {
            foreach ((string themeName, ThemeVariant theme) in new[]
                     {
                         ("light", ThemeVariant.Light),
                         ("dark", ThemeVariant.Dark),
                     })
            foreach ((int width, int height) in new[] { (1440, 900), (900, 600) })
            {
                Application.Current.RequestedThemeVariant = theme;
                using ServiceProvider services = BuildIsolatedServices(
                    fixture.Select(row => row.Record).ToArray());
                App.UseServicesForTests(services);
                MainWindow window = services.GetRequiredService<MainWindow>();
                INavigationService navigation = services.GetRequiredService<INavigationService>();
                IActivityService activities = services.GetRequiredService<IActivityService>();
                DialogService dialogs = services.GetRequiredService<DialogService>();
                try
                {
                    window.Show();
                    window.WindowState = WindowState.Normal;
                    window.Width = width;
                    window.Height = height;
                    window.Activate();
                    navigation.Navigate(ShellDestination.Library);
                    Dispatcher.UIThread.RunJobs();

                    LibraryView view = Assert.IsType<LibraryView>(
                        window.FindControl<ContentControl>("ContentHost")!.Content);
                    LibraryViewModel viewModel = Assert.IsType<LibraryViewModel>(view.DataContext);
                    viewModel.IsInspectorOpen = false;

                    viewModel.Rows = [];
                    viewModel.StatusText = "Choose a fixture configuration.";
                    viewModel.PageState = LibraryPageState.NoConfiguration;
                    RenderAndCapture("empty");
                    Assert.True(view.FindControl<Border>("LibraryEmptyState")!.IsVisible);

                    await viewModel.ReloadAsync();
                    viewModel.Indexing.StatusText = "Fixture index is current.";
                    RenderAndCapture("populated");
                    Assert.Equal(2, view.FindControl<AppDataGrid>("LibraryGrid")!
                        .ItemsSource!.Cast<object>().Count());

                    Guid activity = activities.Start(
                        "Index fixture library", "Reading 2 deterministic tracks",
                        ShellDestination.Library, () => { });
                    viewModel.IsBusy = true;
                    viewModel.PageState = LibraryPageState.Loading;
                    viewModel.Indexing.StatusText = "Indexing fixture library…";
                    RenderAndCapture("busy");
                    Assert.True(window.FindControl<Border>("ActivityBanner")!.IsVisible);
                    viewModel.IsBusy = false;
                    activities.Finish(activity, "Fixture index complete", AppActivityState.Completed);
                    activities.Dismiss(activity);

                    viewModel.Rows = [];
                    viewModel.StatusText = "The fixture cache could not be opened.";
                    viewModel.PageState = LibraryPageState.Error;
                    viewModel.Indexing.StatusText = "Last fixture index failed.";
                    RenderAndCapture("error");
                    Assert.Contains("could not be opened", viewModel.EmptyStateMessage);

                    Task<bool> pending = dialogs.ConfirmAsync(
                        "Apply fixture changes?",
                        "Move 2 files. A recovery journal will be retained for 30 days.",
                        "Apply");
                    Dispatcher.UIThread.RunJobs();
                    DialogHost dialogHost = window.GetVisualDescendants().OfType<DialogHost>().Single();
                    Assert.True(dialogHost.IsVisible);
                    dialogHost.InvalidateMeasure();
                    dialogHost.InvalidateArrange();
                    dialogHost.InvalidateVisual();
                    window.InvalidateVisual();
                    RenderAndCapture("dialog");
                    dialogs.Complete(false);
                    Assert.False(await pending);

                    void RenderAndCapture(string state)
                    {
                        Dispatcher.UIThread.RunJobs();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                        using var frame = window.GetLastRenderedFrame();
                        Assert.NotNull(frame);
                        Assert.Equal(width, frame.PixelSize.Width);
                        Assert.Equal(height, frame.PixelSize.Height);
                        if (string.IsNullOrWhiteSpace(captureDirectory))
                            return;
                        Directory.CreateDirectory(captureDirectory);
                        frame.Save(Path.Combine(captureDirectory,
                            $"{themeName}-{width}x{height}-state-{state}.png"),
                            PngBitmapEncoderOptions.Default);
                    }
                }
                finally
                {
                    window.Hide();
                }
            }
        }
        finally
        {
            Application.Current.RequestedThemeVariant = previousTheme;
        }
    }

    [AvaloniaFact]
    public async Task Launch_time_search_loads_artwork_for_every_realized_library_row()
    {
        TrackRecord[] records = Enumerable.Range(0, 40).Select(index => new TrackRecord
        {
            Path = $@"X:\Fixture\Track {index:00}.flac",
            Title = $"Track {index:00}",
            Artist = "The Fixtures",
            Album = "Launch Search",
            CodecName = "FLAC",
        }).ToArray();
        using ServiceProvider services = BuildIsolatedServices(records);
        App.UseServicesForTests(services);
        MainWindow window = services.GetRequiredService<MainWindow>();
        LibraryViewModel viewModel = services.GetRequiredService<LibraryViewModel>();
        try
        {
            window.Show();
            window.Width = 1440;
            window.Height = 900;
            window.Activate();

            // This is the launch race: filtering navigates to Library while its first cached-row
            // load and the DataGrid's initial layout are both still being scheduled.
            viewModel.SetGlobalFilter("Track");
            for (int attempt = 0; attempt < 40 && viewModel.Rows.Count != records.Length; attempt++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                await Task.Delay(5);
            }

            LibraryView library = Assert.IsType<LibraryView>(
                window.FindControl<ContentControl>("ContentHost")!.Content);
            AppDataGrid grid = library.FindControl<AppDataGrid>("LibraryGrid")!;
            LibraryRow[] realized = [];
            for (int attempt = 0; attempt < 40; attempt++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                realized = grid.GetVisualDescendants().OfType<DataGridRow>()
                    .Select(row => row.DataContext).OfType<LibraryRow>().Distinct().ToArray();
                if (realized.Length > 1 && realized.All(row => row.ThumbnailLoaded))
                    break;
                await Task.Delay(5);
            }

            Assert.True(realized.Length > 1);
            Assert.All(realized, row => Assert.True(row.ThumbnailLoaded, row.Path));
        }
        finally
        {
            window.Hide();
        }
    }

    private static ServiceProvider BuildIsolatedServices(
        IReadOnlyList<TrackRecord>? records = null,
        Action<IServiceCollection>? configureServices = null) =>
        Composition.BuildServices(services =>
        {
            services.AddSingleton<IAppSettings>(new FakeSettings());
            if (records is not null)
                services.AddSingleton<ILibraryService>(new FixtureLibraryService(records));
            configureServices?.Invoke(services);
        });

    private static double ContrastRatio(Color first, Color second)
    {
        double firstLuminance = RelativeLuminance(first);
        double secondLuminance = RelativeLuminance(second);
        double lighter = Math.Max(firstLuminance, secondLuminance);
        double darker = Math.Min(firstLuminance, secondLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color) =>
        0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

    private static double Linear(byte channel)
    {
        double value = channel / 255d;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private sealed class FakeSettings : IAppSettings
    {
        private readonly Dictionary<string, string> _preferences = [];
        public string? ConfigPath => null;
        public LibraryConfiguration? Configuration => null;
        public event EventHandler? ConfigurationChanged;
        public AppConfigurationSnapshot GetSnapshot() => new(null, null, 0);
        public void LoadConfig(string path) => ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        public string? GetRememberedConfigPath() => null;
        public IReadOnlyList<string> RecentConfigPaths => [];
        public void ClearRecentConfigs() { }
        public string? GetPreference(string key) => _preferences.GetValueOrDefault(key);
        public void SetPreference(string key, string? value)
        {
            if (value is null)
                _preferences.Remove(key);
            else
                _preferences[key] = value;
        }
    }

    private sealed class FieldsDialogMediaService : IMediaFileService
    {
        public Task<OperationResult<MediaFileModel>> LoadAsync(
            string path,
            CancellationToken ct = default) => LoadAsync(path, includeArtwork: true, ct);

        public Task<OperationResult<MediaFileModel>> LoadAsync(
            string path,
            bool includeArtwork,
            CancellationToken ct = default) =>
            Task.FromResult(OperationResult<MediaFileModel>.Ok(new MediaFileModel
            {
                Path = path,
                IsWritable = true,
                KnownFields = [new TagFieldValue(TagFields.Title, "Original title")],
            }));
    }

    private sealed class FixtureLibraryService(IReadOnlyList<TrackRecord> records) : ILibraryService
    {
        public bool IsReady => true;

        public Task<(int Added, int Modified, int Removed, int Unchanged)> IndexAsync(
            IProgress<IndexProgress>? progress = null, CancellationToken ct = default) =>
            Task.FromResult((records.Count, 0, 0, 0));

        public Task<LibrarySnapshot> BuildSnapshotAsync(
            LibraryGrouping grouping = LibraryGrouping.AlbumArtist, CancellationToken ct = default) =>
            Task.FromException<LibrarySnapshot>(new NotSupportedException());

        public Task<IReadOnlyList<TrackRecord>> GetAllRecordsAsync(CancellationToken ct = default) =>
            Task.FromResult(records);

        public Task<AnalysisReport> CheckSetsAsync(CancellationToken ct = default) =>
            Task.FromException<AnalysisReport>(new NotSupportedException());

        public Task<FileDetails?> GetFileDetailsAsync(
            string path, bool includeArtwork, CancellationToken ct = default) =>
            Task.FromResult<FileDetails?>(null);

        public Task<byte[]?> GetFirstImageAsync(string path, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(null);

        public Task<IReadOnlyList<byte[]?>> GetFirstImagesAsync(
            IReadOnlyList<string> paths, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<byte[]?>>(paths.Select(_ => (byte[]?)null).ToArray());

        public Task<IReadOnlyList<string>> GetImageSignaturesAsync(
            IReadOnlyList<string> paths, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(paths.Select(_ => "").ToArray());
    }
}
