using System.Collections.Concurrent;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public interface IDecodedAudioVerificationService
{
    Task<AnalysisReport> VerifyAsync(
        string ffmpegExecutable,
        IReadOnlyList<DecodedAudioPair> pairs,
        IProgress<DecodedAudioProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed class DecodedAudioVerificationService(IFfmpegRunner ffmpeg) : IDecodedAudioVerificationService
{
    public async Task<AnalysisReport> VerifyAsync(
        string ffmpegExecutable,
        IReadOnlyList<DecodedAudioPair> pairs,
        IProgress<DecodedAudioProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegExecutable);
        var paths = pairs.SelectMany(pair => new[] { pair.FirstPath, pair.SecondPath })
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToList();
        var hashes = new ConcurrentDictionary<string, string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        int completed = 0;
        await Parallel.ForEachAsync(paths, new ParallelOptions
        {
            MaxDegreeOfParallelism = 2,
            CancellationToken = ct,
        }, async (path, token) =>
        {
            hashes[path] = await ffmpeg.ComputeDecodedAudioHashAsync(ffmpegExecutable, path, token);
            int done = Interlocked.Increment(ref completed);
            progress?.Report(new(done, paths.Count, path));
        });
        var findings = pairs.Where(pair =>
                !StringComparer.OrdinalIgnoreCase.Equals(hashes[pair.FirstPath], hashes[pair.SecondPath]))
            .Select(pair => new AnalysisFinding(pair.FirstPath,
                $"Decoded PCM differs: {pair.Description}. Counterpart: {pair.SecondPath}",
                "Decoded-audio drift"))
            .ToList();
        return new("Decoded-audio verification", findings);
    }
}
