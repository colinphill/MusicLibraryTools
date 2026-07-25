using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public sealed record TranscodeConcurrencySettings(
    bool Automatic,
    int MaximumProcesses);

public sealed record TranscodeWorkItem<T>(
    int Index,
    T Value,
    string VolumeKey,
    AudioEncoderThreadingMode ThreadingMode);

public sealed record TranscodeWorkerContext(
    int WorkerCount,
    int ThreadsPerProcess,
    int CpuBudget);

public sealed record TranscodeWorkResult<T>(
    int Index,
    T Value,
    bool Succeeded,
    Exception? Error = null);

public sealed record TranscodeSchedulerProgress(
    int Completed,
    int Total,
    int Active,
    ImmutableArray<string> ActiveItems,
    TimeSpan Elapsed = default);

public interface ITranscodeWorkScheduler
{
    TranscodeConcurrencySettings Settings { get; }

    void SaveSettings(TranscodeConcurrencySettings settings);

    TranscodeWorkerContext GetWorkerContext(
        int itemCount,
        IReadOnlyCollection<AudioEncoderThreadingMode> threadingModes);

    Task<IReadOnlyList<TranscodeWorkResult<T>>> RunAsync<T>(
        IReadOnlyList<TranscodeWorkItem<T>> items,
        Func<T, int, CancellationToken, Task> action,
        Func<T, string>? describe = null,
        IProgress<TranscodeSchedulerProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed class TranscodeWorkScheduler : ITranscodeWorkScheduler
{
    public const string ConcurrencyPreference =
        "manager.workbench.transcode.concurrency.v1";

    private readonly IAppSettings _settings;
    private readonly int _cpuBudget;
    private readonly int _perVolumeLimit;
    private TranscodeConcurrencySettings _current;

    public TranscodeWorkScheduler(
        IAppSettings settings,
        int? processorCount = null,
        int perVolumeLimit = 2)
    {
        _settings = settings;
        _cpuBudget = Math.Max(
            1,
            processorCount ??
            Environment.ProcessorCount);
        _perVolumeLimit = Math.Max(1, perVolumeLimit);
        _current = Load(settings, _cpuBudget);
    }

    public TranscodeConcurrencySettings Settings => _current;

    public void SaveSettings(
        TranscodeConcurrencySettings settings)
    {
        int maximum = Math.Clamp(
            settings.MaximumProcesses,
            1,
            _cpuBudget);
        _current = settings with
        {
            MaximumProcesses = maximum,
        };
        _settings.SetPreference(
            ConcurrencyPreference,
            _current.Automatic
                ? "auto"
                : maximum.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
    }

    public TranscodeWorkerContext GetWorkerContext(
        int itemCount,
        IReadOnlyCollection<AudioEncoderThreadingMode> threadingModes)
    {
        if (itemCount <= 0)
            return new(0, 1, _cpuBudget);

        int workers;
        if (!_current.Automatic)
        {
            workers = Math.Clamp(
                _current.MaximumProcesses,
                1,
                Math.Min(itemCount, _cpuBudget));
        }
        else
        {
            bool allSingleThreaded =
                threadingModes.Count > 0 &&
                threadingModes.All(mode =>
                    mode ==
                    AudioEncoderThreadingMode.SingleThreaded);
            bool anyInternallyThreaded =
                threadingModes.Any(mode =>
                    mode ==
                    AudioEncoderThreadingMode.InternallyThreaded);
            int target = allSingleThreaded
                ? _cpuBudget
                : anyInternallyThreaded
                    ? Math.Max(1, _cpuBudget / 2)
                    : Math.Max(1, (_cpuBudget + 1) / 2);
            workers = Math.Clamp(
                target,
                1,
                Math.Min(itemCount, _cpuBudget));
        }

        int threadsPerProcess = Math.Max(
            1,
            _cpuBudget / workers);
        return new(
            workers,
            threadsPerProcess,
            _cpuBudget);
    }

    public async Task<IReadOnlyList<TranscodeWorkResult<T>>> RunAsync<T>(
        IReadOnlyList<TranscodeWorkItem<T>> items,
        Func<T, int, CancellationToken, Task> action,
        Func<T, string>? describe = null,
        IProgress<TranscodeSchedulerProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(action);
        if (items.Count == 0)
            return [];

        TranscodeWorkerContext context = GetWorkerContext(
            items.Count,
            items.Select(item =>
                item.ThreadingMode).ToArray());
        var queue = new ConcurrentQueue<TranscodeWorkItem<T>>(
            items.OrderBy(item => item.Index));
        var results =
            new ConcurrentDictionary<int, TranscodeWorkResult<T>>();
        var active = new ConcurrentDictionary<int, string>();
        var volumeGates =
            new ConcurrentDictionary<string, SemaphoreSlim>(
                PathComparer);
        int completed = 0;
        var elapsed = Stopwatch.StartNew();

        async Task WorkerAsync()
        {
            while (queue.TryDequeue(out TranscodeWorkItem<T>? item))
            {
                ct.ThrowIfCancellationRequested();
                SemaphoreSlim volumeGate =
                    volumeGates.GetOrAdd(
                        item.VolumeKey,
                        _ => new SemaphoreSlim(
                            _perVolumeLimit,
                            _perVolumeLimit));
                await volumeGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    string label = describe?.Invoke(item.Value) ??
                        item.Value?.ToString() ??
                        string.Empty;
                    active[item.Index] = label;
                    Report();
                    try
                    {
                        int threadCount =
                            item.ThreadingMode ==
                                AudioEncoderThreadingMode
                                    .ThreadCountControllable
                                ? context.ThreadsPerProcess
                                : 0;
                        await action(
                                item.Value,
                                threadCount,
                                ct)
                            .ConfigureAwait(false);
                        results[item.Index] = new(
                            item.Index,
                            item.Value,
                            true);
                    }
                    catch (OperationCanceledException)
                        when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        results[item.Index] = new(
                            item.Index,
                            item.Value,
                            false,
                            error);
                    }
                    finally
                    {
                        active.TryRemove(item.Index, out _);
                        Interlocked.Increment(ref completed);
                        Report();
                    }
                }
                finally
                {
                    volumeGate.Release();
                }
            }
        }

        void Report() => progress?.Report(new(
            Volatile.Read(ref completed),
            items.Count,
            active.Count,
            [.. active
                .OrderBy(pair => pair.Key)
                .Select(pair => pair.Value)],
            elapsed.Elapsed));

        Report();
        Task[] workers = Enumerable.Range(
                0,
                context.WorkerCount)
            .Select(_ => WorkerAsync())
            .ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
        return items
            .OrderBy(item => item.Index)
            .Select(item => results[item.Index])
            .ToArray();
    }

    private static TranscodeConcurrencySettings Load(
        IAppSettings settings,
        int cpuBudget)
    {
        string? value = settings.GetPreference(
            ConcurrencyPreference);
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals(
                "auto",
                StringComparison.OrdinalIgnoreCase))
            return new(true, cpuBudget);
        return int.TryParse(
                   value,
                   System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out int maximum)
            ? new(false, Math.Clamp(maximum, 1, cpuBudget))
            : new(true, cpuBudget);
    }

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
