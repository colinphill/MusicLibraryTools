using System.Globalization;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

[Collection(LocalizationTestCollection.Name)]
public sealed class ShortcutPlaylistLocalizationTests
{
    public static TheoryData<
        WorkbenchShortcutPlatform,
        string> ShortcutPlatforms =>
        new()
        {
            {
                WorkbenchShortcutPlatform.Windows,
                "Ctrl"
            },
            {
                WorkbenchShortcutPlatform.MacOS,
                "Meta"
            },
            {
                WorkbenchShortcutPlatform.Linux,
                "Ctrl"
            },
        };

    [Theory]
    [MemberData(nameof(ShortcutPlatforms))]
    public void Shortcut_platform_uses_native_primary_modifier_for_defaults_reservations_and_help(
        WorkbenchShortcutPlatform platform,
        string primaryModifier)
    {
        var localization =
            new SwitchingLocalizationService();
        var editor =
            new WorkbenchShortcutEditorViewModel(
                new RecordingShortcutStore(),
                localization: localization,
                platform: platform);

        Assert.Equal(platform, editor.Platform);
        Assert.Equal(
            primaryModifier,
            editor.PrimaryModifier);
        Assert.Equal(
            $"{primaryModifier}+Shift+P",
            editor.DefaultGesture);
        Assert.Equal(
            editor.DefaultGesture,
            editor.GestureText);
        Assert.Contains(
            $"{primaryModifier}+Alt+R",
            editor.GestureHelpText,
            StringComparison.Ordinal);
        Assert.Contains(
            $"{primaryModifier}+K",
            editor.InputWarningText,
            StringComparison.Ordinal);
        Assert.Contains(
            $"{primaryModifier}+I",
            editor.InputWarningText,
            StringComparison.Ordinal);

        if (platform ==
            WorkbenchShortcutPlatform.MacOS)
        {
            Assert.DoesNotContain(
                "Ctrl+",
                editor.GestureHelpText,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "Ctrl+",
                editor.InputWarningText,
                StringComparison.Ordinal);
        }
        string originalHelp =
            editor.GestureHelpText;
        string originalWarning =
            editor.InputWarningText;

        localization.SetCulture("fr-FR");

        Assert.NotEqual(
            originalHelp,
            editor.GestureHelpText);
        Assert.NotEqual(
            originalWarning,
            editor.InputWarningText);
        Assert.StartsWith(
            "fr-FR:",
            editor.GestureHelpText,
            StringComparison.Ordinal);
        Assert.Contains(
            $"{primaryModifier}+Alt+R",
            editor.GestureHelpText,
            StringComparison.Ordinal);
        Assert.Contains(
            $"{primaryModifier}+K",
            editor.InputWarningText,
            StringComparison.Ordinal);
        if (platform ==
            WorkbenchShortcutPlatform.MacOS)
            Assert.DoesNotContain(
                "Ctrl+",
                editor.GestureHelpText,
                StringComparison.Ordinal);

        editor.GestureText =
            $"{primaryModifier}+";
        Assert.StartsWith(
            "fr-FR:Workbench.Shortcuts.Validation.ModifierAndKeyExample",
            editor.GestureValidationMessage,
            StringComparison.Ordinal);

        editor.GestureText =
            $"{primaryModifier}+K";

        Assert.Contains(
            "reserved",
            editor.GestureValidationMessage,
            StringComparison.OrdinalIgnoreCase);

        string nonPrimaryModifier =
            primaryModifier == "Meta"
                ? "Ctrl"
                : "Meta";
        editor.GestureText =
            $"{nonPrimaryModifier}+K";

        Assert.Equal(
            "",
            editor.GestureValidationMessage);

        editor.NewShortcutCommand.Execute(null);

        Assert.Equal(
            editor.DefaultGesture,
            editor.GestureText);
    }

    [Fact]
    public void Shortcut_default_platform_matches_the_current_runtime()
    {
        WorkbenchShortcutPlatform expected =
            OperatingSystem.IsMacOS()
                ? WorkbenchShortcutPlatform.MacOS
                : OperatingSystem.IsWindows()
                    ? WorkbenchShortcutPlatform.Windows
                    : WorkbenchShortcutPlatform.Linux;

        var editor =
            new WorkbenchShortcutEditorViewModel();

        Assert.Equal(expected, editor.Platform);
    }

