using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MusicLibraryManager.Tests;

/// <summary>
/// Final C#-side localization boundary. XAML has a separate exhaustive guard;
/// this verifier concentrates on application-owned text created in view models,
/// services, code-behind, and presentation helpers.
/// </summary>
public sealed partial class WholePresentationLocalizationSourceTests
{
    private static readonly string[] SourceRoots =
    [
        "MusicLibraryManager.Presentation",
        "MusicLibraryManager",
    ];

    private static readonly HashSet<ApprovedLiteral>
        ApprovedUiLiterals =
    [
        new(
            "MusicLibraryManager.Presentation/SettingsChoiceLists.cs",
            "Stereo",
            "Semantic fallback; SettingsViewModel projects the value through ChannelLocalizedChoices."),
        new(
            "MusicLibraryManager.Presentation/SettingsChoiceLists.cs",
            "Multi",
            "Semantic fallback; SettingsViewModel projects the value through ChannelLocalizedChoices."),
        new(
            "MusicLibraryManager.Presentation/WorkbenchViewModel.cs",
            "MusicBrainz",
            "Provider product name used only to initialize a choice that the constructor replaces with its localized projection."),
        new(
            "MusicLibraryManager.Presentation/Localization/ResourceLocalizationService.cs",
            "At least one UI culture is required.",
            "Programmer precondition for localization-service construction."),
    ];

    private static readonly HashSet<ApprovedGridHeader>
        ApprovedGridHeaders =
        [];

    private static readonly HashSet<string>
        CountLocalizationMethods =
    [
        "FormatCount",
        "LC",
        "LFC",
        "SetCountJobStatus",
        "SetCountOperationStatus",
        "SetCountRestorePreview",
        "SetCountStatus",
        "SetCountStatusText",
        "SetFieldMappingCountStatus",
        "SetHistoryCountStatus",
        "SetThumbnailCountStatus",
    ];

    [Fact]
    public void Application_owned_csharp_ui_text_uses_catalog_resources_or_documented_invariants()
    {
        string repositoryRoot =
            FindRepositoryRoot();
        HashSet<string> resourceKeys =
            LoadResourceKeys(repositoryRoot);
        HashSet<string> resourcePrefixes =
            resourceKeys
                .Select(key =>
                    key.Split('.')[0])
                .ToHashSet(
                    StringComparer.Ordinal);
        var errors = new List<string>();
        var observedLiterals =
            new HashSet<ApprovedLiteral>();
        var observedGridHeaders =
            new HashSet<ApprovedGridHeader>();

        foreach ((string relativePath, string source) in
                 EnumerateSources(repositoryRoot))
        {
            string code = RemoveComments(source);
            ValidateLocalizationLookups(
                relativePath,
                code,
                resourceKeys,
                errors);
            ValidateCatalogLookingLiterals(
                relativePath,
                source,
                code,
                resourceKeys,
                resourcePrefixes,
                errors);
            ValidateHealthRunTextCalls(
                relativePath,
                source,
                code,
                resourceKeys,
                errors);
            ValidateUiLiteralSinks(
                relativePath,
                source,
                code,
                resourceKeys,
                observedLiterals,
                errors);
            ValidateMetadataBuilderLiterals(
                relativePath,
                source,
                code,
                resourceKeys,
                errors);
            ValidateGridHeaders(
                relativePath,
                source,
                code,
                resourceKeys,
                observedGridHeaders,
                errors);
        }

        ValidateSettingsChannelProjection(
            repositoryRoot,
            resourceKeys,
            errors);

        foreach (ApprovedLiteral stale in
                 ApprovedUiLiterals.Except(
                     observedLiterals))
        {
            errors.Add(
                $"Stale C# UI-literal allowlist entry: {stale.Path}: \"{stale.Value}\" ({stale.Reason})");
        }
        foreach (ApprovedGridHeader stale in
                 ApprovedGridHeaders.Except(
                     observedGridHeaders))
        {
            errors.Add(
                $"Stale grid-header allowlist entry: {stale.Path}: {stale.Key}=\"{stale.Header}\" ({stale.Reason})");
        }

        string[] distinctErrors =
            errors
                .Distinct(
                    StringComparer.Ordinal)
                .Order(
                    StringComparer.Ordinal)
                .ToArray();
        Assert.True(
            distinctErrors.Length == 0,
            string.Join(
                Environment.NewLine,
                distinctErrors));
    }

