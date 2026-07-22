namespace MusicLibrary.Core.Services;

public sealed record FfmpegTranscodeOptions(
    string Codec,
    string? Encoder = null,
    int? BitrateKbps = null,
    int? SampleRateHz = null,
    int? BitsPerSample = null,
    int? Channels = null);

public interface IFfmpegRunner
{
    Task PreflightAsync(string executable, string requiredEncoder, CancellationToken ct = default);
    Task ConvertAlacToFlacAsync(string executable, string input, string output, CancellationToken ct = default);
    Task DeriveCdFlacAsync(string executable, string input, string output, CancellationToken ct = default);
    Task EncodeAacAsync(string executable, string encoder, int bitrateKbps, string input, string output, CancellationToken ct = default);
    Task RemuxAsync(
        string executable,
        string input,
        string output,
        CancellationToken ct = default) =>
        TranscodeAsync(executable, input, output,
            new FfmpegTranscodeOptions("copy", "copy"), ct);
    async Task<string> ResolveEncoderAsync(
        string executable,
        IReadOnlyList<string> candidates,
        CancellationToken ct = default)
    {
        Exception? lastError = null;
        foreach (string candidate in candidates.Where(candidate =>
                     !string.IsNullOrWhiteSpace(candidate)).Distinct(StringComparer.Ordinal))
        {
            try
            {
                await PreflightAsync(executable, candidate, ct).ConfigureAwait(false);
                return candidate;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) { lastError = error; }
        }
        throw new InvalidOperationException(
            "ffmpeg does not provide any configured encoder candidate.", lastError);
    }
    Task TranscodeAsync(
        string executable,
        string input,
        string output,
        FfmpegTranscodeOptions options,
        CancellationToken ct = default) =>
        options.Codec.Equals("aac", StringComparison.OrdinalIgnoreCase)
            ? EncodeAacAsync(executable, options.Encoder ?? "aac",
                options.BitrateKbps ?? 256, input, output, ct)
            : options.Codec.Equals("flac", StringComparison.OrdinalIgnoreCase) &&
              options.SampleRateHz == 44_100 && options.BitsPerSample == 16
                ? DeriveCdFlacAsync(executable, input, output, ct)
                : options.Codec.Equals("flac", StringComparison.OrdinalIgnoreCase)
                    ? ConvertAlacToFlacAsync(executable, input, output, ct)
                    : throw new NotSupportedException(
                        $"The FFmpeg runner does not expose codec '{options.Codec}'.");
    Task<string> ComputeDecodedAudioHashAsync(string executable, string input, CancellationToken ct = default);
}
