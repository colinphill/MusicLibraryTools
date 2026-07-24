using System.Globalization;
using System.Text;

namespace MusicLibraryManager.Presentation;

public sealed record TextDifferenceSegment(string Text, bool IsDifferent);

internal sealed record TextDifferenceResult(
    IReadOnlyList<TextDifferenceSegment> Before,
    IReadOnlyList<TextDifferenceSegment> After,
    string? UnicodeDetails);

internal static class TextDifference
{
    public static TextDifferenceResult Compare(
        string? before,
        string after,
        ILocalizationService? localization = null)
    {
        Rune[] beforeRunes = (before ?? "").EnumerateRunes().ToArray();
        Rune[] afterRunes = after.EnumerateRunes().ToArray();
        int[,] common = LongestCommonSubsequence(beforeRunes, afterRunes);
        var beforeChanged = Enumerable.Repeat(true, beforeRunes.Length).ToArray();
        var afterChanged = Enumerable.Repeat(true, afterRunes.Length).ToArray();

        int beforeIndex = 0;
        int afterIndex = 0;
        while (beforeIndex < beforeRunes.Length && afterIndex < afterRunes.Length)
        {
            if (beforeRunes[beforeIndex] == afterRunes[afterIndex])
            {
                beforeChanged[beforeIndex++] = false;
                afterChanged[afterIndex++] = false;
            }
            else if (common[beforeIndex + 1, afterIndex] >=
                     common[beforeIndex, afterIndex + 1])
            {
                beforeIndex++;
            }
            else
            {
                afterIndex++;
            }
        }

        bool hasDifference = !string.Equals(before, after, StringComparison.Ordinal);
        return new TextDifferenceResult(
            BuildSegments(beforeRunes, beforeChanged,
                string.IsNullOrEmpty(before)
                    ? Text(
                        localization,
                        "Health.TextDifference.Missing")
                    : null,
                hasDifference,
                localization),
            BuildSegments(afterRunes, afterChanged,
                after.Length == 0
                    ? Text(
                        localization,
                        "Health.TextDifference.Empty")
                    : null,
                hasDifference,
                localization),
            hasDifference ? BuildUnicodeDetails(
                beforeRunes,
                beforeChanged,
                afterRunes,
                afterChanged,
                localization) : null);
    }

    private static int[,] LongestCommonSubsequence(Rune[] before, Rune[] after)
    {
        var lengths = new int[before.Length + 1, after.Length + 1];
        for (int left = before.Length - 1; left >= 0; left--)
        for (int right = after.Length - 1; right >= 0; right--)
            lengths[left, right] = before[left] == after[right]
                ? lengths[left + 1, right + 1] + 1
                : Math.Max(lengths[left + 1, right], lengths[left, right + 1]);
        return lengths;
    }

    private static IReadOnlyList<TextDifferenceSegment> BuildSegments(
        Rune[] runes,
        bool[] changed,
        string? emptyLabel,
        bool hasDifference,
        ILocalizationService? localization)
    {
        if (runes.Length == 0)
            return emptyLabel is null
                ? []
                : [new TextDifferenceSegment(emptyLabel, hasDifference)];

        var segments = new List<TextDifferenceSegment>();
        var text = new StringBuilder();
        bool segmentChanged = changed[0];
        for (int index = 0; index < runes.Length; index++)
        {
            if (changed[index] != segmentChanged)
            {
                segments.Add(new TextDifferenceSegment(
                    ShowWhitespace(
                        text.ToString(),
                        localization),
                    segmentChanged));
                text.Clear();
                segmentChanged = changed[index];
            }
            text.Append(runes[index].ToString());
        }
        segments.Add(new TextDifferenceSegment(
            ShowWhitespace(
                text.ToString(),
                localization),
            segmentChanged));
        return segments;
    }

    private static string BuildUnicodeDetails(
        Rune[] before,
        bool[] beforeChanged,
        Rune[] after,
        bool[] afterChanged,
        ILocalizationService? localization) =>
        Format(
            localization,
            "Health.TextDifference.UnicodeDetails",
            DescribeChanged(
                before,
                beforeChanged,
                localization),
            DescribeChanged(
                after,
                afterChanged,
                localization));

    private static string DescribeChanged(
        Rune[] runes,
        bool[] changed,
        ILocalizationService? localization)
    {
        Rune[] differences = runes.Where((_, index) => changed[index]).ToArray();
        if (differences.Length == 0)
            return Text(
                localization,
                "Health.TextDifference.NoCharacter");

        const int limit = 12;
        string description = string.Join(
            " · ",
            differences.Take(limit).Select(
                rune => Describe(
                    rune,
                    localization)));
        return differences.Length <= limit
            ? description
            : description +
              FormatCount(
                  localization,
                  "Health.TextDifference.More",
                  differences.Length - limit);
    }

    private static string Describe(
        Rune rune,
        ILocalizationService? localization)
    {
        string codePoint = $"U+{rune.Value:X4}";
        string? knownName = rune.Value switch
        {
            0x0009 => "CharacterTabulation",
            0x000A => "LineFeed",
            0x000D => "CarriageReturn",
            0x0020 => "Space",
            0x002D => "HyphenMinus",
            0x00A0 => "NoBreakSpace",
            0x2007 => "FigureSpace",
            0x200B => "ZeroWidthSpace",
            0x200C => "ZeroWidthNonJoiner",
            0x200D => "ZeroWidthJoiner",
            0x2010 => "Hyphen",
            0x2011 => "NonBreakingHyphen",
            0x2018 => "LeftSingleQuotationMark",
            0x2019 => "RightSingleQuotationMark",
            0x201C => "LeftDoubleQuotationMark",
            0x201D => "RightDoubleQuotationMark",
            0x202F => "NarrowNoBreakSpace",
            0x2212 => "MinusSign",
            0xFEFF => "ZeroWidthNoBreakSpace",
            _ => null,
        };
        if (knownName is not null)
            return Format(
                localization,
                "Health.TextDifference.KnownCodePoint",
                codePoint,
                Text(
                    localization,
                    $"Health.TextDifference.UnicodeName.{knownName}"));

        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        string categoryName = Text(
            localization,
            $"Health.TextDifference.UnicodeCategory.{category}");
        return category is UnicodeCategory.Control or UnicodeCategory.Format or
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.EnclosingMark
            ? Format(
                localization,
                "Health.TextDifference.CategoryCodePoint",
                codePoint,
                categoryName)
            : Format(
                localization,
                "Health.TextDifference.VisibleCodePoint",
                codePoint,
                rune,
                categoryName);
    }

    private static string ShowWhitespace(
        string value,
        ILocalizationService? localization) =>
        value.Replace(
            "\u00A0",
            Text(
                localization,
                "Health.TextDifference.NoBreakSpaceMarker"),
            StringComparison.Ordinal);

    private static string Text(
        ILocalizationService? localization,
        string key) =>
        localization?.Get(key) ??
        LocalizedText.Get(key);

    private static string Format(
        ILocalizationService? localization,
        string key,
        params object?[] arguments) =>
        localization?.Format(
            key,
            arguments) ??
        LocalizedText.Format(
            key,
            arguments);

    private static string FormatCount(
        ILocalizationService? localization,
        string key,
        long count,
        params object?[] arguments) =>
        localization?.FormatCount(
            key,
            count,
            arguments) ??
        LocalizedText.FormatCount(
            key,
            count,
            arguments);
}
