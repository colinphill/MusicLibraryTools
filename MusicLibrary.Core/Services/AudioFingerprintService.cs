using System.Diagnostics;
using System.Text.Json;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public sealed record AudioFingerprint(
    string Path,
    string Fingerprint,
    TimeSpan Duration,
    int LookupDurationSeconds,
    string Algorithm = "Chromaprint");

public interface IAudioFingerprintService
{
    Task<AudioFingerprint> GenerateAsync(
        string path,
        CancellationToken ct = default);

    Task<AudioFingerprint> GenerateAsync(
        string path,
        IProgress<OperationProgress>? progress,
        CancellationToken ct = default) =>
        GenerateAsync(path, ct);
}

public interface IFpcalcRunner
{
    Task<AudioFingerprint> GenerateAsync(
        string executable,
        string path,
        CancellationToken ct = default);
}

public enum FpcalcExecutableSource
{
    Configured,
    ApplicationBundle,
    SystemPath,
}

public sealed record FpcalcExecutableResolution(
    string Executable,
    FpcalcExecutableSource Source);

public interface IFpcalcExecutableResolver
{
    FpcalcExecutableResolution Resolve(
        string? configuredExecutable);
}

/// <summary>
/// Resolves a personally configured executable first, then an application-local
/// bundle, and finally the platform search path. Explicit file paths are
/// validated before a long-running discovery operation begins.
/// </summary>
public sealed class FpcalcExecutableResolver :
    IFpcalcExecutableResolver
{
    private readonly string _baseDirectory;

    public FpcalcExecutableResolver()
        : this(AppContext.BaseDirectory)
    {
    }

    public FpcalcExecutableResolver(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            baseDirectory);
        _baseDirectory = Path.GetFullPath(baseDirectory);
    }

    public FpcalcExecutableResolution Resolve(
        string? configuredExecutable)
    {
        string? configured = configuredExecutable?.Trim();
        bool useDefault =
            string.IsNullOrWhiteSpace(configured) ||
            configured.Equals(
                "fpcalc",
                StringComparison.OrdinalIgnoreCase) ||
            configured.Equals(
                "fpcalc.exe",
                StringComparison.OrdinalIgnoreCase);
        if (!useDefault)
        {
            if (!LooksLikePath(configured!))
                return new(
                    configured!,
                    FpcalcExecutableSource.Configured);
            string explicitPath =
                Path.GetFullPath(configured!);
            if (!File.Exists(explicitPath))
                throw new FileNotFoundException(
                    "The configured fpcalc executable does not exist.",
                    explicitPath);
            return new(
                explicitPath,
                FpcalcExecutableSource.Configured);
        }

        foreach (string candidate in BundleCandidates())
            if (File.Exists(candidate))
                return new(
                    candidate,
                    FpcalcExecutableSource.ApplicationBundle);

        return new(
            OperatingSystem.IsWindows()
                ? "fpcalc.exe"
                : "fpcalc",
            FpcalcExecutableSource.SystemPath);
    }

    private IEnumerable<string> BundleCandidates()
    {
        string fileName = OperatingSystem.IsWindows()
            ? "fpcalc.exe"
            : "fpcalc";
        yield return Path.Combine(
            _baseDirectory,
            fileName);
        yield return Path.Combine(
            _baseDirectory,
            "tools",
            fileName);
        yield return Path.Combine(
            _baseDirectory,
            "tools",
            "chromaprint",
            fileName);
        yield return Path.Combine(
            _baseDirectory,
            "runtimes",
            RuntimeIdentifier(),
            "native",
            fileName);
    }

    private static bool LooksLikePath(string value) =>
        Path.IsPathRooted(value) ||
        value.Contains(Path.DirectorySeparatorChar) ||
        value.Contains(Path.AltDirectorySeparatorChar);

    private static string RuntimeIdentifier()
    {
        string os = OperatingSystem.IsWindows()
            ? "win"
            : OperatingSystem.IsMacOS()
                ? "osx"
                : "linux";
        string architecture =
            System.Runtime.InteropServices.RuntimeInformation
                .ProcessArchitecture
                .ToString()
                .ToLowerInvariant();
        return $"{os}-{architecture}";
    }
}

