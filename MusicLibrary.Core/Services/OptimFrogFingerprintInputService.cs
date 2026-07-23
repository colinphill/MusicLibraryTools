using System.Diagnostics;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public interface IAudioFingerprintInputService
{
    Task<PreparedFingerprintInput> PrepareAsync(
        string path,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed class PreparedFingerprintInput :
    IAsyncDisposable
{
    private readonly string? _temporaryDirectory;

    public PreparedFingerprintInput(
        string originalPath,
        string decoderPath,
        string? temporaryDirectory = null)
    {
        OriginalPath = originalPath;
        DecoderPath = decoderPath;
        _temporaryDirectory = temporaryDirectory;
    }

    public string OriginalPath { get; }
    public string DecoderPath { get; }
    public bool IsTemporary => _temporaryDirectory is not null;

    public ValueTask DisposeAsync()
    {
        if (_temporaryDirectory is null)
            return ValueTask.CompletedTask;
        try
        {
            string resolved = Path.GetFullPath(
                _temporaryDirectory);
            string ownedRoot = Path.GetFullPath(
                Path.Combine(
                    Path.GetTempPath(),
                    "MusicLibraryManager",
                    "fingerprints"));
            if (resolved.StartsWith(
                    ownedRoot + Path.DirectorySeparatorChar,
                    PathComparison))
                Directory.Delete(resolved, recursive: true);
        }
        catch
        {
            // A stale temporary decode must not replace the primary result.
        }
        return ValueTask.CompletedTask;
    }

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}

public interface IOptimFrogRunner
{
    Task DecodeAsync(
        string executable,
        string sourcePath,
        string outputPath,
        CancellationToken ct = default);
}

public sealed class OptimFrogRunner : IOptimFrogRunner
{
    public async Task DecodeAsync(
        string executable,
        string sourcePath,
        string outputPath,
        CancellationToken ct = default)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--decode");
        start.ArgumentList.Add(sourcePath);
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add(outputPath);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException(
                    "Unable to start the OptimFROG decoder.");
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                $"Unable to start the OptimFROG decoder at " +
                $"'{executable}': {error.Message}",
                error);
        }

        Task<string> stdout =
            process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr =
            process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Preserve cancellation as the primary failure.
            }
            throw;
        }

        string output = await stdout.ConfigureAwait(false);
        string errorOutput = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(errorOutput)
                ? output.Trim()
                : errorOutput.Trim();
            throw new InvalidOperationException(
                $"OptimFROG decoder exited with code " +
                $"{process.ExitCode}: {detail}");
        }
    }
}

/// <summary>
/// Decodes OptimFROG streams to temporary PCM because official fpcalc builds
/// do not include an OptimFROG decoder. Other formats pass through unchanged.
/// </summary>
public sealed class OptimFrogFingerprintInputService(
    IOptimFrogRunner runner,
    IAppSettings settings) : IAudioFingerprintInputService
{
    public const string ToolsDirectoryPreferenceKey =
        "tools.optimFrogDirectory";

    public async Task<PreparedFingerprintInput> PrepareAsync(
        string path,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        string fullPath = Path.GetFullPath(path);
        string extension =
            Path.GetExtension(fullPath).ToLowerInvariant();
        string? toolBaseName = extension switch
        {
            ".ofr" => "ofr",
            ".ofs" => "ofs",
            ".off" => "off",
            _ => null,
        };
        if (toolBaseName is null)
            return new(fullPath, fullPath);

        string executable = ResolveExecutable(
            toolBaseName);
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "MusicLibraryManager",
            "fingerprints",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        string decoded = Path.Combine(
            temporaryDirectory,
            Path.GetFileNameWithoutExtension(fullPath) +
            ".wav");
        try
        {
            progress?.Report(new(
                OperationPhase.IndexingSources,
                0,
                1,
                fullPath,
                $"Decoding {Path.GetFileName(fullPath)} " +
                "for Chromaprint"));
            await runner.DecodeAsync(
                    executable,
                    fullPath,
                    decoded,
                    ct)
                .ConfigureAwait(false);
            if (!File.Exists(decoded) ||
                new FileInfo(decoded).Length == 0)
                throw new InvalidDataException(
                    "The OptimFROG decoder did not produce PCM audio.");
            return new(
                fullPath,
                decoded,
                temporaryDirectory);
        }
        catch
        {
            try
            {
                Directory.Delete(
                    temporaryDirectory,
                    recursive: true);
            }
            catch
            {
            }
            throw;
        }
    }

    private string ResolveExecutable(string toolBaseName)
    {
        string fileName = OperatingSystem.IsWindows()
            ? toolBaseName + ".exe"
            : toolBaseName;
        string? configured = settings.GetPreference(
            ToolsDirectoryPreferenceKey);
        string[] roots = string.IsNullOrWhiteSpace(configured)
            ?
            [
                Path.Combine(
                    AppContext.BaseDirectory,
                    "tools",
                    "optimfrog"),
            ]
            :
            [
                Path.GetFullPath(configured.Trim()),
            ];
        foreach (string root in roots)
        {
            string candidate = Path.Combine(
                root,
                fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        string expected = roots
            .Select(root => Path.Combine(root, fileName))
            .First();
        throw new FileNotFoundException(
            $"fpcalc cannot decode OptimFROG directly. " +
            $"Configure the official OptimFROG tools directory; " +
            $"'{fileName}' was not found.",
            expected);
    }
}
