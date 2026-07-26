using MusicFileUtilities;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibrary.Core.Tests;

public class FieldsDialogIntegrationTests
{
    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample_alac.m4a")]
    public async Task Load_ShowsDiscNumberAndTotalDiscs(string fixture)
    {
        using var media = TestMedia.Copy(fixture);
        var writer = new TagWriteService();
        var setup = await writer.ApplyAsync(
            [media.Path],
            [
                new(TagFields.DiscNumber, "1"),
                new(TagFields.TotalDiscs, "2"),
            ]);
        Assert.Equal(1, setup.SavedCount);

        FieldsDialogViewModel viewModel =
            CreateEditor(media.Path);
        await viewModel.Loading;

        Assert.Equal("1", viewModel.Rows.Single(
            row => row.Field == TagFields.DiscNumber).Value);
        Assert.Equal("2", viewModel.Rows.Single(
            row => row.Field == TagFields.TotalDiscs).Value);
    }

    [Fact]
    public async Task Load_ShowsCombinedFlacDiscNumberAndTotalDiscs()
    {
        using var media = TestMedia.Copy("sample.flac");
        var source = Assert.IsType<FLACFile>(MediaFile.GetFile(media.Path));
        source["DISCNUMBER"] = "1/2";
        source.Save();

        FieldsDialogViewModel viewModel =
            CreateEditor(media.Path);
        await viewModel.Loading;

        Assert.Equal("1", viewModel.Rows.Single(
            row => row.Field == TagFields.DiscNumber).Value);
        Assert.Equal("2", viewModel.Rows.Single(
            row => row.Field == TagFields.TotalDiscs).Value);
    }

    [Fact]
    public async Task Load_ShowsAndCanRemoveEmptyMp4DiscAtom()
    {
        using var media = TestMedia.Copy("sample_alac.m4a");
        var writer = new TagWriteService();
        var setup = await writer.ApplyAsync(
            [media.Path],
            [
                new(TagFields.DiscNumber, "0"),
                new(TagFields.TotalDiscs, "0"),
            ]);
        Assert.Equal(1, setup.SavedCount);

        var reader = new MediaFileService();
        FieldsDialogViewModel viewModel =
            CreateEditor(media.Path);
        await viewModel.Loading;

        FieldRow discNumber = viewModel.Rows.Single(
            row => row.Field == TagFields.DiscNumber);
        FieldRow totalDiscs = viewModel.Rows.Single(
            row => row.Field == TagFields.TotalDiscs);
        Assert.Equal("0", discNumber.Value);
        Assert.Equal("0", totalDiscs.Value);

        viewModel.RemoveFieldCommand.Execute(discNumber);
        await viewModel.SaveCommand.ExecuteAsync(null);
        await viewModel.SaveCommand.ExecuteAsync(null);

        var reload = await reader.LoadDirectAsync(media.Path, includeArtwork: false);
        Assert.DoesNotContain(reload.Value!.KnownFields,
            field => field.Field is TagFields.DiscNumber or TagFields.TotalDiscs);
    }

    [Fact]
    public async Task Save_RemovesFlacTotalStoredInCombinedVorbisComment()
    {
        using var media = TestMedia.Copy("sample.flac");
        var source = Assert.IsType<FLACFile>(MediaFile.GetFile(media.Path));
        source["TRACKNUMBER"] = "3/12";
        source.Save();

        var reader = new MediaFileService();
        FieldsDialogViewModel viewModel =
            CreateEditor(media.Path);
        await viewModel.Loading;

        FieldRow total = viewModel.Rows.Single(row => row.Field == TagFields.TotalTracks);
        Assert.Equal("12", total.Value);
        viewModel.RemoveFieldCommand.Execute(total);

        bool? closed = null;
        viewModel.CloseRequested += result => closed = result;
        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Null(closed);
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(closed);
        var reload = await reader.LoadDirectAsync(media.Path, includeArtwork: false);
        Assert.Contains(reload.Value!.KnownFields,
            field => field.Field == TagFields.TrackNumber && field.Value == "3");
        Assert.DoesNotContain(reload.Value.KnownFields,
            field => field.Field == TagFields.TotalTracks);
    }

    private static FieldsDialogViewModel CreateEditor(
        string path)
    {
        var settings = new AppSettings(
            Path.Combine(
                Path.GetDirectoryName(path)!,
                "settings.json"));
        var documents = new MetadataDocumentService(
            MediaFormatRegistry.Default);
        var operations = new MetadataOperationService(
            documents,
            MediaFormatRegistry.Default,
            new FileMutationPlanExecutor(
                settings: settings),
            settings);
        return new(
            documents,
            operations,
            [path],
            async (plan, cancellationToken) =>
            {
                await operations.ApplyAsync(
                    plan,
                    ct: cancellationToken);
                return true;
            });
    }

    private sealed class TestMedia : IDisposable
    {
        private readonly string _directory;
        private readonly string _recovery;

        private TestMedia(
            string directory,
            string path)
        {
            _directory = directory;
            _recovery =
                directory +
                ".MusicLibraryManager-recovery";
            Path = path;
        }

        public string Path { get; }

        public static TestMedia Copy(
            string fixture)
        {
            string directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "mlm-fields-" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = System.IO.Path.Combine(
                directory,
                fixture);
            File.Copy(
                MediaFixtures.Path_(fixture),
                path);
            return new(directory, path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(
                    _directory,
                    recursive: true);
            }
            catch { }
            try
            {
                Directory.Delete(
                    _recovery,
                    recursive: true);
            }
            catch { }
        }
    }
}
