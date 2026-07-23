using System.Collections.ObjectModel;
using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

public enum MetadataGridFieldKind
{
    Known,
    Custom,
}

public sealed class UserMetadataColumnRow(
    UserMetadataColumnDescriptor descriptor)
{
    public UserMetadataColumnDescriptor Descriptor { get; internal set; } =
        descriptor;
    public string Label => Descriptor.Label;
    public string Field => Descriptor.Field.DisplayName;
    public string Kind => Descriptor.Field.IsKnown
        ? "Known"
        : "Custom";
}

public sealed class MetadataGridRowComparer(
    string valueKey,
    MetadataGridColumnSortType sortType) : IComparer
{
    private static readonly Regex Number = new(
        @"[-+]?\d+(?:[.,]\d+)?",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public int Compare(object? x, object? y)
    {
        string left = Value(x);
        string right = Value(y);
        if (left.Length == 0 || right.Length == 0)
            return left.Length.CompareTo(right.Length);
        return sortType switch
        {
            MetadataGridColumnSortType.Numeric =>
                CompareNumbers(left, right),
            MetadataGridColumnSortType.Date =>
                CompareDates(left, right),
            _ => string.Compare(
                left,
                right,
                StringComparison.CurrentCultureIgnoreCase),
        };
    }

    private string Value(object? row) => row switch
    {
        LibraryRow library =>
            library.MetadataValues.GetValueOrDefault(valueKey) ?? "",
        WorkbenchTrackViewModel workbench =>
            workbench.MetadataValues.GetValueOrDefault(valueKey) ?? "",
        _ => "",
    };

    private static int CompareNumbers(
        string left,
        string right)
    {
        decimal leftNumber = ParseNumber(left);
        decimal rightNumber = ParseNumber(right);
        return leftNumber.CompareTo(rightNumber);
    }

    private static decimal ParseNumber(string value)
    {
        Match match = Number.Match(value);
        if (!match.Success)
            return decimal.MinValue;
        return decimal.TryParse(
            match.Value,
            NumberStyles.Number,
            CultureInfo.CurrentCulture,
            out decimal parsed) ||
            decimal.TryParse(
                match.Value,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out parsed)
            ? parsed
            : decimal.MinValue;
    }

    private static int CompareDates(
        string left,
        string right)
    {
        bool hasLeft = DateTimeOffset.TryParse(
            left,
            CultureInfo.CurrentCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out DateTimeOffset leftDate);
        bool hasRight = DateTimeOffset.TryParse(
            right,
            CultureInfo.CurrentCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out DateTimeOffset rightDate);
        if (hasLeft && hasRight)
            return leftDate.CompareTo(rightDate);
        if (hasLeft != hasRight)
            return hasLeft ? 1 : -1;
        return string.Compare(
            left,
            right,
            StringComparison.CurrentCultureIgnoreCase);
    }
}

public partial class MetadataGridColumnEditorViewModel :
    ObservableObject
{
    private static readonly HashSet<TagFields>
        InlineEditableFields =
        [
            TagFields.Title,
            TagFields.Artist,
            TagFields.AlbumArtist,
            TagFields.Album,
            TagFields.Genre,
            TagFields.Composer,
            TagFields.Date,
            TagFields.TrackNumber,
            TagFields.DiscNumber,
        ];

    private readonly IMetadataGridColumnStore? _store;
    private readonly MetadataGridSurface _surface;
    private bool _loading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveColumnCommand))]
    private string? _label;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveColumnCommand))]
    private MetadataGridFieldKind _fieldKind;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveColumnCommand))]
    [NotifyPropertyChangedFor(nameof(CanInlineEdit))]
    private MetadataFieldChoice? _selectedKnownField;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveColumnCommand))]
    private string? _customFieldName;
    [ObservableProperty]
    private MetadataGridColumnSortType _sortType;
    [ObservableProperty]
    private double _width = 160;
    [ObservableProperty]
    private bool _inlineEditable;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteColumnCommand))]
    private UserMetadataColumnRow? _selectedColumn;
    [ObservableProperty]
    private string _status = "";

    public MetadataGridColumnEditorViewModel(
        IMetadataGridColumnStore? store,
        MetadataGridSurface surface)
    {
        _store = store;
        _surface = surface;
        KnownFields = Enum.GetValues<TagFields>()
            .Where(field => field != TagFields.NullField)
            .Select(field => new MetadataFieldChoice(
                field,
                field.ToString()))
            .ToArray();
        SelectedKnownField = KnownFields.First();
        Reload();
    }

    public event Action? Changed;
    public ObservableCollection<UserMetadataColumnRow>
        Columns { get; } = [];
    public IReadOnlyList<MetadataFieldChoice> KnownFields { get; }
    public IReadOnlyList<MetadataGridFieldKind> FieldKinds { get; } =
        Enum.GetValues<MetadataGridFieldKind>();
    public IReadOnlyList<MetadataGridColumnSortType> SortTypes { get; } =
        Enum.GetValues<MetadataGridColumnSortType>();
    public bool SupportsInlineEditing =>
        _surface == MetadataGridSurface.Workbench;
    public bool CanInlineEdit =>
        SupportsInlineEditing &&
        FieldKind == MetadataGridFieldKind.Known &&
        SelectedKnownField is not null &&
        InlineEditableFields.Contains(SelectedKnownField.Field);

    [RelayCommand]
    private void NewColumn()
    {
        _loading = true;
        try
        {
            SelectedColumn = null;
            Label = null;
            FieldKind = MetadataGridFieldKind.Known;
            SelectedKnownField = KnownFields.First();
            CustomFieldName = null;
            SortType = MetadataGridColumnSortType.Text;
            Width = 160;
            InlineEditable = false;
            Status = "New unsaved metadata column.";
        }
        finally
        {
            _loading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveColumn))]
    private void SaveColumn()
    {
        if (_store is null)
            return;
        MetadataFieldKey? field = CreateField();
        if (field is null)
        {
            Status = "Choose a known field or enter a custom field name.";
            return;
        }
        string label = Label?.Trim() ?? "";
        if (label.Length == 0)
        {
            Status = "Enter a column label.";
            return;
        }
        bool editable = InlineEditable && CanInlineEdit;
        int order = SelectedColumn?.Descriptor.Order ??
            Columns.Select(row => row.Descriptor.Order)
                .DefaultIfEmpty(1000)
                .Max() + 1;
        var descriptor = new UserMetadataColumnDescriptor(
            SelectedColumn?.Descriptor.Id ?? Guid.NewGuid(),
            label,
            field,
            SelectedColumn?.Descriptor.Visible ?? true,
            order,
            Math.Clamp(Width, 50, 2000),
            SortType,
            editable ? field : null);
        List<UserMetadataColumnDescriptor> descriptors =
            Columns.Select(row => row.Descriptor).ToList();
        int index = descriptors.FindIndex(column =>
            column.Id == descriptor.Id);
        if (index < 0)
            descriptors.Add(descriptor);
        else
            descriptors[index] = descriptor;
        _store.Save(_surface, descriptors);
        Reload(descriptor.Id);
        Status = $"Saved metadata column '{descriptor.Label}'.";
        Changed?.Invoke();
    }

    private bool CanSaveColumn() =>
        _store is not null &&
        !string.IsNullOrWhiteSpace(Label) &&
        (FieldKind == MetadataGridFieldKind.Known
            ? SelectedKnownField is not null
            : !string.IsNullOrWhiteSpace(CustomFieldName));

    [RelayCommand(CanExecute = nameof(CanDeleteColumn))]
    private void DeleteColumn()
    {
        if (_store is null || SelectedColumn is null)
            return;
        string label = SelectedColumn.Label;
        _store.Save(
            _surface,
            Columns
                .Where(row => !ReferenceEquals(
                    row,
                    SelectedColumn))
                .Select(row => row.Descriptor)
                .ToArray());
        Reload();
        NewColumn();
        Status = $"Removed metadata column '{label}'.";
        Changed?.Invoke();
    }

    private bool CanDeleteColumn() =>
        _store is not null && SelectedColumn is not null;

    public void PersistLayout(
        IReadOnlyList<LibraryColumnState> states)
    {
        if (_store is null || Columns.Count == 0)
            return;
        Dictionary<string, LibraryColumnState> byKey =
            states.ToDictionary(
                state => state.Key,
                StringComparer.OrdinalIgnoreCase);
        UserMetadataColumnDescriptor[] descriptors =
            Columns.Select(row =>
            {
                if (byKey.TryGetValue(
                        row.Descriptor.ColumnKey,
                        out LibraryColumnState? state))
                    row.Descriptor = row.Descriptor with
                    {
                        Visible = state.Visible,
                        Order = state.DisplayIndex,
                        Width = state.Width is > 0
                            ? state.Width.Value
                            : row.Descriptor.Width,
                    };
                return row.Descriptor;
            }).ToArray();
        _store.Save(_surface, descriptors);
    }

    partial void OnSelectedColumnChanged(
        UserMetadataColumnRow? value)
    {
        DeleteColumnCommand.NotifyCanExecuteChanged();
        if (_loading || value is null)
            return;
        _loading = true;
        try
        {
            Label = value.Descriptor.Label;
            FieldKind = value.Descriptor.Field.IsKnown
                ? MetadataGridFieldKind.Known
                : MetadataGridFieldKind.Custom;
            SelectedKnownField =
                value.Descriptor.Field.KnownField is { } known
                    ? KnownFields.First(choice =>
                        choice.Field == known)
                    : SelectedKnownField;
            CustomFieldName =
                value.Descriptor.Field.CustomName;
            SortType = value.Descriptor.SortType;
            Width = value.Descriptor.Width;
            InlineEditable =
                value.Descriptor.EditTarget is not null;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnFieldKindChanged(
        MetadataGridFieldKind value)
    {
        OnPropertyChanged(nameof(CanInlineEdit));
        if (!CanInlineEdit)
            InlineEditable = false;
    }

    partial void OnSelectedKnownFieldChanged(
        MetadataFieldChoice? value)
    {
        if (!CanInlineEdit)
            InlineEditable = false;
    }

    private MetadataFieldKey? CreateField() =>
        FieldKind == MetadataGridFieldKind.Known
            ? SelectedKnownField is { } known
                ? MetadataFieldKey.Known(known.Field)
                : null
            : string.IsNullOrWhiteSpace(CustomFieldName)
                ? null
                : MetadataFieldKey.Custom(CustomFieldName);

    private void Reload(Guid? selected = null)
    {
        ReplaceRows(
            _store?.Load(_surface) ?? [],
            selected);
    }

    private void ReplaceRows(
        IEnumerable<UserMetadataColumnDescriptor> descriptors,
        Guid? selected)
    {
        Columns.Clear();
        foreach (UserMetadataColumnDescriptor descriptor in
                 descriptors.OrderBy(column => column.Order))
            Columns.Add(new(descriptor));
        SelectedColumn = selected is null
            ? null
            : Columns.FirstOrDefault(row =>
                row.Descriptor.Id == selected);
    }
}
