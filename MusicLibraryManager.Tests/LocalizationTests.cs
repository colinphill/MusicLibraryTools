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
            CultureInfo.CurrentUICulture =
                CultureInfo.GetCultureInfo("en-US");
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
            "xx-Invalid";
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
            CultureInfo.CurrentUICulture =
                CultureInfo.GetCultureInfo("en-US");
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

    [Fact]
    public void Shipping_locale_descriptors_have_stable_ids_native_autonyms_and_beta_state()
    {
        string[] expectedNames =
        [
            "en-US",
            "de-DE",
            "es-ES",
            "fr-FR",
            "it-IT",
            "pt-BR",
            "ja-JP",
            "ko-KR",
            "zh-CN",
            "zh-TW",
        ];

        Assert.Equal(
            expectedNames,
            LocalizationCultureRegistry
                .ShippingLocales
                .Select(locale => locale.Name));
        Assert.False(
            LocalizationCultureRegistry
                .ShippingLocales[0]
                .IsBeta);
        Assert.All(
            LocalizationCultureRegistry
                .ShippingLocales
                .Skip(1),
            locale =>
            {
                Assert.True(locale.IsBeta);
                Assert.False(
                    string.IsNullOrWhiteSpace(
                        locale.NativeAutonym));
            });
        Assert.Equal(
            "\u7B80\u4F53\u4E2D\u6587\uFF08\u4E2D\u56FD\uFF09",
            LocalizationCultureRegistry
                .ShippingLocales
                .Single(locale =>
                    locale.Name == "zh-CN")
                .NativeAutonym);
    }

    [Theory]
    [InlineData("de-AT", "de-DE")]
    [InlineData("es-MX", "es-ES")]
    [InlineData("fr-CA", "fr-FR")]
    [InlineData("it-CH", "it-IT")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("ja", "ja-JP")]
    [InlineData("ko", "ko-KR")]
    [InlineData("zh", "zh-CN")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("zh-SG", "zh-CN")]
    [InlineData("zh-Hant", "zh-TW")]
    [InlineData("zh-HK", "zh-TW")]
    [InlineData("zh-MO", "zh-TW")]
    [InlineData("nl-NL", "en-US")]
    public void Os_ui_culture_maps_to_supported_exact_or_family_locale(
        string requestedName,
        string expectedName)
    {
        CultureInfo resolved =
            LocalizationCultureRegistry.Resolve(
                CultureInfo.GetCultureInfo(
                    requestedName),
                LocalizationCultureRegistry
                    .ShippingLocales
                    .Select(locale =>
                        locale.Culture)
                    .ToArray());

        Assert.Equal(expectedName, resolved.Name);
    }

    [Fact]
    public void Missing_preference_uses_os_ui_language_persists_it_and_preserves_current_culture()
    {
        CultureInfo originalUI =
            CultureInfo.CurrentUICulture;
        CultureInfo originalFormatting =
            CultureInfo.CurrentCulture;
        var settings = new FakeSettings();
        try
        {
            CultureInfo.CurrentCulture =
                CultureInfo.GetCultureInfo("de-DE");
            CultureInfo.CurrentUICulture =
                CultureInfo.GetCultureInfo("fr-CA");

            var service =
                new ResourceLocalizationService(
                    settings);

            Assert.Equal(
                "fr-FR",
                service.CurrentUICulture.Name);
            Assert.Equal(
                "fr-FR",
                settings.Preferences[
                    LocalizationPreferences
                        .DisplayLanguage]);
            Assert.Equal(
                "de-DE",
                CultureInfo.CurrentCulture.Name);
        }
        finally
        {
            CultureInfo.CurrentUICulture =
                originalUI;
            CultureInfo.CurrentCulture =
                originalFormatting;
        }
    }

    [Fact]
    public void Every_shipping_locale_supports_persisted_startup_and_live_switching_without_changing_current_culture()
    {
        CultureInfo originalUI =
            CultureInfo.CurrentUICulture;
        CultureInfo originalFormatting =
            CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture =
                CultureInfo.GetCultureInfo("de-DE");
            var settings = new FakeSettings();
            settings.Preferences[
                LocalizationPreferences.DisplayLanguage] =
                "en-US";
            var service =
                new ResourceLocalizationService(
                    settings);
            int changes = 0;
            service.CultureChanged +=
                (_, _) => changes++;

            foreach (var locale in
                     LocalizationCultureRegistry
                         .ShippingLocales)
            {
                service.SetCulture(locale.Name);

                Assert.Equal(
                    locale.Name,
                    service.CurrentUICulture.Name);
                Assert.Equal(
                    locale.Name,
                    CultureInfo.CurrentUICulture.Name);
                Assert.Equal(
                    locale.Name,
                    settings.Preferences[
                        LocalizationPreferences
                            .DisplayLanguage]);
                Assert.Equal(
                    "de-DE",
                    CultureInfo.CurrentCulture.Name);
                string displayName =
                    locale.GetDisplayName(service);
                Assert.StartsWith(
                    locale.NativeAutonym,
                    displayName,
                    StringComparison.Ordinal);
                if (locale.IsBeta)
                {
                    Assert.EndsWith(
                        service.Get(
                            LocalizationKeys.Beta),
                        displayName,
                        StringComparison.Ordinal);
                    Assert.DoesNotContain(
                        "\u27E6",
                        displayName,
                        StringComparison.Ordinal);
                }
                else
                    Assert.Equal(
                        locale.NativeAutonym,
                        displayName);

                var restarted =
                    new ResourceLocalizationService(
                        settings);
                Assert.Equal(
                    locale.Name,
                    restarted.CurrentUICulture.Name);
                Assert.Equal(
                    "de-DE",
                    CultureInfo.CurrentCulture.Name);
            }

            Assert.Equal(
                LocalizationCultureRegistry
                    .ShippingLocales.Count - 1,
                changes);
        }
        finally
        {
            CultureInfo.CurrentUICulture =
                originalUI;
            CultureInfo.CurrentCulture =
                originalFormatting;
        }
    }

    [Theory]
    [InlineData("en-US", 1, CardinalPluralCategory.One)]
    [InlineData("en-US", 0, CardinalPluralCategory.Other)]
    [InlineData("fr-FR", 0, CardinalPluralCategory.One)]
    [InlineData("fr-FR", 2, CardinalPluralCategory.Other)]
    [InlineData("pt-BR", 0, CardinalPluralCategory.One)]
    [InlineData("pt-BR", 2, CardinalPluralCategory.Other)]
    [InlineData("pt-PT", 0, CardinalPluralCategory.Other)]
    [InlineData("ja-JP", 1, CardinalPluralCategory.Other)]
    [InlineData("zh-TW", 1, CardinalPluralCategory.Other)]
    [InlineData("ar", 0, CardinalPluralCategory.Zero)]
    [InlineData("ar", 1, CardinalPluralCategory.One)]
    [InlineData("ar", 2, CardinalPluralCategory.Two)]
    [InlineData("ar", 7, CardinalPluralCategory.Few)]
    [InlineData("ar", 18, CardinalPluralCategory.Many)]
    [InlineData("ar", 100, CardinalPluralCategory.Other)]
    public void Cardinal_plural_resolver_supports_all_categories(
        string cultureName,
        long count,
        CardinalPluralCategory expected)
    {
        Assert.Equal(
            expected,
            CardinalPluralResolver.Resolve(
                count,
                CultureInfo.GetCultureInfo(
                    cultureName)));
    }

    [Fact]
    public void Both_localization_paths_use_the_shared_cardinal_resolver()
    {
        CultureInfo originalUI =
            CultureInfo.CurrentUICulture;
        try
        {
            var settings = new FakeSettings();
            settings.Preferences[
                LocalizationPreferences.DisplayLanguage] =
                "fr-FR";
            var service =
                new ResourceLocalizationService(
                    settings);

            Assert.Equal(
                service.Format(
                    "Count.Files.One",
                    0),
                service.FormatCount(
                    "Count.Files",
                    0));
            Assert.Equal(
                LocalizedText.Format(
                    "Count.Files.One",
                    0),
                LocalizedText.FormatCount(
                    "Count.Files",
                    0));

            service.SetCulture("ja-JP");

            Assert.Equal(
                service.Format(
                    "Count.Files.Other",
                    1),
                service.FormatCount(
                    "Count.Files",
                    1));
            Assert.Equal(
                LocalizedText.Format(
                    "Count.Files.Other",
                    1),
                LocalizedText.FormatCount(
                    "Count.Files",
                    1));
        }
        finally
        {
            CultureInfo.CurrentUICulture =
                originalUI;
        }
    }

    [Fact]
    public void Shipping_plural_requirements_only_request_used_categories()
    {
        foreach (var locale in
                 LocalizationCultureRegistry
                     .ShippingLocales)
        {
            IReadOnlyList<CardinalPluralCategory>
                categories =
                CardinalPluralResolver
                    .RequiredCategories(
                        locale.Culture);
            if (locale.Name.StartsWith(
                    "ja",
                    StringComparison.Ordinal) ||
                locale.Name.StartsWith(
                    "ko",
                    StringComparison.Ordinal) ||
                locale.Name.StartsWith(
                    "zh",
                    StringComparison.Ordinal))
                Assert.Equal(
                    [CardinalPluralCategory.Other],
                    categories);
            else
                Assert.Equal(
                    [
                        CardinalPluralCategory.One,
                        CardinalPluralCategory.Other,
                    ],
                    categories);
        }

        Assert.Equal(
            [
                CardinalPluralCategory.Zero,
                CardinalPluralCategory.One,
                CardinalPluralCategory.Two,
                CardinalPluralCategory.Few,
                CardinalPluralCategory.Many,
                CardinalPluralCategory.Other,
            ],
            CardinalPluralResolver
                .RequiredCategories(
                    CultureInfo.GetCultureInfo(
                        "ar")));
    }
}
