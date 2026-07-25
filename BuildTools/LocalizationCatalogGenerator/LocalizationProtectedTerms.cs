using System.Text.RegularExpressions;

namespace MusicLibraryTools.Localization;

/// <summary>
/// Defines the application-owned localization glossary that must remain
/// semantically invariant in every shipping catalog.
/// </summary>
public static class LocalizationProtectedTerms
{
    public static IReadOnlyList<string> LiteralTokens { get; } =
    [
        "Music Library Manager",
        "MusicLibraryManager",
        "MusicLibraryTools",
        "MusicBrainz",
        "Cover Art Archive",
        "Discogs",
        "AcoustID",
        "FFmpeg",
        "ffmpeg",
        "ffprobe",
        "WavPack",
        "MAC",
        "OptimFROG Float",
        "OptimFROG",
        "Matroska",
        "WebM",
        "Vorbis Comment",
        "Vorbis",
        "Glob",
        "glob",
        "ReplayGain",
        "ID3v2.4",
        "ID3v2.3",
        "ID3v2.2",
        "ID3v2",
        "ID3v1",
        "ID3",
        "APEv2",
        "SHA-256",
        "ISO-8859-1",
        "Unicode",
        "Windows-1252",
        "UTF-16LE",
        "UTF-16BE",
        "UTF-16 LE",
        "UTF-16 BE",
        "UTF-16",
        "UTF-8",
        "FLAC",
        "Ogg",
        "Opus",
        "MP3",
        "MP4",
        "AAC",
        "ALAC",
        "WAV",
        "AIFF",
        "DSF",
        "DSD",
        "ASF",
        "WMA",
        "APE",
        "MPC",
        "TTA",
        "TAK",
        "MKA",
        "CD",
        "CUE",
        "EXTINF",
        "JPEG",
        "PNG",
        "ASCII",
        "WAVE",
        "ASIN",
        "BOM",
        "CRLF",
        "LF",
        "LE",
        "CR",
        "ETA",
        "KiB",
        "MiB",
        "KB",
        "MB",
        "GB",
        "TB",
        "kbps",
        "Hz",
        "CPU",
        "CBR",
        "ABR",
        "VBR",
        "ADTS",
        "M4A",
        "PCM",
        "RF64",
        "OFR",
        "OFS",
        "OFF",
        "DualStream",
        "ADB",
        "adb",
        "fpcalc",
        "Chromaprint",
        "Android",
        "Windows Media Player",
        "Windows",
        "Unix",
        "Sonos",
        "Avalonia UI",
        "AvaloniaUI",
        "Avalonia",
        "SkiaSharp",
        "MIT License",
        "ITL",
        "NBSP",
        "GUID",
        "LRA",
        "WPL",
        "SD",
        "DJ",
        "KC",
        "KD",
        "px",
        "loudnorm",
        "ofr",
        "ofs",
        "off",
        "DiscNumLengthLimit",
        "LengthLimit",
        "FileNameWithoutExtension",
        "iTunes",
        "Monkey's Audio",
        "True Audio",
        "Musepack",
        "Latin-1",
        "Latin1",
        "RTF",
        "HTML",
        "CSV",
        "JSON",
        "TSV",
        "XML",
        "M3U8",
        "M3U",
        "PLS",
        "BPM",
        "ISRC",
        "URI",
        "URL",
        "CLI",
        "AND",
        "OR",
        "NOT",
    ];

    public static IReadOnlyList<string> CjkAllowedLatinTokens { get; } =
    [
        .. LiteralTokens,
        "ID",
        "IDs",
        "Ctrl",
        "Cmd",
        "Shift",
        "Alt",
        "Meta",
        "Option",
        "Enter",
        "Delete",
        "Esc",
        "OK",
    ];

    public const string DynamicTokenPatternText =
        @"(?<!\{)\{(?:\d+(?:,[^}:]+)?(?::[^}]*)?|[A-Za-z][A-Za-z0-9]*)\}(?!\})|" +
        @"(?<![\p{L}\p{N}])(?:(?:Ctrl|Cmd|Shift|Alt|Meta|Option)\+)+(?:[A-Za-z0-9]+)(?![\p{L}\p{N}])|" +
        @"(?<![\p{L}\p{N}])F(?:[1-9]|1[0-9]|2[0-4])(?![\p{L}\p{N}])|" +
        @"(?<![\p{L}\p{N}])(?:application|audio|font|image|message|model|multipart|text|video)/[A-Za-z0-9.+-]+(?![\p{L}\p{N}])|" +
        @"(?<![\p{L}\p{N}])[A-Za-z][A-Za-z0-9]*(?:\.[A-Za-z][A-Za-z0-9]*){2,}(?![\p{L}\p{N}])|" +
        @"\.[a-z][a-z0-9]{1,4}(?![\p{L}\p{N}])|" +
        @"\\[nrt]";

