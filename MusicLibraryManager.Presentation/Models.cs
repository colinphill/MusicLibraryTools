using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using MetadataCaching;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;

namespace MusicLibraryManager.Presentation;

public sealed record LibraryColumnState(string Key, double? Width, int DisplayIndex, bool Visible);

public sealed record LibrarySortState(string Key, bool Descending);

public enum LibraryPageState
{
    NoConfiguration,
    Loading,
    Ready,
    NotIndexed,
    NoResults,
    FilteredToZero,
    Error,
}

public enum FieldValueVerification
{
    Exact,
    Unverified,
}

public enum MessageTone
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record LibraryViewDefinition(
    string Name,
    string? Filter,
    FilterMode FilterMode,
    IReadOnlyList<LibraryColumnState> Columns,
    LibrarySortState? Sort,
    LibraryVisualFilterNode? VisualFilter = null);

public partial class LibraryColumnChoice : ObservableObject
{
    public LibraryColumnChoice(
        string key,
        string header,
        bool isVisible,
        string? headerResourceKey = null)
    {
        Key = key;
        _header = header;
        _isVisible = isVisible;
        HeaderResourceKey = headerResourceKey;
    }

    public string Key { get; }
    public string? HeaderResourceKey { get; }

    [ObservableProperty]
    private string _header;

    [ObservableProperty]
    private bool _isVisible;

    public void RefreshLocalizedText(
        Func<string, string> getText)
    {
        ArgumentNullException.ThrowIfNull(getText);
        if (HeaderResourceKey is not null)
            Header = getText(HeaderResourceKey);
    }
}

public sealed record SelectionContext(
    IReadOnlyList<string> Paths,
    IReadOnlyList<TrackRecord>? Records = null,
    bool ReadArtworkDirectly = false)
{
    public static SelectionContext Empty { get; } = new([]);
    public bool HasSelection => Paths.Count > 0;
    public string Summary => Paths.Count switch
    {
        0 => LocalizedText.Get(
            "Inspector.Selection.NothingSelected"),
        1 => Path.GetFileName(Paths[0]),
        _ => LocalizedText.FormatCount(
            "Inspector.Selection.TracksSelected",
            Paths.Count),
    };
}

public sealed record LibraryPendingMetadataEdit(
    MetadataValueEdit Edit,
    ImmutableArray<string> OriginalValues);

public partial class LibraryRow : ObservableObject
{
    private static readonly TagFields[]
        InlineEditFields =
        [
            TagFields.Title,
            TagFields.Artist,
            TagFields.AlbumArtist,
            TagFields.Album,
            TagFields.Genre,
            TagFields.Composer,
            TagFields.Grouping,
            TagFields.Date,
            TagFields.TrackNumber,
            TagFields.TotalTracks,
            TagFields.DiscNumber,
            TagFields.TotalDiscs,
            TagFields.Comment,
        ];
    private readonly string?[] _originalKnownValues;
    private ImmutableArray<string>[]?
        _originalKnownValueSets;
    private bool _suppressPendingNotification;
    private bool _synchronizingMetadataProjection;
    private long? _pendingSourceLength;
    private DateTime? _pendingSourceLastWriteTimeUtc;
    private Dictionary<
        MetadataFieldKey,
        ImmutableArray<string>>?
        _pendingExpectedOriginalValues;
    private HashSet<MetadataFieldKey>?
        _loadedProjectionFields;
    private bool _isEditReserved;
    private LibraryMetadataValueMap?
        _metadataValues;

    public LibraryRow(TrackRecord record)
    {
        // Older caches can contain the FLAC tag implementation name (Vorbis) in the codec field.
        // The container extension is authoritative here: Vorbis comments are the tag format,
        // while the audio/file type remains FLAC.
        bool flacWithTagNameAsCodec =
            System.IO.Path.GetExtension(record.Path).Equals(".flac", StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(record.CodecName) ||
             record.CodecName.StartsWith("Vorbis", StringComparison.OrdinalIgnoreCase));
        Record = flacWithTagNameAsCodec
            ? record with { CodecName = "FLAC" }
            : record;
        _metadataValues = BuildMetadataValues(
            Record.Metadata);
        if (_metadataValues is not null)
            _metadataValues.Changed +=
                OnMetadataValuesChanged;
        _title = Record.Title ?? "";
        _artist = Record.Artist ?? "";
        _albumArtist = Record.AlbumArtist ?? "";
        _album = Record.Album ?? "";
        _genre = Record.Genre ?? "";
        _composer = Record.Composer ?? "";
        _grouping = Record.Grouping ?? "";
        _year =
            Record.ReleaseDate ??
            Record.Year?.ToString() ??
            "";
        _trackEditValue =
            Record.TrackNumber?.ToString();
        _trackTotalEditValue =
            Record.TrackTotal?.ToString();
        _discEditValue =
            Record.DiscNumber?.ToString();
        _discTotalEditValue =
            Record.DiscTotal?.ToString();
        _comment =
            _metadataValues?.GetValueOrDefault(
                MetadataGridValueKey.For(
                    MetadataFieldKey.Known(
                        TagFields.Comment))) ??
            "";
        _originalKnownValues =
        [
            Title,
            Artist,
            AlbumArtist,
            Album,
            Genre,
            Composer,
            Grouping,
            Year,
            TrackEditValue,
            TrackTotalEditValue,
            DiscEditValue,
            DiscTotalEditValue,
            Comment,
        ];
        Details = new DetailsRow(Record);
    }

    public TrackRecord Record { get; private set; }
    public LibraryMetadataValueMap MetadataValues =>
        EnsureMetadataValues();
    public DetailsRow Details { get; private set; }
    public string Path => Record.Path;
    public int? Track => Record.TrackNumber;
    public int? TrackTotal => Record.TrackTotal;
    public int? Disc => Record.DiscNumber;
    public int? DiscTotal => Record.DiscTotal;
    public string Codec => Record.CodecName ?? "";
    public string TagType => Record.TagType ?? "";
    public string CodecType => Record.CodecType.ToString();
    public string SampleRate => Details["SampleRate"];
    public string BitsPerSample => Details["Bits"];
    public string Bitrate => Details["Bitrate"];
    public string Channels => Details["Channels"];
    public string Duration => Details["Duration"];
    public string FileSize => Details["FileSize"];
    public string Modified => Details["Modified"];
    public string SearchText => Details.SearchText;
    public bool HasChanges =>
        HasKnownChanges() ||
        _metadataValues?.HasChanges == true;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _artist;

    [ObservableProperty]
    private string _albumArtist;

    [ObservableProperty]
    private string _album;

    [ObservableProperty]
    private string _genre;

    [ObservableProperty]
    private string _composer;

    [ObservableProperty]
    private string _grouping;

    [ObservableProperty]
    private string _year;

    [ObservableProperty]
    private string? _trackEditValue;

    [ObservableProperty]
    private string? _trackTotalEditValue;

    [ObservableProperty]
    private string? _discEditValue;

    [ObservableProperty]
    private string? _discTotalEditValue;

    [ObservableProperty]
    private string _comment;

    [ObservableProperty]
    private object? _thumbnailSource;

    [ObservableProperty]
    private bool _thumbnailLoaded;

    public event EventHandler? PendingChangesChanged;
    public long PendingChangesVersion { get; private set; }
    internal bool IsEditReserved => _isEditReserved;

    internal void SetEditReservation(bool reserved)
    {
        _isEditReserved = reserved;
        _metadataValues?.SetEditReservation(
            reserved);
    }

    public IReadOnlyList<MetadataValueEdit>
        CreatePendingEdits()
        => CreatePendingEditStates()
            .Select(state => state.Edit)
            .ToArray();

    public IReadOnlyList<LibraryPendingMetadataEdit>
        CreatePendingEditStates()
    {
        var edits = new Dictionary<
            string,
            LibraryPendingMetadataEdit>(
            StringComparer.OrdinalIgnoreCase);
        for (int index = 0;
             index < InlineEditFields.Length;
             index++)
        {
            TagFields field =
                InlineEditFields[index];
            MetadataFieldKey key =
                MetadataFieldKey.Known(field);
            if (_metadataValues?.IsPending(
                    key) == true)
                continue;
            string? original =
                _originalKnownValues[index];
            string? current = KnownValue(field);
            if (StringComparer.Ordinal.Equals(
                    original,
                    current))
                continue;
            edits[MetadataGridValueKey.For(key)] =
                new(
                    new(
                        key,
                        LibraryMetadataValueMap
                            .ParseEditorValues(
                                current ?? "")),
                    OriginalKnownValueSet(index));
        }
        foreach (LibraryPendingMetadataEdit edit in
                 _metadataValues?
                     .CreatePendingEditStates() ??
                 [])
        {
            string key =
                MetadataGridValueKey.For(
                    edit.Edit.Field);
            edits[key] = edit;
        }
        return edits.Values.ToArray();
    }

    public MetadataEditSourceExpectation
        CreatePendingSourceExpectation()
    {
        IReadOnlyList<LibraryPendingMetadataEdit>
            pending = CreatePendingEditStates();
        return new(
            _pendingSourceLength,
            _pendingSourceLastWriteTimeUtc,
            pending.ToDictionary(
                item => item.Edit.Field,
                item =>
                    _pendingExpectedOriginalValues
                        ?.GetValueOrDefault(
                            item.Edit.Field,
                            item.OriginalValues) ??
                    item.OriginalValues));
    }

    public IEnumerable<MetadataPreviewRow>
        CreatePendingChangeRows(
            Func<MetadataFieldKey, string>?
                fieldLabel = null)
    {
        for (int index = 0;
             index < InlineEditFields.Length;
             index++)
        {
            TagFields field =
                InlineEditFields[index];
            if (_metadataValues?.IsPending(
                    MetadataFieldKey.Known(
                        field)) == true)
                continue;
            string? original =
                _originalKnownValues[index];
            string? current = KnownValue(field);
            if (StringComparer.Ordinal.Equals(
                    original,
                    current))
                continue;
            yield return new(
                System.IO.Path.GetFileName(Path),
                fieldLabel?.Invoke(
                    MetadataFieldKey.Known(
                        field)) ??
                MetadataFieldKey.Known(field)
                    .DisplayName,
                LibraryMetadataValueMap
                    .FormatEditorValues(
                        OriginalKnownValueSet(
                            index)),
                current ?? "");
        }
        foreach ((MetadataFieldKey field,
                     string before,
                     string after) in
                 _metadataValues?
                     .CreatePendingRows() ??
                 [])
        {
            yield return new(
                System.IO.Path.GetFileName(Path),
                fieldLabel?.Invoke(field) ??
                field.DisplayName,
                before,
                after);
        }
    }

