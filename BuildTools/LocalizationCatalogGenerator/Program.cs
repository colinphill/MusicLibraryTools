using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

return CatalogGeneratorCommandLine.Run(
    args,
    CatalogGenerator.Run,
    Console.Error);

internal static partial class CatalogGenerator
{
    private static readonly Locale[] Locales =
    [
        new("de-DE", "Deutsch (Deutschland)"),
        new("es-ES", "español (España)"),
        new("fr-FR", "français (France)"),
        new("it-IT", "italiano (Italia)"),
        new("pt-BR", "português (Brasil)"),
        new("ja-JP", "日本語 (日本)"),
        new("ko-KR", "한국어 (대한민국)"),
        new("zh-CN", "简体中文（中国）"),
        new("zh-TW", "繁體中文（台灣）"),
    ];

    private static readonly string[] ProtectedLiteralTokens =
    [
        "Music Library Manager",
        "MusicLibraryManager",
        "MusicLibraryTools",
        "MusicBrainz",
        "Cover Art Archive",
        "Discogs",
        "AcoustID",
        "FFmpeg",
        "ffprobe",
        "WavPack",
        "OptimFROG",
        "Matroska",
        "WebM",
        "Vorbis Comment",
        "Vorbis",
        "Glob",
        "ReplayGain",
        "ID3v2.4",
        "ID3v2.3",
        "ID3v2",
        "ID3v1",
        "ID3",
        "APEv2",
        "SHA-256",
        "ISO-8859-1",
        "Unicode",
        "Windows-1252",
        "UTF-16LE",
        "UTF-16BE",
        "UTF-16",
        "UTF-8",
        "FLAC",
        "Ogg",
        "Opus",
        "MP3",
        "MP4",
        "AAC",
        "ALAC",
        "WAV",
        "AIFF",
        "DSF",
        "ASF",
        "WMA",
        "APE",
        "MPC",
        "TTA",
        "TAK",
        "MKA",
        "CD",
        "EXTINF",
        "JPEG",
        "PNG",
        "ASCII",
        "WAVE",
        "ASIN",
        "BOM",
        "CRLF",
        "LF",
        "CR",
        "ETA",
        "KiB",
        "MiB",
        "KB",
        "MB",
        "GB",
        "TB",
        "kbps",
        "Hz",
        "ADB",
        "adb",
        "fpcalc",
        "Chromaprint",
        "Android",
        "Windows",
        "Unix",
        "Sonos",
        "Avalonia UI",
        "AvaloniaUI",
        "Avalonia",
        "SixLabors.ImageSharp",
        "ImageSharp",
        "Six Labors Split License, Version 1.0",
        "Six Labors Split License",
        "Six Labors",
        "MIT License",
        "ITL",
        "NBSP",
        "GUID",
        "LRA",
        "WPL",
        "SD",
        "DJ",
        "KC",
        "KD",
        "UTF",
        "px",
        "loudnorm",
        "ofr",
        "ofs",
        "DiscNumLengthLimit",
        "LengthLimit",
        "FileNameWithoutExtension",
        "iTunes",
        "Monkey's Audio",
        "True Audio",
        "Musepack",
        "Latin1",
        "Utf16",
        "Utf8",
        "RTF",
        "HTML",
        "CSV",
        "ID",
        "IDs",
        "JSON",
        "TSV",
        "CSV",
        "XML",
        "HTML",
        "M3U8",
        "M3U",
        "PLS",
        "BPM",
        "ISRC",
        "URI",
        "URL",
        "CLI",
        "Ctrl+Z",
        "Ctrl+Y",
        "Ctrl+Shift+Z",
        "Shift+F10",
        "Ctrl",
        "Shift",
        "Alt",
        "OK",
    ];

    private static readonly string[]
        CaseSensitiveProtectedLiteralTokens =
        [
            "Enter",
            "Esc",
        ];

    private static readonly Dictionary<string, string> NativeAutonyms =
        new(StringComparer.Ordinal)
        {
            ["Culture.en-US"] = "English (United States)",
            ["Culture.de-DE"] = "Deutsch (Deutschland)",
            ["Culture.es-ES"] = "español (España)",
            ["Culture.fr-FR"] = "français (France)",
            ["Culture.it-IT"] = "italiano (Italia)",
            ["Culture.pt-BR"] = "português (Brasil)",
            ["Culture.ja-JP"] = "日本語 (日本)",
            ["Culture.ko-KR"] = "한국어 (대한민국)",
            ["Culture.zh-CN"] = "简体中文（中国）",
            ["Culture.zh-TW"] = "繁體中文（台灣）",
        };

