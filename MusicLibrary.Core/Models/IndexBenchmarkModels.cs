namespace MusicLibrary.Core.Models;

public sealed record IndexReaderBenchmarkTrial(
    int Parallelism,
    int SuccessfulReads,
    int FailedReads,
    TimeSpan Elapsed,
    double FilesPerSecond);

public sealed record IndexRootBenchmarkResult(
    string Root,
    int SampleCount,
    TimeSpan EnumerationElapsed,
    IReadOnlyList<IndexReaderBenchmarkTrial> Trials,
    int RecommendedParallelism,
    string? Error)
{
    public bool Succeeded => Error is null && Trials.Count > 0;
}

public sealed record IndexBenchmarkResult(IReadOnlyList<IndexRootBenchmarkResult> Roots);

public sealed record IndexBenchmarkProgress(
    string Root,
    int RootIndex,
    int RootCount,
    int Parallelism,
    int CompletedReads,
    int TotalReads,
    TimeSpan Elapsed,
    string Phase);
