using System.Globalization;

namespace MusicLibraryManager.Presentation;

public interface ILocalizationService
{
    CultureInfo CurrentUICulture { get; }
    IReadOnlyList<CultureInfo> SupportedCultures { get; }
    IReadOnlyList<LocalizationCultureDescriptor>
        SupportedLocales =>
        SupportedCultures
            .Select(LocalizationCultureRegistry.Describe)
            .ToArray();
    event EventHandler? CultureChanged;
    string Get(string key);
    string Format(string key, params object?[] arguments);
    string FormatCount(
        string key,
        long count,
        params object?[] arguments);
    IReadOnlyDictionary<string, string> Snapshot();
    void SetCulture(string cultureName);
}

public static class LocalizationPreferences
{
    public const string DisplayLanguage =
        "manager.appearance.culture.v1";
}

public static class LocalizationKeys
{
    public const string Beta = "Common.Beta";
    public const string DisplayLanguage =
        "Settings.Appearance.DisplayLanguage";
    public const string DisplayLanguageDescription =
        "Settings.Appearance.DisplayLanguageDescription";

    public static string CultureName(string cultureName) =>
        $"Culture.{cultureName}";
}
