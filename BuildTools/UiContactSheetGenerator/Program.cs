using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkiaSharp;

return await ContactSheetGenerator.RunAsync(args);

internal static partial class ContactSheetGenerator
{
    private const int ExpectedWorkbenchFrames = 1080;
    private const int ExpectedCrossApplicationFrames = 1200;
    private const int ExpectedRepresentativeStateFrames = 1320;
    private const int TileWidth = 360;
    private const int ImageHeight = 240;
    private const int CaptionHeight = 70;
    private const int CellPadding = 8;
    private const int Columns = 4;

    private static readonly string[] WorkbenchSections =
    [
        "Session",
        "BulkOperation",
        "AllFields",
        "Files",
        "OnlineMetadata",
        "Reports",
        "Playlists",
        "Tools",
        "Shortcuts",
    ];

    private static readonly string[] ShellDestinations =
    [
        "Home",
        "Library",
        "Workbench",
        "Health",
        "Ingest",
        "Organize",
        "Devices",
        "Operations",
        "Settings",
        "About",
    ];

    private static readonly string[] RepresentativeStates =
    [
        "configured-empty",
        "populated",
        "selected",
        "dirty-pending",
        "busy",
        "validation-error",
        "unavailable-configuration",
        "unavailable-tool",
        "menu-open",
        "drawer-open",
        "dialog",
    ];

