using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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

public sealed class SettingsRuleSummaryCardTests
{
    [AvaloniaFact]
    public async Task Nested_rule_duplication_preserves_values_and_assigns_unique_stable_ids()
    {
        using ServiceProvider services =
            BuildServices();
        App.UseServicesForTests(services);
        SettingsViewModel model =
            services.GetRequiredService<
                SettingsViewModel>();
        await model.NewConfigurationCommand
            .ExecuteAsync(null);

        LibraryProfileEditorRow profile =
            Assert.IsType<
                LibraryProfileEditorRow>(
                model.AdvancedProfile);
        profile.SidecarRules.Clear();
        SidecarRuleEditorRow sidecar =
            SidecarRuleEditorRow.Create();
        sidecar.Id = "cover-art";
        sidecar.Name = "  Cover art  ";
        sidecar.Patterns =
            "  cover.* ; folder.* ,  ";
        sidecar.Enabled = false;
        sidecar.Disposition =
            LibrarySidecarDisposition
                .Quarantine;
        profile.SidecarRules.Add(sidecar);

        model.DuplicateSidecarRuleCommand
            .Execute(sidecar);
        model.DuplicateSidecarRuleCommand
            .Execute(sidecar);

        Assert.Equal(
            3,
            profile.SidecarRules.Count);
        Assert.Equal(
            3,
            profile.SidecarRules
                .Select(rule => rule.Id)
                .Distinct(
                    StringComparer
                        .OrdinalIgnoreCase)
                .Count());
        SidecarRuleEditorRow[] sidecarCopies =
        [
            .. profile.SidecarRules
                .Where(rule =>
                    !ReferenceEquals(
                        rule,
                        sidecar)),
        ];
        Assert.Equal(
            ["cover-art-copy-2",
             "cover-art-copy"],
            sidecarCopies
                .Select(rule => rule.Id));
        Assert.All(
            sidecarCopies,
            copy =>
            {
                Assert.NotSame(
                    sidecar,
                    copy);
                Assert.Equal(
                    sidecar.Name +
                    services
                        .GetRequiredService<
                            ILocalizationService>()
                        .Get(
                            "Settings.Profile.CopySuffix"),
                    copy.Name);
                Assert.Equal(
                    sidecar.Patterns,
                    copy.Patterns);
                Assert.Equal(
                    sidecar.Enabled,
                    copy.Enabled);
                Assert.Equal(
                    sidecar.Disposition,
                    copy.Disposition);
            });

        IngestProfileEditorRow
            ingestProfile =
                Assert.IsType<
                    IngestProfileEditorRow>(
                    model.AdvancedIngestProfile);
        ingestProfile.Recipes.Clear();
        IngestRecipeEditorRow recipe =
            IngestRecipeEditorRow.Create();
        recipe.Id = "hires";
        recipe.Name = "  High resolution  ";
        recipe.Enabled = true;
        recipe.InputExtensions =
            "  .flac ; .wav,\t.dsf  ";
        recipe.RequireLossless = false;
        recipe.InputChannelChoice =
            SettingsChoiceLists.ChannelChoice(
                LibraryChannelSelection.Multi);
        recipe.MatchAnyQualityMinimum =
            true;
        recipe.AlbumCondition =
            LibraryIngestAlbumCondition
                .HasHighResolution;
        recipe.SourceSelection =
            LibraryIngestSourceSelection
                .PreferCdQuality;
        recipe.RequireFallbackApproval =
            true;
        recipe.Action =
            LibraryIngestAction.Copy;
        recipe.MinimumSampleRateHz =
            96_000;
        recipe.MinimumBitsPerSample =
            24;
        var destination =
            new SettingsRootChoice(
                Guid.NewGuid(),
                "  Uncommitted root  ");
        recipe.DestinationRootChoices.Add(
            destination);
        recipe.DestinationRootChoice =
            destination;
        recipe.OutputExtension =
            "  .wv  ";
        recipe.Codec = "  wavpack  ";
        recipe.Encoder =
            "  wavpack-cli  ";
        recipe.ExtraFfmpegOptions =
            "  -custom-option value  ";
        recipe.AddToMediaCatalog = true;
        recipe.BitrateKbps = 640;
        recipe.SampleRateHz = 88_200;
        recipe.BitsPerSample = 32;
        recipe.TranscodeFormatId =
            AudioTranscodeFormatIds.WavPack;
        recipe.TranscodeEncoderId =
            AudioTranscodeEncoderIds
                .WavPackCli;
        recipe.TranscodeRateMode =
            AudioTranscodeRateMode
                .HybridQuality;
        recipe.TranscodeQuality = 7.25;
        recipe.TranscodeCompressionEffort =
            9;
        recipe.TranscodeCreateCorrectionFile =
            true;
        recipe.OutputChannelChoice =
            SettingsChoiceLists.ChannelChoice(
                LibraryChannelSelection.Multi);
        recipe.PreserveMetadata = false;
        recipe.PreserveArtwork = true;
        recipe.UseProfileCollision =
            true;
        recipe.CollisionPolicy =
            LibraryPathCollisionPolicy.Suffix;
        ingestProfile.Recipes.Add(recipe);

        model.DuplicateIngestRecipeCommand
            .Execute(recipe);
        model.DuplicateIngestRecipeCommand
            .Execute(recipe);

        Assert.Equal(
            3,
            ingestProfile.Recipes.Count);
        Assert.Equal(
            3,
            ingestProfile.Recipes
                .Select(item => item.Id)
                .Distinct(
                    StringComparer
                        .OrdinalIgnoreCase)
                .Count());
        IngestRecipeEditorRow[]
            recipeCopies =
            [
                .. ingestProfile.Recipes
                    .Where(item =>
                        !ReferenceEquals(
                            item,
                            recipe)),
            ];
        Assert.Equal(
            ["hires-copy-2",
             "hires-copy"],
            recipeCopies
                .Select(item => item.Id));
        Assert.All(
            recipeCopies,
            copy =>
            {
                Assert.NotSame(
                    recipe,
                    copy);
                Assert.Equal(
                    recipe.Name +
                    services
                        .GetRequiredService<
                            ILocalizationService>()
                        .Get(
                            "Settings.Profile.CopySuffix"),
                    copy.Name);
                Assert.Equal(
                    recipe.Enabled,
                    copy.Enabled);
                Assert.Equal(
                    recipe.InputExtensions,
                    copy.InputExtensions);
                Assert.Equal(
                    recipe.RequireLossless,
                    copy.RequireLossless);
                Assert.Equal(
                    recipe.MinimumSampleRateHz,
                    copy.MinimumSampleRateHz);
                Assert.Equal(
                    recipe.MinimumBitsPerSample,
                    copy.MinimumBitsPerSample);
                Assert.Equal(
                    recipe.InputChannelChoice,
                    copy.InputChannelChoice);
                Assert.Equal(
                    recipe.MatchAnyQualityMinimum,
                    copy.MatchAnyQualityMinimum);
                Assert.Equal(
                    recipe.AlbumCondition,
                    copy.AlbumCondition);
                Assert.Equal(
                    recipe.SourceSelection,
                    copy.SourceSelection);
                Assert.Equal(
                    recipe.RequireFallbackApproval,
                    copy.RequireFallbackApproval);
                Assert.Equal(
                    recipe.Action,
                    copy.Action);
                Assert.Equal(
                    recipe.DestinationRootId,
                    copy.DestinationRootId);
                Assert.Equal(
                    recipe.DestinationRootChoice,
                    copy.DestinationRootChoice);
                Assert.Equal(
                    recipe.OutputExtension,
                    copy.OutputExtension);
                Assert.Equal(
                    recipe.Codec,
                    copy.Codec);
                Assert.Equal(
                    recipe.Encoder,
                    copy.Encoder);
                Assert.Equal(
                    recipe.ExtraFfmpegOptions,
                    copy.ExtraFfmpegOptions);
                Assert.Equal(
                    recipe.AddToMediaCatalog,
                    copy.AddToMediaCatalog);
                Assert.Equal(
                    recipe.BitrateKbps,
                    copy.BitrateKbps);
                Assert.Equal(
                    recipe.SampleRateHz,
                    copy.SampleRateHz);
                Assert.Equal(
                    recipe.BitsPerSample,
                    copy.BitsPerSample);
                Assert.Equal(
                    recipe.TranscodeFormatId,
                    copy.TranscodeFormatId);
                Assert.Equal(
                    recipe.TranscodeEncoderId,
                    copy.TranscodeEncoderId);
                Assert.Equal(
                    recipe.TranscodeRateMode,
                    copy.TranscodeRateMode);
                Assert.Equal(
                    recipe.TranscodeQuality,
                    copy.TranscodeQuality);
                Assert.Equal(
                    recipe.TranscodeCompressionEffort,
                    copy.TranscodeCompressionEffort);
                Assert.Equal(
                    recipe.TranscodeCreateCorrectionFile,
                    copy.TranscodeCreateCorrectionFile);
                Assert.Equal(
                    recipe.OutputChannelChoice,
                    copy.OutputChannelChoice);
                Assert.Equal(
                    recipe.PreserveMetadata,
                    copy.PreserveMetadata);
                Assert.Equal(
                    recipe.PreserveArtwork,
                    copy.PreserveArtwork);
                Assert.Equal(
                    recipe.UseProfileCollision,
                    copy.UseProfileCollision);
                Assert.Equal(
                    recipe.CollisionPolicy,
                    copy.CollisionPolicy);
                AssertChoiceValuesAreUnique(
                    copy.TranscodeEncoderChoices);
                AssertChoiceValuesAreUnique(
                    copy.TranscodeRateModeChoices);
                Assert.Equal(
                    recipe.TranscodeEncoderChoices
                        .Select(choice =>
                            choice.Value),
                    copy.TranscodeEncoderChoices
                        .Select(choice =>
                            choice.Value));
                Assert.Equal(
                    recipe.TranscodeRateModeChoices
                        .Select(choice =>
                            choice.Value),
                    copy.TranscodeRateModeChoices
                        .Select(choice =>
                            choice.Value));
            });

        IngestRecipeEditorRow activatedCopy =
            recipeCopies[0];
        activatedCopy.Action =
            LibraryIngestAction.Transcode;
        LibraryIngestRecipe activatedRecipe =
            activatedCopy.Build();
        Assert.Equal(
            recipe.TranscodeFormatId,
            activatedRecipe.TranscodeFormatId);
        Assert.Equal(
            recipe.TranscodeEncoderId,
            activatedRecipe.TranscodeEncoderId);
        Assert.Equal(
            recipe.TranscodeRateMode.ToString(),
            activatedRecipe.TranscodeRateMode);
        Assert.Equal(
            recipe.TranscodeQuality,
            activatedRecipe.TranscodeQuality);
        Assert.Equal(
            recipe.TranscodeCompressionEffort,
            activatedRecipe
                .TranscodeCompressionEffort);
        Assert.True(
            activatedRecipe
                .TranscodeCreateCorrectionFile);
    }

