using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibrary.App.ViewModels;

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

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private TagFields _fieldToAdd = TagFields.Comment;
    [ObservableProperty] private string? _newUserStringName;

    public ObservableCollection<FieldRow> Rows { get; } = [];

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

    public FieldsDialogViewModel(IMediaFileService media, ITagWriteService writer, IReadOnlyList<string> paths)
    {
        _media = media;
        _writer = writer;
        _paths = paths;
        Loading = LoadAsync();
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

    [RelayCommand]
    private async Task SaveAsync()
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

        if (edits.Count == 0)
        {
            CloseRequested?.Invoke(false);
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _writer.ApplyAsync(_paths, edits);
            if (result.FailedCount == 0)
            {
                CloseRequested?.Invoke(true);
            }
            else
            {
                StatusMessage = result.Summary;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);
}
