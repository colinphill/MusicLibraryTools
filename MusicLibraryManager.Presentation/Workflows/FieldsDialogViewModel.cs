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
    private readonly ILocalizationService? _localization;

    public TagFields? Field { get; }
    public string? UserStringKey { get; }
    public bool IsUserString => UserStringKey is not null;
    public string Name => UserStringKey ??
        (Field is { } knownField
            ? L(
                $"Settings.Choice.TagFields.{knownField}")
            : "");
    public string Kind => L(
        IsUserString
            ? "Fields.Row.Kind.UserString"
            : "Fields.Row.Kind.KnownField");

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

    public string RemoveButtonText => L(
        MarkedForRemoval
            ? "Fields.Action.UndoRemoval"
            : "Fields.Action.Remove");

    public FieldRow(
        TagFields field,
        string value,
        bool mixed,
        bool isNew,
        ILocalizationService? localization = null)
        : this(
            field,
            null,
            value,
            mixed,
            isNew,
            localization)
    {
    }

    public FieldRow(
        string userStringKey,
        string value,
        bool mixed,
        bool isNew,
        ILocalizationService? localization = null)
        : this(
            null,
            userStringKey,
            value,
            mixed,
            isNew,
            localization)
    {
    }

    private FieldRow(
        TagFields? field,
        string? userStringKey,
        string value,
        bool mixed,
        bool isNew,
        ILocalizationService? localization)
    {
        Field = field;
        UserStringKey = userStringKey;
        _localization = localization;
        IsOriginal = !isNew;
        _suppress = true;
        Value = value;
        _suppress = false;
        _original = value;
        IsMixed = mixed;
        IsModified = isNew;   // a freshly added field is written even if left blank-to-remove
    }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(
            nameof(RemoveButtonText));
    }

    partial void OnValueChanged(string value)
    {
        if (_suppress) return;
        IsModified = value != _original || IsModified;
        if (value != _original) IsMixed = false;
    }

    private string L(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);
}

