namespace MusicLibrary.Core.Services;

[Flags]
public enum MetadataSourceCapabilities
{
    None = 0,
    RecordingReleaseLookup = 1,
    ReleaseSearch = 2,
    ReleaseDetails = 4,
    ReleaseArtwork = 8,
}

public sealed record MetadataSourceDescriptor(
    string Id,
    string DisplayName,
    MetadataSourceCapabilities Capabilities,
    bool RequiresCredential = false);

/// <summary>
/// Common discovery contract for code-backed online sources. Functional
/// contracts remain strongly typed; consumers select a capability and then use
/// the corresponding derived interface rather than a provider scripting
/// language.
/// </summary>
public interface IMetadataSourceProvider
{
    MetadataSourceDescriptor Descriptor { get; }
}

public interface IProviderNetworkPolicy
{
    bool IsOffline { get; }
}

public sealed class ProviderNetworkPolicy(
    IAppSettings settings) : IProviderNetworkPolicy
{
    public const string OfflinePreferenceKey =
        "providers.offlineMode";

    public bool IsOffline => bool.TryParse(
        settings.GetPreference(OfflinePreferenceKey),
        out bool offline) && offline;
}

public interface IMetadataSourceCatalog
{
    IReadOnlyList<IMetadataSourceProvider> Providers { get; }
    IMetadataSourceProvider? Find(string id);
}

public sealed class MetadataSourceCatalog(
    IEnumerable<IMetadataSourceProvider> providers) : IMetadataSourceCatalog
{
    public IReadOnlyList<IMetadataSourceProvider> Providers { get; } =
        providers
            .OrderBy(provider => provider.Descriptor.DisplayName,
                StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    public IMetadataSourceProvider? Find(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Providers.FirstOrDefault(provider =>
            provider.Descriptor.Id.Equals(
                id, StringComparison.OrdinalIgnoreCase));
    }
}
