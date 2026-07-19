using Microsoft.Extensions.DependencyInjection;
using MusicLibrary.Core;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void CoreServicesResolveWithActiveConfigurationDependencies()
    {
        var services = new ServiceCollection();
        services.AddMusicLibraryCore();
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<IngestMusicService>(provider.GetRequiredService<IIngestMusicService>());
        Assert.IsType<IngestPreflightService>(
            provider.GetRequiredService<IIngestPreflightService>());
        Assert.IsType<LibraryOperationContextFactory>(
            provider.GetRequiredService<ILibraryOperationContextFactory>());
        Assert.IsType<ItlMetadataRepairService>(
            provider.GetRequiredService<IItlMetadataRepairService>());
    }
}
