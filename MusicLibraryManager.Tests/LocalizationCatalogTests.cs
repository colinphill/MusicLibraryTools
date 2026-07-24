using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed partial class LocalizationCatalogTests
{
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

    private static IReadOnlyList<CatalogEntry>
        LoadCatalog()
    {
        XDocument document = XDocument.Load(
            Path.Combine(
                FindRepositoryRoot(),
                "MusicLibraryManager.Presentation",
                "Resources",
                "Strings.resx"));
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

    private sealed record CatalogEntry(
        string Key,
        string Value);
}
