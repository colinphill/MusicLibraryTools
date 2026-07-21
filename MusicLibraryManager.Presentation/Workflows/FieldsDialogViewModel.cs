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
    private readonly IMediaFileService _media;
    private readonly ITagWriteService _writer;
    private readonly IReadOnlyList<string> _paths;
    private readonly IActivityService? _activities;
    private CancellationTokenSource? _saveCancellation;

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
        IMediaFileService media,
        ITagWriteService writer,
        IReadOnlyList<string> paths,
        IActivityService? activities = null)
    {
        _media = media;
        _writer = writer;
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
    }

    private async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var knownMaps = new List<Dictionary<TagFields, string>>();
            var userStringMaps = new List<Dictionary<string, string>>();
            foreach (var path in _paths)
            {
                var result = await _media.LoadDirectAsync(path, includeArtwork: false);
                if (result.Success)
                {
                    var knownMap = new Dictionary<TagFields, string>();
                    foreach (var kv in result.Value!.KnownFields)
                        knownMap.TryAdd(kv.Field, kv.Value); // first value wins, mirroring the parsers
                    knownMaps.Add(knownMap);

                    var userStringMap = new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase);
                    foreach (TextField field in result.Value.TextFields)
                        userStringMap.TryAdd(field.Key, field.Value);
                    userStringMaps.Add(userStringMap);
                }
            }

            HashSet<TagFields> originalFields = knownMaps.SelectMany(map => map.Keys).ToHashSet();
            foreach (var field in originalFields.OrderBy(f => f.ToString(), StringComparer.Ordinal))
            {
                var values = knownMaps.Select(map =>
                    map.TryGetValue(field, out string? value) ? value : "").Distinct().ToList();
                var mixed = values.Count > 1;
                Rows.Add(new FieldRow(field, mixed ? "" : values.FirstOrDefault() ?? "", mixed, isNew: false));
            }

            IEnumerable<string> userStringKeys = userStringMaps
                .SelectMany(map => map.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase);
            foreach (string key in userStringKeys)
            {
                var values = userStringMaps.Select(map =>
                    map.TryGetValue(key, out string? value) ? value : "").Distinct().ToList();
                bool mixed = values.Count > 1;
                Rows.Add(new FieldRow(
                    key,
                    mixed ? "" : values.FirstOrDefault() ?? "",
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
        List<TagEdit> edits = BuildEdits();
        if (edits.Count == 0)
        {
            CloseRequested?.Invoke(false);
            return;
        }

        if (!IsConfirmingSave)
        {
            IsConfirmingCancel = false;
            IsConfirmingSave = true;
            StatusTone = MessageTone.Warning;
            StatusMessage = $"Apply {edits.Count:N0} field change(s) to {_paths.Count:N0} file(s)? " +
                "This writes the files directly; no recovery journal is created. " +
                "Choose Apply changes to continue.";
            return;
        }

        IsConfirmingCancel = false;
        IsBusy = true;
        _saveCancellation = new CancellationTokenSource();
        CancelCommand.NotifyCanExecuteChanged();
        Guid? activity = _activities?.Start(
            "Save metadata fields",
            $"Applying {edits.Count:N0} field change(s) to {_paths.Count:N0} file(s)",
            ShellDestination.Library,
            _saveCancellation.Cancel);
        var progress = new Progress<int>(completed =>
        {
            if (activity.HasValue)
                _activities!.Report(activity.Value,
                    $"Updated {Math.Min(completed, _paths.Count):N0} of {_paths.Count:N0} file(s)",
                    _paths.Count == 0 ? null : Math.Min(1, (double)completed / _paths.Count));
        });
        try
        {
            BatchWriteResult result = await _writer.ApplyAsync(
                _paths, edits, progress, _saveCancellation.Token);
            if (result.FailedCount == 0)
            {
                StatusTone = MessageTone.Success;
                StatusMessage = result.Summary;
                if (activity.HasValue)
                    _activities!.Finish(activity.Value, result.Summary, AppActivityState.Completed);
                CloseRequested?.Invoke(true);
            }
            else
            {
                StatusTone = MessageTone.Error;
                StatusMessage = $"{result.Summary}. Proposed field changes remain ready to retry.";
                if (activity.HasValue)
                    _activities!.Finish(activity.Value, StatusMessage, AppActivityState.Failed);
            }
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

    private List<TagEdit> BuildEdits()
    {
        var edits = new List<TagEdit>();
        foreach (var row in Rows)
        {
            if (row.MarkedForRemoval)
                edits.Add(row.IsUserString
                    ? TagEdit.UserString(row.UserStringKey!, null)
                    : new TagEdit(row.Field!.Value, null));
            else if (row.IsModified)
            {
                string? value = string.IsNullOrEmpty(row.Value) ? null : row.Value;
                edits.Add(row.IsUserString
                    ? TagEdit.UserString(row.UserStringKey!, value)
                    : new TagEdit(row.Field!.Value, value));
            }
        }
        return edits;
    }

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