    [AvaloniaFact]
    public async Task Rule_removal_menu_restores_focus_and_releases_its_lifecycle()
    {
        using ServiceProvider services =
            BuildServices();
        App.UseServicesForTests(services);
        var view = new SettingsView();
        var window = new Window
        {
            Width = 600,
            Height = 600,
            Content = view,
        };
        try
        {
            window.Show();
            window.Activate();
            SettingsViewModel model =
                Assert.IsType<
                    SettingsViewModel>(
                    view.DataContext);
            await model.NewConfigurationCommand
                .ExecuteAsync(null);

            LibraryProfileEditorRow profile =
                Assert.IsType<
                    LibraryProfileEditorRow>(
                    model.AdvancedProfile);
            profile.SidecarRules.Clear();
            SidecarRuleEditorRow firstSidecar =
                SidecarRuleEditorRow.Create();
            firstSidecar.Name = "First sidecar";
            SidecarRuleEditorRow nextSidecar =
                SidecarRuleEditorRow.Create();
            nextSidecar.Name = "Next sidecar";
            profile.SidecarRules.Add(
                firstSidecar);
            profile.SidecarRules.Add(
                nextSidecar);

            TabControl tabs =
                view.FindControl<TabControl>(
                    "SettingsTabs")!;
            tabs.SelectedIndex = 5;
            view.FindControl<Expander>(
                    "RootPolicyEditorExpander")!
                .IsExpanded = true;
            view.FindControl<Expander>(
                    "RootPolicyAdvancedExpander")!
                .IsExpanded = true;
            Render();

            Border firstCard =
                FindCard(
                    view,
                    "SidecarRuleCard",
                    firstSidecar);
            Border nextCard =
                FindCard(
                    view,
                    "SidecarRuleCard",
                    nextSidecar);
            Button firstMore =
                FindNamed<Button>(
                    firstCard,
                    "SidecarRuleMoreButton");
            Button nextEdit =
                FindNamed<Button>(
                    nextCard,
                    "EditSidecarRuleButton");
            MenuFlyout sidecarFlyout =
                OpenMenu(
                    window,
                    firstMore);
            Assert.Equal(
                1,
                view.ActiveRuleRemovalMenuCount);
            MenuItem removeSidecar =
                Assert.Single(
                    sidecarFlyout.Items
                        .OfType<MenuItem>());

            removeSidecar.Command!.Execute(
                removeSidecar.CommandParameter);
            sidecarFlyout.Hide();
            Render();

            Assert.Equal(
                [nextSidecar],
                profile.SidecarRules);
            Assert.Same(
                nextEdit,
                window.FocusManager?
                    .GetFocusedElement());
            Assert.Equal(
                0,
                view.ActiveRuleRemovalMenuCount);

            IngestProfileEditorRow
                ingestProfile =
                    Assert.IsType<
                        IngestProfileEditorRow>(
                        model
                            .AdvancedIngestProfile);
            ingestProfile.Recipes.Clear();
            IngestRecipeEditorRow recipe =
                IngestRecipeEditorRow.Create();
            recipe.Name = "Only recipe";
            ingestProfile.Recipes.Add(
                recipe);
            tabs.SelectedIndex = 6;
            view.FindControl<Expander>(
                    "IngestProfileEditorExpander")!
                .IsExpanded = true;
            Render();

            Border recipeCard =
                FindCard(
                    view,
                    "IngestRecipeCard",
                    recipe);
            Button recipeMore =
                FindNamed<Button>(
                    recipeCard,
                    "IngestRecipeMoreButton");
            Button addRecipe =
                view.FindControl<Button>(
                    "AddIngestRecipeButton")!;
            MenuFlyout recipeFlyout =
                OpenMenu(
                    window,
                    recipeMore);
            Assert.Equal(
                1,
                view.ActiveRuleRemovalMenuCount);
            MenuItem removeRecipe =
                Assert.Single(
                    recipeFlyout.Items
                        .OfType<MenuItem>());

            removeRecipe.Command!.Execute(
                removeRecipe.CommandParameter);
            recipeFlyout.Hide();
            Render();

            Assert.Empty(
                ingestProfile.Recipes);
            Assert.Same(
                addRecipe,
                window.FocusManager?
                    .GetFocusedElement());
            Assert.Equal(
                0,
                view.ActiveRuleRemovalMenuCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Settings_rule_cards_preserve_semantic_wide_and_narrow_command_layouts()
    {
        using ServiceProvider services =
            BuildServices();
        App.UseServicesForTests(services);
        ILocalizationService localization =
            services.GetRequiredService<
                ILocalizationService>();
        var view = new SettingsView();
        var window = new Window
        {
            Width = 600,
            Height = 600,
            Content = view,
        };
        try
        {
            window.Show();
            window.Activate();
            SettingsViewModel model =
                Assert.IsType<
                    SettingsViewModel>(
                    view.DataContext);
            await model.NewConfigurationCommand
                .ExecuteAsync(null);

            LibraryProfileEditorRow profile =
                Assert.IsType<
                    LibraryProfileEditorRow>(
                    model.AdvancedProfile);
            profile.SidecarRules.Clear();
            SidecarRuleEditorRow sidecar =
                SidecarRuleEditorRow.Create();
            sidecar.Id = "lyrics";
            sidecar.Name =
                "Lyrics sidecars";
            sidecar.Patterns =
                "*.lrc; lyrics/**";
            profile.SidecarRules.Add(
                sidecar);

            IngestProfileEditorRow
                ingestProfile =
                    Assert.IsType<
                        IngestProfileEditorRow>(
                        model
                            .AdvancedIngestProfile);
            ingestProfile.Recipes.Clear();
            IngestRecipeEditorRow recipe =
                IngestRecipeEditorRow
                    .Create();
            recipe.Id = "lossless";
            recipe.Name =
                "Lossless originals";
            recipe.InputExtensions =
                ".flac, .wav";
            ingestProfile.Recipes.Add(
                recipe);

            TabControl tabs =
                view.FindControl<TabControl>(
                    "SettingsTabs")!;
            tabs.SelectedIndex = 5;
            view.FindControl<Expander>(
                    "RootPolicyEditorExpander")!
                .IsExpanded = true;
            view.FindControl<Expander>(
                    "RootPolicyAdvancedExpander")!
                .IsExpanded = true;
            Render();

            Border sidecarCard =
                FindCard(
                    view,
                    "SidecarRuleCard",
                    sidecar);
            AssertCardContract(
                sidecarCard,
                "EditSidecarRuleButton",
                "DuplicateSidecarRuleButton",
                "SidecarRuleMoreButton",
                "SidecarRuleEditorExpander",
                sidecar,
                localization,
                window);
            AssertSummaryCommandBreakpoint(
                window,
                view,
                sidecarCard,
                "EditSidecarRuleButton",
                "DuplicateSidecarRuleButton",
                "SidecarRuleMoreButton");
            ResizeSettingsPageToWidth(
                window,
                view,
                SettingsView
                    .RuleSummaryInlineCommandWidth -
                1);

            Button sidecarDuplicate =
                FindNamed<Button>(
                    sidecarCard,
                    "DuplicateSidecarRuleButton");
            sidecarDuplicate.Command!
                .Execute(
                    sidecarDuplicate
                        .CommandParameter);
            Assert.Equal(
                2,
                profile.SidecarRules.Count);
            Assert.False(
                string.Equals(
                    sidecar.Id,
                    profile.SidecarRules[1]
                        .Id,
                    StringComparison
                        .OrdinalIgnoreCase));

            tabs.SelectedIndex = 6;
            view.FindControl<Expander>(
                    "IngestProfileEditorExpander")!
                .IsExpanded = true;
            Render();

            Border recipeCard =
                FindCard(
                    view,
                    "IngestRecipeCard",
                    recipe);
            AssertCardContract(
                recipeCard,
                "EditIngestRecipeButton",
                "DuplicateIngestRecipeButton",
                "IngestRecipeMoreButton",
                "IngestRecipeEditorExpander",
                recipe,
                localization,
                window);
            AssertSummaryCommandBreakpoint(
                window,
                view,
                recipeCard,
                "EditIngestRecipeButton",
                "DuplicateIngestRecipeButton",
                "IngestRecipeMoreButton");

            Button recipeDuplicate =
                FindNamed<Button>(
                    recipeCard,
                    "DuplicateIngestRecipeButton");
            recipeDuplicate.Command!
                .Execute(
                    recipeDuplicate
                        .CommandParameter);
            Assert.Equal(
                2,
                ingestProfile
                    .Recipes.Count);
            Assert.False(
                string.Equals(
                    recipe.Id,
                    ingestProfile.Recipes[1]
                        .Id,
                    StringComparison
                        .OrdinalIgnoreCase));
        }
        finally
        {
            window.Hide();
        }
    }

    [AvaloniaFact]
    public async Task Ingest_correction_control_is_contextual_and_invalid_saved_values_remain_repairable()
    {
        using ServiceProvider services =
            BuildServices();
        App.UseServicesForTests(services);
        var view = new SettingsView();
        var window = new Window
        {
            Width = 900,
            Height = 600,
            Content = view,
        };
        try
        {
            window.Show();
            SettingsViewModel model =
                Assert.IsType<
                    SettingsViewModel>(
                    view.DataContext);
            await model.NewConfigurationCommand
                .ExecuteAsync(null);
            IngestProfileEditorRow profile =
                Assert.IsType<
                    IngestProfileEditorRow>(
                    model.AdvancedIngestProfile);
            profile.Recipes.Clear();
            IngestRecipeEditorRow recipe =
                IngestRecipeEditorRow.Create();
            recipe.Action =
                LibraryIngestAction.Transcode;
            recipe.TranscodeFormatId =
                AudioTranscodeFormatIds.WavPack;
            recipe.TranscodeEncoderId =
                AudioTranscodeEncoderIds
                    .WavPackCli;
            recipe.TranscodeRateMode =
                AudioTranscodeRateMode.Lossless;
            recipe.TranscodeCreateCorrectionFile =
                true;
            AudioTranscodeCapabilitySnapshot
                correctionCapabilities =
                new(
                    [],
                    [
                        new(
                            AudioTranscodeFormatIds
                                .WavPack,
                            "wavpack",
                            "wv",
                            ".wv",
                            true,
                            [
                                AudioTranscodeEncoderIds
                                    .WavPackCli,
                            ]),
                    ],
                    [
                        new(
                            AudioTranscodeEncoderIds
                                .WavPackCli,
                            AudioTranscodeToolKind
                                .WavPack,
                            "wavpack",
                            AudioEncoderThreadingMode
                                .SingleThreaded,
                            [
                                new(
                                    AudioTranscodeRateMode
                                        .Lossless),
                                new(
                                    AudioTranscodeRateMode
                                        .HybridBitrate,
                                    200,
                                    960,
                                    SupportsCorrectionFile:
                                        true),
                            ],
                            [],
                            [16, 24],
                            SupportsCorrectionFile:
                                true),
                    ],
                    DateTimeOffset.UtcNow,
                    1);
            recipe.ApplyTranscodeCapabilities(
                correctionCapabilities,
                services.GetRequiredService<
                    ILocalizationService>());
            profile.Recipes.Add(recipe);
            view.FindControl<TabControl>(
                    "SettingsTabs")!
                .SelectedIndex = 6;
            view.FindControl<Expander>(
                    "IngestProfileEditorExpander")!
                .IsExpanded = true;
            Render();
            Border card =
                FindCard(
                    view,
                    "IngestRecipeCard",
                    recipe);
            FindNamed<Expander>(
                    card,
                    "IngestRecipeEditorExpander")
                .IsExpanded = true;
            Render();
            FindNamed<Expander>(
                    card,
                    "IngestRecipeAdvancedExpander")
                .IsExpanded = true;
            Render();

            string correctionLabel =
                services.GetRequiredService<
                        ILocalizationService>()
                    .Get(
                        "Transcode.Correction.Create");
            CheckBox checkBox =
                Assert.Single(
                    card.GetVisualDescendants()
                        .OfType<CheckBox>(),
                    control =>
                        ReferenceEquals(
                            control.DataContext,
                            recipe) &&
                        Equals(
                            control.Content,
                            correctionLabel));
            StackPanel option =
                Assert.IsType<StackPanel>(
                    checkBox.Parent);
            Assert.True(
                option.IsEffectivelyVisible,
                $"Option visible={option.IsVisible}; action={recipe.Action}; requested={recipe.TranscodeCreateCorrectionFile}; supported={recipe.IsTranscodeCorrectionFileSupported}; editor expanded={FindNamed<Expander>(card, "IngestRecipeEditorExpander").IsExpanded}; advanced expanded={FindNamed<Expander>(card, "IngestRecipeAdvancedExpander").IsExpanded}.");
            Assert.True(
                checkBox.IsEffectivelyEnabled,
                $"Checkbox enabled={checkBox.IsEnabled}; option effective={option.IsEffectivelyVisible}.");
            Assert.True(
                checkBox.IsChecked,
                $"Checkbox value={checkBox.IsChecked}; requested={recipe.TranscodeCreateCorrectionFile}.");
            Assert.Equal(
                services.GetRequiredService<
                        ILocalizationService>()
                    .Get(
                        "Transcode.Issue.CorrectionUnavailable"),
                AutomationProperties
                    .GetHelpText(checkBox));

            recipe.TranscodeCreateCorrectionFile =
                false;
            Render();
            Assert.False(
                option.IsEffectivelyVisible);

            recipe.ApplyTranscodeCapabilities(
                correctionCapabilities,
                services.GetRequiredService<
                    ILocalizationService>());
            recipe.TranscodeRateMode =
                AudioTranscodeRateMode.HybridBitrate;
            Render();
            Assert.True(
                option.IsEffectivelyVisible);
            Assert.True(
                checkBox.IsEffectivelyEnabled);
            Assert.Equal(
                services.GetRequiredService<
                        ILocalizationService>()
                    .Get(
                        "Transcode.Correction.Help"),
                AutomationProperties
                    .GetHelpText(checkBox));

            recipe.TranscodeCreateCorrectionFile =
                true;
            recipe.TranscodeRateMode =
                AudioTranscodeRateMode.Lossless;
            Render();
            Assert.False(
                recipe
                    .TranscodeCreateCorrectionFile);
            Assert.False(
                option.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertCardContract(
        Border card,
        string editName,
        string duplicateName,
        string moreName,
        string editorName,
        object row,
        ILocalizationService
            localization,
        Window window)
    {
        Button edit =
            FindNamed<Button>(
                card,
                editName);
        Button duplicate =
            FindNamed<Button>(
                card,
                duplicateName);
        Button more =
            FindNamed<Button>(
                card,
                moreName);
        Expander editor =
            FindNamed<Expander>(
                card,
                editorName);
        Assert.False(editor.IsExpanded);
        Assert.True(
            edit.IsEffectivelyVisible);
        Assert.True(
            duplicate
                .IsEffectivelyVisible);
        Assert.True(
            more.IsEffectivelyVisible);
        Assert.Equal(
            localization.Get(
                "Workbench.Navigation.Group.Edit"),
            edit.Content);
        Assert.Equal(
            localization.Get(
                "Settings.Action.Duplicate"),
            duplicate.Content);
        Assert.Equal(
            localization.Get(
                "Workbench.Action.MoreAutomation"),
            AutomationProperties
                .GetName(more));
        Grid commandGrid =
            edit.GetVisualAncestors()
                .OfType<Grid>()
                .First(grid =>
                    IsRuleSummaryCommandGrid(
                        grid));
        AssertSummaryCommandLayout(
            card,
            editName,
            duplicateName,
            moreName,
            stacked: true);
        Assert.True(
            commandGrid.Bounds.Width <=
            card.Bounds.Width + 1);

        edit.Focus();
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
        Assert.True(editor.IsExpanded);

        more.Focus();
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
                more.Flyout);
        Assert.True(flyout.IsOpen);
        MenuItem remove =
            Assert.Single(
                flyout.Items
                    .OfType<MenuItem>());
        Assert.Equal(
            localization.Get(
                "Settings.Action.Remove"),
            remove.Header);
        Assert.Contains(
            "danger",
            remove.Classes);
        Assert.NotNull(remove.Command);
        Assert.Same(
            row,
            remove.CommandParameter);
        flyout.Hide();
        Render();
        Assert.Same(
            more,
            window.FocusManager?
                .GetFocusedElement());
    }

    private static void AssertSummaryCommandLayout(
        Border card,
        string editName,
        string duplicateName,
        string moreName,
        bool stacked)
    {
        Button edit =
            FindNamed<Button>(
                card,
                editName);
        Button duplicate =
            FindNamed<Button>(
                card,
                duplicateName);
        Button more =
            FindNamed<Button>(
                card,
                moreName);
        Grid grid =
            edit.GetVisualAncestors()
                .OfType<Grid>()
                .First(candidate =>
                    IsRuleSummaryCommandGrid(
                        candidate));
        CheckBox enabled =
            Assert.Single(
                grid.Children
                    .OfType<CheckBox>());
        StackPanel summary =
            Assert.Single(
                grid.Children
                    .OfType<StackPanel>());

        Assert.Same(grid, duplicate.Parent);
        Assert.Same(grid, more.Parent);
        Assert.Equal(1, Grid.GetRowSpan(enabled));

        if (!stacked)
        {
            Assert.Equal(
                5,
                grid.ColumnDefinitions.Count);
            Assert.Empty(
                grid.RowDefinitions);
            Assert.True(
                grid.ColumnDefinitions[0]
                    .Width.IsAuto);
            Assert.True(
                grid.ColumnDefinitions[1]
                    .Width.IsStar);
            Assert.All(
                grid.ColumnDefinitions.Skip(2),
                column =>
                    Assert.True(
                        column.Width.IsAuto));
            Control[] controls =
            [
                enabled,
                summary,
                edit,
                duplicate,
                more,
            ];
            for (int index = 0;
                 index < controls.Length;
                 index++)
            {
                Assert.Equal(
                    0,
                    Grid.GetRow(
                        controls[index]));
                Assert.Equal(
                    index,
                    Grid.GetColumn(
                        controls[index]));
                Assert.Equal(
                    1,
                    Grid.GetColumnSpan(
                        controls[index]));
            }
            Assert.True(
                enabled.Bounds.Right <=
                summary.Bounds.Left + 1);
            Assert.True(
                summary.Bounds.Right <=
                edit.Bounds.Left + 1);
            Assert.True(
                edit.Bounds.Right <=
                duplicate.Bounds.Left + 1);
            Assert.True(
                duplicate.Bounds.Right <=
                more.Bounds.Left + 1);
            return;
        }

        Assert.Equal(
            3,
            grid.ColumnDefinitions.Count);
        Assert.Equal(
            2,
            grid.RowDefinitions.Count);
        Assert.True(
            grid.ColumnDefinitions[0]
                .Width.IsAuto);
        Assert.True(
            grid.ColumnDefinitions[1]
                .Width.IsStar);
        Assert.True(
            grid.ColumnDefinitions[2]
                .Width.IsAuto);
        Assert.Equal(0, Grid.GetRow(enabled));
        Assert.Equal(0, Grid.GetColumn(enabled));
        Assert.Equal(0, Grid.GetRow(summary));
        Assert.Equal(1, Grid.GetColumn(summary));
        Assert.Equal(
            2,
            Grid.GetColumnSpan(summary));
        Assert.Equal(1, Grid.GetRow(edit));
        Assert.Equal(0, Grid.GetColumn(edit));
        Assert.Equal(1, Grid.GetRow(duplicate));
        Assert.Equal(1, Grid.GetColumn(duplicate));
        Assert.Equal(1, Grid.GetRow(more));
        Assert.Equal(2, Grid.GetColumn(more));
        Assert.True(
            enabled.Bounds.Bottom <=
            edit.Bounds.Top + 1);
        Assert.True(
            summary.Bounds.Bottom <=
            duplicate.Bounds.Top + 1);
        Assert.True(
            edit.Bounds.Right <=
            duplicate.Bounds.Left + 1);
        Assert.True(
            duplicate.Bounds.Right <=
            more.Bounds.Left + 1);
    }

    private static void
        AssertSummaryCommandBreakpoint(
            Window window,
            SettingsView view,
            Border card,
            string editName,
            string duplicateName,
            string moreName)
    {
        double boundary =
            SettingsView
                .RuleSummaryInlineCommandWidth;
        (double PageWidth, bool Stacked)[]
            cases =
        [
            (boundary - 1, true),
            (boundary, false),
            (boundary + 1, false),
        ];
        foreach (
            (double pageWidth,
                bool stacked) in cases)
        {
            ResizeSettingsPageToWidth(
                window,
                view,
                pageWidth);
            AssertSummaryCommandLayout(
                card,
                editName,
                duplicateName,
                moreName,
                stacked);
        }
    }

    private static void
        ResizeSettingsPageToWidth(
            Window window,
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
            if (Math.Abs(difference) <
                0.01)
            {
                break;
            }

            window.Width = Math.Max(
                400,
                window.Width + difference);
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
                viewport
                    .GetVisualDescendants()
                    .OfType<StackPanel>(),
                panel =>
                    panel.Classes.Contains(
                        "settings-content"));
        return viewport.Bounds.Width -
            content.Margin.Left -
            content.Margin.Right;
    }

    private static Border FindCard(
        SettingsView view,
        string name,
        object row) =>
        Assert.Single(
            view.GetVisualDescendants()
                .OfType<Border>(),
            border =>
                border.Name == name &&
                ReferenceEquals(
                    border.DataContext,
                    row));

    private static T FindNamed<T>(
        Control root,
        string name)
        where T : Control =>
        Assert.Single(
            root.GetVisualDescendants()
                .OfType<T>(),
            control =>
                control.Name == name);

    private static bool IsRuleSummaryCommandGrid(
        Grid grid) =>
        grid.Name is
            "SidecarRuleSummaryCommandGrid" or
            "IngestRecipeSummaryCommandGrid";

    private static void AssertChoiceValuesAreUnique<T>(
        IEnumerable<LocalizedChoice<T>> choices)
    {
        T[] values =
        [
            .. choices.Select(choice =>
                choice.Value),
        ];
        Assert.Equal(
            values.Length,
            values.Distinct().Count());
    }

    private static MenuFlyout OpenMenu(
        Window window,
        Button button)
    {
        Assert.True(button.Focus());
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
                button.Flyout);
        Assert.True(flyout.IsOpen);
        return flyout;
    }

    private static ServiceProvider
        BuildServices()
    {
        var settings =
            new TestSettings();
        return Composition.BuildServices(
            collection =>
                collection.AddSingleton<
                    IAppSettings>(settings));
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