    private static void ValidateMetadataBuilderLiterals(
        string relativePath,
        string originalSource,
        string source,
        HashSet<string> resourceKeys,
        List<string> errors)
    {
        if (relativePath !=
            "MusicLibraryManager.Presentation/MetadataOperationEditorViewModel.cs")
        {
            return;
        }

        foreach (string typeName in
                 new[]
                 {
                     "PendingMetadataOperationRowBuilder",
                     "MetadataPreviewRowBuilder",
                 })
        {
            (int Start, int Length)? range =
                FindTypeBody(
                    source,
                    typeName);
            if (range is not { } body)
            {
                errors.Add(
                    $"{relativePath}: could not locate the {typeName} source body.");
                continue;
            }

            string typeSource =
                source.Substring(
                    body.Start,
                    body.Length);
            foreach (Match literal in
                     StringLiteralPattern()
                         .Matches(typeSource))
            {
                string value =
                    NormalizeLiteral(
                        literal.Groups["value"].Value);
                if (value.Length == 0 ||
                    IsMetadataBuilderInvariant(
                        value))
                {
                    continue;
                }

                if (IsLocalizationKey(value))
                    continue;
                if (IsDynamicLocalizationKey(
                        value,
                        resourceKeys))
                {
                    continue;
                }
                if (value.Contains(
                        '\n') &&
                    value.Contains(
                        "Text(",
                        StringComparison.Ordinal))
                {
                    // The regular-expression tokeniser sees the nested
                    // resource literal inside an interpolated expression as
                    // part of the outer token. Both actual tokens are audited
                    // independently by the localization lookup scan.
                    continue;
                }

                string displayText =
                    InterpolationExpressionPattern()
                        .Replace(
                            value,
                            "");
                if (!displayText.Any(
                        char.IsLetter))
                {
                    continue;
                }

                errors.Add(
                    $"{relativePath}:{LineOf(originalSource, body.Start + literal.Index)}: {typeName} contains unlocalized presentation literal \"{value}\"");
            }
        }
    }

    private static bool IsMetadataBuilderInvariant(
        string value)
    {
        string displayText =
            InterpolationExpressionPattern()
                .Replace(
                    value,
                    "")
                .Trim();
        return displayText is
                "ID3v1" or
                "ID3v2" or
                "APEv2" ||
            displayText.StartsWith(
                "ID3v2.",
                StringComparison.Ordinal) &&
            displayText[
                    "ID3v2.".Length..]
                .All(character =>
                    !char.IsLetter(character));
    }

    private static (int Start, int Length)?
        FindTypeBody(
            string source,
            string typeName)
    {
        Match declaration = Regex.Match(
            source,
            $@"\bclass\s+{Regex.Escape(typeName)}\b",
            RegexOptions.CultureInvariant);
        if (!declaration.Success)
            return null;
        int openingBrace =
            source.IndexOf(
                '{',
                declaration.Index +
                declaration.Length);
        if (openingBrace < 0)
            return null;

        string braceSource =
            CommentOrStringPattern().Replace(
                source,
                match =>
                    new string(
                        match.Value.Select(character =>
                                character is '\r' or '\n'
                                    ? character
                                    : ' ')
                            .ToArray()));
        int depth = 0;
        for (int index = openingBrace;
             index < braceSource.Length;
             index++)
        {
            if (braceSource[index] == '{')
                depth++;
            else if (braceSource[index] == '}')
                depth--;
            if (depth == 0)
            {
                return (
                    openingBrace,
                    index -
                    openingBrace +
                    1);
            }
        }
        return null;
    }

