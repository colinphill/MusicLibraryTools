using MusicLibrary.App.ViewModels;
using MusicLibrary.Core.Services;
using MusicLibraryTools;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class IndexBenchmarkServiceTests
{
    [Fact]
    public async Task BenchmarkMeasuresEachBoundedLevelAndChoosesSmallestNearPeakLevel()
    {
        using var temp = new TempDirectory();
        string root = temp.Directory("music");
        for (int index = 1; index <= 12; index++)
            File.Copy(MediaFixtures.Path_("sample.flac"), Path.Combine(root, $"song-{index}.flac"));
        var settings = LoadSettings(temp, root);
        var reports = new SynchronousProgress<MusicLibrary.Core.Models.IndexBenchmarkProgress>();

        var result = await new IndexBenchmarkService(settings).BenchmarkAsync(4, reports);

        var measured = Assert.Single(result.Roots);
        Assert.True(measured.Succeeded);
        Assert.Equal(12, measured.SampleCount);
        Assert.Equal([1, 2, 4], measured.Trials.Select(trial => trial.Parallelism));
        Assert.All(measured.Trials, trial =>
        {
            Assert.Equal(4, trial.SuccessfulReads);
            Assert.Equal(0, trial.FailedReads);
            Assert.InRange(trial.Parallelism, 1, trial.SuccessfulReads);
        });
        Assert.Equal(measured.SampleCount, measured.Trials.Sum(trial =>
            trial.SuccessfulReads + trial.FailedReads));
        double peak = measured.Trials.Max(trial => trial.FilesPerSecond);
        var recommended = Assert.Single(measured.Trials,
            trial => trial.Parallelism == measured.RecommendedParallelism);
        Assert.True(recommended.FilesPerSecond >= peak * 0.95);
        Assert.DoesNotContain(measured.Trials, trial =>
            trial.Parallelism < measured.RecommendedParallelism && trial.FilesPerSecond >= peak * 0.95);
        Assert.Contains(reports.Values, report => report.Phase == "Collecting sample");
        Assert.Contains(reports.Values, report => report.Phase == "Reading metadata");
    }

    [Fact]
    public async Task BenchmarkReportsAnUnavailableRootWithoutFailingTheWholeRun()
    {
        using var temp = new TempDirectory();
        string missing = Path.Combine(temp.Path, "offline-share");
        var settings = LoadSettings(temp, missing);

        var result = await new IndexBenchmarkService(settings).BenchmarkAsync(8);

        var root = Assert.Single(result.Roots);
        Assert.False(root.Succeeded);
        Assert.NotNull(root.Error);
        Assert.Empty(root.Trials);
    }

    [Fact]
    public void ReaderControlPersistsAndBenchmarkSummaryRetainsPerRootMeasurements()
    {
        using var temp = new TempDirectory();
        var settings = new AppSettings(Path.Combine(temp.Path, "settings.json"));
        using var library = new LibraryService(settings);
        var viewModel = new LibraryViewModel(library, new IndexBenchmarkService(settings), settings);
        viewModel.ReaderParallelism = 7;
        var result = new MusicLibrary.Core.Models.IndexBenchmarkResult(
        [
            new("Z:\\Music", 96, TimeSpan.FromSeconds(2),
                [
                    new(1, 96, 0, TimeSpan.FromSeconds(9.6), 10),
                    new(4, 96, 0, TimeSpan.FromSeconds(3.2), 30),
                ], 4, null),
        ]);

        string summary = LibraryViewModel.DescribeBenchmark(result, 4);

        Assert.Equal("7", settings.GetPreference(IndexBenchmarkService.ReaderParallelismPreference));
        Assert.Contains("Z:\\Music", summary);
        Assert.Contains("1× 10.0/s", summary);
        Assert.Contains("recommend 4", summary);
    }

    private static AppSettings LoadSettings(TempDirectory temp, string root)
    {
        string config = Path.Combine(temp.Path, "library.xml");
        new EditableLibraryConfig
        {
            DatabaseFile = Path.Combine(temp.Path, "cache.db"),
            IndexTargets = [new() { Target = root }],
        }.Save(config);
        var settings = new AppSettings(Path.Combine(temp.Path, "settings.json"));
        settings.LoadConfig(config);
        return settings;
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "index-benchmark-tests-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => System.IO.Directory.CreateDirectory(Path);
        public string Directory(string name)
        {
            string path = System.IO.Path.Combine(Path, name);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }
        public void Dispose()
        {
            try { System.IO.Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
