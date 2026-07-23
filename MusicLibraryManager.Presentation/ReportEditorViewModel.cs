using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicFileUtilities;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

public sealed record ReportFieldChoice(
    string Label,
    ReportFieldDescriptor Descriptor);

public sealed record ReportFieldEditorRow(
    ReportFieldDescriptor Descriptor)
{
    public string Label => Descriptor.Label;
    public string Source => Descriptor.Kind switch
    {
        ReportFieldKind.KnownMetadata =>
            $"Metadata: {Descriptor.KnownField}",
        ReportFieldKind.CustomMetadata =>
            $"Custom: {Descriptor.Name}",
        ReportFieldKind.FileProperty =>
            $"File: {Descriptor.Name}",
        ReportFieldKind.TechnicalProperty =>
            $"Technical: {Descriptor.Name}",
        _ => Descriptor.Kind.ToString(),
    };
}

public sealed record ReportOutputRow(
    string Group,
    string File,
    int Rows,
    int Bytes);

public partial class ReportEditorViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedFieldCommand))]
    private ReportFieldChoice? _selectedAvailableField;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveFieldCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveFieldUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveFieldDownCommand))]
    private ReportFieldEditorRow? _selectedField;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCustomFieldCommand))]
    private string? _customFieldName;
    [ObservableProperty] private string _name = "Music library report";
    [ObservableProperty] private ReportFormat _format = ReportFormat.Csv;
    [ObservableProperty] private ReportEncoding _encoding = ReportEncoding.Utf8;
    [ObservableProperty] private string? _outputPath;
    [ObservableProperty] private bool _oneFilePerGroup;
    [ObservableProperty] private string _groupFileNameTemplate =
        "{Group}.{Format}";
    [ObservableProperty] private ReportFieldEditorRow? _selectedGroupField;
    [ObservableProperty] private ReportFieldEditorRow? _selectedSortField;
    [ObservableProperty] private ReportSortType _sortType =
        ReportSortType.Natural;
    [ObservableProperty] private bool _sortDescending;

    public ReportEditorViewModel()
    {
        AvailableFields = BuildChoices();
        Fields.CollectionChanged += OnFieldsChanged;
        AddDefault(TagFields.Artist);
        AddDefault(TagFields.Album);
        AddDefault(TagFields.DiscNumber);
        AddDefault(TagFields.TrackNumber);
        AddDefault(TagFields.Title);
        Fields.Add(new(ReportFieldDescriptor.File(
            "FileName", "File name")));
        SelectedAvailableField = AvailableFields.FirstOrDefault();
        SelectedSortField = Fields.FirstOrDefault(row =>
            row.Descriptor.KnownField == TagFields.TrackNumber);
    }

    public ObservableCollection<ReportFieldEditorRow> Fields { get; } = [];
    public IReadOnlyList<ReportFieldChoice> AvailableFields { get; }
    public IReadOnlyList<ReportFormat> Formats { get; } =
        Enum.GetValues<ReportFormat>();
    public IReadOnlyList<ReportEncoding> Encodings { get; } =
        Enum.GetValues<ReportEncoding>();
    public IReadOnlyList<ReportSortType> SortTypes { get; } =
        Enum.GetValues<ReportSortType>();
    public string SuggestedExtension => Format switch
    {
        ReportFormat.Text => "txt",
        ReportFormat.Csv => "csv",
        ReportFormat.Html => "html",
        ReportFormat.Rtf => "rtf",
        _ => "txt",
    };

    public event Action? Changed;

    partial void OnNameChanged(string value) => Changed?.Invoke();
    partial void OnFormatChanged(ReportFormat value)
    {
        OnPropertyChanged(nameof(SuggestedExtension));
        Changed?.Invoke();
    }
    partial void OnEncodingChanged(ReportEncoding value) =>
        Changed?.Invoke();
    partial void OnOutputPathChanged(string? value) => Changed?.Invoke();
    partial void OnOneFilePerGroupChanged(bool value) => Changed?.Invoke();
    partial void OnGroupFileNameTemplateChanged(string value) =>
        Changed?.Invoke();
    partial void OnSelectedGroupFieldChanged(
        ReportFieldEditorRow? value) => Changed?.Invoke();
    partial void OnSelectedSortFieldChanged(
        ReportFieldEditorRow? value) => Changed?.Invoke();
    partial void OnSortTypeChanged(ReportSortType value) =>
        Changed?.Invoke();
    partial void OnSortDescendingChanged(bool value) =>
        Changed?.Invoke();

    public ReportConfiguration CreateConfiguration()
    {
        var sorting = SelectedSortField is null
            ? []
            : new[]
            {
                new ReportSortDescriptor(
                    SelectedSortField.Descriptor.Id,
                    SortType,
                    SortDescending),
            };
        return new(
            Name.Trim(),
            Format,
            OutputPath?.Trim() ?? "",
            Fields.Select(row => row.Descriptor).ToImmutableArray(),
            [.. sorting],
            SelectedGroupField?.Descriptor.Id,
            OneFilePerGroup,
            GroupFileNameTemplate,
            Encoding);
    }

    private bool CanAddSelectedField() =>
        SelectedAvailableField is not null &&
        Fields.All(row => !row.Descriptor.Id.Equals(
            SelectedAvailableField.Descriptor.Id,
            StringComparison.OrdinalIgnoreCase));

    [RelayCommand(CanExecute = nameof(CanAddSelectedField))]
    private void AddSelectedField()
    {
        if (SelectedAvailableField is null)
            return;
        var row = new ReportFieldEditorRow(
            SelectedAvailableField.Descriptor);
        Fields.Add(row);
        SelectedField = row;
    }

    private bool CanAddCustomField() =>
        !string.IsNullOrWhiteSpace(CustomFieldName);

    [RelayCommand(CanExecute = nameof(CanAddCustomField))]
    private void AddCustomField()
    {
        string name = CustomFieldName?.Trim() ?? "";
        if (name.Length == 0)
            return;
        string id = "custom." + name;
        ReportFieldEditorRow? existing = Fields.FirstOrDefault(row =>
            row.Descriptor.Id.Equals(
                id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectedField = existing;
            return;
        }
        var row = new ReportFieldEditorRow(
            ReportFieldDescriptor.Custom(name));
        Fields.Add(row);
        SelectedField = row;
        CustomFieldName = null;
    }

    private bool CanRemoveField() => SelectedField is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveField))]
    private void RemoveField()
    {
        if (SelectedField is null)
            return;
        int index = Fields.IndexOf(SelectedField);
        Fields.Remove(SelectedField);
        SelectedField = Fields.Count == 0
            ? null
            : Fields[Math.Min(index, Fields.Count - 1)];
    }

    private bool CanMoveFieldUp() =>
        SelectedField is not null &&
        Fields.IndexOf(SelectedField) > 0;

    [RelayCommand(CanExecute = nameof(CanMoveFieldUp))]
    private void MoveFieldUp()
    {
        if (SelectedField is null)
            return;
        int index = Fields.IndexOf(SelectedField);
        if (index > 0)
            Fields.Move(index, index - 1);
    }

    private bool CanMoveFieldDown() =>
        SelectedField is not null &&
        Fields.IndexOf(SelectedField) is var index &&
        index >= 0 && index < Fields.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveFieldDown))]
    private void MoveFieldDown()
    {
        if (SelectedField is null)
            return;
        int index = Fields.IndexOf(SelectedField);
        if (index >= 0 && index < Fields.Count - 1)
            Fields.Move(index, index + 1);
    }

    private void OnFieldsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (SelectedGroupField is not null &&
            !Fields.Contains(SelectedGroupField))
            SelectedGroupField = null;
        if (SelectedSortField is not null &&
            !Fields.Contains(SelectedSortField))
            SelectedSortField = null;
        AddSelectedFieldCommand.NotifyCanExecuteChanged();
        RemoveFieldCommand.NotifyCanExecuteChanged();
        MoveFieldUpCommand.NotifyCanExecuteChanged();
        MoveFieldDownCommand.NotifyCanExecuteChanged();
        Changed?.Invoke();
    }

    private void AddDefault(TagFields field) =>
        Fields.Add(new(ReportFieldDescriptor.Known(field)));

    private static IReadOnlyList<ReportFieldChoice> BuildChoices()
    {
        var choices = Enum.GetValues<TagFields>()
            .Where(field => field != TagFields.NullField)
            .Select(field => new ReportFieldChoice(
                $"Metadata — {field}",
                ReportFieldDescriptor.Known(field)))
            .ToList();
        choices.AddRange(
        [
            Choice("File — Path", ReportFieldDescriptor.File("Path")),
            Choice("File — File name",
                ReportFieldDescriptor.File("FileName", "File name")),
            Choice("File — Directory",
                ReportFieldDescriptor.File("Directory")),
            Choice("File — Extension",
                ReportFieldDescriptor.File("Extension")),
            Choice("File — Length",
                ReportFieldDescriptor.File("Length", "File size")),
            Choice("File — Modified",
                ReportFieldDescriptor.File("Modified")),
            Choice("Technical — Codec",
                ReportFieldDescriptor.Technical("Codec")),
            Choice("Technical — Codec type",
                ReportFieldDescriptor.Technical("CodecType", "Codec type")),
            Choice("Technical — Bitrate",
                ReportFieldDescriptor.Technical("Bitrate")),
            Choice("Technical — Maximum bitrate",
                ReportFieldDescriptor.Technical(
                    "MaxBitrate", "Maximum bitrate")),
            Choice("Technical — Bits per sample",
                ReportFieldDescriptor.Technical(
                    "BitsPerSample", "Bits per sample")),
            Choice("Technical — Sample rate",
                ReportFieldDescriptor.Technical(
                    "SampleRate", "Sample rate")),
            Choice("Technical — Channels",
                ReportFieldDescriptor.Technical("Channels")),
            Choice("Technical — Duration",
                ReportFieldDescriptor.Technical("Duration")),
        ]);
        return choices;

        static ReportFieldChoice Choice(
            string label,
            ReportFieldDescriptor descriptor) =>
            new(label, descriptor);
    }
}
