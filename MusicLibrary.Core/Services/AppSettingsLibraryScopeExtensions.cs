using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

/// <summary>
/// Namespaces library-specific UI and workflow state by the stable library identifier while
/// retaining a read-only fallback to pre-v2 global preference keys.
/// </summary>
public static class AppSettingsLibraryScopeExtensions
{
    private const string Prefix = "library";

    public static string GetLibraryPreferenceKey(this IAppSettings settings, string key)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        LibraryConfiguration? configuration = settings.GetSnapshot().Configuration;
        return configuration is null || configuration.LibraryId == Guid.Empty
            ? key
            : $"{Prefix}.{configuration.LibraryId:N}.{key}";
    }

    public static string? GetLibraryPreference(this IAppSettings settings, string key)
    {
        string scopedKey = settings.GetLibraryPreferenceKey(key);
        string? value = settings.GetPreference(scopedKey);
        return value ?? (StringComparer.Ordinal.Equals(scopedKey, key)
            ? null
            : settings.GetPreference(key));
    }

    public static void SetLibraryPreference(this IAppSettings settings, string key, string? value) =>
        settings.SetPreference(settings.GetLibraryPreferenceKey(key), value);
}