    private static void ValidateLocalizationLookups(
        string relativePath,
        string source,
        HashSet<string> resourceKeys,
        List<string> errors)
    {
        foreach (Match match in
                 LocalizationLookupPattern()
                     .Matches(source))
        {
            string method =
                match.Groups["method"].Value;
            string key =
                match.Groups["key"].Value;
            bool count =
                CountLocalizationMethods.Contains(
                    method);
            if (count)
            {
                RequireResource(
                    resourceKeys,
                    key + ".One",
                    relativePath,
                    source,
                    match.Index,
                    errors);
                RequireResource(
                    resourceKeys,
                    key + ".Other",
                    relativePath,
                    source,
                    match.Index,
                    errors);
            }
            else
            {
                RequireResource(
                    resourceKeys,
                    key,
                    relativePath,
                    source,
                    match.Index,
                    errors);
            }
        }

        foreach (Match match in
                 HeaderResourceKeyPattern()
                     .Matches(source))
        {
            RequireResource(
                resourceKeys,
                match.Groups["key"].Value,
                relativePath,
                source,
                match.Index,
                errors);
        }
    }

    private static void ValidateCatalogLookingLiterals(
        string relativePath,
        string originalSource,
        string source,
        HashSet<string> resourceKeys,
        HashSet<string> resourcePrefixes,
        List<string> errors)
    {
        foreach (Match literal in
                 StringLiteralPattern()
                     .Matches(source))
        {
            string value =
                NormalizeLiteral(
                    literal.Groups["value"].Value);
            if (!IsLocalizationKey(value))
                continue;
            int separator = value.IndexOf('.');
            if (separator <= 0 ||
                !resourcePrefixes.Contains(
                    value[..separator]))
            {
                continue;
            }

            if (resourceKeys.Contains(value))
                continue;
            bool singular =
                resourceKeys.Contains(
                    value + ".One");
            bool plural =
                resourceKeys.Contains(
                    value + ".Other");
            if (singular && plural)
                continue;
            if (singular || plural)
            {
                RequireResource(
                    resourceKeys,
                    value + ".One",
                    relativePath,
                    originalSource,
                    literal.Index,
                    errors);
                RequireResource(
                    resourceKeys,
                    value + ".Other",
                    relativePath,
                    originalSource,
                    literal.Index,
                    errors);
                continue;
            }
            if (resourceKeys.Any(key =>
                    key.StartsWith(
                        value + ".",
                        StringComparison.Ordinal)))
            {
                continue;
            }
            if (IsPreferenceKeyLiteral(
                    source,
                    literal.Index))
            {
                continue;
            }

            RequireResource(
                resourceKeys,
                value,
                relativePath,
                originalSource,
                literal.Index,
                errors);
        }
    }

    private static void ValidateHealthRunTextCalls(
        string relativePath,
        string originalSource,
        string source,
        HashSet<string> resourceKeys,
        List<string> errors)
    {
        foreach (Match match in
                 HealthRunTextCallPattern()
                     .Matches(source))
        {
            RequireResource(
                resourceKeys,
                match.Groups["nameKey"].Value,
                relativePath,
                originalSource,
                match.Index,
                errors);
            string summaryKey =
                match.Groups["summaryKey"].Value;
            Group count = match.Groups["count"];
            if (count.Success &&
                !string.Equals(
                    count.Value.Trim(),
                    "null",
                    StringComparison.Ordinal))
            {
                RequireResource(
                    resourceKeys,
                    summaryKey + ".One",
                    relativePath,
                    originalSource,
                    match.Index,
                    errors);
                RequireResource(
                    resourceKeys,
                    summaryKey + ".Other",
                    relativePath,
                    originalSource,
                    match.Index,
                    errors);
            }
            else
            {
                RequireResource(
                    resourceKeys,
                    summaryKey,
                    relativePath,
                    originalSource,
                    match.Index,
                    errors);
            }
        }
    }

