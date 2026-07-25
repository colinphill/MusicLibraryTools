using System.Text.Json;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public interface IAudioSourceLayoutInspector
{
    Task<AudioSourceLayout> InspectAsync(
        string sourcePath,
        CancellationToken ct = default);
}

public sealed class AudioSourceLayoutInspector(
    IAppSettings settings,
    IManagedProcessRunner processes) :
    IAudioSourceLayoutInspector
{
    public async Task<AudioSourceLayout> InspectAsync(
        string sourcePath,
        CancellationToken ct = default)
    {
        string ffmpeg = settings.GetSnapshot()
            .Configuration?.FfmpegPath ?? "ffmpeg";
        string ffprobe = ResolveFfprobe(ffmpeg);
        ManagedProcessResult result =
            await processes.RunAsync(
                ffprobe,
                [
                    "-v",
                    "error",
                    "-show_entries",
                    "stream=codec_type",
                    "-show_entries",
                    "program=program_id:program_stream=codec_type",
                    "-of",
                    "json",
                    sourcePath,
                ],
                ct: ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"ffprobe failed with exit code " +
                $"{result.ExitCode}: " +
                FirstDiagnostic(result));

        using JsonDocument document =
            JsonDocument.Parse(result.StandardOutput);
        JsonElement root = document.RootElement;
        int audioStreams = CountStreams(
            root,
            "streams",
            audio: true);
        int nonAudioStreams = CountStreams(
            root,
            "streams",
            audio: false);
        int audioPrograms = 0;
        if (root.TryGetProperty(
                "programs",
                out JsonElement programs) &&
            programs.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement program in
                     programs.EnumerateArray())
            {
                if (CountStreams(
                        program,
                        "streams",
                        audio: true) > 0)
                    audioPrograms++;
            }
        }
        if (audioPrograms == 0 && audioStreams > 0)
            audioPrograms = 1;
        return new(
            audioStreams,
            nonAudioStreams,
            audioPrograms);
    }

    internal static string ResolveFfprobe(
        string ffmpeg)
    {
        if (string.IsNullOrWhiteSpace(ffmpeg) ||
            string.IsNullOrWhiteSpace(
                Path.GetDirectoryName(ffmpeg)))
            return OperatingSystem.IsWindows()
                ? "ffprobe.exe"
                : "ffprobe";
        string directory =
            Path.GetDirectoryName(
                Path.GetFullPath(ffmpeg))!;
        return Path.Combine(
            directory,
            OperatingSystem.IsWindows()
                ? "ffprobe.exe"
                : "ffprobe");
    }

    private static int CountStreams(
        JsonElement owner,
        string property,
        bool audio)
    {
        if (!owner.TryGetProperty(
                property,
                out JsonElement streams) ||
            streams.ValueKind != JsonValueKind.Array)
            return 0;
        return streams.EnumerateArray().Count(
            stream =>
            {
                string? codecType =
                    stream.TryGetProperty(
                        "codec_type",
                        out JsonElement value)
                        ? value.GetString()
                        : null;
                return audio
                    ? string.Equals(
                        codecType,
                        "audio",
                        StringComparison.Ordinal)
                    : !string.Equals(
                        codecType,
                        "audio",
                        StringComparison.Ordinal);
            });
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
            "no diagnostic output";
    }
}
