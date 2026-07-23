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

/// <summary>
/// Generates the local Chromaprint value and whole-file duration used for an
/// AcoustID lookup. AcoustID itself is assigned by the remote service.
/// </summary>
public sealed class AudioFingerprintService(
    IFpcalcRunner fpcalc,
    IAppSettings settings) : IAudioFingerprintService
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
        string executable = string.IsNullOrWhiteSpace(configuredExecutable)
            ? "fpcalc"
            : configuredExecutable;
        progress?.Report(new(
            OperationPhase.IndexingSources,
            0,
            1,
            fullPath,
            $"Generating Chromaprint for {Path.GetFileName(fullPath)}"));
        AudioFingerprint result =
            await fpcalc.GenerateAsync(executable, fullPath, ct);
        progress?.Report(new(
            OperationPhase.Completed,
            1,
            1,
            fullPath,
            $"Generated Chromaprint for {Path.GetFileName(fullPath)}"));
        return result;
    }
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