    [Fact]
    public void Every_command_label_refreshes_in_place_without_rewriting_semantic_bindings()
    {
        var localization =
            new SwitchingLocalizationService();
        WorkbenchShortcutCommand[] commands =
            Enum.GetValues<WorkbenchShortcutCommand>();
        WorkbenchShortcutBinding[] originalBindings =
            commands.Select(
                    (command, index) =>
                        new WorkbenchShortcutBinding(
                            Guid.Parse(
                                $"00000000-0000-0000-0000-{index + 1:D12}"),
                            $"Ctrl+Alt+F{index + 1}",
                            WorkbenchShortcutTargetKind.Command,
                            command,
                            TargetLabel:
                                $"legacy label {index + 1}"))
                .Append(
                    new WorkbenchShortcutBinding(
                        Guid.Parse(
                            "00000000-0000-0000-0000-999999999999"),
                        "Ctrl+Alt+R",
                        WorkbenchShortcutTargetKind.Recipe,
                        RecipeId: Guid.Parse(
                            "11111111-1111-1111-1111-111111111111"),
                        TargetLabel: "User recipe"))
                .ToArray();
        var store =
            new RecordingShortcutStore(
                originalBindings);
        var editor =
            new WorkbenchShortcutEditorViewModel(
                store,
                localization: localization,
                platform:
                    WorkbenchShortcutPlatform.Windows);
        WorkbenchShortcutCommandChoice[] choices =
            [.. editor.Commands];
        WorkbenchShortcutRow[] rows =
            [.. editor.Bindings];
        WorkbenchShortcutBinding[] rowBindings =
            rows.Select(row => row.Binding)
                .ToArray();
        editor.SelectedCommand =
            choices.Single(choice =>
                choice.Command ==
                WorkbenchShortcutCommand.Redo);

        Assert.Equal(
            commands,
            choices.Select(choice =>
                choice.Command));
        Assert.All(
            choices,
            choice => Assert.Equal(
                $"en-US:Workbench.Shortcuts.Command.{choice.Command}",
                choice.Label));
        Assert.All(
            rows.Take(commands.Length),
            row => Assert.Equal(
                $"en-US:Workbench.Shortcuts.Command.{row.Binding.Command}",
                row.Target));
        Assert.Equal(
            "User recipe",
            rows[^1].Target);

        localization.SetCulture("fr-FR");

        Assert.Equal(0, store.SaveCount);
        Assert.Equal(
            choices,
            editor.Commands);
        Assert.Equal(
            rows,
            editor.Bindings);
        Assert.Same(
            choices.Single(choice =>
                choice.Command ==
                WorkbenchShortcutCommand.Redo),
            editor.SelectedCommand);
        Assert.All(
            choices,
            choice => Assert.Equal(
                $"fr-FR:Workbench.Shortcuts.Command.{choice.Command}",
                choice.Label));
        for (int index = 0;
             index < rows.Length;
             index++)
        {
            Assert.Same(
                rowBindings[index],
                rows[index].Binding);
            Assert.Equal(
                originalBindings[index].Id,
                rows[index].Binding.Id);
            Assert.Equal(
                originalBindings[index].Gesture,
                rows[index].Gesture);
            Assert.Equal(
                originalBindings[index].TargetLabel,
                rows[index].Binding.TargetLabel);
        }
        Assert.All(
            rows.Take(commands.Length),
            row => Assert.Equal(
                $"fr-FR:Workbench.Shortcuts.Command.{row.Binding.Command}",
                row.Target));
        Assert.Equal(
            "User recipe",
            rows[^1].Target);
        Assert.All(
            rows.Take(commands.Length),
            row => Assert.True(
                editor.TryMatch(
                    WorkbenchShortcutModifiers.Control |
                    WorkbenchShortcutModifiers.Alt,
                    row.Gesture.Split('+')[^1],
                    out WorkbenchShortcutBinding?
                        matched) &&
                matched?.Id == row.Binding.Id));
    }

