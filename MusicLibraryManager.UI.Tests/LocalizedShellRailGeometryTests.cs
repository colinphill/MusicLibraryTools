using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

public sealed class LocalizedShellRailGeometryTests
{
    [AvaloniaFact]
    public void
        Expanded_and_overlay_rails_keep_the_full_product_identity_accessible_and_unclipped_in_shipping_locales()
    {
        CultureInfo previous =
            CultureInfo.CurrentUICulture;
        var settings = new TestSettings();
        settings.SetPreference(
            LocalizationPreferences
                .DisplayLanguage,
            "en-US");
        AppearancePreferences
            .SetShellRailExpanded(
                settings,
                expanded: true);
        using ServiceProvider services =
            Composition.BuildServices(
                collection =>
                    collection.AddSingleton<
                        IAppSettings>(settings));
        App.UseServicesForTests(services);
        ILocalizationService localization =
            services.GetRequiredService<
                ILocalizationService>();
        MainWindow window =
            services.GetRequiredService<
                MainWindow>();
        try
        {
            window.WindowState =
                WindowState.Normal;
            window.Width = 1_440;
            window.Height = 760;
            window.Show();
            Render();

            foreach (string culture in
                     ShippingCultures)
            {
                localization.SetCulture(
                    culture);
                Render();
                AssertRail(
                    overlay: false,
                    culture);
            }

            window.Width = 900;
            Render();
            foreach (string culture in
                     ShippingCultures)
            {
                localization.SetCulture(
                    culture);
                Render();
                AssertRail(
                    overlay: true,
                    culture);
            }
        }
        finally
        {
            window.Hide();
            CultureInfo.CurrentUICulture =
                previous;
        }

        void AssertRail(
            bool overlay,
            string culture)
        {
            Border rail =
                window.FindControl<Border>(
                    "NavigationRail")!;
            Grid brand =
                window.FindControl<Grid>(
                    "BrandBlock")!;
            StackPanel copy =
                window.FindControl<
                    StackPanel>(
                    "BrandCopy")!;
            Button toggle =
                window.FindControl<Button>(
                    "NavigationRailToggle")!;
            Border scrim =
                window.FindControl<Border>(
                    "NavigationScrim")!;
            TextBlock productName =
                Assert.Single(
                    copy.GetVisualDescendants()
                        .OfType<TextBlock>());
            string expected =
                localization.Get(
                    "App.Title");

            Assert.Equal(
                "Music Library Manager",
                expected);
            Assert.Equal(
                expected,
                productName.Text);
            Assert.Equal(
                expected,
                AutomationProperties
                    .GetName(brand));
            Assert.Equal(
                expected,
                AutomationProperties
                    .GetHelpText(toggle));
            Assert.True(
                copy.IsEffectivelyVisible,
                $"{culture}: product copy was hidden in the {(overlay ? "overlay" : "expanded")} rail.");
            Assert.Equal(
                220,
                rail.Bounds.Width,
                precision: 2);
            Assert.Equal(
                overlay,
                scrim.IsEffectivelyVisible);

            AssertContained(
                brand,
                rail,
                $"{culture}/brand");
            AssertContained(
                productName,
                rail,
                $"{culture}/product-name");
            Assert.True(
                productName.DesiredSize
                        .Width <=
                    productName.Bounds.Width +
                    1,
                $"{culture}: the complete product name required {productName.DesiredSize.Width:0.##} px but received {productName.Bounds.Width:0.##} px.");
            Assert.True(
                productName.DesiredSize
                        .Height <=
                    productName.Bounds.Height +
                    1,
                $"{culture}: the complete product name was vertically clipped.");

            foreach (TextBlock label in
                     rail.GetVisualDescendants()
                         .OfType<TextBlock>()
                         .Where(text =>
                             text.IsEffectivelyVisible &&
                             text.Classes.Contains(
                                 "nav-label")))
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(
                        label.Text),
                    $"{culture}: a visible destination label was blank.");
                AssertContained(
                    label,
                    rail,
                    $"{culture}/{label.Text}");
                Assert.True(
                    label.DesiredSize.Width <=
                    label.Bounds.Width + 1,
                    $"{culture}: destination label '{label.Text}' was clipped.");
            }
        }
    }

    private static readonly string[]
        ShippingCultures =
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

    private static void AssertContained(
        Control child,
        Control ancestor,
        string context)
    {
        Point? origin =
            child.TranslatePoint(
                default,
                ancestor);
        Assert.NotNull(origin);
        Rect bounds =
            new(
                origin.Value,
                child.Bounds.Size);
        Assert.True(
            bounds.Left >= -1 &&
            bounds.Top >= -1 &&
            bounds.Right <=
                ancestor.Bounds.Width + 1 &&
            bounds.Bottom <=
                ancestor.Bounds.Height + 1,
            $"{context}: {bounds} was outside {ancestor.Bounds.Size}.");
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
}