    private static void ValidateUiLiteralSinks(
        string relativePath,
        string originalSource,
        string source,
        HashSet<string> resourceKeys,
        HashSet<ApprovedLiteral> observed,
        List<string> errors)
    {
        CollectExpressionLiterals(
            relativePath,
            originalSource,
            source,
            UiAssignmentPattern(),
            strongSignal: true,
            resourceKeys,
            observed,
            errors);
        CollectExpressionLiterals(
            relativePath,
            originalSource,
            source,
            UiCallPattern(),
            strongSignal: true,
            resourceKeys,
            observed,
            errors);
        CollectExpressionLiterals(
            relativePath,
            originalSource,
            source,
            ValidationExceptionPattern(),
            strongSignal: true,
            resourceKeys,
            observed,
            errors);
        CollectExpressionLiterals(
            relativePath,
            originalSource,
            source,
            YieldedPresentationRowPattern(),
            strongSignal: false,
            resourceKeys,
            observed,
            errors);
        CollectExpressionLiterals(
            relativePath,
            originalSource,
            source,
            StringReturningMethodPattern(),
            strongSignal: false,
            resourceKeys,
            observed,
            errors);
        CollectExpressionLiterals(
            relativePath,
            originalSource,
            source,
            ReturnPresentationTextPattern(),
            strongSignal: false,
            resourceKeys,
            observed,
            errors);
        CollectExpressionLiterals(
            relativePath,
            originalSource,
            source,
            NamedUiArgumentPattern(),
            strongSignal: true,
            resourceKeys,
            observed,
            errors);
        CollectExpressionLiterals(
            relativePath,
            originalSource,
            source,
            SettingsChannelChoicePattern(),
            strongSignal: true,
            resourceKeys,
            observed,
            errors);
        CollectExpressionLiterals(
            relativePath,
            originalSource,
            source,
            DisplayChoiceLiteralPattern(),
            strongSignal: true,
            resourceKeys,
            observed,
            errors);
    }

    private static void CollectExpressionLiterals(
        string relativePath,
        string originalSource,
        string source,
        Regex expressionPattern,
        bool strongSignal,
        HashSet<string> resourceKeys,
        HashSet<ApprovedLiteral> observed,
        List<string> errors)
    {
        foreach (Match expression in
                 expressionPattern.Matches(source))
        {
            Group body =
                expression.Groups["expression"];
            if (!body.Success)
                continue;
            foreach (Match literal in
                     StringLiteralPattern()
                         .Matches(body.Value))
            {
                string value =
                    NormalizeLiteral(
                        literal.Groups["value"].Value);
                if (!IsUiText(
                        value,
                        strongSignal) ||
                    IsLocalizationKey(value) ||
                    IsDynamicLocalizationKey(
                        value,
                        resourceKeys) ||
                    IsSemanticInputLiteral(
                        body.Value,
                        literal))
                {
                    continue;
                }

                ApprovedLiteral? approved = null;
                foreach (ApprovedLiteral invariant in
                         ApprovedUiLiterals)
                {
                    if (invariant.Path ==
                            relativePath &&
                        invariant.Value == value)
                    {
                        approved = invariant;
                        break;
                    }
                }
                if (approved is { } approvedLiteral)
                {
                    observed.Add(
                        approvedLiteral);
                    continue;
                }

                int index =
                    body.Index + literal.Index;
                errors.Add(
                    $"{relativePath}:{LineOf(originalSource, index)}: unlocalized {expression.Groups["sink"].Value} UI text \"{value}\"");
            }
        }
    }

