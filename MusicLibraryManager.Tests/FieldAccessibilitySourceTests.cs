using System.Xml.Linq;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class FieldAccessibilitySourceTests
{
    [Fact]
    public void Shared_field_style_keeps_accessibility_association_enabled()
    {
        string root = FindRepositoryRoot();
        XDocument theme = XDocument.Load(
            Path.Combine(
                root,
                "MusicLibraryManager",
                "Styles",
                "AppTheme.axaml"));
        XElement fieldStyle = Assert.Single(
            theme.Descendants(),
            element =>
                element.Name.LocalName == "Style" &&
                string.Equals(
                    (string?)element.Attribute("Selector"),
                    "StackPanel.field",
                    StringComparison.Ordinal));

        Assert.Contains(
            fieldStyle.Elements(),
            element =>
                element.Name.LocalName == "Setter" &&
                string.Equals(
                    (string?)element.Attribute("Property"),
                    "controls:FieldAccessibility.Associate",
                    StringComparison.Ordinal) &&
                string.Equals(
                    (string?)element.Attribute("Value"),
                    "True",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void Shared_field_behavior_uses_safe_priority_without_layout_or_focus_mutation()
    {
        string source = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "MusicLibraryManager",
                "Controls",
                "FieldAccessibility.cs"));
        string sourceWithoutWhitespace = string.Concat(
            source.Where(
                character =>
                    !char.IsWhiteSpace(character)));

        Assert.Contains(
            "AutomationProperties.LabeledByProperty",
            sourceWithoutWhitespace,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.HelpTextProperty",
            sourceWithoutWhitespace,
            StringComparison.Ordinal);
        Assert.Contains(
            "BindingPriority.Style",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "RefreshScheduler",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SubscribeVisibilityChain",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetVisualAncestors",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visual.IsVisibleProperty",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "LayoutUpdated",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ClearValue",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            ".Focus(",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Focusable =",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IsTabStop",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Workbench_field_wrappers_allow_compact_density_spacing()
    {
        string viewsRoot = Path.Combine(
            FindRepositoryRoot(),
            "MusicLibraryManager",
            "Views");
        string[] offenders = Directory
            .EnumerateFiles(
                viewsRoot,
                "*.axaml",
                SearchOption.AllDirectories)
            .SelectMany(
                path => XDocument
                    .Load(path)
                    .Descendants()
                    .Where(
                        element =>
                            element.Name.LocalName == "StackPanel" &&
                            HasClass(element, "field") &&
                            string.Equals(
                                (string?)element.Attribute("Spacing"),
                                "{DynamicResource AppSpaceLarge}",
                                StringComparison.Ordinal))
                    .Select(
                        _ => Path.GetRelativePath(
                            FindRepositoryRoot(),
                            path)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Settings_field_wrappers_do_not_override_adaptive_spacing()
    {
        string root = FindRepositoryRoot();
        XDocument settings = XDocument.Load(
            Path.Combine(
                root,
                "MusicLibraryManager",
                "Views",
                "SettingsView.axaml"));
        XElement[] offenders = settings
            .Descendants()
            .Where(
                element =>
                    element.Name.LocalName ==
                    "StackPanel" &&
                    HasClass(element, "field") &&
                    string.Equals(
                        (string?)element.Attribute(
                            "Spacing"),
                        "12",
                        StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Workbench_compound_fields_keep_distinct_visible_labels()
    {
        string root = FindRepositoryRoot();

        XDocument playlists = XDocument.Load(
            Path.Combine(
                root,
                "MusicLibraryManager",
                "Views",
                "WorkbenchSections",
                "WorkbenchPlaylistsSectionView.axaml"));
        XElement groupFileNameInput = Assert.Single(
            playlists.Descendants(),
            element =>
                element.Name.LocalName == "TextBox" &&
                ((string?)element.Attribute("Text"))?.Contains(
                    "PlaylistEditor.GroupFileNameTemplate",
                    StringComparison.Ordinal) == true);
        Assert.Equal(
            "{DynamicResource Loc.Workbench.Reports.GroupFileNamePattern}",
            (string?)groupFileNameInput.Attribute(
                "AutomationProperties.Name"));
        Assert.Contains(
            groupFileNameInput.Parent!.Elements(),
            element =>
                element.Name.LocalName == "TextBlock" &&
                string.Equals(
                    (string?)element.Attribute("Text"),
                    "{DynamicResource Loc.Workbench.Reports.GroupFileNamePattern}",
                    StringComparison.Ordinal));

        XDocument reports = XDocument.Load(
            Path.Combine(
                root,
                "MusicLibraryManager",
                "Views",
                "WorkbenchSections",
                "WorkbenchReportsSectionView.axaml"));
        XElement sortTypeInput = Assert.Single(
            reports.Descendants(),
            element =>
                element.Name.LocalName == "ComboBox" &&
                ((string?)element.Attribute("SelectedValue"))?.Contains(
                    "ReportEditor.SortType",
                    StringComparison.Ordinal) == true);
        Assert.Equal(
            "{DynamicResource Loc.Workbench.Reports.SortType}",
            (string?)sortTypeInput.Attribute(
                "AutomationProperties.Name"));
        XElement sortTypeField = sortTypeInput.Ancestors()
            .First(element => HasClass(element, "field"));
        Assert.Contains(
            sortTypeField.Elements(),
            element =>
                element.Name.LocalName == "TextBlock" &&
                string.Equals(
                    (string?)element.Attribute("Text"),
                    "{DynamicResource Loc.Workbench.Reports.SortType}",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void Workbench_inline_recipe_name_and_folder_layout_have_true_field_ownership()
    {
        string root = FindRepositoryRoot();

        XDocument bulk = XDocument.Load(
            Path.Combine(
                root,
                "MusicLibraryManager",
                "Views",
                "WorkbenchSections",
                "WorkbenchBulkOperationSectionView.axaml"));
        XElement recipeNameInput = Assert.Single(
            bulk.Descendants(),
            element =>
                element.Name.LocalName == "TextBox" &&
                string.Equals(
                    (string?)element.Attribute("Text"),
                    "{Binding Name, Mode=TwoWay}",
                    StringComparison.Ordinal));
        XElement inlineField = recipeNameInput.Parent!;
        Assert.Equal("Grid", inlineField.Name.LocalName);
        Assert.Equal(
            "Auto,*",
            (string?)inlineField.Attribute("ColumnDefinitions"));
        Assert.Equal(
            "True",
            (string?)inlineField
                .Attributes()
                .Single(
                    attribute =>
                        attribute.Name.LocalName ==
                        "FieldAccessibility.Associate"));
        Assert.Contains(
            inlineField.Elements(),
            element =>
                element.Name.LocalName == "TextBlock" &&
                HasClass(element, "field-label"));

        XNamespace x =
            "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument transcode = XDocument.Load(
            Path.Combine(
                root,
                "MusicLibraryManager",
                "Views",
                "WorkbenchSections",
                "WorkbenchTranscodeDrawerView.axaml"));
        XElement folderLayout = Assert.Single(
            transcode.Descendants(),
            element =>
                string.Equals(
                    (string?)element.Attribute(x + "Name"),
                    "FolderLayoutOptions",
                    StringComparison.Ordinal));
        Assert.False(HasClass(folderLayout, "field"));
    }

    private static bool HasClass(
        XElement element,
        string className) =>
        ((string?)element.Attribute("Classes"))
            ?.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Contains(
                className,
                StringComparer.Ordinal) == true;

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
}
