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
        using var media = MediaFixtures.Copy(fixture);
        var writer = new TagWriteService();
        var setup = await writer.ApplyAsync(
            [media.Path],
            [
                new(TagFields.DiscNumber, "1"),
                new(TagFields.TotalDiscs, "2"),
            ]);
        Assert.Equal(1, setup.SavedCount);

        var viewModel = new FieldsDialogViewModel(
            new MediaFileService(), writer, [media.Path]);
        await viewModel.Loading;

        Assert.Equal("1", viewModel.Rows.Single(
            row => row.Field == TagFields.DiscNumber).Value);
        Assert.Equal("2", viewModel.Rows.Single(
            row => row.Field == TagFields.TotalDiscs).Value);
    }

    [Fact]
    public async Task Load_ShowsCombinedFlacDiscNumberAndTotalDiscs()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        var source = Assert.IsType<FLACFile>(MediaFile.GetFile(media.Path));
        source["DISCNUMBER"] = "1/2";
        source.Save();

        var viewModel = new FieldsDialogViewModel(
            new MediaFileService(), new TagWriteService(), [media.Path]);
        await viewModel.Loading;

        Assert.Equal("1", viewModel.Rows.Single(
            row => row.Field == TagFields.DiscNumber).Value);
        Assert.Equal("2", viewModel.Rows.Single(
            row => row.Field == TagFields.TotalDiscs).Value);
    }

    [Fact]
    public async Task Load_ShowsAndCanRemoveEmptyMp4DiscAtom()
    {
        using var media = MediaFixtures.Copy("sample_alac.m4a");
        var writer = new TagWriteService();
        var setup = await writer.ApplyAsync(
            [media.Path],
            [
                new(TagFields.DiscNumber, "0"),
                new(TagFields.TotalDiscs, "0"),
            ]);
        Assert.Equal(1, setup.SavedCount);

        var reader = new MediaFileService();
        var viewModel = new FieldsDialogViewModel(reader, writer, [media.Path]);
        await viewModel.Loading;

        FieldRow discNumber = viewModel.Rows.Single(
            row => row.Field == TagFields.DiscNumber);
        FieldRow totalDiscs = viewModel.Rows.Single(
            row => row.Field == TagFields.TotalDiscs);
        Assert.Equal("0", discNumber.Value);
        Assert.Equal("0", totalDiscs.Value);

        viewModel.RemoveFieldCommand.Execute(discNumber);
        await viewModel.SaveCommand.ExecuteAsync(null);

        var reload = await reader.LoadDirectAsync(media.Path, includeArtwork: false);
        Assert.DoesNotContain(reload.Value!.KnownFields,
            field => field.Field is TagFields.DiscNumber or TagFields.TotalDiscs);
    }

    [Fact]
    public async Task Save_RemovesFlacTotalStoredInCombinedVorbisComment()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        var source = Assert.IsType<FLACFile>(MediaFile.GetFile(media.Path));
        source["TRACKNUMBER"] = "3/12";
        source.Save();

        var reader = new MediaFileService();
        var viewModel = new FieldsDialogViewModel(
            reader, new TagWriteService(), [media.Path]);
        await viewModel.Loading;

        FieldRow total = viewModel.Rows.Single(row => row.Field == TagFields.TotalTracks);
        Assert.Equal("12", total.Value);
        viewModel.RemoveFieldCommand.Execute(total);

        bool? closed = null;
        viewModel.CloseRequested += result => closed = result;
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(closed);
        var reload = await reader.LoadDirectAsync(media.Path, includeArtwork: false);
        Assert.Contains(reload.Value!.KnownFields,
            field => field.Field == TagFields.TrackNumber && field.Value == "3");
        Assert.DoesNotContain(reload.Value.KnownFields,
            field => field.Field == TagFields.TotalTracks);
    }
}
