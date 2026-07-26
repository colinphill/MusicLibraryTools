using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed partial class CommandCopyQualityTests
{
    private static readonly HashSet<string>
        CommandElementNames =
    [
        "Button",
        "DropDownButton",
        "HyperlinkButton",
        "MenuItem",
        "SplitButton",
        "ToggleButton",
    ];

    private static readonly HashSet<string>
        LegitimateEllipsisKeys =
    [
        // Each command opens a picker, dialog, editor, or configuration
        // surface in which the user must make another decision.
        "Common.Browse",
        "Inspector.View.AddArtwork",
        "Inspector.View.ReplaceArtwork",
        "Inspector.View.SaveArtworkToFile",
        "Library.Handoff.Action",
        "Settings.Action.Browse",
        "Settings.Action.SaveAs",
        "Settings.Playlists.BrowseDestination",
        "Settings.Playlists.BrowseFile",
        "Transcode.Action.Open",
    ];

    private static readonly HashSet<string>
        ProperDestinationCommandKeys =
    [
        // Stable navigation destination names remain proper labels inside
        // otherwise sentence-case commands.
        "Home.Action.OpenHealth",
        "Ingest.Action.OpenRecovery",
        "Library.Handoff.Action",
        "Workbench.Session.Action.SendToIngest",
    ];

    [Fact]
    public void
        Every_direct_command_ellipsis_opens_a_follow_up_decision_surface()
    {
        string root = FindRepositoryRoot();
        HashSet<string> commandKeys =
            DiscoverDirectCommandResourceKeys(root);
        Assert.All(
            LegitimateEllipsisKeys,
            key => Assert.Contains(key, commandKeys));

        foreach (string catalogPath in
                 EnumerateCatalogs(root))
        {
            Dictionary<string, string> catalog =
                LoadCatalog(catalogPath);
            string[] invalid =
            [
                .. commandKeys
                    .Where(key =>
                        catalog.TryGetValue(
                            key,
                            out string? value) &&
                        value.Contains(
                            '\u2026') &&
                        !LegitimateEllipsisKeys
                            .Contains(key))
                    .OrderBy(
                        key => key,
                        StringComparer.Ordinal),
            ];
            string[] missing =
            [
                .. LegitimateEllipsisKeys
                    .Where(key =>
                        !catalog.TryGetValue(
                            key,
                            out string? value) ||
                        !value.Contains('\u2026'))
                    .OrderBy(
                        key => key,
                        StringComparer.Ordinal),
            ];
            Assert.True(
                invalid.Length == 0 &&
                missing.Length == 0,
                $"{Path.GetFileName(catalogPath)} has an invalid command " +
                $"ellipsis contract.{Environment.NewLine}" +
                $"Unexpected: {string.Join(", ", invalid)}" +
                Environment.NewLine +
                $"Missing: {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void
        Every_direct_english_command_uses_sentence_case_unless_it_names_a_destination()
    {
        string root = FindRepositoryRoot();
        Dictionary<string, string> neutral =
            LoadCatalog(
                Path.Combine(
                    root,
                    "MusicLibraryManager.Presentation",
                    "Resources",
                    "Strings.resx"));
        HashSet<string> commandKeys =
            DiscoverDirectCommandResourceKeys(root);

        string[] violations =
        [
            .. commandKeys
                .Where(key =>
                    neutral.TryGetValue(
                        key,
                        out string? value) &&
                    LooksLikeTitleCaseCommand(value) &&
                    !ProperDestinationCommandKeys
                        .Contains(key))
                .OrderBy(
                    key => key,
                    StringComparer.Ordinal)
                .Select(key =>
                    $"{key}={neutral[key]}"),
        ];

        Assert.True(
            violations.Length == 0,
            "Direct command resources must use sentence case. Add an " +
            "exception only for a stable proper destination name." +
            Environment.NewLine +
            string.Join(
                Environment.NewLine,
                violations));
    }

    [Fact]
    public void
        Display_language_help_describes_live_beta_translation_without_claiming_english_only()
    {
        string root = FindRepositoryRoot();
        var requiredTerms =
            new Dictionary<string, string[]>(
                StringComparer.Ordinal)
            {
                ["en-US"] =
                    ["immediately", "Beta", "machine-assisted", "metadata", "file names", "parsing", "sorting"],
                ["de-DE"] =
                    ["sofort", "Beta", "maschinell", "Bibliotheksmetadaten", "Dateinamen", "Analyse", "Sortierung"],
                ["es-ES"] =
                    ["inmediato", "Beta", "máquina", "metadatos", "nombres de archivo", "análisis", "ordenación"],
                ["fr-FR"] =
                    ["immédiatement", "Bêta", "ordinateur", "métadonnées", "noms de fichiers", "analyse", "tri"],
                ["it-IT"] =
                    ["immediatamente", "Beta", "automaticamente", "metadati", "nomi dei file", "analisi", "ordinamento"],
                ["pt-BR"] =
                    ["imediatamente", "Beta", "máquina", "metadados", "nomes de arquivos", "análise", "ordenação"],
                ["ja-JP"] =
                    ["すぐに", "ベータ", "機械", "メタデータ", "ファイル名", "解析", "並べ替え"],
                ["ko-KR"] =
                    ["즉시", "베타", "기계", "메타데이터", "파일 이름", "구문 분석", "정렬"],
                ["zh-CN"] =
                    ["立即", "测试版", "机器", "元数据", "文件名", "解析", "排序"],
                ["zh-TW"] =
                    ["立即", "測試版", "機器", "中繼資料", "檔案名稱", "剖析", "排序"],
            };
        var obsoleteEnglishOnlyClaims =
            new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["en-US"] =
                    "English (United States) is currently available",
                ["de-DE"] = "Derzeit ist Englisch",
                ["es-ES"] =
                    "Actualmente está disponible Inglés",
                ["fr-FR"] =
                    "L’anglais (États-Unis) est actuellement disponible",
                ["it-IT"] =
                    "Al momento è disponibile Inglese",
                ["pt-BR"] = "No momento, Inglês",
                ["ja-JP"] = "現在は英語",
                ["ko-KR"] = "현재 영어",
                ["zh-CN"] = "目前可使用英语",
                ["zh-TW"] = "目前可使用英文",
            };

        foreach (string catalogPath in
                 EnumerateCatalogs(root))
        {
            string fileName =
                Path.GetFileNameWithoutExtension(
                    catalogPath);
            string culture =
                fileName == "Strings"
                    ? "en-US"
                    : fileName["Strings.".Length..];
            string description =
                LoadCatalog(catalogPath)[
                    "Settings.Appearance.DisplayLanguageDescription"];

            Assert.All(
                requiredTerms[culture],
                term => Assert.Contains(
                    term,
                    description,
                    StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                obsoleteEnglishOnlyClaims[culture],
                description,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void
        Inline_edit_copy_preserves_conflict_context_and_literal_semicolon_syntax()
    {
        string root = FindRepositoryRoot();
        string neutralPath = Path.Combine(
            root,
            "MusicLibraryManager.Presentation",
            "Resources",
            "Strings.resx");
        Dictionary<string, string> neutral =
            LoadCatalog(neutralPath);

        Assert.Equal(
            "The file changed after editing started. Reload it before " +
            "applying the pending change.",
            neutral["Library.PendingChanges.SourceChanged"]);
        Assert.Equal(
            "The {0} value changed after editing started. Reload it " +
            "before applying the pending change.",
            neutral["Library.PendingChanges.FieldChanged"]);
        string inlineDescription =
            neutral[
                "Workbench.Columns.InlineEditingDescription"];
        Assert.Contains(
            "standard fields",
            inlineDescription,
            StringComparison.Ordinal);
        Assert.Contains(
            "custom text fields",
            inlineDescription,
            StringComparison.Ordinal);
        Assert.Contains(
            "multi-value fields",
            inlineDescription,
            StringComparison.Ordinal);

        foreach (string catalogPath in
                 EnumerateCatalogs(root))
        {
            Dictionary<string, string> catalog =
                LoadCatalog(catalogPath);
            Assert.Contains(
                "{0}",
                catalog[
                    "Library.PendingChanges.FieldChanged"],
                StringComparison.Ordinal);
            Assert.Contains(
                "\\;",
                catalog[
                    "Workbench.Columns.InlineEditingDescription"],
                StringComparison.Ordinal);
        }
    }

    private static bool LooksLikeTitleCaseCommand(
        string value)
    {
        string[] words = WordSeparator()
            .Split(value)
            .Where(word =>
                FirstAsciiLetter().IsMatch(word))
            .ToArray();
        return words
            .Skip(1)
            .Any(word =>
                CapitalizedOrdinaryWord()
                    .IsMatch(word));
    }

    private static HashSet<string>
        DiscoverDirectCommandResourceKeys(
            string repositoryRoot)
    {
        var keys = new HashSet<string>(
            StringComparer.Ordinal);
        foreach (string path in
                 Directory.EnumerateFiles(
                     Path.Combine(
                         repositoryRoot,
                         "MusicLibraryManager"),
                     "*.axaml",
                     SearchOption.AllDirectories))
        {
            XDocument document = XDocument.Load(
                path,
                LoadOptions.SetLineInfo);
            foreach (XElement command in
                     document.Descendants()
                         .Where(element =>
                             CommandElementNames
                                 .Contains(
                                     element.Name
                                         .LocalName)))
            {
                IEnumerable<XElement> ownedContent =
                    command
                        .DescendantsAndSelf()
                        .Where(element =>
                            ReferenceEquals(
                                element,
                                command) ||
                            !element.Ancestors()
                                .TakeWhile(ancestor =>
                                    !ReferenceEquals(
                                        ancestor,
                                        command))
                                .Any(ancestor =>
                                    CommandElementNames
                                        .Contains(
                                            ancestor.Name
                                                .LocalName)));
                foreach (XAttribute attribute in
                         ownedContent
                             .SelectMany(element =>
                                 element.Attributes())
                             .Where(attribute =>
                                 attribute.Name.LocalName
                                     is "Content" or
                                     "Header" or
                                     "Text"))
                {
                    Match match =
                        LocalizedResourceKeyPattern()
                            .Match(attribute.Value);
                    if (match.Success)
                        keys.Add(
                            match.Groups["key"].Value);
                }
            }
        }

        Assert.NotEmpty(keys);
        return keys;
    }

    private static Dictionary<string, string>
        LoadCatalog(string path) =>
        XDocument.Load(path)
            .Root!
            .Elements("data")
            .ToDictionary(
                entry =>
                    (string)entry.Attribute("name")!,
                entry =>
                    entry.Element("value")!.Value,
                StringComparer.Ordinal);

    private static IEnumerable<string>
        EnumerateCatalogs(string repositoryRoot) =>
        Directory.EnumerateFiles(
                Path.Combine(
                    repositoryRoot,
                    "MusicLibraryManager.Presentation",
                    "Resources"),
                "Strings*.resx",
                SearchOption.TopDirectoryOnly)
            .OrderBy(
                path => path,
                StringComparer.Ordinal);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current =
            new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(
                    Path.Combine(
                        current.FullName,
                        "MusicLibraryTools.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate MusicLibraryTools.sln.");
    }

    [GeneratedRegex(
        @"Loc\.(?<key>[A-Za-z0-9_.]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        LocalizedResourceKeyPattern();

    [GeneratedRegex(
        @"[\s/():\u2013\u2014-]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex WordSeparator();

    [GeneratedRegex(
        @"^[A-Za-z]",
        RegexOptions.CultureInvariant)]
    private static partial Regex FirstAsciiLetter();

    [GeneratedRegex(
        @"^[A-Z][a-z]+(?:['\u2019][A-Za-z]+)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        CapitalizedOrdinaryWord();
}
