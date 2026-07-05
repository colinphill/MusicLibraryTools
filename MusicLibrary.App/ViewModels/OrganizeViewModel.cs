using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibrary.App.ViewModels;

/// <summary>
/// Organize/Rename tool: previews the moves needed to canonicalize the library layout, then applies
/// them only after the user confirms. Nothing on disk changes until Apply.
/// </summary>
public partial class OrganizeViewModel : ViewModelBase
{
    private readonly ILibraryOrganizer _organizer;
    private readonly IAppSettings _settings;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusText = "Preview to see what would be renamed. No files move until you Apply.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _hasPreview;

    public ObservableCollection<PlannedMove> Moves { get; } = [];

    public OrganizeViewModel(ILibraryOrganizer organizer, IAppSettings settings)
    {
        _organizer = organizer;
        _settings = settings;
        _settings.ConfigurationChanged += (_, _) => PreviewCommand.NotifyCanExecuteChanged();
    }

    private bool IsReady => _settings.Configuration is not null;
    private bool CanPreview() => IsReady && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewAsync()
    {
        IsBusy = true;
        HasPreview = false;
        PreviewCommand.NotifyCanExecuteChanged();
        _cts = new CancellationTokenSource();
        StatusText = "Computing moves…";
        try
        {
            var moves = await _organizer.PreviewMovesAsync(_cts.Token);
            Moves.Clear();
            foreach (var m in moves)
                Moves.Add(m);
            HasPreview = moves.Count > 0;
            StatusText = moves.Count == 0
                ? "Everything is already in its canonical location."
                : $"{moves.Count:N0} files would be moved. Review below, then Apply.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Preview cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Preview failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            PreviewCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanApply() => HasPreview && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        IsBusy = true;
        ApplyCommand.NotifyCanExecuteChanged();
        _cts = new CancellationTokenSource();
        var total = Moves.Count;
        var progress = new Progress<int>(n => StatusText = $"Moving… {n:N0}/{total:N0}");
        try
        {
            var result = await _organizer.ApplyMovesAsync([.. Moves], progress, _cts.Token);
            StatusText = result.FailedCount == 0
                ? $"Moved {result.Moved:N0} files. Re-index to refresh the browser."
                : $"Moved {result.Moved:N0}, {result.FailedCount:N0} failed. Re-index to refresh.";
            Moves.Clear();
            HasPreview = false;
        }
        catch (OperationCanceledException)
        {
            StatusText = $"Cancelled — some files may have already moved. Re-index to refresh.";
            Moves.Clear();
            HasPreview = false;
        }
        catch (Exception ex)
        {
            StatusText = $"Apply failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            ApplyCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();
}