    private static readonly string[] ShippingCultures =
    [
        "en-US",
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

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            Options options = Options.Parse(args);
            ValidateInputs(options);

            string captureDirectory = Path.GetFullPath(
                options.CaptureDirectory);
            string outputDirectory = Path.GetFullPath(
                options.OutputDirectory);
            Directory.CreateDirectory(outputDirectory);

            FrameSet frameSet = LoadFrameSet(captureDirectory);
            List<SheetGroup> groups = BuildGroups(frameSet);
            var sheets = new List<SheetManifest>(groups.Count);
            var outputNames = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            int ordinal = 0;
            foreach (SheetGroup group in groups)
            {
                ordinal++;
                string outputName =
                    $"{ordinal:000}-{Sanitize(group.Kind)}-{Sanitize(group.Identity)}.png";
                if (!outputNames.Add(outputName))
                {
                    throw new InvalidDataException(
                        $"Contact-sheet output identity collided: {outputName}");
                }

                string outputPath = Path.Combine(
                    outputDirectory,
                    outputName);
                RenderSheet(group, outputPath);
                sheets.Add(
                    new SheetManifest(
                        outputName,
                        group.Kind,
                        group.Identity,
                        Sha256(outputPath),
                        group.Frames
                            .Select(frame =>
                                new FrameManifest(
                                    Path.GetFileName(frame),
                                    Sha256(frame)))
                            .ToArray()));
            }

            var manifest = new ContactSheetManifest(
                1,
                "UiContactSheetGenerator/1.0",
                options.SourceSha.ToLowerInvariant(),
                DateTimeOffset.UtcNow,
                new FrameCounts(
                    frameSet.Workbench.Length,
                    frameSet.CrossApplication.Length,
                    frameSet.RepresentativeStates.Length,
                    frameSet.LocaleMinimumSize.Length,
                    frameSet.Supplemental.Length),
                sheets);
            string manifestPath = Path.Combine(
                outputDirectory,
                "contact-sheets.manifest.json");
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(
                    manifest,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true,
                    }) + Environment.NewLine,
                new UTF8Encoding(false));
            string manifestHash = Sha256(manifestPath);
            await WriteReviewTemplateAsync(
                outputDirectory,
                options.SourceSha.ToLowerInvariant(),
                manifestHash,
                sheets);

            Console.WriteLine(
                $"Generated {sheets.Count} captioned contact sheets from " +
                $"{frameSet.RequiredCount} required and " +
                $"{frameSet.LocaleMinimumSize.Length + frameSet.Supplemental.Length} supplemental frames.");
            Console.WriteLine($"Manifest SHA-256: {manifestHash}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static void ValidateInputs(Options options)
    {
        if (!Directory.Exists(options.CaptureDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Capture directory does not exist: {options.CaptureDirectory}");
        }

        if (!ShaPattern().IsMatch(options.SourceSha))
        {
            throw new ArgumentException(
                "--source-sha must be the exact 40-character Git commit SHA.");
        }

        string capture = Path.GetFullPath(options.CaptureDirectory)
            .TrimEnd(Path.DirectorySeparatorChar);
        string output = Path.GetFullPath(options.OutputDirectory)
            .TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(
                capture,
                output,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Capture and output directories must be different.");
        }

        if (Directory.Exists(output) &&
            Directory.EnumerateFileSystemEntries(output).Any())
        {
            throw new IOException(
                $"Output directory must be empty so evidence cannot be mixed: {output}");
        }
    }

    private static FrameSet LoadFrameSet(string captureDirectory)
    {
        string[] pngs = Directory
            .EnumerateFiles(
                captureDirectory,
                "*.png",
                SearchOption.TopDirectoryOnly)
            .OrderBy(
                path => Path.GetFileName(path),
                StringComparer.Ordinal)
            .ToArray();
        string[] workbench = WithPrefix(
            pngs,
            "workbench-matrix-");
        string[] crossApplication = WithPrefix(
            pngs,
            "cross-app-matrix-");
        string[] representativeStates = WithPrefix(
            pngs,
            "representative-state-");

        RequireCount(
            "Workbench matrix",
            workbench,
            ExpectedWorkbenchFrames);
        RequireCount(
            "cross-application matrix",
            crossApplication,
            ExpectedCrossApplicationFrames);
        RequireCount(
            "representative-state matrix",
            representativeStates,
            ExpectedRepresentativeStateFrames);

        var required = workbench
            .Concat(crossApplication)
            .Concat(representativeStates)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] localeMinimumSize = pngs
            .Where(path => IsLocaleMinimumSizeFrame(
                Path.GetFileName(path)))
            .ToArray();
        foreach (string culture in ShippingCultures)
        {
            int count = localeMinimumSize.Count(
                path => Path.GetFileName(path).StartsWith(
                    $"workbench-{culture}-",
                    StringComparison.OrdinalIgnoreCase));
            if (count < WorkbenchSections.Length * 2)
            {
                throw new InvalidDataException(
                    $"Culture {culture} has {count} dedicated minimum-size " +
                    $"Workbench frames; at least {WorkbenchSections.Length * 2} are required.");
            }
        }

        var localeSet = localeMinimumSize.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        string[] supplemental = pngs
            .Where(path =>
                !required.Contains(path) &&
                !localeSet.Contains(path))
            .ToArray();
        return new FrameSet(
            workbench,
            crossApplication,
            representativeStates,
            localeMinimumSize,
            supplemental);
    }

    private static List<SheetGroup> BuildGroups(FrameSet frames)
    {
        var groups = new List<SheetGroup>();
        groups.AddRange(
            GroupByTrailingIdentity(
                "workbench",
                frames.Workbench,
                WorkbenchSections));
        groups.AddRange(
            GroupByTrailingIdentity(
                "cross-application",
                frames.CrossApplication,
                ShellDestinations));
        groups.AddRange(
            GroupRepresentativeStates(
                frames.RepresentativeStates));

        foreach (string culture in ShippingCultures)
        {
            string[] cultureFrames = frames.LocaleMinimumSize
                .Where(path => Path.GetFileName(path).StartsWith(
                    $"workbench-{culture}-",
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(
                    path => Path.GetFileName(path),
                    StringComparer.Ordinal)
                .ToArray();
            groups.Add(
                new SheetGroup(
                    "shipping-locale",
                    culture,
                    cultureFrames));
        }

        groups.AddRange(
            Chunk(
                "supplemental",
                frames.Supplemental,
                Columns * 3));
        return groups;
    }

    private static IEnumerable<SheetGroup> GroupByTrailingIdentity(
        string kind,
        IEnumerable<string> paths,
        IReadOnlyCollection<string> identities)
    {
        var groups = paths.GroupBy(
            path =>
            {
                string name = Path.GetFileNameWithoutExtension(path);
                string identity = identities.Single(
                    value => name.EndsWith(
                        $"-{value}",
                        StringComparison.Ordinal));
                return name[..^(identity.Length + 1)];
            },
            StringComparer.Ordinal);
        foreach (IGrouping<string, string> group in groups.OrderBy(
                     group => group.Key,
                     StringComparer.Ordinal))
        {
            string[] ordered = identities
                .Select(identity => group.Single(path =>
                    Path.GetFileNameWithoutExtension(path).EndsWith(
                        $"-{identity}",
                        StringComparison.Ordinal)))
                .ToArray();
            yield return new SheetGroup(kind, group.Key, ordered);
        }
    }

    private static IEnumerable<SheetGroup> GroupRepresentativeStates(
        IEnumerable<string> paths)
    {
        var grouped = new Dictionary<string, Dictionary<string, string>>(
            StringComparer.Ordinal);
        foreach (string path in paths)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            string remainder = name["representative-state-".Length..];
            string state = RepresentativeStates.Single(value =>
                remainder.StartsWith(
                    value + "-",
                    StringComparison.Ordinal));
            string presentation = remainder[(state.Length + 1)..];
            if (!grouped.TryGetValue(
                    presentation,
                    out Dictionary<string, string>? byState))
            {
                byState = new Dictionary<string, string>(
                    StringComparer.Ordinal);
                grouped.Add(presentation, byState);
            }

            byState.Add(state, path);
        }

        foreach ((string presentation, Dictionary<string, string> byState)
                 in grouped.OrderBy(
                     item => item.Key,
                     StringComparer.Ordinal))
        {
            yield return new SheetGroup(
                "representative-state",
                presentation,
                RepresentativeStates
                    .Select(state => byState[state])
                    .ToArray());
        }
    }

    private static IEnumerable<SheetGroup> Chunk(
        string kind,
        IReadOnlyList<string> paths,
        int size)
    {
        for (int offset = 0; offset < paths.Count; offset += size)
        {
            yield return new SheetGroup(
                kind,
                $"{offset / size + 1:000}",
                paths.Skip(offset).Take(size).ToArray());
        }
    }

    private static void RenderSheet(
        SheetGroup group,
        string outputPath)
    {
        int rows = (group.Frames.Count + Columns - 1) / Columns;
        int cellWidth = TileWidth + CellPadding * 2;
        int cellHeight =
            ImageHeight + CaptionHeight + CellPadding * 2;
        int sheetWidth = cellWidth * Columns;
        int sheetHeight = Math.Max(1, rows) * cellHeight;
        using var bitmap = new SKBitmap(
            sheetWidth,
            sheetHeight,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(245, 247, 250));
        using var borderPaint = new SKPaint
        {
            Color = new SKColor(190, 197, 208),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
        };
        using var captionPaint = new SKPaint
        {
            Color = new SKColor(22, 29, 40),
            IsAntialias = true,
        };
        using var captionFont = new SKFont(
            SKTypeface.Default,
            13);
        var sampling = new SKSamplingOptions(
            SKFilterMode.Linear,
            SKMipmapMode.None);

        for (int index = 0; index < group.Frames.Count; index++)
        {
            int column = index % Columns;
            int row = index / Columns;
            float left = column * cellWidth + CellPadding;
            float top = row * cellHeight + CellPadding;
            using SKBitmap? frame = SKBitmap.Decode(
                group.Frames[index]);
            if (frame is null)
            {
                throw new InvalidDataException(
                    $"Could not decode PNG: {group.Frames[index]}");
            }

            SKRect destination = Fit(
                frame.Width,
                frame.Height,
                left,
                top,
                TileWidth,
                ImageHeight);
            canvas.DrawBitmap(
                frame,
                destination,
                sampling,
                null);
            canvas.DrawRect(
                new SKRect(
                    left,
                    top,
                    left + TileWidth,
                    top + ImageHeight),
                borderPaint);
            DrawCaption(
                canvas,
                Path.GetFileName(group.Frames[index]),
                left,
                top + ImageHeight + 18,
                TileWidth,
                captionFont,
                captionPaint);
        }

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(
            SKEncodedImageFormat.Png,
            100);
        using FileStream stream = File.Create(outputPath);
        data.SaveTo(stream);
        stream.Flush(true);
    }

    private static SKRect Fit(
        int sourceWidth,
        int sourceHeight,
        float left,
        float top,
        float targetWidth,
        float targetHeight)
    {
        float scale = Math.Min(
            targetWidth / sourceWidth,
            targetHeight / sourceHeight);
        float width = sourceWidth * scale;
        float height = sourceHeight * scale;
        float x = left + (targetWidth - width) / 2;
        float y = top + (targetHeight - height) / 2;
        return new SKRect(x, y, x + width, y + height);
    }

    private static void DrawCaption(
        SKCanvas canvas,
        string caption,
        float left,
        float baseline,
        float width,
        SKFont font,
        SKPaint paint)
    {
        const int maxLines = 3;
        var lines = new List<string>();
        string remaining = caption;
        for (int line = 0;
             line < maxLines && remaining.Length > 0;
             line++)
        {
            int take = LargestFittingPrefix(
                remaining,
                width,
                font,
                paint);
            if (take <= 0)
                break;
            bool truncated = line == maxLines - 1 &&
                             take < remaining.Length;
            string value = remaining[..take].Trim();
            if (truncated)
            {
                while (value.Length > 0 &&
                       font.MeasureText(
                           value + "\u2026",
                           paint) > width)
                {
                    value = value[..^1];
                }

                value += "\u2026";
            }

            lines.Add(value);
            remaining = remaining[take..].TrimStart();
            if (truncated)
                break;
        }

        for (int index = 0; index < lines.Count; index++)
        {
            canvas.DrawText(
                lines[index],
                left,
                baseline + index * 18,
                SKTextAlign.Left,
                font,
                paint);
        }
    }

    private static int LargestFittingPrefix(
        string value,
        float width,
        SKFont font,
        SKPaint paint)
    {
        if (font.MeasureText(value, paint) <= width)
            return value.Length;
        int best = 0;
        int cursor = 0;
        while (cursor < value.Length)
        {
            int next = value.IndexOfAny(
                ['-', '_', ' '],
                cursor + 1);
            if (next < 0)
                next = value.Length;
            else
                next++;
            if (font.MeasureText(
                    value[..next],
                    paint) > width)
                break;
            best = next;
            cursor = next;
        }

        if (best > 0)
            return best;
        for (int length = 1; length <= value.Length; length++)
        {
            if (font.MeasureText(
                    value[..length],
                    paint) > width)
                return Math.Max(1, length - 1);
        }

        return value.Length;
    }

    private static async Task WriteReviewTemplateAsync(
        string outputDirectory,
        string sourceSha,
        string manifestHash,
        IReadOnlyList<SheetManifest> sheets)
    {
        var builder = new StringBuilder()
            .AppendLine("# GUI modernization visual review")
            .AppendLine()
            .AppendLine($"- Source SHA: `{sourceSha}`")
            .AppendLine($"- Manifest SHA-256: `{manifestHash}`")
            .AppendLine("- Reviewer:")
            .AppendLine("- Review date:")
            .AppendLine("- Result: Pending human review")
            .AppendLine()
            .AppendLine(
                "Review every captioned tile for clipping, overlap, inaccessible " +
                "actions, missing glyphs, inconsistent hierarchy, and incorrect " +
                "responsive presentation. Record every finding and recapture after fixes.")
            .AppendLine()
            .AppendLine("## Contact sheets")
            .AppendLine();
        foreach (SheetManifest sheet in sheets)
        {
            builder.AppendLine(
                $"- [ ] `{sheet.FileName}` — {sheet.Kind}: {sheet.Identity}");
        }

        builder
            .AppendLine()
            .AppendLine("## Findings")
            .AppendLine()
            .AppendLine("| Sheet and tile | Finding | Resolution/commit | Recaptured |")
            .AppendLine("|---|---|---|---|")
            .AppendLine()
            .AppendLine("## Sign-off")
            .AppendLine()
            .AppendLine(
                "I reviewed every sheet listed above against the exact source SHA " +
                "and manifest and confirmed all findings were resolved or explicitly accepted.")
            .AppendLine()
            .AppendLine("- Reviewer signature/name:")
            .AppendLine("- Date:");
        await File.WriteAllTextAsync(
            Path.Combine(
                outputDirectory,
                "REVIEW.md"),
            builder.ToString(),
            new UTF8Encoding(false));
    }

    private static string[] WithPrefix(
        IEnumerable<string> paths,
        string prefix) =>
        paths.Where(path => Path.GetFileName(path).StartsWith(
                prefix,
                StringComparison.Ordinal))
            .ToArray();

    private static bool IsLocaleMinimumSizeFrame(
        string fileName) =>
        ShippingCultures.Any(culture =>
            fileName.StartsWith(
                $"workbench-{culture}-",
                StringComparison.OrdinalIgnoreCase)) &&
        WorkbenchSections.Any(section =>
            fileName.EndsWith(
                $"-{section}.png",
                StringComparison.Ordinal));

    private static void RequireCount(
        string name,
        IReadOnlyCollection<string> paths,
        int expected)
    {
        if (paths.Count != expected)
        {
            throw new InvalidDataException(
                $"{name} contains {paths.Count} frames; expected exactly {expected}.");
        }

        int unique = paths
            .Select(Path.GetFileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (unique != expected)
        {
            throw new InvalidDataException(
                $"{name} contains duplicate case-insensitive file identities.");
        }
    }

    private static string Sanitize(string value)
    {
        string sanitized = NonFileNameCharacterPattern()
            .Replace(value, "-")
            .Trim('-');
        return sanitized.Length <= 120
            ? sanitized
            : sanitized[..120];
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(
                SHA256.HashData(stream))
            .ToLowerInvariant();
    }

    [GeneratedRegex("^[0-9a-fA-F]{40}$")]
    private static partial Regex ShaPattern();

    [GeneratedRegex("[^A-Za-z0-9._-]+")]
    private static partial Regex NonFileNameCharacterPattern();

    private sealed record Options(
        string CaptureDirectory,
        string OutputDirectory,
        string SourceSha)
    {
        public static Options Parse(IReadOnlyList<string> args)
        {
            var values = new Dictionary<string, string>(
                StringComparer.Ordinal);
            for (int index = 0; index < args.Count; index++)
            {
                string argument = args[index];
                if (argument is not (
                    "--capture-directory" or
                    "--output-directory" or
                    "--source-sha"))
                {
                    throw new ArgumentException(
                        $"Unknown argument: {argument}");
                }

                if (index + 1 >= args.Count ||
                    args[index + 1].StartsWith(
                        "--",
                        StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Missing value for {argument}.");
                }

                if (!values.TryAdd(argument, args[++index]))
                {
                    throw new ArgumentException(
                        $"Duplicate argument: {argument}");
                }
            }

            return new Options(
                Required(values, "--capture-directory"),
                Required(values, "--output-directory"),
                Required(values, "--source-sha"));
        }

        private static string Required(
            IReadOnlyDictionary<string, string> values,
            string name) =>
            values.TryGetValue(name, out string? value) &&
            !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new ArgumentException(
                    $"Required argument is missing: {name}");
    }

    private sealed record FrameSet(
        string[] Workbench,
        string[] CrossApplication,
        string[] RepresentativeStates,
        string[] LocaleMinimumSize,
        string[] Supplemental)
    {
        public int RequiredCount =>
            Workbench.Length +
            CrossApplication.Length +
            RepresentativeStates.Length;
    }

    private sealed record SheetGroup(
        string Kind,
        string Identity,
        IReadOnlyList<string> Frames);

    private sealed record ContactSheetManifest(
        int Version,
        string Generator,
        string SourceSha,
        DateTimeOffset GeneratedAtUtc,
        FrameCounts Counts,
        IReadOnlyList<SheetManifest> Sheets);

    private sealed record FrameCounts(
        int Workbench,
        int CrossApplication,
        int RepresentativeStates,
        int LocaleMinimumSize,
        int Supplemental);

    private sealed record SheetManifest(
        string FileName,
        string Kind,
        string Identity,
        string Sha256,
        IReadOnlyList<FrameManifest> Frames);

    private sealed record FrameManifest(
        string FileName,
        string Sha256);
}
