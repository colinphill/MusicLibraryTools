using System.Globalization;

namespace MusicLibraryManager.Presentation;

public sealed record LocalizationCultureDescriptor(
    string Name,
    string NativeAutonym,
    bool IsBeta)
{
    public CultureInfo Culture { get; } =
        CultureInfo.GetCultureInfo(Name);

    public string GetDisplayName(
        ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
        return IsBeta
            ? $"{NativeAutonym} \u2014 {localization.Get(LocalizationKeys.Beta)}"
            : NativeAutonym;
    }
}

/// <summary>
/// Stable application locale identities and OS-language mapping. Locale names are
/// persisted; display names are deliberately separate and may change without
/// invalidating a saved preference.
/// </summary>
public static class LocalizationCultureRegistry
{
    public const string FallbackCultureName = "en-US";
    private const string GermanCultureName = "de-DE";
    private const string SpanishCultureName = "es-ES";
    private const string FrenchCultureName = "fr-FR";
    private const string ItalianCultureName = "it-IT";
    private const string BrazilianPortugueseCultureName =
        "pt-BR";
    private const string JapaneseCultureName = "ja-JP";
    private const string KoreanCultureName = "ko-KR";
    private const string SimplifiedChineseCultureName =
        "zh-CN";
    private const string TraditionalChineseCultureName =
        "zh-TW";
    private const string SupportedCulturePrecondition =
        "At least one UI culture is required.";

    private static readonly LocalizationCultureDescriptor[]
        ShippingLocaleArray =
        [
            new(FallbackCultureName, "English (United States)", false),
            new(GermanCultureName, "Deutsch (Deutschland)", true),
            new(SpanishCultureName, "espa\u00F1ol (Espa\u00F1a)", true),
            new(FrenchCultureName, "fran\u00E7ais (France)", true),
            new(ItalianCultureName, "italiano (Italia)", true),
            new(BrazilianPortugueseCultureName, "portugu\u00EAs (Brasil)", true),
            new(JapaneseCultureName, "\u65E5\u672C\u8A9E (\u65E5\u672C)", true),
            new(KoreanCultureName, "\uD55C\uAD6D\uC5B4 (\uB300\uD55C\uBBFC\uAD6D)", true),
            new(SimplifiedChineseCultureName, "\u7B80\u4F53\u4E2D\u6587\uFF08\u4E2D\u56FD\uFF09", true),
            new(TraditionalChineseCultureName, "\u7E41\u9AD4\u4E2D\u6587\uFF08\u53F0\u7063\uFF09", true),
        ];

    public static IReadOnlyList<
        LocalizationCultureDescriptor> ShippingLocales =>
        ShippingLocaleArray;

    public static LocalizationCultureDescriptor Describe(
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return ShippingLocaleArray.FirstOrDefault(
                   candidate => string.Equals(
                       candidate.Name,
                       culture.Name,
                       StringComparison.OrdinalIgnoreCase)) ??
               new LocalizationCultureDescriptor(
                   culture.Name,
                   culture.NativeName,
                   !string.Equals(
                       culture.Name,
                       FallbackCultureName,
                       StringComparison.OrdinalIgnoreCase));
    }

    public static CultureInfo Resolve(
        string? requestedCultureName,
        IReadOnlyList<CultureInfo> supportedCultures)
    {
        ArgumentNullException.ThrowIfNull(
            supportedCultures);
        if (supportedCultures.Count == 0)
            throw new ArgumentException(
                SupportedCulturePrecondition,
                nameof(supportedCultures));

        if (!string.IsNullOrWhiteSpace(
                requestedCultureName))
        {
            try
            {
                return Resolve(
                    CultureInfo.GetCultureInfo(
                        requestedCultureName),
                    supportedCultures);
            }
            catch (CultureNotFoundException)
            {
                // A removed or malformed preference is repaired to the
                // stable fallback below.
            }
        }

        return FindFallback(supportedCultures);
    }

    public static CultureInfo Resolve(
        CultureInfo requestedCulture,
        IReadOnlyList<CultureInfo> supportedCultures)
    {
        ArgumentNullException.ThrowIfNull(
            requestedCulture);
        ArgumentNullException.ThrowIfNull(
            supportedCultures);
        if (supportedCultures.Count == 0)
            throw new ArgumentException(
                SupportedCulturePrecondition,
                nameof(supportedCultures));

        CultureInfo? exact = Find(
            requestedCulture.Name,
            supportedCultures);
        if (exact is not null)
            return exact;

        string? mappedName =
            GetFamilyDefault(requestedCulture);
        CultureInfo? mapped = mappedName is null
            ? null
            : Find(mappedName, supportedCultures);
        return mapped ??
               FindFallback(supportedCultures);
    }

    private static string? GetFamilyDefault(
        CultureInfo culture)
    {
        string language =
            culture.TwoLetterISOLanguageName
                .ToLowerInvariant();
        if (language == "zh")
            return ResolveChineseCultureName(
                culture.Name);

        return language switch
        {
            "en" => FallbackCultureName,
            "de" => GermanCultureName,
            "es" => SpanishCultureName,
            "fr" => FrenchCultureName,
            "it" => ItalianCultureName,
            "pt" => BrazilianPortugueseCultureName,
            "ja" => JapaneseCultureName,
            "ko" => KoreanCultureName,
            _ => null,
        };
    }

    private static string ResolveChineseCultureName(
        string cultureName)
    {
        string normalized =
            cultureName.ToLowerInvariant();
        if (normalized.Contains(
                "hant",
                StringComparison.Ordinal) ||
            HasRegion(normalized, "tw") ||
            HasRegion(normalized, "hk") ||
            HasRegion(normalized, "mo"))
            return TraditionalChineseCultureName;
        if (normalized.Contains(
                "hans",
                StringComparison.Ordinal) ||
            HasRegion(normalized, "cn") ||
            HasRegion(normalized, "sg"))
            return SimplifiedChineseCultureName;
        return SimplifiedChineseCultureName;
    }

    private static bool HasRegion(
        string cultureName,
        string region) =>
        cultureName.Split(
                '-',
                StringSplitOptions.RemoveEmptyEntries)
            .Contains(
                region,
                StringComparer.OrdinalIgnoreCase);

    private static CultureInfo? Find(
        string cultureName,
        IReadOnlyList<CultureInfo> supportedCultures) =>
        supportedCultures.FirstOrDefault(
            culture => string.Equals(
                culture.Name,
                cultureName,
                StringComparison.OrdinalIgnoreCase));

    private static CultureInfo FindFallback(
        IReadOnlyList<CultureInfo> supportedCultures) =>
        Find(
            FallbackCultureName,
            supportedCultures) ??
        supportedCultures[0];
}
