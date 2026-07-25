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
            LocalizedText.Format(
                "Workbench.Reports.Source.Metadata",
                Descriptor.KnownField is { } knownField
                    ? LocalizedText.Get(
                        $"Settings.Choice.TagFields.{knownField}")
                    : ""),
        ReportFieldKind.CustomMetadata =>
            LocalizedText.Format(
                "Workbench.Reports.Source.Custom",
                Descriptor.Name),
        ReportFieldKind.FileProperty =>
            LocalizedText.Format(
                "Workbench.Reports.Source.File",
                Descriptor.Name),
        ReportFieldKind.TechnicalProperty =>
            LocalizedText.Format(
                "Workbench.Reports.Source.Technical",
                Descriptor.Name),
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
    private const string DefaultNameResourceKey =
        "Workbench.Reports.DefaultName";
    private readonly ILocalizationService? _localization;
    private bool _nameUsesLocalizedDefault;
    private bool _settingLocalizedDefaultName;
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
    [ObservableProperty] private string _name = "";
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

    public ReportEditorViewModel(
        ILocalizationService? localization = null)
    {
        _localization = localization;
        SetLocalizedDefaultName();
        RefreshLocalizedChoices();
        Fields.CollectionChanged += OnFieldsChanged;
        AddDefault(TagFields.Artist);
        AddDefault(TagFields.Album);
        AddDefault(TagFields.DiscNumber);
        AddDefault(TagFields.TrackNumber);
        AddDefault(TagFields.Title);
        Fields.Add(new(ReportFieldDescriptor.File(
            "FileName",
            L("Workbench.Reports.Field.FileName"))));
        SelectedAvailableField = AvailableFields.FirstOrDefault();
        SelectedSortField = Fields.FirstOrDefault(row =>
            row.Descriptor.KnownField == TagFields.TrackNumber);
        if (_localization is not null)
            _localization.CultureChanged +=
                OnLocalizationCultureChanged;
    }

    public ObservableCollection<ReportFieldEditorRow> Fields { get; } = [];
    public ObservableCollection<ReportFieldChoice>
        AvailableFields { get; } = [];
    public IReadOnlyList<ReportFormat> Formats { get; } =
        Enum.GetValues<ReportFormat>();
    public IReadOnlyList<ReportEncoding> Encodings { get; } =
        Enum.GetValues<ReportEncoding>();
    public IReadOnlyList<ReportSortType> SortTypes { get; } =
        Enum.GetValues<ReportSortType>();
    public ObservableCollection<LocalizedChoice<ReportFormat>>
        FormatChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<ReportEncoding>>
        EncodingChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<ReportSortType>>
        SortTypeChoices { get; } = [];
    public string SuggestedExtension => Format switch
    {
        ReportFormat.Text => "txt",
        ReportFormat.Csv => "csv",
        ReportFormat.Html => "html",
        ReportFormat.Rtf => "rtf",
        _ => "txt",
    };

    public event Action? Changed;

    partial void OnNameChanged(string value)
    {
        if (!_settingLocalizedDefaultName)
            _nameUsesLocalizedDefault = false;
        Changed?.Invoke();
    }
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
            OneFilePerGroup
                ? SelectedGroupField?.Descriptor.Id
                : null,
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

    private IReadOnlyList<ReportFieldChoice> BuildChoices()
    {
        var choices = Enum.GetValues<TagFields>()
            .Where(field => field != TagFields.NullField)
            .Select(field => new ReportFieldChoice(
                LF(
                    "Workbench.Reports.Field.Metadata",
                    L($"Settings.Choice.TagFields.{field}")),
                ReportFieldDescriptor.Known(field)))
            .ToList();
        choices.AddRange(
        [
            FileChoice("Path", ReportFieldDescriptor.File("Path")),
            FileChoice("FileName",
                ReportFieldDescriptor.File(
                    "FileName",
                    L("Workbench.Reports.Field.FileName"))),
            FileChoice("Directory",
                ReportFieldDescriptor.File("Directory")),
            FileChoice("Extension",
                ReportFieldDescriptor.File("Extension")),
            FileChoice("Length",
                ReportFieldDescriptor.File(
                    "Length",
                    L("Workbench.Reports.Field.FileSize"))),
            FileChoice("Modified",
                ReportFieldDescriptor.File("Modified")),
            TechnicalChoice("Codec",
                ReportFieldDescriptor.Technical("Codec")),
            TechnicalChoice("CodecType",
                ReportFieldDescriptor.Technical(
                    "CodecType",
                    L("Workbench.Reports.Field.CodecType"))),
            TechnicalChoice("Bitrate",
                ReportFieldDescriptor.Technical("Bitrate")),
            TechnicalChoice("MaximumBitrate",
                ReportFieldDescriptor.Technical(
                    "MaxBitrate",
                    L("Workbench.Reports.Field.MaximumBitrate"))),
            TechnicalChoice("BitsPerSample",
                ReportFieldDescriptor.Technical(
                    "BitsPerSample",
                    L("Workbench.Reports.Field.BitsPerSample"))),
            TechnicalChoice("SampleRate",
                ReportFieldDescriptor.Technical(
                    "SampleRate",
                    L("Workbench.Reports.Field.SampleRate"))),
            TechnicalChoice("Channels",
                ReportFieldDescriptor.Technical("Channels")),
            TechnicalChoice("Duration",
                ReportFieldDescriptor.Technical("Duration")),
        ]);
        return choices;

        ReportFieldChoice FileChoice(
            string name,
            ReportFieldDescriptor descriptor) =>
            new(
                LF(
                    "Workbench.Reports.Field.File",
                    L($"Workbench.Reports.Field.{name}")),
                descriptor);

        ReportFieldChoice TechnicalChoice(
            string name,
            ReportFieldDescriptor descriptor) =>
            new(
                LF(
                    "Workbench.Reports.Field.Technical",
                    L($"Workbench.Reports.Field.{name}")),
                descriptor);
    }

    private string L(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);

    private string LF(
        string key,
        params object?[] arguments) =>
        _localization?.Format(key, arguments) ??
        LocalizedText.Format(key, arguments);

    private void SetLocalizedDefaultName()
    {
        _settingLocalizedDefaultName = true;
        try
        {
            Name = L(DefaultNameResourceKey);
        }
        finally
        {
            _settingLocalizedDefaultName = false;
        }
        _nameUsesLocalizedDefault = true;
    }

    private void RefreshLocalizedChoices()
    {
        string? selectedId =
            SelectedAvailableField?.Descriptor.Id;
        AvailableFields.Clear();
        foreach (ReportFieldChoice choice in BuildChoices())
            AvailableFields.Add(choice);
        SelectedAvailableField = selectedId is null
            ? AvailableFields.FirstOrDefault()
            : AvailableFields.FirstOrDefault(choice =>
                choice.Descriptor.Id.Equals(
                    selectedId,
                    StringComparison.OrdinalIgnoreCase));
        RefreshChoices(
            FormatChoices,
            Formats,
            "Workbench.Choice.ReportFormat");
        RefreshChoices(
            EncodingChoices,
            Encodings,
            "Workbench.Choice.ReportEncoding");
        RefreshChoices(
            SortTypeChoices,
            SortTypes,
            "Workbench.Choice.ReportSortType");
    }

    private void RefreshChoices<T>(
        ObservableCollection<LocalizedChoice<T>> target,
        IEnumerable<T> values,
        string keyPrefix)
    {
        foreach (T value in values)
        {
            LocalizedChoice<T>? choice =
                target.FirstOrDefault(item =>
                    EqualityComparer<T>.Default.Equals(
                        item.Value,
                        value));
            string label = L($"{keyPrefix}.{value}");
            if (choice is null)
                target.Add(new(value, label));
            else
                choice.Label = label;
        }
    }

    private void OnLocalizationCultureChanged(
        object? sender,
        EventArgs e)
    {
        if (_nameUsesLocalizedDefault)
            SetLocalizedDefaultName();
        RefreshLocalizedChoices();
        OnPropertyChanged(nameof(Fields));
    }
}
