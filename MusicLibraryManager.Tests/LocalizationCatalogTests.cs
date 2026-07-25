using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed partial class LocalizationCatalogTests
{
    private static readonly string[] ShippingCultures =
    [
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

    private static readonly string[] CjkCultures =
    [
        "ja-JP",
        "ko-KR",
        "zh-CN",
        "zh-TW",
    ];

    private static readonly string[] ProtectedCatalogTokens =
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
        "iTunes",
        "ReplayGain",
        "WavPack",
        "MAC",
        "OptimFROG",
        "Matroska",
        "WebM",
        "Vorbis Comment",
        "Vorbis",
        "ID3v2.4",
        "ID3v2.3",
        "ID3v2",
        "ID3v1",
        "ID3",
        "APEv2",
        "SHA-256",
        "ISO-8859-1",
        "Windows-1252",
        "Unicode",
        "UTF-16LE",
        "UTF-16BE",
        "UTF-16",
        "UTF-8",
        "FLAC",
        "Ogg",
        "Opus",
        "MP3",
        "MP4",
        "AAC",
        "ALAC",
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
        "Float",
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
        "M3U8",
        "M3U",
        "PLS",
        "JSON",
        "CSV",
        "TSV",
        "XML",
        "HTML",
        "BPM",
        "ISRC",
        "URI",
        "URL",
        "Ctrl+Z",
        "Ctrl+Y",
        "Ctrl+Shift+Z",
        "Shift+F10",
        "Avalonia UI",
        "AvaloniaUI",
        "Avalonia",
        "SkiaSharp",
        "MIT License",
    ];

    public static IEnumerable<object[]> ShippingCultureData =>
        ShippingCultures.Select(
            cultureName => new object[] { cultureName });

    [Fact]
    public void Catalog_keys_are_unique_and_format_resources_are_valid()
    {
        IReadOnlyList<CatalogEntry> entries = LoadCatalog();
        string[] duplicateKeys = entries
            .GroupBy(entry => entry.Key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        Assert.Empty(duplicateKeys);
        Assert.All(
            entries,
            entry =>
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(entry.Key));
                Assert.False(
                    string.IsNullOrWhiteSpace(entry.Value));
                if (entry.Key.EndsWith(
                        "Format",
                        StringComparison.Ordinal) ||
                    PlaceholderPattern().IsMatch(
                        entry.Value))
                    _ = CompositeFormat.Parse(entry.Value);
            });
    }

    [Fact]
    public void Neutral_catalog_does_not_expose_resource_identifiers_as_values()
    {
        CatalogEntry[] exposedIdentifiers =
            LoadCatalog()
                .Where(entry =>
                    string.Equals(
                        entry.Key,
                        entry.Value,
                        StringComparison.Ordinal))
                .ToArray();

        Assert.Empty(exposedIdentifiers);
    }

    [Fact]
    public void Catalog_does_not_define_an_invented_application_brand()
    {
        Assert.DoesNotContain(
            LoadCatalog(),
            entry => string.Equals(
                entry.Key,
                "App.Brand",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Count_variants_are_paired_and_preserve_placeholders()
    {
        Dictionary<string, string> catalog = LoadCatalog()
            .ToDictionary(
                entry => entry.Key,
                entry => entry.Value,
                StringComparer.Ordinal);
        string[] bases = catalog.Keys
            .Where(key =>
                key.EndsWith(".One", StringComparison.Ordinal) ||
                key.EndsWith(".Other", StringComparison.Ordinal))
            .Select(key => key[..key.LastIndexOf('.')])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(bases);
        foreach (string key in bases)
        {
            Assert.True(
                catalog.TryGetValue(
                    $"{key}.One",
                    out string? one),
                $"Missing singular count resource {key}.One.");
            Assert.True(
                catalog.TryGetValue(
                    $"{key}.Other",
                    out string? other),
                $"Missing plural count resource {key}.Other.");
            Assert.Equal(
                PlaceholderSignature(one!),
                PlaceholderSignature(other!));
        }
    }

    [Theory]
    [MemberData(nameof(ShippingCultureData))]
    public void Shipping_satellite_has_exact_nonblank_key_and_placeholder_parity(
        string cultureName)
    {
        IReadOnlyList<CatalogEntry> neutral = LoadCatalog();
        IReadOnlyList<CatalogEntry> satellite =
            LoadCatalog(cultureName);
        Dictionary<string, string> neutralByKey =
            neutral.ToDictionary(
                entry => entry.Key,
                entry => entry.Value,
                StringComparer.Ordinal);
        Dictionary<string, string> satelliteByKey =
            satellite.ToDictionary(
                entry => entry.Key,
                entry => entry.Value,
                StringComparer.Ordinal);

        Assert.Equal(
            neutralByKey.Keys.OrderBy(
                key => key,
                StringComparer.Ordinal),
            satelliteByKey.Keys.OrderBy(
                key => key,
                StringComparer.Ordinal));
        Assert.Equal(
            satellite.Count,
            satelliteByKey.Count);

        foreach ((string key, string source) in neutralByKey)
        {
            string translated = satelliteByKey[key];
            Assert.False(
                string.IsNullOrWhiteSpace(translated),
                $"{cultureName}:{key} is blank.");
            Assert.NotEqual(
                key,
                translated);
            Assert.DoesNotContain(
                "⟦untranslated:",
                translated,
                StringComparison.Ordinal);
            Assert.Equal(
                PlaceholderTokenSignature(source),
                PlaceholderTokenSignature(translated));
            if (PlaceholderPattern().IsMatch(translated))
                _ = CompositeFormat.Parse(translated);
        }
    }

    [Theory]
    [MemberData(nameof(ShippingCultureData))]
    public void Shipping_satellite_preserves_protected_catalog_tokens(
        string cultureName)
    {
        Dictionary<string, string> neutral = LoadCatalog()
            .ToDictionary(
                entry => entry.Key,
                entry => entry.Value,
                StringComparer.Ordinal);
        Dictionary<string, string> satellite =
            LoadCatalog(cultureName)
                .ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value,
                    StringComparer.Ordinal);

        foreach ((string key, string source) in neutral)
        {
            string translated = satellite[key];
            foreach (string token in ProtectedCatalogTokens)
            {
                int expected = CountProtectedToken(
                    source,
                    token);
                if (expected == 0)
                    continue;
                int actual = CountProtectedToken(
                    translated,
                    token);
                Assert.True(
                    expected == actual,
                    $"{cultureName}:{key} changed protected token " +
                    $"'{token}' ({expected} expected, {actual} actual).");
            }

            Assert.Equal(
                DynamicProtectedTokenSignature(source),
                DynamicProtectedTokenSignature(translated));
        }
    }

    [Theory]
    [MemberData(nameof(ShippingCultureData))]
    public void Shipping_satellite_has_required_plural_categories(
        string cultureName)
    {
        Dictionary<string, string> catalog =
            LoadCatalog(cultureName)
                .ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value,
                    StringComparer.Ordinal);
        string[] bases = catalog.Keys
            .Where(key =>
                key.EndsWith(".One", StringComparison.Ordinal) ||
                key.EndsWith(".Other", StringComparison.Ordinal))
            .Select(key => key[..key.LastIndexOf('.')])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        IReadOnlyList<CardinalPluralCategory>
            requiredCategories =
                CardinalPluralResolver.RequiredCategories(
                    CultureInfo.GetCultureInfo(
                        cultureName));

        Assert.NotEmpty(bases);
        foreach (string key in bases)
        {
            foreach (CardinalPluralCategory category in
                     requiredCategories)
                Assert.True(
                    catalog.ContainsKey(
                        $"{key}.{category}"),
                    $"{cultureName} is missing {key}.{category}.");
        }
    }

    [Fact]
    public void Cjk_satellites_contain_no_unprotected_latin_prose()
    {
        foreach (string cultureName in CjkCultures)
        {
            foreach (CatalogEntry entry in
                     LoadCatalog(cultureName))
            {
                if (entry.Key.StartsWith(
                        "Culture.",
                        StringComparison.Ordinal) ||
                    CjkInvariantExampleKeys.Contains(
                        entry.Key))
                    continue;

                string value = DynamicProtectedPattern()
                    .Replace(entry.Value, "");
                foreach (string token in
                         CjkAllowedLatinTokens)
                    value = ProtectedTokenPattern(
                            token,
                            ignoreCase: true)
                        .Replace(value, "");
                string[] residual = LatinWordPattern()
                    .Matches(value)
                    .Select(match => match.Value)
                    .Where(word => word.Length > 1)
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                Assert.True(
                    residual.Length == 0,
                    $"{cultureName}:{entry.Key} contains " +
                    $"unprotected Latin text: " +
                    string.Join(", ", residual));
            }
        }
    }

    private static string PlaceholderSignature(
        string value) =>
        string.Join(
            ",",
            PlaceholderPattern()
                .Matches(value)
                .Select(match =>
                    match.Groups["index"].Value)
                .Distinct(StringComparer.Ordinal)
            .OrderBy(index =>
                    int.Parse(index)));

    private static string PlaceholderTokenSignature(
        string value) =>
        string.Join(
            "\u001f",
            PlaceholderPattern()
                .Matches(value)
                .Select(match => match.Value)
                .OrderBy(
                    token => token,
                    StringComparer.Ordinal));

    private static int CountProtectedToken(
        string value,
        string token) =>
        ProtectedTokenPattern(token)
            .Matches(value)
            .Count;

    private static Regex ProtectedTokenPattern(
        string token,
        bool ignoreCase = false) =>
        new(
            $@"(?<![A-Za-z0-9])" +
            Regex.Escape(token) +
            @"(?![A-Za-z0-9])",
            RegexOptions.CultureInvariant |
            (ignoreCase
                ? RegexOptions.IgnoreCase
                : RegexOptions.None));

    private static string DynamicProtectedTokenSignature(
        string value) =>
        string.Join(
            "\u001f",
            DynamicProtectedPattern()
                .Matches(value)
                .Select(match => match.Value)
                .OrderBy(
                    token => token,
                    StringComparer.Ordinal));

    private static IReadOnlyList<CatalogEntry>
        LoadCatalog(string? cultureName = null)
    {
        string fileName = cultureName is null
            ? "Strings.resx"
            : $"Strings.{cultureName}.resx";
        XDocument document = XDocument.Load(
            Path.Combine(
                FindRepositoryRoot(),
                "MusicLibraryManager.Presentation",
                "Resources",
                fileName));
        return document.Root!
            .Elements("data")
            .Select(element => new CatalogEntry(
                (string?)element.Attribute("name") ?? "",
                element.Element("value")?.Value ?? ""))
            .ToArray();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current =
            new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(
                    current.FullName,
                    "MusicLibraryManager")) &&
                Directory.Exists(Path.Combine(
                    current.FullName,
                    "MusicLibraryManager.Presentation")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not find the MusicLibraryTools repository root.");
    }

    [GeneratedRegex(
        "(?<!\\{)\\{(?<index>\\d+)(?:,[^}:]+)?(?::[^}]*)?\\}(?!\\})",
        RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(
        @"(?<!\{)\{\d+(?:,[^}:]+)?(?::[^}]*)?\}(?!\})|" +
        @"(?<!\{)\{[A-Za-z][A-Za-z0-9]*\}(?!\})|" +
        @"(?<![\p{L}\p{N}])[A-Za-z][A-Za-z0-9]*(?:\.[A-Za-z][A-Za-z0-9]*){2,}(?![\p{L}\p{N}])|" +
        @"(?<![\p{L}\p{N}])--?[a-z][a-z0-9-]*(?:=[^\s,;)]+)?|" +
        @"(?:[A-Za-z]:\\|(?<!\S)/)[^\s\r\n,;)]*|" +
        @"\.[a-z0-9]{2,5}(?![\p{L}\p{N}])|" +
        @"\\[nrt]",
        RegexOptions.IgnoreCase |
        RegexOptions.CultureInvariant)]
    private static partial Regex DynamicProtectedPattern();

    [GeneratedRegex(
        @"[A-Za-z][A-Za-z'-]*",
        RegexOptions.CultureInvariant)]
    private static partial Regex LatinWordPattern();

    private static readonly HashSet<string>
        CjkInvariantExampleKeys =
    [
        // This is executable advanced-filter syntax rather than prose.
        // Translating its field/operator tokens would make the example
        // impossible to paste back into the parser.
        "Library.Filter.Placeholder",
        "Library.FilterHelp.Description",
    ];

    private static readonly string[] CjkAllowedLatinTokens =
    [
        .. ProtectedCatalogTokens,
        "MusicLibraryTools",
        "Glob",
        "Vorbis",
        "ReplayGain",
        "CD",
        "EXTINF",
        "JPEG",
        "PNG",
        "ASCII",
        "WAVE",
        "ASIN",
        "BOM",
        "Bom",
        "Latin-1",
        "CRLF",
        "LF",
        "LE",
        "CR",
        "Lf",
        "Cr",
        "ETA",
        "KiB",
        "MiB",
        "KB",
        "MB",
        "GB",
        "TB",
        "kbps",
        "Hz",
        "Name",
        "Extension",
        "Codec",
        "Encoder",
        "SampleRate",
        "BitsPerSample",
        "ADB",
        "adb",
        "fpcalc",
        "Chromaprint",
        "Android",
        "Windows",
        "Unix",
        "Sonos",
        "Avalonia",
        "ITL",
        "NBSP",
        "GUID",
        "LRA",
        "WPL",
        "SD",
        "DJ",
        "KC",
        "KD",
        "UTF",
        "px",
        "loudnorm",
        "ofr",
        "ofs",
        "DiscNumLengthLimit",
        "LengthLimit",
        "FileNameWithoutExtension",
        "Monkey's Audio",
        "True Audio",
        "Musepack",
        "Latin1",
        "Utf16",
        "Utf8",
        "RTF",
        "Rtf",
        "HTML",
        "Html",
        "CSV",
        "Csv",
        "ID",
        "IDs",
        "Id",
        "Ids",
        "OK",
        "Ctrl",
        "Shift",
        "Alt",
        "Meta",
        "Delete",
        "Enter",
        "Esc",
    ];

    private sealed record CatalogEntry(
        string Key,
        string Value);
}