    /// <summary>
    /// Overlays only this row's pending metadata values onto a freshly loaded cache row.
    /// Untouched fields continue to come from the new cache record while edit-time originals
    /// and source identity remain immutable for stale-source validation.
    /// </summary>
    public void CopyPendingChangesTo(
        LibraryRow destination)
    {
        ArgumentNullException.ThrowIfNull(
            destination);
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                Path,
                destination.Path))
            throw new ArgumentException(
                nameof(destination));

        IReadOnlyList<LibraryPendingMetadataEdit>
            pending = CreatePendingEditStates();
        if (pending.Count == 0)
            return;
        MetadataEditSourceExpectation
            expectation =
                CreatePendingSourceExpectation();

        destination._suppressPendingNotification =
            true;
        destination
            ._synchronizingMetadataProjection =
            true;
        try
        {
            foreach (LibraryPendingMetadataEdit
                         state in pending)
            {
                MetadataFieldKey field =
                    state.Edit.Field;
                if (field.KnownField is
                        { } known &&
                    TryInlineEditIndex(
                        known,
                        out int index) &&
                    _metadataValues?.IsPending(
                        field) != true)
                {
                    string current =
                        KnownValue(known) ?? "";
                    destination.SetKnownValue(
                        known,
                        current);
                    destination.MetadataValues
                        .SetProjection(
                            field,
                            current);
                    continue;
                }

                ImmutableArray<string>
                    destinationOriginal =
                    field.KnownField is
                            { } destinationKnown &&
                        TryInlineEditIndex(
                            destinationKnown,
                            out int destinationIndex)
                        ? destination
                            .OriginalKnownValueSet(
                                destinationIndex)
                        : destination
                            .MetadataValues
                            .OriginalValues(field);
                destination.MetadataValues
                    .ImportPendingEdit(
                        state with
                        {
                            OriginalValues =
                                destinationOriginal,
                        });
                if (field.KnownField is
                        { } projected &&
                    TryInlineEditIndex(
                        projected,
                        out _))
                {
                    destination.SetKnownValue(
                        projected,
                        LibraryMetadataValueMap
                            .FormatEditorValues(
                                state.Edit.Values));
                }
            }
            destination._pendingSourceLength =
                _pendingSourceLength;
            destination
                ._pendingSourceLastWriteTimeUtc =
                _pendingSourceLastWriteTimeUtc;
            destination
                ._pendingExpectedOriginalValues =
                expectation.OriginalValues
                    .ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value);
            destination.PendingChangesVersion =
                PendingChangesVersion;
        }
        finally
        {
            destination
                ._synchronizingMetadataProjection =
                false;
            destination
                ._suppressPendingNotification =
                false;
        }
        destination.NotifyPendingChangesChanged();
    }

    public void InvalidateThumbnail()
    {
        ThumbnailSource = null;
        ThumbnailLoaded = false;
    }

    internal void LoadMetadataProjection(
        IReadOnlyDictionary<
            MetadataFieldKey,
            ImmutableArray<string>> values,
        IReadOnlyList<
            MetadataFieldKey> requestedFields)
    {
        foreach (MetadataFieldKey field in
                 requestedFields)
        {
            ImmutableArray<string> fieldValues =
                values.GetValueOrDefault(
                    field,
                    []);
            MetadataValues.LoadProjection(
                field,
                fieldValues);
            (_loadedProjectionFields ??= [])
                .Add(field);
            if (field.KnownField is
                    TagFields.Comment &&
                !IsFieldPending(field))
            {
                _synchronizingMetadataProjection =
                    true;
                try
                {
                    string projected =
                        LibraryMetadataValueMap
                            .FormatEditorValues(
                                fieldValues);
                    Comment = projected;
                    int index = Array.IndexOf(
                        InlineEditFields,
                        TagFields.Comment);
                    _originalKnownValues[index] =
                        projected;
                    if (_originalKnownValueSets is
                        not null)
                        _originalKnownValueSets[index] =
                            fieldValues;
                }
                finally
                {
                    _synchronizingMetadataProjection =
                        false;
                }
            }
        }
    }

    internal void BeginMetadataProjection(
        IReadOnlyList<
            MetadataFieldKey> fields)
    {
        foreach (MetadataFieldKey field in
                 fields)
            MetadataValues
                .BeginProjection(field);
    }

    internal void FailMetadataProjection(
        IReadOnlyList<
            MetadataFieldKey> fields)
    {
        foreach (MetadataFieldKey field in
                 fields)
            MetadataValues
                .FailProjection(field);
    }

    internal void ClearMetadataProjection(
        IReadOnlyList<
            MetadataFieldKey> fields)
    {
        foreach (MetadataFieldKey field in
                 fields)
        {
            _metadataValues?.ClearProjection(
                field);
            _loadedProjectionFields?.Remove(
                field);
            if (field.KnownField is
                    TagFields.Comment &&
                !IsFieldPending(field))
            {
                _synchronizingMetadataProjection =
                    true;
                try
                {
                    Comment = "";
                    int index = Array.IndexOf(
                        InlineEditFields,
                        TagFields.Comment);
                    _originalKnownValues[index] = "";
                    if (_originalKnownValueSets is
                        not null)
                        _originalKnownValueSets[index] =
                            [];
                }
                finally
                {
                    _synchronizingMetadataProjection =
                        false;
                }
            }
        }
        if (_loadedProjectionFields?.Count == 0)
            _loadedProjectionFields = null;
    }

    public bool HasExactMetadataValue(
        MetadataFieldKey field) =>
        IsBrowseScalarField(field) ||
        IsFieldPending(field) ||
        _loadedProjectionFields?.Contains(
            field) == true;

    internal ImmutableArray<string>
        MetadataValuesForFilter(
            MetadataFieldKey field,
            ImmutableArray<string>
                projectedValues) =>
        IsFieldPending(field)
            ? CurrentValues(field)
            : projectedValues;

    private static bool IsBrowseScalarField(
        MetadataFieldKey field) =>
        field.KnownField is
            TagFields.Title or
            TagFields.Artist or
            TagFields.AlbumArtist or
            TagFields.Album or
            TagFields.Genre or
            TagFields.Composer or
            TagFields.Grouping or
            TagFields.Date or
            TagFields.TrackNumber or
            TagFields.TotalTracks or
            TagFields.DiscNumber or
            TagFields.TotalDiscs;

    public void RevertPendingChanges()
    {
        _suppressPendingNotification = true;
        _synchronizingMetadataProjection =
            true;
        try
        {
            _metadataValues?
                .RevertPendingChanges();
            for (int index = 0;
                 index < InlineEditFields.Length;
                 index++)
            {
                SetKnownValue(
                    InlineEditFields[index],
                    _originalKnownValues[index]);
                _metadataValues?.SetProjection(
                    MetadataFieldKey.Known(
                        InlineEditFields[index]),
                    LibraryMetadataValueMap
                        .FormatEditorValues(
                            OriginalKnownValueSet(
                                index)));
            }
        }
        finally
        {
            _synchronizingMetadataProjection =
                false;
            _suppressPendingNotification = false;
        }
        NotifyPendingChangesChanged();
    }

    public void AcceptPendingChanges()
    {
        AcceptAppliedEdits(
            CreatePendingEdits(),
            Record.Length,
            Record.LastWriteTime);
    }

    public void AcceptAppliedEdits(
        IReadOnlyList<MetadataValueEdit> appliedEdits,
        long? postLength,
        DateTime? postLastWriteTimeUtc)
    {
        AppliedEditNotification notification =
            AcceptAppliedEditsState(
                appliedEdits,
                postLength,
                postLastWriteTimeUtc);
        NotifyAppliedEdits(notification);
    }

    internal AppliedEditNotification
        AcceptAppliedEditsState(
            IReadOnlyList<MetadataValueEdit> appliedEdits,
            long? postLength,
            DateTime? postLastWriteTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(
            appliedEdits);
        var changedKnownFields =
            new HashSet<TagFields>();
        _suppressPendingNotification = true;
        try
        {
            foreach (MetadataValueEdit edit in
                     appliedEdits)
            {
                TagFields? changedField =
                    AcceptAppliedEditState(
                        edit);
                if (changedField is { } known)
                    changedKnownFields.Add(
                        known);
            }
            if (postLength is { } length)
                Record = Record with
                {
                    Length = length,
                };
            if (postLastWriteTimeUtc is
                { } lastWriteTimeUtc)
                Record = Record with
                {
                    LastWriteTime =
                        lastWriteTimeUtc,
                };
            Details = new(Record);
        }
        finally
        {
            _suppressPendingNotification = false;
        }
        if (HasChanges)
        {
            _pendingSourceLength =
                postLength;
            _pendingSourceLastWriteTimeUtc =
                postLastWriteTimeUtc;
            _pendingExpectedOriginalValues =
                CreatePendingEditStates()
                    .ToDictionary(
                        state =>
                            state.Edit.Field,
                        state =>
                            state.OriginalValues);
        }
        else
            ClearPendingSourceSnapshot();

        return new(
            [.. changedKnownFields]);
    }

    internal void NotifyAppliedEdits(
        AppliedEditNotification notification)
    {
        var notificationErrors =
            new List<Exception>();
        foreach (TagFields field in
                 notification.ChangedKnownFields)
            TryNotifyAppliedField(
                field,
                notificationErrors);
        TryNotify(
            () => _metadataValues?
                .NotifyAppliedValuesChanged(),
            notificationErrors);
        TryNotify(
            () => OnPropertyChanged(
                nameof(Details)),
            notificationErrors);
        TryNotify(
            () => OnPropertyChanged(
                nameof(SearchText)),
            notificationErrors);
        TryNotify(
            NotifyPendingChangesChanged,
            notificationErrors);
        if (notificationErrors.Count > 0)
            throw new AggregateException(
                notificationErrors);
    }

    internal readonly record struct
        AppliedEditNotification(
            ImmutableArray<TagFields>
                ChangedKnownFields);

    private LibraryMetadataValueMap
        EnsureMetadataValues()
    {
        if (_metadataValues is not null)
            return _metadataValues;
        _metadataValues = new(
            new Dictionary<
                string,
                ImmutableArray<string>>(
                StringComparer.OrdinalIgnoreCase));
        _metadataValues.SetEditReservation(
            _isEditReserved);
        _metadataValues.Changed +=
            OnMetadataValuesChanged;
        return _metadataValues;
    }

    private static LibraryMetadataValueMap?
        BuildMetadataValues(
            IReadOnlyDictionary<string, string[]> metadata)
    {
        var values = new Dictionary<
            string,
            ImmutableArray<string>>(
            StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string[] fieldValues) in metadata)
        {
            MetadataFieldKey? field = null;
            if (CachedMetadataKeys.TryGetCustomName(
                    key,
                    out string customName))
                field = MetadataFieldKey.Custom(customName);
            else if (Enum.TryParse(
                         key,
                         ignoreCase: true,
                         out TagFields known) &&
                     known != TagFields.NullField)
                field = MetadataFieldKey.Known(known);
            if (field is not null)
            {
                string valueKey =
                    MetadataGridValueKey.For(field);
                values[valueKey] =
                    [.. fieldValues];
            }
        }
        return values.Count == 0
            ? null
            : new(values);
    }

    private string? KnownValue(TagFields field) =>
        field switch
        {
            TagFields.Title => Title,
            TagFields.Artist => Artist,
            TagFields.AlbumArtist => AlbumArtist,
            TagFields.Album => Album,
            TagFields.Genre => Genre,
            TagFields.Composer => Composer,
            TagFields.Grouping => Grouping,
            TagFields.Date => Year,
            TagFields.TrackNumber => TrackEditValue,
            TagFields.TotalTracks =>
                TrackTotalEditValue,
            TagFields.DiscNumber => DiscEditValue,
            TagFields.TotalDiscs =>
                DiscTotalEditValue,
            TagFields.Comment => Comment,
            _ => null,
        };

    private void SetKnownValue(
        TagFields field,
        string? value)
    {
        switch (field)
        {
            case TagFields.Title:
                Title = value ?? "";
                break;
            case TagFields.Artist:
                Artist = value ?? "";
                break;
            case TagFields.AlbumArtist:
                AlbumArtist = value ?? "";
                break;
            case TagFields.Album:
                Album = value ?? "";
                break;
            case TagFields.Genre:
                Genre = value ?? "";
                break;
            case TagFields.Composer:
                Composer = value ?? "";
                break;
            case TagFields.Grouping:
                Grouping = value ?? "";
                break;
            case TagFields.Date:
                Year = value ?? "";
                break;
            case TagFields.TrackNumber:
                TrackEditValue = value;
                break;
            case TagFields.TotalTracks:
                TrackTotalEditValue = value;
                break;
            case TagFields.DiscNumber:
                DiscEditValue = value;
                break;
            case TagFields.TotalDiscs:
                DiscTotalEditValue = value;
                break;
            case TagFields.Comment:
                Comment = value ?? "";
                break;
        }
    }

    private void OnKnownValueChanged(
        TagFields field,
        string? value)
    {
        if (!_synchronizingMetadataProjection)
        {
            if (TryInlineEditIndex(
                    field,
                    out int index))
                _ = OriginalKnownValueSet(
                    index);
            _metadataValues?.SetProjection(
                MetadataFieldKey.Known(field),
                value ?? "");
        }
        NotifyPendingChangesChanged();
    }

    private void OnMetadataValuesChanged(
        object? sender,
        EventArgs e)
    {
        _synchronizingMetadataProjection = true;
        try
        {
            foreach (TagFields field in
                     InlineEditFields)
            {
                MetadataFieldKey key =
                    MetadataFieldKey.Known(field);
                if (_metadataValues?.IsPending(
                        key) != true)
                    continue;
                string? projected =
                    LibraryMetadataValueMap
                        .FormatEditorValues(
                            _metadataValues!
                                .CurrentValues(
                                    key));
                if (!StringComparer.Ordinal.Equals(
                        projected,
                        KnownValue(field)))
                    SetKnownValue(
                        field,
                        projected);
            }
        }
        finally
        {
            _synchronizingMetadataProjection = false;
        }
        NotifyPendingChangesChanged();
    }

    private void NotifyPendingChangesChanged()
    {
        if (_suppressPendingNotification)
            return;
        if (HasChanges)
            CapturePendingSourceSnapshot();
        else
            ClearPendingSourceSnapshot();
        PendingChangesVersion++;
        var notificationErrors =
            new List<Exception>();
        TryNotify(
            () => OnPropertyChanged(
                nameof(HasChanges)),
            notificationErrors);
        EventHandler? handlers =
            PendingChangesChanged;
        if (handlers is not null)
        {
            foreach (EventHandler handler in
                     handlers.GetInvocationList()
                         .Cast<EventHandler>())
                TryNotify(
                    () => handler(
                        this,
                        EventArgs.Empty),
                    notificationErrors);
        }
        if (notificationErrors.Count > 0)
            throw new AggregateException(
                notificationErrors);
    }

    private static bool IsInlineEditField(
        TagFields field) =>
        Array.IndexOf(
            InlineEditFields,
            field) >= 0;

    private bool HasKnownChanges()
    {
        for (int index = 0;
             index < InlineEditFields.Length;
             index++)
        {
            if (!StringComparer.Ordinal.Equals(
                    _originalKnownValues[index],
                    KnownValue(
                        InlineEditFields[index])))
                return true;
        }
        return false;
    }

    private TagFields? AcceptAppliedEditState(
        MetadataValueEdit edit)
    {
        ImmutableArray<string> currentValues =
            CurrentValues(edit.Field);
        bool preserveCurrent =
            IsFieldPending(edit.Field) &&
            !currentValues.SequenceEqual(
                edit.Values,
                StringComparer.Ordinal);
        if (edit.Field.KnownField is
            { } known &&
            TryInlineEditIndex(
                known,
                out int index))
        {
            SetOriginalKnownValueSet(
                index,
                edit.Values);
            _originalKnownValues[index] =
                LibraryMetadataValueMap
                    .FormatEditorValues(
                        edit.Values);
            if (!preserveCurrent)
                SetKnownValueSilently(
                    known,
                    _originalKnownValues[index]);
        }
        _metadataValues?.AcceptAppliedEdit(
            edit,
            preserveCurrent,
            notify: false);
        UpdateRecord(edit);
        return edit.Field.KnownField is
                { } changed &&
            IsInlineEditField(changed)
                ? changed
                : null;
    }

    // A durable commit must advance semantic state before invoking fallible
    // observers. Direct backing-field assignment is intentional here; normal
    // interactive edits continue to use the generated observable properties.
#pragma warning disable MVVMTK0034
    private void SetKnownValueSilently(
        TagFields field,
        string? value)
    {
        switch (field)
        {
            case TagFields.Title:
                _title = value ?? "";
                break;
            case TagFields.Artist:
                _artist = value ?? "";
                break;
            case TagFields.AlbumArtist:
                _albumArtist = value ?? "";
                break;
            case TagFields.Album:
                _album = value ?? "";
                break;
            case TagFields.Genre:
                _genre = value ?? "";
                break;
            case TagFields.Composer:
                _composer = value ?? "";
                break;
            case TagFields.Grouping:
                _grouping = value ?? "";
                break;
            case TagFields.Date:
                _year = value ?? "";
                break;
            case TagFields.TrackNumber:
                _trackEditValue = value;
                break;
            case TagFields.TotalTracks:
                _trackTotalEditValue = value;
                break;
            case TagFields.DiscNumber:
                _discEditValue = value;
                break;
            case TagFields.TotalDiscs:
                _discTotalEditValue = value;
                break;
            case TagFields.Comment:
                _comment = value ?? "";
                break;
        }
    }
