using System.Globalization;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

[CollectionDefinition(
    Name,
    DisableParallelization = true)]
public sealed class LocalizationTestCollection
{
    public const string Name = "Localization";
}

[Collection(LocalizationTestCollection.Name)]
public sealed class LocalizationTests
{
    [Fact]
    public void Neutral_catalog_resolves_and_missing_keys_are_visible()
    {
        CultureInfo original =
            CultureInfo.CurrentUICulture;
        try
        {
            var service =
                new ResourceLocalizationService(
                    new FakeSettings());

            Assert.Equal(
                "en-US",
                service.CurrentUICulture.Name);
            Assert.Equal(
                "Display language",
                service.Get(
                    LocalizationKeys.DisplayLanguage));
            Assert.Equal(
                "English (United States)",
                service.Get(
                    LocalizationKeys.CultureName(
                        "en-US")));
            Assert.Equal(
                "\u27E6Missing.Key\u27E7",
                service.Get("Missing.Key"));
            Assert.Contains(
                LocalizationKeys.DisplayLanguage,
                service.Snapshot().Keys);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void Unsupported_persisted_culture_repairs_to_neutral()
    {
        CultureInfo original =
            CultureInfo.CurrentUICulture;
        var settings = new FakeSettings();
        settings.Preferences[
            LocalizationPreferences.DisplayLanguage] =
            "fr-FR";
        try
        {
            var service =
                new ResourceLocalizationService(settings);

            Assert.Equal(
                "en-US",
                service.CurrentUICulture.Name);
            Assert.Equal(
                "en-US",
                settings.Preferences[
                    LocalizationPreferences
                        .DisplayLanguage]);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void Culture_switch_raises_event_uses_fallback_and_preserves_formatting_culture()
    {
        CultureInfo originalUI =
            CultureInfo.CurrentUICulture;
        CultureInfo originalFormatting =
            CultureInfo.CurrentCulture;
        var settings = new FakeSettings();
        try
        {
            var service =
                new ResourceLocalizationService(
                    settings,
                    ["en-US", "en-GB"]);
            int changes = 0;
            service.CultureChanged +=
                (_, _) => changes++;

            service.SetCulture("en-GB");

            Assert.Equal(1, changes);
            Assert.Equal(
                "en-GB",
                service.CurrentUICulture.Name);
            Assert.Equal(
                "en-GB",
                CultureInfo.CurrentUICulture.Name);
            Assert.Equal(
                originalFormatting.Name,
                CultureInfo.CurrentCulture.Name);
            Assert.Equal(
                "Display language",
                service.Get(
                    LocalizationKeys.DisplayLanguage));
            Assert.Equal(
                "en-GB",
                settings.Preferences[
                    LocalizationPreferences
                        .DisplayLanguage]);

            service.SetCulture("en-GB");
            Assert.Equal(1, changes);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalUI;
        }
    }

    [Fact]
    public void Localized_choice_changes_label_without_changing_value()
    {
        var choice =
            new LocalizedChoice<string>(
                "en-US",
                "English");
        string? changedProperty = null;
        choice.PropertyChanged +=
            (_, args) =>
                changedProperty = args.PropertyName;

        choice.Label =
            "English (United States)";

        Assert.Equal(
            nameof(LocalizedChoice<string>.Label),
            changedProperty);
        Assert.Equal("en-US", choice.Value);
        Assert.Equal(
            "English (United States)",
            choice.ToString());
    }

    [Fact]
    public void Count_variants_use_ui_text_and_current_formatting_culture()
    {
        CultureInfo originalUI =
            CultureInfo.CurrentUICulture;
        CultureInfo originalFormatting =
            CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture =
                CultureInfo.GetCultureInfo("de-DE");
            var service =
                new ResourceLocalizationService(
                    new FakeSettings());

            Assert.Equal(
                "1 file",
                service.FormatCount(
                    "Count.Files",
                    1));
            Assert.Equal(
                "2 files",
                service.FormatCount(
                    "Count.Files",
                    2));
            Assert.Equal(
                "1.234 files",
                service.FormatCount(
                    "Count.Files",
                    1234));
        }
        finally
        {
            CultureInfo.CurrentUICulture =
                originalUI;
            CultureInfo.CurrentCulture =
                originalFormatting;
        }
    }
}
