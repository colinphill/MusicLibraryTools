using System.Xml.Linq;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class
    WorkbenchDrawerAndEmptyStateSourceTests
{
    [Fact]
    public void Workbench_inspector_uses_one_explicit_scroll_owner_and_the_shared_sticky_footer()
    {
        XDocument inspector =
            LoadView("SelectionInspectorView.axaml");
        XDocument workbenchInspector =
            LoadWorkbenchSection(
                "WorkbenchInspectorDrawerView.axaml");

        XElement scroll =
            Assert.Single(
                inspector.Descendants(),
                element =>
                    element.Name.LocalName ==
                    "ScrollViewer");
        Assert.Equal(
            "InspectorContent",
            Attribute(scroll, "Name"));
        Assert.DoesNotContain(
            workbenchInspector.Descendants(),
            element =>
                element.Name.LocalName ==
                "ScrollViewer");

        XElement footer =
            inspector
                .Descendants()
                .Single(element =>
                    Attribute(element, "Name") ==
                    "InspectorStickyFooter");
        Assert.Contains(
            "sticky-footer",
            Attribute(footer, "Classes")
                .Split(
                    ' ',
                    StringSplitOptions
                        .RemoveEmptyEntries));
        Assert.Equal(
            "2",
            Attribute(footer, "Row"));
        Assert.Contains(
            inspector
                .Descendants()
                .Where(element =>
                    element.Name.LocalName ==
                    "ContentPresenter"),
            presenter =>
                Attribute(presenter, "Name") ==
                "InspectorSupplementaryContent");
        Assert.Contains(
            workbenchInspector.Descendants(),
            element =>
                Attribute(element, "Name") ==
                "WorkbenchTagToolsExpander");
        XElement tagTools =
            Named(
                workbenchInspector,
                "WorkbenchTagToolsExpander");
        Assert.Equal(
            "{Binding DataContext, RelativeSource={RelativeSource AncestorType=local:WorkbenchInspectorDrawerView}}",
            Attribute(
                tagTools,
                "DataContext"));
    }

    [Fact]
    public void Suppressed_sections_remove_every_inspector_entry_point_instead_of_leaving_an_enabled_no_op()
    {
        XDocument view =
            LoadView("WorkbenchView.axaml");
        string code =
            File.ReadAllText(
                Path.Combine(
                    RepositoryRoot,
                    "MusicLibraryManager",
                    "Views",
                    "WorkbenchView.axaml.cs"));

        XElement toggle =
            Named(view, "WorkbenchInspectorToggle");
        XElement menu =
            Named(
                view,
                "WorkbenchMoreInspectorMenuItem");
        Assert.Equal(
            "{DynamicResource Loc.Library.Action.InspectorTooltip}",
            Attribute(toggle, "Tip"));
        Assert.Equal(
            "{DynamicResource Loc.Library.Action.InspectorTooltip}",
            Attribute(menu, "Tip"));

        Assert.Contains(
            "WorkbenchInspectorToggle.IsVisible =",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "WorkbenchInspectorToggle.IsEnabled =",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "WorkbenchMoreInspectorMenuItem.IsVisible =",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "WorkbenchMoreInspectorMenuItem.IsEnabled =",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "WorkbenchSection.Reports or",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "WorkbenchSection.Playlists or",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "WorkbenchSection.Tools or",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "WorkbenchSection.Shortcuts;",
            code,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "WorkbenchAllFieldsSectionView.axaml",
        "AllFieldsEmptyState",
        "AllFieldsAddFilesButton",
        "Workbench.AllFields.Title")]
    [InlineData(
        "WorkbenchFilesSectionView.axaml",
        "FileOperationsEmptyState",
        "FileOperationsAddFilesButton",
        "Workbench.Section.FilesAutomation")]
    public void Source_dependent_sections_have_localized_contextual_setup_states(
        string fileName,
        string stateName,
        string actionName,
        string contextResource)
    {
        XDocument view =
            LoadWorkbenchSection(fileName);
        XElement state =
            Named(view, stateName);
        XElement action =
            Named(view, actionName);

        Assert.Equal(
            "{Binding !HasFiles}",
            Attribute(state, "IsVisible"));
        Assert.Contains(
            state.Descendants(),
            element =>
                Attribute(element, "Text") ==
                $"{{DynamicResource Loc.{contextResource}}}");
        Assert.Contains(
            state.Descendants(),
            element =>
                Attribute(element, "Text") ==
                "{DynamicResource Loc.Workbench.Session.EmptyTitle}");
        Assert.Contains(
            state.Descendants(),
            element =>
                Attribute(element, "Text") ==
                "{DynamicResource Loc.Workbench.Session.EmptyDescription}");
        Assert.Equal(
            "{DynamicResource Loc.Workbench.Action.AddFiles}",
            Attribute(action, "Content"));
        Assert.Equal(
            "{Binding BrowseFilesCommand}",
            Attribute(action, "Command"));
        Assert.DoesNotContain(
            "primary",
            Attribute(action, "Classes")
                .Split(
                    ' ',
                    StringSplitOptions
            .RemoveEmptyEntries));
    }

    [Fact]
    public void Every_workbench_section_declares_its_scroll_empty_state_and_sticky_action_contract()
    {
        (
            string File,
            string EmptyState,
            string Footer,
            string? PageScroll,
            string[] ApprovedAdditionalScrolls)[]
            contracts =
        [
            (
                "WorkbenchSessionSectionView.axaml",
                "SessionEmptyState",
                "SessionStatusFooter",
                null,
                []),
            (
                "WorkbenchBulkOperationSectionView.axaml",
                "BulkEmptyState",
                "BulkStickyFooter",
                "SectionScroll",
                []),
            (
                "WorkbenchAllFieldsSectionView.axaml",
                "AllFieldsEmptyState",
                "AllFieldsStickyFooter",
                "EditorScroll",
                []),
            (
                "WorkbenchOnlineMetadataSectionView.axaml",
                "OnlineMetadataSourceEmptyState",
                "OnlineMetadataStickyFooter",
                "OnlineMetadataStepScroll",
                ["ArtworkEditorScroll"]),
            (
                "WorkbenchReportsSectionView.axaml",
                "ReportsSourceEmptyState",
                "ReportsStickyFooter",
                "EditorScroll",
                []),
            (
                "WorkbenchPlaylistsSectionView.axaml",
                "PlaylistsSourceEmptyState",
                "PlaylistsStickyFooter",
                "EditorScroll",
                []),
            (
                "WorkbenchToolsSectionView.axaml",
                "ToolsSourceEmptyState",
                "ToolsStickyFooter",
                "EditorScroll",
                []),
            (
                "WorkbenchShortcutsSectionView.axaml",
                "ShortcutsEmptyState",
                "ShortcutsStickyFooter",
                "EditorScroll",
                []),
        ];

        foreach (var contract in contracts)
        {
            XDocument view =
                LoadWorkbenchSection(
                    contract.File);
            Assert.NotNull(
                Named(
                    view,
                    contract.EmptyState));
            XElement footer =
                Named(
                    view,
                    contract.Footer);
            Assert.Contains(
                "sticky-footer",
                Attribute(
                    footer,
                    "Classes")
                    .Split(
                        ' ',
                        StringSplitOptions
                            .RemoveEmptyEntries));
            string[] scrollOwners =
            [
                .. view.Descendants()
                    .Where(element =>
                        element.Name.LocalName ==
                            "ScrollViewer")
                    .Select(element =>
                        Attribute(
                            element,
                            "Name")),
            ];
            if (contract.PageScroll is null)
            {
                Assert.Empty(scrollOwners);
            }
            else
            {
                Assert.Equal(
                    [
                        contract.PageScroll,
                        .. contract
                            .ApprovedAdditionalScrolls,
                    ],
                    scrollOwners);
            }
        }

        XDocument files =
            LoadWorkbenchSection(
                "WorkbenchFilesSectionView.axaml");
        Assert.NotNull(
            Named(
                files,
                "FileOperationsEmptyState"));
        XDocument fileEditor =
            LoadView(
                "ReviewedFileOperationEditorView.axaml");
        Assert.Single(
            fileEditor.Descendants(),
            element =>
                element.Name.LocalName ==
                    "ScrollViewer" &&
                Attribute(
                    element,
                    "Name") ==
                    "ReviewedFileOperationFormScroll");
        XElement fileFooter =
            Named(
                fileEditor,
                "ReviewedFileOperationStickyFooter");
        Assert.Contains(
            "sticky-footer",
            Attribute(
                fileFooter,
                "Classes"));

        // Session is an intentional exception: its virtualized
        // AppDataGrid owns scrolling, and its sticky footer reports
        // session state because the section has no mutating workflow
        // action. Online metadata's second ScrollViewer is confined
        // to selected-artwork details inside an approved data surface.
    }

    [Fact]
    public void Advanced_tag_layer_operations_expose_localized_consequence_help_to_sighted_and_assistive_users()
    {
        XDocument view =
            LoadWorkbenchSection(
                "WorkbenchInspectorDrawerView.axaml");
        string[] helpKeys =
        [
            "Workbench.Inspector.CopyPrimaryMetadataHelp",
            "Workbench.Inspector.Id3ConversionHelp",
            "Workbench.Inspector.LayerCopyHelp",
            "Workbench.Inspector.Id3EncodingHelp",
        ];
        foreach (string key in helpKeys)
        {
            Assert.Contains(
                view.Descendants(),
                element =>
                    Attribute(
                        element,
                        "Text") ==
                    $"{{DynamicResource Loc.{key}}}");
        }

        Assert.Contains(
            view.Descendants(),
            element =>
                element.Name.LocalName ==
                    "CheckBox" &&
                Attribute(
                    element,
                    "HelpText") ==
                "{DynamicResource Loc.Workbench.Inspector.CopyPrimaryMetadataHelp}");
        Assert.Contains(
            view.Descendants(),
            element =>
                element.Name.LocalName ==
                    "ComboBox" &&
                Attribute(
                    element,
                    "HelpText") ==
                "{DynamicResource Loc.Workbench.Inspector.Id3EncodingHelp}");
    }

    private static string RepositoryRoot
    {
        get
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
                    return current.FullName;
                current = current.Parent;
            }

            throw new DirectoryNotFoundException(
                "Could not find the MusicLibraryTools repository root.");
        }
    }

    private static XDocument LoadView(
        string fileName) =>
        XDocument.Load(
            Path.Combine(
                RepositoryRoot,
                "MusicLibraryManager",
                "Views",
                fileName));

    private static XDocument LoadWorkbenchSection(
        string fileName) =>
        XDocument.Load(
            Path.Combine(
                RepositoryRoot,
                "MusicLibraryManager",
                "Views",
                "WorkbenchSections",
                fileName));

    private static XElement Named(
        XDocument document,
        string name) =>
        document
            .Descendants()
            .Single(element =>
                Attribute(element, "Name") ==
                name);

    private static string Attribute(
        XElement element,
        string localName) =>
        element
            .Attributes()
            .FirstOrDefault(attribute =>
                attribute.Name.LocalName ==
                    localName ||
                attribute.Name.LocalName.EndsWith(
                    "." + localName,
                    StringComparison.Ordinal))
            ?.Value ??
        "";
}
