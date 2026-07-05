using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibrary.App.ViewModels;

/// <summary>
/// Library-wide analysis: runs the analyzers and duplicate finder over the cache and lists the
/// findings. Selecting a finding opens that file in the editor/inspector.
/// </summary>
public partial class AnalyzerViewModel : ViewModelBase
{
    private readonly ILibraryService _library;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusText = "Run analysis to inspect the library.";

    public ObservableCollection<AnalysisReport> Reports { get; } = [];
    public ObservableCollection<DuplicateGroup> Duplicates { get; } = [];

    /// <summary>Raised with a file path when the user opens a finding/track.</summary>
    public event Action<string>? OpenRequested;

    public AnalyzerViewModel(ILibraryService library, IAppSettings settings)
    {
        _library = library;
        settings.ConfigurationChanged += (_, _) => RunCommand.NotifyCanExecuteChanged();
    }

    private bool CanRun() => _library.IsReady && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsBusy = true;
        RunCommand.NotifyCanExecuteChanged();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        StatusText = "Analyzing…";
        try
        {
            var records = await _library.GetAllRecordsAsync(ct);

            var reports = await Task.Run(() => LibraryAnalyzer.RunAll(records, ct), ct);
            var dupes = await Task.Run(() => DuplicateFinder.Find(records, ct), ct);

            Reports.Clear();
            foreach (var r in reports)
                Reports.Add(r);

            Duplicates.Clear();
            foreach (var d in dupes)
                Duplicates.Add(d);

            var flagged = reports.Sum(r => r.Count);
            StatusText = $"Analyzed {records.Count:N0} tracks · {flagged:N0} findings · {dupes.Count:N0} duplicate groups";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Analysis cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Analysis failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            RunCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void Open(string? path)
    {
        if (!string.IsNullOrEmpty(path))
            OpenRequested?.Invoke(path);
    }
}
