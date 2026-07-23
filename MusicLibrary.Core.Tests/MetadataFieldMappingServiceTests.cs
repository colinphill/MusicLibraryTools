using Microsoft.Extensions.DependencyInjection;
using MetadataCaching;
using MusicFileUtilities;
using MusicLibrary.Core;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class MetadataFieldMappingServiceTests
{
    [Fact]
    public void StoreRoundTripsValidatedMappingsAndResolvesByPath()
    {
        using var temp = new TempDirectory();
        string statePath = Path.Combine(temp.Path, "settings.json");
        var store = CreateStore(statePath);
        store.Save(
        [
            new(MediaFormatFamily.Flac, TagFields.Artist, "PERFORMING_ARTIST"),
            new(MediaFormatFamily.Mp3, TagFields.Artist, "Performing Artist"),
        ]);

        var restarted = CreateStore(statePath);

        Assert.Equal(2, restarted.Load().Count);
        Assert.True(restarted.TryGet(
            Path.Combine(temp.Path, "track.flac"),
            TagFields.Artist,
            out string nativeName));
        Assert.Equal("PERFORMING_ARTIST", nativeName);
        Assert.False(restarted.TryGet(
            Path.Combine(temp.Path, "track.m4a"),
            TagFields.Artist,
            out _));
    }

    [Fact]
    public void StoreRejectsDuplicateAndUnsafeMappings()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(Path.Combine(temp.Path, "settings.json"));
        MetadataFieldMapping mapping =
            new(MediaFormatFamily.Flac, TagFields.Title, "DISPLAY_TITLE");

        Assert.Throws<ArgumentException>(() =>
            store.Save([mapping, mapping with { NativeFieldName = "OTHER_TITLE" }]));
        Assert.Throws<ArgumentException>(() =>
            store.Save([mapping with { NativeFieldName = "BAD=NAME" }]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.Save([mapping with { Field = TagFields.NullField }]));
    }

    [Fact]
    public async Task DocumentPromotesConfiguredNativeValuesToCanonicalField()
    {
        using var temp = new TempDirectory();
        string mediaPath = Path.Combine(temp.Path, "track.flac");
        File.Copy(MediaFixtures.Path_("sample.flac"), mediaPath);
        var store = CreateStore(Path.Combine(temp.Path, "settings.json"));
        store.Save(
        [
            new(MediaFormatFamily.Flac, TagFields.Artist, "PERFORMING_ARTIST"),
        ]);
        IMediaFile media = MediaFile.GetFile(mediaPath, readOnly: false);
        IUserStringMetadata custom = Assert.IsAssignableFrom<IUserStringMetadata>(
            media.Tags.First());
        custom.SetUserString("PERFORMING_ARTIST", "Mapped artist");
        media.SaveTags();
        var documents = new MetadataDocumentService(
            MediaFormatRegistry.Default,
            store);

        MediaDocument document = await documents.LoadAsync(mediaPath);

        Assert.Equal("Mapped artist", document.FirstValue(TagFields.Artist));
        Assert.Empty(document.Values(
            MetadataFieldKey.Custom("PERFORMING_ARTIST")));
        IMediaFile native = MediaFile.GetFile(mediaPath, readOnly: true);
        var cached = new MetadataCacheEntry(
            store.ProjectForCache(mediaPath, native),
            File.GetLastWriteTimeUtc(mediaPath));
        Assert.Equal("Mapped artist", cached.Artist);
    }

    [Fact]
    public async Task OperationWritesConfiguredNativeFieldAndReloadsCanonicalValue()
    {
        using var temp = new TempDirectory();
        string recovery = temp.Path + ".MusicLibraryManager-recovery";
        string mediaPath = Path.Combine(temp.Path, "track.flac");
        File.Copy(MediaFixtures.Path_("sample.flac"), mediaPath);
        string statePath = Path.Combine(temp.Path, "settings.json");
        try
        {
            var settings = new AppSettings(statePath);
            var mappings = new MetadataFieldMappingService(
                settings,
                MediaFormatRegistry.Default);
            mappings.Save(
            [
                new(MediaFormatFamily.Flac, TagFields.Artist, "PERFORMING_ARTIST"),
            ]);
            var documents = new MetadataDocumentService(
                MediaFormatRegistry.Default,
                mappings);
            var operations = new MetadataOperationService(
                documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(settings: settings),
                settings,
                fieldMappings: mappings);
            OperationRecipe recipe = OperationRecipe.Create(
                "Mapped artist",
                new AssignFieldOperation(
                    MetadataFieldKey.Known(TagFields.Artist),
                    "Configured artist"));

            MetadataOperationPlan plan =
                await operations.PreviewAsync([mediaPath], recipe);
            MetadataApplyResult result = await operations.ApplyAsync(plan);

            Assert.Equal(1, result.ChangedFiles);
            MediaDocument reloaded = await documents.LoadAsync(mediaPath);
            Assert.Equal(
                "Configured artist",
                reloaded.FirstValue(TagFields.Artist));
            IUserStringMetadata native = Assert.IsAssignableFrom<IUserStringMetadata>(
                MediaFile.GetFile(mediaPath, readOnly: true).Tags.First());
            Assert.Contains(native.GetUserStrings(), pair =>
                pair.Key == "PERFORMING_ARTIST" &&
                pair.Value == "Configured artist");
        }
        finally
        {
            try { Directory.Delete(recovery, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ServiceRegistrationIncludesFieldMappingService()
    {
        var services = new ServiceCollection();
        services.AddMusicLibraryCore();
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<MetadataFieldMappingService>(
            provider.GetRequiredService<IMetadataFieldMappingService>());
    }

    private static MetadataFieldMappingService CreateStore(string statePath) =>
        new(new AppSettings(statePath), MediaFormatRegistry.Default);

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "mlm-field-map-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for files still held by a failed test.
            }
        }
    }
}
