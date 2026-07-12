using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.App.Services;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibrary.App.ViewModels;

public partial class IngestViewModel : ViewModelBase
{
    private const string SourcePreference = "Ingest.SourceDirectory";
    private const string ConfigPreference = "Ingest.ConfigurationPath";
    private readonly IIngestMusicService _service;
    private readonly IFileDialogService _files;
    private readonly IDialogService _dialogs;
    private readonly IAppSettings _settings;
    private CancellationTokenSource? _cts;
    private IngestPlan? _plan;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    private string? _sourceDirectory;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(PreviewCommand)), NotifyCanExecuteChangedFor(nameof(EditConfigurationCommand))]
    private string? _configurationPath;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(PreviewCommand)), NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _isBusy;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _hasApplicablePreview;
    [ObservableProperty]
    private string _statusText = "Choose an incoming folder and IngestMusic configuration, then Preview.";

    public ObservableCollection<IngestAction> Actions { get; } = [];
    public ObservableCollection<IngestConflict> Conflicts { get; } = [];

    public IngestViewModel(IIngestMusicService service, IFileDialogService files, IDialogService dialogs, IAppSettings settings)
    {
        _service = service; _files = files; _dialogs = dialogs; _settings = settings;
        SourceDirectory = settings.GetPreference(SourcePreference);
        ConfigurationPath = settings.GetPreference(ConfigPreference);
    }

    partial void OnSourceDirectoryChanged(string? value) => InvalidatePreview();
    partial void OnConfigurationPathChanged(string? value) => InvalidatePreview();

    private void InvalidatePreview()
    {
        if (_plan is null) return;
        _plan = null;
        HasApplicablePreview = false;
        StatusText = "Inputs changed. Preview again before applying.";
    }

    [RelayCommand]
    private async Task BrowseSourceAsync()
    {
        string? path = await _files.PickFolderAsync("Select incoming music directory");
        if (path is not null) SourceDirectory = path;
    }

    [RelayCommand]
    private async Task BrowseConfigurationAsync()
    {
        string? path = await _files.PickOpenFileAsync("Select IngestMusic configuration",
            [new FilePickerFilter("XML configuration", ["*.xml"])]);
        if (path is not null) ConfigurationPath = path;
    }

    [RelayCommand]
    private async Task NewConfigurationAsync()
    {
        string? path = await _dialogs.ShowIngestConfigEditorAsync(null);
        if (path is not null)
        {
            ConfigurationPath = path;
            _settings.SetPreference(ConfigPreference, path);
        }
    }

    private bool CanEditConfiguration() => !string.IsNullOrWhiteSpace(ConfigurationPath) && File.Exists(ConfigurationPath);

    [RelayCommand(CanExecute = nameof(CanEditConfiguration))]
    private async Task EditConfigurationAsync()
    {
        string? path = await _dialogs.ShowIngestConfigEditorAsync(ConfigurationPath);
        if (path is not null)
        {
            ConfigurationPath = path;
            _settings.SetPreference(ConfigPreference, path);
            HasApplicablePreview = false;
            _plan = null;
            StatusText = "Configuration saved. Preview again before applying.";
        }
    }

    private bool CanPreview() => !IsBusy && !string.IsNullOrWhiteSpace(SourceDirectory) && !string.IsNullOrWhiteSpace(ConfigurationPath);

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewAsync()
    {
        IsBusy = true; HasApplicablePreview = false; _plan = null; Actions.Clear(); Conflicts.Clear();
        _cts = new CancellationTokenSource();
        try
        {
            StatusText = "Scanning and planning…";
            var plan = await _service.PreviewAsync(new IngestRequest(SourceDirectory!, ConfigurationPath!), _cts.Token);
            _plan = plan;
            foreach (var action in plan.Actions) Actions.Add(action);
            foreach (var conflict in plan.Conflicts) Conflicts.Add(conflict);
            HasApplicablePreview = plan.CanApply;
            _settings.SetPreference(SourcePreference, plan.Request.SourceDirectory);
            _settings.SetPreference(ConfigPreference, plan.Request.ConfigurationPath);
            StatusText = plan.CanApply
                ? $"{plan.Albums.Count} albums, {plan.Actions.Count} actions, {plan.RequiredApprovals.Count} derivation approvals. Review, then Apply."
                : $"Preview has {plan.Conflicts.Count} conflicts and cannot be applied.";
        }
        catch (OperationCanceledException) { StatusText = "Preview cancelled."; }
        catch (Exception ex) { StatusText = $"Preview failed: {ex.Message}"; }
        finally { FinishBusy(); }
    }

    private bool CanApply() => !IsBusy && HasApplicablePreview && _plan is not null;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (_plan is null) return;
        IsBusy = true; _cts = new CancellationTokenSource();
        try
        {
            var decisions = new List<IngestApprovalDecision>();
            foreach (var item in _plan.RequiredApprovals)
            {
                bool approved = await _dialogs.ConfirmCdDerivationAsync(item);
                decisions.Add(new IngestApprovalDecision(item.AlbumKey, approved));
                if (!approved)
                {
                    StatusText = "Derivation declined; the entire run was cancelled and nothing was changed.";
                    return;
                }
            }
            var progress = new Progress<IngestProgress>(p => StatusText = $"{p.Operation}: {p.Album} ({p.CompletedAlbums}/{p.TotalAlbums})");
            var result = await _service.ApplyAsync(_plan, decisions, progress, _cts.Token);
            StatusText = result.Cancelled ? result.Message ?? "Cancelled." : $"Installed {result.Installed} files; {result.Failed} albums failed.";
            HasApplicablePreview = false; _plan = null;
        }
        catch (OperationCanceledException) { StatusText = "Cancelled; any album already committed remains safely journaled."; }
        catch (Exception ex) { StatusText = $"Apply failed: {ex.Message}"; }
        finally { FinishBusy(); }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private void FinishBusy()
    {
        _cts?.Dispose(); _cts = null; IsBusy = false;
        PreviewCommand.NotifyCanExecuteChanged(); ApplyCommand.NotifyCanExecuteChanged();
    }
}
