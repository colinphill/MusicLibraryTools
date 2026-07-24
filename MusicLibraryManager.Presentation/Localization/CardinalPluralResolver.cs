using System.Globalization;

namespace MusicLibraryManager.Presentation;

public enum CardinalPluralCategory
{
    Zero,
    One,
    Two,
    Few,
    Many,
    Other,
}

/// <summary>
/// Resolves integer cardinal-count categories without changing the formatting
/// culture. Shipping locales use One/Other or Other; the complete category set
/// keeps resource selection extensible for languages with richer plural rules.
/// </summary>
public static class CardinalPluralResolver
{
    private static readonly CardinalPluralCategory[]
        OneOther =
        [
            CardinalPluralCategory.One,
            CardinalPluralCategory.Other,
        ];

    private static readonly CardinalPluralCategory[]
        OtherOnly =
        [
            CardinalPluralCategory.Other,
        ];

    private static readonly CardinalPluralCategory[]
        OneFewOther =
        [
            CardinalPluralCategory.One,
            CardinalPluralCategory.Few,
            CardinalPluralCategory.Other,
        ];

    private static readonly CardinalPluralCategory[]
        OneFewManyOther =
        [
            CardinalPluralCategory.One,
            CardinalPluralCategory.Few,
            CardinalPluralCategory.Many,
            CardinalPluralCategory.Other,
        ];

    private static readonly CardinalPluralCategory[]
        OneTwoFewOther =
        [
            CardinalPluralCategory.One,
            CardinalPluralCategory.Two,
            CardinalPluralCategory.Few,
            CardinalPluralCategory.Other,
        ];

    private static readonly CardinalPluralCategory[]
        AllCategories =
        [
            CardinalPluralCategory.Zero,
            CardinalPluralCategory.One,
            CardinalPluralCategory.Two,
            CardinalPluralCategory.Few,
            CardinalPluralCategory.Many,
            CardinalPluralCategory.Other,
        ];

    public static CardinalPluralCategory Resolve(
        long count,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        ulong value = Magnitude(count);
        ulong moduloTen = value % 10;
        ulong moduloHundred = value % 100;

        return culture.TwoLetterISOLanguageName
            .ToLowerInvariant() switch
        {
            "ja" or "ko" or "zh" =>
                CardinalPluralCategory.Other,
            "fr" when value is 0 or 1 =>
                CardinalPluralCategory.One,
            "pt" when culture.Name.Equals(
                    "pt-BR",
                    StringComparison.OrdinalIgnoreCase) &&
                value is 0 or 1 =>
                CardinalPluralCategory.One,
            "ar" => ResolveArabic(
                value,
                moduloHundred),
            "ru" or "uk" => ResolveEastSlavic(
                moduloTen,
                moduloHundred),
            "pl" => ResolvePolish(
                value,
                moduloTen,
                moduloHundred),
            "cs" or "sk" when value == 1 =>
                CardinalPluralCategory.One,
            "cs" or "sk" when value is >= 2 and <= 4 =>
                CardinalPluralCategory.Few,
            "sl" when moduloHundred == 1 =>
                CardinalPluralCategory.One,
            "sl" when moduloHundred == 2 =>
                CardinalPluralCategory.Two,
            "sl" when moduloHundred is 3 or 4 =>
                CardinalPluralCategory.Few,
            "cy" when value == 0 =>
                CardinalPluralCategory.Zero,
            "cy" when value == 1 =>
                CardinalPluralCategory.One,
            "cy" when value == 2 =>
                CardinalPluralCategory.Two,
            "cy" when value == 3 =>
                CardinalPluralCategory.Few,
            "cy" when value == 6 =>
                CardinalPluralCategory.Many,
            _ when value == 1 =>
                CardinalPluralCategory.One,
            _ => CardinalPluralCategory.Other,
        };
    }

    public static IReadOnlyList<
        CardinalPluralCategory> RequiredCategories(
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return culture.TwoLetterISOLanguageName
            .ToLowerInvariant() switch
        {
            "ja" or "ko" or "zh" => OtherOnly,
            "ar" or "cy" => AllCategories,
            "ru" or "uk" or "pl" =>
                OneFewManyOther,
            "cs" or "sk" => OneFewOther,
            "sl" => OneTwoFewOther,
            _ => OneOther,
        };
    }

    public static string ResourceKey(
        string key,
        long count,
        CultureInfo culture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return $"{key}.{Resolve(count, culture)}";
    }

    private static CardinalPluralCategory ResolveArabic(
        ulong value,
        ulong moduloHundred) =>
        value switch
        {
            0 => CardinalPluralCategory.Zero,
            1 => CardinalPluralCategory.One,
            2 => CardinalPluralCategory.Two,
            _ when moduloHundred is >= 3 and <= 10 =>
                CardinalPluralCategory.Few,
            _ when moduloHundred is >= 11 and <= 99 =>
                CardinalPluralCategory.Many,
            _ => CardinalPluralCategory.Other,
        };

    private static CardinalPluralCategory
        ResolveEastSlavic(
        ulong moduloTen,
        ulong moduloHundred)
    {
        if (moduloTen == 1 &&
            moduloHundred != 11)
            return CardinalPluralCategory.One;
        if (moduloTen is >= 2 and <= 4 &&
            moduloHundred is < 12 or > 14)
            return CardinalPluralCategory.Few;
        if (moduloTen == 0 ||
            moduloTen is >= 5 and <= 9 ||
            moduloHundred is >= 11 and <= 14)
            return CardinalPluralCategory.Many;
        return CardinalPluralCategory.Other;
    }

    private static CardinalPluralCategory ResolvePolish(
        ulong value,
        ulong moduloTen,
        ulong moduloHundred)
    {
        if (value == 1)
            return CardinalPluralCategory.One;
        if (moduloTen is >= 2 and <= 4 &&
            moduloHundred is < 12 or > 14)
            return CardinalPluralCategory.Few;
        if (moduloTen is 0 or 1 ||
            moduloTen is >= 5 and <= 9 ||
            moduloHundred is >= 12 and <= 14)
            return CardinalPluralCategory.Many;
        return CardinalPluralCategory.Other;
    }

    private static ulong Magnitude(long value) =>
        value < 0
            ? (ulong)(-(value + 1)) + 1
            : (ulong)value;
}
