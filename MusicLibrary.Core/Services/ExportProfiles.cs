using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

/// <summary>Definitions only: existing specialized tools opt in by creating configured copies.</summary>
public static class BuiltInExportProfiles
{
    public static LibraryExportProfile Android { get; } = Specialized(
        "android", "Android", "android-syncer", "android-managed-mirror");

    public static LibraryExportProfile CarCard { get; } = Specialized(
        "car-card", "Car Card", LocalFileSystemExportTransport.ProviderId, "car-card");

    public static LibraryExportProfile SmartStorage { get; } = Specialized(
        "smart-storage", "Smart Storage", LocalFileSystemExportTransport.ProviderId,
        "smart-storage");

    public static IReadOnlyList<LibraryExportProfile> All { get; } =
        [Android, CarCard, SmartStorage];

    public static IReadOnlyList<LibraryExportProfile> Visible(
        IEnumerable<LibraryExportProfile> configuredProfiles) => configuredProfiles
        .Where(profile => profile.IsVisible)
        .OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    private static LibraryExportProfile Specialized(
        string id,
        string name,
        string transportProvider,
        string transformProvider) => new(
            id,
            name,
            Enabled: false,
            ExportSelectionPolicy.EntireLibrary,
            new(ExportTransformMode.SpecializedProvider, ProviderId: transformProvider),
            new(PreserveSourceLayout: true),
            new(ExportArtworkMode.Embedded),
            new(),
            new(transportProvider, ""),
            new(ExportExtraFileDisposition.Preserve));
}

[Flags]
public enum ExportTransportCapabilities
{
    None = 0,
    LocalFileTree = 1 << 0,
    RemoteDevice = 1 << 1,
    ReviewedMutationPlan = 1 << 2,
    Reconciliation = 1 << 3,
    RecoveryJournal = 1 << 4,
}

public sealed record ExportTransportDescriptor(
    string Id,
    string Name,
    ExportTransportCapabilities Capabilities);

public sealed record ExportTransportPlan(
    string ProfileId,
    string ProfileFingerprint,
    string TransportId,
    string Destination,
    FileMutationPlan MutationPlan,
    IReadOnlyList<OperationIssue> Issues)
{
    public bool CanApply => MutationPlan.CanApply &&
        Issues.All(issue => issue.Severity != OperationIssueSeverity.Blocker);
}

public sealed record ExportTransportResult(
    string ProfileId,
    string TransportId,
    FileMutationSummary Mutations,
    IReadOnlyList<OperationIssue> Issues);

/// <summary>
/// Internal extension seam for destinations. It is registered through Core DI, but is not a
/// compatibility promise or public plugin SDK.
/// </summary>
public interface IExportTransport
{
    ExportTransportDescriptor Descriptor { get; }

    ExportTransportPlan Prepare(LibraryExportProfile profile, FileMutationPlan mutationPlan);

    Task<ExportTransportResult> ApplyAsync(
        ExportTransportPlan plan,
        LibraryExportProfile currentProfile,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed class LocalFileSystemExportTransport : IExportTransport
{
    public const string ProviderId = "local-filesystem";
    private readonly IFileMutationPlanExecutor _executor;

    public LocalFileSystemExportTransport(IFileMutationPlanExecutor executor) =>
        _executor = executor;

    public ExportTransportDescriptor Descriptor { get; } = new(
        ProviderId,
        "Local filesystem",
        ExportTransportCapabilities.LocalFileTree |
        ExportTransportCapabilities.ReviewedMutationPlan |
        ExportTransportCapabilities.Reconciliation |
        ExportTransportCapabilities.RecoveryJournal);

    public ExportTransportPlan Prepare(
        LibraryExportProfile profile,
        FileMutationPlan mutationPlan)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(mutationPlan);
        var issues = new List<OperationIssue>();
        if (!profile.Enabled)
            issues.Add(new("export-profile-disabled", OperationIssueSeverity.Blocker,
                $"Export profile '{profile.Name}' is disabled."));
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                profile.Transport.ProviderId, Descriptor.Id))
            issues.Add(new("export-transport-mismatch", OperationIssueSeverity.Blocker,
                $"Export profile '{profile.Name}' requires transport " +
                $"'{profile.Transport.ProviderId}', not '{Descriptor.Id}'."));
        if (string.IsNullOrWhiteSpace(profile.Transport.Destination))
            issues.Add(new("export-destination-missing", OperationIssueSeverity.Blocker,
                $"Export profile '{profile.Name}' has no destination."));
        else
        {
            try
            {
                string configured = Normalize(profile.Transport.Destination);
                string planned = Normalize(mutationPlan.DestinationRoot);
                if (!PathComparer.Equals(configured, planned))
                    issues.Add(new("export-destination-mismatch", OperationIssueSeverity.Blocker,
                        "The export destination does not match the reviewed mutation plan.",
                        configured));
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or
                                                PathTooLongException)
            {
                issues.Add(new("export-destination-invalid", OperationIssueSeverity.Blocker,
                    $"The export destination is invalid: {error.Message}",
                    profile.Transport.Destination));
            }
        }

        return new(profile.Id, profile.Fingerprint, Descriptor.Id,
            profile.Transport.Destination, mutationPlan, issues);
    }

    public async Task<ExportTransportResult> ApplyAsync(
        ExportTransportPlan plan,
        LibraryExportProfile currentProfile,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(currentProfile);
        if (!StringComparer.Ordinal.Equals(plan.TransportId, Descriptor.Id))
            throw new InvalidOperationException(
                $"Transport plan '{plan.TransportId}' cannot be applied by '{Descriptor.Id}'.");
        if (!StringComparer.Ordinal.Equals(plan.ProfileId, currentProfile.Id) ||
            !StringComparer.Ordinal.Equals(plan.ProfileFingerprint, currentProfile.Fingerprint))
            throw new InvalidOperationException(
                "The export profile changed after preview. Preview the export again before applying it.");
        if (!plan.CanApply)
            throw new InvalidOperationException("The export transport plan contains blocking issues.");

        FileMutationSummary mutations = await _executor.ApplyAsync(
            plan.MutationPlan, progress, ct).ConfigureAwait(false);
        return new(plan.ProfileId, Descriptor.Id, mutations,
            plan.MutationPlan.Issues.Concat(plan.Issues).ToArray());
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
