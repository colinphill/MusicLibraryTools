using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public interface ITranscodePcmReferenceService
{
    Task<string> CreateAsync(
        string sourcePath,
        AudioTranscodeSettings settings,
        string stagedOutputPath,
        CancellationToken ct = default);
}

public sealed class TranscodePcmReferenceService(
    IAppSettings appSettings,
    IManagedProcessRunner processes) :
    ITranscodePcmReferenceService
{
    public async Task<string> CreateAsync(
        string sourcePath,
        AudioTranscodeSettings settings,
        string stagedOutputPath,
        CancellationToken ct = default)
    {
        string referencePath = Path.Combine(
            Path.GetDirectoryName(stagedOutputPath)!,
            "." +
            Path.GetFileNameWithoutExtension(
                stagedOutputPath) +
            ".reference." +
            Guid.NewGuid().ToString("N") +
            ".flac");
        string ffmpeg = appSettings.GetSnapshot()
            .Configuration?.FfmpegPath ?? "ffmpeg";
        var arguments = new List<string>
        {
            "-y",
            "-hide_banner",
            "-nostdin",
            "-loglevel",
            "error",
            "-i",
            sourcePath,
            "-map",
            "0:a:0",
            "-map_metadata",
            "-1",
        };
        AudioTranscodeAdapter.AddSampleConversion(
            arguments,
            settings,
            sourcePath);
        arguments.Add("-c:a");
        arguments.Add("flac");
        int? effectiveBits =
            AudioTranscodeAdapter
                .EffectiveIntegerConversionBitDepth(
                    settings,
                    sourcePath);
        if (effectiveBits is { } bits)
        {
            arguments.Add("-sample_fmt");
            arguments.Add(
                bits <= 16 ? "s16" : "s32");
        }
        arguments.Add(referencePath);
        try
        {
            ManagedProcessResult result =
                await processes.RunAsync(
                    ffmpeg,
                    arguments,
                    ct: ct).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(
                    "FFmpeg could not create the transformed PCM " +
                    $"reference: {FirstDiagnostic(result)}");
            if (!File.Exists(referencePath) ||
                new FileInfo(referencePath).Length == 0)
                throw new InvalidDataException(
                    "FFmpeg produced no transformed PCM reference.");
            return referencePath;
        }
        catch
        {
            TryDelete(referencePath);
            throw;
        }
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
            // The normal stage cleanup will retry removal of sibling files.
        }
    }
}
