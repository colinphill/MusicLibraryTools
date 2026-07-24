using System.Collections.Immutable;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MusicFileUtilities;
using MusicLibrary.Core;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class ReportExportServiceTests
{
    public static TheoryData<ReportFormat, ReportEncoding>
        FormatAndEncodingCases
    {
        get
        {
            var cases =
                new TheoryData<ReportFormat, ReportEncoding>();
            foreach (ReportFormat format in
                     Enum.GetValues<ReportFormat>())
            foreach (ReportEncoding encoding in
                     Enum.GetValues<ReportEncoding>())
                cases.Add(format, encoding);
            return cases;
        }
    }

    [Fact]
    public void CsvRendererQuotesValuesAndNeutralizesFormulas()
    {
        ReportRenderRequest request = RenderRequest(
            ("Simple", "text"),
            ("Comma", "one,two"),
            ("Quote", "say \"hello\""),
            ("Formula", "=HYPERLINK(\"bad\")"));

        string result = new CsvReportRenderer().Render(request);

        Assert.Equal(
            "Field\r\n" +
            "text\r\n" +
            "\"one,two\"\r\n" +
            "\"say \"\"hello\"\"\"\r\n" +
            "\"'=HYPERLINK(\"\"bad\"\")\"\r\n",
            result);
    }

    [Fact]
    public void HtmlRendererEncodesLabelsAndValues()
    {
        ReportRenderRequest request = new(
            [ReportFieldDescriptor.File("FileName", "<File>")],
            [
                new(
                    "song.flac",
                    new Dictionary<string, string>
                    {
                        ["file.FileName"] =
                            "<script>alert('no')</script>",
                    }),
            ]);

        string result = new HtmlReportRenderer().Render(request);

        Assert.Contains("&lt;File&gt;", result);
        Assert.Contains(
            "&lt;script&gt;alert(&#39;no&#39;)&lt;/script&gt;",
            result);
        Assert.DoesNotContain("<script>", result);
    }

    [Fact]
    public void RtfRendererEscapesControlCharactersAndUnicode()
    {
        ReportRenderRequest request = RenderRequest(
            ("Value", @"brace {\} café"));

        string result = new RtfReportRenderer().Render(request);

        Assert.Contains(@"brace \{\\\}", result);
        Assert.Contains(@"\u233?", result);
        Assert.StartsWith(@"{\rtf1", result);
    }

    [Fact]
    public void TextRendererUsesColumnsWithoutMultilineInjection()
    {
        ReportRenderRequest request = RenderRequest(
            ("Value", "one\ttwo\r\nthree"));

        string result = new TextReportRenderer().Render(request);

        Assert.Equal(
            $"Field{Environment.NewLine}" +
            $"one two  three{Environment.NewLine}",
            result);
    }

    [Fact]
    public async Task PreviewReadsSortsGroupsAndRendersDocuments()
    {
        using var temp = new TempDirectory();
        string first = Path.Combine(temp.Path, "track10.flac");
        string second = Path.Combine(temp.Path, "track2.flac");
        var documents = new FakeDocuments(
            Document(
                first,
                artist: "A/B",
                title: "Track 10",
                customValue: "First",
                bitrate: 1000),
            Document(
                second,
                artist: "A/B",
                title: "Track 2",
                customValue: "Second",
                bitrate: 900));
        var executor = new RecordingExecutor();
        var service = CreateService(documents, executor);
        ImmutableArray<ReportFieldDescriptor> fields =
        [
            ReportFieldDescriptor.Known(TagFields.Artist),
            ReportFieldDescriptor.Known(TagFields.Title),
            ReportFieldDescriptor.Custom("CUSTOM"),
            ReportFieldDescriptor.File("FileName"),
            ReportFieldDescriptor.Technical("Bitrate"),
        ];
        var configuration = new ReportConfiguration(
            "By artist",
            ReportFormat.Csv,
            Path.Combine(temp.Path, "reports"),
            fields,
            [new(fields[1].Id, ReportSortType.Natural)],
            GroupByFieldId: fields[0].Id,
            OneFilePerGroup: true,
            GroupFileNameTemplate: "{Group}.{Format}",
            Encoding: ReportEncoding.Utf8WithBom);
        var reports = new List<OperationProgress>();

        ReportExportPlan plan = await service.PreviewAsync(
            new([first, second], configuration),
            new SynchronousProgress<OperationProgress>(reports.Add),
            TestContext.Current.CancellationToken);

        Assert.True(plan.CanApply);
        ReportFilePlan file = Assert.Single(plan.Files);
        Assert.Equal("A/B", file.Group);
        Assert.Equal(2, file.RowCount);
        Assert.Equal(
            Path.Combine(temp.Path, "reports", "A_B.csv"),
            file.DestinationPath);
        FileMutationAction action =
            Assert.Single(plan.MutationPlan.Actions);
        Assert.Equal(FileMutationKind.Write, action.Kind);
        byte[] bytes = action.Content.ToArray();
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        string csv = Encoding.UTF8.GetString(
            bytes.AsSpan(Encoding.UTF8.Preamble.Length));
        Assert.True(
            csv.IndexOf("Track 2", StringComparison.Ordinal) <
            csv.IndexOf("Track 10", StringComparison.Ordinal));
        Assert.Contains("Second", csv);
        Assert.Contains("track2.flac", csv);
        Assert.Contains("900", csv);
        Assert.Equal(OperationPhase.Completed, reports[^1].Phase);
    }

    [Theory]
    [MemberData(nameof(FormatAndEncodingCases))]
    public async Task PreviewProducesGoldenBytesForEveryFormatAndEncoding(
        ReportFormat format,
        ReportEncoding encoding)
    {
        using var temp = new TempDirectory();
        string source = Path.Combine(temp.Path, "source.flac");
        var service = CreateService(
            new FakeDocuments(Document(source, title: "Café")),
            new RecordingExecutor());
        var configuration = new ReportConfiguration(
            "Golden",
            format,
            Path.Combine(temp.Path, "report"),
            [ReportFieldDescriptor.Known(TagFields.Title)],
            Encoding: encoding);

        ReportExportPlan plan = await service.PreviewAsync(
            new([source], configuration),
            ct: TestContext.Current.CancellationToken);

        FileMutationAction action =
            Assert.Single(plan.MutationPlan.Actions);
        Assert.Equal(
            Encode(ExpectedReport(format), encoding),
            action.Content.ToArray());
    }

    [Fact]
    public async Task PreviewCapturesExistingOutputForRecoverableReplacement()
    {
        using var temp = new TempDirectory();
        string source = Path.Combine(temp.Path, "source.flac");
        string output = Path.Combine(temp.Path, "report.txt");
        await File.WriteAllTextAsync(
            output,
            "old",
            TestContext.Current.CancellationToken);
        var service = CreateService(
            new FakeDocuments(Document(source)),
            new RecordingExecutor());
        var configuration = new ReportConfiguration(
            "Report",
            ReportFormat.Text,
            output,
            [ReportFieldDescriptor.Known(TagFields.Title)]);

        ReportExportPlan plan = await service.PreviewAsync(
            new([source], configuration),
            ct: TestContext.Current.CancellationToken);

        FileMutationAction action =
            Assert.Single(plan.MutationPlan.Actions);
        Assert.Equal(FileMutationKind.ReplaceGenerated, action.Kind);
        Assert.True(action.ExpectedDestination!.Exists);
        Assert.True(plan.MutationPlan.RetainRecovery);
    }

    [Fact]
    public async Task InvalidConfigurationReturnsBlockingPreview()
    {
        var service = CreateService(
            new FakeDocuments(),
            new RecordingExecutor());
        var configuration = new ReportConfiguration(
            "",
            ReportFormat.Csv,
            "",
            []);

        ReportExportPlan plan = await service.PreviewAsync(
            new([], configuration),
            ct: TestContext.Current.CancellationToken);

        Assert.False(plan.CanApply);
        Assert.Empty(plan.Files);
        Assert.Contains(plan.Issues, issue =>
            issue.Code == "report-sources-empty");
        Assert.Contains(plan.Issues, issue =>
            issue.Code == "report-fields-empty");
    }

    [Fact]
    public async Task InvalidOutputPathReturnsBlockedPlanInsteadOfThrowing()
    {
        var service = CreateService(
            new FakeDocuments(Document("source.flac")),
            new RecordingExecutor());
        var configuration = new ReportConfiguration(
            "Report",
            ReportFormat.Csv,
            "\0",
            [ReportFieldDescriptor.Known(TagFields.Title)]);

        ReportExportPlan plan = await service.PreviewAsync(
            new(["source.flac"], configuration),
            ct: TestContext.Current.CancellationToken);

        Assert.False(plan.CanApply);
        Assert.Contains(plan.Issues, issue =>
            issue.Code == "report-output-invalid");
    }

    [Fact]
    public async Task PreviewAndApplyObserveProgressAndCancellation()
    {
        using var temp = new TempDirectory();
        string source = Path.Combine(temp.Path, "source.flac");
        var documents = new FakeDocuments(Document(source));
        var executor = new RecordingExecutor();
        var service = CreateService(documents, executor);
        var configuration = new ReportConfiguration(
            "Report",
            ReportFormat.Text,
            Path.Combine(temp.Path, "report.txt"),
            [ReportFieldDescriptor.Known(TagFields.Title)]);
        ReportExportPlan plan = await service.PreviewAsync(
            new([source], configuration),
            ct: TestContext.Current.CancellationToken);

        ReportExportResult result = await service.ApplyAsync(
            plan,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.FileCount);
        Assert.Same(plan.MutationPlan, executor.Plan);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.PreviewAsync(
                new([source], configuration),
                ct: cancellation.Token));
    }

    [Fact]
    public void ServiceRegistrationIncludesAllRenderers()
    {
        var services =
            new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddMusicLibraryCore();
        using Microsoft.Extensions.DependencyInjection.ServiceProvider
            provider = services.BuildServiceProvider();

        Assert.IsType<ReportExportService>(
            provider.GetRequiredService<IReportExportService>());
        Assert.Equal(
            Enum.GetValues<ReportFormat>(),
            provider.GetServices<IReportRenderer>()
                .Select(renderer => renderer.Format)
                .Order()
                .ToArray());
    }

    private static ReportExportService CreateService(
        IMetadataDocumentService documents,
        IFileMutationPlanExecutor executor) =>
        new(
            documents,
            executor,
            [
                new TextReportRenderer(),
                new CsvReportRenderer(),
                new HtmlReportRenderer(),
                new RtfReportRenderer(),
            ]);

    private static ReportRenderRequest RenderRequest(
        params (string Source, string Value)[] values)
    {
        ReportFieldDescriptor field =
            ReportFieldDescriptor.File("FileName", "Field");
        return new(
            [field],
            values.Select(value => new ReportRow(
                value.Source,
                new Dictionary<string, string>
                {
                    [field.Id] = value.Value,
                })).ToArray());
    }

    private static string ExpectedReport(ReportFormat format) =>
        format switch
        {
            ReportFormat.Text =>
                "Title" + Environment.NewLine +
                "Café" + Environment.NewLine,
            ReportFormat.Csv =>
                "Title\r\nCafé\r\n",
            ReportFormat.Html =>
                "<!doctype html>\n<html><head><meta charset=\"utf-8\">" +
                "<title>Music library report</title></head><body><table>\n" +
                "<thead><tr><th>Title</th></tr></thead>\n<tbody>\n" +
                "<tr><td>Caf&#233;</td></tr>\n</tbody>\n" +
                "</table></body></html>\n",
            ReportFormat.Rtf =>
                @"{\rtf1\ansi\deff0" + Environment.NewLine +
                @"\b Title\b0 \par" + Environment.NewLine +
                @"Caf\u233?\par" + Environment.NewLine +
                "}",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    private static byte[] Encode(
        string text,
        ReportEncoding encoding)
    {
        Encoding selected = encoding switch
        {
            ReportEncoding.Utf8 => new UTF8Encoding(false),
            ReportEncoding.Utf8WithBom => new UTF8Encoding(true),
            ReportEncoding.Utf16LittleEndian =>
                new UnicodeEncoding(false, true),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
        };
        return [.. selected.GetPreamble(), .. selected.GetBytes(text)];
    }

    private static MediaDocument Document(
        string path,
        string artist = "Artist",
        string title = "Title",
        string customValue = "Custom",
        uint bitrate = 1000)
    {
        ImmutableArray<MetadataValueSet> fields =
        [
            new(
                MetadataFieldKey.Known(TagFields.Artist),
                [artist]),
            new(
                MetadataFieldKey.Known(TagFields.Title),
                [title]),
            new(
                MetadataFieldKey.Custom("CUSTOM"),
                [customValue]),
        ];
        return new(
            Path.GetFullPath(path),
            [new("Test", fields, true, true, true, true)],
            [],
            new CodecModel
            {
                CodecName = "FLAC",
                AverageBitrate = bitrate,
                Samplerate = 44100,
                Channels = 2,
                DurationInSeconds = 180,
            },
            new(
                Path.GetFullPath(path),
                123,
                DateTime.UnixEpoch,
                "hash"),
            true);
    }

    private sealed class FakeDocuments(
        params MediaDocument[] documents) :
        IMetadataDocumentService
    {
        private readonly Dictionary<string, MediaDocument> _documents =
            documents.ToDictionary(
                document => document.Path,
                PathComparer);

        public Task<MediaDocument> LoadAsync(
            string path,
            bool includeArtwork = true,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                _documents[Path.GetFullPath(path)]);
        }
    }

    private sealed class RecordingExecutor : IFileMutationPlanExecutor
    {
        public FileMutationPlan? Plan { get; private set; }

        public Task<FileMutationSummary> ApplyAsync(
            FileMutationPlan plan,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Plan = plan;
            progress?.Report(new(
                OperationPhase.Completed,
                plan.Actions.Count,
                plan.Actions.Count));
            return Task.FromResult(new FileMutationSummary(
                0,
                plan.Actions.Count,
                0,
                0,
                null,
                []));
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "mlm-report-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
