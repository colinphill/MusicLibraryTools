using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public interface IIngestPreflightService
{
    Task<IngestPreflightResult> CheckAsync(IngestRequest request, CancellationToken ct = default);
}

/// <summary>Checks ingest inputs and external tools without enumerating or reading source media.</summary>
public sealed class IngestPreflightService(
    IFfmpegRunner ffmpeg,
    IAppSettings? settings = null) : IIngestPreflightService
{
    public async Task<IngestPreflightResult> CheckAsync(IngestRequest request, CancellationToken ct = default)
    {
        var checks = new List<IngestPreflightCheck>();
        string source;
        try
        {
            source = Path.GetFullPath(request.SourceDirectory);
            checks.Add(Directory.Exists(source)
                ? Pass("Source", source)
                : Error("Source", $"Source directory is unavailable: {source}"));
        }
        catch (Exception ex)
        {
            checks.Add(Error("Source", $"Invalid source directory: {ex.Message}"));
            return new(checks);
        }

        IngestMusicConfiguration configuration;
        try
        {
            var resolved = IngestMusicConfiguration.Resolve(request, settings);
            configuration = resolved.Configuration;
            checks.Add(Pass("Configuration", resolved.ConfigurationPath is null
                ? "Using the active library configuration."
                : $"Loaded {resolved.ConfigurationPath}"));
        }
        catch (Exception ex)
        {
            checks.Add(Error("Configuration", $"Configuration could not be loaded: {ex.Message}"));
            return new(checks);
        }

        string[] destinations =
        [
            configuration.AacDestination,
            configuration.CdDestination,
            configuration.PairedCdDestination,
            configuration.HighResolutionDestination,
        ];
        destinations = destinations.Where(destination =>
            !string.IsNullOrWhiteSpace(destination)).ToArray();
        if (destinations.Any(destination => PathsOverlap(source, destination)))
            checks.Add(Error("Path isolation", "The source directory overlaps an ingestion destination."));
        else if (destinations.SelectMany((left, index) => destinations.Skip(index + 1)
                     .Select(right => (left, right))).Any(pair => PathsOverlap(pair.left, pair.right)))
            checks.Add(Error("Path isolation", "Two or more ingestion destinations overlap."));
        else
            checks.Add(Pass("Path isolation", "Source and destination trees do not overlap."));

        var unavailable = destinations.Distinct(PathComparer).Where(destination => !Directory.Exists(destination)).ToList();
        checks.Add(unavailable.Count == 0
            ? Pass("Destinations", "All configured destination directories are reachable.")
            : Warning("Destinations", $"{unavailable.Count:N0} destination(s) are unavailable or will be created during apply: " +
                string.Join(", ", unavailable)));

        if (!string.IsNullOrWhiteSpace(configuration.ItunesLibraryPath))
            checks.Add(File.Exists(configuration.ItunesLibraryPath)
                ? Pass("iTunes library", configuration.ItunesLibraryPath)
                : Error("iTunes library", $"Configured library is unavailable: {configuration.ItunesLibraryPath}"));

        try
        {
            await ffmpeg.PreflightAsync(configuration.FfmpegPath, configuration.AacEncoder, ct);
            checks.Add(Pass("ffmpeg", $"Found {configuration.AacEncoder} via {configuration.FfmpegPath}."));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A cleanup-only source can still be previewed/applied without transcoding, so external
            // tool failure is prominent but does not prevent the source scan.
            checks.Add(Warning("ffmpeg", $"Transcoding is not ready: {ex.Message}"));
        }

        return new(checks);
    }

    private static bool PathsOverlap(string first, string second)
    {
        string left = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        string right = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return PathComparer.Equals(left, right) ||
            left.StartsWith(right + Path.DirectorySeparatorChar, PathComparison) ||
            right.StartsWith(left + Path.DirectorySeparatorChar, PathComparison);
    }

    private static IngestPreflightCheck Pass(string name, string message) =>
        new(name, IngestPreflightSeverity.Pass, message);
    private static IngestPreflightCheck Warning(string name, string message) =>
        new(name, IngestPreflightSeverity.Warning, message);
    private static IngestPreflightCheck Error(string name, string message) =>
        new(name, IngestPreflightSeverity.Error, message);
    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