    private static void ValidateGridHeaders(
        string relativePath,
        string originalSource,
        string source,
        HashSet<string> resourceKeys,
        HashSet<ApprovedGridHeader> observed,
        List<string> errors)
    {
        MatchCollection columns =
            LiteralGridColumnPattern()
                .Matches(source);
        for (int index = 0;
             index < columns.Count;
             index++)
        {
            Match column = columns[index];
            string key =
                column.Groups["key"].Value;
            string header =
                NormalizeLiteral(
                    column.Groups["header"].Value);
            int nextStart = index + 1 < columns.Count
                ? columns[index + 1].Index
                : source.Length;
            int itemEnd = Math.Min(
                nextStart,
                Math.Min(
                    source.Length,
                    column.Index + 900));
            string itemSource = source[
                column.Index..itemEnd];
            Match resource =
                HeaderResourceKeyPattern()
                    .Match(itemSource);
            if (resource.Success)
            {
                RequireResource(
                    resourceKeys,
                    resource.Groups["key"].Value,
                    relativePath,
                    originalSource,
                    column.Index,
                    errors);
                continue;
            }

            ApprovedGridHeader? approved = null;
            foreach (ApprovedGridHeader invariant in
                     ApprovedGridHeaders)
            {
                if (invariant.Path ==
                        relativePath &&
                    invariant.Key == key &&
                    invariant.Header ==
                        header)
                {
                    approved = invariant;
                    break;
                }
            }
            if (approved is { } approvedHeader)
            {
                observed.Add(
                    approvedHeader);
                continue;
            }

            errors.Add(
                $"{relativePath}:{LineOf(originalSource, column.Index)}: code-created grid header {key}=\"{header}\" has no HeaderResourceKey.");
        }
    }

    private static void ValidateSettingsChannelProjection(
        string repositoryRoot,
        HashSet<string> resourceKeys,
        List<string> errors)
    {
        string path = Path.Combine(
            repositoryRoot,
            "MusicLibraryManager.Presentation",
            "SettingsViewModel.cs");
        string source =
            File.ReadAllText(path);
        if (!source.Contains(
                "ChannelLocalizedChoices",
                StringComparison.Ordinal) ||
            !source.Contains(
                "Settings.Choice.LibraryChannelSelection.",
                StringComparison.Ordinal))
        {
            errors.Add(
                "SettingsViewModel.cs: channel semantics are not projected through localized display choices.");
        }

        foreach (string value in
                 new[] { "Stereo", "Multi" })
        {
            string key =
                "Settings.Choice.LibraryChannelSelection." +
                value;
            if (!resourceKeys.Contains(key))
            {
                errors.Add(
                    $"SettingsViewModel.cs: missing localized channel choice {key}");
            }
        }
    }

    private static bool IsUiText(
        string value,
        bool strongSignal)
    {
        string trimmed =
            value.Trim();
        if (trimmed.Length == 0 ||
            !trimmed.Any(char.IsLetter) ||
            IsTechnicalInvariant(trimmed))
        {
            return false;
        }

        string displayText =
            InterpolationExpressionPattern()
                .Replace(
                    trimmed,
                    "");
        if (!displayText.Any(char.IsLetter))
            return false;
        if (HighImpactSingleWordPattern()
            .IsMatch(displayText.Trim()))
        {
            return true;
        }
        return ProsePattern()
            .IsMatch(displayText) ||
            strongSignal &&
            DisplayWordPattern()
                .IsMatch(displayText.Trim());
    }

    private static bool IsTechnicalInvariant(
        string value) =>
        value.Length <= 2 &&
        !value.Any(char.IsWhiteSpace) ||
        value.StartsWith(
            "U+",
            StringComparison.Ordinal) ||
        value.StartsWith(
            "*.",
            StringComparison.Ordinal) ||
        value.StartsWith(
            "{",
            StringComparison.Ordinal) &&
        value.EndsWith(
            "}",
            StringComparison.Ordinal) ||
        MimeTypePattern().IsMatch(value) ||
        DotNetFormatStringPattern().IsMatch(value) ||
        FileNameStemPattern().IsMatch(value) ||
        FileExtensionPattern().IsMatch(value) ||
        ShortcutPattern().IsMatch(value) ||
        PathPattern().IsMatch(value);

    private static bool IsSemanticInputLiteral(
        string expression,
        Match literal)
    {
        string before = expression[
            Math.Max(
                0,
                literal.Index - 80)..literal.Index];
        string after = expression[
            Math.Min(
                expression.Length,
                literal.Index +
                literal.Length)..Math.Min(
                expression.Length,
                literal.Index +
                literal.Length +
                80)];
        if (Regex.IsMatch(
                after,
                "^\\s*=>",
                RegexOptions.CultureInvariant))
        {
            return true;
        }
        return Regex.IsMatch(
            before,
            "(?:==|!=|\\bis|\\bcase|(?:Equals|StartsWith|EndsWith)\\s*\\(\\s*|Equals\\s*\\([^,]*,)\\s*$",
            RegexOptions.CultureInvariant);
    }