/// <summary>
/// Add / edit / remove arbitrary <see cref="TagFields"/> across the selected files. Unlike the main
/// editor's curated set, every writable field is available. Values differing across the selection
/// show as "mixed" and are only written when edited.
/// </summary>
public partial class FieldsDialogViewModel :
    ViewModelBase,
    IDisposable
{
    private readonly IMetadataDocumentService _documents;
    private readonly IMetadataOperationService _operations;
    private readonly Func<
        MetadataOperationPlan,
        CancellationToken,
        Task<bool>> _reviewPending;
    private readonly IReadOnlyList<string> _paths;
    private readonly IActivityService? _activities;
    private readonly ILocalizationService? _localization;
    private CancellationTokenSource? _saveCancellation;
    private MetadataOperationPlan? _plan;
    private string? _statusKey;
    private object?[] _statusArguments = [];
    private long? _statusCount;
    private bool _disposed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(CancelButtonText))]
    private bool _isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string? _statusMessage;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiagnosticDetail))]
    private string? _diagnosticDetail;
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
    public ObservableCollection<LocalizedChoice<TagFields>>
        AddableFieldChoices { get; } = [];
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool HasDiagnosticDetail =>
        !string.IsNullOrWhiteSpace(
            DiagnosticDetail);
    public bool HasPendingChanges =>
        !string.IsNullOrWhiteSpace(NewUserStringName) ||
        Rows.Any(row => row.IsModified || row.MarkedForRemoval);
    public string SaveButtonText => L(
        IsConfirmingSave
            ? "Inspector.View.ReviewChanges"
            : "Common.Preview");
    public string CancelButtonText => IsBusy
        ? L("Fields.Action.CancelSave")
        : IsConfirmingCancel
            ? L("Fields.Action.DiscardChanges")
            : L("Common.Cancel");
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
        ? LF(
            "Fields.Title.Single",
            System.IO.Path.GetFileName(
                _paths[0]))
        : LFC(
            "Fields.Title.Files",
            _paths.Count);

    /// <summary>Completes when the initial multi-file field aggregation has loaded.</summary>
    public Task Loading { get; }

    /// <summary>Raised to close the dialog; the bool is the save/cancel result.</summary>
    public event Action<bool>? CloseRequested;

    public FieldsDialogViewModel(
        IMetadataDocumentService documents,
        IMetadataOperationService operations,
        IReadOnlyList<string> paths,
        Func<
            MetadataOperationPlan,
            CancellationToken,
            Task<bool>> reviewPending,
        IActivityService? activities = null,
        ILocalizationService? localization = null)
    {
        _documents = documents;
        _operations = operations;
        _reviewPending = reviewPending ??
            throw new ArgumentNullException(
                nameof(reviewPending));
        _paths = paths;
        _activities = activities;
        _localization = localization;
        RefreshLocalizedChoices();
        if (_localization is not null)
            _localization.CultureChanged +=
                OnLocalizationCultureChanged;
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
            var diagnostics =
                new List<string>();
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
                    diagnostics.Add(
                        $"{path}{Environment.NewLine}" +
                        error.Message);
                }
            }

            if (diagnostics.Count > 0)
            {
                SetCountStatus(
                    MessageTone.Warning,
                    "Fields.Status.LoadFailures",
                    diagnostics.Count);
                DiagnosticDetail = string.Join(
                    Environment.NewLine +
                    Environment.NewLine,
                    diagnostics);
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
                        isNew: false,
                        localization: _localization)
                    : new FieldRow(
                        field.CustomName!,
                        editorValue,
                        mixed,
                        isNew: false,
                        localization: _localization));
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
        Rows.Add(
            new FieldRow(
                FieldToAdd,
                "",
                mixed: false,
                isNew: true,
                localization: _localization));
    }

    [RelayCommand]
    private void AddUserString()
    {
        string key = NewUserStringName?.Trim() ?? "";
        if (key.Length == 0)
        {
            SetStatus(
                MessageTone.Warning,
                "Fields.Status.UserStringNameRequired");
            return;
        }

        FieldRow? existing = Rows.FirstOrDefault(row =>
            row.IsUserString && string.Equals(
                row.UserStringKey, key, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.MarkedForRemoval = false;
            SetStatus(
                MessageTone.Info,
                "Fields.Status.UserStringExists",
                existing.UserStringKey);
            return;
        }

        Rows.Add(
            new FieldRow(
                key,
                "",
                mixed: false,
                isNew: true,
                localization: _localization));
        NewUserStringName = null;
        ClearStatus();
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
            DiagnosticDetail = null;
            Guid? previewActivity = _activities?.Start(
                L("Fields.Activity.Preview.Title"),
                LF(
                    "Fields.Activity.Preview.Starting",
                    edits.Count,
                    _paths.Count),
                ShellDestination.Library,
                _saveCancellation.Cancel);
            IProgress<OperationProgress> previewProgress =
                CreateProgress(previewActivity);
            try
            {
                _plan = await _operations
                    .PreviewValueEditsAsync(
                        BuildRequests(edits),
                        L("Fields.Operation.Name"),
                        previewProgress,
                        _saveCancellation.Token);
                DiagnosticDetail =
                    PlanDiagnosticDetail(_plan);
                int blockers = _plan.Files
                    .SelectMany(file => file.Issues)
                    .Count(issue =>
                        issue.Severity ==
                        OperationIssueSeverity.Blocker);
                if (!_plan.CanApply)
                {
                    if (blockers > 0)
                        SetCountStatus(
                            MessageTone.Error,
                            "Fields.Status.PreviewBlockers",
                            blockers);
                    else
                        SetStatus(
                            MessageTone.Info,
                            "Fields.Status.PreviewNoChanges");
                    if (previewActivity.HasValue)
                        _activities!.Finish(
                            previewActivity.Value,
                            StatusMessage!,
                            blockers > 0
                                ? AppActivityState.Failed
                                : AppActivityState.Completed);
                    _plan = null;
                    return;
                }
                IsConfirmingSave = true;
                SetStatus(
                    MessageTone.Info,
                    "Fields.Status.PreviewReady",
                    _plan.ChangedFileCount,
                    edits.Count);
                if (previewActivity.HasValue)
                    _activities!.Finish(
                        previewActivity.Value,
                        LFC(
                            "Fields.Activity.Preview.Completed",
                            _plan.ChangedFileCount));
            }
            catch (OperationCanceledException) when (
                _saveCancellation.IsCancellationRequested)
            {
                SetStatus(
                    MessageTone.Warning,
                    "Fields.Status.PreviewCancelled");
                if (previewActivity.HasValue)
                    _activities!.Finish(
                        previewActivity.Value,
                        StatusMessage!,
                        AppActivityState.Cancelled);
            }
            catch (Exception error)
            {
                SetFailure(
                    "Fields.Status.PreviewFailed",
                    error);
                if (previewActivity.HasValue)
                    _activities!.Finish(
                        previewActivity.Value,
                        StatusMessage!,
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
            SetStatus(
                MessageTone.Warning,
                "Fields.Status.PreviewExpired");
            return;
        }

        IsConfirmingCancel = false;
        IsBusy = true;
        _saveCancellation = new CancellationTokenSource();
        CancelCommand.NotifyCanExecuteChanged();
        DiagnosticDetail = null;
        Guid? activity = _activities?.Start(
            L("Fields.Activity.Preview.Title"),
            L(
                "Workbench.PendingChanges.Description"),
            ShellDestination.Library,
            _saveCancellation.Cancel);
        try
        {
            bool accepted =
                await _reviewPending(
                    _plan,
                    _saveCancellation.Token);
            if (!accepted)
            {
                SetStatus(
                    MessageTone.Info,
                    "Workbench.PendingChanges.Description");
                if (activity.HasValue)
                    _activities!.Finish(
                        activity.Value,
                        StatusMessage!,
                        AppActivityState.Completed);
                return;
            }
            if (activity.HasValue)
                _activities!.Finish(
                    activity.Value,
                    L(
                        "Workbench.PendingChanges.Description"),
                    AppActivityState.Completed);
            _plan = null;
            IsConfirmingSave = false;
            CloseRequested?.Invoke(true);
        }
        catch (OperationCanceledException) when (_saveCancellation.IsCancellationRequested)
        {
            SetStatus(
                MessageTone.Warning,
                "Fields.Status.PreviewCancelled");
            if (activity.HasValue)
                _activities!.Finish(activity.Value, StatusMessage!, AppActivityState.Cancelled);
        }
        catch (Exception error)
        {
            SetFailure(
                "Fields.Status.PreviewFailed",
                error);
            if (activity.HasValue)
                _activities!.Finish(activity.Value, StatusMessage!, AppActivityState.Failed);
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
                update.CurrentPath is { } path
                    ? LF(
                        "Fields.Progress.WithPath",
                        L(
                            $"Fields.Progress.Phase.{update.Phase}"),
                        path)
                    : L(
                        $"Fields.Progress.Phase.{update.Phase}"),
                fraction);
        });

    private static string? PlanDiagnosticDetail(
        MetadataOperationPlan plan)
    {
        string[] diagnostics = plan.Files
            .SelectMany(file =>
                file.Issues.Select(issue =>
                    $"{file.Path}{Environment.NewLine}" +
                    issue.Message))
            .ToArray();
        return diagnostics.Length == 0
            ? null
            : string.Join(
                Environment.NewLine +
                Environment.NewLine,
                diagnostics);
    }

    private void RefreshLocalizedChoices()
    {
        if (AddableFieldChoices.Count == 0)
        {
            foreach (TagFields field in
                     AddableFields)
                AddableFieldChoices.Add(
                    new LocalizedChoice<TagFields>(
                        field,
                        L(
                            $"Settings.Choice.TagFields.{field}")));
            return;
        }

        foreach (LocalizedChoice<TagFields> choice in
                 AddableFieldChoices)
            choice.Label = L(
                $"Settings.Choice.TagFields.{choice.Value}");
    }

    private string L(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);

    private string LF(
        string key,
        params object?[] arguments) =>
        _localization?.Format(
            key,
            arguments) ??
        LocalizedText.Format(
            key,
            arguments);

    private string LFC(
        string key,
        long count,
        params object?[] arguments) =>
        _localization?.FormatCount(
            key,
            count,
            arguments) ??
        LocalizedText.FormatCount(
            key,
            count,
            arguments);

    private void SetStatus(
        MessageTone tone,
        string key,
        params object?[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        _statusCount = null;
        StatusTone = tone;
        StatusMessage = LF(
            key,
            arguments);
    }

    private void SetCountStatus(
        MessageTone tone,
        string key,
        long count,
        params object?[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        _statusCount = count;
        StatusTone = tone;
        StatusMessage = LFC(
            key,
            count,
            arguments);
    }

    private void SetFailure(
        string key,
        Exception error)
    {
        SetStatus(
            MessageTone.Error,
            key);
        DiagnosticDetail = error.Message;
    }

    private void ClearStatus()
    {
        _statusKey = null;
        _statusArguments = [];
        _statusCount = null;
        StatusMessage = null;
        DiagnosticDetail = null;
    }

    private void OnLocalizationCultureChanged(
        object? sender,
        EventArgs e)
    {
        if (_statusKey is { } key)
            StatusMessage = _statusCount is { } count
                ? LFC(
                    key,
                    count,
                    _statusArguments)
                : LF(
                    key,
                    _statusArguments);
        RefreshLocalizedChoices();
        foreach (FieldRow row in Rows)
            row.RefreshLocalization();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SaveButtonText));
        OnPropertyChanged(nameof(CancelButtonText));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_localization is not null)
            _localization.CultureChanged -=
                OnLocalizationCultureChanged;
        Rows.CollectionChanged -= OnRowsChanged;
        foreach (FieldRow row in Rows)
            row.PropertyChanged -= OnRowChanged;
        GC.SuppressFinalize(this);
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
            SetStatus(
                MessageTone.Warning,
                "Fields.Status.ConfirmDiscard");
            return;
        }
        CloseRequested?.Invoke(false);
    }
}