    public static int Run(string[] args)
    {
        string repositoryRoot = FindRepositoryRoot();
        string resourcesDirectory = Path.Combine(
            repositoryRoot,
            "MusicLibraryManager.Presentation",
            "Resources");
        string neutralPath = Path.Combine(resourcesDirectory, "Strings.resx");
        bool checkOnly = args.Contains("--check", StringComparer.Ordinal);

        XDocument neutral = XDocument.Load(
            neutralPath,
            LoadOptions.PreserveWhitespace);
        IReadOnlyList<XElement> neutralEntries = neutral.Root!
            .Elements("data")
            .ToArray();
        IReadOnlyList<Term> terms = ParseTerms();
        var failures = new List<string>();
        var residualCjkWords = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (XElement entry in neutralEntries)
        {
            string key =
                (string?)entry.Attribute("name") ?? "";
            string value =
                entry.Element("value")?.Value ?? "";
            if (string.Equals(
                    key,
                    value,
                    StringComparison.Ordinal))
                failures.Add(
                    $"Neutral resource '{key}' exposes its resource identifier as UI text.");
        }

        foreach (Locale locale in Locales)
        {
            XDocument satellite = new(neutral);
            foreach (XElement entry in satellite.Root!.Elements("data"))
            {
                string key = (string?)entry.Attribute("name") ?? "";
                XElement valueElement = entry.Element("value") ??
                    throw new InvalidDataException(
                        $"Resource '{key}' has no value.");
                string source = valueElement.Value;
                string translated = Translate(
                    key,
                    source,
                    locale,
                    terms);
                if (string.IsNullOrWhiteSpace(translated))
                    failures.Add($"{locale.Name}:{key}: blank translation");
                if (string.Equals(
                        key,
                        translated,
                        StringComparison.Ordinal))
                    failures.Add(
                        $"{locale.Name}:{key}: translation exposes its resource identifier as UI text");
                if (!HaveMatchingPlaceholders(source, translated))
                    failures.Add($"{locale.Name}:{key}: placeholder mismatch");
                if (IsCjk(locale.Name) &&
                    !NativeAutonyms.ContainsKey(key))
                {
                    string[] residualLatinWords =
                        FindUnprotectedLatinWords(translated);
                    residualCjkWords.UnionWith(
                        residualLatinWords);
                }
                valueElement.Value = translated;
            }

            ValidateKeyParity(neutralEntries, satellite, locale, failures);
            string destination = Path.Combine(
                resourcesDirectory,
                $"Strings.{locale.Name}.resx");
            string generated = Serialize(satellite);
            if (checkOnly)
            {
                if (!File.Exists(destination) ||
                    !string.Equals(
                        File.ReadAllText(destination),
                        generated,
                        StringComparison.Ordinal))
                    failures.Add(
                        $"{locale.Name}: catalog is not generated from the current neutral catalog");
            }
            else
            {
                File.WriteAllText(
                    destination,
                    generated,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }

        if (residualCjkWords.Count > 0)
            failures.Add(
                "CJK catalogs contain unprotected Latin text: " +
                string.Join(
                    ", ",
                    residualCjkWords.OrderBy(
                        word => word,
                        StringComparer.OrdinalIgnoreCase)));

        if (failures.Count > 0)
        {
            foreach (string failure in failures.Take(100))
                Console.Error.WriteLine(failure);
            if (failures.Count > 100)
                Console.Error.WriteLine(
                    $"... and {failures.Count - 100:N0} more failures.");
            return 1;
        }

        Console.WriteLine(
            $"{(checkOnly ? "Validated" : "Generated")} " +
            $"{Locales.Length} satellite catalogs with " +
            $"{neutralEntries.Count:N0} resources each.");
        return 0;
    }

    private static string Translate(
        string key,
        string source,
        Locale locale,
        IReadOnlyList<Term> terms)
    {
        if (NativeAutonyms.TryGetValue(key, out string? autonym))
            return autonym;
        if (key == "Common.Beta")
            return locale.Name switch
            {
                "de-DE" => "Beta",
                "es-ES" => "Beta",
                "fr-FR" => "Bêta",
                "it-IT" => "Beta",
                "pt-BR" => "Beta",
                "ja-JP" => "ベータ",
                "ko-KR" => "베타",
                "zh-CN" => "测试版",
                "zh-TW" => "測試版",
                _ => "Beta",
            };
        if (ExactResourceTranslations.TryGetValue(
                key,
                out IReadOnlyDictionary<string, string>?
                    exactTranslations))
            return exactTranslations[locale.Name];

        ProtectedText protectedText = Protect(source);
        string result = protectedText.Text;
        var tokens = protectedText.Tokens.ToList();
        foreach (Term term in terms)
        {
            string replacement = term.Translations[locale.Name];
            result = term.Pattern.Replace(
                result,
                _ => AddProtectedToken(
                    replacement,
                    tokens));
        }
        result = TranslateInflectedWords(
            result,
            locale,
            terms,
            tokens);

        string[] uncoveredSourceWords =
            EnglishWordPattern()
                .Matches(result)
                .Select(match => match.Value)
                .Where(word => word.Length > 1)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    word => word,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
        if (uncoveredSourceWords.Length > 0)
            throw new InvalidDataException(
                $"Translation-memory entries do not cover '{key}': " +
                string.Join(", ", uncoveredSourceWords));

        return new ProtectedText(
                result,
                tokens)
            .Restore(result);
    }

    private static ProtectedText Protect(string value)
    {
        var tokens = new List<string>();
        string protectedValue = DynamicProtectedPattern().Replace(
            value,
            match => AddProtectedToken(match.Value, tokens));
        foreach (string literal in ProtectedLiteralTokens
                     .OrderByDescending(token => token.Length))
        {
            protectedValue = Regex.Replace(
                protectedValue,
                $@"(?<![A-Za-z0-9]){Regex.Escape(literal)}(?![A-Za-z0-9])",
                match => AddProtectedToken(match.Value, tokens),
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant);
        }
        foreach (string literal in
                 CaseSensitiveProtectedLiteralTokens)
        {
            protectedValue = Regex.Replace(
                protectedValue,
                $@"(?<![\p{{L}}\p{{N}}])" +
                Regex.Escape(literal) +
                @"(?![\p{L}\p{N}])",
                match => AddProtectedToken(
                    match.Value,
                    tokens),
                RegexOptions.CultureInvariant);
        }
        return new ProtectedText(protectedValue, tokens);
    }

    private static string AddProtectedToken(
        string value,
        ICollection<string> tokens)
    {
        int index = tokens.Count;
        tokens.Add(value);
        return $"\uE100{index:D8}\uE101";
    }

    private static Regex TermPattern(string source) =>
        new(
            $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(source)}(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase |
            RegexOptions.CultureInvariant);

    private static bool IsCjk(string cultureName) =>
        cultureName.StartsWith("ja", StringComparison.Ordinal) ||
        cultureName.StartsWith("ko", StringComparison.Ordinal) ||
        cultureName.StartsWith("zh", StringComparison.Ordinal);

    private static string[] FindUnprotectedLatinWords(
        string value) =>
        EnglishWordPattern()
            .Matches(Protect(value).Text)
            .Select(match => match.Value)
            .Where(word => word.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(word => word, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string TranslateInflectedWords(
        string value,
        Locale locale,
        IReadOnlyList<Term> terms,
        ICollection<string> tokens)
    {
        Dictionary<string, Term> words = terms
            .Where(term =>
                EnglishWordPattern().Match(term.Source) is
                    { Success: true } match &&
                match.Length == term.Source.Length)
            .GroupBy(
                term => term.Source,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);

        return EnglishWordPattern().Replace(
            value,
            match =>
            {
                foreach (string stem in EnglishStems(
                             match.Value))
                {
                    if (words.TryGetValue(
                            stem,
                            out Term? term))
                        return AddProtectedToken(
                            term.Translations[locale.Name],
                            tokens);
                }
                return match.Value;
            });
    }

    private static IEnumerable<string> EnglishStems(
        string word)
    {
        if (word.Length > 4 &&
            word.EndsWith("ies", StringComparison.OrdinalIgnoreCase))
            yield return word[..^3] + "y";
        if (word.Length > 5 &&
            word.EndsWith("ied", StringComparison.OrdinalIgnoreCase))
            yield return word[..^3] + "y";
        if (word.Length > 5 &&
            word.EndsWith("ing", StringComparison.OrdinalIgnoreCase))
        {
            string stem = word[..^3];
            yield return stem;
            yield return stem + "e";
            if (stem.Length > 2 &&
                char.ToLowerInvariant(stem[^1]) ==
                char.ToLowerInvariant(stem[^2]))
                yield return stem[..^1];
        }
        if (word.Length > 4 &&
            word.EndsWith("ed", StringComparison.OrdinalIgnoreCase))
        {
            string stem = word[..^2];
            yield return stem;
            yield return stem + "e";
        }
        if (word.Length > 4 &&
            word.EndsWith("es", StringComparison.OrdinalIgnoreCase))
        {
            yield return word[..^2];
            yield return word[..^1];
        }
        if (word.Length > 3 &&
            word.EndsWith('s'))
            yield return word[..^1];
        if (word.Length > 4 &&
            word.EndsWith("ly", StringComparison.OrdinalIgnoreCase))
            yield return word[..^2];
    }

    private static IReadOnlyList<Term> ParseTerms()
    {
        string[] localeNames = Locales
            .Select(locale => locale.Name)
            .ToArray();
        var terms = new List<Term>();
        foreach (string rawLine in TranslationRows.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            string[] columns = line.Split('|');
            if (columns.Length != localeNames.Length + 1)
                throw new InvalidDataException(
                    $"Translation row has {columns.Length} columns instead of " +
                    $"{localeNames.Length + 1}: {line}");
            var translations = new Dictionary<string, string>(
                StringComparer.Ordinal);
            for (int index = 0; index < localeNames.Length; index++)
                translations[localeNames[index]] = columns[index + 1].Trim();
            string source = columns[0].Trim();
            terms.Add(new Term(
                source,
                translations,
                TermPattern(source)));
        }

        return terms
            .OrderByDescending(term => term.Source.Length)
            .ThenBy(term => term.Source, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyDictionary<
        string,
        IReadOnlyDictionary<string, string>>
        ParseExactResourceTranslations()
    {
        string[] localeNames = Locales
            .Select(locale => locale.Name)
            .ToArray();
        var resources = new Dictionary<
            string,
            IReadOnlyDictionary<string, string>>(
            StringComparer.Ordinal);
        foreach (string rawLine in
                 ExactResourceTranslationRows.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 ||
                line.StartsWith('#'))
                continue;
            string[] columns = line.Split('|');
            if (columns.Length != localeNames.Length + 1)
                throw new InvalidDataException(
                    $"Exact resource translation row has {columns.Length} columns instead of " +
                    $"{localeNames.Length + 1}: {line}");
            var translations =
                new Dictionary<string, string>(
                    StringComparer.Ordinal);
            for (int index = 0;
                 index < localeNames.Length;
                 index++)
            {
                translations[localeNames[index]] =
                    columns[index + 1].Trim();
            }
            resources.Add(
                columns[0].Trim(),
                translations);
        }
        return resources;
    }

    private static bool HaveMatchingPlaceholders(
        string source,
        string translated) =>
        PlaceholderPattern().Matches(source)
            .Select(match => match.Value)
            .OrderBy(value => value, StringComparer.Ordinal)
            .SequenceEqual(
                PlaceholderPattern().Matches(translated)
                    .Select(match => match.Value)
                    .OrderBy(value => value, StringComparer.Ordinal),
                StringComparer.Ordinal);

    private static void ValidateKeyParity(
        IReadOnlyList<XElement> neutralEntries,
        XDocument satellite,
        Locale locale,
        ICollection<string> failures)
    {
        string[] neutralKeys = neutralEntries
            .Select(entry => (string?)entry.Attribute("name") ?? "")
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        string[] satelliteKeys = satellite.Root!
            .Elements("data")
            .Select(entry => (string?)entry.Attribute("name") ?? "")
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        if (!neutralKeys.SequenceEqual(
                satelliteKeys,
                StringComparer.Ordinal))
            failures.Add($"{locale.Name}: key set differs from neutral catalog");
    }

    private static string Serialize(XDocument document)
    {
        var builder = new StringBuilder();
        using var stringWriter = new Utf8StringWriter(builder);
        using var writer = XmlWriter.Create(
            stringWriter,
            new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false),
                Indent = false,
                NewLineChars = "\n",
                NewLineHandling = NewLineHandling.Replace,
                OmitXmlDeclaration = false,
            });
        document.Save(writer);
        writer.Flush();
        return builder.ToString();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current =
            new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(
                    current.FullName,
                    "MusicLibraryManager.Presentation")) &&
                Directory.Exists(Path.Combine(
                    current.FullName,
                    "MusicLibraryManager.Tests")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate the MusicLibraryTools repository root.");
    }

    private sealed record Locale(
        string Name,
        string NativeAutonym);

    private sealed record Term(
        string Source,
        IReadOnlyDictionary<string, string> Translations,
        Regex Pattern);

    private sealed record ProtectedText(
        string Text,
        IReadOnlyList<string> Tokens)
    {
        public string Restore(string value)
        {
            for (int index = 0; index < Tokens.Count; index++)
                value = value.Replace(
                    $"\uE100{index:D8}\uE101",
                    Tokens[index],
                    StringComparison.Ordinal);
            return value;
        }
    }

    private sealed class Utf8StringWriter(
        StringBuilder builder) : StringWriter(builder)
    {
        public override Encoding Encoding { get; } =
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false);
    }

    [GeneratedRegex(
        @"(?<!\{)\{\d+(?:,[^}:]+)?(?::[^}]*)?\}(?!\})|" +
        @"(?<![\p{L}\p{N}])[A-Za-z][A-Za-z0-9]*(?:\.[A-Za-z][A-Za-z0-9]*){2,}(?![\p{L}\p{N}])|" +
        @"(?<![\p{L}\p{N}])--?[a-z][a-z0-9-]*(?:=[^\s,;)]+)?|" +
        @"(?:[A-Za-z]:\\|(?<!\S)/)[^\s\r\n,;)]*|" +
        @"\.[a-z0-9]{2,5}(?![\p{L}\p{N}])|" +
        @"\\[nrt]",
        RegexOptions.IgnoreCase |
        RegexOptions.CultureInvariant)]
    private static partial Regex DynamicProtectedPattern();

    [GeneratedRegex(
        @"(?<!\{)\{\d+(?:,[^}:]+)?(?::[^}]*)?\}(?!\})",
        RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(
        @"[A-Za-z][A-Za-z'-]*",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishWordPattern();

    private static readonly string ExactResourceTranslationRows =
        """
        # Resource key|de-DE|es-ES|fr-FR|it-IT|pt-BR|ja-JP|ko-KR|zh-CN|zh-TW
        Navigation.About|Über|Acerca de|À propos|Informazioni|Sobre|このアプリについて|정보|关于|關於
        About.Title|Über|Acerca de|À propos|Informazioni|Sobre|このアプリについて|정보|关于|關於
        About.Subtitle|Produktdetails, Danksagungen und Lizenzvereinbarungen.|Detalles del producto, agradecimientos y acuerdos de licencia.|Détails du produit, remerciements et contrats de licence.|Dettagli del prodotto, riconoscimenti e accordi di licenza.|Detalhes do produto, agradecimentos e contratos de licença.|製品の詳細、謝辞、ライセンス契約。|제품 세부 정보, 감사의 말 및 사용권 계약입니다.|产品详细信息、致谢和许可协议。|產品詳細資訊、致謝與授權協議。
        About.Tagline|Ein durchdachter Arbeitsbereich für die Musik, die Ihnen wichtig ist.|Un espacio de trabajo pensado para la música que te importa.|Un espace de travail soigné pour la musique qui vous tient à cœur.|Uno spazio di lavoro curato per la musica a cui tieni.|Um espaço de trabalho cuidadoso para a música que importa para você.|大切な音楽のための、思いやりのあるワークスペース。|소중한 음악을 위한 세심한 작업 공간입니다.|为您珍爱的音乐打造的贴心工作区。|為您珍愛的音樂打造的貼心工作區。
        About.BrandAutomation|Music Library Manager-Logo|Logotipo de Music Library Manager|Logo Music Library Manager|Logo di Music Library Manager|Logotipo do Music Library Manager|Music Library Manager のロゴ|Music Library Manager 로고|Music Library Manager 徽标|Music Library Manager 標誌
        About.VersionFormat|Version {0}|Versión {0}|Version {0}|Versione {0}|Versão {0}|バージョン {0}|버전 {0}|版本 {0}|版本 {0}
        About.Mission.Title|Für langlebige Musikbibliotheken entwickelt|Diseñado para bibliotecas musicales duraderas|Conçu pour des bibliothèques musicales durables|Progettato per librerie musicali durature|Feito para bibliotecas de música duradouras|長く使える音楽ライブラリのために|오래 지속되는 음악 라이브러리를 위해 제작|专为持久的音乐库而打造|專為長久的音樂資料庫打造
        About.Mission.Description|Prüfen, ergänzen und bewahren Sie Ihre Sammlung mit transparenten, überprüfbaren Änderungen.|Inspecciona, enriquece y conserva tu colección con cambios transparentes y revisables.|Inspectez, enrichissez et préservez votre collection grâce à des modifications transparentes et vérifiables.|Esamina, arricchisci e conserva la tua raccolta con modifiche trasparenti e verificabili.|Inspecione, enriqueça e preserve sua coleção com alterações transparentes e revisáveis.|透明で確認可能な変更により、コレクションを調査、強化、保護します。|투명하고 검토 가능한 변경으로 컬렉션을 검사하고 보강하며 보존합니다.|通过透明、可审查的更改来检查、丰富和保护您的收藏。|透過透明、可檢閱的變更來檢查、豐富並保存您的收藏。
        About.OpenSource.Title|Open-Source-Grundlagen|Fundamentos de código abierto|Fondations open source|Fondamenti open source|Fundamentos de código aberto|オープンソースの基盤|오픈 소스 기반|开源基础|開放原始碼基礎
        About.OpenSource.Description|Music Library Manager wird teilweise durch diese verwendeten Pakete ermöglicht.|Music Library Manager es posible en parte gracias a estos paquetes utilizados.|Music Library Manager est rendu possible en partie par ces paquets utilisés.|Music Library Manager è reso possibile in parte da questi pacchetti utilizzati.|O Music Library Manager é possível em parte graças a estes pacotes utilizados.|Music Library Manager は、これらの参照パッケージによって支えられています。|Music Library Manager는 이러한 참조 패키지의 도움으로 만들어졌습니다.|Music Library Manager 的实现部分得益于这些引用的软件包。|Music Library Manager 的實現部分得益於這些參照套件。
        About.Package.VersionFormat|Version {0}|Versión {0}|Version {0}|Versione {0}|Versão {0}|バージョン {0}|버전 {0}|版本 {0}|版本 {0}
        About.Package.Avalonia.Name|Avalonia UI|Avalonia UI|Avalonia UI|Avalonia UI|Avalonia UI|Avalonia UI|Avalonia UI|Avalonia UI|Avalonia UI
        About.Package.Avalonia.Identity|Paketfamilie: Avalonia|Familia de paquetes: Avalonia|Famille de paquets : Avalonia|Famiglia di pacchetti: Avalonia|Família de pacotes: Avalonia|パッケージファミリー: Avalonia|패키지 제품군: Avalonia|软件包系列：Avalonia|套件系列：Avalonia
        About.Package.Avalonia.Description|Plattformübergreifendes Benutzeroberflächen-Framework für eine reaktionsschnelle, native Desktop-Erfahrung.|Marco de interfaz de usuario multiplataforma para una experiencia de escritorio nativa y adaptable.|Cadre d’interface utilisateur multiplateforme pour une expérience de bureau native et adaptative.|Framework di interfaccia utente multipiattaforma per un'esperienza desktop nativa e reattiva.|Framework de interface do usuário multiplataforma para uma experiência de desktop nativa e responsiva.|応答性の高いネイティブデスクトップ体験を実現するクロスプラットフォームのユーザーインターフェイスフレームワーク。|반응형 네이티브 데스크톱 환경을 위한 크로스 플랫폼 사용자 인터페이스 프레임워크입니다.|用于响应式原生桌面体验的跨平台用户界面框架。|用於回應式原生桌面體驗的跨平台使用者介面框架。
        About.Package.Avalonia.LicenseName|MIT License|MIT License|MIT License|MIT License|MIT License|MIT License|MIT License|MIT License|MIT License
        About.Package.Avalonia.LicenseAutomation|Avalonia UI-Lizenzvereinbarung|Acuerdo de licencia de Avalonia UI|Contrat de licence Avalonia UI|Accordo di licenza di Avalonia UI|Contrato de licença do Avalonia UI|Avalonia UI のライセンス契約|Avalonia UI 사용권 계약|Avalonia UI 许可协议|Avalonia UI 授權協議
        About.Package.Avalonia.CopyAutomation|Avalonia UI-Lizenzvereinbarung kopieren|Copiar el acuerdo de licencia de Avalonia UI|Copier le contrat de licence Avalonia UI|Copia l'accordo di licenza di Avalonia UI|Copiar o contrato de licença do Avalonia UI|Avalonia UI のライセンス契約をコピー|Avalonia UI 사용권 계약 복사|复制 Avalonia UI 许可协议|複製 Avalonia UI 授權協議
        About.Package.Avalonia.LicenseTextAutomation|Vollständiger Avalonia UI-Lizenztext|Texto completo de la licencia de Avalonia UI|Texte complet de la licence Avalonia UI|Testo completo della licenza di Avalonia UI|Texto completo da licença do Avalonia UI|Avalonia UI の完全なライセンス本文|전체 Avalonia UI 사용권 텍스트|完整的 Avalonia UI 许可文本|完整的 Avalonia UI 授權文字
        About.Package.ImageSharp.Name|ImageSharp|ImageSharp|ImageSharp|ImageSharp|ImageSharp|ImageSharp|ImageSharp|ImageSharp|ImageSharp
        About.Package.ImageSharp.Identity|Paket: SixLabors.ImageSharp|Paquete: SixLabors.ImageSharp|Paquet : SixLabors.ImageSharp|Pacchetto: SixLabors.ImageSharp|Pacote: SixLabors.ImageSharp|パッケージ: SixLabors.ImageSharp|패키지: SixLabors.ImageSharp|软件包：SixLabors.ImageSharp|套件：SixLabors.ImageSharp
        About.Package.ImageSharp.Description|Plattformübergreifende Bildverarbeitungsbibliothek zur Prüfung und Optimierung von Grafiken.|Biblioteca multiplataforma de procesamiento de imágenes para inspeccionar y optimizar las ilustraciones.|Bibliothèque multiplateforme de traitement d’images pour inspecter et optimiser les illustrations.|Libreria multipiattaforma di elaborazione delle immagini per ispezionare e ottimizzare la grafica.|Biblioteca multiplataforma de processamento de imagens para inspecionar e otimizar ilustrações.|アートワークの検査と最適化に使用するクロスプラットフォーム画像処理ライブラリ。|아트워크 검사 및 최적화를 위한 크로스 플랫폼 이미지 처리 라이브러리입니다.|用于检查和优化插图的跨平台图像处理库。|用於檢查和最佳化圖稿的跨平台影像處理程式庫。
        About.Package.ImageSharp.LicenseName|Six Labors Split License, Version 1.0|Six Labors Split License, Version 1.0|Six Labors Split License, Version 1.0|Six Labors Split License, Version 1.0|Six Labors Split License, Version 1.0|Six Labors Split License, Version 1.0|Six Labors Split License, Version 1.0|Six Labors Split License, Version 1.0|Six Labors Split License, Version 1.0
        About.Package.ImageSharp.LicenseAutomation|ImageSharp-Lizenzvereinbarung|Acuerdo de licencia de ImageSharp|Contrat de licence ImageSharp|Accordo di licenza di ImageSharp|Contrato de licença do ImageSharp|ImageSharp のライセンス契約|ImageSharp 사용권 계약|ImageSharp 许可协议|ImageSharp 授權協議
        About.Package.ImageSharp.CopyAutomation|ImageSharp-Lizenzvereinbarung kopieren|Copiar el acuerdo de licencia de ImageSharp|Copier le contrat de licence ImageSharp|Copia l'accordo di licenza di ImageSharp|Copiar o contrato de licença do ImageSharp|ImageSharp のライセンス契約をコピー|ImageSharp 사용권 계약 복사|复制 ImageSharp 许可协议|複製 ImageSharp 授權協議
        About.Package.ImageSharp.LicenseTextAutomation|Vollständiger ImageSharp-Lizenztext|Texto completo de la licencia de ImageSharp|Texte complet de la licence ImageSharp|Testo completo della licenza di ImageSharp|Texto completo da licença do ImageSharp|ImageSharp の完全なライセンス本文|전체 ImageSharp 사용권 텍스트|完整的 ImageSharp 许可文本|完整的 ImageSharp 授權文字
        About.License.Agreement|Lizenzvereinbarung|Acuerdo de licencia|Contrat de licence|Accordo di licenza|Contrato de licença|ライセンス契約|사용권 계약|许可协议|授權協議
        About.License.Copy|Lizenz kopieren|Copiar licencia|Copier la licence|Copia licenza|Copiar licença|ライセンスをコピー|사용권 복사|复制许可|複製授權
        About.Trademarks|Produktnamen und Marken Dritter sind Eigentum ihrer jeweiligen Inhaber.|Los nombres de productos y las marcas de terceros pertenecen a sus respectivos propietarios.|Les noms de produits et marques de tiers appartiennent à leurs propriétaires respectifs.|I nomi dei prodotti e i marchi di terze parti appartengono ai rispettivi proprietari.|Nomes de produtos e marcas de terceiros pertencem aos seus respectivos proprietários.|サードパーティの製品名および商標は、それぞれの所有者に帰属します。|타사 제품 이름 및 상표는 해당 소유자의 자산입니다.|第三方产品名称和商标归其各自所有者所有。|第三方產品名稱與商標均屬其各自所有者。
        """.Replace(
            "\r",
            "",
            StringComparison.Ordinal);

    private static readonly IReadOnlyDictionary<
        string,
        IReadOnlyDictionary<string, string>>
        ExactResourceTranslations =
            ParseExactResourceTranslations();

    private static readonly string TranslationRows =
        """
        # English|de-DE|es-ES|fr-FR|it-IT|pt-BR|ja-JP|ko-KR|zh-CN|zh-TW
        about|Über|Acerca de|À propos|Informazioni|Sobre|このアプリについて|정보|关于|關於
        pending changes|ausstehende Änderungen|cambios pendientes|modifications en attente|modifiche in sospeso|alterações pendentes|保留中の変更|보류 중인 변경 사항|待处理更改|待處理變更
        online metadata|Online-Metadaten|metadatos en línea|métadonnées en ligne|metadati online|metadados online|オンラインメタデータ|온라인 메타데이터|在线元数据|線上中繼資料
        album artist|Albumkünstler|artista del álbum|artiste de l’album|artista album|artista do álbum|アルバムアーティスト|앨범 아티스트|专辑艺术家|專輯演出者
        track total|Titel insgesamt|total de pistas|total des pistes|totale tracce|total de faixas|トラック総数|트랙 합계|音轨总数|曲目總數
        disc total|Discs insgesamt|total de discos|total des disques|totale dischi|total de discos|ディスク総数|디스크 합계|光盘总数|光碟總數
        file operations|Dateivorgänge|operaciones de archivo|opérations sur les fichiers|operazioni sui file|operações de arquivo|ファイル操作|파일 작업|文件操作|檔案操作
        bulk operations|Massenvorgänge|operaciones masivas|opérations groupées|operazioni in blocco|operações em massa|一括操作|일괄 작업|批量操作|批次操作
        external tools|Externe Werkzeuge|herramientas externas|outils externes|strumenti esterni|ferramentas externas|外部ツール|외부 도구|外部工具|外部工具
        display language|Anzeigesprache|idioma de visualización|langue d’affichage|lingua di visualizzazione|idioma de exibição|表示言語|표시 언어|显示语言|顯示語言
        effective policy|Wirksame Richtlinie|directiva efectiva|stratégie effective|criterio effettivo|política efetiva|有効なポリシー|유효 정책|生效策略|生效原則
        review changes|Änderungen prüfen|revisar cambios|examiner les modifications|rivedi modifiche|revisar alterações|変更を確認|변경 사항 검토|审阅更改|檢閱變更
        apply reviewed plan|Geprüften Plan anwenden|aplicar el plan revisado|appliquer le plan examiné|applica piano rivisto|aplicar plano revisado|確認済みプランを適用|검토된 계획 적용|应用已审阅的计划|套用已檢閱的計畫
        import metadata|Metadaten importieren|importar metadatos|importer les métadonnées|importa metadati|importar metadados|メタデータをインポート|메타데이터 가져오기|导入元数据|匯入中繼資料
        save tags|Tags speichern|guardar etiquetas|enregistrer les balises|salva tag|salvar etiquetas|タグを保存|태그 저장|保存标签|儲存標籤
        last write time|Letzte Schreibzeit|hora de última escritura|heure de dernière écriture|ora ultima scrittura|hora da última gravação|最終更新日時|마지막 쓰기 시간|最后写入时间|最後寫入時間
        content width|Inhaltsbreite|ancho del contenido|largeur du contenu|larghezza contenuto|largura do conteúdo|コンテンツ幅|콘텐츠 너비|内容宽度|內容寬度
        selected files|ausgewählte Dateien|archivos seleccionados|fichiers sélectionnés|file selezionati|arquivos selecionados|選択したファイル|선택한 파일|所选文件|選取的檔案
        selected tracks|ausgewählte Titel|pistas seleccionadas|pistes sélectionnées|tracce selezionate|faixas selecionadas|選択したトラック|선택한 트랙|所选音轨|選取的曲目
        selected items|ausgewählte Elemente|elementos seleccionados|éléments sélectionnés|elementi selezionati|itens selecionados|選択した項目|선택한 항목|所选项目|選取的項目
        all fields|Alle Felder|todos los campos|tous les champs|tutti i campi|todos os campos|すべてのフィールド|모든 필드|所有字段|所有欄位
        add files|Dateien hinzufügen|añadir archivos|ajouter des fichiers|aggiungi file|adicionar arquivos|ファイルを追加|파일 추가|添加文件|新增檔案
        add folder|Ordner hinzufügen|añadir carpeta|ajouter un dossier|aggiungi cartella|adicionar pasta|フォルダーを追加|폴더 추가|添加文件夹|新增資料夾
        no changes|Keine Änderungen|sin cambios|aucune modification|nessuna modifica|nenhuma alteração|変更なし|변경 사항 없음|无更改|沒有變更
        no files|Keine Dateien|sin archivos|aucun fichier|nessun file|nenhum arquivo|ファイルなし|파일 없음|无文件|沒有檔案
        no results|Keine Ergebnisse|sin resultados|aucun résultat|nessun risultato|nenhum resultado|結果なし|결과 없음|无结果|沒有結果
        not available|Nicht verfügbar|no disponible|non disponible|non disponibile|não disponível|利用できません|사용할 수 없음|不可用|無法使用
        in progress|In Bearbeitung|en curso|en cours|in corso|em andamento|進行中|진행 중|进行中|進行中
        read only|Schreibgeschützt|solo lectura|lecture seule|sola lettura|somente leitura|読み取り専用|읽기 전용|只读|唯讀
        click outside|außerhalb klicken|hacer clic fuera|cliquer à l’extérieur|fare clic all’esterno|clicar fora|外側をクリック|바깥쪽 클릭|单击外部|按一下外部
        focus restoration|Fokuswiederherstellung|restauración del foco|restauration du focus|ripristino del focus|restauração do foco|フォーカス復元|포커스 복원|焦点恢复|焦點還原
        primary action|Primäre Aktion|acción principal|action principale|azione principale|ação principal|主要アクション|기본 작업|主要操作|主要動作
        one column|Eine Spalte|una columna|une colonne|una colonna|uma coluna|1列|한 열|一列|一欄
        music library|Musikbibliothek|biblioteca musical|bibliothèque musicale|libreria musicale|biblioteca de música|音楽ライブラリ|음악 라이브러리|音乐库|音樂資料庫
        iTunes library|iTunes-Mediathek|biblioteca de iTunes|bibliothèque iTunes|libreria iTunes|biblioteca do iTunes|iTunesライブラリ|iTunes 보관함|iTunes资料库|iTunes資料庫
        exact match|Exakte Übereinstimmung|coincidencia exacta|correspondance exacte|corrispondenza esatta|correspondência exata|完全一致|정확히 일치|完全匹配|完全符合
        file-to-track|Datei-zu-Titel|archivo a pista|fichier vers piste|file-traccia|arquivo para faixa|ファイルからトラック|파일-트랙|文件到音轨|檔案到曲目
        apply|anwenden|aplicar|appliquer|applica|aplicar|適用|적용|应用|套用
        applied|angewendet|aplicado|appliqué|applicato|aplicado|適用済み|적용됨|已应用|已套用
        applying|wird angewendet|aplicando|application|applicazione|aplicando|適用中|적용 중|正在应用|正在套用
        approve|genehmigen|aprobar|approuver|approva|aprovar|承認|승인|批准|核准
        approved|genehmigt|aprobado|approuvé|approvato|aprovado|承認済み|승인됨|已批准|已核准
        add|hinzufügen|añadir|ajouter|aggiungi|adicionar|追加|추가|添加|新增
        added|hinzugefügt|añadido|ajouté|aggiunto|adicionado|追加済み|추가됨|已添加|已新增
        remove|entfernen|quitar|supprimer|rimuovi|remover|削除|제거|移除|移除
        removed|entfernt|quitado|supprimé|rimosso|removido|削除済み|제거됨|已移除|已移除
        delete|löschen|eliminar|supprimer|elimina|excluir|削除|삭제|删除|刪除
        deleted|gelöscht|eliminado|supprimé|eliminato|excluído|削除済み|삭제됨|已删除|已刪除
        restore|wiederherstellen|restaurar|restaurer|ripristina|restaurar|復元|복원|还原|還原
        restored|wiederhergestellt|restaurado|restauré|ripristinato|restaurado|復元済み|복원됨|已还原|已還原
        replace|ersetzen|reemplazar|remplacer|sostituisci|substituir|置換|바꾸기|替换|取代
        replaced|ersetzt|reemplazado|remplacé|sostituito|substituído|置換済み|바뀜|已替换|已取代
        preserve|beibehalten|conservar|conserver|mantieni|preservar|保持|유지|保留|保留
        preserved|beibehalten|conservado|conservé|mantenuto|preservado|保持済み|유지됨|已保留|已保留
        copy|kopieren|copiar|copier|copia|copiar|コピー|복사|复制|複製
        copied|kopiert|copiado|copié|copiato|copiado|コピー済み|복사됨|已复制|已複製
        move|verschieben|mover|déplacer|sposta|mover|移動|이동|移动|移動
        moved|verschoben|movido|déplacé|spostato|movido|移動済み|이동됨|已移动|已移動
        clear|leeren|borrar|effacer|cancella|limpar|クリア|지우기|清除|清除
        cleared|geleert|borrado|effacé|cancellato|limpo|クリア済み|지워짐|已清除|已清除
        close|schließen|cerrar|fermer|chiudi|fechar|閉じる|닫기|关闭|關閉
        open|öffnen|abrir|ouvrir|apri|abrir|開く|열기|打开|開啟
        browse|durchsuchen|examinar|parcourir|sfoglia|procurar|参照|찾아보기|浏览|瀏覽
        choose|auswählen|elegir|choisir|scegli|escolher|選択|선택|选择|選擇
        select|auswählen|seleccionar|sélectionner|seleziona|selecionar|選択|선택|选择|選取
        selected|ausgewählt|seleccionado|sélectionné|selezionato|selecionado|選択済み|선택됨|已选择|已選取
        search|suchen|buscar|rechercher|cerca|pesquisar|検索|검색|搜索|搜尋
        scan|scannen|analizar|analyser|scansiona|examinar|スキャン|스캔|扫描|掃描
        scanning|wird gescannt|analizando|analyse|scansione|examinando|スキャン中|스캔 중|正在扫描|正在掃描
        index|indexieren|indexar|indexer|indicizza|indexar|インデックス|인덱스|索引|索引
        indexing|wird indexiert|indexando|indexation|indicizzazione|indexando|インデックス作成中|인덱싱 중|正在索引|正在建立索引
        refresh|aktualisieren|actualizar|actualiser|aggiorna|atualizar|更新|새로 고침|刷新|重新整理
        load|laden|cargar|charger|carica|carregar|読み込む|불러오기|加载|載入
        loaded|geladen|cargado|chargé|caricato|carregado|読み込み済み|불러옴|已加载|已載入
        loading|wird geladen|cargando|chargement|caricamento|carregando|読み込み中|불러오는 중|正在加载|正在載入
        save|speichern|guardar|enregistrer|salva|salvar|保存|저장|保存|儲存
        saved|gespeichert|guardado|enregistré|salvato|salvo|保存済み|저장됨|已保存|已儲存
        edit|bearbeiten|editar|modifier|modifica|editar|編集|편집|编辑|編輯
        editing|Bearbeitung|edición|modification|modifica|edição|編集中|편집 중|正在编辑|正在編輯
        preview|Vorschau|vista previa|aperçu|anteprima|visualização|プレビュー|미리 보기|预览|預覽
        previewed|in Vorschau|previsualizado|prévisualisé|in anteprima|visualizado|プレビュー済み|미리 봄|已预览|已預覽
        previewing|Vorschau läuft|previsualizando|prévisualisation|anteprima in corso|visualizando|プレビュー中|미리 보는 중|正在预览|正在預覽
        review|prüfen|revisar|examiner|rivedi|revisar|確認|검토|审阅|檢閱
        reviewed|geprüft|revisado|examiné|rivisto|revisado|確認済み|검토됨|已审阅|已檢閱
        revert|zurücksetzen|revertir|annuler|ripristina|reverter|元に戻す|되돌리기|还原|還原
        undo|rückgängig|deshacer|annuler|annulla|desfazer|元に戻す|실행 취소|撤销|復原
        redo|wiederholen|rehacer|rétablir|ripeti|refazer|やり直す|다시 실행|重做|取消復原
        cancel|abbrechen|cancelar|annuler|annulla|cancelar|キャンセル|취소|取消|取消
        cancelled|abgebrochen|cancelado|annulé|annullato|cancelado|キャンセル済み|취소됨|已取消|已取消
        discard|verwerfen|descartar|ignorer|scarta|descartar|破棄|버리기|放弃|捨棄
        repeat|wiederholen|repetir|répéter|ripeti|repetir|繰り返す|반복|重复|重複
        continue|fortfahren|continuar|continuer|continua|continuar|続行|계속|继续|繼續
        retry|erneut versuchen|reintentar|réessayer|riprova|tentar novamente|再試行|다시 시도|重试|重試
        run|ausführen|ejecutar|exécuter|esegui|executar|実行|실행|运行|執行
        running|wird ausgeführt|ejecutando|exécution|esecuzione|executando|実行中|실행 중|正在运行|正在執行
        stop|anhalten|detener|arrêter|arresta|parar|停止|중지|停止|停止
        start|starten|iniciar|démarrer|avvia|iniciar|開始|시작|开始|開始
        create|erstellen|crear|créer|crea|criar|作成|만들기|创建|建立
        created|erstellt|creado|créé|creato|criado|作成済み|만들어짐|已创建|已建立
        build|erstellen|compilar|générer|compila|compilar|ビルド|빌드|构建|建置
        validate|überprüfen|validar|valider|convalida|validar|検証|검증|验证|驗證
        repair|reparieren|reparar|réparer|ripara|reparar|修復|복구|修复|修復
        repairs|Reparaturen|reparaciones|réparations|riparazioni|reparos|修復|복구 작업|修复|修復
        recover|wiederherstellen|recuperar|récupérer|recupera|recuperar|復旧|복구|恢复|復原
        recovery|Wiederherstellung|recuperación|récupération|ripristino|recuperação|復旧|복구|恢复|復原
        quarantine|Quarantäne|cuarentena|quarantaine|quarantena|quarentena|隔離|격리|隔离|隔離
        quarantined|in Quarantäne|en cuarentena|en quarantaine|in quarantena|em quarentena|隔離済み|격리됨|已隔离|已隔離
        purge|bereinigen|purgar|purger|elimina definitivamente|expurgar|完全削除|완전 삭제|清除|清除
        synchronize|synchronisieren|sincronizar|synchroniser|sincronizza|sincronizar|同期|동기화|同步|同步
        synchronization|Synchronisierung|sincronización|synchronisation|sincronizzazione|sincronização|同期|동기화|同步|同步
        import|importieren|importar|importer|importa|importar|インポート|가져오기|导入|匯入
        export|exportieren|exportar|exporter|esporta|exportar|エクスポート|내보내기|导出|匯出
        write|schreiben|escribir|écrire|scrivi|gravar|書き込む|쓰기|写入|寫入
        written|geschrieben|escrito|écrit|scritto|gravado|書き込み済み|기록됨|已写入|已寫入
        read|lesen|leer|lire|leggi|ler|読み取る|읽기|读取|讀取
        failed|fehlgeschlagen|fallido|échec|non riuscito|falhou|失敗|실패|失败|失敗
        failure|Fehler|error|échec|errore|falha|失敗|실패|失败|失敗
        error|Fehler|error|erreur|errore|erro|エラー|오류|错误|錯誤
        warning|Warnung|advertencia|avertissement|avviso|aviso|警告|경고|警告|警告
        information|Information|información|information|informazione|informação|情報|정보|信息|資訊
        status|Status|estado|état|stato|status|状態|상태|状态|狀態
        progress|Fortschritt|progreso|progression|avanzamento|progresso|進行状況|진행률|进度|進度
        completed|abgeschlossen|completado|terminé|completato|concluído|完了|완료됨|已完成|已完成
        complete|vollständig|completo|terminé|completo|concluído|完了|완료|完成|完成
        ready|bereit|listo|prêt|pronto|pronto|準備完了|준비됨|就绪|就緒
        active|aktiv|activo|actif|attivo|ativo|有効|활성|活动|作用中
        inactive|inaktiv|inactivo|inactif|inattivo|inativo|無効|비활성|非活动|非作用中
        enabled|aktiviert|habilitado|activé|abilitato|ativado|有効|사용|已启用|已啟用
        disabled|deaktiviert|deshabilitado|désactivé|disabilitato|desativado|無効|사용 안 함|已禁用|已停用
        available|verfügbar|disponible|disponible|disponibile|disponível|利用可能|사용 가능|可用|可用
        unavailable|nicht verfügbar|no disponible|indisponible|non disponibile|indisponível|利用不可|사용 불가|不可用|無法使用
        missing|fehlend|ausente|manquant|mancante|ausente|不足|누락|缺失|遺失
        invalid|ungültig|no válido|non valide|non valido|inválido|無効|잘못됨|无效|無效
        valid|gültig|válido|valide|valido|válido|有効|유효|有效|有效
        changed|geändert|cambiado|modifié|modificato|alterado|変更済み|변경됨|已更改|已變更
        unchanged|unverändert|sin cambios|inchangé|invariato|inalterado|変更なし|변경 안 됨|未更改|未變更
        unsaved|nicht gespeichert|sin guardar|non enregistré|non salvato|não salvo|未保存|저장 안 됨|未保存|未儲存
        pending|ausstehend|pendiente|en attente|in sospeso|pendente|保留中|보류 중|待处理|待處理
        planned|geplant|planificado|planifié|pianificato|planejado|計画済み|계획됨|已计划|已規劃
        proposed|vorgeschlagen|propuesto|proposé|proposto|proposto|提案済み|제안됨|建议|建議
        current|aktuell|actual|actuel|corrente|atual|現在|현재|当前|目前
        previous|vorherig|anterior|précédent|precedente|anterior|前|이전|上一个|上一個
        next|weiter|siguiente|suivant|avanti|próximo|次へ|다음|下一步|下一步
        first|erste|primero|premier|primo|primeiro|最初|첫 번째|第一个|第一個
        last|letzte|último|dernier|ultimo|último|最後|마지막|最后|最後
        before|vorher|antes|avant|prima|antes|変更前|이전|之前|之前
        after|nachher|después|après|dopo|depois|変更後|이후|之后|之後
        new|neu|nuevo|nouveau|nuovo|novo|新規|새로 만들기|新建|新增
        old|alt|antiguo|ancien|vecchio|antigo|旧|이전|旧|舊
        original|Original|original|original|originale|original|元|원본|原始|原始
        destination|Ziel|destino|destination|destinazione|destino|宛先|대상|目标|目的地
        source|Quelle|origen|source|origine|origem|ソース|원본|源|來源
        target|Ziel|destino|cible|destinazione|destino|対象|대상|目标|目標
        root|Stammordner|raíz|racine|radice|raiz|ルート|루트|根|根目錄
        folder|Ordner|carpeta|dossier|cartella|pasta|フォルダー|폴더|文件夹|資料夾
        directory|Verzeichnis|directorio|répertoire|directory|diretório|ディレクトリ|디렉터리|目录|目錄
        path|Pfad|ruta|chemin|percorso|caminho|パス|경로|路径|路徑
        location|Speicherort|ubicación|emplacement|posizione|local|場所|위치|位置|位置
        file|Datei|archivo|fichier|file|arquivo|ファイル|파일|文件|檔案
        files|Dateien|archivos|fichiers|file|arquivos|ファイル|파일|文件|檔案
        item|Element|elemento|élément|elemento|item|項目|항목|项目|項目
        items|Elemente|elementos|éléments|elementi|itens|項目|항목|项目|項目
        row|Zeile|fila|ligne|riga|linha|行|행|行|列
        rows|Zeilen|filas|lignes|righe|linhas|行|행|行|列
        column|Spalte|columna|colonne|colonna|coluna|列|열|列|欄
        columns|Spalten|columnas|colonnes|colonne|colunas|列|열|列|欄
        field|Feld|campo|champ|campo|campo|フィールド|필드|字段|欄位
        fields|Felder|campos|champs|campi|campos|フィールド|필드|字段|欄位
        value|Wert|valor|valeur|valore|valor|値|값|值|值
        values|Werte|valores|valeurs|valori|valores|値|값|值|值
        metadata|Metadaten|metadatos|métadonnées|metadati|metadados|メタデータ|메타데이터|元数据|中繼資料
        tag|Tag|etiqueta|balise|tag|etiqueta|タグ|태그|标签|標籤
        tags|Tags|etiquetas|balises|tag|etiquetas|タグ|태그|标签|標籤
        layer|Ebene|capa|couche|livello|camada|レイヤー|레이어|层|圖層
        layers|Ebenen|capas|couches|livelli|camadas|レイヤー|레이어|层|圖層
        artwork|Cover|carátula|illustration|copertina|capa|アートワーク|아트워크|封面|封面
        image|Bild|imagen|image|immagine|imagem|画像|이미지|图像|影像
        images|Bilder|imágenes|images|immagini|imagens|画像|이미지|图像|影像
        thumbnail|Miniaturansicht|miniatura|miniature|miniatura|miniatura|サムネイル|미리 보기 이미지|缩略图|縮圖
        cover|Cover|carátula|pochette|copertina|capa|カバー|표지|封面|封面
        audio|Audio|audio|audio|audio|áudio|オーディオ|오디오|音频|音訊
        bitrate|Bitrate|tasa de bits|débit binaire|bitrate|taxa de bits|ビットレート|비트 전송률|比特率|位元率
        sample rate|Abtastrate|frecuencia de muestreo|fréquence d’échantillonnage|frequenza di campionamento|taxa de amostragem|サンプルレート|샘플링 속도|采样率|取樣率
        codec|Codec|códec|codec|codec|codec|コーデック|코덱|编解码器|編解碼器
        encoding|Codierung|codificación|encodage|codifica|codificação|エンコーディング|인코딩|编码|編碼
        title|Titel|título|titre|titolo|título|タイトル|제목|标题|標題
        artist|Künstler|artista|artiste|artista|artista|アーティスト|아티스트|艺术家|演出者
        album|Album|álbum|album|album|álbum|アルバム|앨범|专辑|專輯
        track|Titel|pista|piste|traccia|faixa|トラック|트랙|音轨|曲目
        tracks|Titel|pistas|pistes|tracce|faixas|トラック|트랙|音轨|曲目
        disc|Disc|disco|disque|disco|disco|ディスク|디스크|光盘|光碟
        genre|Genre|género|genre|genere|gênero|ジャンル|장르|流派|曲風
        composer|Komponist|compositor|compositeur|compositore|compositor|作曲者|작곡가|作曲家|作曲者
        comment|Kommentar|comentario|commentaire|commento|comentário|コメント|설명|备注|註解
        date|Datum|fecha|date|data|data|日付|날짜|日期|日期
        year|Jahr|año|année|anno|ano|年|연도|年份|年份
        number|Nummer|número|numéro|numero|número|番号|번호|编号|編號
        name|Name|nombre|nom|nome|nome|名前|이름|名称|名稱
        label|Bezeichnung|etiqueta|libellé|etichetta|rótulo|ラベル|레이블|标签|標籤
        labels|Bezeichnungen|etiquetas|libellés|etichette|rótulos|ラベル|레이블|标签|標籤
        country|Land|país|pays|paese|país|国|국가|国家|國家
        release|Veröffentlichung|lanzamiento|parution|pubblicazione|lançamento|リリース|릴리스|发行版|發行版
        edition|Ausgabe|edición|édition|edizione|edição|エディション|에디션|版本|版本
        duration|Dauer|duración|durée|durata|duração|長さ|재생 시간|时长|長度
        size|Größe|tamaño|taille|dimensione|tamanho|サイズ|크기|大小|大小
        bytes|Bytes|bytes|octets|byte|bytes|バイト|바이트|字节|位元組
        format|Format|formato|format|formato|formato|形式|형식|格式|格式
        formats|Formate|formatos|formats|formati|formatos|形式|형식|格式|格式
        type|Typ|tipo|type|tipo|tipo|種類|유형|类型|類型
        mode|Modus|modo|mode|modalità|modo|モード|모드|模式|模式
        policy|Richtlinie|directiva|stratégie|criterio|política|ポリシー|정책|策略|原則
        configuration|Konfiguration|configuración|configuration|configurazione|configuração|構成|구성|配置|設定
        settings|Einstellungen|configuración|paramètres|impostazioni|configurações|設定|설정|设置|設定
        preference|Einstellung|preferencia|préférence|preferenza|preferência|設定|기본 설정|首选项|偏好設定
        option|Option|opción|option|opzione|opção|オプション|옵션|选项|選項
        options|Optionen|opciones|options|opzioni|opções|オプション|옵션|选项|選項
        profile|Profil|perfil|profil|profilo|perfil|プロファイル|프로필|配置文件|設定檔
        recipe|Rezept|receta|recette|ricetta|receita|レシピ|레시피|方案|配方
        plan|Plan|plan|plan|piano|plano|プラン|계획|计划|計畫
        action|Aktion|acción|action|azione|ação|アクション|작업|操作|動作
        actions|Aktionen|acciones|actions|azioni|ações|アクション|작업|操作|動作
        operation|Vorgang|operación|opération|operazione|operação|操作|작업|操作|操作
        operations|Vorgänge|operaciones|opérations|operazioni|operações|操作|작업|操作|操作
        tool|Werkzeug|herramienta|outil|strumento|ferramenta|ツール|도구|工具|工具
        tools|Werkzeuge|herramientas|outils|strumenti|ferramentas|ツール|도구|工具|工具
        shortcut|Tastenkürzel|atajo|raccourci|scorciatoia|atalho|ショートカット|바로 가기|快捷键|快速鍵
        report|Bericht|informe|rapport|rapporto|relatório|レポート|보고서|报告|報告
        reports|Berichte|informes|rapports|rapporti|relatórios|レポート|보고서|报告|報告
        playlist|Wiedergabeliste|lista de reproducción|liste de lecture|playlist|playlist|プレイリスト|재생 목록|播放列表|播放清單
        playlists|Wiedergabelisten|listas de reproducción|listes de lecture|playlist|playlists|プレイリスト|재생 목록|播放列表|播放清單
        session|Sitzung|sesión|session|sessione|sessão|セッション|세션|会话|工作階段
        inspector|Inspektor|inspector|inspecteur|ispettore|inspetor|インスペクター|검사기|检查器|檢查器
        workbench navigation|Arbeitsbereichsnavigation|navegación del área de trabajo|navigation de l’atelier|navigazione dell'area di lavoro|navegação da área de trabalho|ワークベンチのナビゲーション|워크벤치 탐색|工作台导航|工作台導覽
        workbench|Arbeitsbereich|área de trabajo|atelier|area di lavoro|área de trabalho|ワークベンチ|작업대|工作台|工作台
        library|Bibliothek|biblioteca|bibliothèque|libreria|biblioteca|ライブラリ|라이브러리|库|資料庫
        health|Integrität|estado|intégrité|integrità|integridade|健全性|상태|健康状况|健康狀況
        audit|Prüfung|auditoría|audit|verifica|auditoria|監査|감사|审核|稽核
        ingest|Aufnahme|ingesta|ingestion|acquisizione|ingestão|取り込み|수집|引入|擷取
        output|Ausgabe|salida|sortie|output|saída|出力|출력|输出|輸出
        input|Eingabe|entrada|entrée|input|entrada|入力|입력|输入|輸入
        mapping|Zuordnung|asignación|mappage|mappatura|mapeamento|マッピング|매핑|映射|對應
        mappings|Zuordnungen|asignaciones|mappages|mappature|mapeamentos|マッピング|매핑|映射|對應
        match|Übereinstimmung|coincidencia|correspondance|corrispondenza|correspondência|一致|일치|匹配|符合
        matched|zugeordnet|coincidente|correspondant|corrispondente|correspondente|一致|일치함|已匹配|已符合
        result|Ergebnis|resultado|résultat|risultato|resultado|結果|결과|结果|結果
        results|Ergebnisse|resultados|résultats|risultati|resultados|結果|결과|结果|結果
        details|Details|detalles|détails|dettagli|detalhes|詳細|세부 정보|详细信息|詳細資料
        history|Verlauf|historial|historique|cronologia|histórico|履歴|기록|历史记录|歷程記錄
        journal|Journal|diario|journal|registro|diário|ジャーナル|저널|日志|日誌
        cache|Cache|caché|cache|cache|cache|キャッシュ|캐시|缓存|快取
        cached|zwischengespeichert|en caché|mis en cache|memorizzato nella cache|em cache|キャッシュ済み|캐시됨|已缓存|已快取
        catalog|Katalog|catálogo|catalogue|catalogo|catálogo|カタログ|카탈로그|目录|目錄
        database|Datenbank|base de datos|base de données|database|banco de dados|データベース|데이터베이스|数据库|資料庫
        device|Gerät|dispositivo|appareil|dispositivo|dispositivo|デバイス|기기|设备|裝置
        provider|Anbieter|proveedor|fournisseur|fornitore|provedor|プロバイダー|공급자|提供商|提供者
        scope|Bereich|ámbito|portée|ambito|escopo|範囲|범위|范围|範圍
        filter|Filter|filtro|filtre|filtro|filtro|フィルター|필터|筛选器|篩選器
        condition|Bedingung|condición|condition|condizione|condição|条件|조건|条件|條件
        group|Gruppe|grupo|groupe|gruppo|grupo|グループ|그룹|组|群組
        custom|Benutzerdefiniert|personalizado|personnalisé|personalizzato|personalizado|カスタム|사용자 지정|自定义|自訂
        advanced|Erweitert|avanzado|avancé|avanzate|avançado|詳細設定|고급|高级|進階
        more|Mehr|más|plus|altro|mais|その他|더 보기|更多|更多
        less|Weniger|menos|moins|meno|menos|少なく|간단히|更少|較少
        information|Information|información|informations|informazioni|informações|情報|정보|信息|資訊
        summary|Zusammenfassung|resumen|résumé|riepilogo|resumo|概要|요약|摘要|摘要
        message|Meldung|mensaje|message|messaggio|mensagem|メッセージ|메시지|消息|訊息
        reason|Grund|motivo|raison|motivo|motivo|理由|이유|原因|原因
        issue|Problem|problema|problème|problema|problema|問題|문제|问题|問題
        issues|Probleme|problemas|problèmes|problemi|problemas|問題|문제|问题|問題
        finding|Befund|hallazgo|constat|riscontro|constatação|検出項目|발견 사항|发现项|發現項目
        findings|Befunde|hallazgos|constats|riscontri|constatações|検出項目|발견 사항|发现项|發現項目
        blocker|Blockierung|bloqueo|blocage|blocco|bloqueio|ブロッカー|차단 요소|阻止项|封鎖項目
        threshold|Schwellenwert|umbral|seuil|soglia|limite|しきい値|임계값|阈值|臨界值
        maximum|Maximum|máximo|maximum|massimo|máximo|最大|최대|最大值|最大值
        minimum|Minimum|mínimo|minimum|minimo|mínimo|最小|최소|最小值|最小值
        total|Gesamt|total|total|totale|total|合計|합계|总计|總計
        count|Anzahl|cantidad|nombre|conteggio|contagem|件数|개수|计数|計數
        every|jede|cada|chaque|ogni|cada|すべての|모든|每个|每個
        all|alle|todos|tous|tutti|todos|すべて|모두|全部|全部
        none|keine|ninguno|aucun|nessuno|nenhum|なし|없음|无|無
        one|eine|uno|un|uno|um|1件|하나|一个|一個
        only|nur|solo|uniquement|solo|somente|のみ|전용|仅|僅
        each|jeweils|cada|chaque|ciascuno|cada|各|각|每个|每個
        per|pro|por|par|per|por|あたり|당|每|每
        recent|zuletzt verwendet|reciente|récent|recente|recente|最近|최근|最近|最近
        local|lokal|local|local|locale|local|ローカル|로컬|本地|本機
        external|extern|externo|externe|esterno|externo|外部|외부|外部|外部
        embedded|eingebettet|incrustado|intégré|incorporato|incorporado|埋め込み|포함됨|嵌入式|內嵌
        canonical|kanonisch|canónico|canonique|canonico|canônico|正規|정규|规范|標準
        normal|normal|normal|normal|normale|normal|標準|일반|正常|一般
        similar|ähnlich|similar|similaire|simile|semelhante|類似|유사|相似|相似
        duplicate|Duplikat|duplicado|doublon|duplicato|duplicado|重複|중복|重复|重複
        empty|leer|vacío|vide|vuoto|vazio|空|비어 있음|空|空白
        known|bekannt|conocido|connu|noto|conhecido|既知|알려짐|已知|已知
        unknown|unbekannt|desconocido|inconnu|sconosciuto|desconhecido|不明|알 수 없음|未知|未知
        directly|direkt|directamente|directement|direttamente|diretamente|直接|직접|直接|直接
        offline|offline|sin conexión|hors ligne|offline|offline|オフライン|오프라인|离线|離線
        recursive|rekursiv|recursivo|récursif|ricorsivo|recursivo|再帰的|재귀|递归|遞迴
        text|Text|texto|texte|testo|texto|テキスト|텍스트|文本|文字
        representation|Darstellung|representación|représentation|rappresentazione|representação|表現|표현|表示|表示
        disposition|Disposition|disposición|disposition|disposizione|disposição|処理|처리|处置|處置
        executable|Programmdatei|ejecutable|exécutable|eseguibile|executável|実行ファイル|실행 파일|可执行文件|可執行檔
        argument|Argument|argumento|argument|argomento|argumento|引数|인수|参数|引數
        arguments|Argumente|argumentos|arguments|argomenti|argumentos|引数|인수|参数|引數
        command|Befehl|comando|commande|comando|comando|コマンド|명령|命令|命令
        token|Token|token|jeton|token|token|トークン|토큰|标记|權杖
        reference|Verweis|referencia|référence|riferimento|referência|参照|참조|引用|參照
        references|Verweise|referencias|références|riferimenti|referências|参照|참조|引用|參照
        mode|Modus|modo|mode|modalità|modo|モード|모드|模式|模式
        theme|Design|tema|thème|tema|tema|テーマ|테마|主题|佈景主題
        light|Hell|claro|clair|chiaro|claro|ライト|밝게|浅色|淺色
        dark|Dunkel|oscuro|sombre|scuro|escuro|ダーク|어둡게|深色|深色
        automatic|Automatisch|automático|automatique|automatico|automático|自動|자동|自动|自動
        system|System|sistema|système|sistema|sistema|システム|시스템|系统|系統
        language|Sprache|idioma|langue|lingua|idioma|言語|언어|语言|語言
        accessibility|Barrierefreiheit|accesibilidad|accessibilité|accessibilità|acessibilidade|アクセシビリティ|접근성|辅助功能|協助工具
        dialog|Dialog|diálogo|boîte de dialogue|finestra|caixa de diálogo|ダイアログ|대화 상자|对话框|對話方塊
        menu|Menü|menú|menu|menu|menu|メニュー|메뉴|菜单|功能表
        drawer|Seitenleiste|panel lateral|volet|pannello laterale|painel lateral|ドロワー|서랍|抽屉|抽屜
        header|Kopfzeile|encabezado|en-tête|intestazione|cabeçalho|ヘッダー|머리글|标题栏|標頭
        button|Schaltfläche|botón|bouton|pulsante|botão|ボタン|단추|按钮|按鈕
        tab|Registerkarte|pestaña|onglet|scheda|guia|タブ|탭|选项卡|索引標籤
        section|Abschnitt|sección|section|sezione|seção|セクション|섹션|部分|區段
        navigation|Navigation|navegación|navigation|navigazione|navegação|ナビゲーション|탐색|导航|導覽
        page|Seite|página|page|pagina|página|ページ|페이지|页面|頁面
        window|Fenster|ventana|fenêtre|finestra|janela|ウィンドウ|창|窗口|視窗
        resize|Größe ändern|cambiar tamaño|redimensionner|ridimensiona|redimensionar|サイズ変更|크기 조정|调整大小|調整大小
        compact|Kompakt|compacto|compact|compatto|compacto|コンパクト|간단히|紧凑|精簡
        docked|Angedockt|acoplado|ancré|ancorato|encaixado|ドッキング|고정됨|停靠|停駐
        overlay|Überlagerung|superposición|superposition|sovrapposizione|sobreposição|オーバーレイ|오버레이|浮层|浮動層
        visible|sichtbar|visible|visible|visibile|visível|表示|표시|可见|可見
        hidden|ausgeblendet|oculto|masqué|nascosto|oculto|非表示|숨김|隐藏|隱藏
        width|Breite|ancho|largeur|larghezza|largura|幅|너비|宽度|寬度
        height|Höhe|alto|hauteur|altezza|altura|高さ|높이|高度|高度
        pixels|Pixel|píxeles|pixels|pixel|pixels|ピクセル|픽셀|像素|像素
        pane|Bereich|panel|volet|riquadro|painel|ペイン|창|窗格|窗格
        panes|Bereiche|paneles|volets|riquadri|painéis|ペイン|창|窗格|窗格
        sort|sortieren|ordenar|trier|ordina|ordenar|並べ替え|정렬|排序|排序
        order|Reihenfolge|orden|ordre|ordine|ordem|順序|순서|顺序|順序
        reorder|neu anordnen|reordenar|réorganiser|riordina|reordenar|並べ替え|순서 바꾸기|重新排序|重新排序
        visibility|Sichtbarkeit|visibilidad|visibilité|visibilità|visibilidade|表示|표시 여부|可见性|可見性
        selection|Auswahl|selección|sélection|selezione|seleção|選択|선택|选择|選取
        focus|Fokus|foco|focus|focus|foco|フォーカス|포커스|焦点|焦點
        keyboard|Tastatur|teclado|clavier|tastiera|teclado|キーボード|키보드|键盘|鍵盤
        mouse|Maus|ratón|souris|mouse|mouse|マウス|마우스|鼠标|滑鼠
        accessibility name|Barrierefreiheitsname|nombre de accesibilidad|nom d’accessibilité|nome accessibile|nome de acessibilidade|アクセシビリティ名|접근성 이름|辅助功能名称|協助工具名稱
        tooltip|QuickInfo|información sobre herramientas|info-bulle|descrizione comando|dica de ferramenta|ツールヒント|도구 설명|工具提示|工具提示
        automation|Automatisierung|automatización|automatisation|automazione|automação|自動化|자동화|自动化|自動化
        shortcut|Tastenkürzel|atajo|raccourci|scorciatoia|atalho|ショートカット|바로 가기|快捷键|快速鍵
        percent|Prozent|porcentaje|pourcentage|percentuale|porcentagem|パーセント|퍼센트|百分比|百分比
        space|Speicherplatz|espacio|espace|spazio|espaço|領域|공간|空间|空間
        storage|Speicher|almacenamiento|stockage|archiviazione|armazenamento|ストレージ|저장소|存储|儲存空間
        retained|beibehalten|retenido|conservé|conservato|retido|保持済み|유지됨|已保留|已保留
        savings|Einsparung|ahorro|économie|risparmio|economia|節約|절감|节省|節省
        compact recovery|Kompakte Wiederherstellung|recuperación compacta|récupération compacte|ripristino compatto|recuperação compacta|コンパクト復旧|압축 복구|紧凑恢复|精簡復原
        full recovery|Vollständige Wiederherstellung|recuperación completa|récupération complète|ripristino completo|recuperação completa|完全復旧|전체 복구|完整恢复|完整復原
        full-file recovery|Vollständige Dateiwiederherstellung|recuperación del archivo completo|récupération du fichier complet|ripristino del file completo|recuperação do arquivo completo|ファイル全体の復旧|전체 파일 복구|完整文件恢复|完整檔案復原
        payload|Nutzdaten|carga útil|charge utile|payload|carga útil|ペイロード|페이로드|有效负载|承載資料
        hash|Hash|hash|hachage|hash|hash|ハッシュ|해시|哈希|雜湊
        checksum|Prüfsumme|suma de comprobación|somme de contrôle|checksum|soma de verificação|チェックサム|체크섬|校验和|總和檢查碼
        stale|veraltet|obsoleto|obsolète|obsoleto|desatualizado|古い|오래됨|已过期|已過期
        modified|geändert|modificado|modifié|modificato|modificado|変更済み|수정됨|已修改|已修改
        externally|extern|externamente|en externe|esternamente|externamente|外部で|외부에서|外部|外部
        collision|Konflikt|conflicto|collision|conflitto|conflito|競合|충돌|冲突|衝突
        capacity|Kapazität|capacidad|capacité|capacità|capacidade|容量|용량|容量|容量
        required|erforderlich|obligatorio|requis|obbligatorio|obrigatório|必須|필수|必需|必要
        optional|optional|opcional|facultatif|facoltativo|opcional|任意|선택 사항|可选|選用
        recommended|empfohlen|recomendado|recommandé|consigliato|recomendado|推奨|권장|推荐|建議
        default|Standard|predeterminado|par défaut|predefinito|padrão|既定|기본값|默认|預設
        custom|Benutzerdefiniert|personalizado|personnalisé|personalizzato|personalizado|カスタム|사용자 지정|自定义|自訂
        yes|Ja|sí|oui|sì|sim|はい|예|是|是
        no|Nein|no|non|no|não|いいえ|아니요|否|否
        ok|OK|Aceptar|OK|OK|OK|OK|확인|确定|確定
        and|und|y|et|e|e|および|및|和|與
        or|oder|o|ou|o|ou|または|또는|或|或
        with|mit|con|avec|con|com|付き|포함|带|含
        without|ohne|sin|sans|senza|sem|なし|없이|不带|不含
        from|von|de|depuis|da|de|から|에서|从|從
        to|zu|a|vers|a|para|へ|로|到|至
        for|für|para|pour|per|para|用|용|用于|用於
        of|von|de|de|di|de|の|의|的|的
        in|in|en|dans|in|em|内|에서|在|在
        on|auf|en|sur|su|em|上|에서|在|在
        at|bei|en|à|a|em|で|에서|在|於
        by|durch|por|par|da|por|による|기준|按|依
        as|als|como|comme|come|como|として|으로|作为|作為
        into|in|en|dans|in|em|へ|로|到|至
        under|unter|bajo|sous|sotto|sob|下|아래|下|下
        over|über|sobre|sur|sopra|sobre|上|위|上|上
        between|zwischen|entre|entre|tra|entre|間|사이|之间|之間
        before|vor|antes de|avant|prima di|antes de|前|전에|之前|之前
        after|nach|después de|après|dopo|depois de|後|후에|之后|之後
        when|wenn|cuando|lorsque|quando|quando|場合|때|当|當
        while|während|mientras|pendant|mentre|enquanto|間|동안|期间|期間
        if|wenn|si|si|se|se|場合|경우|如果|如果
        then|dann|entonces|puis|quindi|então|次に|그런 다음|然后|然後
        this|dies|este|ce|questo|este|この|이|此|此
        that|das|ese|ce|quello|esse|その|해당|该|該
        these|diese|estos|ces|questi|estes|これら|이러한|这些|這些
        those|jene|esos|ceux|quelli|esses|それら|해당|那些|那些
        a|ein|un|un|un|um|1つの|하나의|一个|一個
        an|ein|un|un|un|um|1つの|하나의|一个|一個
        the|die|el|le|il|o|対象の|해당|该|該
        is|ist|es|est|è|é|です|입니다|是|是
        are|sind|son|sont|sono|são|です|입니다|是|是
        was|war|era|était|era|era|でした|이었음|曾是|曾是
        were|waren|eran|étaient|erano|eram|でした|이었음|曾是|曾是
        be|sein|ser|être|essere|ser|する|됨|为|為
        has|hat|tiene|a|ha|tem|あります|있음|有|有
        have|haben|tener|avoir|avere|ter|あります|있음|有|有
        can|kann|puede|peut|può|pode|できます|가능|可以|可以
        cannot|kann nicht|no puede|ne peut pas|non può|não pode|できません|할 수 없음|无法|無法
        could|konnte|podría|pourrait|potrebbe|poderia|可能でした|가능|可以|可以
        will|wird|se|sera|verrà|será|します|예정|将|將
        must|muss|debe|doit|deve|deve|必要です|해야 함|必须|必須
        may|kann|puede|peut|può|pode|可能です|가능|可能|可能
        use|verwenden|usar|utiliser|usa|usar|使用|사용|使用|使用
        uses|verwendet|usa|utilise|usa|usa|使用します|사용함|使用|使用
        keep|beibehalten|conservar|conserver|mantieni|manter|保持|유지|保留|保留
        leave|belassen|dejar|laisser|lascia|deixar|残す|유지|保留|保留
        make|erstellen|hacer|créer|crea|criar|作成|만들기|创建|建立
        find|finden|buscar|trouver|trova|localizar|検索|찾기|查找|尋找
        found|gefunden|encontrado|trouvé|trovato|encontrado|検出|찾음|已找到|已找到
        update|aktualisieren|actualizar|mettre à jour|aggiorna|atualizar|更新|업데이트|更新|更新
        updated|aktualisiert|actualizado|mis à jour|aggiornato|atualizado|更新済み|업데이트됨|已更新|已更新
        change|Änderung|cambio|modification|modifica|alteração|変更|변경|更改|變更
        changes|Änderungen|cambios|modifications|modifiche|alterações|変更|변경 사항|更改|變更
        set|festlegen|establecer|définir|imposta|definir|設定|설정|设置|設定
        mark|markieren|marcar|marquer|contrassegna|marcar|マーク|표시|标记|標記
        show|anzeigen|mostrar|afficher|mostra|mostrar|表示|표시|显示|顯示
        hide|ausblenden|ocultar|masquer|nascondi|ocultar|非表示|숨기기|隐藏|隱藏
        include|einschließen|incluir|inclure|includi|incluir|含める|포함|包括|包含
        includes|enthält|incluye|inclut|include|inclui|含む|포함함|包括|包含
        exclude|ausschließen|excluir|exclure|escludi|excluir|除外|제외|排除|排除
        remain|verbleiben|permanecer|rester|rimanere|permanecer|残る|유지|保留|保留
        retained|beibehalten|retenido|conservé|conservato|retido|保持済み|유지됨|已保留|已保留
        send|senden|enviar|envoyer|invia|enviar|送信|보내기|发送|傳送
        allow|zulassen|permitir|autoriser|consenti|permitir|許可|허용|允许|允許
        require|erfordern|requerir|exiger|richiedi|exigir|必須|필요|需要|需要
        required|erforderlich|requerido|requis|richiesto|obrigatório|必須|필수|必需|必要
        press|drücken|pulsar|appuyer|premi|pressionar|押す|누르기|按|按下
        click|klicken|hacer clic|cliquer|fai clic|clicar|クリック|클릭|单击|按一下
        drag|ziehen|arrastrar|faire glisser|trascina|arrastar|ドラッグ|끌기|拖动|拖曳
        drop|ablegen|soltar|déposer|rilascia|soltar|ドロップ|놓기|放置|放置
        resize|Größe ändern|cambiar tamaño|redimensionner|ridimensiona|redimensionar|サイズ変更|크기 조정|调整大小|調整大小
        inspect|prüfen|inspeccionar|inspecter|ispeziona|inspecionar|検査|검사|检查|檢查
        optimize|optimieren|optimizar|optimiser|ottimizza|otimizar|最適化|최적화|优化|最佳化
        normalize|normalisieren|normalizar|normaliser|normalizza|normalizar|正規化|정규화|规范化|正規化
        convert|konvertieren|convertir|convertir|converti|converter|変換|변환|转换|轉換
        conversion|Konvertierung|conversión|conversion|conversione|conversão|変換|변환|转换|轉換
        organization|Organisation|organización|organisation|organizzazione|organização|整理|구성|组织|組織
        naming|Benennung|nomenclatura|nommage|denominazione|nomenclatura|命名|이름 지정|命名|命名
        pattern|Muster|patrón|motif|modello|padrão|パターン|패턴|模式|模式
        template|Vorlage|plantilla|modèle|modello|modelo|テンプレート|템플릿|模板|範本
        separator|Trennzeichen|separador|séparateur|separatore|separador|区切り文字|구분 기호|分隔符|分隔符號
        prefix|Präfix|prefijo|préfixe|prefisso|prefixo|接頭辞|접두사|前缀|前置詞
        suffix|Suffix|sufijo|suffixe|suffisso|sufixo|接尾辞|접미사|后缀|後置詞
        pattern|Muster|patrón|motif|modello|padrão|パターン|패턴|模式|模式
        resolution|Auflösung|resolución|résolution|risoluzione|resolução|解像度|해상도|分辨率|解析度
        quality|Qualität|calidad|qualité|qualità|qualidade|品質|품질|质量|品質
        compression|Komprimierung|compresión|compression|compressione|compactação|圧縮|압축|压缩|壓縮
        color|Farbe|color|couleur|colore|cor|色|색상|颜色|色彩
        front|Vorderseite|frontal|recto|fronte|frente|前面|앞면|正面|正面
        back|Rückseite|posterior|verso|retro|verso|背面|뒷면|背面|背面
        description|Beschreibung|descripción|description|descrizione|descrição|説明|설명|说明|說明
        help|Hilfe|ayuda|aide|aiuto|ajuda|ヘルプ|도움말|帮助|說明
        learn more|Mehr erfahren|más información|en savoir plus|ulteriori informazioni|saiba mais|詳細情報|자세히 알아보기|了解更多|深入瞭解
        dismiss|schließen|descartar|ignorer|chiudi|dispensar|閉じる|닫기|关闭|關閉
        refresh|aktualisieren|actualizar|actualiser|aggiorna|atualizar|更新|새로 고침|刷新|重新整理
        reset|zurücksetzen|restablecer|réinitialiser|reimposta|redefinir|リセット|재설정|重置|重設
        browse|durchsuchen|examinar|parcourir|sfoglia|procurar|参照|찾아보기|浏览|瀏覽
        unknown|unbekannt|desconocido|inconnu|sconosciuto|desconhecido|不明|알 수 없음|未知|未知
        missing|fehlend|falta|manquant|mancante|ausente|不足|누락|缺失|遺失
        runs|Läufe|ejecuciones|exécutions|esecuzioni|execuções|実行|실행|运行|執行
        other|Sonstige|otro|autre|altro|outro|その他|기타|其他|其他
        recording|Aufnahme|grabación|enregistrement|registrazione|gravação|録音|녹음|录音|錄音
        punctuation|Interpunktion|puntuación|ponctuation|punteggiatura|pontuação|句読点|문장 부호|标点|標點
        letter|Buchstabe|letra|lettre|lettera|letra|文字|문자|字母|字母
        legacy|Veraltet|heredado|hérité|legacy|legado|従来|레거시|旧版|舊版
        styles|Stile|estilos|styles|stili|estilos|スタイル|스타일|风格|樣式
        releases|Veröffentlichungen|lanzamientos|parutions|pubblicazioni|lançamentos|リリース|릴리스|发行版|發行版
        expression|Ausdruck|expresión|expression|espressione|expressão|式|식|表达式|運算式
        regular|Regulär|regular|régulier|regolare|regular|正規|정규|正则|規則
        technical|Technisch|técnico|technique|tecnico|técnico|技術|기술|技术|技術
        sequence|Sequenz|secuencia|séquence|sequenza|sequência|連番|순서|序列|序列
        genres|Genres|géneros|genres|generi|gêneros|ジャンル|장르|流派|曲風
        searching|Suche läuft|buscando|recherche|ricerca|pesquisando|検索中|검색 중|正在搜索|正在搜尋
        media|Medien|medios|média|supporti|mídia|メディア|미디어|媒体|媒體
        symbol|Symbol|símbolo|symbole|simbolo|símbolo|記号|기호|符号|符號
        cleanup|Bereinigung|limpieza|nettoyage|pulizia|limpeza|クリーンアップ|정리|清理|清理
        initialize|Initialisieren|inicializar|initialiser|inizializza|inicializar|初期化|초기화|初始化|初始化
        byte|Byte|byte|octet|byte|byte|バイト|바이트|字节|位元組
        barcode|Strichcode|código de barras|code-barres|codice a barre|código de barras|バーコード|바코드|条形码|條碼
        outputs|Ausgaben|salidas|sorties|output|saídas|出力|출력|输出|輸出
        than|als|que|que|di|que|より|보다|比|比
        albums|Alben|álbumes|albums|album|álbuns|アルバム|앨범|专辑|專輯
        present|vorhanden|presente|présent|presente|presente|存在|있음|存在|存在
        conflicts|Konflikte|conflictos|conflits|conflitti|conflitos|競合|충돌|冲突|衝突
        contains|enthält|contiene|contient|contiene|contém|含む|포함|包含|包含
        relative|Relativ|relativo|relatif|relativo|relativo|相対|상대|相对|相對
        devices|Geräte|dispositivos|appareils|dispositivi|dispositivos|デバイス|기기|设备|裝置
        hi-res|hochauflösend|alta resolución|haute résolution|alta risoluzione|alta resolução|ハイレゾ|고해상도|高解析度|高解析度
        descending|Absteigend|descendente|décroissant|decrescente|decrescente|降順|내림차순|降序|遞減
        preparing|Vorbereitung|preparando|préparation|preparazione|preparando|準備中|준비 중|正在准备|正在準備
        matches|Übereinstimmungen|coincidencias|correspondances|corrispondenze|correspondências|一致|일치 항목|匹配项|符合項目
        personal|Persönlich|personal|personnel|personale|pessoal|個人|개인|个人|個人
        padding|Auffüllung|relleno|remplissage|riempimento|preenchimento|パディング|채움|填充|填補
        key|Schlüssel|clave|clé|chiave|chave|キー|키|键|索引鍵
        example|Beispiel|ejemplo|exemple|esempio|exemplo|例|예|示例|範例
        working|In Arbeit|trabajando|traitement|elaborazione|processando|処理中|작업 중|正在处理|正在處理
        lyricist|Textdichter|letrista|parolier|paroliere|letrista|作詞者|작사가|作词者|作詞者
        grid|Raster|cuadrícula|grille|griglia|grade|グリッド|그리드|网格|格線
        shortcuts|Tastenkürzel|atajos|raccourcis|scorciatoie|atalhos|ショートカット|바로 가기|快捷键|快速鍵
        mixer|Mischer|mezclador|mixeur|mixer|mixer|ミキサー|믹서|混音器|混音器
        errors|Fehler|errores|erreurs|errori|erros|エラー|오류|错误|錯誤
        reviewing|Prüfung|revisando|examen|revisione|revisando|確認中|검토 중|正在审阅|正在檢閱
        purging|Bereinigung|purgando|purge|eliminazione definitiva|expurgando|完全削除中|완전 삭제 중|正在清除|正在清除
        band|Band|banda|groupe|gruppo|banda|バンド|밴드|乐队|樂團
        application|Anwendung|aplicación|application|applicazione|aplicativo|アプリケーション|응용 프로그램|应用程序|應用程式
        older|Älter|anterior|plus ancien|precedente|mais antigo|古い|이전|较旧|較舊
        podcast|Podcast|pódcast|podcast|podcast|podcast|ポッドキャスト|팟캐스트|播客|播客
        work|Werk|obra|œuvre|opera|obra|作品|작품|作品|作品
        during|während|durante|pendant|durante|durante|中|중|期间|期間
        starting|Startet|iniciando|démarrage|avvio|iniciando|開始中|시작 중|正在开始|正在開始
        logo|Logo|logotipo|logo|logo|logotipo|ロゴ|로고|徽标|標誌
        absolute|Absolut|absoluto|absolu|assoluto|absoluto|絶対|절대|绝对|絕對
        interrupted|Unterbrochen|interrumpido|interrompu|interrotto|interrompido|中断|중단됨|已中断|已中斷
        equals|Gleich|igual a|égal à|uguale a|igual a|等しい|같음|等于|等於
        song|Song|canción|morceau|brano|música|曲|곡|歌曲|歌曲
        compatibility|Kompatibilität|compatibilidad|compatibilité|compatibilità|compatibilidade|互換性|호환성|兼容性|相容性
        sidecar|Begleitdatei|archivo asociado|fichier annexe|file associato|arquivo auxiliar|サイドカー|사이드카|伴随文件|附屬檔案
        transcode|Transcodieren|transcodificar|transcoder|transcodifica|transcodificar|トランスコード|트랜스코딩|转码|轉碼
        remux|Remuxen|remultiplexar|remultiplexer|rimultiplex|remultiplexar|再多重化|리먹스|重新封装|重新封裝
        conductor|Dirigent|director|chef d’orchestre|direttore|regente|指揮者|지휘자|指挥|指揮
        endian|Byte-Reihenfolge|orden de bytes|boutisme|ordine byte|ordem de bytes|エンディアン|엔디언|字节序|位元組順序
        transform|Umwandlung|transformación|transformation|trasformazione|transformação|変換|변환|转换|轉換
        paths|Pfade|rutas|chemins|percorsi|caminhos|パス|경로|路径|路徑
        reading|Lesen|leyendo|lecture|lettura|lendo|読み取り中|읽는 중|正在读取|正在讀取
        propose|Vorschlagen|proponer|proposer|proponi|propor|提案|제안|建议|建議
        calculating|Berechnung|calculando|calcul|calcolo|calculando|計算中|계산 중|正在计算|正在計算
        mixed|Gemischt|mixto|mixte|misto|misto|混在|혼합|混合|混合
        little|Little-Endian|little-endian|petit-boutiste|little-endian|little-endian|リトルエンディアン|리틀 엔디언|小端|小端序
        numeric|Numerisch|numérico|numérique|numerico|numérico|数値|숫자|数字|數值
        titles|Titel|títulos|titres|titoli|títulos|タイトル|제목|标题|標題
        confidence|Konfidenz|confianza|confiance|confidenza|confiança|信頼度|신뢰도|置信度|信賴度
        draft|Entwurf|borrador|brouillon|bozza|rascunho|下書き|초안|草稿|草稿
        numbering|Nummerierung|numeración|numérotation|numerazione|numeração|番号付け|번호 매기기|编号|編號
        duplicates|Duplikate|duplicados|doublons|duplicati|duplicados|重複|중복 항목|重复项|重複項目
        conflict|Konflikt|conflicto|conflit|conflitto|conflito|競合|충돌|冲突|衝突
        representative|Repräsentativ|representativo|représentatif|rappresentativo|representativo|代表|대표|代表|代表
        level|Ebene|nivel|niveau|livello|nível|レベル|수준|级别|層級
        parent|Übergeordnet|principal|parent|superiore|pai|親|상위|父级|上層
        modifier|Modifikator|modificador|modificateur|modificatore|modificador|修飾子|수정자|修饰键|修飾鍵
        case|Groß-/Kleinschreibung|mayúsculas y minúsculas|casse|maiuscole/minuscole|maiúsculas/minúsculas|大文字/小文字|대/소문자|大小写|大小寫
        editions|Ausgaben|ediciones|éditions|edizioni|edições|エディション|에디션|版本|版本
        types|Typen|tipos|types|tipi|tipos|種類|유형|类型|類型
        organize|Organisieren|organizar|organiser|organizza|organizar|整理|정리|整理|整理
        quote|Anführungszeichen|comilla|guillemet|virgolette|aspas|引用符|따옴표|引号|引號
        position|Position|posición|position|posizione|posição|位置|위치|位置|位置
        folders|Ordner|carpetas|dossiers|cartelle|pastas|フォルダー|폴더|文件夹|資料夾
        sec|Sek.|s|s|sec|s|秒|초|秒|秒
        grouping|Gruppierung|agrupación|groupement|raggruppamento|agrupamento|グループ化|그룹화|分组|分組
        fingerprint|Fingerabdruck|huella|empreinte|impronta|impressão digital|フィンガープリント|지문|指纹|指紋
        any|Beliebig|cualquiera|tout|qualsiasi|qualquer|任意|모두|任意|任意
        job|Auftrag|trabajo|tâche|processo|tarefa|ジョブ|작업|作业|工作
        tolerance|Toleranz|tolerancia|tolérance|tolleranza|tolerância|許容範囲|허용 오차|容差|容許誤差
        archive|Archiv|archivo|archive|archivio|arquivo|アーカイブ|보관 파일|存档|封存
        generate|Erzeugen|generar|générer|genera|gerar|生成|생성|生成|產生
        version|Version|versión|version|versione|versão|バージョン|버전|版本|版本
        adopt|Übernehmen|adoptar|adopter|adotta|adotar|採用|채택|采用|採用
        artists|Künstler|artistas|artistes|artisti|artistas|アーティスト|아티스트|艺术家|演出者
        restoring|Wiederherstellung|restaurando|restauration|ripristino|restaurando|復元中|복원 중|正在还原|正在還原
        subtitle|Untertitel|subtítulo|sous-titre|sottotitolo|subtítulo|サブタイトル|부제|副标题|副標題
        assign|Zuweisen|asignar|attribuer|assegna|atribuir|割り当て|할당|分配|指派
        ascending|Aufsteigend|ascendente|croissant|crescente|crescente|昇順|오름차순|升序|遞增
        upper|Großbuchstaben|mayúsculas|majuscule|maiuscolo|maiúsculas|大文字|대문자|大写|大寫
        already|bereits|ya|déjà|già|já|すでに|이미|已经|已經
        moves|Verschiebungen|movimientos|déplacements|spostamenti|movimentações|移動|이동|移动|移動
        connected|Verbunden|conectado|connecté|connesso|conectado|接続済み|연결됨|已连接|已連線
        sentence|Satz|frase|phrase|frase|frase|文|문장|句子|句子
        manual|Manuell|manual|manuel|manuale|manual|手動|수동|手动|手動
        latin|Lateinisch|latino|latin|latino|latino|ラテン|라틴|拉丁|拉丁
        correct|Korrekt|correcto|correct|corretto|correto|正しい|올바름|正确|正確
        hyphen|Bindestrich|guion|trait d’union|trattino|hífen|ハイフン|하이픈|连字符|連字號
        non-breaking|Geschützt|inseparable|insécable|non separabile|não separável|改行なし|줄 바꿈 없음|不间断|不換行
        unauthorized|Nicht autorisiert|no autorizado|non autorisé|non autorizzato|não autorizado|未承認|권한 없음|未授权|未授權
        lower|Kleinbuchstaben|minúsculas|minuscule|minuscolo|minúsculas|小文字|소문자|小写|小寫
        exact|Exakt|exacto|exact|esatto|exato|完全|정확|精确|精確
        studio|Studio|estudio|studio|studio|estúdio|スタジオ|스튜디오|工作室|錄音室
        illustration|Illustration|ilustración|illustration|illustrazione|ilustração|イラスト|일러스트레이션|插图|插圖
        comments|Kommentare|comentarios|commentaires|commenti|comentários|コメント|설명|注释|註解
        unmatched|Nicht zugeordnet|sin coincidencia|sans correspondance|senza corrispondenza|sem correspondência|未一致|일치하지 않음|未匹配|未符合
        capture|Aufnahme|captura|capture|acquisizione|captura|キャプチャ|캡처|捕获|擷取
        screen|Bildschirm|pantalla|écran|schermo|tela|画面|화면|屏幕|螢幕
        video|Video|vídeo|vidéo|video|vídeo|動画|비디오|视频|影片
        fish|Fisch|pez|poisson|pesce|peixe|魚|물고기|鱼|魚
        colored|farbig|coloreado|coloré|colorato|colorido|色付き|색상|彩色|彩色
        bright|hell|brillante|vif|vivace|brilhante|鮮やか|밝은|鲜艳|鮮豔
        step|Schritt|paso|étape|passaggio|etapa|ステップ|단계|步骤|步驟
        ignored|Ignoriert|ignorado|ignoré|ignorato|ignorado|無視|무시됨|已忽略|已忽略
        absent|Abwesend|ausente|absent|assente|ausente|なし|없음|不存在|不存在
        reverse|Umgekehrt|inverso|inverse|inverso|inverso|逆|역방향|反向|反向
        inconsistencies|Inkonsistenzen|inconsistencias|incohérences|incoerenze|inconsistências|不整合|불일치|不一致|不一致
        wildcard|Platzhalter|comodín|joker|carattere jolly|curinga|ワイルドカード|와일드카드|通配符|萬用字元
        candidate|Kandidat|candidato|candidat|candidato|candidato|候補|후보|候选项|候選項目
        ambiguous|Mehrdeutig|ambiguo|ambigu|ambiguo|ambíguo|あいまい|모호함|不明确|不明確
        sources|Quellen|orígenes|sources|origini|fontes|ソース|원본|源|來源
        again|erneut|de nuevo|à nouveau|di nuovo|novamente|再度|다시|再次|再次
        try|Versuchen|intentar|essayer|prova|tentar|試す|시도|尝试|嘗試
        supported|Unterstützt|compatible|pris en charge|supportato|compatível|対応|지원됨|支持|支援
        discover|Erkennen|detectar|découvrir|rileva|descobrir|検出|검색|发现|探索
        workspace|Arbeitsbereich|espacio de trabajo|espace de travail|area di lavoro|espaço de trabalho|ワークスペース|작업 영역|工作区|工作區
        extended|Erweitert|extendido|étendu|esteso|estendido|拡張|확장|扩展|延伸
        gesture|Tastenkürzel|gesto|geste|gesto|gesto|ジェスチャー|제스처|手势|手勢
        artworks|Cover|carátulas|illustrations|copertine|capas|アートワーク|아트워크|封面|封面
        info|Info|información|info|informazioni|informações|情報|정보|信息|資訊
        natural|Natürlich|natural|naturel|naturale|natural|自然|자연|自然|自然
        currency|Währung|moneda|devise|valuta|moeda|通貨|통화|货币|貨幣
        kind|Art|tipo|type|tipo|tipo|種類|종류|种类|種類
        enrich|Anreichern|enriquecer|enrichir|arricchisci|enriquecer|補完|보강|丰富|豐富
        automate|Automatisieren|automatizar|automatiser|automatizza|automatizar|自動化|자동화|自动化|自動化
        control|Steuerung|control|contrôle|controllo|controle|制御|제어|控制|控制
        warnings|Warnungen|advertencias|avertissements|avvisi|avisos|警告|경고|警告|警告
        card|Karte|tarjeta|carte|scheda|cartão|カード|카드|卡片|卡片
        car|Auto|coche|voiture|auto|carro|車|차량|汽车|汽車
        validation|Validierung|validación|validation|convalida|validação|検証|검증|验证|驗證
        within|innerhalb|dentro de|dans|entro|dentro de|以内|이내|范围内|範圍內
        protected|Geschützt|protegido|protégé|protetto|protegido|保護|보호됨|受保护|受保護
        retention|Aufbewahrung|retención|conservation|conservazione|retenção|保持|보존|保留|保留
        connector|Connector|conector|connecteur|connettore|conector|コネクタ|커넥터|连接器|連接器
        string|Zeichenfolge|cadena|chaîne|stringa|cadeia|文字列|문자열|字符串|字串
        days|Tage|días|jours|giorni|dias|日|일|天|天
        day|Tag|día|jour|giorno|dia|日|일|天|天
        user|Benutzer|usuario|utilisateur|utente|usuário|ユーザー|사용자|用户|使用者
        passed|Bestanden|superado|réussi|superato|aprovado|合格|통과|已通过|已通過
        check|Prüfung|comprobación|vérification|controllo|verificação|チェック|검사|检查|檢查
        destinations|Ziele|destinos|destinations|destinazioni|destinos|宛先|대상|目标|目的地
        surrogate|Ersatzzeichen|sustituto|substitut|surrogato|substituto|サロゲート|서로게이트|代理项|代理項
        math|Mathematik|matemáticas|mathématiques|matematica|matemática|数学|수학|数学|數學
        assigned|Zugewiesen|asignado|attribué|assegnato|atribuído|割り当て済み|할당됨|已分配|已指派
        greater|Größer|mayor|supérieur|maggiore|maior|より大きい|보다 큼|大于|大於
        processing|Verarbeitung|procesamiento|traitement|elaborazione|processamento|処理中|처리 중|正在处理|正在處理
        non-music|Nicht-Musik|no musical|non musical|non musicale|não musical|音楽以外|비음악|非音乐|非音樂
        staged|Bereitgestellt|preparado|préparé|preparato|preparado|ステージ済み|준비됨|已暂存|已暫存
        staging|Bereitstellung|preparación|préparation|preparazione|preparação|ステージング|준비|暂存|暫存
        titlecase|Titelschreibweise|mayúscula inicial|casse de titre|iniziali maiuscole|iniciais maiúsculas|タイトルケース|제목 대/소문자|标题大小写|標題大小寫
        uppercase|Großbuchstaben|mayúsculas|majuscules|maiuscole|maiúsculas|大文字|대문자|大写|大寫
        lowercase|Kleinbuchstaben|minúsculas|minuscules|minuscole|minúsculas|小文字|소문자|小写|小寫
        final|Abschließend|final|final|finale|final|最終|최종|最终|最終
        ignore|Ignorieren|ignorar|ignorer|ignora|ignorar|無視|무시|忽略|忽略
        initial|Anfänglich|inicial|initial|iniziale|inicial|初期|초기|初始|初始
        platform|Plattform|plataforma|plateforme|piattaforma|plataforma|プラットフォーム|플랫폼|平台|平台
        dash|Gedankenstrich|raya|tiret|trattino|travessão|ダッシュ|대시|破折号|破折號
        assignment|Zuweisung|asignación|attribution|assegnazione|atribuição|割り当て|할당|分配|指派
        rename|Umbenennen|cambiar nombre|renommer|rinomina|renomear|名前変更|이름 바꾸기|重命名|重新命名
        planning|Planung|planificación|planification|pianificazione|planejamento|計画中|계획 중|正在规划|正在規劃
        recipes|Rezepte|recetas|recettes|ricette|receitas|レシピ|레시피|方案|配方
        append|Anhängen|anexar|ajouter|aggiungi|acrescentar|追加|추가|追加|附加
        performance|Darbietung|interpretación|interprétation|esecuzione|apresentação|演奏|공연|演出|表演
        setup|Einrichtung|configuración|configuration|configurazione|configuração|セットアップ|설정|设置|設定
        guided|Geführt|guiado|guidé|guidato|guiado|ガイド付き|안내|引导式|引導式
        stable|Stabil|estable|stable|stabile|estável|安定|안정|稳定|穩定
        shared|Gemeinsam|compartido|partagé|condiviso|compartilhado|共有|공유|共享|共用
        container|Container|contenedor|conteneur|contenitore|contêiner|コンテナー|컨테이너|容器|容器
        cross-sync|Quersynchronisierung|sincronización cruzada|synchronisation croisée|sincronizzazione incrociata|sincronização cruzada|クロス同期|교차 동기화|交叉同步|交叉同步
        offset|Versatz|desplazamiento|décalage|scostamento|deslocamento|オフセット|오프셋|偏移|位移
        indexed|Indexiert|indexado|indexé|indicizzato|indexado|インデックス済み|인덱싱됨|已索引|已建立索引
        cancelling|Wird abgebrochen|cancelando|annulation|annullamento|cancelando|キャンセル中|취소 중|正在取消|正在取消
        policies|Richtlinien|directivas|stratégies|criteri|políticas|ポリシー|정책|策略|原則
        yet|noch|todavía|encore|ancora|ainda|まだ|아직|尚未|尚未
        finished|Beendet|finalizado|terminé|terminato|finalizado|完了|완료됨|已完成|已完成
        activity|Aktivität|actividad|activité|attività|atividade|アクティビティ|활동|活动|活動
        finalizing|Abschluss|finalizando|finalisation|finalizzazione|finalizando|最終処理中|마무리 중|正在完成|正在完成
        roles|Rollen|roles|rôles|ruoli|funções|役割|역할|角色|角色
        setting|Einstellung|ajuste|paramètre|impostazione|configuração|設定|설정|设置|設定
        machine|Computer|equipo|machine|computer|máquina|コンピューター|컴퓨터|计算机|電腦
        component|Komponente|componente|composant|componente|componente|コンポーネント|구성 요소|组件|元件
        unicode|Unicode|Unicode|Unicode|Unicode|Unicode|Unicode|Unicode|Unicode|Unicode
        limit|Grenzwert|límite|limite|limite|limite|制限|제한|限制|限制
        endings|Endungen|finales|fins|terminazioni|finais|終端|줄 끝|结尾|結尾
        line|Zeile|línea|ligne|riga|linha|行|줄|行|行
        filename|Dateiname|nombre de archivo|nom de fichier|nome file|nome do arquivo|ファイル名|파일 이름|文件名|檔案名稱
        reconciliation|Abgleich|conciliación|rapprochement|riconciliazione|reconciliação|照合|조정|协调|協調
        client|Client|cliente|client|client|cliente|クライアント|클라이언트|客户端|用戶端
        mtime|Änderungszeit|hora de modificación|heure de modification|ora modifica|hora de modificação|変更時刻|수정 시간|修改时间|修改時間
        globs|Glob-Muster|patrones glob|motifs glob|modelli glob|padrões glob|グロブパターン|glob 패턴|通配模式|萬用模式
        exclusion|Ausschluss|exclusión|exclusion|esclusione|exclusão|除外|제외|排除|排除
        danger|Gefahr|peligro|danger|pericolo|perigo|危険|위험|危险|危險
        needing|benötigen|que necesitan|nécessitant|che richiedono|que precisam|必要|필요한|需要|需要
        roots|Stammordner|raíces|racines|radici|raízes|ルート|루트|根|根目錄
        success|Erfolg|éxito|succès|successo|sucesso|成功|성공|成功|成功
        elapsed|Vergangen|transcurrido|écoulé|trascorso|decorrido|経過|경과|已用时间|經過時間
        channels|Kanäle|canales|canaux|canali|canais|チャンネル|채널|声道|聲道
        bits|Bits|bits|bits|bit|bits|ビット|비트|位|位元
        state|Status|estado|état|stato|estado|状態|상태|状态|狀態
        discovering|Erkennung|detectando|découverte|rilevamento|descobrindo|検出中|검색 중|正在发现|正在探索
        jobs|Aufträge|trabajos|tâches|processi|tarefas|ジョブ|작업|作业|工作
        rebalance|Neu verteilen|reequilibrar|rééquilibrer|ribilancia|reequilibrar|再調整|재조정|重新平衡|重新平衡
        maintenance|Wartung|mantenimiento|maintenance|manutenzione|manutenção|メンテナンス|유지 관리|维护|維護
        fix|Beheben|corregir|corriger|correggi|corrigir|修正|수정|修复|修正
        general|Allgemein|general|général|generale|geral|全般|일반|常规|一般
        appearance|Darstellung|apariencia|apparence|aspetto|aparência|外観|모양|外观|外觀
        quit|Beenden|salir|quitter|esci|sair|終了|종료|退出|結束
        managed|Verwaltet|administrado|géré|gestito|gerenciado|管理済み|관리됨|已管理|已管理
        clearly|Übersichtlich|claramente|clairement|chiaramente|claramente|明確に|명확하게|清晰地|清楚地
        your|Ihre|su|votre|la tua|sua|あなたの|사용자의|您的|您的
        live|Live|en vivo|en direct|in tempo reale|ao vivo|ライブ|실시간|实时|即時
        home|Startseite|inicio|accueil|home|início|ホーム|홈|主页|首頁
        reindex|Neu indexieren|reindexar|réindexer|reindicizza|reindexar|再インデックス|다시 인덱싱|重新索引|重新建立索引
        reveal|Anzeigen|mostrar|révéler|mostra|revelar|表示|표시|显示|顯示
        normalization|Normalisierung|normalización|normalisation|normalizzazione|normalização|正規化|정규화|规范化|正規化
        sanitize|Bereinigen|sanear|assainir|sanifica|higienizar|サニタイズ|정리|净化|清理
        stereo|Stereo|estéreo|stéréo|stereo|estéreo|ステレオ|스테레오|立体声|立體聲
        multichannel|Mehrkanal|multicanal|multicanal|multicanale|multicanal|マルチチャンネル|다중 채널|多声道|多聲道
        copyright|Urheberrecht|derechos de autor|droits d’auteur|copyright|direitos autorais|著作権|저작권|版权|著作權
        arranger|Arrangeur|arreglista|arrangeur|arrangiatore|arranjador|編曲者|편곡가|编曲者|編曲者
        engineer|Tontechniker|ingeniero|ingénieur du son|tecnico del suono|engenheiro|エンジニア|엔지니어|工程师|工程師
        remixer|Remixer|remezclador|remixeur|remixer|remixer|リミキサー|리믹서|混音师|混音師
        rating|Bewertung|valoración|évaluation|valutazione|avaliação|評価|평가|评分|評分
        producer|Produzent|productor|producteur|produttore|produtor|プロデューサー|프로듀서|制作人|製作人
        script|Skript|guion|script|script|script|スクリプト|스크립트|脚本|指令碼
        reload|Neu laden|recargar|recharger|ricarica|recarregar|再読み込み|다시 불러오기|重新加载|重新載入
        healthy|Fehlerfrei|saludable|sain|integro|íntegro|正常|정상|正常|正常
        need|benötigen|necesitan|nécessitent|richiedono|precisam|必要|필요|需要|需要
        attention|Aufmerksamkeit|atención|attention|attenzione|atenção|注意|주의|注意|注意
        lossy|Verlustbehaftet|con pérdida|avec perte|con perdita|com perdas|非可逆|손실|有损|有損
        recorded|Erfasst|registrado|enregistré|registrato|registrado|記録済み|기록됨|已记录|已記錄
        accepted|Akzeptiert|aceptado|accepté|accettato|aceito|受け入れ済み|허용됨|已接受|已接受
        extensions|Erweiterungen|extensiones|extensions|estensioni|extensões|拡張子|확장자|扩展名|副檔名
        affected|Betroffen|afectado|affecté|interessato|afetado|影響あり|영향받음|受影响|受影響
        matrix|Matrix|matriz|matrice|matrice|matriz|マトリックス|행렬|矩阵|矩陣
        representations|Darstellungen|representaciones|représentations|rappresentazioni|representações|表現|표현|表示|表示
        initialized|Initialisiert|inicializado|initialisé|inizializzato|inicializado|初期化済み|초기화됨|已初始化|已初始化
        candidates|Kandidaten|candidatos|candidats|candidati|candidatos|候補|후보|候选项|候選項目
        identifiers|Kennungen|identificadores|identifiants|identificatori|identificadores|識別子|식별자|标识符|識別碼
        auto-detect|Automatisch erkennen|detectar automáticamente|détecter automatiquement|rileva automaticamente|detectar automaticamente|自動検出|자동 감지|自动检测|自動偵測
        checking|Prüfung|comprobando|vérification|controllo|verificando|確認中|확인 중|正在检查|正在檢查
        comparing|Vergleich|comparando|comparaison|confronto|comparando|比較中|비교 중|正在比较|正在比較
        compilation|Kompilation|recopilación|compilation|compilation|compilação|コンピレーション|컴필레이션|合辑|合輯
        token|Token|token|jeton|token|token|トークン|토큰|令牌|權杖
        decoded|Dekodiert|decodificado|décodé|decodificato|decodificado|デコード済み|디코딩됨|已解码|已解碼
        verification|Überprüfung|verificación|vérification|verifica|verificação|検証|확인|验证|驗證
        decoding|Dekodierung|decodificando|décodage|decodifica|decodificando|デコード中|디코딩 중|正在解码|正在解碼
        strategy|Strategie|estrategia|stratégie|strategia|estratégia|方式|전략|策略|策略
        access|Zugriff|acceso|accès|accesso|acesso|アクセス|액세스|访问|存取
        stored|Gespeichert|almacenado|stocké|archiviato|armazenado|保存済み|저장됨|已存储|已儲存
        discs|Discs|discos|disques|dischi|discos|ディスク|디스크|光盘|光碟
        disposition|Disposition|disposición|disposition|disposizione|disposição|処理|처리|处置|處置
        existing|Vorhanden|existente|existant|esistente|existente|既存|기존|现有|現有
        extra|Zusätzlich|adicional|supplémentaire|aggiuntivo|adicional|追加|추가|额外|額外
        follow|Übernehmen|seguir|suivre|segui|seguir|従う|따르기|跟随|跟隨
        syntax|Syntax|sintaxis|syntaxe|sintassi|sintaxe|構文|구문|语法|語法
        family|Familie|familia|famille|famiglia|família|ファミリー|제품군|系列|系列
        gapless|Lückenlos|sin pausas|sans intervalle|senza interruzioni|sem intervalos|ギャップレス|끊김 없음|无缝|無縫
        identity|Identität|identidad|identité|identità|identidade|識別|ID|标识|識別
        strips|Entfernt|elimina|supprime|rimuove|remove|除去|제거|移除|移除
        suffixes|Suffixe|sufijos|suffixes|suffissi|sufixos|接尾辞|접미사|后缀|後置詞
        leaflet|Faltblatt|folleto|feuillet|opuscolo|folheto|リーフレット|소책자|折页|摺頁
        license|Lizenz|licencia|licence|licenza|licença|ライセンス|라이선스|许可证|授權
        lyrics|Liedtext|letra|paroles|testo|letra|歌詞|가사|歌词|歌詞
        removals|Entfernungen|eliminaciones|suppressions|rimozioni|remoções|削除|제거|移除|移除
        depth|Tiefe|profundidad|profondeur|profondità|profundidade|深度|깊이|深度|深度
        mood|Stimmung|estado de ánimo|humeur|atmosfera|clima|ムード|분위기|情绪|情緒
        movement|Satz|movimiento|mouvement|movimento|movimento|楽章|악장|乐章|樂章
        performer|Interpret|intérprete|interprète|interprete|intérprete|演奏者|연주자|表演者|演出者
        renamed|Umbenannt|renombrado|renommé|rinominato|renomeado|名前変更済み|이름 변경됨|已重命名|已重新命名
        skipped|Übersprungen|omitido|ignoré|saltato|ignorado|スキップ済み|건너뜀|已跳过|已略過
        compatible|Kompatibel|compatible|compatible|compatibile|compatível|互換|호환|兼容|相容
        sync|Synchronisieren|sincronizar|synchroniser|sincronizza|sincronizar|同期|동기화|同步|同步
        transport|Transport|transporte|transport|trasporto|transporte|転送|전송|传输|傳輸
        trim|Entfernen|recortar|supprimer|rimuovi|remover|除去|잘라내기|修剪|修剪
        whitespace|Leerraum|espacios|espaces|spazi|espaços|空白|공백|空白|空白
        verify|Überprüfen|verificar|vérifier|verifica|verificar|検証|확인|验证|驗證
        website|Website|sitio web|site web|sito web|site|ウェブサイト|웹사이트|网站|網站
        whole|Gesamt|completo|entier|intero|inteiro|全体|전체|整个|整個
        writer|Autor|autor|auteur|autore|autor|作家|작가|作者|作者
        steel|Stahl|acero|acier|acciaio|aço|スチール|스틸|钢|鋼
        blue|Blau|azul|bleu|blu|azul|青|파랑|蓝色|藍色
        filter syntax|Filtersyntax|sintaxis del filtro|syntaxe du filtre|sintassi del filtro|sintaxe do filtro|フィルター構文|필터 구문|筛选器语法|篩選器語法
        above|oben|arriba|ci-dessus|sopra|acima|上記|위|上方|上方
        accent|Akzent|acento|accent|accento|acento|アクセント|악센트|重音|重音
        according|gemäß|según|selon|secondo|conforme|従って|따라|根据|依據
        across|über|entre|sur|tra|entre|全体|전체|跨|跨
        adjust|Anpassen|ajustar|ajuster|regola|ajustar|調整|조정|调整|調整
        adoption|Übernahme|adopción|adoption|adozione|adoção|採用|채택|采用|採用
        against|gegen|contra|contre|contro|contra|対して|대상|针对|針對
        alias|Alias|alias|alias|alias|alias|別名|별칭|别名|別名
        alter|Ändern|modificar|modifier|modifica|alterar|変更|변경|更改|變更
        always|immer|siempre|toujours|sempre|sempre|常に|항상|始终|永遠
        amber|Bernstein|ámbar|ambre|ambra|âmbar|琥珀色|호박색|琥珀色|琥珀色
        among|unter|entre|parmi|tra|entre|間|중|之间|之間
        analysis|Analyse|análisis|analyse|analisi|análise|分析|분석|分析|分析
        analyzing|Analyse läuft|analizando|analyse|analisi|analisando|分析中|분석 중|正在分析|正在分析
        app|App|aplicación|application|app|aplicativo|アプリ|앱|应用|應用程式
        appear|erscheinen|aparecer|apparaître|apparire|aparecer|表示|표시|出现|出現
        applicable|Anwendbar|aplicable|applicable|applicabile|aplicável|適用可能|적용 가능|适用|適用
        approval|Genehmigung|aprobación|approbation|approvazione|aprovação|承認|승인|批准|核准
        approvals|Genehmigungen|aprobaciones|approbations|approvazioni|aprovações|承認|승인|批准|核准
        art|Kunst|arte|art|arte|arte|アート|아트|艺术|藝術
        authoritative|Maßgeblich|autoritativo|faisant autorité|autorevole|oficial|信頼済み|신뢰할 수 있음|权威|權威
        authorize|Autorisieren|autorizar|autoriser|autorizza|autorizar|承認|승인|授权|授權
        auto|Automatisch|automático|automatique|automatico|automático|自動|자동|自动|自動
        automatically|automatisch|automáticamente|automatiquement|automaticamente|automaticamente|自動的に|자동으로|自动|自動
        aware|berücksichtigt|consciente|informé|consapevole|ciente|認識|인식|知晓|知悉
        backup|Sicherung|copia de seguridad|sauvegarde|backup|backup|バックアップ|백업|备份|備份
        backups|Sicherungen|copias de seguridad|sauvegardes|backup|backups|バックアップ|백업|备份|備份
        balanced|Ausgeglichen|equilibrado|équilibré|bilanciato|equilibrado|均衡|균형|平衡|平衡
        because|weil|porque|car|perché|porque|ため|때문에|因为|因為
        become|werden|convertirse|devenir|diventare|tornar-se|なる|됨|变为|變為
        been|wurde|sido|été|stato|sido|済み|됨|已经|已經
        begin|Beginnen|comenzar|commencer|inizia|iniciar|開始|시작|开始|開始
        behavior|Verhalten|comportamiento|comportement|comportamento|comportamento|動作|동작|行为|行為
        being|wird|siendo|étant|essendo|sendo|中|중|正在|正在
        below|unten|abajo|ci-dessous|sotto|abaixo|下記|아래|下方|下方
        beneath|unterhalb|debajo|sous|sotto|abaixo|下|아래|下方|下方
        best|Beste|mejor|meilleur|migliore|melhor|最適|최상|最佳|最佳
        big|Groß|grande|grand|grande|grande|ビッグ|큼|大|大
        binding|Bindung|enlace|liaison|associazione|vínculo|バインド|바인딩|绑定|繫結
        bindings|Bindungen|enlaces|liaisons|associazioni|vínculos|バインド|바인딩|绑定|繫結
        bit|Bit|bit|bit|bit|bit|ビット|비트|位|位元
        blank|Leer|en blanco|vide|vuoto|em branco|空白|비어 있음|空白|空白
        block|Blockieren|bloquear|bloquer|blocca|bloquear|ブロック|차단|阻止|封鎖
        blocked|Blockiert|bloqueado|bloqué|bloccato|bloqueado|ブロック済み|차단됨|已阻止|已封鎖
        blocking|Blockierend|bloqueante|bloquant|bloccante|bloqueador|ブロック中|차단|阻止|封鎖
        branch|Zweig|rama|branche|ramo|ramo|分岐|분기|分支|分支
        break|Umbruch|salto|rupture|interruzione|quebra|改行|줄 바꿈|换行|換行
        bucketed|Gruppiert|agrupado|regroupé|raggruppato|agrupado|分類済み|그룹화됨|已分组|已分組
        built|Erstellt|compilado|généré|creato|compilado|構築済み|빌드됨|已构建|已建置
        bulk|Massenverarbeitung|masivo|groupé|in blocco|em massa|一括|일괄|批量|批次
        busy|Beschäftigt|ocupado|occupé|occupato|ocupado|使用中|사용 중|忙碌|忙碌
        but|aber|pero|mais|ma|mas|ただし|하지만|但是|但是
        bypasses|Umgeht|omite|contourne|ignora|ignora|回避|우회|绕过|略過
        calm|Ruhig|tranquilo|serein|tranquillo|calmo|落ち着いた|차분한|从容|平靜
        carefully|sorgfältig|cuidadosamente|avec soin|con attenzione|com cuidado|慎重に|신중하게|谨慎地|謹慎地
        carriage return|Wagenrücklauf|retorno de carro|retour chariot|ritorno carrello|retorno de carro|キャリッジリターン|캐리지 리턴|回车|歸位
        categories|Kategorien|categorías|catégories|categorie|categorias|カテゴリ|범주|类别|類別
        category|Kategorie|categoría|catégorie|categoria|categoria|カテゴリ|범주|类别|類別
        cell|Zelle|celda|cellule|cella|célula|セル|셀|单元格|儲存格
        cells|Zellen|celdas|cellules|celle|células|セル|셀|单元格|儲存格
        character|Zeichen|carácter|caractère|carattere|caractere|文字|문자|字符|字元
        characters|Zeichen|caracteres|caractères|caratteri|caracteres|文字|문자|字符|字元
        choice|Auswahl|opción|choix|scelta|escolha|選択肢|선택|选项|選項
        choices|Auswahlmöglichkeiten|opciones|choix|scelte|escolhas|選択肢|선택 항목|选项|選項
        chosen|Ausgewählt|elegido|choisi|scelto|escolhido|選択済み|선택됨|已选择|已選取
        classification|Klassifizierung|clasificación|classification|classificazione|classificação|分類|분류|分类|分類
        clean|Bereinigen|limpiar|nettoyer|pulisci|limpar|クリーン|정리|清理|清理
        clipboard|Zwischenablage|portapapeles|presse-papiers|appunti|área de transferência|クリップボード|클립보드|剪贴板|剪貼簿
        cluster|Cluster|grupo|groupe|gruppo|grupo|クラスター|클러스터|群集|叢集
        clusters|Cluster|grupos|groupes|gruppi|grupos|クラスター|클러스터|群集|叢集
        code|Code|código|code|codice|código|コード|코드|代码|程式碼
        collection|Sammlung|colección|collection|raccolta|coleção|コレクション|컬렉션|集合|集合
        combine|Kombinieren|combinar|combiner|combina|combinar|結合|결합|合并|合併
        combined|Kombiniert|combinado|combiné|combinato|combinado|結合済み|결합됨|已合并|已合併
        combines|Kombiniert|combina|combine|combina|combina|結合|결합|合并|合併
        combining|Kombinieren|combinando|combinaison|combinazione|combinando|結合中|결합 중|正在合并|正在合併
        come|stammen|provenir|provenir|provenire|vir|来る|옴|来自|來自
        comma|Komma|coma|virgule|virgola|vírgula|カンマ|쉼표|逗号|逗號
        committed|Bestätigt|confirmado|validé|confermato|confirmado|確定済み|커밋됨|已提交|已認可
        common|Gemeinsam|común|commun|comune|comum|共通|공통|通用|共用
        companion|Begleiter|complemento|compagnon|componente|complemento|補助|도우미|配套项|隨附項目
        compare|Vergleichen|comparar|comparer|confronta|comparar|比較|비교|比较|比較
        comparison|Vergleich|comparación|comparaison|confronto|comparação|比較|비교|比较|比較
        comparisons|Vergleiche|comparaciones|comparaisons|confronti|comparações|比較|비교|比较|比較
        completion|Abschluss|finalización|achèvement|completamento|conclusão|完了|완료|完成|完成
        composition|Zusammensetzung|composición|composition|composizione|composição|合成|구성|组合|組合
        computing|Berechnung|calculando|calcul|calcolo|calculando|計算中|계산 중|正在计算|正在計算
        configure|Konfigurieren|configurar|configurer|configura|configurar|構成|구성|配置|設定
        configured|Konfiguriert|configurado|configuré|configurato|configurado|構成済み|구성됨|已配置|已設定
        confirm|Bestätigen|confirmar|confirmer|conferma|confirmar|確認|확인|确认|確認
        confirmation|Bestätigung|confirmación|confirmation|conferma|confirmação|確認|확인|确认|確認
        confirmed|Bestätigt|confirmado|confirmé|confermato|confirmado|確認済み|확인됨|已确认|已確認
        connect|Verbinden|conectar|connecter|connetti|conectar|接続|연결|连接|連線
        connections|Verbindungen|conexiones|connexions|connessioni|conexões|接続|연결|连接|連線
        considered|Berücksichtigt|considerado|considéré|considerato|considerado|考慮済み|고려됨|已考虑|已考量
        consistency|Konsistenz|coherencia|cohérence|coerenza|consistência|整合性|일관성|一致性|一致性
        contain|Enthalten|contener|contenir|contenere|conter|含む|포함|包含|包含
        containing|Enthält|que contiene|contenant|contenente|contendo|含む|포함|包含|包含
        content|Inhalt|contenido|contenu|contenuto|conteúdo|内容|콘텐츠|内容|內容
        contents|Inhalte|contenidos|contenus|contenuti|conteúdo|内容|콘텐츠|内容|內容
        continuous|Fortlaufend|continuo|continu|continuo|contínuo|連続|연속|连续|連續
        contradictory|Widersprüchlich|contradictorio|contradictoire|contraddittorio|contraditório|矛盾|모순|矛盾|矛盾
        convenient|Praktisch|conveniente|pratique|pratico|conveniente|便利|편리|便捷|便利
        counterpart|Gegenstück|contraparte|contrepartie|controparte|equivalente|対応項目|대응 항목|对应项|對應項目
        credential|Anmeldedaten|credencial|identifiant|credenziale|credencial|資格情報|자격 증명|凭据|認證
        credit|Mitwirkung|crédito|crédit|credito|crédito|クレジット|크레딧|署名|製作群
        cross|Kreuz|cruzado|croisé|incrociato|cruzado|クロス|교차|交叉|交叉
        cuesheet|Cue-Sheet|hoja cue|feuille cue|cue sheet|folha cue|キューシート|큐 시트|提示表|提示表
        cuesheets|Cue-Sheets|hojas cue|feuilles cue|cue sheet|folhas cue|キューシート|큐 시트|提示表|提示表
        customize|Anpassen|personalizar|personnaliser|personalizza|personalizar|カスタマイズ|사용자 지정|自定义|自訂
        data|Daten|datos|données|dati|dados|データ|데이터|数据|資料
        decimal|Dezimal|decimal|décimal|decimale|decimal|10進|10진수|十进制|十進位
        declined|Abgelehnt|rechazado|refusé|rifiutato|recusado|拒否済み|거부됨|已拒绝|已拒絕
        decoder|Decoder|decodificador|décodeur|decoder|decodificador|デコーダー|디코더|解码器|解碼器
        decomposition|Zerlegung|descomposición|décomposition|decomposizione|decomposição|分解|분해|分解|分解
        deduplicate|Deduplizieren|desduplicar|dédupliquer|deduplica|desduplicar|重複排除|중복 제거|去重|去除重複
        deferred|Zurückgestellt|aplazado|différé|differito|adiado|延期|지연됨|已推迟|已延後
        deletion|Löschung|eliminación|suppression|eliminazione|exclusão|削除|삭제|删除|刪除
        deletions|Löschungen|eliminaciones|suppressions|eliminazioni|exclusões|削除|삭제|删除|刪除
        delimited|Getrennt|delimitado|délimité|delimitato|delimitado|区切り|구분됨|分隔|分隔
        derivation|Ableitung|derivación|dérivation|derivazione|derivação|派生|파생|派生|衍生
        derivations|Ableitungen|derivaciones|dérivations|derivazioni|derivações|派生|파생|派生|衍生
        derive|Ableiten|derivar|dériver|deriva|derivar|導出|파생|派生|衍生
        desired|Gewünscht|deseado|souhaité|desiderato|desejado|希望|원하는|所需|所需
        detail|Detail|detalle|détail|dettaglio|detalhe|詳細|세부 정보|详细信息|詳細資料
        diagnostic|Diagnose|diagnóstico|diagnostic|diagnostica|diagnóstico|診断|진단|诊断|診斷
        did|hat|hizo|a|ha|fez|実行済み|수행함|已执行|已執行
        differ|Abweichen|diferir|différer|differire|diferir|異なる|다름|不同|不同
        differences|Unterschiede|diferencias|différences|differenze|diferenças|差異|차이|差异|差異
        different|Unterschiedlich|diferente|différent|diverso|diferente|異なる|다른|不同|不同
        differs|Weicht ab|difiere|diffère|differisce|difere|異なる|다름|不同|不同
        digit|Ziffer|dígito|chiffre|cifra|dígito|数字|숫자|数字|數字
        dimension|Abmessung|dimensión|dimension|dimensione|dimensão|寸法|크기|尺寸|尺寸
        direct|Direkt|directo|direct|diretto|direto|直接|직접|直接|直接
        directed|Benutzergesteuert|dirigido|dirigé|diretto|direcionado|指定|지정됨|定向|指定
        discovery|Erkennung|detección|découverte|rilevamento|descoberta|検出|검색|发现|探索
        disk|Datenträger|disco|disque|disco|disco|ディスク|디스크|磁盘|磁碟
        displaced|Verdrängt|desplazado|déplacé|spostato|deslocado|移動済み|이동됨|已移位|已位移
        display|Anzeige|visualización|affichage|visualizzazione|exibição|表示|표시|显示|顯示
        distance|Abstand|distancia|distance|distanza|distância|距離|거리|距离|距離
        do|Ausführen|hacer|faire|eseguire|fazer|実行|실행|执行|執行
        documented|Dokumentiert|documentado|documenté|documentato|documentado|文書化済み|문서화됨|已记录|已記錄
        does|führt aus|hace|fait|esegue|faz|実行|수행|执行|執行
        double|Doppelt|doble|double|doppio|duplo|ダブル|이중|双|雙
        down|Abwärts|abajo|vers le bas|giù|abaixo|下|아래|向下|向下
        drift|Abweichung|deriva|dérive|deriva|desvio|ずれ|편차|偏移|偏移
        durable|Dauerhaft|duradero|durable|durevole|durável|永続|영구|持久|持久
        editor|Editor|editor|éditeur|editor|editor|エディター|편집기|编辑器|編輯器
        effective|Wirksam|efectivo|effectif|effettivo|efetivo|有効|유효|有效|有效
        effects|Auswirkungen|efectos|effets|effetti|efeitos|効果|효과|效果|效果
        either|Entweder|cualquiera|l’un ou l’autre|uno dei due|qualquer|いずれか|둘 중 하나|任一|任一
        eligible|Geeignet|elegible|éligible|idoneo|elegível|対象|대상|符合条件|符合資格
        embedding|Einbettung|incrustación|intégration|incorporamento|incorporação|埋め込み|포함|嵌入|內嵌
        enable|Aktivieren|habilitar|activer|abilita|ativar|有効化|사용|启用|啟用
        enclosing|Umgebend|envolvente|englobant|racchiuso|envolvente|囲む|둘러싼|包含|包含
        encoded|Codiert|codificado|encodé|codificato|codificado|エンコード済み|인코딩됨|已编码|已編碼
        encoder|Encoder|codificador|encodeur|encoder|codificador|エンコーダー|인코더|编码器|編碼器
        english|Englisch|inglés|anglais|inglese|inglês|英語|영어|英语|英文
        enough|Ausreichend|suficiente|suffisant|sufficiente|suficiente|十分|충분|足够|足夠
        entered|Eingegeben|introducido|saisi|inserito|inserido|入力済み|입력됨|已输入|已輸入
        entire|Gesamt|completo|entier|intero|inteiro|全体|전체|整个|整個
        entries|Einträge|entradas|entrées|voci|entradas|エントリ|항목|条目|項目
        enumerate|Auflisten|enumerar|énumérer|enumera|enumerar|列挙|열거|枚举|列舉
        equal|Gleich|igual|égal|uguale|igual|等しい|같음|相等|相等
        even|Auch|incluso|même|anche|mesmo|でも|심지어|即使|即使
        exceed|Überschreiten|exceder|dépasser|supera|exceder|超過|초과|超过|超過
        exceeding|Überschreitet|excediendo|dépassant|superamento|excedendo|超過|초과|超过|超過
        exists|Vorhanden|existe|existe|esiste|existe|存在|있음|存在|存在
        exited|Beendet|finalizado|terminé|terminato|encerrado|終了|종료됨|已退出|已結束
        expired|Abgelaufen|caducado|expiré|scaduto|expirado|期限切れ|만료됨|已过期|已過期
        explicit|Explizit|explícito|explicite|esplicito|explícito|明示的|명시적|显式|明確
        explore|Erkunden|explorar|explorer|esplora|explorar|参照|탐색|浏览|瀏覽
        explorer|Explorer|explorador|explorateur|esplora file|explorador|エクスプローラー|탐색기|资源管理器|檔案總管
        extension|Erweiterung|extensión|extension|estensione|extensão|拡張子|확장자|扩展名|副檔名
        extract|Extrahieren|extraer|extraire|estrai|extrair|抽出|추출|提取|擷取
        extraction|Extraktion|extracción|extraction|estrazione|extração|抽出|추출|提取|擷取
        fallback|Fallback|alternativa|repli|ripiego|alternativa|フォールバック|대체|回退|後援
        faster|Schneller|más rápido|plus rapide|più veloce|mais rápido|高速|더 빠름|更快|更快
        line feed|Zeilenvorschub|salto de línea|saut de ligne|avanzamento riga|avanço de linha|改行|줄 바꿈|换行|換行
        fidelity|Genauigkeit|fidelidad|fidélité|fedeltà|fidelidade|忠実度|충실도|保真度|精確度
        figure|Zahl|figura|chiffre|figura|figura|数字|숫자|数字|數字
        filesystem|Dateisystem|sistema de archivos|système de fichiers|file system|sistema de arquivos|ファイルシステム|파일 시스템|文件系统|檔案系統
        finish|Beenden|finalizar|terminer|termina|finalizar|完了|마침|完成|完成
        finishing|Abschluss|finalizando|finalisation|completamento|finalizando|完了処理|마무리|正在完成|正在完成
        flagged|Markiert|marcado|signalé|contrassegnato|sinalizado|フラグ済み|표시됨|已标记|已標記
        flags|Markierungen|indicadores|indicateurs|contrassegni|sinalizadores|フラグ|플래그|标志|旗標
        force|Erzwingen|forzar|forcer|forza|forçar|強制|강제|强制|強制
        form|Form|forma|forme|forma|forma|形式|형식|形式|形式
        frames|Frames|fotogramas|images|fotogrammi|quadros|フレーム|프레임|帧|畫格
        full|Vollständig|completo|complet|completo|completo|完全|전체|完整|完整
        fuzzy|Unscharf|difuso|approximatif|sfocato|difuso|あいまい|유사|模糊|模糊
        gain|Verstärkung|ganancia|gain|guadagno|ganho|ゲイン|게인|增益|增益
        glob|Glob-Muster|patrón glob|motif glob|modello glob|padrão glob|グロブパターン|glob 패턴|通配模式|萬用模式
        global|Global|global|global|globale|global|グローバル|전역|全局|全域
        go|Los|ir|aller|vai|ir|移動|이동|转到|移至
        handled|Verarbeitet|gestionado|traité|gestito|processado|処理済み|처리됨|已处理|已處理
        here|Hier|aquí|ici|qui|aqui|ここ|여기|此处|這裡
        hierarchy|Hierarchie|jerarquía|hiérarchie|gerarchia|hierarquia|階層|계층|层次结构|階層
        high|Hoch|alto|élevé|alto|alto|高|높음|高|高
        highest|Höchste|más alto|le plus élevé|più alto|mais alto|最高|최고|最高|最高
        highlighted|Hervorgehoben|resaltado|surligné|evidenziato|realçado|強調表示|강조됨|已突出显示|已醒目提示
        hover|Daraufzeigen|pasar el cursor|survoler|passa sopra|passar o mouse|ポイント|마우스로 가리키기|悬停|暫留
        how|Wie|cómo|comment|come|como|方法|방법|如何|如何
        icon|Symbol|icono|icône|icona|ícone|アイコン|아이콘|图标|圖示
        identifier|Kennung|identificador|identifiant|identificatore|identificador|識別子|식별자|标识符|識別碼
        identify|Identifizieren|identificar|identifier|identifica|identificar|識別|식별|识别|識別
        immediately|Sofort|inmediatamente|immédiatement|immediatamente|imediatamente|すぐに|즉시|立即|立即
        importable|Importierbar|importable|importable|importabile|importável|インポート可能|가져오기 가능|可导入|可匯入
        incoming|Eingehend|entrante|entrant|in arrivo|recebido|受信|수신|传入|傳入
        incompatible|Inkompatibel|incompatible|incompatible|incompatibile|incompatível|非互換|호환되지 않음|不兼容|不相容
        inconsistent|Inkonsistent|inconsistente|incohérent|incoerente|inconsistente|不整合|불일치|不一致|不一致
        independent|Unabhängig|independiente|indépendant|indipendente|independente|独立|독립|独立|獨立
        indeterminate|Unbestimmt|indeterminado|indéterminé|indeterminato|indeterminado|不確定|확정되지 않음|不确定|不確定
        indexer|Indexer|indexador|indexeur|indicizzatore|indexador|インデクサー|인덱서|索引器|索引器
        infer|Ableiten|inferir|déduire|deduci|inferir|推測|추론|推断|推斷
        ingestion|Aufnahme|ingesta|ingestion|acquisizione|ingestão|取り込み|수집|引入|擷取
        inline|Inline|en línea|en ligne|incorporato|embutido|インライン|인라인|内联|內嵌
        installed|Installiert|instalado|installé|installato|instalado|インストール済み|설치됨|已安装|已安裝
        installs|Installiert|instala|installe|installa|instala|インストール|설치|安装|安裝
        instantly|Sofort|al instante|instantanément|istantaneamente|instantaneamente|すぐに|즉시|立即|立即
        instead|Stattdessen|en su lugar|à la place|invece|em vez disso|代わりに|대신|而是|而是
        integration|Integration|integración|intégration|integrazione|integração|統合|통합|集成|整合
        integrations|Integrationen|integraciones|intégrations|integrazioni|integrações|統合|통합|集成|整合
        integrity|Integrität|integridad|intégrité|integrità|integridade|整合性|무결성|完整性|完整性
        intend|Beabsichtigen|pretender|prévoir|intendere|pretender|意図|의도|打算|預期
        intentionally|Absichtlich|intencionalmente|intentionnellement|intenzionalmente|intencionalmente|意図的に|의도적으로|有意|有意
        internal|Intern|interno|interne|interno|interno|内部|내부|内部|內部
        invariants|Invarianten|invariantes|invariants|invarianti|invariantes|不変条件|불변값|不变量|不變值
        inventorying|Inventarisierung|inventariando|inventaire|inventario|inventariando|一覧作成中|인벤토리 작성 중|正在盘点|正在盤點
        invocation|Aufruf|invocación|appel|invocazione|invocação|呼び出し|호출|调用|呼叫
        invocations|Aufrufe|invocaciones|appels|invocazioni|invocações|呼び出し|호출|调用|呼叫
        irreversibly|Unwiderruflich|irreversiblemente|irréversiblement|irreversibilmente|irreversivelmente|元に戻せず|되돌릴 수 없게|不可逆|不可逆
        isolation|Isolation|aislamiento|isolement|isolamento|isolamento|分離|격리|隔离|隔離
        it|es|ello|il|esso|isso|それ|해당 항목|它|它
        its|dessen|su|son|il suo|seu|その|해당|其|其
        join|Verbinden|unir|joindre|unisci|unir|結合|결합|连接|連接
        joiner|Verbinder|unión|joncteur|congiuntore|juntor|接合子|연결자|连接符|連接符
        largest|Größte|más grande|le plus grand|più grande|maior|最大|가장 큼|最大|最大
        later|Später|más tarde|plus tard|più tardi|mais tarde|後で|나중에|稍后|稍後
        latest|Neueste|más reciente|plus récent|più recente|mais recente|最新|최신|最新|最新
        layout|Layout|diseño|disposition|layout|layout|レイアウト|레이아웃|布局|版面配置
        lead|Führend|principal|principal|principale|principal|先頭|선행|前导|前置
        least|Mindestens|mínimo|au moins|almeno|pelo menos|最小|최소|至少|至少
        left|Links|izquierda|gauche|sinistra|esquerda|左|왼쪽|左|左
        length|Länge|longitud|longueur|lunghezza|comprimento|長さ|길이|长度|長度
        lightweight|Leichtgewichtig|ligero|léger|leggero|leve|軽量|경량|轻量|輕量
        like|Wie|como|comme|come|como|同様|같이|如同|如同
        likely|Wahrscheinlich|probable|probable|probabile|provável|可能性あり|가능성 있음|可能|可能
        list|Liste|lista|liste|elenco|lista|一覧|목록|列表|清單
        lock|Sperre|bloqueo|verrou|blocco|bloqueio|ロック|잠금|锁定|鎖定
        longer|Länger|más largo|plus long|più lungo|mais longo|より長い|더 김|更长|更長
        lookup|Nachschlagen|búsqueda|recherche|ricerca|consulta|検索|조회|查找|查詢
        lookups|Nachschlagen|búsquedas|recherches|ricerche|consultas|検索|조회|查找|查詢
        lossless|Verlustfrei|sin pérdida|sans perte|senza perdita|sem perdas|可逆|무손실|无损|無損
        loudness|Lautheit|sonoridad|sonie|sonorità|sonoridade|ラウドネス|라우드니스|响度|響度
        made|Erstellt|hecho|effectué|creato|feito|作成済み|만들어짐|已生成|已建立
        manager|Manager|administrador|gestionnaire|gestore|gerenciador|マネージャー|관리자|管理器|管理員
        map|Zuordnen|asignar|mapper|mappa|mapear|マップ|매핑|映射|對應
        mapped|Zugeordnet|asignado|mappé|mappato|mapeado|マップ済み|매핑됨|已映射|已對應
        max|Maximum|máximo|maximum|massimo|máximo|最大|최대|最大|最大
        mebibytes|Mebibytes|mebibytes|mébioctets|mebibyte|mebibytes|メビバイト|메비바이트|兆二进制字节|百萬位元組
        membership|Zugehörigkeit|pertenencia|appartenance|appartenenza|associação|所属|구성원|成员关系|成員關係
        merge|Zusammenführen|combinar|fusionner|unisci|mesclar|マージ|병합|合并|合併
        merges|Zusammenführungen|combinaciones|fusions|unioni|mesclagens|マージ|병합|合并|合併
        meta|Meta|meta|méta|meta|meta|メタ|메타|元|中繼
        minus sign|Minuszeichen|signo menos|signe moins|segno meno|sinal de menos|マイナス記号|빼기 기호|减号|減號
        mirror|Spiegeln|reflejar|miroir|specchio|espelhar|ミラー|미러|镜像|鏡像
        modification|Änderung|modificación|modification|modifica|modificação|変更|수정|修改|修改
        most|Meiste|mayoría|plus|maggior parte|maioria|ほとんど|대부분|大多数|大多數
        multi|Mehrfach|múltiple|multiple|multiplo|múltiplo|複数|다중|多重|多重
        multiple|Mehrere|múltiple|plusieurs|multiplo|múltiplos|複数|여러|多个|多個
        mutation|Mutation|mutación|mutation|mutazione|mutação|変更|변경|变更|變更
        mutations|Mutationen|mutaciones|mutations|mutazioni|mutações|変更|변경|变更|變更
        narrow|Schmal|estrecho|étroit|stretto|estreito|狭い|좁음|窄|窄
        native|Nativ|nativo|natif|nativo|nativo|ネイティブ|기본|原生|原生
        negate|Negieren|negar|nier|nega|negar|否定|부정|取反|否定
        never|Niemals|nunca|jamais|mai|nunca|しない|안 함|从不|永不
        non|Nicht|no|non|non|não|非|비|非|非
        non-joiner|Nichtverbinder|no enlazador|anti-liant|non congiuntore|não juntor|非接合子|비연결자|非连接符|非連接符
        nonspacing|Nichtabstand|sin espaciado|sans espacement|senza spaziatura|sem espaçamento|非スペース|비간격|非间距|非間距
        normalized-equivalent|normalisiert gleichwertig|equivalente normalizado|équivalent normalisé|equivalente normalizzato|equivalente normalizado|正規化同等|정규화 동등|规范化等价|正規化等價
        not|nicht|no|ne…pas|non|não|ではない|아님|不|不
        note|Hinweis|nota|remarque|nota|observação|注記|참고|注释|注意
        nothing|Nichts|nada|rien|niente|nada|なし|없음|无|無
        now|Jetzt|ahora|maintenant|ora|agora|今すぐ|지금|现在|現在
        off|Aus|desactivado|désactivé|disattivato|desativado|オフ|끔|关闭|關閉
        once|Einmal|una vez|une fois|una volta|uma vez|一度|한 번|一次|一次
        operating|Betrieb|operativo|fonctionnement|operativo|operacional|動作|운영|运行|執行
        ordinary|Normal|normal|ordinaire|normale|comum|通常|일반|普通|一般
        outside|Außerhalb|fuera|extérieur|esterno|fora|外部|외부|外部|外部
        override|Überschreiben|anular|remplacer|sostituzione|substituir|上書き|재정의|覆盖|覆寫
        overrides|Überschreibungen|anulaciones|remplacements|sostituzioni|substituições|上書き|재정의|覆盖|覆寫
        oversized|Zu groß|sobredimensionado|surdimensionné|sovradimensionato|superdimensionado|大きすぎる|너무 큼|过大|過大
        own|Eigene|propio|propre|proprio|próprio|独自|자체|自己的|自己的
        pair|Paar|par|paire|coppia|par|ペア|쌍|对|配對
        pairs|Paare|pares|paires|coppie|pares|ペア|쌍|对|配對
        palette|Palette|paleta|palette|tavolozza|paleta|パレット|팔레트|调色板|調色盤
        panel|Bereich|panel|panneau|pannello|painel|パネル|패널|面板|面板
        paragraph|Absatz|párrafo|paragraphe|paragrafo|parágrafo|段落|단락|段落|段落
        parser|Parser|analizador|analyseur|parser|analisador|パーサー|파서|解析器|剖析器
        parsing|Analyse|análisis|analyse|analisi|análise|解析|구문 분석|解析|剖析
        partial|Teilweise|parcial|partiel|parziale|parcial|部分|부분|部分|部分
        paste|Einfügen|pegar|coller|incolla|colar|貼り付け|붙여넣기|粘贴|貼上
        pasted|Eingefügt|pegado|collé|incollato|colado|貼り付け済み|붙여넣음|已粘贴|已貼上
        pasting|Einfügen|pegando|collage|incollaggio|colando|貼り付け中|붙여넣는 중|正在粘贴|正在貼上
        peak|Spitze|pico|pic|picco|pico|ピーク|피크|峰值|峰值
        permanently|Dauerhaft|permanentemente|définitivement|permanentemente|permanentemente|完全に|영구적으로|永久|永久
        permissions|Berechtigungen|permisos|autorisations|autorizzazioni|permissões|アクセス許可|권한|权限|權限
        permits|Erlaubt|permite|autorise|consente|permite|許可|허용|允许|允許
        permitting|Erlaubt|permitiendo|autorisant|consentendo|permitindo|許可|허용|允许|允許
        phrases|Ausdrücke|frases|phrases|frasi|frases|語句|구문|短语|片語
        placeholders|Platzhalter|marcadores|espaces réservés|segnaposto|marcadores|プレースホルダー|자리 표시자|占位符|預留位置
        plain|Einfach|simple|simple|semplice|simples|プレーン|일반|纯文本|純文字
        player|Player|reproductor|lecteur|lettore|player|プレーヤー|플레이어|播放器|播放器
        plus|Plus|más|plus|più|mais|プラス|더하기|加|加
        populate|Füllen|rellenar|remplir|popola|preencher|入力|채우기|填充|填入
        portable|Portabel|portátil|portable|portatile|portátil|ポータブル|휴대용|便携|可攜
        positive|Positiv|positivo|positif|positivo|positivo|正|양수|正|正
        prefer|Bevorzugen|preferir|préférer|preferisci|preferir|優先|선호|首选|優先
        preflight|Vorprüfung|comprobación previa|contrôle préalable|controllo preliminare|verificação prévia|事前確認|사전 검사|预检|預先檢查
        prepare|Vorbereiten|preparar|préparer|prepara|preparar|準備|준비|准备|準備
        prepared|Vorbereitet|preparado|préparé|preparato|preparado|準備済み|준비됨|已准备|已準備
        prerequisite|Voraussetzung|requisito previo|prérequis|prerequisito|pré-requisito|前提条件|필수 조건|先决条件|必要條件
        prerequisites|Voraussetzungen|requisitos previos|prérequis|prerequisiti|pré-requisitos|前提条件|필수 조건|先决条件|必要條件
        preservation|Erhaltung|conservación|conservation|conservazione|preservação|保持|보존|保留|保留
        primary|Primär|principal|principal|principale|principal|主要|기본|主要|主要
        private|Privat|privado|privé|privato|privado|非公開|비공개|私有|私人
        problem|Problem|problema|problème|problema|problema|問題|문제|问题|問題
        process|Prozess|proceso|processus|processo|processo|処理|프로세스|进程|程序
        produce|Erzeugen|producir|produire|produci|produzir|生成|생성|生成|產生
        project|Projekt|proyecto|projet|progetto|projeto|プロジェクト|프로젝트|项目|專案
        protection|Schutz|protección|protection|protezione|proteção|保護|보호|保护|保護
        provided|Bereitgestellt|proporcionado|fourni|fornito|fornecido|提供済み|제공됨|已提供|已提供
        query|Abfrage|consulta|requête|query|consulta|クエリ|쿼리|查询|查詢
        quotation|Anführungszeichen|comillas|guillemets|virgolette|aspas|引用符|따옴표|引号|引號
        range|Bereich|rango|plage|intervallo|faixa|範囲|범위|范围|範圍
        re|Erneut|de nuevo|à nouveau|nuovamente|novamente|再|다시|重新|重新
        reached|Erreicht|alcanzado|atteint|raggiunto|alcançado|到達|도달|已达到|已達到
        readable|Lesbar|legible|lisible|leggibile|legível|読み取り可能|읽기 가능|可读|可讀
        reassigned|Neu zugewiesen|reasignado|réattribué|riassegnato|reatribuído|再割り当て|다시 할당됨|已重新分配|已重新指派
        receives|Empfängt|recibe|reçoit|riceve|recebe|受信|수신|接收|接收
        recognized|Erkannt|reconocido|reconnu|riconosciuto|reconhecido|認識済み|인식됨|已识别|已識別
        recognizes|Erkennt|reconoce|reconnaît|riconosce|reconhece|認識|인식|识别|識別
        reconnect|Neu verbinden|reconectar|reconnecter|riconnetti|reconectar|再接続|다시 연결|重新连接|重新連線
        records|Datensätze|registros|enregistrements|record|registros|記録|레코드|记录|記錄
        recoverable|Wiederherstellbar|recuperable|récupérable|recuperabile|recuperável|復元可能|복구 가능|可恢复|可復原
        redundancy|Redundanz|redundancia|redondance|ridondanza|redundância|冗長性|중복성|冗余|備援
        referential|Referenziell|referencial|référentiel|referenziale|referencial|参照|참조|引用|參照
        reflected|Übernommen|reflejado|reflété|riflesso|refletido|反映済み|반영됨|已反映|已反映
        regenerate|Neu erzeugen|regenerar|régénérer|rigenera|regenerar|再生成|다시 생성|重新生成|重新產生
        regenerated|Neu erzeugt|regenerado|régénéré|rigenerato|regenerado|再生成済み|다시 생성됨|已重新生成|已重新產生
        regenerating|Neuerzeugung|regenerando|régénération|rigenerazione|regenerando|再生成中|다시 생성 중|正在重新生成|正在重新產生
        regional|Regional|regional|régional|regionale|regional|地域|지역|区域|區域
        reliable|Zuverlässig|fiable|fiable|affidabile|confiável|信頼性あり|신뢰할 수 있음|可靠|可靠
        removable|Wechseldatenträger|extraíble|amovible|rimovibile|removível|リムーバブル|이동식|可移动|卸除式
        replacement|Ersatz|reemplazo|remplacement|sostituzione|substituição|置換|교체|替换|取代
        replacements|Ersetzungen|reemplazos|remplacements|sostituzioni|substituições|置換|교체|替换|取代
        represented|Dargestellt|representado|représenté|rappresentato|representado|表現済み|표현됨|已表示|已表示
        represents|Stellt dar|representa|représente|rappresenta|representa|表す|나타냄|表示|表示
        requirements|Anforderungen|requisitos|exigences|requisiti|requisitos|要件|요구 사항|要求|需求
        reserved|Reserviert|reservado|réservé|riservato|reservado|予約済み|예약됨|保留|保留
        resolve|Auflösen|resolver|résoudre|risolvi|resolver|解決|해결|解决|解決
        resolved|Aufgelöst|resuelto|résolu|risolto|resolvido|解決済み|해결됨|已解决|已解決
        resolving|Auflösung|resolviendo|résolution|risoluzione|resolvendo|解決中|해결 중|正在解决|正在解決
        restorable|Wiederherstellbar|restaurable|restaurable|ripristinabile|restaurável|復元可能|복원 가능|可还原|可還原
        return|Zurückgeben|devolver|retourner|restituisci|retornar|戻す|반환|返回|傳回
        returned|Zurückgegeben|devuelto|retourné|restituito|retornado|返却済み|반환됨|已返回|已傳回
        reusable|Wiederverwendbar|reutilizable|réutilisable|riutilizzabile|reutilizável|再利用可能|재사용 가능|可复用|可重複使用
        reviewable|Prüfbar|revisable|révisable|revisionabile|revisável|確認可能|검토 가능|可审阅|可檢閱
        revise|Überarbeiten|revisar|réviser|rivedi|revisar|修正|수정|修订|修訂
        right|Rechts|derecha|droite|destra|direita|右|오른쪽|右|右
        role|Rolle|rol|rôle|ruolo|função|役割|역할|角色|角色
        rollback|Rollback|reversión|retour arrière|rollback|reversão|ロールバック|롤백|回滚|回復
        rolled|Zurückgesetzt|revertido|annulé|annullato|revertido|ロールバック済み|롤백됨|已回滚|已回復
        rolling|Zurücksetzen|revirtiendo|annulation|annullamento|revertendo|ロールバック中|롤백 중|正在回滚|正在回復
        rule|Regel|regla|règle|regola|regra|ルール|규칙|规则|規則
        rules|Regeln|reglas|règles|regole|regras|ルール|규칙|规则|規則
        safe|Sicher|seguro|sûr|sicuro|seguro|安全|안전|安全|安全
        safely|Sicher|de forma segura|en sécurité|in sicurezza|com segurança|安全に|안전하게|安全地|安全地
        safety|Sicherheit|seguridad|sécurité|sicurezza|segurança|安全性|안전|安全性|安全性
        same|Gleich|igual|identique|stesso|igual|同じ|동일|相同|相同
        sample|Beispiel|muestra|échantillon|campione|amostra|サンプル|샘플|示例|範例
        scanned|Gescannt|analizado|analysé|scansionato|examinado|スキャン済み|스캔됨|已扫描|已掃描
        score|Bewertung|puntuación|score|punteggio|pontuação|スコア|점수|分数|分數
        scroll|Scrollen|desplazarse|faire défiler|scorri|rolar|スクロール|스크롤|滚动|捲動
        second|Zweite|segundo|deuxième|secondo|segundo|2番目|두 번째|第二|第二
        seconds|Sekunden|segundos|secondes|secondi|segundos|秒|초|秒|秒
        secure|Sicher|seguro|sécurisé|sicuro|seguro|安全|보안|安全|安全
        see|Anzeigen|ver|voir|vedi|ver|参照|보기|查看|查看
        selector|Auswahl|selector|sélecteur|selettore|seletor|セレクター|선택기|选择器|選取器
        semantics|Semantik|semántica|sémantique|semantica|semântica|意味|의미|语义|語意
        sent|Gesendet|enviado|envoyé|inviato|enviado|送信済み|전송됨|已发送|已傳送
        separate|Getrennt|separado|séparé|separato|separado|分離|분리|分开|分開
        separated|Getrennt|separado|séparé|separato|separado|分離済み|분리됨|已分开|已分開
        serial|Seriennummer|serie|série|seriale|serial|シリアル|일련번호|序列号|序號
        server|Server|servidor|serveur|server|servidor|サーバー|서버|服务器|伺服器
        service|Dienst|servicio|service|servizio|serviço|サービス|서비스|服务|服務
        share|Freigeben|compartir|partager|condividi|compartilhar|共有|공유|共享|共用
        shell|Shell|intérprete|shell|shell|shell|シェル|셸|外壳|殼層
        shown|Angezeigt|mostrado|affiché|mostrato|exibido|表示済み|표시됨|已显示|已顯示
        side|Seite|lado|côté|lato|lado|側|쪽|侧|側
        sign|Zeichen|signo|signe|segno|sinal|記号|기호|符号|符號
        since|Seit|desde|depuis|da|desde|以降|이후|自|自
        single|Einzeln|único|unique|singolo|único|単一|단일|单个|單一
        smaller|Kleiner|más pequeño|plus petit|più piccolo|menor|小さい|더 작음|更小|更小
        smart|Intelligent|inteligente|intelligent|intelligente|inteligente|スマート|스마트|智能|智慧
        so|Daher|por tanto|donc|quindi|portanto|そのため|따라서|因此|因此
        some|Einige|algunos|certains|alcuni|alguns|一部|일부|一些|一些
        specialized|Spezialisiert|especializado|spécialisé|specializzato|especializado|専用|특수|专用|專用
        specific|Spezifisch|específico|spécifique|specifico|específico|特定|특정|特定|特定
        spelling|Schreibweise|ortografía|orthographe|ortografia|ortografia|表記|철자|拼写|拼字
        spellings|Schreibweisen|ortografías|orthographes|ortografie|ortografias|表記|철자|拼写|拼字
        split|Aufteilen|dividir|scinder|dividi|dividir|分割|분할|拆分|分割
        stage|Stufe|etapa|étape|fase|etapa|段階|단계|阶段|階段
        stay|Beibehalten|permanecer|rester|rimani|permanecer|維持|유지|保持|保持
        still|Weiterhin|todavía|encore|ancora|ainda|まだ|아직|仍然|仍然
        stopped|Angehalten|detenido|arrêté|arrestato|parado|停止済み|중지됨|已停止|已停止
        store|Speichern|almacenar|stocker|archivia|armazenar|保存|저장|存储|儲存
        stricter|Strenger|más estricto|plus strict|più rigoroso|mais rigoroso|厳格|더 엄격|更严格|更嚴格
        structural|Strukturell|estructural|structurel|strutturale|estrutural|構造|구조적|结构|結構
        style|Stil|estilo|style|stile|estilo|スタイル|스타일|样式|樣式
        subfolders|Unterordner|subcarpetas|sous-dossiers|sottocartelle|subpastas|サブフォルダー|하위 폴더|子文件夹|子資料夾
        submit|Übermitteln|enviar|envoyer|invia|enviar|送信|제출|提交|提交
        submitted|Übermittelt|enviado|envoyé|inviato|enviado|送信済み|제출됨|已提交|已提交
        succeeded|Erfolgreich|correcto|réussi|riuscito|bem-sucedido|成功|성공|成功|成功
        succeeds|Erfolgreich|correcto|réussit|riesce|bem-sucedido|成功|성공|成功|成功
        successful|Erfolgreich|correcto|réussi|riuscito|bem-sucedido|成功|성공|成功|成功
        successfully|Erfolgreich|correctamente|avec succès|correttamente|com sucesso|正常に|성공적으로|成功|成功
        such|Solche|tal|tel|tale|tal|そのような|그러한|此类|此類
        suggested|Vorgeschlagen|sugerido|suggéré|suggerito|sugerido|推奨|제안됨|建议|建議
        suggestion|Vorschlag|sugerencia|suggestion|suggerimento|sugestão|提案|제안|建议|建議
        suggestions|Vorschläge|sugerencias|suggestions|suggerimenti|sugestões|提案|제안|建议|建議
        supplied|Bereitgestellt|proporcionado|fourni|fornito|fornecido|提供済み|제공됨|已提供|已提供
        supply|Bereitstellen|proporcionar|fournir|fornisci|fornecer|提供|제공|提供|提供
        character tabulation|Zeichentabulator|tabulación de caracteres|tabulation de caractères|tabulazione caratteri|tabulação de caracteres|文字タブ|문자 탭|字符制表|字元定位
        take|Übernehmen|tomar|prendre|prendi|usar|取得|가져오기|采用|採用
        takes|Benötigt|requiere|nécessite|richiede|requer|必要|필요|需要|需要
        tell|Angeben|indicar|indiquer|indica|informar|指定|알림|指明|指定
        temp|Temporär|temporal|temporaire|temporaneo|temporário|一時|임시|临时|暫存
        temporary|Temporär|temporal|temporaire|temporaneo|temporário|一時|임시|临时|暫時
        terminal|Terminal|terminal|terminal|terminale|terminal|ターミナル|터미널|终端|終端機
        terms|Begriffe|términos|termes|termini|termos|用語|용어|术语|詞彙
        their|deren|sus|leurs|loro|seus|それらの|해당|其|其
        them|sie|ellos|les|li|eles|それら|해당 항목|它们|它們
        there|Dort|allí|là|lì|lá|そこ|거기|那里|那裡
        they|sie|ellos|ils|loro|eles|それら|해당 항목|它们|它們
        through|Durch|mediante|via|tramite|por meio de|経由|통해|通过|透過
        time|Zeit|tiempo|temps|tempo|tempo|時間|시간|时间|時間
        times|Mal|veces|fois|volte|vezes|回|회|次|次
        transfer|Übertragen|transferir|transférer|trasferisci|transferir|転送|전송|传输|傳輸
        transferred|Übertragen|transferido|transféré|trasferito|transferido|転送済み|전송됨|已传输|已傳輸
        transferring|Übertragung|transfiriendo|transfert|trasferimento|transferindo|転送中|전송 중|正在传输|正在傳輸
        tree|Baum|árbol|arborescence|albero|árvore|ツリー|트리|树|樹狀目錄
        trusts|Vertraut|confía|fait confiance|considera affidabile|confia|信頼|신뢰|信任|信任
        two|Zwei|dos|deux|due|dois|2つ|둘|两个|兩個
        uncommitted|Nicht bestätigt|sin confirmar|non validé|non confermato|não confirmado|未確定|커밋되지 않음|未提交|未認可
        underlying|Zugrunde liegend|subyacente|sous-jacent|sottostante|subjacente|基になる|기본|底层|基礎
        undone|Rückgängig|deshecho|annulé|annullato|desfeito|元に戻した|실행 취소됨|已撤销|已復原
        unified|Vereinheitlicht|unificado|unifié|unificato|unificado|統合|통합|统一|統一
        united|Vereinigte|unidos|unis|uniti|unidos|合衆国|미합중국|美国|美國
        unless|Sofern nicht|a menos que|sauf si|a meno che|a menos que|しない限り|아니면|除非|除非
        unlimited|Unbegrenzt|ilimitado|illimité|illimitato|ilimitado|無制限|무제한|无限制|無限制
        unlisted|Nicht aufgeführt|no listado|non répertorié|non elencato|não listado|未掲載|목록에 없음|未列出|未列出
        unsafe|Unsicher|no seguro|non sécurisé|non sicuro|inseguro|安全でない|안전하지 않음|不安全|不安全
        unsupported|Nicht unterstützt|no compatible|non pris en charge|non supportato|não compatível|非対応|지원되지 않음|不支持|不支援
        until|Bis|hasta|jusqu’à|fino a|até|まで|까지|直到|直到
        unverified|Nicht überprüft|sin verificar|non vérifié|non verificato|não verificado|未検証|확인되지 않음|未验证|未驗證
        up|Einrichten|configurar|configurer|configura|configurar|設定|설정|设置|設定
        used|Verwendet|usado|utilisé|usato|usado|使用済み|사용됨|已使用|已使用
        using|Mit|usando|avec|usando|usando|使用|사용|使用|使用
        variant|Variante|variante|variante|variante|variante|バリアント|변형|变体|變體
        variants|Varianten|variantes|variantes|varianti|variantes|バリアント|변형|变体|變體
        view|Ansicht|vista|vue|vista|exibição|ビュー|보기|视图|檢視
        viewed|Angezeigt|visto|consulté|visualizzato|visualizado|表示済み|봄|已查看|已檢視
        views|Ansichten|vistas|vues|viste|exibições|ビュー|보기|视图|檢視
        visual|Visuell|visual|visuel|visivo|visual|ビジュアル|시각적|可视化|視覺
        volume|Volume|volumen|volume|volume|volume|ボリューム|볼륨|卷|磁碟區
        vorbis|Vorbis|Vorbis|Vorbis|Vorbis|Vorbis|Vorbis|Vorbis|Vorbis|Vorbis
        want|Möchten|querer|vouloir|desidera|desejar|希望|원함|希望|希望
        web|Web|web|web|web|web|ウェブ|웹|网络|網頁
        what|Was|qué|quoi|cosa|o que|内容|무엇|什么|什麼
        where|Wo|dónde|où|dove|onde|場所|위치|位置|位置
        which|Welche|que|qui|che|que|対象|해당|其中|其中
        whose|Deren|cuyo|dont|il cui|cujo|対象の|해당|其|其
        why|Warum|por qué|pourquoi|perché|por quê|理由|이유|原因|原因
        workflow|Arbeitsablauf|flujo de trabajo|flux de travail|flusso di lavoro|fluxo de trabalho|ワークフロー|워크플로|工作流|工作流程
        writable|Beschreibbar|grabable|inscriptible|scrivibile|gravável|書き込み可能|쓰기 가능|可写|可寫入
        wrote|Geschrieben|escrito|écrit|scritto|gravado|書き込み済み|기록됨|已写入|已寫入
        you|Sie|usted|vous|tu|você|ユーザー|사용자|您|您
        zero|Null|cero|zéro|zero|zero|ゼロ|0|零|零
        re-encode|Neu codieren|recodificar|réencoder|ricodifica|recodificar|再エンコード|다시 인코딩|重新编码|重新編碼
        hyphen-minus|Bindestrich-Minus|guion menos|trait d’union-signe moins|trattino-meno|hífen-menos|ハイフンマイナス|하이픈-빼기|连字符减号|連字號減號
        option2|Option2|opción2|option2|opzione2|opção2|オプション2|옵션2|选项2|選項2
        enter|eingeben|introducir|saisir|inserisci|inserir|入力|입력|输入|輸入
        album matrix|Album-Matrix|matriz del álbum|matrice d’album|matrice album|matriz do álbum|アルバムマトリックス|앨범 행렬|专辑矩阵|專輯矩陣
        album suffix|Album-Suffix|sufijo del álbum|suffixe d’album|suffisso album|sufixo do álbum|アルバム接尾辞|앨범 접미사|专辑后缀|專輯後置詞
        codec type|Codec-Typ|tipo de códec|type de codec|tipo codec|tipo de codec|コーデックの種類|코덱 유형|编解码器类型|編解碼器類型
        android destination|Android-Ziel|destino Android|destination Android|destinazione Android|destino Android|Androidの宛先|Android 대상|Android目标|Android目的地
        local source|Lokale Quelle|origen local|source locale|origine locale|origem local|ローカルソース|로컬 원본|本地源|本機來源
        maximum dimension|Maximale Abmessung|dimensión máxima|dimension maximale|dimensione massima|dimensão máxima|最大寸法|최대 크기|最大尺寸|最大尺寸
        stable id|Stabile ID|ID estable|ID stable|ID stabile|ID estável|安定ID|안정 ID|稳定ID|穩定ID
        transport destination|Transportziel|destino de transporte|destination de transport|destinazione di trasporto|destino de transporte|転送先|전송 대상|传输目标|傳輸目的地
        source disposition|Quelldisposition|disposición de origen|disposition de la source|disposizione origine|disposição da origem|ソース処理|원본 처리|源处置|來源處置
        original album|Originalalbum|álbum original|album original|album originale|álbum original|オリジナルアルバム|원본 앨범|原始专辑|原始專輯
        original date|Originaldatum|fecha original|date originale|data originale|data original|元の日付|원래 날짜|原始日期|原始日期
        band logo|Bandlogo|logotipo de la banda|logo du groupe|logo della band|logotipo da banda|バンドロゴ|밴드 로고|乐队徽标|樂團標誌
        studio logo|Studiologo|logotipo del estudio|logo du studio|logo dello studio|logotipo do estúdio|スタジオロゴ|스튜디오 로고|工作室徽标|錄音室標誌
        offline cache|Offline-Cache|caché sin conexión|cache hors ligne|cache offline|cache offline|オフラインキャッシュ|오프라인 캐시|离线缓存|離線快取
        id3 tag version|ID3-Tag-Version|versión de etiqueta ID3|version de balise ID3|versione tag ID3|versão da etiqueta ID3|ID3タグバージョン|ID3 태그 버전|ID3标签版本|ID3標籤版本
        maximum bitrate|Maximale Bitrate|tasa de bits máxima|débit binaire maximal|bitrate massimo|taxa de bits máxima|最大ビットレート|최대 비트 전송률|最大比特率|最大位元率
        compilation token|Kompilations-Token|token de recopilación|jeton de compilation|token compilation|token de compilação|コンピレーショントークン|컴필레이션 토큰|合辑令牌|合輯權杖
        legacy musiclibrarytools|Früheres MusicLibraryTools|MusicLibraryTools heredado|ancien MusicLibraryTools|MusicLibraryTools precedente|MusicLibraryTools legado|従来のMusicLibraryTools|레거시 MusicLibraryTools|旧版MusicLibraryTools|舊版MusicLibraryTools
        dj mixer|DJ-Mischer|mezclador DJ|mixeur DJ|mixer DJ|mixer de DJ|DJミキサー|DJ 믹서|DJ混音器|DJ混音器
        musicbrainz disc id|MusicBrainz-Disc-ID|ID de disco de MusicBrainz|ID de disque MusicBrainz|ID disco MusicBrainz|ID de disco do MusicBrainz|MusicBrainzディスクID|MusicBrainz 디스크 ID|MusicBrainz光盘ID|MusicBrainz光碟ID
        musicbrainz original album id|MusicBrainz-ID des Originalalbums|ID del álbum original de MusicBrainz|ID d’album original MusicBrainz|ID album originale MusicBrainz|ID do álbum original do MusicBrainz|MusicBrainzオリジナルアルバムID|MusicBrainz 원본 앨범 ID|MusicBrainz原始专辑ID|MusicBrainz原始專輯ID
        musicbrainz album id|MusicBrainz-Album-ID|ID de álbum de MusicBrainz|ID d’album MusicBrainz|ID album MusicBrainz|ID de álbum do MusicBrainz|MusicBrainzアルバムID|MusicBrainz 앨범 ID|MusicBrainz专辑ID|MusicBrainz專輯ID
        archive id|Archiv-ID|ID de archivo|ID d’archive|ID archivio|ID de arquivo|アーカイブID|보관 ID|存档ID|封存ID
        music|Musik|música|musique|musica|música|音楽|음악|音乐|音樂
        rock|Rockmusik|rock|rock|rock|rock|ロック|록|摇滚|搖滾
        kept|Beibehalten|conservado|conservé|mantenuto|mantido|保持|유지됨|已保留|已保留
        beta|Beta|Beta|Bêta|Beta|Beta|ベータ|베타|测试版|測試版
        """
        .Replace("\r", "", StringComparison.Ordinal);
}