    private static bool IsPreferenceKeyLiteral(
        string source,
        int literalIndex)
    {
        int lineStart =
            source.LastIndexOf(
                '\n',
                Math.Max(
                    0,
                    literalIndex - 1)) +
            1;
        int lineEnd =
            source.IndexOf(
                '\n',
                literalIndex);
        if (lineEnd < 0)
            lineEnd = source.Length;
        return source[
                lineStart..lineEnd]
            .Contains(
                "Preference",
                StringComparison.Ordinal);
    }

    private static bool IsLocalizationKey(
        string value) =>
        LocalizationKeyPattern()
            .IsMatch(value);

    private static bool IsDynamicLocalizationKey(
        string value,
        HashSet<string> resourceKeys)
    {
        if (!value.Contains(
                '{',
                StringComparison.Ordinal))
        {
            return false;
        }
        string prefix =
            value[..value.IndexOf(
                '{')];
        int closingBrace =
            value.LastIndexOf(
                '}');
        string suffix =
            closingBrace < 0
                ? ""
                : value[(closingBrace + 1)..];
        return prefix.EndsWith(
                ".",
                StringComparison.Ordinal) &&
            resourceKeys.Any(key =>
                key.StartsWith(
                    prefix,
                    StringComparison.Ordinal) &&
                key.EndsWith(
                    suffix,
                    StringComparison.Ordinal));
    }

    private static string NormalizeLiteral(
        string value) =>
        value.Replace(
                "\\\"",
                "\"",
                StringComparison.Ordinal)
            .Replace(
                "\"\"",
                "\"",
                StringComparison.Ordinal);

    private static string RemoveComments(
        string source) =>
        CommentOrStringPattern().Replace(
            source,
            match =>
            {
                if (match.Groups["string"].Success)
                    return match.Value;
                return new string(
                    match.Value.Select(character =>
                            character is '\r' or '\n'
                                ? character
                                : ' ')
                        .ToArray());
            });

    private static IEnumerable<(
        string RelativePath,
        string Source)> EnumerateSources(
        string repositoryRoot)
    {
        foreach (string rootName in
                 SourceRoots)
        {
            string root = Path.Combine(
                repositoryRoot,
                rootName);
            foreach (string path in
                     Directory.EnumerateFiles(
                         root,
                         "*.cs",
                         SearchOption.AllDirectories)
                         .Where(path =>
                             !path.Contains(
                                 $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                                 StringComparison.OrdinalIgnoreCase) &&
                             !path.Contains(
                                 $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                                 StringComparison.OrdinalIgnoreCase)))
            {
                yield return (
                    Path.GetRelativePath(
                            repositoryRoot,
                            path)
                        .Replace(
                            Path.DirectorySeparatorChar,
                            '/'),
                    File.ReadAllText(path));
            }
        }
    }

    private static void RequireResource(
        HashSet<string> resourceKeys,
        string key,
        string relativePath,
        string source,
        int index,
        List<string> errors)
    {
        if (!resourceKeys.Contains(key))
        {
            errors.Add(
                $"{relativePath}:{LineOf(source, index)}: missing localization resource {key}");
        }
    }

