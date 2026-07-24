using System.Globalization;
using System.Text.RegularExpressions;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.Tests;

[Collection(LocalizationTestCollection.Name)]
public sealed partial class SettingsLocalizationTests
{
    [Fact]
    public void Settings_view_has_no_unapproved_user_facing_literals()
    {
        string xaml = File.ReadAllText(FindRepositoryFile(
            Path.Combine("MusicLibraryManager", "Views", "SettingsView.axaml")));
        MatchCollection attributes = UserFacingAttribute().Matches(xaml);

        string[] literals = attributes
            .Select(match => match.Groups["value"].Value)
            .Where(value => !value.StartsWith('{'))
            .Where(value => value is not "!" and not "i" and not "1" and not "2")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(literals);
    }

    [Fact]
    public void Settings_view_model_routes_runtime_text_through_localization()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            Path.Combine("MusicLibraryManager.Presentation",
                "SettingsViewModel.cs")));

        Assert.DoesNotMatch(
            @"(?:StatusMessage|DiscogsCredentialStatus|FieldMappingStatus)\s*=\s*\$?""",
            source);
        Assert.DoesNotMatch(
            @"(?:PickFolderAsync|PickFileAsync|SaveFileAsync|ConfirmAsync|ShowMessageAsync)\(\s*\$?""",
            source);
        Assert.DoesNotMatch(
            @"issues\.Add\(\(\d+,\s*\$?""",
            source);

        string[] proseLiterals = CSharpStringLiteral().Matches(source)
            .Select(match => match.Value[1..^1])
            .Where(value => Regex.IsMatch(value, @"[A-Za-z]+\s+[A-Za-z]+"))
            .Where(value => value != "Steel Blue")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Empty(proseLiterals);
    }

    [Fact]
    public async Task Culture_refresh_changes_labels_without_changing_choice_identity()
    {
        var localization = new SwitchingLocalizationService();
        var viewModel = new SettingsViewModel(
            new FakeSettings(),
            new FakeFilePicker(),
            new FakeDialogs(),
            new FakeTheme(),
            localization: localization);
        await viewModel.NewConfigurationCommand.ExecuteAsync(null);

        LibraryProfileEditorRow profile = Assert.IsType<LibraryProfileEditorRow>(
            viewModel.AdvancedProfile);
        LibraryPathCollisionPolicy collision = profile.CollisionPolicy;
        ThemeChoice dark = viewModel.ThemeChoices.Single(choice =>
            choice.Value == "Dark");
        LocalizedChoice<string> french =
            viewModel.DisplayLanguageChoices.Single(
                choice =>
                    choice.Value == "fr-FR");
        Assert.StartsWith(
            "fran\u00E7ais (France)",
            french.Label,
            StringComparison.Ordinal);
        viewModel.SelectedThemeChoice = dark;
        LibraryPathCollisionPolicy[] collisionValues =
            viewModel.CollisionPolicyChoices.Select(choice => choice.Value).ToArray();
        string[] playlistValues =
            viewModel.PlaylistEncodingChoices.Select(choice => choice.Value).ToArray();
        string collisionLabel =
            viewModel.CollisionPolicyChoices[0].Label;
        string themeLabel = dark.Name;

        localization.SetCulture("fr-FR");

        Assert.Equal(collisionValues,
            viewModel.CollisionPolicyChoices.Select(choice => choice.Value));
        Assert.Equal(playlistValues,
            viewModel.PlaylistEncodingChoices.Select(choice => choice.Value));
        Assert.Equal(collision, profile.CollisionPolicy);
        Assert.Same(dark, viewModel.SelectedThemeChoice);
        Assert.Equal("Dark", viewModel.SelectedTheme);
        Assert.NotEqual(collisionLabel,
            viewModel.CollisionPolicyChoices[0].Label);
        Assert.NotEqual(themeLabel, dark.Name);
        Assert.StartsWith("fr-FR:", dark.Name, StringComparison.Ordinal);
        Assert.Same(
            french,
            viewModel.DisplayLanguageChoices.Single(
                choice =>
                    choice.Value == "fr-FR"));
        Assert.Contains(
            "fr-FR:Common.Beta",
            french.Label,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Field_mapping_saved_count_uses_shipping_plural_rules_and_relocalizes()
    {
        CultureInfo previousUICulture =
            CultureInfo.CurrentUICulture;
        try
        {
            var settings = new FakeSettings();
            settings.SetPreference(
                LocalizationPreferences.DisplayLanguage,
                "en-US");
            var localization =
                new ResourceLocalizationService(settings);
            var viewModel = new SettingsViewModel(
                settings,
                new FakeFilePicker(),
                new FakeDialogs(),
                new FakeTheme(),
                localization: localization);

            Assert.Empty(viewModel.FieldMappings);
            viewModel.SaveFieldMappingsCommand.Execute(null);
            Assert.Equal(
                localization.FormatCount(
                    "Settings.FieldMappings.Status.Saved",
                    0),
                viewModel.FieldMappingStatus);

            localization.SetCulture("pt-BR");

            Assert.Equal(
                localization.Format(
                    "Settings.FieldMappings.Status.Saved.One",
                    0),
                viewModel.FieldMappingStatus);
            Assert.Equal(
                localization.FormatCount(
                    "Settings.FieldMappings.Status.Saved",
                    0),
                viewModel.FieldMappingStatus);
        }
        finally
        {
            CultureInfo.CurrentUICulture =
                previousUICulture;
        }
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file '{relativePath}'.");
    }

    [GeneratedRegex(
        @"(?:Text|Content|Header|PlaceholderText|ToolTip\.Tip|AutomationProperties\.Name)=""(?<value>[^""]+)""")]
    private static partial Regex UserFacingAttribute();

    [GeneratedRegex(@"""(?:\\.|[^""\\])*""")]
    private static partial Regex CSharpStringLiteral();

    private sealed class SwitchingLocalizationService : ILocalizationService
    {
        private CultureInfo _culture = CultureInfo.GetCultureInfo("en-US");

        public CultureInfo CurrentUICulture => _culture;
        public IReadOnlyList<CultureInfo> SupportedCultures { get; } =
        [
            CultureInfo.GetCultureInfo("en-US"),
            CultureInfo.GetCultureInfo("fr-FR"),
        ];
        public event EventHandler? CultureChanged;

        public string Get(string key) => $"{_culture.Name}:{key}";

        public string Format(string key, params object?[] arguments) =>
            Get(key);

        public string FormatCount(
            string key,
            long count,
            params object?[] arguments) =>
            Get($"{key}.{(count == 1 ? "One" : "Other")}");

        public IReadOnlyDictionary<string, string> Snapshot() =>
            new Dictionary<string, string>();

        public void SetCulture(string cultureName)
        {
            CultureInfo next = CultureInfo.GetCultureInfo(cultureName);
            if (string.Equals(next.Name, _culture.Name,
                    StringComparison.OrdinalIgnoreCase))
                return;
            _culture = next;
            CultureChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