#pragma warning restore MVVMTK0034

    private void TryNotifyAppliedField(
        TagFields field,
        ICollection<Exception> errors)
    {
        string propertyName = field switch
        {
            TagFields.Title => nameof(Title),
            TagFields.Artist => nameof(Artist),
            TagFields.AlbumArtist =>
                nameof(AlbumArtist),
            TagFields.Album => nameof(Album),
            TagFields.Genre => nameof(Genre),
            TagFields.Composer =>
                nameof(Composer),
            TagFields.Grouping =>
                nameof(Grouping),
            TagFields.Date => nameof(Year),
            TagFields.TrackNumber =>
                nameof(TrackEditValue),
            TagFields.TotalTracks =>
                nameof(TrackTotalEditValue),
            TagFields.DiscNumber =>
                nameof(DiscEditValue),
            TagFields.TotalDiscs =>
                nameof(DiscTotalEditValue),
            TagFields.Comment => nameof(Comment),
            _ => "",
        };
        if (propertyName.Length > 0)
            TryNotify(
                () => OnPropertyChanged(
                    propertyName),
                errors);
        if (field is TagFields.TrackNumber)
            TryNotify(
                () => OnPropertyChanged(
                    nameof(Track)),
                errors);
        else if (field is TagFields.TotalTracks)
            TryNotify(
                () => OnPropertyChanged(
                    nameof(TrackTotal)),
                errors);
        else if (field is TagFields.DiscNumber)
            TryNotify(
                () => OnPropertyChanged(
                    nameof(Disc)),
                errors);
        else if (field is TagFields.TotalDiscs)
            TryNotify(
                () => OnPropertyChanged(
                    nameof(DiscTotal)),
                errors);
    }

    private static void TryNotify(
        Action notification,
        ICollection<Exception> errors)
    {
        try
        {
            notification();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
    }

    private bool IsFieldPending(
        MetadataFieldKey field)
    {
        if (_metadataValues?.IsPending(
                field) == true)
            return true;
        return field.KnownField is
            { } known &&
            TryInlineEditIndex(
                known,
                out int index) &&
            !StringComparer.Ordinal.Equals(
                _originalKnownValues[index],
                KnownValue(known));
    }

    private ImmutableArray<string>
        CurrentValues(
            MetadataFieldKey field)
    {
        if (_metadataValues?.IsPending(
                field) == true)
            return _metadataValues.CurrentValues(
                field);
        if (field.KnownField is
            { } known &&
            IsInlineEditField(known))
        {
            string? value = KnownValue(known);
            return LibraryMetadataValueMap
                .ParseEditorValues(
                    value ?? "");
        }
        return _metadataValues?.CurrentValues(
                   field) ??
               [];
    }

    private static bool TryInlineEditIndex(
        TagFields field,
        out int index)
    {
        index = Array.IndexOf(
            InlineEditFields,
            field);
        return index >= 0;
    }

    private void UpdateRecord(
        MetadataValueEdit edit)
    {
        string? value =
            edit.Values.FirstOrDefault();
        if (edit.Field.KnownField is
            { } known)
        {
            Record = known switch
            {
                TagFields.Title =>
                    Record with
                    {
                        Title = value,
                    },
                TagFields.Artist =>
                    Record with
                    {
                        Artist = value,
                    },
                TagFields.AlbumArtist =>
                    Record with
                    {
                        AlbumArtist = value,
                    },
                TagFields.Album =>
                    Record with
                    {
                        Album = value,
                    },
                TagFields.Genre =>
                    Record with
                    {
                        Genre = value,
                    },
                TagFields.Composer =>
                    Record with
                    {
                        Composer = value,
                    },
                TagFields.Grouping =>
                    Record with
                    {
                        Grouping = value,
                    },
                TagFields.Date =>
                    Record with
                    {
                        ReleaseDate = value,
                        Year = ParseYear(value),
                    },
                TagFields.TrackNumber =>
                    Record with
                    {
                        TrackNumber =
                            ParseNumber(value),
                    },
                TagFields.TotalTracks =>
                    Record with
                    {
                        TrackTotal =
                            ParseNumber(value),
                    },
                TagFields.DiscNumber =>
                    Record with
                    {
                        DiscNumber =
                            ParseNumber(value),
                    },
                TagFields.TotalDiscs =>
                    Record with
                    {
                        DiscTotal =
                            ParseNumber(value),
                    },
                _ => Record,
            };
        }
        var metadata = Record.Metadata.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        string metadataKey =
            edit.Field.KnownField?.ToString() ??
            CachedMetadataKeys.Custom(
                edit.Field.CustomName!);
        if (edit.Values.IsEmpty)
            metadata.Remove(metadataKey);
        else
            metadata[metadataKey] =
                [.. edit.Values];
        Record = Record with
        {
            Metadata = metadata,
        };
    }

    private static int? ParseNumber(
        string? value) =>
        int.TryParse(
            value,
            out int number)
            ? number
            : null;

    private static int? ParseYear(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string prefix = value.Length >= 4
            ? value[..4]
            : value;
        return ParseNumber(prefix);
    }

    private ImmutableArray<string>
        OriginalKnownValues(
            TagFields field)
    {
        ImmutableArray<string> mapped =
            _metadataValues?.OriginalValues(
                MetadataFieldKey.Known(field)) ??
            [];
        if (!mapped.IsDefaultOrEmpty)
            return mapped;
        string? value =
            TryInlineEditIndex(
                field,
                out int index)
                ? _originalKnownValues[
                    index]
                : KnownValue(field);
        return string.IsNullOrWhiteSpace(value)
            ? []
            : [value];
    }

    private ImmutableArray<string>
        OriginalKnownValueSet(int index)
    {
        _originalKnownValueSets ??=
            new ImmutableArray<string>[
                InlineEditFields.Length];
        ImmutableArray<string> values =
            _originalKnownValueSets[index];
        if (!values.IsDefault)
            return values;
        values = OriginalKnownValues(
            InlineEditFields[index]);
        _originalKnownValueSets[index] =
            values;
        return values;
    }

    private void SetOriginalKnownValueSet(
        int index,
        ImmutableArray<string> values)
    {
        _originalKnownValueSets ??=
            new ImmutableArray<string>[
                InlineEditFields.Length];
        _originalKnownValueSets[index] =
            values;
    }

    private void CapturePendingSourceSnapshot()
    {
        if (_pendingSourceLength is not null &&
            _pendingSourceLastWriteTimeUtc is not
                null)
            return;
        _pendingSourceLength =
            Record.Length > 0
                ? Record.Length
                : null;
        _pendingSourceLastWriteTimeUtc =
            Record.LastWriteTime == default
                ? null
                : Record.LastWriteTime.Kind ==
                    DateTimeKind.Utc
                    ? Record.LastWriteTime
                    : Record.LastWriteTime
                        .ToUniversalTime();
    }

    private void ClearPendingSourceSnapshot()
    {
        _pendingSourceLength = null;
        _pendingSourceLastWriteTimeUtc = null;
        _pendingExpectedOriginalValues =
            null;
    }

    private void EnsureEditIsNotReserved()
    {
        if (_isEditReserved)
            throw new InvalidOperationException(
                LocalizedText.Get(
                    "Workbench.Status.PendingChangesBlocked"));
    }

    partial void OnTitleChanging(
        string value) =>
        EnsureEditIsNotReserved();
    partial void OnArtistChanging(
        string value) =>
        EnsureEditIsNotReserved();
    partial void OnAlbumArtistChanging(
        string value) =>
        EnsureEditIsNotReserved();
    partial void OnAlbumChanging(
        string value) =>
        EnsureEditIsNotReserved();
    partial void OnGenreChanging(
        string value) =>
        EnsureEditIsNotReserved();
    partial void OnComposerChanging(
        string value) =>
        EnsureEditIsNotReserved();
    partial void OnGroupingChanging(
        string value) =>
        EnsureEditIsNotReserved();
    partial void OnYearChanging(
        string value) =>
        EnsureEditIsNotReserved();
    partial void OnTrackEditValueChanging(
        string? value) =>
        EnsureEditIsNotReserved();
    partial void OnTrackTotalEditValueChanging(
        string? value) =>
        EnsureEditIsNotReserved();
    partial void OnDiscEditValueChanging(
        string? value) =>
        EnsureEditIsNotReserved();
    partial void OnDiscTotalEditValueChanging(
        string? value) =>
        EnsureEditIsNotReserved();
    partial void OnCommentChanging(
        string value) =>
        EnsureEditIsNotReserved();

    partial void OnTitleChanged(string value) =>
        OnKnownValueChanged(
            TagFields.Title,
            value);
    partial void OnArtistChanged(string value) =>
        OnKnownValueChanged(
            TagFields.Artist,
            value);
    partial void OnAlbumArtistChanged(string value) =>
        OnKnownValueChanged(
            TagFields.AlbumArtist,
            value);
    partial void OnAlbumChanged(string value) =>
        OnKnownValueChanged(
            TagFields.Album,
            value);
    partial void OnGenreChanged(string value) =>
        OnKnownValueChanged(
            TagFields.Genre,
            value);
    partial void OnComposerChanged(string value) =>
        OnKnownValueChanged(
            TagFields.Composer,
            value);
    partial void OnGroupingChanged(string value) =>
        OnKnownValueChanged(
            TagFields.Grouping,
            value);
    partial void OnYearChanged(string value) =>
        OnKnownValueChanged(
            TagFields.Date,
            value);
    partial void OnTrackEditValueChanged(
        string? value) =>
        OnKnownValueChanged(
            TagFields.TrackNumber,
            value);
    partial void OnTrackTotalEditValueChanged(
        string? value) =>
        OnKnownValueChanged(
            TagFields.TotalTracks,
            value);
    partial void OnDiscEditValueChanged(
        string? value) =>
        OnKnownValueChanged(
            TagFields.DiscNumber,
            value);
    partial void OnDiscTotalEditValueChanged(
        string? value) =>
        OnKnownValueChanged(
            TagFields.TotalDiscs,
            value);
    partial void OnCommentChanged(string value) =>
        OnKnownValueChanged(
            TagFields.Comment,
            value);
}

