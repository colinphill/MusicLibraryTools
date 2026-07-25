using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class ApplicationLocalizationCoverageTests
{
    private static readonly HashSet<string>
        LocalizableAttributes =
    [
        "Text",
        "Content",
        "Header",
        "PlaceholderText",
        "Title",
        "Subtitle",
        "ToolTip.Tip",
        "AutomationProperties.Name",
        "AutomationProperties.HelpText",
        "Label",
    ];

    private static readonly HashSet<InvariantLiteral>
        ApprovedTextInvariants =
    [
        new(
            "MainWindow.axaml",
            "Text",
            "Ctrl+K"),
        new(
            "Views/LibraryView.axaml",
            "Text",
            "Artist:Miles AND NOT Codec:MP3"),
        new(
            "Views/LibraryView.axaml",
            "Text",
            "Album:\"Kind of Blue\""),
        new(
            "Views/ReviewedFileOperationEditorView.axaml",
            "PlaceholderText",
            "{}{Name}{Extension}"),
        new(
            "Views/WorkbenchSections/WorkbenchInspectorDrawerView.axaml",
            "Text",
            "ID3v2"),
        new(
            "Views/WorkbenchSections/WorkbenchInspectorDrawerView.axaml",
            "Text",
            "APEv2"),
        new(
            "Views/WorkbenchSections/WorkbenchInspectorDrawerView.axaml",
            "Text",
            "ID3v1"),
        new(
            "Views/WorkbenchSections/WorkbenchPlaylistsSectionView.axaml",
            "PlaceholderText",
            "{}{Name} - {Group}"),
        new(
            "Views/WorkbenchSections/WorkbenchReportsSectionView.axaml",
            "PlaceholderText",
            "{}{Group}.{Format}"),
        new(
            "Views/WorkbenchSections/WorkbenchToolsSectionView.axaml",
            "PlaceholderText",
            "{}{Files}"),
    ];

    [Fact]
    public void Every_application_xaml_resource_resolves_and_literal_is_approved()
    {
        string repositoryRoot =
            FindRepositoryRoot();
        string applicationRoot = Path.Combine(
            repositoryRoot,
            "MusicLibraryManager");
        HashSet<string> resourceKeys =
            LoadResourceKeys(repositoryRoot);
        var observedInvariants =
            new HashSet<InvariantLiteral>();
        var errors = new List<string>();

        foreach (string path in
                 Directory.EnumerateFiles(
                     applicationRoot,
                     "*.axaml",
                     SearchOption.AllDirectories))
        {
            string relativePath = Path
                .GetRelativePath(
                    applicationRoot,
                    path)
                .Replace(
                    Path.DirectorySeparatorChar,
                    '/');
            XDocument document = XDocument.Load(
                path,
                LoadOptions.SetLineInfo);

            foreach (XAttribute attribute in
                     document.Descendants()
                         .Attributes())
            {
                string value =
                    attribute.Value.Trim();
                if (value.Length == 0)
                    continue;

                foreach (string key in
                         ExtractLocalizedKeys(value))
                {
                    if (!resourceKeys.Contains(key))
                    {
                        errors.Add(
                            $"{relativePath}: unresolved Loc.{key} in {Describe(attribute)}");
                    }
                }

                if (!LocalizableAttributes.Contains(
                        attribute.Name.LocalName) ||
                    IsApprovedMarkup(value))
                {
                    continue;
                }

                var literal = new InvariantLiteral(
                    relativePath,
                    attribute.Name.LocalName,
                    value);
                if (ApprovedTextInvariants.Contains(
                        literal))
                {
                    observedInvariants.Add(literal);
                    continue;
                }

                if (!value.Any(char.IsLetter) ||
                    value == "i")
                    continue;

                errors.Add(
                    $"{relativePath}: unapproved literal {attribute.Name.LocalName}=\"{value}\" at {Describe(attribute)}");
            }

            foreach (XElement text in
                     document.Descendants()
                         .Where(element =>
                             element.Name.LocalName ==
                             "LocalizedFormatTextBlock"))
            {
                string? key = text
                    .Attribute("ResourceKey")
                    ?.Value
                    .Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    errors.Add(
                        $"{relativePath}: LocalizedFormatTextBlock at {Describe(text)} has no ResourceKey.");
                    continue;
                }

                bool countVariant =
                    bool.TryParse(
                        text.Attribute(
                                "UseCountVariant")
                            ?.Value,
                        out bool parsed) &&
                    parsed;
                if (countVariant)
                {
                    RequireResource(
                        resourceKeys,
                        key + ".One",
                        relativePath,
                        text,
                        errors);
                    RequireResource(
                        resourceKeys,
                        key + ".Other",
                        relativePath,
                        text,
                        errors);
                }
                else
                {
                    RequireResource(
                        resourceKeys,
                        key,
                        relativePath,
                        text,
                        errors);
                }
            }
        }

        foreach (InvariantLiteral stale in
                 ApprovedTextInvariants.Except(
                     observedInvariants))
        {
            errors.Add(
                $"Stale XAML invariant allowlist entry: {stale}");
        }

        Assert.True(
            errors.Count == 0,
            string.Join(
                Environment.NewLine,
                errors));
    }

    private static IEnumerable<string>
        ExtractLocalizedKeys(string value)
    {
        const string marker =
            "{DynamicResource Loc.";
        int offset = 0;
        while ((offset = value.IndexOf(
                   marker,
                   offset,
                   StringComparison.Ordinal)) >= 0)
        {
            int keyStart =
                offset + marker.Length;
            int keyEnd = value.IndexOf(
                '}',
                keyStart);
            if (keyEnd < 0)
                yield break;
            string key = value[
                keyStart..keyEnd].Trim();
            if (key.Length > 0)
                yield return key;
            offset = keyEnd + 1;
        }
    }

    private static bool IsApprovedMarkup(
        string value) =>
        value.StartsWith(
            "{Binding",
            StringComparison.Ordinal) ||
        value.StartsWith(
            "{StaticResource",
            StringComparison.Ordinal) ||
        value.StartsWith(
            "{DynamicResource",
            StringComparison.Ordinal) ||
        value.StartsWith(
            "{TemplateBinding",
            StringComparison.Ordinal) ||
        value.StartsWith(
            "{x:",
            StringComparison.Ordinal);

    private static void RequireResource(
        HashSet<string> resourceKeys,
        string key,
        string relativePath,
        XObject source,
        List<string> errors)
    {
        if (!resourceKeys.Contains(key))
        {
            errors.Add(
                $"{relativePath}: unresolved Loc.{key} at {Describe(source)}");
        }
    }

    private static string Describe(
        XObject source)
    {
        if (source is IXmlLineInfo lineInfo &&
            lineInfo.HasLineInfo())
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"line {lineInfo.LineNumber}");
        }

        return "unknown line";
    }

    private static HashSet<string>
        LoadResourceKeys(string repositoryRoot) =>
        XDocument.Load(
                Path.Combine(
                    repositoryRoot,
                    "MusicLibraryManager.Presentation",
                    "Resources",
                    "Strings.resx"))
            .Root!
            .Elements("data")
            .Select(element =>
                (string?)element.Attribute(
                    "name"))
            .Where(key =>
                key is not null)
            .Select(key => key!)
            .ToHashSet(
                StringComparer.Ordinal);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current =
            new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(
                    Path.Combine(
                        current.FullName,
                        "MusicLibraryManager")) &&
                Directory.Exists(
                    Path.Combine(
                        current.FullName,
                        "MusicLibraryManager.Presentation")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find the MusicLibraryTools repository root.");
    }

    private readonly record struct InvariantLiteral(
        string Path,
        string Attribute,
        string Value);
}