    [Fact]
    public void Saving_a_command_persists_its_enum_identity_not_its_localized_label()
    {
        var localization =
            new SwitchingLocalizationService();
        var store =
            new RecordingShortcutStore();
        var editor =
            new WorkbenchShortcutEditorViewModel(
                store,
                localization: localization,
                platform:
                    WorkbenchShortcutPlatform.Windows)
            {
                GestureText = "Ctrl+Shift+F8",
            };
        editor.SelectedCommand =
            editor.Commands.Single(choice =>
                choice.Command ==
                WorkbenchShortcutCommand
                    .PreviewCurrentRecipe);

        editor.SaveShortcutCommand.Execute(null);

        WorkbenchShortcutBinding saved =
            Assert.Single(store.Bindings);
        Assert.Equal(
            WorkbenchShortcutCommand
                .PreviewCurrentRecipe,
            saved.Command);
        Assert.Null(saved.TargetLabel);
        Assert.Equal(
            "Ctrl+Shift+F8",
            saved.Gesture);

        localization.SetCulture("fr-FR");

        Assert.Equal(1, store.SaveCount);
        Assert.Equal(saved.Id, store.Bindings[0].Id);
        Assert.Equal(
            saved.Gesture,
            store.Bindings[0].Gesture);
        Assert.Equal(
            saved.Command,
            store.Bindings[0].Command);
    }

