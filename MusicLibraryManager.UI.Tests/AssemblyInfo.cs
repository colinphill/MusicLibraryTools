using Xunit;

// UI fixtures replace the application's process-wide service provider and localization state.
// Running fixture classes concurrently can attach a view to another fixture's provider, making
// otherwise deterministic interaction and screenshot tests fail according to scheduler timing.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MusicLibraryManager.UI.Tests;

[CollectionDefinition(
    Name,
    DisableParallelization = true)]
public sealed class ApplicationServiceProviderCollection
{
    public const string Name =
        "Process-wide application services";
}
