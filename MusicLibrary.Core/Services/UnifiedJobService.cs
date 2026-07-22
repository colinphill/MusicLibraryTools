using MusicLibrary.Core.Models;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

/// <summary>
/// Read-only operation catalog. Execution belongs to operation-specific typed services; this
/// catalog carries only presentation metadata used by the Operations tab.
/// </summary>
public interface IUnifiedJobService
{
    IReadOnlyList<UnifiedJobDescriptor> Catalog { get; }
}

public sealed class UnifiedJobService : IUnifiedJobService
{
    public const string ConfiguredExportJobPrefix = "configured-export:";

    private readonly IReadOnlyList<ILibraryOperationProvider> _providers;
    private readonly IAppSettings? _settings;

    /// <summary>Compatibility constructor used by headless callers: exposes the full catalog.</summary>
    public UnifiedJobService() : this(BuiltInLibraryOperationProviders.All, null)
    {
    }

    public UnifiedJobService(
        IEnumerable<ILibraryOperationProvider> providers,
        IAppSettings? settings)
    {
        _providers = providers.ToArray();
        _settings = settings;
    }

    public IReadOnlyList<UnifiedJobDescriptor> Catalog
    {
        get
        {
            LibraryConfiguration? configuration = _settings?.Configuration;
            IEnumerable<UnifiedJobDescriptor> builtIns = _providers
                .Where(provider => _settings is null ||
                    provider.GetAvailability(configuration).Available)
                .Select(provider => provider.Descriptor);
            if (configuration is null)
                return builtIns.ToArray();

            IEnumerable<UnifiedJobDescriptor> configuredExports = configuration.ExportProfiles
                .Where(profile => profile.IsVisible &&
                    profile.Transform.Mode != ExportTransformMode.SpecializedProvider)
                .OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(profile => new UnifiedJobDescriptor(
                    ConfiguredExportJobPrefix + profile.Id,
                    "Export: " + profile.Name,
                    "Preview and apply the configured selection, naming, transport, and " +
                    "reconciliation policy.",
                    UnifiedJobApplyMode.ApplyFlag,
                    [],
                    "",
                    0));
            return builtIns.Concat(configuredExports).ToArray();
        }
    }
}
