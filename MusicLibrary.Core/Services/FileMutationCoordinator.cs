namespace MusicLibrary.Core.Services;

/// <summary>
/// Serializes filesystem mutations that touch the same normalized path. A fixed stripe set avoids
/// retaining one semaphore for every file ever edited while still allowing unrelated files to run
/// concurrently.
/// </summary>
public interface IFileMutationCoordinator
{
    ValueTask<IDisposable> AcquireAsync(string path, CancellationToken ct = default);
    ValueTask<IDisposable> AcquireAsync(IReadOnlyCollection<string> paths, CancellationToken ct = default);
}

/// <inheritdoc cref="IFileMutationCoordinator"/>
public sealed class FileMutationCoordinator : IFileMutationCoordinator
{
    private const int StripeCount = 257;
    private readonly SemaphoreSlim[] _stripes = Enumerable.Range(0, StripeCount)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();

    internal static FileMutationCoordinator Shared { get; } = new();

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public ValueTask<IDisposable> AcquireAsync(string path, CancellationToken ct = default)
        => AcquireAsync([path], ct);

    public async ValueTask<IDisposable> AcquireAsync(
        IReadOnlyCollection<string> paths,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
            return EmptyLease.Instance;

        // Sorting unique stripe IDs gives every multi-path operation the same acquisition order and
        // therefore prevents two simultaneous moves from deadlocking each other.
        var stripeIds = paths
            .Select(GetStripeId)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        var acquired = 0;
        try
        {
            foreach (var id in stripeIds)
            {
                await _stripes[id].WaitAsync(ct).ConfigureAwait(false);
                acquired++;
            }
            return new Lease(_stripes, stripeIds);
        }
        catch
        {
            for (var i = acquired - 1; i >= 0; i--)
                _stripes[stripeIds[i]].Release();
            throw;
        }
    }

    private static int GetStripeId(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return (PathComparer.GetHashCode(normalized) & int.MaxValue) % StripeCount;
    }

    private sealed class Lease(SemaphoreSlim[] stripes, int[] stripeIds) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            for (var i = stripeIds.Length - 1; i >= 0; i--)
                stripes[stripeIds[i]].Release();
        }
    }

    private sealed class EmptyLease : IDisposable
    {
        public static EmptyLease Instance { get; } = new();
        public void Dispose() { }
    }
}