    [Fact]
    public void Every_shipping_locale_refreshes_every_command_from_its_semantic_enum()
    {
        CultureInfo original =
            CultureInfo.CurrentUICulture;
        try
        {
            var localization =
                new ResourceLocalizationService(
                    new FakeSettings());
            WorkbenchShortcutBinding[] bindings =
                Enum.GetValues<
                        WorkbenchShortcutCommand>()
                    .Select(
                        (command, index) =>
                            new WorkbenchShortcutBinding(
                                Guid.Parse(
                                    $"22222222-2222-2222-2222-{index + 1:D12}"),
                                $"Ctrl+Shift+F{index + 1}",
                                WorkbenchShortcutTargetKind
                                    .Command,
                                command,
                                TargetLabel:
                                    "obsolete localized label"))
                    .ToArray();
            var store =
                new RecordingShortcutStore(
                    bindings);
            var editor =
                new WorkbenchShortcutEditorViewModel(
                    store,
                    localization: localization,
                    platform:
                        WorkbenchShortcutPlatform
                            .Windows);
            WorkbenchShortcutCommandChoice[]
                commandChoices =
                [.. editor.Commands];
            WorkbenchShortcutRow[] rows =
                [.. editor.Bindings];

            foreach (
                LocalizationCultureDescriptor locale in
                LocalizationCultureRegistry
                    .ShippingLocales)
            {
                localization.SetCulture(
                    locale.Name);

                Assert.Equal(
                    commandChoices,
                    editor.Commands);
                Assert.Equal(
                    rows,
                    editor.Bindings);
                foreach (
                    WorkbenchShortcutCommandChoice
                        choice in commandChoices)
                    Assert.Equal(
                        localization.Get(
                            $"Workbench.Shortcuts.Command.{choice.Command}"),
                        choice.Label);
                foreach (
                    WorkbenchShortcutRow row in
                    rows)
                {
                    Assert.Equal(
                        localization.Get(
                            $"Workbench.Shortcuts.Command.{row.Binding.Command}"),
                        row.Target);
                    Assert.Equal(
                        bindings.Single(
                                binding =>
                                    binding.Id ==
                                    row.Binding.Id)
                            .Gesture,
                        row.Gesture);
                }
                Assert.Equal(0, store.SaveCount);
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture =
                original;
        }
    }

    [Fact]
    public void Every_shipping_locale_keeps_platform_specific_shortcut_help_and_reservations()
    {
        CultureInfo original =
            CultureInfo.CurrentUICulture;
        try
        {
            var localization =
                new ResourceLocalizationService(
                    new FakeSettings());
            WorkbenchShortcutEditorViewModel[] editors =
                Enum.GetValues<
                        WorkbenchShortcutPlatform>()
                    .Select(platform =>
                        new WorkbenchShortcutEditorViewModel(
                            new RecordingShortcutStore(),
                            localization: localization,
                            platform: platform))
                    .ToArray();

            foreach (
                LocalizationCultureDescriptor locale in
                LocalizationCultureRegistry
                    .ShippingLocales)
            {
                localization.SetCulture(
                    locale.Name);

                foreach (
                    WorkbenchShortcutEditorViewModel
                        editor in editors)
                {
                    string primary =
                        editor.Platform ==
                        WorkbenchShortcutPlatform
                            .MacOS
                            ? "Meta"
                            : "Ctrl";
                    Assert.Contains(
                        $"{primary}+Alt+R",
                        editor.GestureHelpText,
                        StringComparison.Ordinal);
                    Assert.Contains(
                        $"{primary}+K",
                        editor.InputWarningText,
                        StringComparison.Ordinal);
                    Assert.Contains(
                        $"{primary}+I",
                        editor.InputWarningText,
                        StringComparison.Ordinal);
                    if (editor.Platform ==
                        WorkbenchShortcutPlatform
                            .MacOS)
                    {
                        Assert.DoesNotContain(
                            "Ctrl+",
                            editor.GestureHelpText,
                            StringComparison.Ordinal);
                        Assert.DoesNotContain(
                            "Ctrl+",
                            editor.InputWarningText,
                            StringComparison.Ordinal);
                    }

                    editor.GestureText =
                        $"{primary}+K";
                    Assert.False(
                        string.IsNullOrWhiteSpace(
                            editor
                                .GestureValidationMessage));
                }
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture =
                original;
        }
    }

    [Fact]
    public void Every_playlist_format_choice_refreshes_in_place_while_configuration_ids_remain_invariant()
    {
        var localization =
            new SwitchingLocalizationService();
        var editor =
            new PlaylistEditorViewModel(
                localization);
        string[] expectedIds =
            ["m3u8", "m3u", "wpl"];
        LocalizedChoice<string>[] choices =
            [.. editor.FormatChoices];

        Assert.Equal(
            expectedIds,
            editor.Formats);
        Assert.Equal(
            expectedIds,
            choices.Select(choice =>
                choice.Value));
        Assert.All(
            choices,
            choice => Assert.Equal(
                $"en-US:{TechnicalLabelResourceKeys.ForPlaylistFormat(choice.Value)}",
                choice.Label));

        foreach (string id in expectedIds)
        {
            editor.Format = id;

            Assert.Equal(
                id,
                editor.CreateConfiguration().Format);
            Assert.Equal(
                id,
                editor.SuggestedExtension);
        }
        editor.Format = "wpl";

        localization.SetCulture("fr-FR");

        Assert.Equal(
            choices,
            editor.FormatChoices);
        Assert.Equal(
            expectedIds,
            choices.Select(choice =>
                choice.Value));
        Assert.All(
            choices,
            choice => Assert.Equal(
                $"fr-FR:{TechnicalLabelResourceKeys.ForPlaylistFormat(choice.Value)}",
                choice.Label));
        Assert.Equal("wpl", editor.Format);
        Assert.Equal(
            "wpl",
            editor.CreateConfiguration().Format);
    }

    [Fact]
    public void Every_shipping_locale_exposes_standard_playlist_labels_without_changing_format_ids()
    {
        CultureInfo original =
            CultureInfo.CurrentUICulture;
        try
        {
            var localization =
                new ResourceLocalizationService(
                    new FakeSettings());
            var editor =
                new PlaylistEditorViewModel(
                    localization);
            LocalizedChoice<string>[] choices =
                [.. editor.FormatChoices];
            var expected =
                new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["m3u8"] = "M3U8",
                    ["m3u"] = "M3U",
                    ["wpl"] =
                        "Windows Media Player (WPL)",
                };

            foreach (
                LocalizationCultureDescriptor locale in
                LocalizationCultureRegistry
                    .ShippingLocales)
            {
                localization.SetCulture(
                    locale.Name);

                Assert.Equal(
                    choices,
                    editor.FormatChoices);
                Assert.Equal(
                    expected.Keys,
                    editor.FormatChoices.Select(
                        choice => choice.Value));
                foreach (
                    LocalizedChoice<string> choice in
                    editor.FormatChoices)
                {
                    Assert.Equal(
                        expected[choice.Value],
                        choice.Label);
                    editor.Format = choice.Value;
                    Assert.Equal(
                        choice.Value,
                        editor.CreateConfiguration()
                            .Format);
                }
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture =
                original;
        }
    }

    [Theory]
    [InlineData(
        "m3u",
        "Technical.PlaylistFormat.M3u")]
    [InlineData(
        "M3U8",
        "Technical.PlaylistFormat.M3u8")]
    [InlineData(
        " wpl ",
        "Technical.PlaylistFormat.Wpl")]
    public void Playlist_format_resource_mapping_is_case_insensitive_and_trimmed(
        string value,
        string expectedResourceKey)
    {
        Assert.Equal(
            expectedResourceKey,
            TechnicalLabelResourceKeys
                .ForPlaylistFormat(value));
    }

    [Fact]
    public void Workbench_section_xaml_binds_localized_presentation_without_replacing_semantic_values()
    {
        string shortcuts = File.ReadAllText(
            FindRepositoryFile(
                Path.Combine(
                    "MusicLibraryManager",
                    "Views",
                    "WorkbenchSections",
                    "WorkbenchShortcutsSectionView.axaml")));
        string playlists = File.ReadAllText(
            FindRepositoryFile(
                Path.Combine(
                    "MusicLibraryManager",
                    "Views",
                    "WorkbenchSections",
                    "WorkbenchPlaylistsSectionView.axaml")));

        Assert.Contains(
            "ShortcutEditor.GestureHelpText",
            shortcuts,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShortcutEditor.InputWarningText",
            shortcuts,
            StringComparison.Ordinal);
        Assert.Contains(
            "PlaylistEditor.FormatChoices",
            playlists,
            StringComparison.Ordinal);
        Assert.Contains(
            "DisplayMemberBinding=\"{Binding Label}\"",
            playlists,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectedValueBinding=\"{Binding Value}\"",
            playlists,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectedValue=\"{Binding PlaylistEditor.Format, Mode=TwoWay}\"",
            playlists,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ItemsSource=\"{Binding PlaylistEditor.Formats}\"",
            playlists,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(
        string relativePath)
    {
        DirectoryInfo? directory =
            new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate =
                Path.Combine(
                    directory.FullName,
                    relativePath);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file '{relativePath}'.");
    }

    private sealed class RecordingShortcutStore :
        IWorkbenchShortcutStore
    {
        public RecordingShortcutStore(
            IEnumerable<WorkbenchShortcutBinding>?
                bindings = null)
        {
            Bindings =
                bindings?.ToList() ?? [];
        }

        public List<WorkbenchShortcutBinding>
            Bindings { get; private set; }
        public int SaveCount { get; private set; }

        public IReadOnlyList<WorkbenchShortcutBinding>
            Load() =>
            [.. Bindings];

        public void Save(
            IReadOnlyList<WorkbenchShortcutBinding>
                bindings)
        {
            SaveCount++;
            Bindings = [.. bindings];
        }
    }

    private sealed class SwitchingLocalizationService :
        ILocalizationService
    {
        private CultureInfo _culture =
            CultureInfo.GetCultureInfo("en-US");

        public CultureInfo CurrentUICulture =>
            _culture;
        public IReadOnlyList<CultureInfo>
            SupportedCultures { get; } =
        [
            CultureInfo.GetCultureInfo("en-US"),
            CultureInfo.GetCultureInfo("fr-FR"),
        ];
        public event EventHandler? CultureChanged;

        public string Get(string key) =>
            key switch
            {
                "Workbench.Shortcuts.GestureHelp" =>
                    $"{_culture.Name}:Use Ctrl+Alt+R, Meta+F8, or Ctrl+Shift+Enter.",
                "Workbench.Shortcuts.InputWarning" =>
                    $"{_culture.Name}:Ctrl+K and Ctrl+I are reserved.",
                _ => $"{_culture.Name}:{key}",
            };

        public string Format(
            string key,
            params object?[] arguments) =>
            key switch
            {
                "Workbench.Shortcuts.Validation.Reserved" =>
                    $"{arguments[0]} is reserved.",
                "Workbench.Shortcuts.Validation.Conflict" =>
                    $"{arguments[0]} conflicts with {arguments[1]}.",
                _ => Get(key),
            };

        public string FormatCount(
            string key,
            long count,
            params object?[] arguments) =>
            $"{Get(key)}:{count}";

        public IReadOnlyDictionary<string, string>
            Snapshot() =>
            new Dictionary<string, string>();

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
