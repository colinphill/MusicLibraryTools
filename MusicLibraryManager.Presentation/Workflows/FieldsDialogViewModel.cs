using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

/// <summary>One arbitrary tag field being edited in the fields dialog.</summary>
public partial class FieldRow : ObservableObject
{
    public TagFields? Field { get; }
    public string? UserStringKey { get; }
    public bool IsUserString => UserStringKey is not null;
    public string Name => UserStringKey ?? Field?.ToString() ?? "";
    public string Kind => IsUserString ? "User string" : "Known field";

    /// <summary>True when the field already exists on disk (vs. freshly added in this dialog).</summary>
    public bool IsOriginal { get; }

    private string _original = "";
    private bool _suppress;

    [ObservableProperty] private string _value = "";
    [ObservableProperty] private bool _isMixed;
    [ObservableProperty] private bool _isModified;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemoveButtonText))]
    private bool _markedForRemoval;

    public string RemoveButtonText => MarkedForRemoval ? "Undo" : "Remove";

    public FieldRow(TagFields field, string value, bool mixed, bool isNew)
        : this(field, null, value, mixed, isNew)
    {
    }

    public FieldRow(string userStringKey, string value, bool mixed, bool isNew)
        : this(null, userStringKey, value, mixed, isNew)
    {
    }

    private FieldRow(
        TagFields? field,
        string? userStringKey,
        string value,
        bool mixed,
        bool isNew)
    {
        Field = field;
        UserStringKey = userStringKey;
        IsOriginal = !isNew;
        _suppress = true;
        Value = value;
        _suppress = false;
        _original = value;
        IsMixed = mixed;
        IsModified = isNew;   // a freshly added field is written even if left blank-to-remove
    }

    partial void OnValueChanged(string value)
    {
        if (_suppress) return;
        IsModified = value != _original || IsModified;
        if (value != _original) IsMixed = false;
    }
}

