using MetadataCaching;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

/// <summary>Preserves database indexing stages while adapting them to the shared operation model.</summary>
internal sealed class IndexOperationProgressAdapter(IProgress<OperationProgress> progress)
    : IProgress<IndexProgress>
{
    private readonly IProgress<OperationProgress> _progress = progress;
    private readonly object _gate = new();
    private int _enumerated;
    private int _scanned;

    public void Report(IndexProgress value)
    {
        lock (_gate)
        {
            _enumerated = Math.Max(_enumerated, value.Enumerated);
            _scanned = Math.Max(_scanned, value.Scanned);
            if (value.Phase != IndexPhase.Completed)
                _progress.Report(Convert(value));
        }
    }

    public void ReportCompleted(int added, int modified, int removed, int unchanged)
    {
        lock (_gate)
        {
            _progress.Report(new OperationProgress(
                OperationPhase.IndexingSources,
                _enumerated,
                _enumerated,
                Message: $"Index complete: {_enumerated:N0} found, " +
                         $"{_scanned:N0} metadata read; {added:N0} added, " +
                         $"{modified:N0} modified, {removed:N0} removed, " +
                         $"{unchanged:N0} unchanged"));
        }
    }

    private static OperationProgress Convert(IndexProgress value)
    {
        int completed = value.Phase switch
        {
            IndexPhase.Enumeration => value.Enumerated,
            IndexPhase.Metadata => value.Scanned,
            IndexPhase.Database => value.DatabaseProcessed,
            _ => 0,
        };
        string count = value.Phase switch
        {
            IndexPhase.Enumeration => $": {value.Enumerated:N0} found",
            IndexPhase.Metadata => $": {value.Scanned:N0} metadata read",
            IndexPhase.Database => $": {value.DatabaseProcessed:N0} applied",
            _ => "",
        };
        string rate = value.Phase is IndexPhase.Enumeration or IndexPhase.Metadata or IndexPhase.Database &&
                      value.FilesPerSecond > 0
            ? $" ({value.FilesPerSecond:N1} files/s)"
            : "";
        return new OperationProgress(
            OperationPhase.IndexingSources,
            completed,
            CurrentPath: string.IsNullOrWhiteSpace(value.Root) ? null : value.Root,
            Message: $"{value.Detail}{count}{rate}");
    }
}
