using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class UnifiedJobServiceTests
{
    [Fact]
    public void CatalogContainsOnlyOperationsBackedByTypedCoreServices()
    {
        var catalog = new UnifiedJobService().Catalog;

        Assert.Equal(
            ["playlist-sync", "artwork-normalization", "smart-storage", "car-card", "cross-library-sync", "redundancies", "itunes-validation"],
            catalog.Select(job => job.Id).ToArray());
        Assert.Equal(UnifiedJobApplyMode.ApplyFlag,
            catalog.Single(job => job.Id == "playlist-sync").ApplyMode);
        Assert.Equal(UnifiedJobApplyMode.ReadOnly,
            catalog.Single(job => job.Id == "itunes-validation").ApplyMode);
        Assert.Equal(UnifiedJobApplyMode.ApplyFlag,
            catalog.Single(job => job.Id == "artwork-normalization").ApplyMode);
    }

    [Fact]
    public void CatalogCarriesPresentationMetadataButNoExecutionArguments()
    {
        foreach (UnifiedJobDescriptor descriptor in new UnifiedJobService().Catalog)
        {
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Name));
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Description));
            Assert.DoesNotContain(".exe", descriptor.ArgumentsHint,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ConfiguredCatalog_HidesProvidersWhoseCapabilitiesAreUnavailable()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "operation-providers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string configPath = Path.Combine(directory, "library.xml");
            var editable = EditableLibraryConfig.CreateNew();
            editable.IndexTargets.Add(editable.CreateIndexTarget(directory));
            editable.Save(configPath);
            var settings = new AppSettings(Path.Combine(directory, "settings.json"));
            settings.LoadConfig(configPath);

            IReadOnlyList<UnifiedJobDescriptor> catalog = new UnifiedJobService(
                BuiltInLibraryOperationProviders.All, settings).Catalog;

            Assert.Equal(["itunes-validation"], catalog.Select(job => job.Id));
            Assert.False(BuiltInExportProfiles.Android.IsVisible);
            Assert.False(BuiltInExportProfiles.CarCard.IsVisible);
            Assert.False(BuiltInExportProfiles.SmartStorage.IsVisible);
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch { }
        }
    }

    [Fact]
    public void CatalogOnlyLibraryWithCatalog_HidesArtworkNormalization()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "catalog-only-operations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string configPath = Path.Combine(directory, "library.xml");
            var editable = EditableLibraryConfig.CreateNew();
            editable.ItunesLibraryPath = Path.Combine(directory, "Library.itl");
            editable.IndexTargets.Add(editable.CreateIndexTarget(directory));
            editable.Save(configPath);
            var settings = new AppSettings(Path.Combine(directory, "settings.json"));
            settings.LoadConfig(configPath);

            IReadOnlyList<UnifiedJobDescriptor> catalog = new UnifiedJobService(
                BuiltInLibraryOperationProviders.All, settings).Catalog;

            Assert.DoesNotContain(catalog, job => job.Id == "artwork-normalization");
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch { }
        }
    }

    [Fact]
    public void ConfiguredCatalog_ExposesEnabledGenericExportsAsTypedJobs()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "configured-export-job-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "source");
            string destination = Path.Combine(directory, "destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            string configPath = Path.Combine(directory, "library.xml");
            var editable = EditableLibraryConfig.CreateNew();
            editable.IndexTargets.Add(editable.CreateIndexTarget(source));
            var output = editable.CreateIndexTarget(destination);
            output.ProfileId = LibraryProfilePresets.ArtistAlbumId;
            output.Permissions = LibraryRootPermissions.SynchronizeOutput;
            editable.IndexTargets.Add(output);
            editable.ExportProfiles.Add(new LibraryExportProfile(
                "portable", "Portable library", true,
                ExportSelectionPolicy.EntireLibrary,
                new(ExportTransformMode.Copy),
                new(PreserveSourceLayout: true),
                new(ExportArtworkMode.Embedded, FrontCoverOnly: false),
                new(),
                new(LocalFileSystemExportTransport.ProviderId, destination),
                new()));
            editable.Save(configPath);
            var settings = new AppSettings(Path.Combine(directory, "settings.json"));
            settings.LoadConfig(configPath);

            IReadOnlyList<UnifiedJobDescriptor> catalog = new UnifiedJobService(
                BuiltInLibraryOperationProviders.All, settings).Catalog;

            UnifiedJobDescriptor job = Assert.Single(catalog, item =>
                item.Id == UnifiedJobService.ConfiguredExportJobPrefix + "portable");
            Assert.Equal("Export: Portable library", job.Name);
            Assert.Equal(UnifiedJobApplyMode.ApplyFlag, job.ApplyMode);
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch { }
        }
    }
}
