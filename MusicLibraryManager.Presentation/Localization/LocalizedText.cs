using System.Globalization;
using System.Resources;

namespace MusicLibraryManager.Presentation;

/// <summary>
/// Resource lookup for presentation models that are also constructed directly by
/// tests or tooling. Application views should receive <see cref="ILocalizationService"/>
/// when they need live culture-change notifications.
/// </summary>
public static class LocalizedText
{
    private const string ResourceBaseName =
        "MusicLibraryManager.Presentation.Resources.Strings";
    private static readonly ResourceManager Resources =
        new(
            ResourceBaseName,
            typeof(LocalizedText).Assembly);

    public static string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Resources.GetString(
                   key,
                   CultureInfo.CurrentUICulture) ??
               $"\u27E6{key}\u27E7";
    }

    public static string Format(
        string key,
        params object?[] arguments) =>
        string.Format(
            CultureInfo.CurrentCulture,
            Get(key),
            arguments);

    public static string FormatCount(
        string key,
        long count,
        params object?[] arguments)
    {
        string variant =
            count == 1 ? $"{key}.One" : $"{key}.Other";
        object?[] formatArguments =
            [count, .. arguments];
        return Format(variant, formatArguments);
    }
}
