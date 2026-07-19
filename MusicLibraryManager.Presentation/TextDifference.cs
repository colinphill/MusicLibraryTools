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
    public static TextDifferenceResult Compare(string? before, string after)
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
                string.IsNullOrEmpty(before) ? "(missing)" : null, hasDifference),
            BuildSegments(afterRunes, afterChanged,
                after.Length == 0 ? "(empty)" : null, hasDifference),
            hasDifference ? BuildUnicodeDetails(
                beforeRunes, beforeChanged, afterRunes, afterChanged) : null);
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
        bool hasDifference)
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
                segments.Add(new TextDifferenceSegment(ShowWhitespace(text.ToString()), segmentChanged));
                text.Clear();
                segmentChanged = changed[index];
            }
            text.Append(runes[index].ToString());
        }
        segments.Add(new TextDifferenceSegment(ShowWhitespace(text.ToString()), segmentChanged));
        return segments;
    }

    private static string BuildUnicodeDetails(
        Rune[] before,
        bool[] beforeChanged,
        Rune[] after,
        bool[] afterChanged) =>
        $"Before: {DescribeChanged(before, beforeChanged)}\n" +
        $"After:  {DescribeChanged(after, afterChanged)}";

    private static string DescribeChanged(Rune[] runes, bool[] changed)
    {
        Rune[] differences = runes.Where((_, index) => changed[index]).ToArray();
        if (differences.Length == 0)
            return "∅ (no character)";

        const int limit = 12;
        string description = string.Join(" · ", differences.Take(limit).Select(Describe));
        return differences.Length <= limit
            ? description
            : $"{description} · … ({differences.Length - limit} more)";
    }

    private static string Describe(Rune rune)
    {
        string codePoint = $"U+{rune.Value:X4}";
        string? knownName = rune.Value switch
        {
            0x0009 => "CHARACTER TABULATION",
            0x000A => "LINE FEED",
            0x000D => "CARRIAGE RETURN",
            0x0020 => "SPACE",
            0x002D => "HYPHEN-MINUS",
            0x00A0 => "NO-BREAK SPACE",
            0x2007 => "FIGURE SPACE",
            0x200B => "ZERO WIDTH SPACE",
            0x200C => "ZERO WIDTH NON-JOINER",
            0x200D => "ZERO WIDTH JOINER",
            0x2010 => "HYPHEN",
            0x2011 => "NON-BREAKING HYPHEN",
            0x2018 => "LEFT SINGLE QUOTATION MARK",
            0x2019 => "RIGHT SINGLE QUOTATION MARK",
            0x201C => "LEFT DOUBLE QUOTATION MARK",
            0x201D => "RIGHT DOUBLE QUOTATION MARK",
            0x202F => "NARROW NO-BREAK SPACE",
            0x2212 => "MINUS SIGN",
            0xFEFF => "ZERO WIDTH NO-BREAK SPACE",
            _ => null,
        };
        if (knownName is not null)
            return $"{codePoint} {knownName}";

        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.Control or UnicodeCategory.Format or
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.EnclosingMark
            ? $"{codePoint} ({category})"
            : $"{codePoint} ‘{rune}’ ({category})";
    }

    private static string ShowWhitespace(string value) =>
        value.Replace("\u00A0", "⟦NBSP⟧", StringComparison.Ordinal);
}
