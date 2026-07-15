using System.Diagnostics;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

public interface IIndexBenchmarkService
{
    Task<IndexBenchmarkResult> BenchmarkAsync(
        int currentParallelism,
        IProgress<IndexBenchmarkProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Runs a bounded, read-only metadata benchmark independently for each configured scan root.
/// It never writes tags or the metadata cache and deliberately omits artwork reads.
/// </summary>
public sealed class IndexBenchmarkService(IAppSettings settings) : IIndexBenchmarkService
{
    public const string ReaderParallelismPreference = "Index.ReaderParallelism";
    internal static int MaximumSampleFiles = 224;
    internal static int MaximumBenchmarkParallelism = 32;

    public Task<IndexBenchmarkResult> BenchmarkAsync(
        int currentParallelism,
        IProgress<IndexBenchmarkProgress>? progress = null,
        CancellationToken ct = default)
    {
        var snapshot = settings.GetSnapshot();
        if (snapshot.Configuration is null)
            throw new InvalidOperationException("No library configuration is loaded.");
        var roots = snapshot.Configuration.IndexLocations
            .Select(location => Path.TrimEndingDirectorySeparator(Path.GetFullPath(location.Target)))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToList();
        return Task.Run(() => Benchmark(roots, Math.Clamp(currentParallelism, 1, 64), progress, ct), ct);
    }

    private static IndexBenchmarkResult Benchmark(
        IReadOnlyList<string> roots,
        int currentParallelism,
        IProgress<IndexBenchmarkProgress>? progress,
        CancellationToken ct)
    {
        var results = new List<IndexRootBenchmarkResult>(roots.Count);
        var progressSync = new object();
        void Report(IndexBenchmarkProgress value)
        {
            if (progress is null)
                return;
            lock (progressSync)
                progress.Report(value);
        }
        for (int rootIndex = 0; rootIndex < roots.Count; rootIndex++)
        {
            ct.ThrowIfCancellationRequested();
            string root = roots[rootIndex];
            var enumerationClock = Stopwatch.StartNew();
            Report(new(root, rootIndex + 1, roots.Count, 0, 0,
                MaximumSampleFiles, TimeSpan.Zero, "Collecting sample"));
            var sample = new List<string>(MaximumSampleFiles);
            try
            {
                foreach (var entry in new MusicFileEnumerator(root))
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.FileType == MFEType.MusicFile)
                        sample.Add(entry.Name);
                    if (sample.Count >= MaximumSampleFiles)
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new(root, 0, enumerationClock.Elapsed, [], 0, ex.Message));
                continue;
            }
            ct.ThrowIfCancellationRequested();
            enumerationClock.Stop();
            if (sample.Count == 0)
            {
                results.Add(new(root, 0, enumerationClock.Elapsed, [], 0,
                    "No supported music files were found."));
                continue;
            }

            Shuffle(sample);
            int maximum = Math.Min(Math.Min(MaximumBenchmarkParallelism, 64), sample.Count);
            var levels = PowersOfTwo(maximum).Append(Math.Min(currentParallelism, maximum))
                .Distinct().OrderBy(value => value).ToList();
            // Each level receives an equally sized, disjoint slice and must have at least one file
            // per reader. Dropping oversized levels is more honest than timing idle workers.
            while (levels.Count > 1 && levels[^1] > sample.Count / levels.Count)
                levels.RemoveAt(levels.Count - 1);
            var trials = new List<IndexReaderBenchmarkTrial>(levels.Count);
            int sampleOffset = 0;
            for (int levelIndex = 0; levelIndex < levels.Count; levelIndex++)
            {
                int parallelism = levels[levelIndex];
                int trialCount = sample.Count / levels.Count +
                    (levelIndex < sample.Count % levels.Count ? 1 : 0);
                var trialSample = sample.GetRange(sampleOffset, trialCount);
                sampleOffset += trialCount;
                ct.ThrowIfCancellationRequested();
                int completed = 0, succeeded = 0, failed = 0;
                var clock = Stopwatch.StartNew();
                Report(new(root, rootIndex + 1, roots.Count, parallelism, 0,
                    trialSample.Count, TimeSpan.Zero, "Reading metadata"));
                try
                {
                    Parallel.ForEach(trialSample, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = parallelism,
                        CancellationToken = ct,
                    }, path =>
                    {
                        try
                        {
                            var file = MediaFile.GetFile(path, readOnly: true, readArtwork: false);
                            // Force the parser's primary projections so lazy providers cannot make a
                            // benchmark look faster by postponing their actual metadata work.
                            _ = file.Codecs.First().DurationInFrames;
                            _ = file.Tags.First().GetKnownMetadata().Count();
                            Interlocked.Increment(ref succeeded);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Interlocked.Increment(ref failed);
                        }
                        int done = Interlocked.Increment(ref completed);
                        if (done == trialSample.Count || done % 16 == 0)
                            Report(new(root, rootIndex + 1, roots.Count, parallelism,
                                done, trialSample.Count, clock.Elapsed, "Reading metadata"));
                    });
                }
                catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException))
                {
                    throw new OperationCanceledException(ct);
                }
                clock.Stop();
                double rate = clock.Elapsed.TotalSeconds > 0
                    ? succeeded / clock.Elapsed.TotalSeconds
                    : 0;
                trials.Add(new(parallelism, succeeded, failed, clock.Elapsed, rate));
            }

            double peak = trials.Max(trial => trial.FilesPerSecond);
            if (peak <= 0)
            {
                results.Add(new(root, sample.Count, enumerationClock.Elapsed, trials, 0,
                    "All sampled metadata reads failed."));
                continue;
            }
            int recommended = trials
                .Where(trial => trial.SuccessfulReads > 0 && trial.FilesPerSecond >= peak * 0.95)
                .Select(trial => trial.Parallelism)
                .DefaultIfEmpty(1)
                .Min();
            results.Add(new(root, sample.Count, enumerationClock.Elapsed, trials, recommended, null));
        }
        return new(results);
    }

    private static IEnumerable<int> PowersOfTwo(int maximum)
    {
        for (int value = 1; value <= maximum; value *= 2)
            yield return value;
        if (maximum > 1 && (maximum & (maximum - 1)) != 0)
            yield return maximum;
    }

    private static void Shuffle(List<string> paths)
    {
        // Fixed seed keeps repeat runs comparable while spreading adjacent albums/formats across
        // concurrency levels. Each path is still read exactly once in the entire benchmark.
        var random = new Random(0x4D4C54);
        for (int index = paths.Count - 1; index > 0; index--)
        {
            int swap = random.Next(index + 1);
            (paths[index], paths[swap]) = (paths[swap], paths[index]);
        }
    }
}
