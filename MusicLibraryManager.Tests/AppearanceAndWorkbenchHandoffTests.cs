using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class AppearanceAndWorkbenchHandoffTests
{
    [Fact]
    public void Appearance_preferences_use_stable_keys_and_safe_defaults()
    {
        var settings = new TestSettings();

        Assert.Equal(
            "manager.appearance.density.v1",
            AppearancePreferences.DensityPreference);
        Assert.Equal(
            "manager.appearance.shellRailExpanded.v1",
            AppearancePreferences.ShellRailExpandedPreference);
        Assert.Equal(
            UiDensity.Standard,
            AppearancePreferences.GetDensity(settings));
        Assert.True(
            AppearancePreferences.GetShellRailExpanded(settings));

        AppearancePreferences.SetDensity(
            settings,
            UiDensity.Compact);
        AppearancePreferences.SetShellRailExpanded(
            settings,
            expanded: false);

        Assert.Equal(
            UiDensity.Compact,
            AppearancePreferences.GetDensity(settings));
        Assert.False(
            AppearancePreferences.GetShellRailExpanded(settings));
        Assert.Equal(
            nameof(UiDensity.Compact),
            settings.GetPreference(
                AppearancePreferences.DensityPreference));
        Assert.Equal(
            bool.FalseString,
            settings.GetPreference(
                AppearancePreferences.ShellRailExpandedPreference));
    }

    [Fact]
    public void Invalid_appearance_preferences_fall_back_without_migration()
    {
        var settings = new TestSettings();
        settings.SetPreference(
            AppearancePreferences.DensityPreference,
            "UnknownDensity");
        settings.SetPreference(
            AppearancePreferences.ShellRailExpandedPreference,
            "not-a-boolean");

        Assert.Equal(
            UiDensity.Standard,
            AppearancePreferences.GetDensity(settings));
        Assert.True(
            AppearancePreferences.GetShellRailExpanded(settings));
    }

    [Fact]
    public void Workbench_handoff_captures_ordered_distinct_paths_and_stable_identity()
    {
        string first = Path.GetFullPath("first.flac");
        string second = Path.GetFullPath("second.flac");
        var paths = new List<string>
        {
            first,
            "",
            second,
            first,
        };

        WorkbenchHandoffRequest request =
            WorkbenchHandoffRequest.Create(
                WorkbenchSection.Files,
                WorkbenchHandoffScopeKind.VisibleResults,
                paths);
        paths.Clear();

        Assert.Equal(
            WorkbenchSection.Files,
            request.DestinationSection);
        Assert.Equal(
            WorkbenchHandoffScopeKind.VisibleResults,
            request.ScopeKind);
        Assert.Equal(
            [first, second],
            request.CapturedPaths);
        Assert.Equal(
            0,
            (int)WorkbenchHandoffScopeKind.Selected);
        Assert.Equal(
            1,
            (int)WorkbenchHandoffScopeKind.VisibleResults);
        Assert.Equal(
            2,
            (int)WorkbenchHandoffScopeKind.AllResults);
    }

    private sealed class TestSettings : IAppSettings
    {
        private readonly Dictionary<string, string>
            _preferences = [];

        public string? ConfigPath => null;
        public LibraryConfiguration? Configuration => null;
        public event EventHandler? ConfigurationChanged;

        public AppConfigurationSnapshot GetSnapshot() =>
            new(null, null, 0);

        public void LoadConfig(string path) =>
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);

        public string? GetRememberedConfigPath() => null;
        public IReadOnlyList<string> RecentConfigPaths => [];
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
