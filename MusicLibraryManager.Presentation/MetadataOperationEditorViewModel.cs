using CommunityToolkit.Mvvm.ComponentModel;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

public sealed record MetadataFieldChoice(TagFields Field, string Label);

public sealed record MetadataPreviewRow(
    string File,
    string Field,
    string Before,
    string After);

/// <summary>
/// Shared typed-operation editor state. Workbench and Library own separate instances so personal
/// in-progress input does not leak between pages, while both are driven by one Core catalog.
/// </summary>
public partial class MetadataOperationEditorViewModel : ObservableObject
{
    private readonly IMetadataOperationCatalog _catalog;

    public MetadataOperationEditorViewModel(
        IMetadataOperationCatalog catalog,
        MetadataOperationSurface surface)
    {
        _catalog = catalog;
        OperationDescriptors = catalog.Operations
            .Where(operation => operation.Supports(surface))
            .ToArray();
        Fields =
        [
            new(TagFields.Title, "Title"),
            new(TagFields.Artist, "Artist"),
            new(TagFields.AlbumArtist, "Album artist"),
            new(TagFields.Album, "Album"),
            new(TagFields.Genre, "Genre"),
            new(TagFields.Composer, "Composer"),
            new(TagFields.Date, "Date"),
            new(TagFields.TrackNumber, "Track"),
            new(TagFields.TotalTracks, "Track total"),
            new(TagFields.DiscNumber, "Disc"),
            new(TagFields.TotalDiscs, "Disc total"),
            new(TagFields.Comment, "Comment"),
        ];
        SelectedOperation = OperationDescriptors.FirstOrDefault();
        SelectedField = Fields[0];
        DestinationField = Fields[1];
    }

    public IReadOnlyList<MetadataOperationDescriptor> OperationDescriptors { get; }
    public IReadOnlyList<MetadataFieldChoice> Fields { get; }
    public IReadOnlyList<MetadataCaseMode> CaseModes { get; } =
        Enum.GetValues<MetadataCaseMode>();

    [ObservableProperty]
    private MetadataOperationDescriptor? _selectedOperation;

    [ObservableProperty]
    private MetadataFieldChoice? _selectedField;

    [ObservableProperty]
    private MetadataFieldChoice? _destinationField;

    [ObservableProperty]
    private string? _operationValue;

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private string? _replacementText;

    [ObservableProperty]
    private bool _useRegularExpression;

    [ObservableProperty]
    private MetadataCaseMode _selectedCaseMode = MetadataCaseMode.Title;

    [ObservableProperty]
    private int _sequenceStart = 1;

    [ObservableProperty]
    private int _sequencePadding = 2;

    public bool CanCreate => SelectedOperation is not null && SelectedField is not null;

    public OperationRecipe CreateRecipe(string? name = null)
    {
        if (SelectedOperation is null || SelectedField is null)
            throw new InvalidOperationException("Choose an operation and field.");
        MetadataOperation operation = _catalog.Create(new(
            SelectedOperation.Kind,
            MetadataFieldKey.Known(SelectedField.Field),
            DestinationField is null
                ? null
                : MetadataFieldKey.Known(DestinationField.Field),
            OperationValue,
            SearchText,
            ReplacementText,
            UseRegularExpression,
            SelectedCaseMode,
            SequenceStart,
            SequencePadding));
        return OperationRecipe.Create(
            name ?? $"{SelectedOperation.DisplayName}: {SelectedField.Label}",
            operation);
    }
}