/// <summary>
/// Add / edit / remove arbitrary <see cref="TagFields"/> across the selected files. Unlike the main
/// editor's curated set, every writable field is available. Values differing across the selection
/// show as "mixed" and are only written when edited.
/// </summary>
public partial class FieldsDialogViewModel : ViewModelBase
{
    private readonly IMetadataDocumentService _documents;
    private readonly IMetadataOperationService _operations;
    private readonly IReadOnlyList<string> _paths;
    private readonly IActivityService? _activities;
    private CancellationTokenSource? _saveCancellation;
    private MetadataOperationPlan? _plan;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(CancelButtonText))]
    private bool _isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string? _statusMessage;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusInfo))]
    [NotifyPropertyChangedFor(nameof(IsStatusSuccess))]
    [NotifyPropertyChangedFor(nameof(IsStatusWarning))]
    [NotifyPropertyChangedFor(nameof(IsStatusError))]
    [NotifyPropertyChangedFor(nameof(StatusIcon))]
    private MessageTone _statusTone = MessageTone.Info;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveButtonText))]
    private bool _isConfirmingSave;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CancelButtonText))]
    private bool _isConfirmingCancel;
    [ObservableProperty] private TagFields _fieldToAdd = TagFields.Comment;
    [ObservableProperty] private string? _newUserStringName;

    public ObservableCollection<FieldRow> Rows { get; } = [];
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool HasPendingChanges =>
        !string.IsNullOrWhiteSpace(NewUserStringName) ||
        Rows.Any(row => row.IsModified || row.MarkedForRemoval);
    public string SaveButtonText => IsConfirmingSave ? "Apply changes" : "Save fields";
    public string CancelButtonText => IsBusy
        ? "Cancel save"
        : IsConfirmingCancel ? "Discard changes" : "Cancel";
    public bool IsStatusInfo => StatusTone == MessageTone.Info;
    public bool IsStatusSuccess => StatusTone == MessageTone.Success;
    public bool IsStatusWarning => StatusTone == MessageTone.Warning;
    public bool IsStatusError => StatusTone == MessageTone.Error;
    public string StatusIcon => StatusTone switch
    {
        MessageTone.Success => "✓",
        MessageTone.Warning => "⚠",
        MessageTone.Error => "!",
        _ => "i",
    };

    /// <summary>All addable fields (every TagFields except the null sentinel), alphabetized.</summary>
    public IReadOnlyList<TagFields> AddableFields { get; } =
        Enum.GetValues<TagFields>()
            .Where(f => f != TagFields.NullField)
            .OrderBy(f => f.ToString(), StringComparer.Ordinal)
            .ToList();

    public string Title => _paths.Count == 1
        ? $"Edit fields — {System.IO.Path.GetFileName(_paths[0])}"
        : $"Edit fields — {_paths.Count} files";

    /// <summary>Completes when the initial multi-file field aggregation has loaded.</summary>
    public Task Loading { get; }

    /// <summary>Raised to close the dialog; the bool is the save/cancel result.</summary>
    public event Action<bool>? CloseRequested;

    public FieldsDialogViewModel(
        IMetadataDocumentService documents,
        IMetadataOperationService operations,
        IReadOnlyList<string> paths,
        IActivityService? activities = null)
    {
        _documents = documents;
        _operations = operations;
        _paths = paths;
        _activities = activities;
        Rows.CollectionChanged += OnRowsChanged;
        Loading = LoadAsync();
    }

    partial void OnNewUserStringNameChanged(string? value)
    {
        OnPropertyChanged(nameof(HasPendingChanges));
        ResetConfirmations();
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (FieldRow row in e.OldItems)
                row.PropertyChanged -= OnRowChanged;
        if (e.NewItems is not null)
            foreach (FieldRow row in e.NewItems)
                row.PropertyChanged += OnRowChanged;
        OnPropertyChanged(nameof(HasPendingChanges));
        ResetConfirmations();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(FieldRow.Value) or nameof(FieldRow.IsModified) or
            nameof(FieldRow.MarkedForRemoval)))
            return;
        OnPropertyChanged(nameof(HasPendingChanges));
        ResetConfirmations();
    }

    private void ResetConfirmations()
    {
        IsConfirmingSave = false;
        IsConfirmingCancel = false;
        _plan = null;
    }

    private async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var documents = new List<MediaDocument>(
                _paths.Count);
            foreach (string path in _paths)
            {
                try
                {
                    documents.Add(
                        await _documents.LoadAsync(
                            path,
                            includeArtwork: false));
                }
                catch (Exception error)
                {
                    StatusTone = MessageTone.Warning;
                    StatusMessage =
                        $"Could not read '{path}': " +
                        error.Message;
                }
            }

            MetadataFieldKey[] fields = documents
                .SelectMany(document =>
                    document.TagLayers.SelectMany(
                        layer => layer.Fields))
                .Select(value => value.Field)
                .Distinct()
                .OrderBy(
                    field => field.DisplayName,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (MetadataFieldKey field in fields)
            {
                ImmutableArray<string>[] values =
                    documents.Select(document =>
                            document.Values(field))
                        .ToArray();
                bool mixed = values.Skip(1).Any(value =>
                    !values[0].SequenceEqual(
                        value,
                        StringComparer.Ordinal));
                string editorValue = mixed
                    ? ""
                    : string.Join(
                        Environment.NewLine,
                        values[0]);
                Rows.Add(field.IsKnown
                    ? new FieldRow(
                        field.KnownField!.Value,
                        editorValue,
                        mixed,
                        isNew: false)
                    : new FieldRow(
                        field.CustomName!,
                        editorValue,
                        mixed,
                        isNew: false));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void AddField()
    {
        FieldRow? existing = Rows.FirstOrDefault(row => row.Field == FieldToAdd);
        if (existing is not null)
        {
            existing.MarkedForRemoval = false;   // re-adding an un-removes a struck field
            return;
        }
        Rows.Add(new FieldRow(FieldToAdd, "", mixed: false, isNew: true));
    }

    [RelayCommand]
    private void AddUserString()
    {
        string key = NewUserStringName?.Trim() ?? "";
        if (key.Length == 0)
        {
            StatusMessage = "Enter a user-string name.";
            return;
        }

        FieldRow? existing = Rows.FirstOrDefault(row =>
            row.IsUserString && string.Equals(
                row.UserStringKey, key, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.MarkedForRemoval = false;
            StatusMessage = $"User string '{existing.UserStringKey}' is already in the list.";
            return;
        }

        Rows.Add(new FieldRow(key, "", mixed: false, isNew: true));
        NewUserStringName = null;
        StatusMessage = null;
    }

    [RelayCommand]
    private void RemoveField(FieldRow row)
    {
        // Added-but-not-yet-saved rows just disappear; existing fields are struck through so the
        // user can see (and undo) that they'll be removed on save.
        if (!row.IsOriginal)
            Rows.Remove(row);
        else
            row.MarkedForRemoval = !row.MarkedForRemoval;
    }

    private bool CanSave() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        List<MetadataValueEdit> edits = BuildEdits();
        if (edits.Count == 0)
        {
            CloseRequested?.Invoke(false);
            return;
        }

        if (!IsConfirmingSave)
        {
            IsConfirmingCancel = false;
            IsBusy = true;
            _saveCancellation =
                new CancellationTokenSource();
            CancelCommand.NotifyCanExecuteChanged();
            Guid? previewActivity = _activities?.Start(
                "Preview metadata fields",
                $"Validating {edits.Count:N0} field change(s) for " +
                $"{_paths.Count:N0} file(s)",
                ShellDestination.Library,
                _saveCancellation.Cancel);
            IProgress<OperationProgress> previewProgress =
                CreateProgress(previewActivity);
            try
            {
                _plan = await _operations
                    .PreviewValueEditsAsync(
                        BuildRequests(edits),
                        "Edit Library metadata fields",
                        previewProgress,
                        _saveCancellation.Token);
                int blockers = _plan.Files
                    .SelectMany(file => file.Issues)
                    .Count(issue =>
                        issue.Severity ==
                        OperationIssueSeverity.Blocker);
                if (!_plan.CanApply)
                {
                    StatusTone = MessageTone.Error;
                    StatusMessage = blockers > 0
                        ? $"Preview found {blockers:N0} blocker(s). " +
                          "No files were changed."
                        : "Preview found no applicable changes. " +
                          "No files were changed.";
                    if (previewActivity.HasValue)
                        _activities!.Finish(
                            previewActivity.Value,
                            StatusMessage,
                            blockers > 0
                                ? AppActivityState.Failed
                                : AppActivityState.Completed);
                    _plan = null;
                    return;
                }
                IsConfirmingSave = true;
                StatusTone = blockers > 0
                    ? MessageTone.Error
                    : MessageTone.Warning;
                StatusMessage =
                    $"Preview ready for " +
                    $"{_plan.ChangedFileCount:N0} file(s) and " +
                    $"{edits.Count:N0} field change(s). " +
                    "Apply uses stale-file checks, recovery journals, " +
                    "and undo. Choose Apply changes to continue.";
                if (previewActivity.HasValue)
                    _activities!.Finish(
                        previewActivity.Value,
                        $"Previewed {_plan.ChangedFileCount:N0} file(s)");
            }
            catch (OperationCanceledException) when (
                _saveCancellation.IsCancellationRequested)
            {
                StatusTone = MessageTone.Warning;
                StatusMessage =
                    "Preview cancelled. Proposed field changes " +
                    "remain ready to retry.";
                if (previewActivity.HasValue)
                    _activities!.Finish(
                        previewActivity.Value,
                        StatusMessage,
                        AppActivityState.Cancelled);
            }
            catch (Exception error)
            {
                StatusTone = MessageTone.Error;
                StatusMessage =
                    $"Preview failed: {error.Message}. Proposed " +
                    "field changes remain ready to retry.";
                if (previewActivity.HasValue)
                    _activities!.Finish(
                        previewActivity.Value,
                        StatusMessage,
                        AppActivityState.Failed);
            }
            finally
            {
                _saveCancellation.Dispose();
                _saveCancellation = null;
                IsBusy = false;
                CancelCommand.NotifyCanExecuteChanged();
            }
            return;
        }

        if (_plan is null)
        {
            IsConfirmingSave = false;
            StatusTone = MessageTone.Warning;
            StatusMessage =
                "The preview expired. Preview the field changes again.";
            return;
        }

        IsConfirmingCancel = false;
        IsBusy = true;
        _saveCancellation = new CancellationTokenSource();
        CancelCommand.NotifyCanExecuteChanged();
        Guid? activity = _activities?.Start(
            "Save metadata fields",
            $"Applying the reviewed field plan to " +
            $"{_plan.ChangedFileCount:N0} file(s)",
            ShellDestination.Library,
            _saveCancellation.Cancel);
        IProgress<OperationProgress> progress =
            CreateProgress(activity);
        try
        {
            MetadataApplyResult result =
                await _operations.ApplyAsync(
                    _plan,
                    progress,
                    _saveCancellation.Token);
            StatusTone = MessageTone.Success;
            StatusMessage =
                $"Updated {result.ChangedFiles:N0} file(s). " +
                "Originals are retained for undo.";
            if (activity.HasValue)
                _activities!.Finish(
                    activity.Value,
                    StatusMessage,
                    AppActivityState.Completed);
            _plan = null;
            CloseRequested?.Invoke(true);
        }
        catch (OperationCanceledException) when (_saveCancellation.IsCancellationRequested)
        {
            StatusTone = MessageTone.Warning;
            StatusMessage = "Save cancelled. Proposed field changes remain ready to retry.";
            if (activity.HasValue)
                _activities!.Finish(activity.Value, StatusMessage, AppActivityState.Cancelled);
        }
        catch (Exception ex)
        {
            StatusTone = MessageTone.Error;
            StatusMessage = $"Save failed: {ex.Message}. Proposed field changes remain ready to retry.";
            if (activity.HasValue)
                _activities!.Finish(activity.Value, StatusMessage, AppActivityState.Failed);
        }
        finally
        {
            _saveCancellation.Dispose();
            _saveCancellation = null;
            IsBusy = false;
            CancelCommand.NotifyCanExecuteChanged();
        }
    }

    private List<MetadataValueEdit> BuildEdits()
    {
        var edits = new List<MetadataValueEdit>();
        foreach (var row in Rows)
        {
            MetadataFieldKey field = row.IsUserString
                ? MetadataFieldKey.Custom(
                    row.UserStringKey!)
                : MetadataFieldKey.Known(
                    row.Field!.Value);
            if (row.MarkedForRemoval)
                edits.Add(new(field, []));
            else if (row.IsModified)
            {
                ImmutableArray<string> values = row.Value
                    .Replace(
                        "\r\n",
                        "\n",
                        StringComparison.Ordinal)
                    .Replace('\r', '\n')
                    .Split(
                        '\n',
                        StringSplitOptions.None)
                    .Where(value => value.Length > 0)
                    .ToImmutableArray();
                edits.Add(new(field, values));
            }
        }
        return edits;
    }

    private IReadOnlyDictionary<
        string,
        IReadOnlyList<MetadataValueEdit>> BuildRequests(
            IReadOnlyList<MetadataValueEdit> edits) =>
        _paths.ToDictionary(
            path => path,
            _ => edits,
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

    private IProgress<OperationProgress> CreateProgress(
        Guid? activity) =>
        new Progress<OperationProgress>(update =>
        {
            if (!activity.HasValue)
                return;
            double? fraction =
                update.Total is > 0
                    ? Math.Clamp(
                        (double)update.Completed /
                        update.Total.Value,
                        0,
                        1)
                    : null;
            _activities!.Report(
                activity.Value,
                update.Message ??
                update.CurrentPath ??
                "Updating metadata fields",
                fraction);
        });

    private bool CanCancel() => !IsBusy || _saveCancellation is not null;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (IsBusy)
        {
            _saveCancellation?.Cancel();
            return;
        }
        if (!HasPendingChanges)
        {
            CloseRequested?.Invoke(false);
            return;
        }
        if (!IsConfirmingCancel)
        {
            IsConfirmingSave = false;
            IsConfirmingCancel = true;
            StatusTone = MessageTone.Warning;
            StatusMessage = "Discard the unsaved field changes? Choose Discard changes to confirm.";
            return;
        }
        CloseRequested?.Invoke(false);
    }
}
