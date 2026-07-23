using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class DelimitedMetadataImportServiceTests
{
    [Fact]
    public async Task CsvMapsQuotedRepeatedAndCustomColumnsToOrderedEdits()
    {
        using var session = new TempDirectory();
        string first = Path.Combine(session.Path, "one.flac");
        string second = Path.Combine(session.Path, "two.flac");
        string source = Path.Combine(session.Path, "metadata.csv");
        await File.WriteAllTextAsync(
            source,
            "Path,Title,Artist,Artist,Custom:DJ_SET,Unknown\r\n" +
            "one.flac,\"Title, One\",First,Guest,Morning,ignored\r\n" +
            "two.flac,\"Line one\r\nLine two\",Second,,Evening,ignored");
        var service = new DelimitedMetadataImportService();

        DelimitedMetadataImportResult result =
            await service.ImportAsync(source, [first, second]);

        Assert.True(result.CanPreview);
        Assert.Equal(2, result.DataRows);
        Assert.Equal(2, result.MatchedRows);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == "import.unknown-column" &&
                     issue.Severity ==
                         DelimitedMetadataImportIssueSeverity.Warning);
        IReadOnlyList<MetadataValueEdit> firstEdits =
            result.EditsByPath[first];
        Assert.Equal(
            ["Title, One"],
            Values(firstEdits, TagFields.Title));
        Assert.Equal(
            ["First", "Guest"],
            Values(firstEdits, TagFields.Artist));
        Assert.Equal(
            ["Morning"],
            Assert.Single(
                firstEdits,
                edit => edit.Field.CustomName == "DJ_SET").Values);
        Assert.Equal(
            ["Line one\r\nLine two"],
            Values(
                result.EditsByPath[second],
                TagFields.Title));
        Assert.Equal(
            ["Second"],
            Values(
                result.EditsByPath[second],
                TagFields.Artist));
    }

    [Fact]
    public async Task SemicolonImportCanTreatEmptyCellsAsFieldRemoval()
    {
        using var session = new TempDirectory();
        string media = Path.Combine(session.Path, "track.flac");
        string source = Path.Combine(session.Path, "metadata.txt");
        await File.WriteAllTextAsync(
            source,
            "Path;Album Artist;Custom:DJ_SET\n" +
            "track.flac;;");
        var service = new DelimitedMetadataImportService();

        DelimitedMetadataImportResult result =
            await service.ImportAsync(
                source,
                [media],
                new(
                    EmptyCellMode:
                        DelimitedMetadataEmptyCellMode.RemoveField));

        Assert.True(result.CanPreview);
        IReadOnlyList<MetadataValueEdit> edits =
            Assert.Single(result.EditsByPath).Value;
        Assert.Empty(
            Assert.Single(
                edits,
                edit => edit.Field.KnownField ==
                    TagFields.AlbumArtist).Values);
        Assert.Empty(
            Assert.Single(
                edits,
                edit => edit.Field.CustomName == "DJ_SET").Values);
    }

    [Fact]
    public async Task DuplicateOrMalformedRowsBlockPreview()
    {
        using var session = new TempDirectory();
        string media = Path.Combine(session.Path, "track.flac");
        string duplicate =
            Path.Combine(session.Path, "duplicate.csv");
        await File.WriteAllTextAsync(
            duplicate,
            "Path,Title\ntrack.flac,First\ntrack.flac,Second");
        var service = new DelimitedMetadataImportService();

        DelimitedMetadataImportResult duplicates =
            await service.ImportAsync(duplicate, [media]);

        Assert.False(duplicates.CanPreview);
        Assert.Contains(
            duplicates.Issues,
            issue => issue.Code == "import.duplicate-path" &&
                     issue.Severity ==
                         DelimitedMetadataImportIssueSeverity.Blocker);

        string malformed =
            Path.Combine(session.Path, "malformed.csv");
        await File.WriteAllTextAsync(
            malformed,
            "Path,Title\ntrack.flac,\"unfinished");

        DelimitedMetadataImportResult invalid =
            await service.ImportAsync(malformed, [media]);

        Assert.False(invalid.CanPreview);
        Assert.Contains(
            invalid.Issues,
            issue => issue.Code == "import.malformed");
    }

    [Fact]
    public async Task AmbiguousAndInvalidPathsStayOutsideTheSelectedScope()
    {
        using var session = new TempDirectory();
        string first =
            Path.Combine(session.Path, "one", "track.flac");
        string second =
            Path.Combine(session.Path, "two", "track.flac");
        string source = Path.Combine(session.Path, "paths.csv");
        await File.WriteAllTextAsync(
            source,
            "Path,Title\ntrack.flac,Ambiguous\n" +
            "bad\0path.flac,Invalid");
        var service = new DelimitedMetadataImportService();

        DelimitedMetadataImportResult result =
            await service.ImportAsync(source, [first, second]);

        Assert.False(result.CanPreview);
        Assert.Empty(result.EditsByPath);
        Assert.Contains(
            result.Issues,
            issue => issue.Code == "import.ambiguous-path");
        Assert.Contains(
            result.Issues,
            issue => issue.Code == "import.invalid-path");
    }

    private static IReadOnlyList<string> Values(
        IReadOnlyList<MetadataValueEdit> edits,
        TagFields field) =>
        Assert.Single(
            edits,
            edit => edit.Field.KnownField == field).Values;

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "mlm-delimited-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
