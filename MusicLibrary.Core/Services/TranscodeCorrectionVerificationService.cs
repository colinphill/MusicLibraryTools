using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public interface ITranscodeCorrectionVerificationService
{
    Task<string> ReconstructAsync(
        string stagedOutputPath,
        AudioTranscodeToolKind tool,
        CancellationToken ct = default);
}

public sealed class TranscodeCorrectionVerificationService(
    IAppSettings settings,
    IManagedProcessRunner processes) :
    ITranscodeCorrectionVerificationService
{
    public async Task<string> ReconstructAsync(
        string stagedOutputPath,
        AudioTranscodeToolKind tool,
        CancellationToken ct = default)
    {
        string reconstructedPath = Path.Combine(
            Path.GetDirectoryName(stagedOutputPath)!,
            "." +
            Path.GetFileNameWithoutExtension(
                stagedOutputPath) +
            ".reconstructed." +
            Guid.NewGuid().ToString("N") +
            ".wav");
        (string executable, IReadOnlyList<string> arguments) =
            tool switch
            {
                AudioTranscodeToolKind.WavPack =>
                    WavPackCommand(
                        stagedOutputPath,
                        reconstructedPath),
                AudioTranscodeToolKind.OptimFrog =>
                    OptimFrogCommand(
                        stagedOutputPath,
                        reconstructedPath),
                _ => throw new InvalidOperationException(
                    "The selected encoder does not support a " +
                    "correction stream."),
            };
        try
        {
            ManagedProcessResult result =
                await processes.RunAsync(
                    executable,
                    arguments,
                    ct: ct).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(
                    "Correction reconstruction failed: " +
                    FirstDiagnostic(result));
            if (!File.Exists(reconstructedPath) ||
                new FileInfo(reconstructedPath).Length == 0)
                throw new InvalidDataException(
                    "Correction reconstruction produced no audio.");
            return reconstructedPath;
        }
        catch
        {
            TryDelete(reconstructedPath);
            throw;
        }
    }

    private (string, IReadOnlyList<string>)
        WavPackCommand(
            string input,
            string output)
    {
        string configured = settings.GetSnapshot()
            .Configuration?.WavpackPath ?? "wavpack";
        string executable = ResolveSiblingTool(
            configured,
            "wvunpack");
        return (
            executable,
            ["-q", "-y", input, "-o", output]);
    }

    private (string, IReadOnlyList<string>)
        OptimFrogCommand(
            string input,
            string output)
    {
        string directory = settings.GetPreference(
                OptimFrogFingerprintInputService
                    .ToolsDirectoryPreferenceKey) ??
            Path.Combine(
                AppContext.BaseDirectory,
                "tools",
                "optimfrog");
        string executable = Path.Combine(
            directory,
            OperatingSystem.IsWindows()
                ? "ofs.exe"
                : "ofs");
        return (
            executable,
            ["--decode", input, "--output", output]);
    }

    internal static string ResolveSiblingTool(
        string configured,
        string siblingName)
    {
        string? directory =
            Path.GetDirectoryName(configured);
        if (string.IsNullOrWhiteSpace(directory))
            return OperatingSystem.IsWindows()
                ? siblingName + ".exe"
                : siblingName;
        return Path.Combine(
            Path.GetDirectoryName(
                Path.GetFullPath(configured))!,
            OperatingSystem.IsWindows()
                ? siblingName + ".exe"
                : siblingName);
    }

    private static string FirstDiagnostic(
        ManagedProcessResult result)
    {
        string text = string.IsNullOrWhiteSpace(
                result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        return text.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .FirstOrDefault() ??
            $"exit code {result.ExitCode}";
    }

    private static void TryDelete(
        string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // The transcode stage cleanup will retry sibling cleanup.
        }
    }
}
