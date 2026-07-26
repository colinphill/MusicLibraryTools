using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class EditorSourceReconciliationTests
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

    private static readonly Regex CSharpResourceTokenPattern =
        new(
            "(?:@)?\"(?<key>(?:Column|Fields|Inspector|Library|" +
            "ReviewedFileOperation)\\.[A-Za-z0-9_.]+)\"",
            RegexOptions.CultureInvariant);

    private static readonly Regex XamlResourceTokenPattern =
        new(
            "\\{(?:Dynamic|Static)Resource\\s+Loc\\." +
            "(?<key>[A-Za-z0-9_.]+)(?=\\s|\\})",
            RegexOptions.CultureInvariant);

    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Shared_column_labels_use_their_exact_ui_contexts()
    {
        XDocument allFields = LoadXaml(
            "MusicLibraryManager",
            "Views",
            "WorkbenchSections",
            "WorkbenchAllFieldsSectionView.axaml");
        XElement back = Named(
            allFields,
            "AllFieldsBackButton");
        Assert.Equal(
            "{DynamicResource Loc.Common.Back}",
            Attribute(back, "Content"));
        Assert.DoesNotContain(
            "Loc.Column.Back",
            back.ToString(),
            StringComparison.Ordinal);

        XDocument library = LoadXaml(
            "MusicLibraryManager",
            "Views",
            "LibraryView.axaml");
        XElement displayLabel = Named(
            library,
            "LibraryColumnDisplayLabel");
        XElement displayEditor = Named(
            library,
            "LibraryColumnDisplayLabelEditor");
        Assert.Equal(
            "{DynamicResource Loc.Library.Columns.DisplayLabel}",
            Attribute(displayLabel, "Text"));
        Assert.Equal(
            "{DynamicResource Loc.Library.Columns.DisplayLabel}",
            Attribute(
                displayEditor,
                "AutomationProperties.Name"));

        XElement groupLabel = Named(
            library,
            "LibraryVisualFilterGroupNumberLabel");
        XElement groupEditor = Named(
            library,
            "LibraryVisualFilterGroupNumberEditor");
        Assert.Equal(
            "{DynamicResource Loc.Library.VisualFilter.GroupNumber}",
            Attribute(groupLabel, "Text"));
        Assert.Equal(
            "Wrap",
            Attribute(groupLabel, "TextWrapping"));
        XElement groupGrid =
            groupLabel.Parent?.Parent ??
            throw new InvalidDataException(
                "The visual-filter group field must remain inside its responsive grid.");
        Assert.Equal(
            "2*,3*",
            Attribute(
                groupGrid,
                "ColumnDefinitions"));
        XElement visualFilterLayout =
            Named(
                library,
                "LibraryVisualFilterLayout");
        Assert.Equal(
            "270,*",
            Attribute(
                visualFilterLayout,
                "ColumnDefinitions"));
        string codeBehind = File.ReadAllText(
            FindRepositoryFile(
                "MusicLibraryManager",
                "Views",
                "LibraryView.axaml.cs"));
        Assert.Contains(
            "ApplyVisualFilterLayout(",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "new ColumnDefinitions(\"*\")",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "new RowDefinitions(",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Equal(
            "{DynamicResource Loc.Library.VisualFilter.GroupNumber}",
            Attribute(
                groupEditor,
                "AutomationProperties.Name"));
    }

    [Fact]
    public void Session_format_column_is_singular_and_keeps_its_semantic_key()
    {
        string source = File.ReadAllText(
            FindRepositoryFile(
                "MusicLibraryManager",
                "Views",
                "WorkbenchSections",
                "WorkbenchSessionSectionView.axaml.cs"));

        Assert.Contains(
            "new(\"Format\", L(\"Column.Format\"), \"Format\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "HeaderResourceKey: \"Column.Format\"",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new(\"Format\", L(\"Column.Formats\"), \"Format\"",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Reviewed_file_operation_destination_uses_the_context_property()
    {
        XDocument editor = LoadXaml(
            "MusicLibraryManager",
            "Views",
            "ReviewedFileOperationEditorView.axaml");
        XElement destination = Named(
            editor,
            "ReviewedFileOperationDestination");

        Assert.Equal(
            "{Binding DestinationPlaceholder}",
            Attribute(destination, "PlaceholderText"));
        Assert.DoesNotContain(
            "Loc.ReviewedFileOperation.DestinationPlaceholder",
            editor.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Deprecated_editor_aliases_remain_complete_reviewed_resources_without_runtime_lookups()
    {
        string repositoryRoot = FindRepositoryRoot();
        IReadOnlyDictionary<string, string> neutral =
            LoadCatalog(
                Path.Combine(
                    repositoryRoot,
                    "MusicLibraryManager.Presentation",
                    "Resources",
                    "Strings.resx"));
        IReadOnlyDictionary<string, XElement> manifest =
            XDocument.Load(
                    Path.Combine(
                        repositoryRoot,
                        "BuildTools",
                        "LocalizationCatalogGenerator",
                        "EditorialReviewManifest.xml"))
                .Root!
                .Elements("entry")
                .ToDictionary(
                    entry => (string)entry.Attribute("key")!,
                    StringComparer.Ordinal);

        Assert.Equal(
            64,
            EditorSourceReconciliationContract
                .CompatibilityAliases.Length);
        foreach (string alias in
                 EditorSourceReconciliationContract
                     .CompatibilityAliases)
        {
            Assert.True(
                neutral.TryGetValue(alias, out string? value),
                $"Neutral catalog is missing compatibility alias '{alias}'.");
            Assert.False(
                string.IsNullOrWhiteSpace(value),
                $"Neutral compatibility alias '{alias}' is blank.");
            Assert.True(
                manifest.TryGetValue(alias, out XElement? record),
                $"Review manifest is missing compatibility alias '{alias}'.");
            Assert.Contains(
                (string?)record!.Attribute("status"),
                new[]
                {
                    "EditorialReviewed",
                    "GlossaryReviewed",
                });
            Assert.Matches(
                "^packet:v1:[0-9a-f]{64}:[0-9a-f]{64}$",
                (string?)record.Attribute("disposition") ?? "");
        }

        foreach (string culture in ShippingCultures)
        {
            IReadOnlyDictionary<string, string> satellite =
                LoadCatalog(
                    Path.Combine(
                        repositoryRoot,
                        "MusicLibraryManager.Presentation",
                        "Resources",
                        $"Strings.{culture}.resx"));
            foreach (string alias in
                     EditorSourceReconciliationContract
                         .CompatibilityAliases)
            {
                Assert.True(
                    satellite.TryGetValue(
                        alias,
                        out string? value),
                    $"{culture} is missing compatibility alias '{alias}'.");
                Assert.False(
                    string.IsNullOrWhiteSpace(value),
                    $"{culture} compatibility alias '{alias}' is blank.");
            }
        }

        HashSet<string> runtimeLookups =
            CollectShippingRuntimeLookups(repositoryRoot);
        foreach (string alias in
                 EditorSourceReconciliationContract
                     .CompatibilityAliases)
        {
            Assert.DoesNotContain(alias, runtimeLookups);
        }
    }

    [Fact]
    public void Shipping_source_uses_every_editor_reconciliation_resource_contract()
    {
        string repositoryRoot = FindRepositoryRoot();
        HashSet<string> runtimeLookups =
            CollectShippingRuntimeLookups(repositoryRoot);
        string[] expectedRuntimeKeys =
        [
            .. EditorSourceReconciliationContract
                .AddedOrChangedResources
                .Select(key =>
                    key.EndsWith(
                            ".One",
                            StringComparison.Ordinal) ||
                        key.EndsWith(
                            ".Other",
                            StringComparison.Ordinal)
                        ? key[..key.LastIndexOf('.')]
                        : key)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(14, expectedRuntimeKeys.Length);
        foreach (string key in expectedRuntimeKeys)
            Assert.Contains(key, runtimeLookups);
    }

    private static HashSet<string>
        CollectShippingRuntimeLookups(
            string repositoryRoot)
    {
        var lookups =
            new HashSet<string>(StringComparer.Ordinal);
        foreach (string sourceRootName in
                 new[]
                 {
                     "MusicLibraryManager",
                     "MusicLibraryManager.Presentation",
                 })
        {
            string sourceRoot = Path.Combine(
                repositoryRoot,
                sourceRootName);
            foreach (string path in
                     Directory.EnumerateFiles(
                         sourceRoot,
                         "*",
                         SearchOption.AllDirectories))
            {
                string extension = Path.GetExtension(path);
                if (extension is not ".cs" and not ".axaml")
                    continue;
                string relativePath = Path.GetRelativePath(
                    sourceRoot,
                    path);
                string[] segments = relativePath.Split(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                if (segments.Any(segment =>
                        segment is "bin" or "obj" or "Resources"))
                    continue;

                string source = File.ReadAllText(path);
                Regex pattern = extension == ".cs"
                    ? CSharpResourceTokenPattern
                    : XamlResourceTokenPattern;
                foreach (Match match in pattern.Matches(source))
                    lookups.Add(match.Groups["key"].Value);
            }
        }

        return lookups;
    }

    private static IReadOnlyDictionary<string, string>
        LoadCatalog(
            string path) =>
        XDocument.Load(path)
            .Root!
            .Elements("data")
            .ToDictionary(
                element => (string)element.Attribute("name")!,
                element => element.Element("value")?.Value ?? "",
                StringComparer.Ordinal);

    private static XDocument LoadXaml(
        params string[] segments) =>
        XDocument.Load(
            FindRepositoryFile(segments),
            LoadOptions.PreserveWhitespace);

    private static XElement Named(
        XDocument document,
        string name) =>
        document
            .Descendants()
            .Single(element =>
                (string?)element.Attribute(
                    Xaml + "Name") == name);

    private static string Attribute(
        XElement element,
        string name) =>
        element.Attribute(name)?.Value ??
        throw new InvalidDataException(
            $"Element '{(string?)element.Attribute(Xaml + "Name")}' " +
            $"does not define '{name}'.");

    private static string FindRepositoryFile(
        params string[] segments)
    {
        DirectoryInfo? directory =
            new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate =
                Path.Combine(
                    directory.FullName,
                    Path.Combine(segments));
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file " +
            $"'{Path.Combine(segments)}'.");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory =
            new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(
                    Path.Combine(
                        directory.FullName,
                        "MusicLibraryTools.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the MusicLibraryTools repository root.");
    }
}

internal static class EditorSourceReconciliationContract
{
    public static readonly string[] AddedOrChangedResources =
    [
        "Column.Format",
        "Fields.Activity.Preview.Starting",
        "Fields.Count.FieldChanges.One",
        "Fields.Count.FieldChanges.Other",
        "Fields.Count.FilesWithChanges.One",
        "Fields.Count.FilesWithChanges.Other",
        "Fields.Count.SelectedFiles.One",
        "Fields.Count.SelectedFiles.Other",
        "Fields.Status.PreviewReady",
        "Inspector.Artwork.Shared.One",
        "Inspector.Artwork.Shared.Other",
        "Inspector.Dialog.Discard.Message",
        "Inspector.Dialog.Revert.Message",
        "Inspector.Picker.SaveArtwork",
        "Library.Columns.DisplayLabel",
        "Library.VisualFilter.GroupNumber",
        "ReviewedFileOperation.DestinationFolderPlaceholder",
        "ReviewedFileOperation.QuarantineFolderPlaceholder",
    ];

    public static readonly string[] CompatibilityAliases =
    [
        "Column.Types",
        "Column.Use",
        "Fields.Activity.Save.Starting.One",
        "Fields.Activity.Save.Starting.Other",
        "Fields.Activity.Save.Title",
        "Fields.Status.SaveCancelled",
        "Fields.Status.SaveComplete.One",
        "Fields.Status.SaveComplete.Other",
        "Fields.Status.SaveFailed",
        "Inspector.Activity.PreviewingTracks.One",
        "Inspector.Activity.PreviewingTracks.Other",
        "Inspector.Activity.UpdatingTracks.One",
        "Inspector.Activity.UpdatingTracks.Other",
        "Inspector.Dialog.ApplyReviewed.Message",
        "Inspector.Dialog.ApplyReviewed.Title",
        "Inspector.Dialog.DirectWriteWarning",
        "Inspector.Dialog.OptimizeArtwork.DirectMessage",
        "Inspector.Dialog.OptimizeArtwork.Message",
        "Inspector.Dialog.RemoveArtwork.DirectMessage.One",
        "Inspector.Dialog.RemoveArtwork.DirectMessage.Other",
        "Inspector.Dialog.RemoveArtwork.Message.One",
        "Inspector.Dialog.RemoveArtwork.Message.Other",
        "Inspector.Dialog.ReplaceFrontCover.DirectMessage.One",
        "Inspector.Dialog.ReplaceFrontCover.DirectMessage.Other",
        "Inspector.Dialog.ReplaceFrontCover.Message.One",
        "Inspector.Dialog.ReplaceFrontCover.Message.Other",
        "Inspector.Dialog.SaveConfirmation.ArtworkOnly",
        "Inspector.Dialog.SaveConfirmation.TagsAndArtwork",
        "Inspector.Dialog.SaveConfirmation.TagsOnly",
        "Inspector.Operation.EditLibraryArtworkSet",
        "Inspector.Operation.OptimizeLibraryArtwork",
        "Inspector.Operation.RemoveLibraryArtwork",
        "Inspector.Operation.ReplaceFrontCover",
        "Inspector.Picker.ChooseCoverArtwork",
        "Inspector.Progress.UpdatedOfTotal",
        "Inspector.Status.ArtworkPartialFailure",
        "Inspector.Status.ArtworkPartialFailureRetry",
        "Inspector.Status.ArtworkSaveFailedRetry",
        "Inspector.Status.MetadataFieldsUpdated",
        "Inspector.Status.NoChanges",
        "Inspector.Status.OperationCancelled",
        "Inspector.Status.OperationFailed",
        "Inspector.Status.PreviewBlockers.One",
        "Inspector.Status.PreviewBlockers.Other",
        "Inspector.Status.PreviewNoChanges",
        "Inspector.Status.ReviewedNotApplied",
        "Inspector.Status.ReviewedUpdated.One",
        "Inspector.Status.ReviewedUpdated.Other",
        "Inspector.Status.SaveFailed",
        "Inspector.Status.SavePartialFailure",
        "Inspector.Status.UpdatedArtwork.One",
        "Inspector.Status.UpdatedArtwork.Other",
        "Inspector.Status.UpdatedTagsAndArtwork.One",
        "Inspector.Status.UpdatedTagsAndArtwork.Other",
        "Inspector.Status.UpdatedTagsSummary",
        "ReviewedFileOperation.Activity.Applying",
        "ReviewedFileOperation.DestinationPlaceholder",
        "ReviewedFileOperation.Dialog.Apply.Message.One",
        "ReviewedFileOperation.Dialog.Apply.Message.Other",
        "ReviewedFileOperation.Dialog.Apply.Title",
        "ReviewedFileOperation.Error.PreviewFirst",
        "ReviewedFileOperation.Status.ApplyCancelled",
        "ReviewedFileOperation.Status.ApplyFailed",
        "ReviewedFileOperation.Status.Completed",
    ];
}
