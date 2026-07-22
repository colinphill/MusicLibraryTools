using System.Diagnostics;

namespace MusicLibrary.Core.Services;

public sealed class WavpackRunner : IWavpackRunner
{
    public Task PreflightAsync(
        string executable,
        CancellationToken ct = default) =>
        RunAsync(executable, ["--version"], ct);

    public Task EncodeDsdAsync(
        string executable,
        string input,
        string output,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        if (!Path.GetExtension(input).Equals(".dsf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "WavPack DSD encoding requires a .dsf input.");
        if (!Path.GetExtension(output).Equals(".wv", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "WavPack DSD encoding requires a .wv output.");

        // -v performs WavPack's lossless verification pass. The explicit -o form works on
        // Windows and is required by the macOS/Linux command-line implementation.
        return RunAsync(executable,
            ["-q", "-y", "-v", "--import-id3", input, "-o", output], ct);
    }

    private static async Task RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
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
            if (!process.Start())
                throw new InvalidOperationException("Unable to start WavPack.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Unable to start WavPack at '{executable}': {ex.Message}", ex);
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
                // Preserve the cancellation as the primary failure.
            }
            throw;
        }

        string standardOutput = await stdout.ConfigureAwait(false);
        string standardError = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(standardError)
                ? standardOutput.Trim()
                : standardError.Trim();
            throw new InvalidOperationException(
                $"WavPack exited with code {process.ExitCode}: {detail}");
        }
    }
}
