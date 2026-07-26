using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed partial class UiModernizationSourceQualityTests
{
    private static readonly HashSet<string>
        ApprovedSpacingValues =
    [
        "0",
        "4",
        "8",
        "12",
        "16",
        "24",
    ];

    private static readonly HashSet<string>
        ApprovedHardCodedTechnicalCopy =
    [
        // Stable gestures, query-language examples, and tag-layer identifiers
        // are deliberately invariant. Everything else visible to a user must
        // come from Loc.* so live culture changes can update an open view.
        "Ctrl+K",
        "Artist:Miles AND NOT Codec:MP3",
        "Album:\"Kind of Blue\"",
        "ID3v1",
        "ID3v2",
        "APEv2",
    ];

    private static readonly HashSet<string>
        VisibleLabelExemptControlNames =
    [
        // These are navigation, search, or command-surface choices whose
        // visible selected value is the durable identity; they are not form
        // fields. Every exception remains individually named and reviewable.
        "SearchBox",
        "HealthResultPicker",
        "JobPicker",
        "RecoveryRunPicker",
        "SettingsCategoryPicker",
        "WorkbenchSectionPicker",
    ];

    private static readonly HashSet<string>
        StructuralClassesWithoutSelectors =
    [
        // These are queried by responsive code or group an already styled
        // composite; they intentionally do not represent visual styles.
        "activity",
        "export-profile-card",
        "field-mapping-fields",
        "responsive-form",
        "responsive-theme-grid",
        "settings-pages",
    ];

    private static readonly HashSet<string>
        TooltipRepeatExemptControlNames =
    [
        // Shell destinations become icon-only when the adaptive rail is
        // collapsed. In that presentation the tooltip supplies the missing
        // visible identity instead of repeating text that remains on screen.
        "HomeNav",
        "LibraryNav",
        "WorkbenchNav",
        "HealthNav",
        "IngestNav",
        "OrganizeNav",
        "DevicesNav",
        "OperationsNav",
        "SettingsNav",
        "AboutNav",
    ];

    private static readonly HashSet<string>
        TooltipValueRevealBindings =
    [
        // These tooltips reveal an untrimmed path, selected device name, or
        // status value whose on-screen rendering is deliberately ellipsized
        // or otherwise space constrained.
        "{Binding File}",
        "{Binding Path}",
        "{Binding DisplayName}",
        "{Binding SelectedMatrix.Root}",
        "{Binding StatusText}",
    ];

    [Fact]
    public void Shipping_catalogs_contain_no_generic_scaffold_copy()
    {
        string resourceRoot =
            Path.Combine(
                FindRepositoryRoot(),
                "MusicLibraryManager.Presentation",
                "Resources");
        string[] scaffolding =
        [
            "Empty Title",
            "Empty Description",
            "Binding Title",
            "Picker Title",
        ];
        var errors = new List<string>();

        foreach (string path in
                 Directory.EnumerateFiles(
                     resourceRoot,
                     "Strings*.resx"))
        {
            XDocument document =
                XDocument.Load(path);
            foreach (XElement entry in
                     document.Root!
                         .Elements("data"))
            {
                string value =
                    entry.Element("value")?.Value ??
                    "";
                foreach (string scaffold in scaffolding)
                {
                    if (value.Contains(
                            scaffold,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add(
                            $"{Path.GetFileName(path)}:{(string?)entry.Attribute("name")}: {value}");
                    }
                }
            }
        }

        Assert.True(
            errors.Count == 0,
            string.Join(
                Environment.NewLine,
                errors));
    }

    [Fact]
    public void Common_browse_uses_an_ellipsis_only_for_picker_commands()
    {
        string repositoryRoot =
            FindRepositoryRoot();
        XDocument neutral =
            XDocument.Load(
                Path.Combine(
                    repositoryRoot,
                    "MusicLibraryManager.Presentation",
                    "Resources",
                    "Strings.resx"));
        string browse =
            neutral.Root!
                .Elements("data")
                .Single(entry =>
                    (string?)entry.Attribute("name") ==
                    "Common.Browse")
                .Element("value")!
                .Value;
        Assert.Equal("Browse…", browse);

        XElement[] uses =
            Directory.EnumerateFiles(
                    Path.Combine(
                        repositoryRoot,
                        "MusicLibraryManager"),
                    "*.axaml",
                    SearchOption.AllDirectories)
                .SelectMany(path =>
                    XDocument.Load(
                            path,
                            LoadOptions.SetLineInfo)
                        .Descendants())
                .Where(element =>
                    element.Attributes().Any(attribute =>
                        attribute.Value.Contains(
                            "Loc.Common.Browse",
                            StringComparison.Ordinal)))
                .ToArray();

        Assert.NotEmpty(uses);
        Assert.All(
            uses,
            use =>
            {
                Assert.Equal(
                    "Button",
                    use.Name.LocalName);
                string command =
                    use.Attributes()
                        .Single(attribute =>
                            attribute.Name.LocalName ==
                            "Command")
                        .Value;
                Assert.Contains(
                    "Browse",
                    command,
                    StringComparison.Ordinal);
            });
    }

    [Fact]
    public void Reviewed_neutral_copy_uses_en_us_spelling_and_clear_workflow_labels()
    {
        XDocument neutral =
            XDocument.Load(
                Path.Combine(
                    FindRepositoryRoot(),
                    "MusicLibraryManager.Presentation",
                    "Resources",
                    "Strings.resx"));
        Dictionary<string, string> values =
            neutral.Root!
                .Elements("data")
                .ToDictionary(
                    entry =>
                        (string)entry.Attribute("name")!,
                    entry =>
                        entry.Element("value")!.Value,
                    StringComparer.Ordinal);

        Assert.Equal(
            "Canceling…",
            values["Activity.Cancelling"]);
        Assert.Equal(
            "Indexing canceled; safely committed partial progress was retained.",
            values["Index.Status.Cancelled"]);
        Assert.Equal(
            "Organization was canceled. Completed moves remain reflected in the library.",
            values["Organize.Status.ApplyCancelled"]);
        Assert.Equal(
            "Organization preview canceled.",
            values["Organize.Status.PreviewCancelled"]);
        Assert.Equal(
            "Add standard field",
            values["FieldsEditor.AddField"]);
        Assert.Equal(
            "Add custom text field",
            values["FieldsEditor.AddUserString"]);
        Assert.Equal(
            "Delete permanently",
            values["Dialog.Purge.Action"]);
        Assert.Equal(
            "Generate missing CD-quality tracks",
            values["Dialog.CdDerivation.Title"]);
        Assert.Equal(
            "Metadata match",
            values[
                "OnlineMetadata.Mapping.Confidence.Metadata"]);
        Assert.Equal(
            "Recording ID match",
            values[
                "OnlineMetadata.Mapping.Confidence.RecordingId"]);
    }

    [Fact]
    public void Shipping_form_inputs_use_shared_styles_and_durable_accessible_labels()
    {
        string applicationRoot =
            Path.Combine(
                FindRepositoryRoot(),
                "MusicLibraryManager");
        HashSet<string> inputTypes =
        [
            "TextBox",
            "ComboBox",
            "NumericUpDown",
            "Slider",
        ];
        var errors = new List<string>();

        foreach (string path in
                 Directory.EnumerateFiles(
                     applicationRoot,
                     "*.axaml",
                     SearchOption.AllDirectories))
        {
            XDocument document =
                XDocument.Load(
                    path,
                    LoadOptions.SetLineInfo);
            string relativePath =
                Path.GetRelativePath(
                        applicationRoot,
                        path)
                    .Replace(
                        Path.DirectorySeparatorChar,
                        '/');
            foreach (XElement input in
                     document.Descendants()
                         .Where(element =>
                             inputTypes.Contains(
                                 element.Name.LocalName)))
            {
                string classes =
                    (string?)input.Attribute(
                        "Classes") ??
                    "";
                if (!HasLiteralClass(
                        classes,
                        "app") &&
                    !HasLiteralClass(
                        classes,
                        "top-search-input"))
                {
                    errors.Add(
                        $"{relativePath}: {Describe(input)} {input.Name.LocalName} does not use the shared 'app' input style.");
                }

                string accessibleName =
                    (string?)input.Attribute(
                        "AutomationProperties.Name") ??
                    "";
                if (string.IsNullOrWhiteSpace(
                        accessibleName))
                {
                    errors.Add(
                        $"{relativePath}: {Describe(input)} {input.Name.LocalName} has no durable accessible name.");
                }

                string? controlName =
                    input.Attributes()
                        .FirstOrDefault(attribute =>
                            attribute.Name.LocalName ==
                            "Name")
                        ?.Value;
                if (!HasVisibleDurableLabel(
                        input) &&
                    (controlName is null ||
                     !VisibleLabelExemptControlNames
                         .Contains(controlName)))
                {
                    errors.Add(
                        $"{relativePath}: {Describe(input)} {controlName ?? input.Name.LocalName} has no visible durable label. Put the input in a shared 'field' container with a field-label TextBlock; placeholders and AutomationProperties.Name are not visible labels.");
                }

                if ((controlName is null ||
                     !VisibleLabelExemptControlNames
                         .Contains(controlName)) &&
                    HasExplicitLabeledByAttribute(
                        input) &&
                    !HasExplicitLabeledBy(input))
                {
                    errors.Add(
                        $"{relativePath}: {Describe(input)} {controlName ?? input.Name.LocalName} has an invalid AutomationProperties.LabeledBy reference. Use a resolvable element binding to a named, visible field-label TextBlock.");
                }
                else if ((controlName is null ||
                          !VisibleLabelExemptControlNames
                              .Contains(controlName)) &&
                         !HasProgrammaticLabelAssociation(
                             input))
                {
                    errors.Add(
                        $"{relativePath}: {Describe(input)} {controlName ?? input.Name.LocalName} has no programmatic label association. Put it in a StackPanel.field, enable FieldAccessibility.Associate on its logical field container, or set AutomationProperties.LabeledBy explicitly.");
                }

                XElement? associatedField =
                    FindAssociatedFieldContainer(
                        input);
                if ((controlName is null ||
                     !VisibleLabelExemptControlNames
                         .Contains(controlName)) &&
                    associatedField is not null &&
                    !HasExplicitLabeledBy(input))
                {
                    int ownedInputCount =
                        associatedField
                            .Descendants()
                            .Count(candidate =>
                                IsFormInput(candidate) &&
                                ReferenceEquals(
                                    FindAssociatedFieldContainer(
                                        candidate),
                                    associatedField));
                    if (ownedInputCount != 1)
                    {
                        errors.Add(
                            $"{relativePath}: {Describe(input)} {controlName ?? input.Name.LocalName} shares one automatic field association with {ownedInputCount} form inputs. Give each input its own associated field or set AutomationProperties.LabeledBy explicitly.");
                    }
                }
            }
        }

        Assert.True(
            errors.Count == 0,
            string.Join(
                Environment.NewLine,
                errors));
    }

    [Fact]
    public void Form_label_gate_rejects_a_statically_hidden_owned_label()
    {
        XElement input =
            ParseFormLabelFixture(
                """
                <StackPanel Classes="field">
                  <TextBlock Classes="field-label"
                             Text="Title"
                             IsVisible="False" />
                  <TextBox />
                </StackPanel>
                """);

        Assert.False(
            HasVisibleDurableLabel(input));
        Assert.False(
            HasProgrammaticLabelAssociation(
                input));
    }

    [Fact]
    public void Form_label_gate_rejects_an_unrelated_preceding_label()
    {
        XElement input =
            ParseFormLabelFixture(
                """
                <StackPanel>
                  <TextBlock Classes="field-label"
                             Text="Title" />
                  <TextBox />
                </StackPanel>
                """);

        Assert.False(
            HasVisibleDurableLabel(input));
        Assert.False(
            HasProgrammaticLabelAssociation(
                input));
    }

    [Theory]
    [InlineData("TitleLabel")]
    [InlineData("{Binding}")]
    [InlineData("{Binding #MissingLabel}")]
    public void Form_label_gate_rejects_malformed_or_missing_explicit_targets(
        string labeledBy)
    {
        XElement input =
            ParseExplicitFormLabelFixture(
                labeledBy);

        Assert.False(
            HasVisibleDurableLabel(input));
        Assert.False(
            HasExplicitLabeledBy(input));
        Assert.False(
            HasProgrammaticLabelAssociation(
                input));
    }

    [Theory]
    [InlineData("{Binding #TitleLabel}")]
    [InlineData("{Binding ElementName=TitleLabel}")]
    [InlineData("{x:Reference TitleLabel}")]
    public void Form_label_gate_accepts_a_resolvable_visible_explicit_association(
        string labeledBy)
    {
        XElement input =
            ParseExplicitFormLabelFixture(
                labeledBy);

        Assert.True(
            HasVisibleDurableLabel(input));
        Assert.True(
            HasExplicitLabeledBy(input));
        Assert.True(
            HasProgrammaticLabelAssociation(
                input));
    }

    [Fact]
    public void Shipping_xaml_contains_no_unapproved_hard_coded_ui_copy()
    {
        string applicationRoot =
            Path.Combine(
                FindRepositoryRoot(),
                "MusicLibraryManager");
        HashSet<string> visibleAttributes =
        [
            "Content",
            "Header",
            "HelpText",
            "PlaceholderText",
            "Text",
            "Tip",
            "Title",
            "Watermark",
        ];
        var errors = new List<string>();

        foreach (string path in
                 Directory.EnumerateFiles(
                     applicationRoot,
                     "*.axaml",
                     SearchOption.AllDirectories))
        {
            XDocument document =
                XDocument.Load(
                    path,
                    LoadOptions.SetLineInfo);
            string relativePath =
                Path.GetRelativePath(
                        applicationRoot,
                        path)
                    .Replace(
                        Path.DirectorySeparatorChar,
                        '/');

            foreach (XAttribute attribute in
                     document.Descendants()
                         .Attributes()
                         .Where(attribute =>
                             visibleAttributes.Contains(
                                 attribute.Name.LocalName)))
            {
                string value =
                    attribute.Value.Trim();
                if (!IsUnapprovedHardCodedUiCopy(
                        value))
                    continue;

                errors.Add(
                    $"{relativePath}: {Describe(attribute)} {attribute.Name.LocalName} contains unlocalized UI copy '{value}'.");
            }

            foreach (XText text in
                     document.DescendantNodes()
                         .OfType<XText>())
            {
                string value = text.Value.Trim();
                if (text.Parent is null ||
                    !IsUiTextElement(
                        text.Parent.Name.LocalName) ||
                    !IsUnapprovedHardCodedUiCopy(
                        value))
                    continue;

                errors.Add(
                    $"{relativePath}: {Describe(text)} {text.Parent.Name.LocalName} contains unlocalized UI copy '{value}'.");
            }
        }

        Assert.True(
            errors.Count == 0,
            string.Join(
                Environment.NewLine,
                errors));
    }

    [Fact]
    public void Shipping_icon_buttons_use_shared_vector_content_instead_of_font_glyphs()
    {
        string applicationRoot =
            Path.Combine(
                FindRepositoryRoot(),
                "MusicLibraryManager");
        var errors = new List<string>();

        foreach (string path in
                 Directory.EnumerateFiles(
                     applicationRoot,
                     "*.axaml",
                     SearchOption.AllDirectories))
        {
            XDocument document =
                XDocument.Load(
                    path,
                    LoadOptions.SetLineInfo);
            string relativePath =
                Path.GetRelativePath(
                        applicationRoot,
                        path)
                    .Replace(
                        Path.DirectorySeparatorChar,
                        '/');
            foreach (XElement button in
                     document.Descendants()
                         .Where(element =>
                             element.Name.LocalName ==
                             "Button"))
            {
                string content =
                    ((string?)button.Attribute(
                        "Content") ?? "")
                    .Trim();
                if (content.Length == 0 ||
                    content.StartsWith('{') ||
                    content.Any(char.IsLetterOrDigit))
                    continue;
                errors.Add(
                    $"{relativePath}: {Describe(button)} uses font glyph Content=\"{content}\". Use AppVectorIcon in the shared app icon-button style.");
            }
        }

        Assert.True(
            errors.Count == 0,
            string.Join(
                Environment.NewLine,
                errors));
    }

    [Fact]
    public void Shipping_toggles_expose_visible_content_or_a_durable_accessible_name()
    {
        string applicationRoot =
            Path.Combine(
                FindRepositoryRoot(),
                "MusicLibraryManager");
        HashSet<string> toggleTypes =
        [
            "CheckBox",
            "RadioButton",
            "ToggleSwitch",
        ];
        var errors = new List<string>();

        foreach (string path in
                 Directory.EnumerateFiles(
                     applicationRoot,
                     "*.axaml",
                     SearchOption.AllDirectories))
        {
            XDocument document =
                XDocument.Load(
                    path,
                    LoadOptions.SetLineInfo);
            string relativePath =
                Path.GetRelativePath(
                        applicationRoot,
                        path)
                    .Replace(
                        Path.DirectorySeparatorChar,
                        '/');

            foreach (XElement toggle in
                     document.Descendants()
                         .Where(element =>
                             toggleTypes.Contains(
                                 element.Name.LocalName)))
            {
                string content =
                    (string?)toggle.Attribute(
                        "Content") ??
                    "";
                string accessibleName =
                    (string?)toggle.Attribute(
                        "AutomationProperties.Name") ??
                    "";
                if (string.IsNullOrWhiteSpace(
                        content) &&
                    string.IsNullOrWhiteSpace(
                        accessibleName))
                {
                    errors.Add(
                        $"{relativePath}: {Describe(toggle)} {toggle.Name.LocalName} has neither visible content nor a durable accessible name.");
                }
            }
        }

        Assert.True(
            errors.Count == 0,
            string.Join(
                Environment.NewLine,
            errors));
    }

    [Fact]
    public void Shipping_tooltips_add_help_instead_of_repeating_the_visible_label()
    {
        string applicationRoot =
            Path.Combine(
                FindRepositoryRoot(),
                "MusicLibraryManager");
        Dictionary<string, string> resources =
            LoadNeutralResources();
        var errors = new List<string>();
        int tooltipCount = 0;

        foreach (string path in
                 Directory.EnumerateFiles(
                     applicationRoot,
                     "*.axaml",
                     SearchOption.AllDirectories))
        {
            XDocument document =
                XDocument.Load(
                    path,
                    LoadOptions.SetLineInfo);
            string relativePath =
                Path.GetRelativePath(
                        applicationRoot,
                        path)
                    .Replace(
                        Path.DirectorySeparatorChar,
                        '/');
            foreach (XElement owner in
                     document.Descendants()
                         .Where(element =>
                             element.Attributes()
                                 .Any(attribute =>
                                     attribute.Name.LocalName is
                                         "ToolTip.Tip" or
                                         "Tip")))
            {
                tooltipCount++;
                XAttribute tooltip =
                    owner.Attributes()
                        .Single(attribute =>
                            attribute.Name.LocalName is
                                "ToolTip.Tip" or
                                "Tip");
                string tooltipIdentity =
                    ResolveUiIdentity(
                        tooltip.Value,
                        resources);
                if (tooltipIdentity.Length == 0 ||
                    TooltipValueRevealBindings
                        .Contains(tooltip.Value))
                {
                    continue;
                }

                string? controlName =
                    owner.Attributes()
                        .FirstOrDefault(attribute =>
                            attribute.Name.LocalName ==
                            "Name")
                        ?.Value;
                if (controlName is not null &&
                    TooltipRepeatExemptControlNames
                        .Contains(controlName))
                {
                    continue;
                }

                string[] visibleIdentities =
                [
                    .. owner.Attributes()
                        .Where(attribute =>
                            attribute.Name.LocalName is
                                "Content" or
                                "Header" or
                                "Text")
                        .Select(attribute =>
                            ResolveUiIdentity(
                                attribute.Value,
                                resources)),
                    .. owner.Descendants()
                        .Where(element =>
                            element.Name.LocalName ==
                                "TextBlock" &&
                            IsVisibleTooltipLabel(
                                owner,
                                element))
                        .Select(element =>
                            ResolveUiIdentity(
                                (string?)element
                                    .Attribute("Text") ??
                                "",
                                resources)),
                ];
                if (!visibleIdentities.Any(
                        identity =>
                            identity.Length > 0 &&
                            string.Equals(
                                identity,
                                tooltipIdentity,
                                StringComparison
                                    .OrdinalIgnoreCase)))
                {
                    continue;
                }

                errors.Add(
                    $"{relativePath}: {Describe(tooltip)} {controlName ?? owner.Name.LocalName} tooltip repeats its visible label '{tooltipIdentity}'. Use the tooltip for a consequence or prerequisite, or add a narrowly documented icon/value-reveal exemption.");
            }
        }

        Assert.True(
            tooltipCount > 0,
            "No shipping ToolTip.Tip attributes were inspected.");
        Assert.True(
            errors.Count == 0,
            string.Join(
                Environment.NewLine,
                errors));
    }

    [Fact]
    public void Every_literal_visual_class_resolves_or_is_an_explicit_structural_hook()
    {
        string applicationRoot =
            Path.Combine(
                FindRepositoryRoot(),
                "MusicLibraryManager");
        string[] paths =
        [
            .. Directory.EnumerateFiles(
                applicationRoot,
                "*.axaml",
                SearchOption.AllDirectories),
        ];
        var defined = new HashSet<string>(
            StringComparer.Ordinal);
        var uses = new List<(
            string Path,
            string ClassName)>();

        foreach (string path in paths)
        {
            XDocument document =
                XDocument.Load(path);
            foreach (string selector in
                     document.Descendants()
                         .Attributes("Selector")
                         .Select(attribute =>
                             attribute.Value))
            {
                foreach (Match match in
                         StyleClassPattern()
                             .Matches(selector))
                {
                    defined.Add(
                        match.Groups["name"].Value);
                }
            }

            foreach (XAttribute classes in
                     document.Descendants()
                         .Attributes("Classes"))
            {
                if (classes.Value.StartsWith(
                        '{'))
                    continue;
                foreach (string className in
                         classes.Value.Split(
                             ' ',
                             StringSplitOptions
                                 .RemoveEmptyEntries))
                {
                    uses.Add((
                        Path.GetRelativePath(
                                applicationRoot,
                                path)
                            .Replace(
                                Path.DirectorySeparatorChar,
                                '/'),
                        className));
                }
            }
        }

        string[] unresolved =
        [
            .. uses
                .Where(use =>
                    !defined.Contains(
                        use.ClassName) &&
                    !StructuralClassesWithoutSelectors
                        .Contains(
                            use.ClassName))
                .Select(use =>
                    $"{use.Path}: Classes contains unresolved '{use.ClassName}'")
                .Distinct(
                    StringComparer.Ordinal),
        ];

        Assert.True(
            unresolved.Length == 0,
            string.Join(
                Environment.NewLine,
                unresolved));
    }

    [Fact]
    public void Shared_semantic_layout_classes_are_used_by_shipping_views()
    {
        string applicationRoot =
            Path.Combine(
                FindRepositoryRoot(),
                "MusicLibraryManager");
        string[] requiredClasses =
        [
            "section-frame",
            "empty-state",
            "drawer-surface",
            "field",
            "content-stack",
            "toolbar",
            "sticky-footer",
            "dense-card",
        ];
        var uses = new Dictionary<string, int>(
            StringComparer.Ordinal);

        foreach (string path in
                 Directory.EnumerateFiles(
                     applicationRoot,
                     "*.axaml",
                     SearchOption.AllDirectories))
        {
            XDocument document =
                XDocument.Load(path);
            foreach (XAttribute attribute in
                     document.Descendants()
                         .Attributes("Classes"))
            {
                if (attribute.Value.StartsWith(
                        '{'))
                    continue;
                foreach (string className in
                         attribute.Value.Split(
                             ' ',
                             StringSplitOptions
                                 .RemoveEmptyEntries))
                {
                    uses[className] =
                        uses.GetValueOrDefault(
                            className) + 1;
                }
            }
        }

        Assert.All(
            requiredClasses,
            className =>
                Assert.True(
                    uses.GetValueOrDefault(
                        className) > 0,
                    $"No shipping view uses the shared '{className}' semantic class."));

        XDocument workbench =
            XDocument.Load(
                Path.Combine(
                    applicationRoot,
                    "Views",
                    "WorkbenchView.axaml"));
        XNamespace x =
            "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement sectionFrame =
            Assert.Single(
                workbench.Descendants(),
                element =>
                    (string?)element.Attribute(
                        x + "Name") ==
                    "WorkbenchSectionContentCard");
        XElement drawerHost =
            Assert.Single(
                workbench.Descendants(),
                element =>
                    (string?)element.Attribute(
                        x + "Name") ==
                    "WorkbenchDrawerPane");
        Assert.Contains(
            "section-frame",
            ((string?)sectionFrame.Attribute(
                "Classes") ?? "")
                .Split(' '));
        Assert.Contains(
            "drawer-surface",
            ((string?)drawerHost.Attribute(
                "Classes") ?? "")
                .Split(' '));
    }

    [Fact]
    public void
        Destructive_workflows_keep_scope_or_count_visible_before_the_action_is_enabled()
    {
        string repositoryRoot =
            FindRepositoryRoot();
        string viewRoot =
            Path.Combine(
                repositoryRoot,
                "MusicLibraryManager",
                "Views");
        XNamespace x =
            "http://schemas.microsoft.com/winfx/2006/xaml";

        XDocument health =
            XDocument.Load(
                Path.Combine(
                    viewRoot,
                    "HealthView.axaml"));
        (
            string ActionResource,
            string CountResource)[] healthRepairs =
        [
            (
                "Loc.Health.Action.ApplyMetadataRepairs",
                "Health.Repairs.MetadataCount"),
            (
                "Loc.Health.Action.ApplyFileRepairs",
                "Health.Repairs.FileCount"),
            (
                "Loc.Health.Action.ApplyItunesRepairs",
                "Health.Repairs.ItunesCount"),
            (
                "Loc.Health.Action.ApplyArtworkRepairs",
                "Health.Artwork.SelectedCount"),
        ];
        foreach ((
                     string actionResource,
                     string countResource)
                 in healthRepairs)
        {
            XElement action =
                Assert.Single(
                    health.Descendants(),
                    element =>
                        element.Name.LocalName ==
                        "Button" &&
                        ((string?)element.Attribute(
                             "Content") ?? "")
                        .Contains(
                            actionResource,
                            StringComparison.Ordinal));
            XElement banner =
                Assert.Single(
                    action.Ancestors(),
                    element =>
                        element.Name.LocalName ==
                        "Border" &&
                        HasClass(
                            element,
                            "status-banner"));
            Assert.Contains(
                banner.Descendants(),
                element =>
                    element.Name.LocalName ==
                    "LocalizedFormatTextBlock" &&
                    (string?)element.Attribute(
                        "ResourceKey") ==
                    countResource);
        }

        XDocument devices =
            XDocument.Load(
                Path.Combine(
                    viewRoot,
                    "DevicesView.axaml"));
        Assert.Single(
            devices.Descendants(),
            element =>
                (string?)element.Attribute(
                    x + "Name") ==
                "ApplyButton");
        XElement plannedCount =
            Assert.Single(
                devices.Descendants(),
                element =>
                    element.Name.LocalName ==
                    "LocalizedFormatTextBlock" &&
                    (string?)element.Attribute(
                        "ResourceKey") ==
                    "Devices.PlannedCount");
        Assert.Contains(
            plannedCount.Ancestors(),
            element =>
                element.Name.LocalName ==
                "Border" &&
                HasClass(
                    element,
                    "status-banner"));

        XDocument ingest =
            XDocument.Load(
                Path.Combine(
                    viewRoot,
                    "IngestView.axaml"));
        Assert.Single(
            ingest.Descendants(),
            element =>
                element.Name.LocalName ==
                "Button" &&
                ((string?)element.Attribute(
                     "Command") ?? "")
                .Contains(
                    "ApplyCommand",
                    StringComparison.Ordinal));
        string[] ingestScopeCounts =
        [
            "Ingest.Summary.OutputCount",
            "Ingest.Summary.ConflictCount",
            "Ingest.Summary.CleanupCount",
        ];
        Assert.All(
            ingestScopeCounts,
            resourceKey =>
                Assert.Contains(
                    ingest.Descendants(),
                    element =>
                        element.Name.LocalName ==
                        "LocalizedFormatTextBlock" &&
                        (string?)element.Attribute(
                            "ResourceKey") ==
                        resourceKey));
        string ingestViewModel =
            File.ReadAllText(
                Path.Combine(
                    repositoryRoot,
                    "MusicLibraryManager.Presentation",
                    "Workflows",
                    "IngestViewModel.cs"));
        Assert.Contains(
            "private bool CanApply() => !IsBusy && HasApplicablePreview && _plan is not null;",
            ingestViewModel,
            StringComparison.Ordinal);

        XDocument organize =
            XDocument.Load(
                Path.Combine(
                    viewRoot,
                    "OrganizeView.axaml"));
        Assert.Single(
            organize.Descendants(),
            element =>
                element.Name.LocalName ==
                "Button" &&
                ((string?)element.Attribute(
                     "Command") ?? "")
                .Contains(
                    "ApplyCommand",
                    StringComparison.Ordinal));
        Assert.Single(
            organize.Descendants(),
            element =>
                element.Name.LocalName ==
                "LocalizedFormatTextBlock" &&
                (string?)element.Attribute(
                    "ResourceKey") ==
                "Organize.PlannedCount");

        XDocument operations =
            XDocument.Load(
                Path.Combine(
                    viewRoot,
                    "OperationsView.axaml"));
        Assert.Single(
            operations.Descendants(),
            element =>
                element.Name.LocalName ==
                "Button" &&
                ((string?)element.Attribute(
                     "Command") ?? "")
                .Contains(
                    "ApplyRestoreCommand",
                    StringComparison.Ordinal));
        Assert.Single(
            operations.Descendants(),
            element =>
                element.Name.LocalName ==
                "Border" &&
                (string?)element.Attribute(
                    "IsVisible") ==
                "{Binding ShowRestorePreview}" &&
                element.Descendants()
                    .Any(descendant =>
                        (string?)descendant
                            .Attribute(
                                "Text") ==
                        "{Binding RestorePreviewText}"));
        XElement purgeAction =
            Assert.Single(
                operations.Descendants(),
                element =>
                    element.Name.LocalName ==
                    "MenuItem" &&
                    ((string?)element.Attribute(
                         "Command") ?? "")
                    .Contains(
                        "ApplyPurgeCommand",
                        StringComparison.Ordinal));
        Assert.True(
            HasClass(
                purgeAction,
                "danger"));

        string operationsViewModel =
            File.ReadAllText(
                Path.Combine(
                    repositoryRoot,
                    "MusicLibraryManager.Presentation",
                    "Workflows",
                    "OperationsViewModel.cs"));
        Assert.Contains(
            "private bool CanApplyRestore() => !IsBusy && _restorePlan?.CanApply == true;",
            operationsViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShowRestorePreview = _restorePlan.CanApply;",
            operationsViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Operations.RestorePreview.Ready\"",
            operationsViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "_restorePlan.Actions.Count",
            operationsViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "private bool CanApplyPurge() => !IsBusy && _purgePlan?.CanApply == true;",
            operationsViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "StatusText = PurgePreviewText!;",
            operationsViewModel,
            StringComparison.Ordinal);

        XDocument settings =
            XDocument.Load(
                Path.Combine(
                    viewRoot,
                    "SettingsView.axaml"));
        AssertRowActionHasIdentity(
            settings,
            "RemoveIndexTargetCommand",
            element =>
                element.Name.LocalName ==
                "TextBox" &&
                ((string?)element.Attribute(
                     "Text") ?? "")
                .Contains(
                    "{Binding Path",
                    StringComparison.Ordinal));
        AssertRowActionHasIdentity(
            settings,
            "DeleteLibraryProfileCommand",
            element =>
                (string?)element.Attribute(
                    x + "Name") ==
                "ProfilePresetPicker");
        AssertRowActionHasIdentity(
            settings,
            "DeleteIngestProfileCommand",
            element =>
                (string?)element.Attribute(
                    x + "Name") ==
                "IngestProfilePicker");

        static void AssertRowActionHasIdentity(
            XDocument document,
            string commandName,
            Func<XElement, bool>
                identifiesScope)
        {
            XElement action =
                Assert.Single(
                    document.Descendants(),
                    element =>
                        ((string?)element.Attribute(
                             "Command") ?? "")
                        .Contains(
                            commandName,
                            StringComparison.Ordinal));
            XElement? actionRegion =
                action.Ancestors()
                    .FirstOrDefault(
                        element =>
                            element.Name.LocalName ==
                            "Grid" &&
                            element.Descendants()
                                .Any(
                                    identifiesScope));
            Assert.NotNull(
                actionRegion);
            Assert.Contains(
                actionRegion.Descendants(),
                element =>
                    identifiesScope(
                        element));
        }

        static bool HasClass(
            XElement element,
            string className) =>
            ((string?)element.Attribute(
                 "Classes") ?? "")
            .Split(
                ' ',
                StringSplitOptions
                    .RemoveEmptyEntries)
            .Contains(
                className,
                StringComparer.Ordinal);
    }

    [Fact]
    public void Shipping_layout_spacing_uses_the_approved_scale()
    {
        string applicationRoot =
            Path.Combine(
                FindRepositoryRoot(),
                "MusicLibraryManager");
        HashSet<string> spacingAttributes =
        [
            "Margin",
            "Padding",
            "Spacing",
            "RowSpacing",
            "ColumnSpacing",
        ];
        var errors = new List<string>();

        foreach (string path in
                 Directory.EnumerateFiles(
                     applicationRoot,
                     "*.axaml",
                     SearchOption.AllDirectories))
        {
            XDocument document =
                XDocument.Load(
                    path,
                    LoadOptions.SetLineInfo);
            string relativePath =
                Path.GetRelativePath(
                        applicationRoot,
                        path)
                    .Replace(
                        Path.DirectorySeparatorChar,
                        '/');
            foreach (XAttribute attribute in
                     document.Descendants()
                         .Attributes()
                         .Where(attribute =>
                             IsSpacingAttribute(
                                 attribute,
                                 spacingAttributes)))
            {
                string value =
                    attribute.Value.Trim();
                if (value.StartsWith(
                        "{DynamicResource ",
                        StringComparison.Ordinal))
                    continue;
                foreach (string component in
                         value.Split(
                             ',',
                             StringSplitOptions
                                 .TrimEntries |
                             StringSplitOptions
                                 .RemoveEmptyEntries))
                {
                    if (!double.TryParse(
                            component,
                            System.Globalization
                                .NumberStyles.Float,
                            System.Globalization
                                .CultureInfo.InvariantCulture,
                            out double numeric))
                        continue;
                    string normalized =
                        Math.Abs(numeric)
                            .ToString(
                                "0.################",
                                System.Globalization
                                    .CultureInfo.InvariantCulture);
                    if (ApprovedSpacingValues
                        .Contains(normalized))
                        continue;

                    errors.Add(
                        $"{relativePath}: {Describe(attribute)} {GetSpacingPropertyName(attribute)}='{value}' contains '{component}', outside the 0/4/8/12/16/24 spacing scale.");
                }
            }
        }

        Assert.True(
            errors.Count == 0,
            string.Join(
                Environment.NewLine,
                errors));
    }

    [Fact]
    public void Code_created_thickness_values_use_the_approved_spacing_scale()
    {
        string applicationRoot =
            Path.Combine(
                FindRepositoryRoot(),
                "MusicLibraryManager");
        var errors = new List<string>();

        foreach (string path in
                 Directory.EnumerateFiles(
                     applicationRoot,
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(path);
            string relativePath =
                Path.GetRelativePath(
                        applicationRoot,
                        path)
                    .Replace(
                        Path.DirectorySeparatorChar,
                        '/');
            foreach (Match match in
                     ThicknessConstructorPattern()
                         .Matches(source))
            {
                string arguments =
                    match.Groups["arguments"]
                        .Value;
                foreach (Match number in
                         NumericLiteralPattern()
                             .Matches(arguments))
                {
                    double numeric =
                        double.Parse(
                            number.Value,
                            System.Globalization
                                .CultureInfo.InvariantCulture);
                    string normalized =
                        Math.Abs(numeric)
                            .ToString(
                                "0.################",
                                System.Globalization
                                    .CultureInfo.InvariantCulture);
                    if (ApprovedSpacingValues
                        .Contains(normalized))
                        continue;

                    int line =
                        source.AsSpan(
                                0,
                                match.Index +
                                number.Index)
                            .Count('\n') + 1;
                    errors.Add(
                        $"{relativePath}: line {line} Thickness argument '{number.Value}' is outside the 0/4/8/12/16/24 spacing scale.");
                }
            }
        }

        Assert.True(
            errors.Count == 0,
            string.Join(
                Environment.NewLine,
                errors));
    }

    [Fact]
    public void Shipping_surface_radii_use_the_approved_scale()
    {
        string applicationRoot =
            Path.Combine(
                FindRepositoryRoot(),
                "MusicLibraryManager");
        HashSet<string> approvedValues =
        [
            "6",
            "8",
            "12",
            // Used only for controls that must remain fully rounded.
            "99",
        ];
        var errors = new List<string>();

        foreach (string path in
                 Directory.EnumerateFiles(
                     applicationRoot,
                     "*.axaml",
                     SearchOption.AllDirectories))
        {
            XDocument document =
                XDocument.Load(
                    path,
                    LoadOptions.SetLineInfo);
            string relativePath =
                Path.GetRelativePath(
                        applicationRoot,
                        path)
                    .Replace(
                        Path.DirectorySeparatorChar,
                        '/');
            foreach (XAttribute attribute in
                     document.Descendants()
                         .Attributes()
                         .Where(attribute =>
                             attribute.Name.LocalName ==
                             "CornerRadius"))
            {
                string value =
                    attribute.Value.Trim();
                if (approvedValues.Contains(
                        value) ||
                    value.StartsWith(
                        "{DynamicResource ",
                        StringComparison.Ordinal))
                    continue;

                errors.Add(
                    $"{relativePath}: {Describe(attribute)} CornerRadius='{value}' is outside the 6/8/12 surface-radius scale.");
            }
        }

        Assert.True(
            errors.Count == 0,
            string.Join(
                Environment.NewLine,
                errors));
    }

    [Fact]
    public void More_menu_labels_do_not_imply_an_extra_dialog()
    {
        Dictionary<string, string> resources =
            LoadNeutralResources();
        string[] keys =
        [
            "Workbench.Action.More",
            "Library.Action.More",
            "Ingest.Action.More",
            "Devices.Action.More",
        ];

        Assert.All(
            keys,
            key =>
            {
                string value = resources[key];
                Assert.DoesNotContain(
                    "...",
                    value,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    "\u2026",
                    value,
                    StringComparison.Ordinal);
            });
    }

    [Fact]
    public void Neutral_catalog_uses_the_single_ellipsis_character()
    {
        string[] violations = LoadNeutralResources()
            .Where(item =>
                item.Value.Contains(
                    "...",
                    StringComparison.Ordinal))
            .Select(item =>
                $"{item.Key}={item.Value}")
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Shared_technical_choice_resources_use_canonical_spelling()
    {
        Dictionary<string, string> resources =
            LoadNeutralResources();
        var expected =
            new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["Technical.Format.Csv"] =
                    "CSV",
                ["Technical.Format.Html"] =
                    "HTML",
                ["Technical.Format.Rtf"] =
                    "RTF",
                ["Technical.PlaylistFormat.M3u"] =
                    "M3U",
                ["Technical.PlaylistFormat.M3u8"] =
                    "M3U8",
                ["Technical.PlaylistFormat.M3uFamily"] =
                    "M3U/M3U8",
                ["Technical.PlaylistFormat.Wpl"] =
                    "Windows Media Player (WPL)",
                ["Technical.Encoding.Ascii"] =
                    "ASCII",
                ["Technical.Encoding.Utf8"] =
                    "UTF-8",
                ["Technical.Encoding.Utf8WithBom"] =
                    "UTF-8 with BOM",
                ["Technical.Encoding.Utf16Le"] =
                    "UTF-16 LE",
                ["Technical.Encoding.Utf16Be"] =
                    "UTF-16 BE",
                ["Technical.LineEnding.CrLf"] =
                    "Windows (CRLF)",
                ["Technical.LineEnding.Lf"] =
                    "Unix (LF)",
                ["Technical.Encoding.Latin1"] =
                    "Latin-1",
                ["Technical.Encoding.Utf16"] =
                    "UTF-16",
                ["Technical.Id3Version.V22"] =
                    "ID3v2.2",
                ["Technical.Id3Version.V23"] =
                    "ID3v2.3",
                ["Technical.Id3Version.V24"] =
                    "ID3v2.4",
                ["Technical.MusicBrainzIds"] =
                    "MusicBrainz IDs",
            };

        Assert.All(
            expected,
            item =>
                Assert.Equal(
                    item.Value,
                    resources[item.Key]));
    }

    private static Dictionary<string, string>
        LoadNeutralResources() =>
        XDocument.Load(
                Path.Combine(
                    FindRepositoryRoot(),
                    "MusicLibraryManager.Presentation",
                    "Resources",
                    "Strings.resx"))
            .Root!
            .Elements("data")
            .ToDictionary(
                element =>
                    (string)element.Attribute(
                        "name")!,
                element =>
                    element.Element("value")!
                        .Value,
                StringComparer.Ordinal);

    private static bool HasVisibleDurableLabel(
        XElement input)
    {
        XElement? field =
            FindAssociatedFieldContainer(
                input);
        if (field is not null)
        {
            return HasOwnedVisibleFieldLabel(
                field);
        }

        return HasExplicitLabeledBy(input);
    }

    private static bool HasProgrammaticLabelAssociation(
        XElement input)
    {
        if (HasExplicitLabeledByAttribute(
                input))
        {
            return HasExplicitLabeledBy(
                input);
        }

        XElement? field =
            FindAssociatedFieldContainer(
                input);
        return field is not null &&
            HasOwnedVisibleFieldLabel(field);
    }

    private static bool HasExplicitLabeledBy(
        XElement input) =>
        TryResolveExplicitLabeledByTarget(
            input,
            out _);

    private static bool
        HasExplicitLabeledByAttribute(
            XElement input) =>
        input.Attributes()
            .Any(attribute =>
                attribute.Name.LocalName ==
                "AutomationProperties.LabeledBy");

    private static bool HasOwnedVisibleFieldLabel(
        XElement field) =>
        field.Descendants()
            .Any(element =>
                IsVisibleLabelElement(element) &&
                ReferenceEquals(
                    FindAssociatedFieldContainer(
                        element),
                    field));

    private static bool
        TryResolveExplicitLabeledByTarget(
            XElement input,
            out XElement? target)
    {
        target = null;
        XAttribute? labeledBy =
            input.Attributes()
                .FirstOrDefault(attribute =>
                    attribute.Name.LocalName ==
                    "AutomationProperties.LabeledBy");
        if (labeledBy is null ||
            !TryParseElementReference(
                labeledBy.Value,
                out string targetName))
        {
            return false;
        }

        XElement? nameScope =
            FindNameScopeRoot(input);
        if (nameScope is null)
            return false;

        XElement[] matches =
        [
            .. nameScope
                .DescendantsAndSelf()
                .Where(candidate =>
                    ReferenceEquals(
                        FindNameScopeRoot(candidate),
                        nameScope) &&
                    candidate.Attributes()
                        .Any(attribute =>
                            attribute.Name.LocalName ==
                                "Name" &&
                            string.Equals(
                                attribute.Value,
                                targetName,
                                StringComparison.Ordinal)))
                .Take(2),
        ];
        if (matches.Length != 1 ||
            !IsVisibleLabelElement(
                matches[0]))
        {
            return false;
        }

        target = matches[0];
        return true;
    }

    private static bool TryParseElementReference(
        string value,
        out string targetName)
    {
        Match match =
            LabeledByElementReferencePattern()
                .Match(value.Trim());
        if (!match.Success)
        {
            targetName = "";
            return false;
        }

        targetName =
            match.Groups["name"].Value;
        return targetName.Length > 0;
    }

    private static XElement? FindNameScopeRoot(
        XElement element) =>
        element.AncestorsAndSelf()
            .FirstOrDefault(candidate =>
                candidate.Name.LocalName is
                    "ControlTemplate" or
                    "DataTemplate" or
                    "ItemsPanelTemplate" or
                    "TreeDataTemplate") ??
        element.Document?.Root;

    private static XElement?
        FindAssociatedFieldContainer(
            XElement input) =>
        input.Ancestors()
            .FirstOrDefault(
                IsAssociatedFieldContainer);

    private static bool IsAssociatedFieldContainer(
        XElement element)
    {
        bool usesSharedFieldStyle =
            element.Name.LocalName ==
                "StackPanel" &&
            HasLiteralClass(
                (string?)element.Attribute(
                    "Classes") ??
                "",
                "field");
        bool enablesAssociation =
            element.Attributes()
                .Any(attribute =>
                    attribute.Name.LocalName ==
                        "FieldAccessibility.Associate" &&
                    string.Equals(
                        attribute.Value,
                        "True",
                        StringComparison.OrdinalIgnoreCase));
        return usesSharedFieldStyle ||
            enablesAssociation;
    }

    private static XElement ParseFormLabelFixture(
        string content)
    {
        XDocument document =
            XDocument.Parse(
                $"""
                 <UserControl xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                   {content}
                 </UserControl>
                 """);
        return Assert.Single(
            document.Descendants(),
            element =>
                IsFormInput(element));
    }

    private static XElement
        ParseExplicitFormLabelFixture(
            string labeledBy)
    {
        XDocument document =
            XDocument.Parse(
                """
                <UserControl xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <Grid>
                    <TextBlock x:Name="TitleLabel"
                               Classes="field-label"
                               Text="Title" />
                    <TextBox />
                  </Grid>
                </UserControl>
                """);
        XElement input =
            Assert.Single(
                document.Descendants(),
                element =>
                    IsFormInput(element));
        input.SetAttributeValue(
            "AutomationProperties.LabeledBy",
            labeledBy);
        return input;
    }

    private static string ResolveUiIdentity(
        string value,
        IReadOnlyDictionary<string, string>
            resources)
    {
        string trimmed = value.Trim();
        const string prefix =
            "{DynamicResource Loc.";
        if (trimmed.StartsWith(
                prefix,
                StringComparison.Ordinal) &&
            trimmed.EndsWith(
                '}'))
        {
            string key =
                trimmed[
                    prefix.Length..
                    ^1];
            return resources.GetValueOrDefault(
                    key,
                    trimmed)
                .Trim();
        }

        return trimmed;
    }

    private static bool IsVisibleTooltipLabel(
        XElement owner,
        XElement candidate)
    {
        if ((string?)candidate.Attribute(
                "IsVisible") ==
            "False")
        {
            return false;
        }

        foreach (XElement ancestor in
                 candidate.Ancestors())
        {
            if (ReferenceEquals(
                    ancestor,
                    owner))
                return true;
            if (ancestor.Name.LocalName is
                "Button" or
                "ContextMenu" or
                "Flyout" or
                "MenuFlyout" or
                "ToolTip")
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsSpacingAttribute(
        XAttribute attribute,
        IReadOnlySet<string> spacingAttributes)
    {
        if (spacingAttributes.Contains(
                attribute.Name.LocalName))
            return true;

        return attribute.Name.LocalName ==
                "Value" &&
            attribute.Parent?.Name.LocalName ==
                "Setter" &&
            spacingAttributes.Contains(
                (string?)attribute.Parent
                    .Attribute("Property") ??
                "");
    }

    private static string GetSpacingPropertyName(
        XAttribute attribute) =>
        attribute.Name.LocalName == "Value"
            ? (string?)attribute.Parent?
                .Attribute("Property") ??
              "Value"
            : attribute.Name.LocalName;

    private static bool IsFormInput(
        XElement element) =>
        element.Name.LocalName is
            "ComboBox" or
            "NumericUpDown" or
            "Slider" or
            "TextBox";

    private static bool IsVisibleLabelElement(
        XElement element) =>
        element.Name.LocalName ==
            "TextBlock" &&
        HasLiteralClass(
            (string?)element.Attribute(
                "Classes") ??
            "",
            "field-label") &&
        !string.IsNullOrWhiteSpace(
            (string?)element.Attribute(
                "Text")) &&
        ((string?)element.Attribute(
             "IsVisible") !=
         "False");

    private static bool HasLiteralClass(
        string classes,
        string expected) =>
        !classes.StartsWith(
            '{') &&
        classes.Split(
                ' ',
                StringSplitOptions
                    .RemoveEmptyEntries)
            .Contains(
                expected,
                StringComparer.Ordinal);

    private static bool IsUnapprovedHardCodedUiCopy(
        string value)
    {
        if (string.IsNullOrWhiteSpace(
                value) ||
            value.StartsWith(
                '{') ||
            ApprovedHardCodedTechnicalCopy
                .Contains(value) ||
            !value.Any(char.IsLetter))
        {
            return false;
        }

        // Geometry mini-language and URI values are not copy.
        if (value.StartsWith(
                "M",
                StringComparison.Ordinal) &&
            value.Any(char.IsDigit) &&
            value.Any(character =>
                character is ',' or 'L' or
                    'C' or 'A' or 'Z'))
        {
            return false;
        }
        if (Uri.TryCreate(
                value,
                UriKind.Absolute,
                out _))
        {
            return false;
        }

        return true;
    }

    private static bool IsUiTextElement(
        string localName) =>
        localName is
            "Button" or
            "CheckBox" or
            "ComboBoxItem" or
            "Content" or
            "Header" or
            "HelpText" or
            "ListBoxItem" or
            "MenuItem" or
            "PlaceholderText" or
            "RadioButton" or
            "Run" or
            "Span" or
            "TabItem" or
            "Text" or
            "TextBlock" or
            "Tip" or
            "Title" or
            "Watermark";

    private static string Describe(
        XObject source)
    {
        if (source is
                System.Xml.IXmlLineInfo lineInfo &&
            lineInfo.HasLineInfo())
            return $"line {lineInfo.LineNumber}";
        return "unknown line";
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
        @"^\{\s*(?:Binding\s+(?:#(?<name>[A-Za-z_][A-Za-z0-9_]*)|ElementName\s*=\s*(?<name>[A-Za-z_][A-Za-z0-9_]*))|x:Reference\s+(?:Name\s*=\s*)?(?<name>[A-Za-z_][A-Za-z0-9_]*))\s*\}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        LabeledByElementReferencePattern();

    [GeneratedRegex(
        @"\.(?<name>[A-Za-z][A-Za-z0-9_-]*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        StyleClassPattern();

    [GeneratedRegex(
        @"new\s+(?:(?:global::)?Avalonia\.)?Thickness\s*\((?<arguments>[^)]*)\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        ThicknessConstructorPattern();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_.])[-+]?\d+(?:\.\d+)?(?![A-Za-z0-9_.])",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        NumericLiteralPattern();
}
