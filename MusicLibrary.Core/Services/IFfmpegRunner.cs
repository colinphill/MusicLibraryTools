namespace MusicLibrary.Core.Services;

public interface IFfmpegRunner
{
    Task PreflightAsync(string executable, string requiredEncoder, CancellationToken ct = default);
    Task ConvertAlacToFlacAsync(string executable, string input, string output, CancellationToken ct = default);
    Task DeriveCdFlacAsync(string executable, string input, string output, CancellationToken ct = default);
    Task EncodeAacAsync(string executable, string encoder, int bitrateKbps, string input, string output, CancellationToken ct = default);
    Task<string> ComputeDecodedAudioHashAsync(string executable, string input, CancellationToken ct = default);
}
