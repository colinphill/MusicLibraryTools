using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed partial class ShellLocalizationSourceTests
{
    private static readonly string[] RuntimeUiFiles =
    [
        "MusicLibraryManager.Presentation/AppActivityService.cs",
        "MusicLibraryManager.Presentation/HomeViewModel.cs",
        "MusicLibraryManager.Presentation/IndexingViewModel.cs",
        "MusicLibraryManager.Presentation/ShellViewModel.cs",
        "MusicLibraryManager/Services/DialogService.cs",
        "MusicLibraryManager/Services/WorkflowIntegrationService.cs",
    ];

    [Fact]
    public void Shell_and_workflow_integration_ui_text_uses_the_catalog()
    {
        string repositoryRoot =
            FindRepositoryRoot();
        HashSet<string> resourceKeys =
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
        var errors = new List<string>();

        foreach (string relativePath in
                 RuntimeUiFiles)
        {
            string source =
                File.ReadAllText(
                    Path.Combine(
                        repositoryRoot,
                        relativePath.Replace(
                            '/',
                            Path.DirectorySeparatorChar)));

            foreach (Match literal in
                     StringLiteralPattern()
                         .Matches(source))
            {
                string value =
                    literal.Groups["value"]
                        .Value;
                if (ProsePattern().IsMatch(
                        value))
                {
                    errors.Add(
                        $"{relativePath}: unlocalized prose literal \"{value}\"");
                }
            }

            foreach (Match lookup in
                     ResourceLookupPattern()
                         .Matches(source))
            {
                string key =
                    lookup.Groups["key"].Value;
                bool count =
                    lookup.Groups["method"]
                        .Value
                        .EndsWith(
                            "Count",
                            StringComparison.Ordinal);
                if (count)
                {
                    Require(
                        resourceKeys,
                        key + ".One",
                        relativePath,
                        errors);
                    Require(
                        resourceKeys,
                        key + ".Other",
                        relativePath,
                        errors);
                }
                else
                {
                    Require(
                        resourceKeys,
                        key,
                        relativePath,
                        errors);
                }
            }
        }

        Assert.True(
            errors.Count == 0,
            string.Join(
                Environment.NewLine,
                errors));
    }

    private static void Require(
        HashSet<string> resourceKeys,
        string key,
        string relativePath,
        List<string> errors)
    {
        if (!resourceKeys.Contains(key))
            errors.Add(
                $"{relativePath}: missing resource {key}");
    }

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

    [GeneratedRegex(
        "\"(?<value>(?:\\\\.|[^\"\\\\])*)\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        StringLiteralPattern();

    [GeneratedRegex(
        "[A-Za-z]+\\s+[A-Za-z]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        ProsePattern();

    [GeneratedRegex(
        "(?:_localization\\.|LocalizedText\\.)?(?<method>Get|Text|Format|FormatCount)\\(\\s*\"(?<key>[A-Za-z][A-Za-z0-9.]+)\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        ResourceLookupPattern();
}
