using System.Text.RegularExpressions;
using System.Xml.Linq;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed partial class LocalizationSourceCompletenessTests
{
    private static readonly string[] ScopedXamlFiles =
    [
        "MainWindow.axaml",
        "Views/DialogHost.axaml",
        "Controls/PageHeader.axaml",
        "Controls/PersistedSplitView.axaml",
        "Views/LibraryView.axaml",
        "Views/HealthView.axaml",
        "Views/HomeView.axaml",
        "Views/IngestView.axaml",
        "Views/OperationsView.axaml",
        "Views/DevicesView.axaml",
        "Views/OrganizeView.axaml",
        "Views/AboutView.axaml",
        "Views/ArtworkPreviewWindow.axaml",
        "Views/ReviewedFileOperationEditorView.axaml",
        "Views/FieldsEditorView.axaml",
    ];

    private static readonly string[]
        RuntimeLocalizationFiles =
    [
        "LibraryViewModel.cs",
        "SelectionInspectorViewModel.cs",
    ];

    private static readonly HashSet<string>
        DynamicChoiceKeyPrefixes =
    [
        "Library.Choice.FilterMode",
        "Library.Choice.ImportEmptyCellMode",
        "Library.Choice.OperationScope",
        "Inspector.Artwork.Type",
    ];

    private static readonly HashSet<string> LocalizableAttributes =
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

    private static readonly HashSet<InvariantLiteral> InvariantLiterals =
    [
        new("MainWindow.axaml", "Text", "Ctrl K"),
        new("MainWindow.axaml", "Text", "\u2699"),
        new("Views/DialogHost.axaml", "Content", "\u2715"),
        new("Views/LibraryView.axaml", "Content", "?"),
        new("Views/LibraryView.axaml", "Content", "\u2715"),
        new(
            "Views/LibraryView.axaml",
            "Text",
            "Artist:Miles AND NOT Codec:MP3"),
        new(
            "Views/LibraryView.axaml",
            "Text",
            "Album:\"Kind of Blue\""),
        new(
            "Views/LibraryView.axaml",
            "PlaceholderText",
            "{}{Group}.{Format}"),
        new(
            "Views/LibraryView.axaml",
            "PlaceholderText",
            "{}{Name} - {Group}"),
        new(
            "Views/LibraryView.axaml",
            "PlaceholderText",
            "{}{Files}"),
        new("Views/HealthView.axaml", "PlaceholderText", "0"),
        new("Views/HealthView.axaml", "Text", "i"),
        new("Views/HomeView.axaml", "Text", "1"),
        new("Views/HomeView.axaml", "Text", "2"),
        new("Views/HomeView.axaml", "Text", "3"),
        new("Views/HomeView.axaml", "Text", "4"),
        new("Views/IngestView.axaml", "Text", "i"),
        new("Views/OperationsView.axaml", "Text", "i"),
        new("Views/DevicesView.axaml", "Text", "i"),
        new("Views/DevicesView.axaml", "Text", "!"),
        new(
            "Views/DevicesView.axaml",
            "PlaceholderText",
            "music or /storage/\u2026/Music"),
        new(
            "Views/DevicesView.axaml",
            "PlaceholderText",
            "**/*.tmp"),
        new(
            "Views/ReviewedFileOperationEditorView.axaml",
            "PlaceholderText",
            "{}{Name}{Extension}"),
    ];

    private static readonly HashSet<InvariantLiteral>
        InvariantMarkupFormats =
    [
        new(
            "Views/LibraryView.axaml",
            "Text",
            "{Binding PendingChanges.Count, StringFormat=({0})}"),
    ];

    [Fact]
    public void Scoped_xaml_uses_catalog_resources_or_explicit_invariants()
    {
        string repositoryRoot = FindRepositoryRoot();
        HashSet<string> resourceKeys =
            LoadResourceKeys(repositoryRoot);
        var observedInvariants =
            new HashSet<InvariantLiteral>();
        var observedMarkupFormats =
            new HashSet<InvariantLiteral>();
        var errors = new List<string>();

        foreach (string relativePath in ScopedXamlFiles)
        {
            string path = Path.Combine(
                repositoryRoot,
                "MusicLibraryManager",
                relativePath.Replace(
                    '/',
                    Path.DirectorySeparatorChar));
            XDocument document = XDocument.Load(
                path,
                LoadOptions.SetLineInfo);
            foreach (XAttribute attribute in document
                         .Descendants()
                         .Attributes()
                         .Where(attribute =>
                             LocalizableAttributes.Contains(
                                 attribute.Name.LocalName)))
            {
                string value = attribute.Value.Trim();
                if (value.Length == 0)
                    continue;

                Match resourceMatch =
                    DynamicResourcePattern().Match(value);
                if (resourceMatch.Success)
                {
                    RequireResource(
                        resourceKeys,
                        resourceMatch.Groups["key"].Value,
                        relativePath,
                        attribute,
                        errors);
                    continue;
                }

                if (value.Contains(
                        "StringFormat=",
                        StringComparison.Ordinal))
                {
                    var format = new InvariantLiteral(
                        relativePath,
                        attribute.Name.LocalName,
                        value);
                    if (InvariantMarkupFormats.Contains(format))
                        observedMarkupFormats.Add(format);
                    else
                        errors.Add(
                            $"{relativePath}: unlocalized StringFormat in {attribute.Name.LocalName}: {value}");
                    continue;
                }

                if (IsMarkupExpression(value))
                    continue;

                var literal = new InvariantLiteral(
                    relativePath,
                    attribute.Name.LocalName,
                    value);
                if (InvariantLiterals.Contains(literal))
                    observedInvariants.Add(literal);
                else
                    errors.Add(
                        $"{relativePath}: literal {attribute.Name.LocalName}=\"{value}\"");
            }

            foreach (XElement element in document
                         .Descendants()
                         .Where(element =>
                             element.Name.LocalName ==
                             "LocalizedFormatTextBlock"))
            {
                string? key = element
                    .Attribute("ResourceKey")
                    ?.Value;
                if (string.IsNullOrWhiteSpace(key))
                {
                    errors.Add(
                        $"{relativePath}: LocalizedFormatTextBlock is missing ResourceKey");
                    continue;
                }
                bool useCountVariant =
                    bool.TryParse(
                        element.Attribute(
                                "UseCountVariant")
                            ?.Value,
                        out bool parsed) &&
                    parsed;
                if (useCountVariant)
                {
                    RequireResource(
                        resourceKeys,
                        key + ".One",
                        relativePath,
                        element,
                        errors);
                    RequireResource(
                        resourceKeys,
                        key + ".Other",
                        relativePath,
                        element,
                        errors);
                }
                else
                {
                    RequireResource(
                        resourceKeys,
                        key,
                        relativePath,
                        element,
                        errors);
                }
            }
        }

        foreach (InvariantLiteral stale in
                 InvariantLiterals.Except(observedInvariants))
            errors.Add(
                $"Stale invariant allowlist entry: {stale}");
        foreach (InvariantLiteral stale in
                 InvariantMarkupFormats.Except(
                     observedMarkupFormats))
            errors.Add(
                $"Stale format allowlist entry: {stale}");

        Assert.True(
            errors.Count == 0,
            string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void Scoped_code_created_labels_reference_catalog_keys()
    {
        string repositoryRoot = FindRepositoryRoot();
        HashSet<string> resourceKeys =
            LoadResourceKeys(repositoryRoot);
        var errors = new List<string>();
        int staticColumnCount = 0;
        int localizedColumnCount = 0;

        foreach (string relativeXamlPath in ScopedXamlFiles)
        {
            string relativeCodePath =
                relativeXamlPath + ".cs";
            string path = Path.Combine(
                repositoryRoot,
                "MusicLibraryManager",
                relativeCodePath.Replace(
                    '/',
                    Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                continue;

            string source = File.ReadAllText(path);
            foreach (Match match in
                     CodeResourceKeyPattern().Matches(source))
            {
                string key = match.Groups["key"].Value;
                if (!resourceKeys.Contains(key))
                    errors.Add(
                        $"{relativeCodePath}: missing resource {key}");
            }

            staticColumnCount +=
                StaticColumnDefinitionPattern()
                    .Matches(source)
                    .Count;
            MatchCollection headerKeys =
                HeaderResourceKeyPattern()
                    .Matches(source);
            localizedColumnCount += headerKeys.Count;
            foreach (Match match in headerKeys)
            {
                string key = match.Groups["key"].Value;
                if (!resourceKeys.Contains(key))
                    errors.Add(
                        $"{relativeCodePath}: missing column resource {key}");
            }
        }

        Assert.Equal(
            staticColumnCount,
            localizedColumnCount);
        Assert.True(
            errors.Count == 0,
            string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void Library_and_inspector_runtime_ui_sinks_use_catalog_resources()
    {
        string repositoryRoot = FindRepositoryRoot();
        HashSet<string> resourceKeys =
            LoadResourceKeys(repositoryRoot);
        var errors = new List<string>();
        var countKeys = new HashSet<string>(
            StringComparer.Ordinal);

        foreach (string relativePath in
                 RuntimeLocalizationFiles)
        {
            string path = Path.Combine(
                repositoryRoot,
                "MusicLibraryManager.Presentation",
                relativePath);
            string source = File.ReadAllText(path);
            CollectCountKeys(
                source,
                countKeys);
            RejectLiteralUiSinks(
                relativePath,
                source,
                errors);
            ValidateRuntimeResourceKeys(
                relativePath,
                source,
                resourceKeys,
                countKeys,
                errors);
        }

        string modelsPath = Path.Combine(
            repositoryRoot,
            "MusicLibraryManager.Presentation",
            "Models.cs");
        string modelsSource =
            File.ReadAllText(modelsPath);
        int sliceStart = modelsSource.IndexOf(
            "public partial class LibraryColumnChoice",
            StringComparison.Ordinal);
        int sliceEnd = modelsSource.IndexOf(
            "public partial class IndexTargetEditorRow",
            StringComparison.Ordinal);
        Assert.True(
            sliceStart >= 0 && sliceEnd > sliceStart,
            "Could not locate the Library/Inspector model slice.");
        string modelSlice = modelsSource[
            sliceStart..sliceEnd];
        CollectCountKeys(
            modelSlice,
            countKeys);
        ValidateRuntimeResourceKeys(
            "Models.cs (Library/Inspector slice)",
            modelSlice,
            resourceKeys,
            countKeys,
            errors);

        RequireChoiceResources(
            resourceKeys,
            "Library.Choice.FilterMode",
            Enum.GetNames<FilterMode>(),
            errors);
        RequireChoiceResources(
            resourceKeys,
            "Library.Choice.ImportEmptyCellMode",
            Enum.GetNames<
                DelimitedMetadataEmptyCellMode>(),
            errors);
        RequireChoiceResources(
            resourceKeys,
            "Library.Choice.OperationScope",
            Enum.GetNames<LibraryOperationScope>(),
            errors);
        RequireChoiceResources(
            resourceKeys,
            "Inspector.Artwork.Type",
            Enum.GetNames<
                MusicFileUtilities.ID3v2Util.APICType>(),
            errors,
            ".Label");

        foreach (string key in countKeys)
        {
            if (!resourceKeys.Contains(
                    key + ".One"))
                errors.Add(
                    $"Missing singular runtime resource {key}.One");
            if (!resourceKeys.Contains(
                    key + ".Other"))
                errors.Add(
                    $"Missing plural runtime resource {key}.Other");
        }

        Assert.True(
            errors.Count == 0,
            string.Join(Environment.NewLine, errors));
    }

    private static void CollectCountKeys(
        string source,
        HashSet<string> countKeys)
    {
        foreach (Match match in
                 RuntimeCountKeyPattern().Matches(
                     source))
            countKeys.Add(
                match.Groups["key"].Value);
        if (source.Contains(
                "SetCountStatusText(",
                StringComparison.Ordinal))
        {
            countKeys.Add(
                "Library.Status.Tracks");
            countKeys.Add(
                "Library.Status.TracksPreserved");
        }
    }

    private static void ValidateRuntimeResourceKeys(
        string relativePath,
        string source,
        HashSet<string> resourceKeys,
        HashSet<string> countKeys,
        List<string> errors)
    {
        foreach (Match match in
                 RuntimeResourceKeyPattern().Matches(
                     source))
        {
            string key =
                match.Groups["key"].Value;
            if (countKeys.Contains(key) ||
                DynamicChoiceKeyPrefixes.Contains(key))
                continue;
            if (!resourceKeys.Contains(key))
                errors.Add(
                    $"{relativePath}: missing runtime resource {key}");
        }
    }

    private static void RejectLiteralUiSinks(
        string relativePath,
        string source,
        List<string> errors)
    {
        foreach (Match match in
                 RuntimeUiSinkLiteralPattern().Matches(
                     source))
        {
            string value =
                match.Groups["value"].Value;
            if (value.Length == 0 ||
                SemanticResourceKeyPattern()
                    .IsMatch(value))
                continue;
            errors.Add(
                $"{relativePath}: literal UI sink {match.Groups["sink"].Value}: \"{value}\"");
        }
    }

    private static void RequireChoiceResources<T>(
        HashSet<string> resourceKeys,
        string prefix,
        IEnumerable<T> values,
        List<string> errors,
        string suffix = "")
    {
        foreach (T value in values)
        {
            string key =
                $"{prefix}.{value}{suffix}";
            if (!resourceKeys.Contains(key))
                errors.Add(
                    $"Missing localized choice resource {key}");
        }
    }

    private static void RequireResource(
        HashSet<string> resourceKeys,
        string key,
        string relativePath,
        XObject source,
        List<string> errors)
    {
        if (!resourceKeys.Contains(key))
            errors.Add(
                $"{relativePath}: missing resource {key} used by {source}");
    }

    private static bool IsMarkupExpression(
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

    private static HashSet<string> LoadResourceKeys(
        string repositoryRoot)
    {
        XDocument document = XDocument.Load(
            Path.Combine(
                repositoryRoot,
                "MusicLibraryManager.Presentation",
                "Resources",
                "Strings.resx"));
        return document.Root!
            .Elements("data")
            .Select(element =>
                (string?)element.Attribute("name"))
            .Where(key => key is not null)
            .Select(key => key!)
            .ToHashSet(StringComparer.Ordinal);
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
        "^\\{DynamicResource Loc\\.(?<key>[^}\\s]+)\\}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DynamicResourcePattern();

    [GeneratedRegex(
        "\"(?<key>(?:ArtworkPreview|Column|Common|Devices|Dialog|Health|Ingest|Library|Operations|Organize|Shell)\\.[A-Za-z0-9.]+)\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex CodeResourceKeyPattern();

    [GeneratedRegex(
        "new(?:\\s+AppGridColumnDefinition)?\\s*\\(\\s*\"[^\"]+\"\\s*,\\s*\"[^\"]+\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex StaticColumnDefinitionPattern();

    [GeneratedRegex(
        "HeaderResourceKey:\\s*\"(?<key>[^\"]+)\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex HeaderResourceKeyPattern();

    [GeneratedRegex(
        "(?s)(?:LC|SetCountStatusText|SetCountOperationStatus|SetCountStatus|LocalizedText\\.FormatCount)\\s*\\(\\s*(?:MessageTone\\.[A-Za-z]+\\s*,\\s*)?\"(?<key>[A-Za-z0-9.]+)\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeCountKeyPattern();

    [GeneratedRegex(
        "\"(?<key>(?:Library|Inspector|Column|Common)\\.[A-Za-z0-9.]+)\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeResourceKeyPattern();

    [GeneratedRegex(
        "(?ms)(?<sink>StatusText|OperationStatus|StatusMessage|Overview|ArtworkSummary|FilterError|VisualFilterEditor\\.Status)\\s*=\\s*\\$?\"(?<value>[^\"]*)\"|(?<sink>ConfirmAsync|PickFileAsync|PickFolderAsync|SaveFileAsync|FilePickerType|BeginLibraryOperation|CreateRecipe)\\s*\\(\\s*\\$?\"(?<value>[^\"]*)\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeUiSinkLiteralPattern();

    [GeneratedRegex(
        "^[A-Za-z][A-Za-z0-9]*(?:\\.[A-Za-z0-9]+)+$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SemanticResourceKeyPattern();

    private readonly record struct InvariantLiteral(
        string Path,
        string Attribute,
        string Value);
}