    private static int LineOf(
        string source,
        int index)
    {
        int line = 1;
        for (int offset = 0;
             offset < index &&
             offset < source.Length;
             offset++)
        {
            if (source[offset] == '\n')
                line++;
        }
        return line;
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

    [GeneratedRegex(
        "(?s)(?<string>\\$?@?\"(?:\"\"|\\\\.|[^\"\\\\])*\")|(?<comment>//[^\\r\\n]*|/\\*.*?\\*/)",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        CommentOrStringPattern();

    [GeneratedRegex(
        "\\$?@?\"(?<value>(?:\"\"|\\\\.|[^\"\\\\])*)\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        StringLiteralPattern();

    [GeneratedRegex(
        "(?is)(?<![A-Za-z0-9_])(?<sink>(?:(?:[A-Za-z_][A-Za-z0-9_.]*)?(?:statusText|statusMessage|operationStatus|progressText|overview|title|subtitle|summary|artworkSummary|filterError|validationError|errorMessage|message|description|label|display|resultText|diagnosticText|unicodeDetails|thumbnailStatus|rootHealth|lastScanText)|_?(?:roles|front|back|approved|before|after)))\\b\\s*(?:=|=>)\\s*(?<expression>.{0,900}?);",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        UiAssignmentPattern();

    [GeneratedRegex(
        "(?is)(?<sink>ConfirmAsync|PickFileAsync|PickFolderAsync|SaveFileAsync|FilePickerType|Begin[A-Za-z]*Operation|BeginActivity|CreateRecipe|OperationRecipe\\.Create|SetStatus|SetStatusText|SetOperationStatus|SetFailure)\\s*\\((?<expression>.{0,900}?)\\)\\s*;",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        UiCallPattern();

    [GeneratedRegex(
        "(?is)(?<sink>InvalidOperationException|ArgumentException|[A-Za-z][A-Za-z0-9_]*ValidationException)\\s*\\((?<expression>.{0,500}?)\\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        ValidationExceptionPattern();

    [GeneratedRegex(
        "(?is)(?<sink>yield\\s+return\\s+new)\\s*\\((?<expression>.{0,1200}?)\\)\\s*;",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        YieldedPresentationRowPattern();

    [GeneratedRegex(
        "(?is)(?:public|private|internal)\\s+(?:static\\s+)?string\\??\\s+(?<sink>[A-Za-z][A-Za-z0-9_]*)\\s*(?:\\([^)]*\\))?\\s*=>\\s*(?<expression>.{0,900}?);",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        StringReturningMethodPattern();

    [GeneratedRegex(
        "(?is)(?<sink>return)\\s+(?<expression>.{0,1200}?);",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        ReturnPresentationTextPattern();

    [GeneratedRegex(
        "(?is)(?<sink>(?:Title|Subtitle|Message|Summary|Description|Label|Status|Error|Overview)\\s*:)\\s*(?<expression>\\$?@?\"(?:\"\"|\\\\.|[^\"\\\\])*\")",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        NamedUiArgumentPattern();

    [GeneratedRegex(
        "(?is)(?<sink>LibraryChannelSelection\\.(?:Stereo|Multi))\\s*,\\s*(?<expression>\\$?@?\"(?:\"\"|\\\\.|[^\"\\\\])*\")",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        SettingsChannelChoicePattern();

    [GeneratedRegex(
        "(?is)(?<sink>new)(?:\\s+[A-Za-z_][A-Za-z0-9_<>?,. ]*)?\\s*\\(\\s*(?:[A-Za-z_][A-Za-z0-9_]*\\.)+[A-Za-z_][A-Za-z0-9_]*\\s*,\\s*(?<expression>\\$?@?\"(?:\"\"|\\\\.|[^\"\\\\])*\")",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        DisplayChoiceLiteralPattern();

    [GeneratedRegex(
        "(?s)(?:(?:LocalizedText|_localization)\\??\\.(?<method>Get|Format|FormatCount)|(?<![A-Za-z0-9_.])(?<method>Get|Format|FormatCount|L|LF|LC|LFC|Text|SetStatus|SetStatusText|SetCountStatus|SetCountStatusText|SetOperationStatus|SetCountOperationStatus|SetOperationFailure|SetFailure|SetStatusFailure|SetDiscogsStatus|SetFieldMappingStatus|SetFieldMappingCountStatus|SetHistoryStatus|SetHistoryCountStatus|SetHistoryFailure|SetJobStatus|SetCountJobStatus|SetJobStatusFailure|SetRestorePreview|SetCountRestorePreview|SetPurgePreview|SetThumbnailStatus|SetThumbnailCountStatus|SetVisualFilterStatus|SetProviderProgressStatus))\\s*\\(\\s*(?:(?:MessageTone\\.[A-Za-z]+|[A-Za-z_][A-Za-z0-9_?.]*)\\s*,\\s*)?\"(?<key>[A-Za-z][A-Za-z0-9.]+)\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        LocalizationLookupPattern();

    [GeneratedRegex(
        "(?s)(?<![A-Za-z0-9_.])RunText\\s*\\(\\s*[^,()]+,\\s*\"(?<nameKey>[A-Za-z][A-Za-z0-9.]+)\"\\s*,\\s*\"(?<summaryKey>[A-Za-z][A-Za-z0-9.]+)\"(?:\\s*,\\s*(?<count>[^,()\\r\\n]+))?",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        HealthRunTextCallPattern();

    [GeneratedRegex(
        "HeaderResourceKey\\s*:\\s*\"(?<key>[A-Za-z][A-Za-z0-9.]+)\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        HeaderResourceKeyPattern();

    [GeneratedRegex(
        "(?s)new(?:\\s+AppGridColumnDefinition)?\\s*\\(\\s*\"(?<key>[^\"]+)\"\\s*,\\s*\"(?<header>[^\"]+)\"\\s*,\\s*(?:\"[^\"]*\"|null)\\s*,\\s*\\d",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        LiteralGridColumnPattern();

    [GeneratedRegex(
        "\\p{L}{2,}(?:[^\\p{L}\\r\\n]+|\\s+)\\p{L}{2,}",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        ProsePattern();

    [GeneratedRegex(
        "^(?:Yes|No|Other|Before|After|Candidate|Cached|Ready|Running|Completed|Failed|Stereo|Multi|more|\\(missing\\)|\\(empty\\))$",
        RegexOptions.IgnoreCase |
        RegexOptions.CultureInvariant)]
    private static partial Regex
        HighImpactSingleWordPattern();

    [GeneratedRegex(
        "^[A-Za-z][A-Za-z -]*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        DisplayWordPattern();

    [GeneratedRegex(
        "\\{[^{}]+\\}",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        InterpolationExpressionPattern();

    [GeneratedRegex(
        "^[A-Za-z][A-Za-z0-9]*(?:\\.[A-Za-z0-9]+)+$",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        LocalizationKeyPattern();

    [GeneratedRegex(
        "^(?:\\.?[A-Za-z0-9_-]+\\.(?:mp3|flac|m4a|wav|wv|ogg|opus|m3u8?|wpl|csv|tsv|json|xml|db|exe|dll)|(?:mp3|flac|m4a|wav|wv|ogg|opus|m3u8?|wpl|csv|tsv|json|xml|utf-8|utf-16|utf-16be|ascii|crlf|lf))$",
        RegexOptions.IgnoreCase |
        RegexOptions.CultureInvariant)]
    private static partial Regex
        FileExtensionPattern();

    [GeneratedRegex(
        "^[A-Za-z][A-Za-z0-9.+-]*/[A-Za-z0-9.+-]+$",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        MimeTypePattern();

    [GeneratedRegex(
        "^(?:[yMdHhmsftzK:/\\\\ .,'%-]+|[PpNnFfEeGgCcDdXxRr][0-9]*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        DotNetFormatStringPattern();

    [GeneratedRegex(
        "^[a-z0-9]+(?:-[a-z0-9]+)*\\.$",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        FileNameStemPattern();

    [GeneratedRegex(
        "^(?:(?:Ctrl|Control|Alt|Shift|Meta|Cmd|Command|Win)\\+)+[A-Za-z0-9]+$",
        RegexOptions.IgnoreCase |
        RegexOptions.CultureInvariant)]
    private static partial Regex
        ShortcutPattern();

    [GeneratedRegex(
        "^(?:[A-Za-z]:\\\\|\\\\\\\\|/|\\.\\.?[/\\\\])",
        RegexOptions.CultureInvariant)]
    private static partial Regex
        PathPattern();

    private readonly record struct ApprovedLiteral(
        string Path,
        string Value,
        string Reason);

    private readonly record struct ApprovedGridHeader(
        string Path,
        string Key,
        string Header,
        string Reason);
}