    public static Regex DynamicTokenPattern { get; } =
        new(
            DynamicTokenPatternText,
            RegexOptions.IgnoreCase |
            RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> FindMismatches(
        string source,
        string translated)
    {
        var mismatches = new List<string>();
        foreach (string token in LiteralTokens)
        {
            int expected = CountLiteralToken(source, token);
            if (expected == 0)
                continue;
            int actual = CountLiteralToken(translated, token);
            if (expected != actual)
                mismatches.Add(
                    $"literal '{token}' ({expected} expected, {actual} actual)");
        }

        string expectedDynamic = DynamicSignature(source);
        string actualDynamic = DynamicSignature(translated);
        if (!string.Equals(
                expectedDynamic,
                actualDynamic,
                StringComparison.Ordinal))
            mismatches.Add(
                "dynamic tokens differ " +
                $"(expected [{expectedDynamic}], actual [{actualDynamic}])");

        foreach (string sourceDerivedToken in
                 SourceDerivedTokens(source))
        {
            int expected = CountLiteralToken(
                source,
                sourceDerivedToken);
            int actual = CountLiteralToken(
                translated,
                sourceDerivedToken);
            if (expected != actual)
                mismatches.Add(
                    $"source-derived token '{sourceDerivedToken}' " +
                    $"({expected} expected, {actual} actual)");
        }

        return mismatches;
    }

    public static IReadOnlyList<string> ContextualKeyNames(
        string value)
    {
        var results = PressedKeyPattern
            .Matches(value)
            .Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal);

        if (value.Contains(
                "Avalonia key name",
                StringComparison.OrdinalIgnoreCase))
            results.UnionWith(
                NamedKeyPattern
                    .Matches(value)
                    .Select(match => match.Value));

        return results.ToArray();
    }

    public static IReadOnlyList<string> SourceDerivedTokens(
        string value)
    {
        var results = ContextualKeyNames(value)
            .ToHashSet(StringComparer.Ordinal);
        results.UnionWith(
            CliFlagPattern
                .Matches(value)
                .Select(match => match.Value));
        results.UnionWith(
            PathPattern
                .Matches(value)
                .Select(match => match.Value));
        results.UnionWith(
            QuotedIdentifierPattern
                .Matches(value)
                .Select(match =>
                    match.Groups["token"].Value));
        return results.ToArray();
    }

    private static int CountLiteralToken(
        string value,
        string token) =>
        new Regex(
                $@"(?<![A-Za-z0-9])" +
                Regex.Escape(token) +
                @"(?![A-Za-z0-9])",
                RegexOptions.CultureInvariant)
            .Matches(value)
            .Count;

    private static string DynamicSignature(string value) =>
        string.Join(
            "\u001f",
            DynamicTokenPattern
                .Matches(value)
                .Select(match => match.Value)
                .OrderBy(
                    token => token,
                    StringComparer.Ordinal));

    private static Regex NamedKeyPattern { get; } =
        new(
            @"(?<![\p{L}\p{N}])(?:Enter|Delete|Esc|F(?:[1-9]|1[0-9]|2[0-4])|[A-Z])(?![\p{L}\p{N}])",
            RegexOptions.CultureInvariant);

    private static Regex PressedKeyPattern { get; } =
        new(
            @"(?<=\bpress\s)(?:Enter|Delete|Esc|F(?:[1-9]|1[0-9]|2[0-4]))(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase |
            RegexOptions.CultureInvariant);

    private static Regex CliFlagPattern { get; } =
        new(
            @"(?<![\p{L}\p{N}])--?[a-z][a-z0-9-]*(?:=[^\s,;)]+)?(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase |
            RegexOptions.CultureInvariant);

    private static Regex PathPattern { get; } =
        new(
            @"(?:[A-Za-z]:\\|(?<!\S)/)[^\s\r\n,;)]*",
            RegexOptions.CultureInvariant);

    private static Regex QuotedIdentifierPattern { get; } =
        new(
            @"[‘'“""](?<token>[A-Za-z][A-Za-z0-9._-]*)[’'”""](?=\s+(?:alias|identifier|ID)\b)",
            RegexOptions.IgnoreCase |
            RegexOptions.CultureInvariant);
}
