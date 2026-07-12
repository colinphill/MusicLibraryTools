using System.Diagnostics;
using System.Text;

namespace MusicLibrary.Core.Services;

public sealed class FfmpegRunner : IFfmpegRunner
{
    public async Task PreflightAsync(string executable, string requiredEncoder, CancellationToken ct = default)
    {
        string output = await RunAsync(executable, ["-hide_banner", "-encoders"], ct);
        if (!output.Contains(requiredEncoder, StringComparison.Ordinal))
            throw new InvalidOperationException($"ffmpeg does not provide the required '{requiredEncoder}' encoder.");
    }

    public Task ConvertAlacToFlacAsync(string executable, string input, string output, CancellationToken ct = default)
        => RunNoOutputAsync(executable, ["-y", "-hide_banner", "-loglevel", "error", "-i", input,
            "-map", "0:a:0", "-map_metadata", "0", "-c:a", "flac", output], ct);

    public Task DeriveCdFlacAsync(string executable, string input, string output, CancellationToken ct = default)
        => RunNoOutputAsync(executable, ["-y", "-hide_banner", "-loglevel", "error", "-i", input,
            "-map", "0:a:0", "-map_metadata", "0", "-af",
            "aresample=44100:osf=s16:dither_method=triangular", "-c:a", "flac", output], ct);

    public Task EncodeAacAsync(string executable, string encoder, int bitrateKbps, string input, string output, CancellationToken ct = default)
        => RunNoOutputAsync(executable, ["-y", "-hide_banner", "-loglevel", "error", "-i", input,
            "-map", "0:a:0", "-map_metadata", "0", "-c:a", encoder, "-b:a", $"{bitrateKbps}k", output], ct);

    public async Task<string> ComputeDecodedAudioHashAsync(string executable, string input, CancellationToken ct = default)
    {
        string output = await RunAsync(executable, ["-hide_banner", "-loglevel", "error", "-i", input,
            "-map", "0:a:0", "-c:a", "pcm_s32le", "-f", "hash", "-hash", "sha256", "-"], ct);
        string? hash = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => line.StartsWith("SHA256=", StringComparison.OrdinalIgnoreCase));
        return hash ?? throw new InvalidDataException("ffmpeg did not produce an audio hash.");
    }

    private static async Task RunNoOutputAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
        => _ = await RunAsync(executable, arguments, ct);

    private static async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Unable to start ffmpeg.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Unable to start ffmpeg at '{executable}': {ex.Message}", ex);
        }
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch { }
            throw;
        }
        string outText = await stdout;
        string errText = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}: {errText.Trim()}");
        return outText + Environment.NewLine + errText;
    }
}
