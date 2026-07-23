using Microsoft.Extensions.DependencyInjection;
using MusicFileUtilities;
using MusicLibrary.Core;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class MetadataGridColumnStoreTests
{
    [Fact]
    public void StoreRoundTripsKnownAndCustomColumnsBySurface()
    {
        using var temp = new TempDirectory();
        string state = Path.Combine(temp.Path, "settings.json");
        var store = new MetadataGridColumnStore(
            new AppSettings(state));
        UserMetadataColumnDescriptor known = new(
            Guid.NewGuid(),
            "Catalog",
            MetadataFieldKey.Known(TagFields.CatalogNumber),
            true,
            3,
            180,
            MetadataGridColumnSortType.Text);
        UserMetadataColumnDescriptor custom = new(
            Guid.NewGuid(),
            "DJ set",
            MetadataFieldKey.Custom("DJ_SET"),
            false,
            5,
            220);

        store.Save(MetadataGridSurface.Library, [known]);
        store.Save(MetadataGridSurface.Workbench, [custom]);

        var reloaded = new MetadataGridColumnStore(
            new AppSettings(state));
        Assert.Equal(
            TagFields.CatalogNumber,
            Assert.Single(reloaded.Load(
                MetadataGridSurface.Library)).Field.KnownField);
        Assert.Equal(
            "DJ_SET",
            Assert.Single(reloaded.Load(
                MetadataGridSurface.Workbench)).Field.CustomName);
    }

    [Fact]
    public void StoreRejectsMalformedOrDuplicateDescriptors()
    {
        using var temp = new TempDirectory();
        var store = new MetadataGridColumnStore(
            new AppSettings(
                Path.Combine(temp.Path, "settings.json")));
        Guid id = Guid.NewGuid();
        UserMetadataColumnDescriptor column = new(
            id,
            "Catalog",
            MetadataFieldKey.Known(TagFields.CatalogNumber),
            true,
            0,
            160);

        Assert.Throws<ArgumentException>(() =>
            store.Save(
                MetadataGridSurface.Library,
                [column, column]));
        Assert.Throws<ArgumentException>(() =>
            store.Save(
                MetadataGridSurface.Library,
                [column with { Label = "" }]));
    }

    [Fact]
    public void ValueKeysAreStableAndSafeForBindingPaths()
    {
        Assert.Equal(
            "K_CatalogNumber",
            MetadataGridValueKey.For(
                MetadataFieldKey.Known(
                    TagFields.CatalogNumber)));
        string custom = MetadataGridValueKey.For(
            MetadataFieldKey.Custom("CATALOG/NO. 1"));
        Assert.StartsWith("C_", custom);
        Assert.DoesNotContain('/', custom);
        Assert.DoesNotContain(' ', custom);
    }

    [Fact]
    public void ServiceRegistrationIncludesMetadataColumnStore()
    {
        var services = new ServiceCollection();
        services.AddMusicLibraryCore();
        using ServiceProvider provider =
            services.BuildServiceProvider();

        Assert.IsType<MetadataGridColumnStore>(
            provider.GetRequiredService<
                IMetadataGridColumnStore>());
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "mlm-column-tests-" +
            Guid.NewGuid().ToString("N"));

        public TempDirectory() =>
            Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