public sealed class LibraryMetadataValueMap :
    ObservableObject,
    IReadOnlyDictionary<string, string>
{
    private Dictionary<string, string>?
        _values;
    private Dictionary<
        string,
        ImmutableArray<string>>?
        _originalValues;
    private Dictionary<
        string,
        ImmutableArray<string>>?
        _editedOriginals;
    private HashSet<string>?
        _loadingFields;
    private HashSet<string>?
        _unavailableFields;
    private bool _suppressChanged;
    private bool _isEditReserved;

    public LibraryMetadataValueMap(
        IReadOnlyDictionary<
            string,
            ImmutableArray<string>> values)
    {
        if (values.Count > 0)
        {
            _originalValues = new(
                values,
                StringComparer.OrdinalIgnoreCase);
            _values = values.ToDictionary(
                pair => pair.Key,
                pair => Display(pair.Value),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public event EventHandler? Changed;

    public string this[string key]
    {
        get
        {
            if (_values?.TryGetValue(
                    key,
                    out string? value) == true)
                return value;
            if (_loadingFields?.Contains(
                    key) == true)
                return LocalizedText.Get(
                    "Library.Metadata.Loading");
            if (_unavailableFields?.Contains(
                    key) == true)
                return LocalizedText.Get(
                    "Library.Metadata.Unavailable");
            return "";
        }
        set
        {
            if (_isEditReserved)
                throw new InvalidOperationException(
                    LocalizedText.Get(
                        "Workbench.Status.PendingChangesBlocked"));
            string normalized = value ?? "";
            if (StringComparer.Ordinal.Equals(
                    this[key],
                    normalized))
                return;
            if (!TryParseField(
                    key,
                    out _))
                return;
            if (_editedOriginals?.TryGetValue(
                    key,
                    out ImmutableArray<string>
                        original) != true)
                original =
                    _originalValues?
                        .GetValueOrDefault(
                            key,
                            []) ?? [];
            (_editedOriginals ??= new(
                    StringComparer.OrdinalIgnoreCase))
                .TryAdd(
                key,
                original);
            EnsureValues()[key] = normalized;
            if (ParseValues(normalized).SequenceEqual(
                    original,
                    StringComparer.Ordinal))
                _editedOriginals.Remove(key);
            OnPropertyChanged("Item[]");
            NotifyChanged();
        }
    }

    public IEnumerable<string> Keys =>
        _values is null
            ? []
            : _values.Keys;
    public IEnumerable<string> Values =>
        _values is null
            ? []
            : _values.Values;
    public int Count => _values?.Count ?? 0;
    public bool HasChanges =>
        _editedOriginals?.Count > 0;

    public bool ContainsKey(string key) =>
        _values?.ContainsKey(key) == true;

    public bool TryGetValue(
        string key,
        out string value)
    {
        value = this[key];
        return _values?.ContainsKey(key) ==
            true;
    }

    public IEnumerator<KeyValuePair<string, string>>
        GetEnumerator() =>
        (_values ??
         EmptyValues).GetEnumerator();

    System.Collections.IEnumerator
        System.Collections.IEnumerable.GetEnumerator() =>
        GetEnumerator();

    internal void SetProjection(
        MetadataFieldKey field,
        string value)
    {
        string key =
            MetadataGridValueKey.For(field);
        EnsureValues()[key] = value;
        _editedOriginals?.Remove(key);
        OnPropertyChanged("Item[]");
    }

    internal void SetEditReservation(
        bool reserved) =>
        _isEditReserved = reserved;

    internal void ImportPendingEdit(
        LibraryPendingMetadataEdit state)
    {
        string key =
            MetadataGridValueKey.For(
                state.Edit.Field);
        EnsureOriginalValues()[key] =
            state.OriginalValues;
        EnsureValues()[key] =
            Display(state.Edit.Values);
        if (state.Edit.Values.SequenceEqual(
                state.OriginalValues,
                StringComparer.Ordinal))
            _editedOriginals?.Remove(key);
        else
            (_editedOriginals ??= new(
                StringComparer.OrdinalIgnoreCase))[
                key] = state.OriginalValues;
        OnPropertyChanged("Item[]");
    }

    internal void LoadProjection(
        MetadataFieldKey field,
        ImmutableArray<string> values)
    {
        string key =
            MetadataGridValueKey.For(field);
        if (_editedOriginals?.ContainsKey(
                key) == true)
            return;
        _loadingFields?.Remove(key);
        _unavailableFields?.Remove(key);
        EnsureOriginalValues()[key] =
            values;
        EnsureValues()[key] =
            Display(values);
        OnPropertyChanged("Item[]");
    }

    internal void ClearProjection(
        MetadataFieldKey field)
    {
        string key =
            MetadataGridValueKey.For(field);
        if (_editedOriginals?.ContainsKey(
                key) == true)
            return;
        _loadingFields?.Remove(key);
        _unavailableFields?.Remove(key);
        _originalValues?.Remove(key);
        _values?.Remove(key);
        OnPropertyChanged("Item[]");
    }

    internal void BeginProjection(
        MetadataFieldKey field)
    {
        string key =
            MetadataGridValueKey.For(field);
        if (_values?.ContainsKey(key) == true ||
            _editedOriginals?.ContainsKey(
                key) == true)
            return;
        _unavailableFields?.Remove(key);
        (_loadingFields ??= new(
            StringComparer.OrdinalIgnoreCase))
            .Add(key);
        OnPropertyChanged("Item[]");
    }

    internal void FailProjection(
        MetadataFieldKey field)
    {
        string key =
            MetadataGridValueKey.For(field);
        _loadingFields?.Remove(key);
        if (_editedOriginals?.ContainsKey(
                key) == true)
            return;
        (_unavailableFields ??= new(
            StringComparer.OrdinalIgnoreCase))
            .Add(key);
        OnPropertyChanged("Item[]");
    }

    internal bool IsPending(
        MetadataFieldKey field) =>
        _editedOriginals?.ContainsKey(
            MetadataGridValueKey.For(field)) ==
        true;

    internal ImmutableArray<string>
        CurrentValues(
            MetadataFieldKey field) =>
        ParseValues(
            this[MetadataGridValueKey.For(field)]);

    internal ImmutableArray<string>
        OriginalValues(
            MetadataFieldKey field) =>
        _originalValues?.GetValueOrDefault(
            MetadataGridValueKey.For(field),
            []) ?? [];

    internal IReadOnlyList<MetadataValueEdit>
        CreatePendingEdits() =>
        CreatePendingEditStates()
            .Select(state => state.Edit)
            .ToArray();

    internal IReadOnlyList<LibraryPendingMetadataEdit>
        CreatePendingEditStates() =>
        (_editedOriginals ??
         EmptyEditedOriginals)
            .Select(pair =>
            {
                string current =
                    _values?.GetValueOrDefault(
                        pair.Key) ??
                    "";
                if (!TryParseField(
                        pair.Key,
                        out MetadataFieldKey field))
                    throw new InvalidOperationException(
                        pair.Key);
                return new LibraryPendingMetadataEdit(
                    new(
                        field,
                        ParseValues(current)),
                    pair.Value);
            })
            .ToArray();

    internal IEnumerable<(
        MetadataFieldKey Field,
        string Before,
        string After)> CreatePendingRows() =>
        (_editedOriginals ??
         EmptyEditedOriginals).Select(pair =>
        {
            if (!TryParseField(
                    pair.Key,
                    out MetadataFieldKey field))
                throw new InvalidOperationException(
                    pair.Key);
            return (
                field,
                Display(pair.Value),
                _values?.GetValueOrDefault(
                    pair.Key) ??
                "");
        });

    internal void RevertPendingChanges()
    {
        if (_editedOriginals is not
            { Count: > 0 } edited)
            return;
        _suppressChanged = true;
        try
        {
            foreach ((string key,
                         ImmutableArray<string> value)
                     in edited)
                EnsureValues()[key] =
                    Display(value);
            edited.Clear();
            OnPropertyChanged("Item[]");
        }
        finally
        {
            _suppressChanged = false;
        }
    }

    internal void AcceptAppliedEdit(
        MetadataValueEdit edit,
        bool preserveCurrent,
        bool notify = true)
    {
        string key =
            MetadataGridValueKey.For(
                edit.Field);
        EnsureOriginalValues()[key] =
            edit.Values;
        if (preserveCurrent)
        {
            (_editedOriginals ??= new(
                    StringComparer.OrdinalIgnoreCase))[
                key] = edit.Values;
        }
        else
        {
            EnsureValues()[key] = Display(
                edit.Values);
            _editedOriginals?.Remove(key);
        }
        if (notify)
            OnPropertyChanged("Item[]");
    }

    internal void NotifyAppliedValuesChanged() =>
        OnPropertyChanged("Item[]");

    private void NotifyChanged()
    {
        if (!_suppressChanged)
            Changed?.Invoke(
                this,
                EventArgs.Empty);
    }

    private Dictionary<string, string>
        EnsureValues() =>
        _values ??= new(
            StringComparer.OrdinalIgnoreCase);

    private Dictionary<
        string,
        ImmutableArray<string>>
        EnsureOriginalValues() =>
        _originalValues ??= new(
            StringComparer.OrdinalIgnoreCase);

    internal static bool TryParseField(
        string valueKey,
        out MetadataFieldKey field)
    {
        field = null!;
        if (valueKey.StartsWith(
                "K_",
                StringComparison.OrdinalIgnoreCase) &&
            Enum.TryParse(
                valueKey[2..],
                ignoreCase: true,
                out TagFields known) &&
            known != TagFields.NullField)
        {
            field = MetadataFieldKey.Known(known);
            return true;
        }
        if (!valueKey.StartsWith(
                "C_",
                StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            string name = Encoding.UTF8.GetString(
                Convert.FromHexString(
                    valueKey[2..]));
            if (string.IsNullOrWhiteSpace(name))
                return false;
            field = MetadataFieldKey.Custom(name);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static ImmutableArray<string>
        ParseEditorValues(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];
        var values = new List<string>();
        var current = new StringBuilder();
        bool escaped = false;
        foreach (char character in value)
        {
            if (escaped)
            {
                current.Append(character);
                escaped = false;
                continue;
            }
            if (character == '\\')
            {
                escaped = true;
                continue;
            }
            if (character != ';')
            {
                current.Append(character);
                continue;
            }
            AddCurrent();
        }
        if (escaped)
            current.Append('\\');
        AddCurrent();
        return [.. values];

        void AddCurrent()
        {
            string item = current.ToString().Trim();
            current.Clear();
            if (item.Length > 0)
                values.Add(item);
        }
    }

    internal static string FormatEditorValues(
        ImmutableArray<string> values) =>
        string.Join(
            "; ",
            values.Select(value =>
                value.Replace(
                        "\\",
                        "\\\\",
                        StringComparison.Ordinal)
                    .Replace(
                        ";",
                        "\\;",
                        StringComparison.Ordinal)));

    private static ImmutableArray<string>
        ParseValues(string value) =>
        ParseEditorValues(value);

    private static string Display(
        ImmutableArray<string> values) =>
        FormatEditorValues(values);

    private static readonly IReadOnlyDictionary<
        string,
        string>
        EmptyValues =
        new Dictionary<string, string>();

    private static readonly IReadOnlyDictionary<
        string,
        ImmutableArray<string>>
        EmptyEditedOriginals =
        new Dictionary<
            string,
            ImmutableArray<string>>();
}

public partial class EditableTagField : ObservableObject
{
    private string? _loadedValue;
    private bool _loadedMixed;
    private bool _isEditReserved;
    private bool _bypassEditReservation;
    private Func<string, string> _getText =
        LocalizedText.Get;

    public EditableTagField(
        TagFields field,
        string label,
        string? labelResourceKey = null)
    {
        Field = field;
        _label = label;
        LabelResourceKey = labelResourceKey;
    }

    public TagFields Field { get; }
    public string? LabelResourceKey { get; }

    [ObservableProperty]
    private string _label;

    public string OriginalDisplayValue =>
        _loadedMixed
            ? _getText(
                "Inspector.Field.MixedValues")
            : _loadedValue ?? "";

    [ObservableProperty]
    private string? _value;

    [ObservableProperty]
    private bool _isMixed;

    [ObservableProperty]
    private bool _isModified;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnverified))]
    [NotifyPropertyChangedFor(nameof(PlaceholderText))]
    [NotifyPropertyChangedFor(nameof(VerificationMessage))]
    private FieldValueVerification _verification = FieldValueVerification.Exact;

    public bool IsUnverified => Verification == FieldValueVerification.Unverified;
    public string PlaceholderText => IsUnverified
        ? _getText(
            "Inspector.Field.UnverifiedPlaceholder")
        : IsMixed
            ? _getText(
                "Inspector.Field.MixedPlaceholder")
            : _getText(
                "Inspector.Field.NoValuePlaceholder");
    public string? VerificationMessage => IsUnverified
        ? _getText(
            "Inspector.Field.UnverifiedHelp")
        : null;

    public void RefreshLocalizedText(
        Func<string, string> getText)
    {
        ArgumentNullException.ThrowIfNull(getText);
        _getText = getText;
        if (LabelResourceKey is not null)
            Label = getText(LabelResourceKey);
        OnPropertyChanged(
            nameof(OriginalDisplayValue));
        OnPropertyChanged(
            nameof(PlaceholderText));
        OnPropertyChanged(
            nameof(VerificationMessage));
    }

    public void SetLoaded(string? value, bool mixed)
        => SetLoaded(string.IsNullOrEmpty(value) ? [] : [value], mixed, FieldValueVerification.Exact);

    public void SetLoaded(
        IReadOnlyList<string> values,
        bool mixed,
        FieldValueVerification verification = FieldValueVerification.Exact)
    {
        ImmutableArray<string> distinctValues =
        [
            .. values
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal),
        ];
        _bypassEditReservation = true;
        try
        {
            Verification = verification;
            Value = verification ==
                        FieldValueVerification.Exact &&
                    !mixed
                ? distinctValues.Length == 0
                    ? null
                    : LibraryMetadataValueMap
                        .FormatEditorValues(
                            distinctValues)
                : null;
            IsMixed = mixed ||
                verification ==
                FieldValueVerification.Unverified;
            _loadedValue = Value;
            _loadedMixed = IsMixed;
            IsModified = false;
            OnPropertyChanged(
                nameof(PlaceholderText));
        }
        finally
        {
            _bypassEditReservation = false;
        }
    }

    /// <summary>
    /// Advances the loaded baseline to values that were durably applied while
    /// preserving any newer value entered after the reviewed operation began.
    /// </summary>
    public void AcceptAppliedValues(
        IReadOnlyList<string> values)
    {
        AcceptAppliedValuesState(values);
        NotifyAppliedValuesAccepted();
    }

    internal void AcceptAppliedValuesState(
        IReadOnlyList<string> values)
    {
        ImmutableArray<string> appliedValues =
        [
            .. values
                .Where(value =>
                    !string.IsNullOrEmpty(value))
                .Distinct(StringComparer.Ordinal),
        ];
        string? currentValue = Value;

        _loadedValue = appliedValues.Length == 0
            ? null
            : LibraryMetadataValueMap
                .FormatEditorValues(
                    appliedValues);
        _loadedMixed = false;
#pragma warning disable MVVMTK0034
        _verification =
            FieldValueVerification.Exact;
        _isMixed = false;
        _isModified = !LibraryMetadataValueMap
            .ParseEditorValues(
                currentValue ?? "")
            .SequenceEqual(
                appliedValues,
                StringComparer.Ordinal);
#pragma warning restore MVVMTK0034
    }

    internal void NotifyAppliedValuesAccepted()
    {
        var errors = new List<Exception>();
        TryNotify(
            () => OnPropertyChanged(
                nameof(Verification)),
            errors);
        TryNotify(
            () => OnPropertyChanged(
                nameof(IsUnverified)),
            errors);
        TryNotify(
            () => OnPropertyChanged(
                nameof(IsMixed)),
            errors);
        TryNotify(
            () => OnPropertyChanged(
                nameof(IsModified)),
            errors);
        TryNotify(
            () => OnPropertyChanged(
                nameof(OriginalDisplayValue)),
            errors);
        TryNotify(
            () => OnPropertyChanged(
                nameof(PlaceholderText)),
            errors);
        TryNotify(
            () => OnPropertyChanged(
                nameof(VerificationMessage)),
            errors);
        if (errors.Count > 0)
            throw new AggregateException(errors);
    }

    internal void SetEditReservation(
        bool reserved) =>
        _isEditReserved = reserved;

    private static void TryNotify(
        Action notification,
        ICollection<Exception> errors)
    {
        try
        {
            notification();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
    }

    private void EnsureEditIsNotReserved()
    {
        if (_isEditReserved &&
            !_bypassEditReservation)
            throw new InvalidOperationException(
                LocalizedText.Get(
                    "Workbench.Status.PendingChangesBlocked"));
    }

    partial void OnValueChanging(
        string? value) =>
        EnsureEditIsNotReserved();

    partial void OnIsMixedChanged(bool value) => OnPropertyChanged(nameof(PlaceholderText));

    partial void OnValueChanged(string? value)
    {
        IsModified = _loadedMixed
            ? value is not null
            : !LibraryMetadataValueMap
                .ParseEditorValues(
                    value ?? "")
                .SequenceEqual(
                    LibraryMetadataValueMap
                        .ParseEditorValues(
                            _loadedValue ?? ""),
                    StringComparer.Ordinal);
    }
}

public partial class ArtworkPreviewItem : ObservableObject
{
    private string _technicalSummary;
    private Func<ID3v2Util.APICType, string>
        _formatType = FormatType;
    private bool _isEditReserved;

    public ArtworkPreviewItem(
        object? source,
        ID3v2Util.APICType type,
        string mimeType,
        byte[] data,
        string technicalSummary,
        string? description)
    {
        Source = source;
        _type = type;
        MimeType = mimeType;
        Data = data;
        _technicalSummary = technicalSummary;
        _description = description;
    }

    public object? Source { get; private set; }
    public string MimeType { get; private set; }
    public byte[] Data { get; private set; }
    public string Label => _formatType(Type);
    public string Summary => string.IsNullOrWhiteSpace(Description)
        ? _technicalSummary
        : $"{Description} · {_technicalSummary}";
    public bool IsModified { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private ID3v2Util.APICType _type;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string? _description;

    partial void OnTypeChanged(ID3v2Util.APICType value) => MarkModified();
    partial void OnDescriptionChanged(string? value) => MarkModified();

    public void AcceptChanges()
    {
        if (!AcceptChangesState())
            return;
        NotifyChangesAccepted();
    }

    internal bool AcceptChangesState()
    {
        if (!IsModified)
            return false;
        IsModified = false;
        return true;
    }

    internal void NotifyChangesAccepted() =>
        OnPropertyChanged(nameof(IsModified));

    internal void SetEditReservation(
        bool reserved) =>
        _isEditReserved = reserved;

    public void ReplaceContent(
        object? source,
        string mimeType,
        byte[] data,
        string technicalSummary)
    {
        EnsureEditIsNotReserved();
        Source = source;
        MimeType = mimeType;
        Data = data;
        _technicalSummary = technicalSummary;
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(MimeType));
        OnPropertyChanged(nameof(Data));
        OnPropertyChanged(nameof(Summary));
        MarkModified();
    }

    public void RefreshLocalizedText(
        Func<ID3v2Util.APICType, string>
            formatType)
    {
        ArgumentNullException.ThrowIfNull(formatType);
        _formatType = formatType;
        OnPropertyChanged(nameof(Label));
    }

    public void RefreshTechnicalSummary(
        string technicalSummary)
    {
        ArgumentNullException.ThrowIfNull(
            technicalSummary);
        if (string.Equals(
                _technicalSummary,
                technicalSummary,
                StringComparison.Ordinal))
            return;
        _technicalSummary = technicalSummary;
        OnPropertyChanged(nameof(Summary));
    }

    private void MarkModified()
    {
        if (IsModified)
            return;
        IsModified = true;
        OnPropertyChanged(nameof(IsModified));
    }

    private void EnsureEditIsNotReserved()
    {
        if (_isEditReserved)
            throw new InvalidOperationException(
                LocalizedText.Get(
                    "Workbench.Status.PendingChangesBlocked"));
    }

    partial void OnTypeChanging(
        ID3v2Util.APICType value) =>
        EnsureEditIsNotReserved();
    partial void OnDescriptionChanging(
        string? value) =>
        EnsureEditIsNotReserved();

    private static string FormatType(ID3v2Util.APICType value)
    {
        string text = value.ToString();
        return string.Concat(text.Select((character, index) =>
            index > 0 && char.IsUpper(character) && char.IsLower(text[index - 1])
                ? $" {char.ToLowerInvariant(character)}"
                : character.ToString()));
    }
}

public partial class IndexTargetEditorRow : ObservableObject
{
    private bool _refreshingProfileChoices;
    private Func<LibraryRootPermissions, string>? _permissionSummaryFormatter;
    private Func<string, string> _getText =
        LocalizedText.Get;
    private Func<string, object?[], string> _formatText =
        LocalizedText.Format;
    [ObservableProperty]
    private Guid _id = Guid.NewGuid();

    [ObservableProperty]
    private string _path = "";

    [ObservableProperty]
    private string? _profileId;

    [ObservableProperty]
    private SettingsProfileChoice? _profileChoice;

    public ObservableCollection<SettingsProfileChoice> ProfileChoices { get; } = [];

    [ObservableProperty]
    private string? _filter;

    [ObservableProperty]
    private string? _indexFormats;

    [ObservableProperty]
    private string? _indexIncludePatterns;

    [ObservableProperty]
    private string? _indexExcludePatterns;

    private LibraryRootPermissions _permissions;

    public LibraryRootPermissions Permissions
    {
        get => _permissions;
        set
        {
            if (!SetProperty(ref _permissions, value))
                return;
            OnPropertyChanged(nameof(AllowMetadataWrites));
            OnPropertyChanged(nameof(AllowArtworkWrites));
            OnPropertyChanged(nameof(AllowOrganization));
            OnPropertyChanged(nameof(AllowIngestOutput));
            OnPropertyChanged(nameof(AllowSynchronizationOutput));
            OnPropertyChanged(nameof(IsReadOnly));
            OnPropertyChanged(nameof(PermissionSummary));
        }
    }

    public bool AllowMetadataWrites
    {
        get => HasPermission(LibraryRootPermissions.WriteMetadata);
        set => SetPermission(LibraryRootPermissions.WriteMetadata, value);
    }

    public bool AllowArtworkWrites
    {
        get => HasPermission(LibraryRootPermissions.WriteArtwork);
        set => SetPermission(LibraryRootPermissions.WriteArtwork, value);
    }

    public bool AllowOrganization
    {
        get => HasPermission(LibraryRootPermissions.OrganizeFiles);
        set => SetPermission(LibraryRootPermissions.OrganizeFiles, value);
    }

    public bool AllowIngestOutput
    {
        get => HasPermission(LibraryRootPermissions.IngestOutput);
        set => SetPermission(LibraryRootPermissions.IngestOutput, value);
    }

    public bool AllowSynchronizationOutput
    {
        get => HasPermission(LibraryRootPermissions.SynchronizeOutput);
        set => SetPermission(LibraryRootPermissions.SynchronizeOutput, value);
    }

    public bool IsReadOnly => Permissions == LibraryRootPermissions.None;

    public string PermissionSummary =>
        _permissionSummaryFormatter?.Invoke(Permissions) ??
        (IsReadOnly
            ? _getText(
                "Settings.Permissions.Summary.ReadOnly")
            : _formatText(
                "Settings.Permissions.Summary.Allowed",
                [string.Join(", ", PermissionLabels())]));

    public void SetPermissionSummaryFormatter(
        Func<LibraryRootPermissions, string> formatter)
    {
        _permissionSummaryFormatter = formatter;
        OnPropertyChanged(nameof(PermissionSummary));
    }

    public void RefreshPermissionSummary() =>
        OnPropertyChanged(nameof(PermissionSummary));

    public void RefreshLocalizedText(
        ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(
            localization);
        _getText = localization.Get;
        _formatText = localization.Format;
        RefreshPermissionSummary();
    }

    [ObservableProperty]
    private bool _isSyncTarget;

    public ObservableCollection<IndexTargetSetEditorRow> Memberships { get; } = [];

    public IndexTargetEntry? Source { get; set; }

    partial void OnIsSyncTargetChanged(bool value)
    {
        if (value)
            AllowSynchronizationOutput = true;
    }

    partial void OnProfileChoiceChanged(SettingsProfileChoice? value)
    {
        if (!_refreshingProfileChoices && value is not null)
            ProfileId = value.Id;
    }

    partial void OnProfileIdChanged(string? value)
    {
        if (_refreshingProfileChoices)
            return;
        _refreshingProfileChoices = true;
        try
        {
            ProfileChoice = ProfileChoices.FirstOrDefault(choice => string.Equals(
                choice.Id, value, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _refreshingProfileChoices = false;
        }
    }

    public void RefreshProfileChoices(IEnumerable<LibraryProfile> profiles)
    {
        _refreshingProfileChoices = true;
        try
        {
            ProfileChoices.Clear();
            foreach (LibraryProfile profile in profiles)
                ProfileChoices.Add(new(profile.Id, profile.Name));
            ProfileChoice = ProfileChoices.FirstOrDefault(choice => string.Equals(
                choice.Id, ProfileId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _refreshingProfileChoices = false;
        }
    }

    private bool HasPermission(LibraryRootPermissions permission) =>
        Permissions.HasFlag(permission);

    private void SetPermission(LibraryRootPermissions permission, bool enabled) =>
        Permissions = enabled ? Permissions | permission : Permissions & ~permission;

    private IEnumerable<string> PermissionLabels()
    {
        if (AllowMetadataWrites)
            yield return _getText(
                "Settings.Permissions.Metadata");
        if (AllowArtworkWrites)
            yield return _getText(
                "Settings.Permissions.Artwork");
        if (AllowOrganization)
            yield return _getText(
                "Settings.Permissions.Organization");
        if (AllowIngestOutput)
            yield return _getText(
                "Settings.Permissions.IngestOutput");
        if (AllowSynchronizationOutput)
            yield return _getText(
                "Settings.Permissions.SyncOutput");
    }
}

public partial class IndexTargetSetEditorRow : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string? _offset;
}

public partial class SyncPlaylistEditorRow : ObservableObject
{
    [ObservableProperty]
    private string _name = "";
}

public partial class PlaylistTargetEditorRow : ObservableObject
{
    [ObservableProperty]
    private string _target = "";

    [ObservableProperty]
    private string _type = "m3u";

    [ObservableProperty]
    private string? _sets;

    [ObservableProperty] private string _pathStyle = "legacy";
    [ObservableProperty] private string _encoding = "utf-8";
    [ObservableProperty] private bool _emitByteOrderMark = true;
    [ObservableProperty] private string _lineEnding = "platform";
    [ObservableProperty] private bool _includeExtendedInfo = true;
    [ObservableProperty] private string _fileNameTransform = "legacy";
    [ObservableProperty] private int _maxTrackCount = 500;
    [ObservableProperty] private LibraryPathCollisionPolicy _collisionPolicy =
        LibraryPathCollisionPolicy.Stop;

    public PlaylistTargetEntry? Source { get; set; }
}

public partial class PlaylistSourceEditorRow : ObservableObject
{
    [ObservableProperty] private string _location = "";
    [ObservableProperty] private string _type = "m3u";
    [ObservableProperty] private bool _recursive;

    public PlaylistSourceEntry? Source { get; set; }
}

public partial class MetadataFieldMappingEditorRow : ObservableObject
{
    [ObservableProperty] private MediaFormatFamily _format;
    [ObservableProperty] private TagFields _field = TagFields.Title;
    [ObservableProperty] private string _nativeFieldName = "";

    public MetadataFieldMapping Build() =>
        new(Format, Field, NativeFieldName);

    public static MetadataFieldMappingEditorRow From(
        MetadataFieldMapping mapping) =>
        new()
        {
            Format = mapping.Format,
            Field = mapping.Field,
            NativeFieldName = mapping.NativeFieldName,
        };
}

public partial class ExportProfileEditorRow : ObservableObject
{
    public required LibraryExportProfile Source { get; init; }
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private ExportSelectionKind _selectionKind;
    [ObservableProperty] private string? _selectionValues;
    [ObservableProperty] private string? _selectionQuery;
    [ObservableProperty] private ExportTransformMode _transformMode;
    [ObservableProperty] private string? _transformRecipeId;
    [ObservableProperty] private string? _transformProviderId;
    [ObservableProperty] private string? _codec;
    [ObservableProperty] private string? _container;
    [ObservableProperty] private string? _namingProfileId;
    [ObservableProperty] private bool _preserveSourceLayout;
    [ObservableProperty] private string? _folderTemplate;
    [ObservableProperty] private string? _fileNameTemplate;
    [ObservableProperty] private bool _useNamingProfileCollision = true;
    [ObservableProperty] private LibraryPathCollisionPolicy _collisionPolicy;
    [ObservableProperty] private ExportArtworkMode _artworkMode;
    [ObservableProperty] private bool _frontCoverOnly;
    [ObservableProperty] private bool _preserveArtworkEncoding;
    [ObservableProperty] private int? _artworkMaximumDimension;
    [ObservableProperty] private int? _artworkMaximumBytes;
    [ObservableProperty] private bool _playlistEnabled;
    [ObservableProperty] private string _playlistFormat = "m3u8";
    [ObservableProperty] private bool _playlistRelativePaths = true;
    [ObservableProperty] private bool _playlistIncludeExtendedInfo = true;
    [ObservableProperty] private string _playlistEncoding = "utf-8";
    [ObservableProperty] private bool _playlistWriteBom;
    [ObservableProperty] private string _playlistLineEnding = "platform";
    [ObservableProperty] private int? _playlistMaximumTracks;
    [ObservableProperty] private string _transportProviderId = "local-filesystem";
    [ObservableProperty] private string _transportDestination = "";
    [ObservableProperty] private string? _transportOptions;
    [ObservableProperty] private ExportExtraFileDisposition _extraFileDisposition;
    [ObservableProperty] private bool _replaceChangedFiles;
    [ObservableProperty] private bool _removeEmptyDirectories;
    [ObservableProperty] private int? _maximumRemovals;

    public static ExportProfileEditorRow From(LibraryExportProfile profile) => new()
    {
        Source = profile,
        Id = profile.Id,
        Name = profile.Name,
        Enabled = profile.Enabled,
        SelectionKind = profile.Selection.Kind,
        SelectionValues = string.Join(", ", profile.Selection.Values),
        SelectionQuery = profile.Selection.Query,
        TransformMode = profile.Transform.Mode,
        TransformRecipeId = profile.Transform.RecipeId,
        TransformProviderId = profile.Transform.ProviderId,
        Codec = profile.Transform.Codec,
        Container = profile.Transform.Container,
        NamingProfileId = profile.Naming.LibraryProfileId,
        PreserveSourceLayout = profile.Naming.PreserveSourceLayout,
        FolderTemplate = profile.Naming.FolderTemplate,
        FileNameTemplate = profile.Naming.FileNameTemplate,
        UseNamingProfileCollision = profile.Naming.CollisionPolicy is null,
        CollisionPolicy = profile.Naming.CollisionPolicy ??
            LibraryPathCollisionPolicy.Stop,
        ArtworkMode = profile.Artwork.Mode,
        FrontCoverOnly = profile.Artwork.FrontCoverOnly,
        PreserveArtworkEncoding = profile.Artwork.PreserveEncoding,
        ArtworkMaximumDimension = profile.Artwork.MaximumDimension,
        ArtworkMaximumBytes = profile.Artwork.MaximumBytes,
        PlaylistEnabled = profile.Playlists.Enabled,
        PlaylistFormat = profile.Playlists.Format,
        PlaylistRelativePaths = profile.Playlists.RelativePaths,
        PlaylistIncludeExtendedInfo = profile.Playlists.IncludeExtendedInfo,
        PlaylistEncoding = profile.Playlists.EncodingName,
        PlaylistWriteBom = profile.Playlists.WriteByteOrderMark,
        PlaylistLineEnding = profile.Playlists.LineEnding,
        PlaylistMaximumTracks = profile.Playlists.MaximumTracks,
        TransportProviderId = profile.Transport.ProviderId,
        TransportDestination = profile.Transport.Destination,
        TransportOptions = string.Join("; ", profile.Transport.Options.Select(pair =>
            $"{pair.Key}={pair.Value}")),
        ExtraFileDisposition = profile.Reconciliation.ExtraFiles,
        ReplaceChangedFiles = profile.Reconciliation.ReplaceChangedFiles,
        RemoveEmptyDirectories = profile.Reconciliation.RemoveEmptyDirectories,
        MaximumRemovals = profile.Reconciliation.MaximumRemovals,
    };

    public static ExportProfileEditorRow Create() => From(new(
        "export-" + Guid.NewGuid().ToString("N")[..12],
        LocalizedText.Get(
            "Settings.ExportProfile.NewName"),
        false,
        ExportSelectionPolicy.EntireLibrary,
        new(),
        new(PreserveSourceLayout: true),
        new(),
        new(),
        new("local-filesystem", ""),
        new()));

    public LibraryExportProfile Build() => Source with
    {
        Id = Id.Trim(),
        Name = Name.Trim(),
        Enabled = Enabled,
        Selection = new(SelectionKind, SplitValues(SelectionValues), Clean(SelectionQuery)),
        Transform = new(TransformMode, Clean(TransformRecipeId),
            Clean(TransformProviderId), Clean(Codec), Clean(Container)),
        Naming = new(Clean(NamingProfileId), PreserveSourceLayout,
            Clean(FolderTemplate), Clean(FileNameTemplate),
            UseNamingProfileCollision ? null : CollisionPolicy),
        Artwork = new(ArtworkMode, FrontCoverOnly, PreserveArtworkEncoding,
            ArtworkMaximumDimension, ArtworkMaximumBytes),
        Playlists = new(PlaylistEnabled, PlaylistFormat.Trim(), PlaylistRelativePaths,
            PlaylistIncludeExtendedInfo, PlaylistEncoding.Trim(), PlaylistWriteBom,
            PlaylistLineEnding.Trim(), PlaylistMaximumTracks),
        Transport = new(TransportProviderId.Trim(), TransportDestination.Trim(),
            ParseOptions(TransportOptions)),
        Reconciliation = new(ExtraFileDisposition, ReplaceChangedFiles,
            RemoveEmptyDirectories, MaximumRemovals),
    };

    private static ImmutableArray<string> SplitValues(string? values) =>
        (values ?? "").Split([',', ';', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

    private static ImmutableDictionary<string, string> ParseOptions(string? options)
    {
        var result = ImmutableDictionary.CreateBuilder<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (string item in (options ?? "").Split([';', '\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = item.IndexOf('=');
            if (separator <= 0)
                throw new InvalidDataException(
                    $"Export transport option '{item}' must use name=value syntax.");
            result.Add(item[..separator].Trim(), item[(separator + 1)..].Trim());
        }
        return result.ToImmutable();
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public partial class HealthRuleEditorRow : ObservableObject
{
    public required string Id { get; init; }

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private LibraryHealthSeverity _severity;
    [ObservableProperty] private bool _proposeRepair;
    [ObservableProperty] private bool _applyRepair;

    public LibraryHealthRulePolicy Build() => new(
        Id, Enabled, Severity, ProposeRepair, ApplyRepair);
}

public sealed record SettingsRootChoice(Guid? Id, string Label)
{
    public override string ToString() => Label;
}

public sealed record SettingsProfileChoice(string Id, string Label)
{
    public override string ToString() => Label;
}

public partial class IngestRecipeEditorRow : ObservableObject
{
    private Func<string, string> _getText =
        LocalizedText.Get;
    private Func<string, object?[], string> _formatText =
        LocalizedText.Format;

    public required LibraryIngestRecipe Source { get; init; }
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _enabled;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _inputExtensions = "";
    [ObservableProperty] private bool? _requireLossless;
    [ObservableProperty] private int? _minimumSampleRateHz;
    [ObservableProperty] private int? _minimumBitsPerSample;
    [ObservableProperty] private SettingsChannelChoice _inputChannelChoice =
        SettingsChoiceLists.ChannelChoices[0];
    [ObservableProperty] private bool _matchAnyQualityMinimum;
    [ObservableProperty] private LibraryIngestAlbumCondition _albumCondition;
    [ObservableProperty] private LibraryIngestSourceSelection _sourceSelection;
    [ObservableProperty] private bool _requireFallbackApproval;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    [NotifyPropertyChangedFor(nameof(IsTranscode))]
    private LibraryIngestAction _action;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private Guid? _destinationRootId;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private SettingsRootChoice? _destinationRootChoice;
    [ObservableProperty] private string? _outputExtension;
    [ObservableProperty] private string? _codec;
    [ObservableProperty] private string? _encoder;
    [ObservableProperty] private string? _extraFfmpegOptions;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private bool _addToMediaCatalog;
    [ObservableProperty] private int? _bitrateKbps;
    [ObservableProperty] private int? _sampleRateHz;
    [ObservableProperty] private int? _bitsPerSample;
    [ObservableProperty] private string? _transcodeFormatId;
    [ObservableProperty] private string _transcodeEncoderId =
        AudioTranscodeEncoderIds.Automatic;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBitrateControl))]
    [NotifyPropertyChangedFor(nameof(HasQualityControl))]
    private AudioTranscodeRateMode _transcodeRateMode =
        AudioTranscodeRateMode.Lossless;
    [ObservableProperty] private double? _transcodeQuality;
    [ObservableProperty] private int _transcodeCompressionEffort = 5;
    [ObservableProperty] private bool _transcodeCreateCorrectionFile;
    [ObservableProperty] private SettingsChannelChoice _outputChannelChoice =
        SettingsChoiceLists.ChannelChoices[0];
    [ObservableProperty] private bool _preserveMetadata = true;
    [ObservableProperty] private bool _preserveArtwork = true;
    [ObservableProperty] private bool _useProfileCollision = true;
    [ObservableProperty] private LibraryPathCollisionPolicy _collisionPolicy;
    private bool _refreshingDestinationRoots;
    private AudioTranscodeCapabilitySnapshot? _transcodeCapabilities;
    private bool _refreshingTranscodeChoices;

    public ObservableCollection<SettingsRootChoice> DestinationRootChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<string>> TranscodeFormatChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<string>> TranscodeEncoderChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<AudioTranscodeRateMode>>
        TranscodeRateModeChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<int?>> TranscodeSampleRateChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<int?>> TranscodeBitDepthChoices { get; } = [];

    public bool IsTranscode => Action == LibraryIngestAction.Transcode;
    public bool HasBitrateControl => TranscodeRateMode is
        AudioTranscodeRateMode.ConstantBitrate or
        AudioTranscodeRateMode.AverageBitrate or
        AudioTranscodeRateMode.ConstrainedVariableBitrate or
        AudioTranscodeRateMode.HybridBitrate;
    public bool HasQualityControl => TranscodeRateMode is
        AudioTranscodeRateMode.VariableQuality or
        AudioTranscodeRateMode.HybridQuality;
    public bool IsTranscodeCorrectionFileSupported
    {
        get
        {
            AudioEncoderDescriptor? encoder =
                ResolveSelectedTranscodeEncoder();
            return encoder?
                       .SupportsCorrectionFile ==
                   true &&
                   encoder.RateControls.Any(
                       control =>
                           control.Mode ==
                               TranscodeRateMode &&
                           control
                               .SupportsCorrectionFile);
        }
    }
    public bool IsTranscodeCorrectionFileOptionVisible =>
        IsTranscodeCorrectionFileSupported ||
        TranscodeCreateCorrectionFile;
    public bool CanEditTranscodeCorrectionFile =>
        IsTranscodeCorrectionFileSupported ||
        TranscodeCreateCorrectionFile;
    public string TranscodeCorrectionFileHelpText =>
        _getText(
            IsTranscodeCorrectionFileSupported
                ? "Transcode.Correction.Help"
                : "Transcode.Issue.CorrectionUnavailable");

    public LocalizedChoice<string>? SelectedTranscodeFormatChoice
    {
        get => TranscodeFormatChoices.FirstOrDefault(choice =>
            choice.Value == TranscodeFormatId);
        set
        {
            if (value is not null)
                TranscodeFormatId = value.Value;
        }
    }

    public LocalizedChoice<string>? SelectedTranscodeEncoderChoice
    {
        get => TranscodeEncoderChoices.FirstOrDefault(choice =>
            choice.Value == TranscodeEncoderId);
        set
        {
            if (value is not null)
                TranscodeEncoderId = value.Value;
        }
    }

    public LocalizedChoice<AudioTranscodeRateMode>? SelectedTranscodeRateModeChoice
    {
        get => TranscodeRateModeChoices.FirstOrDefault(choice =>
            choice.Value == TranscodeRateMode);
        set
        {
            if (value is not null)
                TranscodeRateMode = value.Value;
        }
    }

    public LocalizedChoice<int?>? SelectedTranscodeSampleRateChoice
    {
        get => TranscodeSampleRateChoices.FirstOrDefault(choice =>
            choice.Value == SampleRateHz);
        set
        {
            if (value is not null)
                SampleRateHz = value.Value;
        }
    }

    public LocalizedChoice<int?>? SelectedTranscodeBitDepthChoice
    {
        get => TranscodeBitDepthChoices.FirstOrDefault(choice =>
            choice.Value == BitsPerSample);
        set
        {
            if (value is not null)
                BitsPerSample = value.Value;
        }
    }

    public string Summary => _formatText(
        "Settings.IngestRecipe.Summary",
        [
            _getText(
                $"Settings.Choice.LibraryIngestAction.{Action}"),
            InputExtensions,
            DestinationRootId is not null
                ? DestinationRootChoice?.Label ??
                  DestinationRootId.Value.ToString("D")
                : AddToMediaCatalog
                    ? _getText(
                        "Settings.IngestRecipe.Destination.ConfiguredMediaCatalog")
                    : _getText(
                        "Settings.IngestRecipe.Destination.None"),
        ]);

    public void RefreshLocalizedText(
        ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(
            localization);
        _getText = localization.Get;
        _formatText = localization.Format;
        RefreshTranscodeChoices();
        OnPropertyChanged(nameof(Summary));
    }

    public static IngestRecipeEditorRow From(LibraryIngestRecipe recipe) => new()
    {
        Source = recipe,
        Id = recipe.Id,
        Name = recipe.Name,
        Enabled = recipe.Enabled,
        InputExtensions = string.Join(", ", recipe.InputExtensions),
        RequireLossless = recipe.RequireLossless,
        MinimumSampleRateHz = recipe.MinimumSampleRateHz,
        MinimumBitsPerSample = recipe.MinimumBitsPerSample,
        InputChannelChoice = SettingsChoiceLists.ChannelChoice(recipe.InputChannels),
        MatchAnyQualityMinimum = recipe.MatchAnyQualityMinimum,
        AlbumCondition = recipe.AlbumCondition,
        SourceSelection = recipe.SourceSelection,
        RequireFallbackApproval = recipe.RequireFallbackApproval,
        Action = recipe.Action,
        DestinationRootId = recipe.DestinationRootId,
        OutputExtension = recipe.OutputExtension,
        Codec = recipe.Codec,
        Encoder = recipe.Encoder,
        ExtraFfmpegOptions = recipe.ExtraFfmpegOptions,
        AddToMediaCatalog = recipe.AddToMediaCatalog,
        BitrateKbps = recipe.BitrateKbps,
        SampleRateHz = recipe.SampleRateHz,
        BitsPerSample = recipe.BitsPerSample,
        TranscodeFormatId = recipe.TranscodeFormatId ??
            InferLegacyFormatId(recipe),
        TranscodeEncoderId = recipe.TranscodeEncoderId ??
            InferLegacyEncoderId(recipe),
        TranscodeRateMode = Enum.TryParse(
            recipe.TranscodeRateMode,
            ignoreCase: true,
            out AudioTranscodeRateMode rateMode)
                ? rateMode
                : InferLegacyRateMode(recipe),
        TranscodeQuality = recipe.TranscodeQuality,
        TranscodeCompressionEffort =
            recipe.TranscodeCompressionEffort,
        TranscodeCreateCorrectionFile =
            recipe.TranscodeCreateCorrectionFile,
        OutputChannelChoice = SettingsChoiceLists.ChannelChoice(recipe.OutputChannels),
        PreserveMetadata = recipe.PreserveMetadata,
        PreserveArtwork = recipe.PreserveArtwork,
        UseProfileCollision = recipe.CollisionPolicy is null,
        CollisionPolicy = recipe.CollisionPolicy ?? LibraryPathCollisionPolicy.Stop,
    };

    public static IngestRecipeEditorRow Create() => From(new(
        Id: "recipe-" + Guid.NewGuid().ToString("N")[..12],
        Name: "New recipe",
        Enabled: false,
        InputExtensions: [".flac"],
        RequireLossless: null,
        MinimumSampleRateHz: null,
        MinimumBitsPerSample: null,
        InputChannels: LibraryChannelSelection.Stereo,
        MatchAnyQualityMinimum: false,
        Action: LibraryIngestAction.Copy,
        DestinationRootId: null,
        DestinationLegacyRole: LibraryIngestRole.None,
        OutputExtension: null,
        Codec: null,
        Encoder: null,
        BitrateKbps: null,
        SampleRateHz: null,
        BitsPerSample: null,
        OutputChannels: LibraryChannelSelection.Stereo,
        PreserveMetadata: true,
        PreserveArtwork: true,
        CollisionPolicy: null));

    public IngestRecipeEditorRow CloneForDuplicate(
        string id,
        string name)
    {
        var clone = new IngestRecipeEditorRow
        {
            Source = Source with
            {
                Id = id,
                Name = name,
            },
        };
        clone._getText = _getText;
        clone._formatText = _formatText;
        clone.Id = id;
        clone.Name = name;
        clone.Enabled = Enabled;
        clone.InputExtensions = InputExtensions;
        clone.RequireLossless = RequireLossless;
        clone.MinimumSampleRateHz =
            MinimumSampleRateHz;
        clone.MinimumBitsPerSample =
            MinimumBitsPerSample;
        clone.InputChannelChoice =
            InputChannelChoice;
        clone.MatchAnyQualityMinimum =
            MatchAnyQualityMinimum;
        clone.AlbumCondition = AlbumCondition;
        clone.SourceSelection = SourceSelection;
        clone.RequireFallbackApproval =
            RequireFallbackApproval;
        clone.Action = Action;
        clone.DestinationRootChoice =
            DestinationRootChoice;
        clone.DestinationRootId =
            DestinationRootId;
        clone.OutputExtension =
            OutputExtension;
        clone.Codec = Codec;
        clone.Encoder = Encoder;
        clone.ExtraFfmpegOptions =
            ExtraFfmpegOptions;
        clone.AddToMediaCatalog =
            AddToMediaCatalog;
        clone.BitrateKbps = BitrateKbps;
        clone.SampleRateHz = SampleRateHz;
        clone.BitsPerSample = BitsPerSample;
        clone.TranscodeFormatId =
            TranscodeFormatId;
        clone.TranscodeEncoderId =
            TranscodeEncoderId;
        clone.TranscodeRateMode =
            TranscodeRateMode;
        clone.TranscodeQuality =
            TranscodeQuality;
        clone.TranscodeCompressionEffort =
            TranscodeCompressionEffort;
        clone.TranscodeCreateCorrectionFile =
            TranscodeCreateCorrectionFile;
        clone.OutputChannelChoice =
            OutputChannelChoice;
        clone.PreserveMetadata =
            PreserveMetadata;
        clone.PreserveArtwork =
            PreserveArtwork;
        clone.UseProfileCollision =
            UseProfileCollision;
        clone.CollisionPolicy =
            CollisionPolicy;
        clone._transcodeCapabilities =
            _transcodeCapabilities;
        clone.DestinationRootChoices.Clear();
        foreach (SettingsRootChoice choice in
                 DestinationRootChoices)
        {
            clone.DestinationRootChoices.Add(
                choice);
        }
        CopyChoices(
            TranscodeFormatChoices,
            clone.TranscodeFormatChoices);
        CopyChoices(
            TranscodeEncoderChoices,
            clone.TranscodeEncoderChoices);
        CopyChoices(
            TranscodeRateModeChoices,
            clone.TranscodeRateModeChoices);
        CopyChoices(
            TranscodeSampleRateChoices,
            clone.TranscodeSampleRateChoices);
        CopyChoices(
            TranscodeBitDepthChoices,
            clone.TranscodeBitDepthChoices);
        return clone;
    }

    public LibraryIngestRecipe Build()
    {
        return Source with
        {
            Id = Id.Trim(),
            Name = Name.Trim(),
            Enabled = Enabled,
            InputExtensions = InputExtensions.Split(
                [',', ';', ' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            RequireLossless = RequireLossless,
            MinimumSampleRateHz = MinimumSampleRateHz,
            MinimumBitsPerSample = MinimumBitsPerSample,
            InputChannels = InputChannelChoice.Value,
            MatchAnyQualityMinimum = MatchAnyQualityMinimum,
            AlbumCondition = AlbumCondition,
            SourceSelection = SourceSelection,
            RequireFallbackApproval = RequireFallbackApproval,
            Action = Action,
            DestinationRootId = DestinationRootId,
            DestinationLegacyRole = LibraryIngestRole.None,
            OutputExtension = IsTranscode
                ? SharedOutputExtension(
                    TranscodeFormatId)
                : Clean(OutputExtension),
            Codec = IsTranscode
                ? SharedOutputCodec(
                    TranscodeFormatId)
                : Clean(Codec),
            Encoder = IsTranscode
                ? LegacyEncoder(
                    TranscodeEncoderId)
                : Clean(Encoder),
            ExtraFfmpegOptions = IsTranscode
                ? null
                : Clean(ExtraFfmpegOptions),
            TranscodeFormatId = IsTranscode
                ? Clean(TranscodeFormatId)
                : null,
            TranscodeEncoderId = IsTranscode
                ? Clean(TranscodeEncoderId) ??
                  AudioTranscodeEncoderIds.Automatic
                : null,
            TranscodeRateMode = IsTranscode
                ? TranscodeRateMode.ToString()
                : null,
            TranscodeQuality = IsTranscode &&
                HasQualityControl
                    ? TranscodeQuality
                    : null,
            TranscodeCompressionEffort =
                TranscodeCompressionEffort,
            TranscodeCreateCorrectionFile =
                IsTranscode &&
                TranscodeCreateCorrectionFile,
            AddToMediaCatalog = AddToMediaCatalog,
            BitrateKbps = BitrateKbps,
            SampleRateHz = SampleRateHz,
            BitsPerSample = BitsPerSample,
            OutputChannels = OutputChannelChoice.Value,
            PreserveMetadata = PreserveMetadata,
            PreserveArtwork = PreserveArtwork,
            CollisionPolicy = UseProfileCollision ? null : CollisionPolicy,
            OutputRepresentationRole = LibraryRepresentationRole.Ignore,
        };
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void ApplyTranscodeCapabilities(
        AudioTranscodeCapabilitySnapshot snapshot,
        ILocalizationService localization)
    {
        _transcodeCapabilities = snapshot;
        _getText = localization.Get;
        _formatText = localization.Format;
        RefreshTranscodeChoices();
    }

    private void RefreshTranscodeChoices()
    {
        _refreshingTranscodeChoices = true;
        try
        {
            string? selectedFormat = TranscodeFormatId;
            TranscodeFormatChoices.Clear();
            foreach (AudioTranscodeFormatDescriptor format in
                     _transcodeCapabilities?.Formats ?? [])
                TranscodeFormatChoices.Add(new(
                    format.Id,
                    FormatLabel(format)));
            if (!string.IsNullOrWhiteSpace(selectedFormat) &&
                !TranscodeFormatChoices.Any(choice =>
                    choice.Value == selectedFormat))
                TranscodeFormatChoices.Add(new(
                    selectedFormat,
                    selectedFormat + " — " +
                    _getText("Transcode.Value.Unavailable")));
            TranscodeFormatId = selectedFormat ??
                TranscodeFormatChoices.FirstOrDefault(choice =>
                    choice.Value == AudioTranscodeFormatIds.Flac)?.Value ??
                TranscodeFormatChoices.FirstOrDefault()?.Value;
            RefreshTranscodeEncoderChoices();
            RefreshChoice(
                TranscodeSampleRateChoices,
                new int?[]
                {
                    null, 44100, 48000, 88200, 96000, 176400, 192000,
                },
                value => value is null
                    ? _getText("Transcode.Value.Preserve")
                    : $"{value.Value / 1000d:0.#} kHz");
            RefreshChoice(
                TranscodeBitDepthChoices,
                new int?[] { null, 16, 24, 32 },
                value => value is null
                    ? _getText("Transcode.Value.Preserve")
                    : _formatText(
                        "Transcode.Value.Bits",
                        [value.Value]));
            OnPropertyChanged(
                nameof(SelectedTranscodeFormatChoice));
            OnPropertyChanged(
                nameof(SelectedTranscodeSampleRateChoice));
            OnPropertyChanged(
                nameof(SelectedTranscodeBitDepthChoice));
        }
        finally
        {
            _refreshingTranscodeChoices = false;
            RefreshTranscodeCorrectionFileOption(
                clearInapplicable: false);
        }
    }

    private void RefreshTranscodeEncoderChoices()
    {
        string selected = TranscodeEncoderId;
        TranscodeEncoderChoices.Clear();
        TranscodeEncoderChoices.Add(new(
            AudioTranscodeEncoderIds.Automatic,
            _getText("Transcode.Encoder.Auto")));
        AudioTranscodeFormatDescriptor? format =
            _transcodeCapabilities?.FindFormat(
                TranscodeFormatId ?? "");
        foreach (string id in format?.EncoderIds ?? [])
            TranscodeEncoderChoices.Add(new(
                id,
                EncoderLabel(id)));
        if (!TranscodeEncoderChoices.Any(choice =>
                choice.Value == selected))
            TranscodeEncoderChoices.Add(new(
                selected,
                selected + " — " +
                _getText("Transcode.Value.Unavailable")));
        TranscodeEncoderId = selected;
        RefreshTranscodeRateModeChoices();
        OnPropertyChanged(
            nameof(SelectedTranscodeEncoderChoice));
    }

    private void RefreshTranscodeRateModeChoices()
    {
        AudioEncoderDescriptor? encoder =
            ResolveSelectedTranscodeEncoder();
        AudioTranscodeRateMode[] modes = encoder is null
            ? [TranscodeRateMode]
            : [.. encoder.RateControls.Select(control =>
                control.Mode).Distinct()];
        AudioTranscodeRateMode selected = TranscodeRateMode;
        RefreshChoice(
            TranscodeRateModeChoices,
            modes,
            mode => _getText($"Transcode.RateMode.{mode}"));
        TranscodeRateMode = modes.Contains(selected)
            ? selected
            : modes[0];
        OnPropertyChanged(
            nameof(SelectedTranscodeRateModeChoice));
        RefreshTranscodeCorrectionFileOption(
            clearInapplicable:
                !_refreshingTranscodeChoices);
    }

    private AudioEncoderDescriptor?
        ResolveSelectedTranscodeEncoder()
    {
        AudioTranscodeFormatDescriptor? format =
            _transcodeCapabilities?.FindFormat(
                TranscodeFormatId ?? "");
        string? encoderId = TranscodeEncoderId ==
                            AudioTranscodeEncoderIds.Automatic
            ? format?.EncoderIds.FirstOrDefault()
            : TranscodeEncoderId;
        return encoderId is null ||
               format is null ||
               !format.EncoderIds.Contains(
                   encoderId,
                   StringComparer.Ordinal)
            ? null
            : _transcodeCapabilities?.FindEncoder(
                encoderId);
    }

    private void RefreshTranscodeCorrectionFileOption(
        bool clearInapplicable)
    {
        if (clearInapplicable &&
            !IsTranscodeCorrectionFileSupported &&
            TranscodeCreateCorrectionFile)
        {
            TranscodeCreateCorrectionFile = false;
        }

        OnPropertyChanged(
            nameof(
                IsTranscodeCorrectionFileSupported));
        OnPropertyChanged(
            nameof(
                IsTranscodeCorrectionFileOptionVisible));
        OnPropertyChanged(
            nameof(
                CanEditTranscodeCorrectionFile));
        OnPropertyChanged(
            nameof(
                TranscodeCorrectionFileHelpText));
    }

    private string FormatLabel(
        AudioTranscodeFormatDescriptor format) =>
        format.Id switch
        {
            AudioTranscodeFormatIds.Flac =>
                _getText("Transcode.Format.Flac"),
            AudioTranscodeFormatIds.AlacM4a =>
                _getText("Transcode.Format.AlacM4a"),
            AudioTranscodeFormatIds.AacM4a =>
                _getText("Transcode.Format.AacM4a"),
            AudioTranscodeFormatIds.AacAdts =>
                _getText("Transcode.Format.AacAdts"),
            AudioTranscodeFormatIds.Mp3 =>
                _getText("Transcode.Format.Mp3"),
            AudioTranscodeFormatIds.OpusOgg =>
                _getText("Transcode.Format.OpusOgg"),
            AudioTranscodeFormatIds.VorbisOgg =>
                _getText("Transcode.Format.VorbisOgg"),
            AudioTranscodeFormatIds.WavPack =>
                _getText("Transcode.Format.WavPack"),
            AudioTranscodeFormatIds.PcmWave =>
                _getText("Transcode.Format.PcmWave"),
            AudioTranscodeFormatIds.PcmRf64 =>
                _getText("Transcode.Format.PcmRf64"),
            AudioTranscodeFormatIds.PcmAiff =>
                _getText("Transcode.Format.PcmAiff"),
            AudioTranscodeFormatIds.TrueAudio =>
                _getText("Transcode.Format.TrueAudio"),
            AudioTranscodeFormatIds.OptimFrog =>
                _getText("Transcode.Format.OptimFrog"),
            AudioTranscodeFormatIds.OptimFrogDualStream =>
                _getText("Transcode.Format.OptimFrogDualStream"),
            AudioTranscodeFormatIds.OptimFrogFloat =>
                _getText("Transcode.Format.OptimFrogFloat"),
            AudioTranscodeFormatIds.MonkeysAudio =>
                _getText("Transcode.Format.MonkeysAudio"),
            _ => $"{format.Codec} ({format.Container})",
        };

    private string EncoderLabel(string id) =>
        id.StartsWith("ffmpeg:", StringComparison.Ordinal)
            ? "FFmpeg — " + id["ffmpeg:".Length..]
            : id switch
            {
                AudioTranscodeEncoderIds.WavPackCli =>
                    _getText("Transcode.Encoder.WavPack"),
                AudioTranscodeEncoderIds.OptimFrogOfr =>
                    _getText("Transcode.Encoder.OptimFrogOfr"),
                AudioTranscodeEncoderIds.OptimFrogOfs =>
                    _getText("Transcode.Encoder.OptimFrogOfs"),
                AudioTranscodeEncoderIds.OptimFrogOff =>
                    _getText("Transcode.Encoder.OptimFrogOff"),
                AudioTranscodeEncoderIds.MonkeysAudioMac =>
                    _getText("Transcode.Encoder.MonkeysAudioMac"),
                _ => id,
            };

    private static void RefreshChoice<T>(
        ObservableCollection<LocalizedChoice<T>> choices,
        IEnumerable<T> values,
        Func<T, string> label)
    {
        choices.Clear();
        foreach (T value in values)
            choices.Add(new(value, label(value)));
    }

    private static void CopyChoices<T>(
        IEnumerable<LocalizedChoice<T>> source,
        ICollection<LocalizedChoice<T>> destination)
    {
        destination.Clear();
        var seen = new HashSet<T>();
        foreach (LocalizedChoice<T> choice in source)
        {
            if (!seen.Add(choice.Value))
                continue;
            destination.Add(
                new(
                    choice.Value,
                    choice.Label));
        }
    }

    private static string? InferLegacyFormatId(
        LibraryIngestRecipe recipe)
    {
        string? extension = Clean(recipe.OutputExtension)?
            .ToLowerInvariant();
        string? codec = Clean(recipe.Codec)?.ToLowerInvariant();
        return extension switch
        {
            ".flac" or "flac" =>
                AudioTranscodeFormatIds.Flac,
            ".m4a" or "m4a" when codec == "alac" =>
                AudioTranscodeFormatIds.AlacM4a,
            ".m4a" or "m4a" =>
                AudioTranscodeFormatIds.AacM4a,
            ".wv" or "wv" =>
                AudioTranscodeFormatIds.WavPack,
            ".ape" or "ape" =>
                AudioTranscodeFormatIds.MonkeysAudio,
            _ => null,
        };
    }

    private static string InferLegacyEncoderId(
        LibraryIngestRecipe recipe)
    {
        if (!string.IsNullOrWhiteSpace(
                recipe.TranscodeEncoderId))
            return recipe.TranscodeEncoderId;
        if (InferLegacyFormatId(recipe) ==
            AudioTranscodeFormatIds.WavPack)
            return AudioTranscodeEncoderIds.WavPackCli;
        if (InferLegacyFormatId(recipe) ==
            AudioTranscodeFormatIds.MonkeysAudio)
            return AudioTranscodeEncoderIds.MonkeysAudioMac;
        return string.IsNullOrWhiteSpace(recipe.Encoder)
            ? AudioTranscodeEncoderIds.Automatic
            : recipe.Encoder.Contains(
                ':',
                StringComparison.Ordinal)
                ? recipe.Encoder
                : AudioTranscodeEncoderIds.Ffmpeg(
                    recipe.Encoder);
    }

    private static AudioTranscodeRateMode InferLegacyRateMode(
        LibraryIngestRecipe recipe) =>
        recipe.BitrateKbps is not null
            ? AudioTranscodeRateMode.AverageBitrate
            : AudioTranscodeRateMode.Lossless;

    private static string? SharedOutputExtension(
        string? formatId) =>
        IngestTranscodeSettingsResolver.ResolveFormat(
            formatId)?.Extension;

    private static string? SharedOutputCodec(
        string? formatId) =>
        IngestTranscodeSettingsResolver.ResolveFormat(
            formatId)?.Codec;

    private static string? LegacyEncoder(string? encoderId) =>
        encoderId?.StartsWith(
            "ffmpeg:",
            StringComparison.Ordinal) == true
            ? encoderId["ffmpeg:".Length..]
            : null;

    partial void OnTranscodeFormatIdChanged(string? value)
    {
        if (_refreshingTranscodeChoices)
            return;
        OnPropertyChanged(
            nameof(SelectedTranscodeFormatChoice));
        RefreshTranscodeEncoderChoices();
    }

    partial void OnTranscodeEncoderIdChanged(string value)
    {
        if (_refreshingTranscodeChoices)
            return;
        OnPropertyChanged(
            nameof(SelectedTranscodeEncoderChoice));
        RefreshTranscodeRateModeChoices();
    }

    partial void OnTranscodeRateModeChanged(
        AudioTranscodeRateMode value)
    {
        OnPropertyChanged(
            nameof(SelectedTranscodeRateModeChoice));
        RefreshTranscodeCorrectionFileOption(
            clearInapplicable:
                !_refreshingTranscodeChoices);
    }

    partial void
        OnTranscodeCreateCorrectionFileChanged(
            bool value)
    {
        OnPropertyChanged(
            nameof(
                IsTranscodeCorrectionFileOptionVisible));
        OnPropertyChanged(
            nameof(
                CanEditTranscodeCorrectionFile));
        OnPropertyChanged(
            nameof(
                TranscodeCorrectionFileHelpText));
    }

    partial void OnSampleRateHzChanged(int? value) =>
        OnPropertyChanged(
            nameof(SelectedTranscodeSampleRateChoice));

    partial void OnBitsPerSampleChanged(int? value) =>
        OnPropertyChanged(
            nameof(SelectedTranscodeBitDepthChoice));

    partial void OnDestinationRootChoiceChanged(SettingsRootChoice? value)
    {
        if (!_refreshingDestinationRoots)
            DestinationRootId = value?.Id;
    }

    public void RefreshDestinationRootChoices(
        IEnumerable<IndexTargetEditorRow> roots,
        string? noDirectRootLabel = null,
        string? newRootLabel = null,
        Func<Guid, string>? missingRootLabel = null)
    {
        noDirectRootLabel ??= LocalizedText.Get(
            "Settings.DestinationRoot.None");
        newRootLabel ??= LocalizedText.Get(
            "Settings.DestinationRoot.NewRoot");
        Guid? selectedId = DestinationRootId;
        _refreshingDestinationRoots = true;
        try
        {
            DestinationRootChoices.Clear();
            DestinationRootChoices.Add(new(null, noDirectRootLabel));
            foreach (IndexTargetEditorRow root in roots)
            {
                string path = string.IsNullOrWhiteSpace(root.Path)
                    ? newRootLabel
                    : root.Path.Trim();
                DestinationRootChoices.Add(new(root.Id, path));
            }
            SettingsRootChoice? selected = DestinationRootChoices.FirstOrDefault(choice =>
                choice.Id == selectedId);
            if (selected is null && selectedId is not null)
            {
                selected = new(selectedId,
                    missingRootLabel?.Invoke(selectedId.Value) ??
                    $"Missing root ({selectedId.Value:D})");
                DestinationRootChoices.Add(selected);
            }
            DestinationRootChoice = selected ?? DestinationRootChoices[0];
        }
        finally
        {
            _refreshingDestinationRoots = false;
        }
    }
}

public partial class IngestProfileEditorRow : ObservableObject
{
    public required LibraryIngestProfile Source { get; init; }
    public string Id => Source.Id;
    public bool IsBuiltIn => LibraryIngestProfilePresets.All.Any(profile => string.Equals(
        profile.Id, Id, StringComparison.OrdinalIgnoreCase));
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private LibrarySourceDisposition _sourceDisposition;
    [ObservableProperty] private bool _preserveSidecars;
    public ObservableCollection<IngestRecipeEditorRow> Recipes { get; } = [];

    public static IngestProfileEditorRow From(LibraryIngestProfile profile)
    {
        var editor = new IngestProfileEditorRow
        {
            Source = profile,
            Name = profile.Name,
            Enabled = profile.Ingest.Enabled,
            SourceDisposition = profile.Ingest.SourceDisposition,
            PreserveSidecars = profile.Ingest.PreserveSidecars,
        };
        foreach (LibraryIngestRecipe recipe in profile.Ingest.Recipes)
            editor.Recipes.Add(IngestRecipeEditorRow.From(recipe));
        return editor;
    }

    public LibraryIngestProfile Build() => Source with
    {
        Name = Name.Trim(),
        Ingest = Source.Ingest with
        {
            Enabled = Enabled,
            SourceDisposition = SourceDisposition,
            PreserveSidecars = PreserveSidecars,
            Recipes = Recipes.Select(recipe => recipe.Build()).ToArray(),
        },
    };
}

public partial class SidecarRuleEditorRow : ObservableObject
{
    public required LibrarySidecarRule Source { get; init; }
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _patterns = "";
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private LibrarySidecarDisposition _disposition;

    public static SidecarRuleEditorRow From(LibrarySidecarRule rule) => new()
    {
        Source = rule,
        Id = rule.Id,
        Name = rule.Name,
        Patterns = string.Join(", ", rule.Patterns),
        Enabled = rule.Enabled,
        Disposition = rule.Disposition,
    };

    public static SidecarRuleEditorRow Create() => From(new(
        "sidecar-" + Guid.NewGuid().ToString("N")[..12],
        "New sidecar rule",
        true,
        ["*.txt"],
        LibrarySidecarDisposition.Preserve));

    public SidecarRuleEditorRow CloneForDuplicate(
        string id,
        string name) =>
        new()
        {
            Source = Source with
            {
                Id = id,
                Name = name,
            },
            Id = id,
            Name = name,
            Patterns = Patterns,
            Enabled = Enabled,
            Disposition = Disposition,
        };

    public LibrarySidecarRule Build() => Source with
    {
        Id = Id.Trim(),
        Name = Name.Trim(),
        Patterns = Patterns.Split([',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        Enabled = Enabled,
        Disposition = Disposition,
    };
}

public partial class LibraryProfileEditorRow : ObservableObject
{
    public required LibraryProfile Source { get; init; }
    public string Id => Source.Id;
    public bool IsCustom => Source.Preset == LibraryProfilePreset.Custom;

    [ObservableProperty] private string _name = "";

    [ObservableProperty] private string _directoryTemplate = "";
    [ObservableProperty] private string _fileNameTemplate = "";
    [ObservableProperty] private int _trackPadding;
    [ObservableProperty] private int _discPadding;
    [ObservableProperty] private LibraryPathCollisionPolicy _collisionPolicy;
    [ObservableProperty] private bool _useItunesCanonicalNaming;
    [ObservableProperty] private bool _preserveUnicode;
    [ObservableProperty] private string _invalidCharacterReplacement = "_";
    [ObservableProperty] private string _missingArtistFallback = "Unknown Artist";
    [ObservableProperty] private string _missingAlbumFallback = "Unknown Album";
    [ObservableProperty] private string _missingTitleFallback = "Untitled";
    [ObservableProperty] private string _compilationValue = "Compilations";
    [ObservableProperty] private LibraryUnicodeNormalization _unicodeNormalization;
    [ObservableProperty] private int? _componentLengthLimit;
    [ObservableProperty] private int? _discAlbumLengthLimit;
    [ObservableProperty] private int? _completePathLengthLimit;
    [ObservableProperty] private LibraryDiscStrategy _discStrategy;
    [ObservableProperty] private LibraryTrackTotalScope _trackTotalScope;
    [ObservableProperty] private bool _inferAlbumSuffix;
    [ObservableProperty] private bool _identityUsesAlbumArtist;
    [ObservableProperty] private bool _identityStripsFormatSuffixes;
    [ObservableProperty] private bool _identityStripsDiscSuffixes;
    [ObservableProperty] private bool _identityIncludesReleaseYear;
    [ObservableProperty] private bool _preserveReplayGain;
    [ObservableProperty] private bool _preserveMusicBrainzIdentifiers;
    [ObservableProperty] private bool _preserveCustomFields;
    [ObservableProperty] private bool _preserveCompilationSemantics;
    [ObservableProperty] private int _highResolutionMinimumSampleRateHz;
    [ObservableProperty] private int _highResolutionMinimumBitsPerSample;
    [ObservableProperty] private LibraryArtworkStorage _artworkStorage;
    [ObservableProperty] private LibraryArtworkRoleSelection _artworkRoles;
    [ObservableProperty] private LibraryArtworkEncoding _artworkEncoding;
    [ObservableProperty] private int _artworkMaximumDimension;
    [ObservableProperty] private int _artworkMaximumEncodedBytes;
    [ObservableProperty] private int _artworkJpegQuality;
    [ObservableProperty] private string _artworkSidecarTemplate = "";
    [ObservableProperty] private bool _readArtworkAtIndexTime;
    [ObservableProperty] private LibrarySidecarDisposition _unknownSidecarDisposition;

    public ObservableCollection<HealthRuleEditorRow> HealthRules { get; } = [];
    public ObservableCollection<SidecarRuleEditorRow> SidecarRules { get; } = [];

    public static LibraryProfileEditorRow From(LibraryProfile profile)
    {
        var editor = new LibraryProfileEditorRow
        {
            Source = profile,
            Name = profile.Name,
            DirectoryTemplate = profile.Naming.DirectoryTemplate,
            FileNameTemplate = profile.Naming.FileNameTemplate,
            TrackPadding = profile.Naming.TrackPadding,
            DiscPadding = profile.Naming.DiscPadding,
            CollisionPolicy = profile.Naming.CollisionPolicy,
            UseItunesCanonicalNaming = profile.Naming.UseItunesCanonicalNaming,
            PreserveUnicode = profile.Naming.PreserveUnicode,
            InvalidCharacterReplacement = profile.Naming.InvalidCharacterReplacement,
            MissingArtistFallback = profile.Naming.MissingArtistFallback,
            MissingAlbumFallback = profile.Naming.MissingAlbumFallback,
            MissingTitleFallback = profile.Naming.MissingTitleFallback,
            CompilationValue = profile.Naming.CompilationValue,
            UnicodeNormalization = profile.Naming.UnicodeNormalization,
            ComponentLengthLimit = profile.Naming.ComponentLengthLimit,
            DiscAlbumLengthLimit = profile.Naming.DiscAlbumLengthLimit,
            CompletePathLengthLimit = profile.Naming.CompletePathLengthLimit,
            DiscStrategy = profile.Disc.Strategy,
            TrackTotalScope = profile.Disc.TrackTotalScope,
            InferAlbumSuffix = profile.Disc.InferAlbumSuffix,
            IdentityUsesAlbumArtist = profile.AlbumIdentity.UseAlbumArtist,
            IdentityStripsFormatSuffixes = profile.AlbumIdentity.StripFormatSuffixes,
            IdentityStripsDiscSuffixes = profile.AlbumIdentity.StripDiscSuffixes,
            IdentityIncludesReleaseYear = profile.AlbumIdentity.IncludeReleaseYear,
            PreserveReplayGain = profile.Metadata.PreserveReplayGain,
            PreserveMusicBrainzIdentifiers =
                profile.Metadata.PreserveMusicBrainzIdentifiers,
            PreserveCustomFields = profile.Metadata.PreserveCustomFields,
            PreserveCompilationSemantics =
                profile.Metadata.PreserveCompilationSemantics,
            HighResolutionMinimumSampleRateHz =
                profile.Quality.HighResolutionMinimumSampleRateHz,
            HighResolutionMinimumBitsPerSample =
                profile.Quality.HighResolutionMinimumBitsPerSample,
            ArtworkStorage = profile.Artwork.Storage,
            ArtworkRoles = profile.Artwork.Roles,
            ArtworkEncoding = profile.Artwork.Encoding,
            ArtworkMaximumDimension = profile.Artwork.MaximumDimension,
            ArtworkMaximumEncodedBytes = profile.Artwork.MaximumEncodedBytes,
            ArtworkJpegQuality = profile.Artwork.JpegQuality,
            ArtworkSidecarTemplate = profile.Artwork.SidecarFileNameTemplate,
            ReadArtworkAtIndexTime = profile.Artwork.ReadAtIndexTime,
            UnknownSidecarDisposition = profile.Sidecars.UnknownFileDisposition,
        };
        foreach (LibraryHealthRulePolicy rule in profile.Health.Rules)
            editor.HealthRules.Add(new()
            {
                Id = rule.Id,
                Enabled = rule.Enabled,
                Severity = rule.Severity,
                ProposeRepair = rule.ProposeRepair,
                ApplyRepair = rule.ApplyRepair,
            });
        foreach (LibrarySidecarRule rule in profile.Sidecars.Rules)
            editor.SidecarRules.Add(SidecarRuleEditorRow.From(rule));
        return editor;
    }

    public LibraryProfile Build() => Source with
    {
        Name = Name.Trim(),
        Naming = Source.Naming with
        {
            DirectoryTemplate = DirectoryTemplate,
            FileNameTemplate = FileNameTemplate,
            TrackPadding = TrackPadding,
            DiscPadding = DiscPadding,
            CollisionPolicy = CollisionPolicy,
            UseItunesCanonicalNaming = UseItunesCanonicalNaming,
            PreserveUnicode = PreserveUnicode,
            InvalidCharacterReplacement = InvalidCharacterReplacement,
            MissingArtistFallback = MissingArtistFallback,
            MissingAlbumFallback = MissingAlbumFallback,
            MissingTitleFallback = MissingTitleFallback,
            CompilationValue = CompilationValue,
            UnicodeNormalization = UnicodeNormalization,
            ComponentLengthLimit = ComponentLengthLimit,
            DiscAlbumLengthLimit = DiscAlbumLengthLimit,
            CompletePathLengthLimit = CompletePathLengthLimit,
        },
        Disc = Source.Disc with
        {
            Strategy = DiscStrategy,
            TrackTotalScope = TrackTotalScope,
            InferAlbumSuffix = InferAlbumSuffix,
        },
        AlbumIdentity = Source.AlbumIdentity with
        {
            UseAlbumArtist = IdentityUsesAlbumArtist,
            StripFormatSuffixes = IdentityStripsFormatSuffixes,
            StripDiscSuffixes = IdentityStripsDiscSuffixes,
            IncludeReleaseYear = IdentityIncludesReleaseYear,
        },
        Metadata = Source.Metadata with
        {
            PreserveReplayGain = PreserveReplayGain,
            PreserveMusicBrainzIdentifiers = PreserveMusicBrainzIdentifiers,
            PreserveCustomFields = PreserveCustomFields,
            PreserveCompilationSemantics = PreserveCompilationSemantics,
        },
        Quality = Source.Quality with
        {
            HighResolutionMinimumSampleRateHz = HighResolutionMinimumSampleRateHz,
            HighResolutionMinimumBitsPerSample = HighResolutionMinimumBitsPerSample,
        },
        Health = new(HealthRules.Select(rule => rule.Build()).ToArray()),
        Artwork = Source.Artwork with
        {
            Storage = ArtworkStorage,
            Roles = ArtworkRoles,
            Encoding = ArtworkEncoding,
            MaximumDimension = ArtworkMaximumDimension,
            MaximumEncodedBytes = ArtworkMaximumEncodedBytes,
            JpegQuality = ArtworkJpegQuality,
            SidecarFileNameTemplate = ArtworkSidecarTemplate,
            ReadAtIndexTime = ReadArtworkAtIndexTime,
        },
        Sidecars = Source.Sidecars with
        {
            UnknownFileDisposition = UnknownSidecarDisposition,
            Rules = SidecarRules.Select(rule => rule.Build()).ToArray(),
        },
    };
}