/// <summary>
/// Generates the local Chromaprint value and whole-file duration used for an
/// AcoustID lookup. AcoustID itself is assigned by the remote service.
/// </summary>
public sealed class AudioFingerprintService(
    IFpcalcRunner fpcalc,
    IAppSettings settings,
    IAudioPayloadIdentityService? payloadIdentities = null,
    IAudioFingerprintCache? cache = null,
    IFpcalcExecutableResolver? executableResolver = null,
    IAudioFingerprintInputService? inputService = null)
    : IAudioFingerprintService
{
    public const string ExecutablePreferenceKey = "tools.fpcalcPath";

    public async Task<AudioFingerprint> GenerateAsync(
        string path,
        CancellationToken ct = default) =>
        await GenerateAsync(path, progress: null, ct);

    public async Task<AudioFingerprint> GenerateAsync(
        string path,
        IProgress<OperationProgress>? progress,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException(
                "The audio file to fingerprint does not exist.", fullPath);
        string? configuredExecutable =
            settings.GetPreference(ExecutablePreferenceKey);
        FpcalcExecutableResolution resolution =
            (executableResolver ??
             new FpcalcExecutableResolver())
            .Resolve(configuredExecutable);
        string executable = resolution.Executable;
        progress?.Report(new(
            OperationPhase.IndexingSources,
            0,
            1,
            fullPath,
            $"Generating Chromaprint for {Path.GetFileName(fullPath)}"));
        string? payloadIdentity = null;
        if (payloadIdentities is not null && cache is not null)
        {
            try
            {
                payloadIdentity = await payloadIdentities.ComputeAsync(
                        fullPath, progress, ct)
                    .ConfigureAwait(false);
                AudioFingerprint? cached = await cache.ReadAsync(
                        payloadIdentity, fullPath, ct)
                    .ConfigureAwait(false);
                if (cached is not null)
                {
                    progress?.Report(new(
                        OperationPhase.Completed,
                        1,
                        1,
                        fullPath,
                        $"Loaded cached Chromaprint for " +
                        Path.GetFileName(fullPath)));
                    return cached;
                }
            }
            catch (Exception error) when (
                error is not OperationCanceledException)
            {
                payloadIdentity = null;
            }
        }
        await using PreparedFingerprintInput input =
            inputService is null
                ? new(fullPath, fullPath)
                : await inputService.PrepareAsync(
                        fullPath,
                        progress,
                        ct)
                    .ConfigureAwait(false);
        AudioFingerprint result =
            await fpcalc.GenerateAsync(
                    executable,
                    input.DecoderPath,
                    ct)
                .ConfigureAwait(false);
        if (!PathComparer.Equals(
                result.Path,
                fullPath))
            result = result with { Path = fullPath };
        if (payloadIdentity is not null && cache is not null)
        {
            try
            {
                await cache.WriteAsync(payloadIdentity, result, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (
                error is not OperationCanceledException)
            {
                // A cache failure never invalidates a generated fingerprint.
            }
        }
        progress?.Report(new(
            OperationPhase.Completed,
            1,
            1,
            fullPath,
            $"Generated Chromaprint for {Path.GetFileName(fullPath)}"));
        return result;
    }

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}

public sealed class FpcalcRunner : IFpcalcRunner
{
    public async Task<AudioFingerprint> GenerateAsync(
        string executable,
        string path,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-json");
        start.ArgumentList.Add("-length");
        start.ArgumentList.Add("0");
        start.ArgumentList.Add(path);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Unable to start fpcalc.");
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                $"Unable to start fpcalc at '{executable}': {error.Message}", error);
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None)
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
                $"fpcalc exited with code {process.ExitCode}: {detail}");
        }
        return ParseOutput(path, output);
    }

    public static AudioFingerprint ParseOutput(string path, string output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        try
        {
            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("duration", out JsonElement durationElement) ||
                !durationElement.TryGetDouble(out double durationSeconds) ||
                !double.IsFinite(durationSeconds) ||
                durationSeconds <= 0)
                throw new InvalidDataException(
                    "fpcalc did not report a positive whole-file duration.");
            if (!root.TryGetProperty("fingerprint", out JsonElement fingerprintElement) ||
                fingerprintElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(fingerprintElement.GetString()))
                throw new InvalidDataException(
                    "fpcalc did not report a compressed Chromaprint fingerprint.");

            string fingerprint = fingerprintElement.GetString()!;
            return new(
                Path.GetFullPath(path),
                fingerprint,
                TimeSpan.FromSeconds(durationSeconds),
                Math.Max(1, checked((int)Math.Round(
                    durationSeconds, MidpointRounding.AwayFromZero))));
        }
        catch (JsonException error)
        {
            throw new InvalidDataException(
                "fpcalc returned malformed JSON output.", error);
        }
    }
}
