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
        // These tooltips reveal an untrimmed path/status value whose on-screen
        // rendering is deliberately ellipsized or otherwise space constrained.
        "{Binding File}",
        "{Binding Path}",
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
            }
        }

        Assert.True(
            errors.Count == 0,
            string.Join(
                Environment.NewLine,
                errors));
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
    public void Shared_technical_choice_resources_use_canonical_spelling()
    {
        Dictionary<string, string> resources =
            LoadNeutralResources();
        var expected =
            new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["Workbench.Choice.ReportFormat.Csv"] =
                    "CSV",
                ["Workbench.Choice.ReportFormat.Html"] =
                    "HTML",
                ["Workbench.Choice.ReportFormat.Rtf"] =
                    "RTF",
                ["Workbench.Choice.ReportEncoding.Utf8"] =
                    "UTF-8",
                ["Workbench.Choice.ReportEncoding.Utf8WithBom"] =
                    "UTF-8 with BOM",
                ["Workbench.Choice.ReportEncoding.Utf16LittleEndian"] =
                    "UTF-16 LE",
                ["Workbench.Choice.PlaylistLineEnding.CrLf"] =
                    "CRLF",
                ["Workbench.Choice.PlaylistLineEnding.Lf"] =
                    "LF",
                ["Workbench.Choice.Id3EncodingPolicy.Latin1"] =
                    "Latin-1",
                ["Workbench.Choice.Id3EncodingPolicy.Utf16"] =
                    "UTF-16",
                ["Workbench.Choice.Id3EncodingPolicy.Utf8"] =
                    "UTF-8",
                ["Workbench.Choice.Id3Version.V22"] =
                    "ID3v2.2",
                ["Workbench.Choice.Id3Version.V23"] =
                    "ID3v2.3",
                ["Workbench.Choice.Id3Version.V24"] =
                    "ID3v2.4",
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
        // Inline grid/list editors derive their visible identity from the
        // row/column header or item label. They still must expose an
        // AutomationProperties.Name, which is enforced above.
        if (input.Ancestors()
            .Any(element =>
                element.Name.LocalName is
                    "DataTemplate" or
                    "TreeDataTemplate"))
        {
            return true;
        }

        XElement? field =
            input.Ancestors()
                .FirstOrDefault(element =>
                    HasLiteralClass(
                        (string?)element.Attribute(
                            "Classes") ??
                        "",
                        "field"));
        if (field is not null &&
            field.Descendants()
                .Any(element =>
                    element.Name.LocalName ==
                    "TextBlock" &&
                    !string.IsNullOrWhiteSpace(
                        (string?)element.Attribute(
                            "Text"))))
        {
            return true;
        }

        XElement current = input;
        for (int depth = 0;
             depth < 4 &&
             current.Parent is
                 { } parent;
             depth++,
             current = parent)
        {
            foreach (XElement sibling in
                     current.ElementsBeforeSelf()
                         .Reverse())
            {
                if (IsFormInput(
                        sibling) ||
                    sibling.Descendants()
                        .Any(IsFormInput))
                {
                    break;
                }

                if (IsVisibleLabelElement(
                        sibling) ||
                    sibling.Descendants()
                        .Any(
                            IsVisibleLabelElement))
                {
                    return true;
                }
            }
        }

        return false;
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
