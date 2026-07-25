using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public interface IIngestPreflightService
{
    Task<IngestPreflightResult> CheckAsync(IngestRequest request, CancellationToken ct = default);
}

/// <summary>Checks ingest inputs and external tools without enumerating or reading source media.</summary>
public sealed class IngestPreflightService(
    IFfmpegRunner ffmpeg,
    IAppSettings? settings = null,
    IWavpackRunner? wavpack = null,
    IAudioTranscodeCapabilityService? transcodeCapabilities = null) :
    IIngestPreflightService
{
    public Task<IngestPreflightResult> CheckAsync(IngestRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => CheckCoreAsync(request, ct), ct);
    }

    private async Task<IngestPreflightResult> CheckCoreAsync(
        IngestRequest request,
        CancellationToken ct)
    {
        var checks = new List<IngestPreflightCheck>();
        string source;
        try
        {
            source = Path.GetFullPath(request.SourceDirectory);
            if (!Directory.Exists(source))
                checks.Add(Error(
                    "Source",
                    $"Source directory is unavailable: {source}"));
            else if (request.HasExplicitSourceFiles)
            {
                string[] selected = request.SourceFiles!
                    .Where(path =>
                        !string.IsNullOrWhiteSpace(path))
                    .Select(Path.GetFullPath)
                    .Distinct(PathComparer)
                    .ToArray();
                int unavailableSelected = selected.Count(path =>
                    !File.Exists(path));
                int outside = selected.Count(path =>
                    !IsPathWithinRoot(path, source));
                checks.Add(
                    unavailableSelected == 0 &&
                    outside == 0
                        ? Pass(
                            "Selected files",
                            $"{selected.Length:N0} selected file(s) are available.")
                        : Error(
                            "Selected files",
                            $"{unavailableSelected:N0} file(s) are unavailable and " +
                            $"{outside:N0} are outside the source directory."));
            }
            else
                checks.Add(Pass("Source", source));
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
            .. configuration.Profile.Ingest.Recipes
                .Where(recipe => recipe.Enabled)
                .Select(configuration.ResolveTarget)
                .Where(target => target is not null)
                .Select(target => target!.Target),
        ];
        destinations = destinations.Where(destination =>
                !string.IsNullOrWhiteSpace(destination))
            .Distinct(PathComparer).ToArray();
        IEnumerable<string> isolationRoots =
            request.HasExplicitSourceFiles
                ? request.SourceFiles!
                    .Where(path =>
                        !string.IsNullOrWhiteSpace(path))
                    .Select(Path.GetFullPath)
                    .Select(path =>
                        Path.GetDirectoryName(path)!)
                    .Distinct(PathComparer)
                : [source];
        if (destinations.Any(destination =>
                isolationRoots.Any(selectedRoot =>
                    PathsOverlap(
                        selectedRoot,
                        destination))))
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

        MusicLibraryTools.LibraryIngestRecipe[] sharedRecipes =
            configuration.Profile.Ingest.Recipes
                .Where(recipe =>
                    recipe.Enabled &&
                    IngestTranscodeSettingsResolver.UsesSharedEngine(recipe))
                .ToArray();
        if (sharedRecipes.Length == 0)
            checks.Add(Pass(
                "Transcode engine",
                "Not required by the active ingest recipes."));
        else if (transcodeCapabilities is null)
            checks.Add(Warning(
                "Transcode engine",
                "Shared transcode capabilities are unavailable."));
        else
        {
            try
            {
                AudioTranscodeCapabilitySnapshot snapshot =
                    await transcodeCapabilities.GetAsync(
                        forceRefresh: false,
                        ct);
                var unavailableSetups = new List<string>();
                foreach (MusicLibraryTools.LibraryIngestRecipe recipe in sharedRecipes)
                {
                    AudioTranscodeSettings settings =
                        IngestTranscodeSettingsResolver.Resolve(recipe)!;
                    if (!IngestTranscodeSettingsResolver.TryResolveCapability(
                            snapshot,
                            settings,
                            out _,
                            out _,
                            out _))
                        unavailableSetups.Add(recipe.Name);
                }
                checks.Add(unavailableSetups.Count == 0
                    ? Pass(
                        "Transcode engine",
                        $"{sharedRecipes.Length:N0} ingest transcode setup(s) are ready.")
                    : Warning(
                        "Transcode engine",
                        "Unavailable ingest transcode setup(s): " +
                        string.Join(", ", unavailableSetups)));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                checks.Add(Warning(
                    "Transcode engine",
                    $"Transcoding is not ready: {ex.Message}"));
            }
        }

        try
        {
            string[] encoders = RequiredEncoders(configuration);
            if (encoders.Length == 0 && !RequiresFfmpeg(configuration))
                checks.Add(Pass("ffmpeg", "Not required by the active ingest recipes."));
            else
            {
                if (encoders.Length == 0)
                    encoders = [""];
                foreach (string encoder in encoders)
                    await ffmpeg.PreflightAsync(configuration.FfmpegPath, encoder, ct);
                string? automaticAac = null;
                bool needsAutomaticAac = configuration.Profile.Ingest.Recipes.Any(
                    recipe => recipe.Enabled &&
                        recipe.Action == MusicLibraryTools.LibraryIngestAction.Transcode &&
                        string.IsNullOrWhiteSpace(recipe.Encoder) &&
                        (recipe.Codec ?? recipe.OutputExtension ?? "").Trim()
                            .TrimStart('.').Equals("aac", StringComparison.OrdinalIgnoreCase));
                if (needsAutomaticAac)
                    automaticAac = await ffmpeg.ResolveEncoderAsync(
                        configuration.FfmpegPath,
                        [configuration.AacEncoder, "aac"], ct);
                string capabilities = string.Join(", ", encoders
                    .Where(encoder => !string.IsNullOrWhiteSpace(encoder))
                    .Append(automaticAac)
                    .Where(encoder => !string.IsNullOrWhiteSpace(encoder))
                    .Distinct(StringComparer.Ordinal));
                checks.Add(Pass("ffmpeg", string.IsNullOrWhiteSpace(capabilities)
                    ? $"Found ffmpeg via {configuration.FfmpegPath}."
                    : $"Found {capabilities} via {configuration.FfmpegPath}."));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A cleanup-only source can still be previewed/applied without transcoding, so external
            // tool failure is prominent but does not prevent the source scan.
            checks.Add(Warning("ffmpeg", $"Transcoding is not ready: {ex.Message}"));
        }

        if (!RequiresWavpack(configuration))
            checks.Add(Pass("WavPack", "Not required by the active ingest recipes."));
        else
        {
            try
            {
                await (wavpack ?? new WavpackRunner()).PreflightAsync(
                    configuration.WavpackPath, ct);
                checks.Add(Pass("WavPack",
                    $"Found WavPack via {configuration.WavpackPath}."));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                checks.Add(Warning("WavPack",
                    $"DSF-to-WavPack DSD encoding is not ready: {ex.Message}"));
            }
        }

        return new(checks);
    }

    internal static bool RequiresFfmpeg(IngestMusicConfiguration configuration) =>
        configuration.Profile.Ingest.Recipes.Any(recipe => recipe.Enabled &&
            !IngestTranscodeSettingsResolver.UsesSharedEngine(recipe) &&
            recipe.Action != MusicLibraryTools.LibraryIngestAction.Copy &&
            !RequiresWavpack(recipe));

    internal static bool RequiresWavpack(IngestMusicConfiguration configuration) =>
        configuration.Profile.Ingest.Recipes.Any(recipe =>
            recipe.Enabled &&
            !IngestTranscodeSettingsResolver.UsesSharedEngine(recipe) &&
            RequiresWavpack(recipe));

    private static bool RequiresWavpack(MusicLibraryTools.LibraryIngestRecipe recipe) =>
        recipe.Action == MusicLibraryTools.LibraryIngestAction.Transcode &&
        recipe.InputExtensions.Count > 0 &&
        recipe.InputExtensions.All(extension =>
            extension.Equals(".dsf", StringComparison.OrdinalIgnoreCase)) &&
        recipe.OutputExtension?.Equals(".wv", StringComparison.OrdinalIgnoreCase) == true &&
        (recipe.Codec ?? "wv").Trim().ToLowerInvariant() is "wv" or "wavpack";

    internal static string[] RequiredEncoders(IngestMusicConfiguration configuration)
    {
        return configuration.Profile.Ingest.Recipes
            .Where(recipe => recipe.Enabled &&
                recipe.Action == MusicLibraryTools.LibraryIngestAction.Transcode &&
                !IngestTranscodeSettingsResolver.UsesSharedEngine(recipe) &&
                !RequiresWavpack(recipe))
            .Select(recipe =>
            {
                string codec = (recipe.Codec ?? recipe.OutputExtension ?? "")
                    .Trim().TrimStart('.').ToLowerInvariant();
                return codec is "aac" or "m4a"
                    ? recipe.Encoder
                    : codec == "flac" ? "flac" : recipe.Encoder ?? codec;
            })
            .Where(encoder => !string.IsNullOrWhiteSpace(encoder))
            .Select(encoder => encoder!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool PathsOverlap(string first, string second)
    {
        string left = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        string right = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return PathComparer.Equals(left, right) ||
            left.StartsWith(right + Path.DirectorySeparatorChar, PathComparison) ||
            right.StartsWith(left + Path.DirectorySeparatorChar, PathComparison);
    }

    private static bool IsPathWithinRoot(
        string path,
        string root)
    {
        string candidate =
            Path.GetFullPath(path);
        string parent =
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(root)) +
            Path.DirectorySeparatorChar;
        return candidate.StartsWith(
            parent,
            PathComparison);
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
