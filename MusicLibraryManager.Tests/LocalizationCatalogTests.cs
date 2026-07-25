using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MusicFileUtilities;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using MusicLibraryTools.Localization;
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

    private static readonly string[]
        MandatoryProtectedLiteralTokens =
        [
            "Music Library Manager",
            "MusicLibraryManager",
            "MusicLibraryTools",
            "MusicBrainz",
            "Cover Art Archive",
            "Discogs",
            "AcoustID",
            "Avalonia UI",
            "SkiaSharp",
            "Windows Media Player",
            "FFmpeg",
            "ffprobe",
            "WavPack",
            "MAC",
            "OptimFROG",
            "fpcalc",
            "Chromaprint",
            "ID3v1",
            "ID3v2.2",
            "ID3v2.3",
            "ID3v2.4",
            "APEv2",
            "Vorbis Comment",
            "ReplayGain",
            "Matroska",
            "WebM",
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
            "SHA-256",
            "ISO-8859-1",
            "Windows-1252",
            "UTF-8",
            "UTF-16",
            "UTF-16 LE",
            "UTF-16 BE",
            "Latin-1",
            "CRLF",
            "LF",
            "CBR",
            "ABR",
            "VBR",
            "PCM",
            "RF64",
            "M3U",
            "M3U8",
            "WPL",
            "CSV",
            "HTML",
            "RTF",
            "JSON",
            "XML",
            "BPM",
            "ISRC",
            "URI",
            "URL",
            "CLI",
        ];

    public static IEnumerable<object[]> ShippingCultureData =>
        ShippingCultures.Select(
            cultureName => new object[] { cultureName });

    public static IEnumerable<object[]> ProtectedTermMutationData
    {
        get
        {
            yield return
            [
                "product name",
                "Open Windows Media Player (WPL).",
                "Öffnen Sie Windows Medien Player (WPL).",
            ];
            yield return
            [
                "package name",
                "Avalonia UI uses SkiaSharp.",
                "Avalonia Benutzeroberfläche uses SkiaSharp.",
            ];
            yield return
            [
                "tool name",
                "Run FFmpeg and ffprobe.",
                "Run FFMpeg and ffprobe.",
            ];
            yield return
            [
                "codec",
                "Store FLAC in Matroska.",
                "Store Flac in Matroska.",
            ];
            yield return
            [
                "tag format",
                "Write ID3v2.4 and APEv2.",
                "Write ID3v2.3 and APEv2.",
            ];
            yield return
            [
                "keyboard gesture",
                "Press Ctrl+Shift+Z.",
                "Press Ctrl+Umschalt+Z.",
            ];
            yield return
            [
                "CLI flag",
                "Run --check.",
                "Run --verify.",
            ];
            yield return
            [
                "path",
                @"Use C:\Tools\ffmpeg.exe.",
                @"Use C:\Werkzeuge\ffmpeg.exe.",
            ];
            yield return
            [
                "MIME type",
                "Accept image/jpeg.",
                "Accept image/jpg.",
            ];
        }
    }

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
            IReadOnlyList<string> mismatches =
                LocalizationProtectedTerms.FindMismatches(
                    source,
                    translated);
            Assert.True(
                mismatches.Count == 0,
                $"{cultureName}:{key} changed protected terms: " +
                string.Join("; ", mismatches));
        }
    }

    [Fact]
    public void Protected_literal_glossary_contains_independent_mandatory_baseline()
    {
        Assert.All(
            MandatoryProtectedLiteralTokens,
            token => Assert.Contains(
                token,
                LocalizationProtectedTerms.LiteralTokens));
    }

    [Theory]
    [MemberData(nameof(ShippingCultureData))]
    public void Windows_media_player_uses_its_canonical_name_in_every_satellite(
        string cultureName)
    {
        CatalogEntry entry = Assert.Single(
            LoadCatalog(cultureName),
            item => string.Equals(
                item.Key,
                "Technical.PlaylistFormat.Wpl",
                StringComparison.Ordinal));

        Assert.Equal(
            "Windows Media Player (WPL)",
            entry.Value);
    }

    [Theory]
    [MemberData(nameof(ProtectedTermMutationData))]
    public void Protected_term_contract_rejects_independent_mutation_examples(
        string category,
        string source,
        string mutated)
    {
        Assert.True(
            LocalizationProtectedTerms.FindMismatches(
                    source,
                    mutated)
                .Count > 0,
            $"The independent {category} mutation was not rejected.");
    }

    [Fact]
    public void Protected_term_contract_covers_gestures_mime_and_contextual_keys()
    {
        const string source =
            "Use Meta+F8, image/jpeg, or an Avalonia key name such as P, Enter, Delete, or F8.";

        Assert.Empty(
            LocalizationProtectedTerms.FindMismatches(
                source,
                "Verwenden Sie Meta+F8, image/jpeg oder einen Avalonia-Tastennamen wie P, Enter, Delete oder F8."));
        Assert.NotEmpty(
            LocalizationProtectedTerms.FindMismatches(
                source,
                "Verwenden Sie Strg+F8, bild/jpeg oder einen Tastennamen wie P, Eingabe, Löschen oder F8."));
    }

    [Fact]
    public void Ordinary_labels_are_not_treated_as_invariant_identifiers()
    {
        Assert.Empty(
            LocalizationProtectedTerms.FindMismatches(
                "Delete the Name and Encoder values, then select OK.",
                "Löschen Sie die Werte für Name und Encoder, und wählen Sie dann OK."));
        Assert.Empty(
            LocalizationProtectedTerms.FindMismatches(
                "Set an oversized width or height near 1,000 pixels.",
                "Legen Sie eine Überbreite oder -höhe nahe 1.000 Pixel fest."));
    }

    [Fact]
    public void Source_cli_flags_are_preserved_without_matching_hyphenated_prose()
    {
        const string source =
            "Run --check before using -version.";

        Assert.Empty(
            LocalizationProtectedTerms.FindMismatches(
                source,
                "Führen Sie --check aus, bevor Sie -version verwenden."));
        Assert.NotEmpty(
            LocalizationProtectedTerms.FindMismatches(
                source,
                "Führen Sie die Prüfung aus, bevor Sie die Version anzeigen."));
    }

    [Fact]
    public void Semantic_technical_choices_share_resource_keys()
    {
        Assert.Equal(
            "Technical.Format.Csv",
            TechnicalLabelResourceKeys.For(ReportFormat.Csv));
        Assert.Equal(
            "Technical.Encoding.Utf8",
            TechnicalLabelResourceKeys.For(ReportEncoding.Utf8));
        Assert.Equal(
            "Technical.Encoding.Utf16Le",
            TechnicalLabelResourceKeys.For(
                PlaylistWorkspaceEncoding.Utf16LittleEndian));
        Assert.Equal(
            "Technical.LineEnding.CrLf",
            TechnicalLabelResourceKeys.For(
                PlaylistLineEnding.CrLf));
        Assert.Equal(
            "Technical.Id3Version.V24",
            TechnicalLabelResourceKeys.For(
                ID3v2Version.V24));
        Assert.Equal(
            "Technical.Encoding.Latin1",
            TechnicalLabelResourceKeys.For(
                ID3TextEncodingPolicy.Latin1));
        Assert.Equal(
            "Technical.Encoding.Utf16Be",
            TechnicalLabelResourceKeys.ForSettingsChoice(
                "PlaylistEncoding",
                "utf-16be"));
        Assert.Null(
            TechnicalLabelResourceKeys.For(
                ReportFormat.Text));
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
        Dictionary<string, string> neutral = LoadCatalog()
            .ToDictionary(
                entry => entry.Key,
                entry => entry.Value,
                StringComparer.Ordinal);
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

                string value =
                    LocalizationProtectedTerms.DynamicTokenPattern
                    .Replace(entry.Value, "");
                foreach (string token in
                          CjkAllowedLatinTokens)
                    value = ProtectedTokenPattern(
                            token,
                            ignoreCase: false)
                        .Replace(value, "");
                foreach (string token in
                         LocalizationProtectedTerms
                             .SourceDerivedTokens(
                                 neutral[entry.Key]))
                    value = ProtectedTokenPattern(
                            token,
                            ignoreCase: false)
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
        .. LocalizationProtectedTerms.CjkAllowedLatinTokens,
    ];

    private sealed record CatalogEntry(
        string Key,
        string Value);
}
