using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MusicFileUtilities;

/// <summary>
/// Native Matroska/WebM metadata reader and preservation-safe editor. Writes leave every Cluster
/// and Cue byte at its original offset: replaced top-level metadata becomes equal-sized Void
/// padding, new metadata is appended, and an existing pre-Cluster slot receives an updated
/// SeekHead.
/// </summary>
public sealed class MatroskaFile :
    TagBase,
    IMediaFile,
    ICodecProvider,
    IMetadataWriter,
    IMultiValueMetadataWriter,
    IUserStringMetadata,
    IMultiValueUserStringMetadata,
    IArtworkWriter,
    IChapterMetadata
{
    private const ulong EbmlId = 0x1A45DFA3;
    private const ulong SegmentId = 0x18538067;
    private const ulong SeekHeadId = 0x114D9B74;
    private const ulong InfoId = 0x1549A966;
    private const ulong TracksId = 0x1654AE6B;
    private const ulong ClusterId = 0x1F43B675;
    private const ulong CuesId = 0x1C53BB6B;
    private const ulong AttachmentsId = 0x1941A469;
    private const ulong ChaptersId = 0x1043A770;
    private const ulong TagsId = 0x1254C367;
    private const ulong VoidId = 0xEC;
    private const ulong Crc32Id = 0xBF;

    private const ulong DocTypeId = 0x4282;
    private const ulong TimestampScaleId = 0x2AD7B1;
    private const ulong DurationId = 0x4489;
    private const ulong SegmentTitleId = 0x7BA9;
    private const ulong TrackEntryId = 0xAE;
    private const ulong TrackTypeId = 0x83;
    private const ulong TrackUidId = 0x73C5;
    private const ulong CodecIdId = 0x86;
    private const ulong CodecPrivateId = 0x63A2;
    private const ulong AudioId = 0xE1;
    private const ulong SamplingFrequencyId = 0xB5;
    private const ulong OutputSamplingFrequencyId = 0x78B5;
    private const ulong ChannelsId = 0x9F;
    private const ulong BitDepthId = 0x6264;

    private const ulong TagId = 0x7373;
    private const ulong TargetsId = 0x63C0;
    private const ulong TagTrackUidId = 0x63C5;
    private const ulong TagEditionUidId = 0x63C9;
    private const ulong TagChapterUidId = 0x63C4;
    private const ulong TagAttachmentUidId = 0x63C6;
    private const ulong SimpleTagId = 0x67C8;
    private const ulong TagNameId = 0x45A3;
    private const ulong TagLanguageId = 0x447A;
    private const ulong TagLanguageBcp47Id = 0x447B;
    private const ulong TagDefaultId = 0x4484;
    private const ulong TagStringId = 0x4487;
    private const ulong TagBinaryId = 0x4485;

    private const ulong AttachedFileId = 0x61A7;
    private const ulong FileDescriptionId = 0x467E;
    private const ulong FileNameId = 0x466E;
    private const ulong FileMediaTypeId = 0x4660;
    private const ulong FileDataId = 0x465C;
    private const ulong FileUidId = 0x46AE;

    private const ulong EditionEntryId = 0x45B9;
    private const ulong ChapterAtomId = 0xB6;
    private const ulong ChapterUidId = 0x73C4;
    private const ulong ChapterTimeStartId = 0x91;
    private const ulong ChapterTimeEndId = 0x92;
    private const ulong ChapterDisplayId = 0x80;
    private const ulong ChapStringId = 0x85;
    private const ulong ChapLanguageId = 0x437C;
    private const ulong ChapLanguageBcp47Id = 0x437D;

    private const int MaxTextMetadataBytes = 32 * 1024 * 1024;
    private const int MaxAttachmentBytes = 512 * 1024 * 1024;

    private static readonly Dictionary<string, TagFields> Mappings =
        CreateMappings();
    private static readonly Dictionary<TagFields, string> PreferredNames =
        CreatePreferredNames();

    private readonly bool _readArtwork;
    private readonly List<ElementRef> _children = [];
    private readonly List<TagGroup> _tagGroups = [];
    private readonly List<MatroskaAttachment> _attachments = [];
    private readonly List<MediaChapter> _chapters = [];
    private readonly List<(ulong Id, byte[] Raw)> _infoChildren = [];
    private string _filename;
    private string _docType = "";
    private long _segmentOffset;
    private long _segmentDataOffset;
    private long _segmentEnd;
    private int _segmentSizeWidth;
    private bool _segmentSizeUnknown;
    private ulong _timestampScale = 1_000_000;
    private double _durationTicks;
    private string _infoTitle = "";
    private ulong _audioTrackUid;
    private bool _hasAudioTrack;
    private bool _tagsDirty;
    private bool _attachmentsDirty;
    private bool _chaptersDirty;
    private bool _infoDirty;

    public MatroskaFile(
        string filename,
        bool readArtwork = true,
        long? knownLength = null)
    {
        _filename = filename ??
            throw new ArgumentNullException(nameof(filename));
        _readArtwork = readArtwork;
        Parse(knownLength);
    }

    public override string TagType => _docType.Equals(
        "webm", StringComparison.OrdinalIgnoreCase)
        ? "WebM Tags"
        : "Matroska Tags";

    public string DocType => _docType;
    public IReadOnlyList<MediaChapter> Chapters => _chapters;
    public IEnumerable<ICodecProvider> Codecs
    {
        get { yield return this; }
    }
    public IEnumerable<IMetadataProvider> Tags
    {
        get { yield return this; }
    }

    public string CodecName { get; private set; } = "Matroska audio";
    public CodecType CodecType { get; private set; } = CodecType.Lossy;
    public uint AverageBitrate { get; private set; }
    public uint MaxBitrate { get; private set; }
    public uint BitsPerSample { get; private set; }
    public uint Samplerate { get; private set; }
    public uint Channels { get; private set; }
    public uint DurationInFrames { get; private set; }
    public uint DurationInSeconds => DurationInFrames / 75;

    public override IEnumerable<KeyValuePair<TagFields, string>>
        GetKnownMetadata()
    {
        bool hasTagTitle = GlobalValues().Any(value =>
            value.Text is not null &&
            Mappings.TryGetValue(value.Name, out TagFields field) &&
            field == TagFields.Title);
        if (!hasTagTitle && !string.IsNullOrEmpty(_infoTitle))
            yield return KeyValuePair.Create(
                TagFields.Title, _infoTitle);
        foreach (TagValue value in GlobalValues())
        {
            if (value.Text is null ||
                !Mappings.TryGetValue(value.Name, out TagFields field))
                continue;
            if (field is TagFields.TrackNumber or TagFields.DiscNumber)
            {
                string[] parts = value.Text.Split('/', 2);
                yield return KeyValuePair.Create(field, parts[0]);
                if (parts.Length == 2)
                    yield return KeyValuePair.Create(
                        field == TagFields.TrackNumber
                            ? TagFields.TotalTracks
                            : TagFields.TotalDiscs,
                        parts[1]);
            }
            else
                yield return KeyValuePair.Create(field, value.Text);
        }
    }

    public override IEnumerable<KeyValuePair<string, string>>
        GetTextMetadata()
    {
        if (!GlobalValues().Any(value =>
                value.Text is not null &&
                value.Name.Equals(
                    "TITLE", StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrEmpty(_infoTitle))
            yield return KeyValuePair.Create("TITLE", _infoTitle);
        foreach (TagValue value in GlobalValues())
            if (value.Text is not null)
                yield return KeyValuePair.Create(value.Name, value.Text);
    }

    public IEnumerable<KeyValuePair<string, string>> GetUserStrings()
    {
        foreach (TagValue value in GlobalValues())
            if (value.Text is not null &&
                !Mappings.ContainsKey(value.Name))
                yield return KeyValuePair.Create(value.Name, value.Text);
    }

    public override IEnumerable<IMetadataImage> GetImageMetadata()
    {
        if (!_readArtwork)
            yield break;
        foreach (MatroskaAttachment attachment in _attachments)
            if (attachment.IsImage)
                yield return attachment;
    }

    public void SetField(TagFields field, string value) =>
        SetFieldValues(
            field,
            value is null ? Array.Empty<string>() : [value]);

    public void RemoveField(TagFields field) =>
        SetFieldValues(field, Array.Empty<string>());

    public bool SupportsMultipleValues(TagFields field) =>
        field is not TagFields.NullField and
        not TagFields.Title and
        not TagFields.TrackNumber and
        not TagFields.TotalTracks and
        not TagFields.DiscNumber and
        not TagFields.TotalDiscs and
        not TagFields.MovementNumber and
        not TagFields.MovementTotal;

    public void SetFieldValues(
        TagFields field,
        IReadOnlyList<string> values)
    {
        if (field == TagFields.NullField)
            throw new ArgumentException(
                "NullField is not writable.", nameof(field));
        ArgumentNullException.ThrowIfNull(values);
        if (!SupportsMultipleValues(field) && values.Count > 1)
            throw new NotSupportedException(
                $"{field} does not support multiple Matroska values.");
        if (field is TagFields.TrackNumber or TagFields.TotalTracks)
        {
            SetNumberingField(
                field,
                values.FirstOrDefault(),
                TagFields.TrackNumber,
                TagFields.TotalTracks,
                "PART_NUMBER");
            return;
        }
        if (field is TagFields.DiscNumber or TagFields.TotalDiscs)
        {
            SetNumberingField(
                field,
                values.FirstOrDefault(),
                TagFields.DiscNumber,
                TagFields.TotalDiscs,
                "DISC");
            return;
        }

        foreach (TagGroup group in _tagGroups.Where(group => group.IsGlobal))
            group.Values.RemoveAll(value =>
                Mappings.TryGetValue(value.Name, out TagFields mapped) &&
                mapped == field);
        if (field == TagFields.Title)
        {
            _infoTitle = values.FirstOrDefault() ?? "";
            _infoDirty = true;
            _tagsDirty = true;
            RefreshStandardFields();
            return;
        }
        if (values.Count > 0)
        {
            TagGroup group = GetOrCreateGlobalGroup();
            string name = PreferredName(field);
            foreach (string value in values)
                if (value is not null)
                    group.Values.Add(new TagValue(name, value));
        }
        _tagsDirty = true;
        RefreshStandardFields();
    }

    private void SetNumberingField(
        TagFields changed,
        string value,
        TagFields numberField,
        TagFields totalField,
        string preferredName)
    {
        Dictionary<TagFields, string> current = GetKnownMetadata()
            .GroupBy(item => item.Key)
            .ToDictionary(group => group.Key, group => group.First().Value);
        string number = changed == numberField
            ? value
            : current.GetValueOrDefault(numberField);
        string total = changed == totalField
            ? value
            : current.GetValueOrDefault(totalField);
        foreach (TagGroup group in _tagGroups.Where(group => group.IsGlobal))
            group.Values.RemoveAll(item =>
                Mappings.TryGetValue(item.Name, out TagFields mapped) &&
                (mapped == numberField || mapped == totalField));
        if (!string.IsNullOrEmpty(number))
        {
            string combined = string.IsNullOrEmpty(total)
                ? number
                : number + "/" + total;
            GetOrCreateGlobalGroup().Values.Add(
                new TagValue(preferredName, combined));
        }
        else if (!string.IsNullOrEmpty(total))
        {
            GetOrCreateGlobalGroup().Values.Add(
                new TagValue(PreferredName(totalField), total));
        }
        _tagsDirty = true;
        RefreshStandardFields();
    }

    public void SetUserString(string key, string value) =>
        SetUserStringValues(
            key,
            value is null ? Array.Empty<string>() : [value]);

    public void RemoveUserString(string key) =>
        SetUserStringValues(key, Array.Empty<string>());

    public void SetUserStringValues(
        string key,
        IReadOnlyList<string> values)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException(
                "A user-string key is required.", nameof(key));
        ArgumentNullException.ThrowIfNull(values);
        string normalized = key.Trim().ToUpperInvariant();
        if (Mappings.ContainsKey(normalized))
            throw new ArgumentException(
                $"'{key}' is a standard Matroska tag.", nameof(key));
        foreach (TagGroup group in _tagGroups.Where(group => group.IsGlobal))
            group.Values.RemoveAll(value =>
                value.Name.Equals(normalized,
                    StringComparison.OrdinalIgnoreCase));
        if (values.Count > 0)
        {
            TagGroup group = GetOrCreateGlobalGroup();
            foreach (string value in values)
                if (value is not null)
                    group.Values.Add(new TagValue(normalized, value));
        }
        _tagsDirty = true;
        RefreshStandardFields();
    }

    public void SetFrontCover(byte[] imageData, string mimeType)
    {
        EnsureAttachmentsSupported();
        _attachments.RemoveAll(attachment =>
            attachment.IsImage &&
            attachment.PictureType == ID3v2Util.APICType.FrontCover);
        if (imageData is { Length: > 0 })
        {
            string normalized = NormalizeMime(mimeType, imageData);
            _attachments.Add(MatroskaAttachment.Create(
                ID3v2Util.APICType.FrontCover,
                normalized,
                "Front cover",
                CoverFileName(
                    ID3v2Util.APICType.FrontCover, normalized),
                imageData));
        }
        _attachmentsDirty = true;
    }

    public void RemoveImages()
    {
        EnsureAttachmentsSupported();
        _attachments.RemoveAll(attachment => attachment.IsImage);
        _attachmentsDirty = true;
    }

    public void SetImages(IReadOnlyList<ArtworkImage> images)
    {
        EnsureAttachmentsSupported();
        ArgumentNullException.ThrowIfNull(images);
        _attachments.RemoveAll(attachment => attachment.IsImage);
        foreach (ArtworkImage image in images)
        {
            if (image?.Data is not { Length: > 0 })
                continue;
            string mime = NormalizeMime(image.MimeType, image.Data);
            _attachments.Add(MatroskaAttachment.Create(
                image.Type,
                mime,
                image.Description,
                CoverFileName(image.Type, mime),
                image.Data));
        }
        _attachmentsDirty = true;
    }

    public void SetChapters(IReadOnlyList<MediaChapter> chapters)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        ulong previousStart = 0;
        for (int index = 0; index < chapters.Count; index++)
        {
            MediaChapter chapter = chapters[index] ??
                throw new ArgumentException(
                    "A chapter cannot be null.", nameof(chapters));
            if (index > 0 && chapter.StartNanoseconds < previousStart)
                throw new ArgumentException(
                    "Chapters must be ordered by start time.",
                    nameof(chapters));
            if (chapter.EndNanoseconds is ulong end &&
                end < chapter.StartNanoseconds)
                throw new ArgumentException(
                    "A chapter cannot end before it starts.",
                    nameof(chapters));
            previousStart = chapter.StartNanoseconds;
        }
        _chapters.Clear();
        _chapters.AddRange(chapters);
        _chaptersDirty = true;
    }

    public void SaveTags(string outputPath = null) =>
        Save(outputPath);

    public void Save(string outputPath = null)
    {
        string target = outputPath ?? _filename ??
            throw new InvalidOperationException(
                "No filename is associated with this file.");

        byte[] tags = _tagsDirty ? BuildTags() : null;
        byte[] info = _infoDirty ? BuildInfo() : null;
        byte[] attachments =
            _attachmentsDirty ? BuildAttachments() : null;
        byte[] chapters =
            _chaptersDirty ? BuildChapters() : null;

        var appended = new List<(ulong Id, byte[] Data)>();
        if (_infoDirty && info.Length > 0)
            appended.Add((InfoId, info));
        if (_chaptersDirty && chapters.Length > 0)
            appended.Add((ChaptersId, chapters));
        if (_attachmentsDirty && attachments.Length > 0)
            appended.Add((AttachmentsId, attachments));
        if (_tagsDirty && tags.Length > 0)
            appended.Add((TagsId, tags));

        long oldSegmentLength = _segmentEnd - _segmentDataOffset;
        long appendLength = appended.Sum(item => (long)item.Data.Length);
        long newSegmentLength = checked(oldSegmentLength + appendLength);
        if (!_segmentSizeUnknown &&
            !FitsVint((ulong)newSegmentLength, _segmentSizeWidth))
            throw new InvalidOperationException(
                "The edited Segment no longer fits its existing EBML size field.");

        var positions = new Dictionary<ulong, ulong>();
        foreach (ElementRef child in _children)
        {
            if (child.Id is SeekHeadId or VoidId or Crc32Id ||
                IsDirtyMutable(child.Id))
                continue;
            positions.TryAdd(
                child.Id,
                checked((ulong)(child.Offset - _segmentDataOffset)));
        }
        long nextPosition = oldSegmentLength;
        foreach ((ulong id, byte[] data) in appended)
        {
            positions[id] = checked((ulong)nextPosition);
            nextPosition = checked(nextPosition + data.Length);
        }

        var seekTargets = new List<(ulong Id, ulong Position)>();
        foreach (ulong id in new[]
                 {
                     InfoId, TracksId, ChaptersId, AttachmentsId,
                     TagsId, CuesId,
                 })
            if (positions.TryGetValue(id, out ulong position))
                seekTargets.Add((id, position));

        ElementRef seekSlot = SelectSeekSlot(seekTargets);
        byte[] seekHead = BuildSeekHeadExact(
            seekTargets, seekSlot.TotalLength);

        string tempPath = Tools.CreateSiblingTempPath(target);
        try
        {
            {
                using FileStream source =
                    Tools.OpenReadSequential(_filename);
                using FileStream destination =
                    Tools.CreateWriteSequential(tempPath);

                Tools.CopyExactly(source, destination, _segmentOffset);
                destination.Write(IdBytes(SegmentId));
                if (_segmentSizeUnknown)
                {
                    source.Position =
                        _segmentDataOffset - _segmentSizeWidth;
                    byte[] sizeBytes = new byte[_segmentSizeWidth];
                    source.ReadExactly(sizeBytes);
                    destination.Write(sizeBytes);
                }
                else
                    destination.Write(EncodeVint(
                        checked((ulong)newSegmentLength),
                        _segmentSizeWidth));

                long cursor = _segmentDataOffset;
                foreach (ElementRef child in _children)
                {
                    if (child.Offset > cursor)
                    {
                        source.Position = cursor;
                        Tools.CopyExactly(
                            source, destination,
                            child.Offset - cursor);
                    }

                    if (child.Offset == seekSlot.Offset)
                        destination.Write(seekHead);
                    else if (child.Id is SeekHeadId or Crc32Id ||
                             IsDirtyMutable(child.Id))
                        destination.Write(
                            BuildVoid(child.TotalLength));
                    else
                    {
                        source.Position = child.Offset;
                        Tools.CopyExactly(
                            source, destination,
                            child.TotalLength);
                    }
                    cursor = checked(
                        child.Offset + child.TotalLength);
                }
                if (cursor < _segmentEnd)
                {
                    source.Position = cursor;
                    Tools.CopyExactly(
                        source, destination,
                        _segmentEnd - cursor);
                }
                foreach ((_, byte[] data) in appended)
                    destination.Write(data);
                if (_segmentEnd < source.Length)
                {
                    source.Position = _segmentEnd;
                    Tools.CopyToEnd(source, destination);
                }
                destination.Flush(flushToDisk: true);
            }
            Tools.AtomicReplace(tempPath, target);
        }
        catch
        {
            Tools.DeleteIfExists(tempPath);
            throw;
        }

        _filename = target;
        Parse(knownLength: null);
    }

    private void Parse(long? knownLength)
    {
        _children.Clear();
        _tagGroups.Clear();
        _attachments.Clear();
        _chapters.Clear();
        _infoChildren.Clear();
        _docType = "";
        _timestampScale = 1_000_000;
        _durationTicks = 0;
        _infoTitle = "";
        _audioTrackUid = 0;
        _hasAudioTrack = false;
        _tagsDirty = false;
        _attachmentsDirty = false;
        _chaptersDirty = false;
        _infoDirty = false;
        ResetCodec();

        using FileStream stream = Tools.OpenReadSequential(_filename);
        long fileLength = knownLength ?? stream.Length;
        if (fileLength < 12)
            throw new InvalidDataException(
                "Truncated EBML document.");

        ElementRef ebml = ReadElement(stream, fileLength);
        if (ebml.Id != EbmlId)
            throw new InvalidDataException(
                "The file does not begin with an EBML Header.");
        byte[] ebmlPayload = ReadPayload(
            stream, ebml, 1024 * 1024, "EBML Header");
        foreach (MemoryElement child in Elements(ebmlPayload))
            if (child.Id == DocTypeId)
                _docType = ReadUtf8(child.Data);
        if (!_docType.Equals("matroska",
                StringComparison.OrdinalIgnoreCase) &&
            !_docType.Equals("webm",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Unsupported EBML DocType '{_docType}'.");

        stream.Position = checked(ebml.Offset + ebml.TotalLength);
        ElementRef segment;
        do
        {
            if (stream.Position >= fileLength)
                throw new InvalidDataException(
                    "The EBML document has no Segment.");
            segment = ReadElement(stream, fileLength);
            if (segment.Id != SegmentId)
                stream.Position = checked(
                    segment.Offset + segment.TotalLength);
        }
        while (segment.Id != SegmentId);

        _segmentOffset = segment.Offset;
        _segmentDataOffset = segment.DataOffset;
        _segmentSizeWidth =
            segment.HeaderLength - IdBytes(SegmentId).Length;
        _segmentSizeUnknown = segment.UnknownSize;
        _segmentEnd = segment.UnknownSize
            ? fileLength
            : checked(segment.DataOffset + segment.DataLength);

        stream.Position = _segmentDataOffset;
        while (stream.Position < _segmentEnd)
        {
            ElementRef child = ReadElement(stream, _segmentEnd);
            _children.Add(child);
            stream.Position = checked(child.Offset + child.TotalLength);
        }
        if (stream.Position != _segmentEnd)
            throw new InvalidDataException(
                "A Segment child exceeds the Segment boundary.");
        if (!_children.Any(child => child.Id == ClusterId))
            throw new InvalidDataException(
                "The Segment contains no Cluster.");

        // Matroska permits flexible top-level ordering. Parse technical Track state before Tags
        // so a TagTrackUID can be classified correctly even when Tags precede Tracks on disk.
        foreach (ElementRef child in _children.Where(
                     child => child.Id == InfoId))
            ParseInfo(ReadPayload(
                stream, child, MaxTextMetadataBytes, "Info"));
        foreach (ElementRef child in _children.Where(
                     child => child.Id == TracksId))
            ParseTracks(ReadPayload(
                stream, child, MaxTextMetadataBytes, "Tracks"));
        foreach (ElementRef child in _children.Where(
                     child => child.Id == TagsId))
            ParseTags(ReadPayload(
                stream, child, MaxTextMetadataBytes, "Tags"));
        foreach (ElementRef child in _children.Where(
                     child => child.Id == ChaptersId))
            ParseChapters(ReadPayload(
                stream, child, MaxTextMetadataBytes, "Chapters"));
        foreach (ElementRef child in _children.Where(
                     child => child.Id == AttachmentsId))
            ParseAttachments(ReadPayload(
                stream, child, MaxAttachmentBytes, "Attachments"));

        double seconds =
            _durationTicks * _timestampScale / 1_000_000_000d;
        if (seconds > 0)
        {
            DurationInFrames = checked((uint)Math.Min(
                uint.MaxValue, Math.Round(seconds * 75)));
            long clusterBytes = _children
                .Where(child => child.Id == ClusterId)
                .Sum(child => child.TotalLength);
            double bitrate = clusterBytes * 8d / seconds;
            AverageBitrate = checked((uint)Math.Min(
                uint.MaxValue, Math.Round(bitrate)));
        }
        RefreshStandardFields();
    }

    private void ParseInfo(byte[] payload)
    {
        foreach (MemoryElement child in Elements(payload))
        {
            _infoChildren.Add((child.Id, child.Raw.ToArray()));
            if (child.Id == TimestampScaleId)
                _timestampScale = ReadUnsigned(child.Data);
            else if (child.Id == DurationId)
                _durationTicks = ReadFloat(child.Data);
            else if (child.Id == SegmentTitleId)
                _infoTitle = ReadUtf8(child.Data);
        }
    }

    private void ParseTracks(byte[] payload)
    {
        foreach (MemoryElement entry in Elements(payload))
        {
            if (entry.Id != TrackEntryId)
                continue;
            ulong type = 0;
            ulong uid = 0;
            string codecId = "";
            byte[] codecPrivate = null;
            double sampling = 0;
            double outputSampling = 0;
            ulong channels = 0;
            ulong bitDepth = 0;
            foreach (MemoryElement child in Elements(entry.Data))
            {
                switch (child.Id)
                {
                    case TrackTypeId:
                        type = ReadUnsigned(child.Data);
                        break;
                    case TrackUidId:
                        uid = ReadUnsigned(child.Data);
                        break;
                    case CodecIdId:
                        codecId = ReadUtf8(child.Data);
                        break;
                    case CodecPrivateId:
                        codecPrivate = child.Data.ToArray();
                        break;
                    case AudioId:
                        foreach (MemoryElement audio in Elements(child.Data))
                        {
                            switch (audio.Id)
                            {
                                case SamplingFrequencyId:
                                    sampling = ReadNumber(audio.Data);
                                    break;
                                case OutputSamplingFrequencyId:
                                    outputSampling =
                                        ReadNumber(audio.Data);
                                    break;
                                case ChannelsId:
                                    channels =
                                        ReadUnsigned(audio.Data);
                                    break;
                                case BitDepthId:
                                    bitDepth =
                                        ReadUnsigned(audio.Data);
                                    break;
                            }
                        }
                        break;
                }
            }
            if (type != 2 || _hasAudioTrack)
                continue;
            _hasAudioTrack = true;
            _audioTrackUid = uid;
            ProjectCodec(
                codecId, codecPrivate,
                outputSampling > 0 ? outputSampling : sampling,
                channels, bitDepth);
        }
    }

    private void ProjectCodec(
        string codecId,
        byte[] codecPrivate,
        double sampling,
        ulong channels,
        ulong bitDepth)
    {
        (CodecName, CodecType) = codecId switch
        {
            "A_FLAC" => ("FLAC", CodecType.Lossless),
            "A_ALAC" => ("Apple Lossless", CodecType.Lossless),
            "A_OPUS" => ("Opus", CodecType.Lossy),
            "A_VORBIS" => ("Vorbis", CodecType.Lossy),
            "A_AAC" => ("AAC", CodecType.Lossy),
            "A_MPEG/L3" => ("MPEG Layer 3", CodecType.Lossy),
            "A_MPEG/L2" => ("MPEG Layer 2", CodecType.Lossy),
            "A_PCM/INT/LIT" => ("PCM", CodecType.Lossless),
            "A_PCM/INT/BIG" => ("PCM", CodecType.Lossless),
            "A_PCM/FLOAT/IEEE" => ("IEEE float PCM", CodecType.Lossless),
            "A_WAVPACK4" => ("WavPack", CodecType.Lossless),
            "A_TTA1" => ("TTA", CodecType.Lossless),
            _ when !string.IsNullOrEmpty(codecId) =>
                (codecId, CodecType.Lossy),
            _ => ("Matroska audio", CodecType.Lossy),
        };
        Samplerate = checked((uint)Math.Min(
            uint.MaxValue, Math.Round(sampling)));
        Channels = checked((uint)Math.Min(uint.MaxValue, channels));
        BitsPerSample = checked((uint)Math.Min(uint.MaxValue, bitDepth));

        if (codecId == "A_OPUS" && Samplerate == 0)
            Samplerate = 48_000;
        if (codecId == "A_FLAC" &&
            TryReadFlacStreamInfo(
                codecPrivate,
                out uint flacRate,
                out uint flacChannels,
                out uint flacBits))
        {
            Samplerate = flacRate;
            Channels = flacChannels;
            BitsPerSample = flacBits;
        }
    }

    private void ParseTags(byte[] payload)
    {
        foreach (MemoryElement tag in Elements(payload))
        {
            if (tag.Id != TagId)
                continue;
            byte[] targets = null;
            var values = new List<TagValue>();
            var unknown = new List<byte[]>();
            bool global = true;
            foreach (MemoryElement child in Elements(tag.Data))
            {
                if (child.Id == TargetsId)
                {
                    targets = child.Raw.ToArray();
                    foreach (MemoryElement target in Elements(child.Data))
                    {
                        if (target.Id is TagTrackUidId or
                            TagEditionUidId or
                            TagChapterUidId or
                            TagAttachmentUidId)
                        {
                            ulong uid = ReadUnsigned(target.Data);
                            if (uid != 0 &&
                                !(target.Id == TagTrackUidId &&
                                  uid == _audioTrackUid))
                                global = false;
                        }
                    }
                }
                else if (child.Id == SimpleTagId)
                    values.Add(ParseSimpleTag(child));
                else
                    unknown.Add(child.Raw.ToArray());
            }
            _tagGroups.Add(new TagGroup(
                global, targets, values, unknown));
        }
    }

    private static TagValue ParseSimpleTag(MemoryElement simple)
    {
        string name = "";
        string text = null;
        byte[] binary = null;
        string language = null;
        bool? isDefault = null;
        foreach (MemoryElement child in Elements(simple.Data))
        {
            switch (child.Id)
            {
                case TagNameId:
                    name = ReadUtf8(child.Data).ToUpperInvariant();
                    break;
                case TagStringId:
                    text = ReadUtf8(child.Data);
                    break;
                case TagBinaryId:
                    binary = child.Data.ToArray();
                    break;
                case TagLanguageBcp47Id:
                case TagLanguageId:
                    language ??= ReadUtf8(child.Data);
                    break;
                case TagDefaultId:
                    isDefault = ReadUnsigned(child.Data) != 0;
                    break;
            }
        }
        return new TagValue(
            name, text, binary, language, isDefault,
            simple.Raw.ToArray());
    }

    private void ParseAttachments(byte[] payload)
    {
        foreach (MemoryElement child in Elements(payload))
        {
            if (child.Id != AttachedFileId)
                continue;
            string description = "";
            string name = "";
            string mime = "application/octet-stream";
            byte[] data = [];
            ulong uid = 0;
            var unknown = new List<byte[]>();
            foreach (MemoryElement field in Elements(child.Data))
            {
                switch (field.Id)
                {
                    case FileDescriptionId:
                        description = ReadUtf8(field.Data);
                        break;
                    case FileNameId:
                        name = ReadUtf8(field.Data);
                        break;
                    case FileMediaTypeId:
                        mime = ReadAscii(field.Data);
                        break;
                    case FileDataId:
                        data = field.Data.ToArray();
                        break;
                    case FileUidId:
                        uid = ReadUnsigned(field.Data);
                        break;
                    default:
                        unknown.Add(field.Raw.ToArray());
                        break;
                }
            }
            _attachments.Add(new MatroskaAttachment(
                name, mime, description, data, uid, unknown));
        }
    }

    private void ParseChapters(byte[] payload)
    {
        foreach (MemoryElement edition in Elements(payload))
        {
            if (edition.Id != EditionEntryId)
                continue;
            foreach (MemoryElement child in Elements(edition.Data))
                if (child.Id == ChapterAtomId)
                    ParseChapterAtom(child);
        }
    }

    private void ParseChapterAtom(MemoryElement atom)
    {
        ulong start = 0;
        ulong? end = null;
        ulong? uid = null;
        string title = "";
        string language = "und";
        var nested = new List<MemoryElement>();
        foreach (MemoryElement child in Elements(atom.Data))
        {
            switch (child.Id)
            {
                case ChapterUidId:
                    uid = ReadUnsigned(child.Data);
                    break;
                case ChapterTimeStartId:
                    start = ReadUnsigned(child.Data);
                    break;
                case ChapterTimeEndId:
                    end = ReadUnsigned(child.Data);
                    break;
                case ChapterDisplayId:
                    foreach (MemoryElement display in Elements(child.Data))
                    {
                        if (display.Id == ChapStringId &&
                            string.IsNullOrEmpty(title))
                            title = ReadUtf8(display.Data);
                        else if (display.Id is ChapLanguageBcp47Id or
                                 ChapLanguageId)
                            language = ReadUtf8(display.Data);
                    }
                    break;
                case ChapterAtomId:
                    nested.Add(child);
                    break;
            }
        }
        _chapters.Add(new MediaChapter(
            start, end, title, language, uid));
        foreach (MemoryElement child in nested)
            ParseChapterAtom(child);
    }

    private byte[] BuildTags()
    {
        var tags = new List<byte[]>();
        foreach (TagGroup group in _tagGroups)
        {
            var children = new List<byte[]>();
            if (group.Targets is { Length: > 0 })
                children.Add(group.Targets);
            foreach (TagValue value in group.Values)
            {
                if (value.Raw is { Length: > 0 } && !value.Modified)
                    children.Add(value.Raw);
                else if (!string.IsNullOrWhiteSpace(value.Name))
                    children.Add(BuildSimpleTag(value));
            }
            children.AddRange(group.UnknownChildren);
            if (children.Count > 0)
                tags.Add(Master(TagId, children));
        }
        return tags.Count == 0
            ? []
            : Master(TagsId, tags);
    }

    private byte[] BuildInfo()
    {
        var children = _infoChildren
            .Where(child => child.Id != SegmentTitleId)
            .Select(child => child.Raw)
            .ToList();
        if (!string.IsNullOrEmpty(_infoTitle))
            children.Add(Utf8(SegmentTitleId, _infoTitle));
        return Master(InfoId, children);
    }

    private static byte[] BuildSimpleTag(TagValue value)
    {
        var children = new List<byte[]>
        {
            Utf8(TagNameId, value.Name),
        };
        if (!string.IsNullOrWhiteSpace(value.Language))
            children.Add(Utf8(
                value.Language.Contains('-')
                    ? TagLanguageBcp47Id
                    : TagLanguageId,
                value.Language));
        if (value.IsDefault.HasValue)
            children.Add(UInt(
                TagDefaultId, value.IsDefault.Value ? 1UL : 0UL));
        if (value.Text is not null)
            children.Add(Utf8(TagStringId, value.Text));
        else if (value.Binary is not null)
            children.Add(Binary(TagBinaryId, value.Binary));
        return Master(SimpleTagId, children);
    }

    private byte[] BuildAttachments()
    {
        if (_attachments.Count == 0)
            return [];
        var files = new List<byte[]>();
        foreach (MatroskaAttachment attachment in _attachments)
        {
            var children = new List<byte[]>();
            if (!string.IsNullOrEmpty(attachment.Description))
                children.Add(Utf8(
                    FileDescriptionId, attachment.Description));
            children.Add(Utf8(FileNameId,
                string.IsNullOrWhiteSpace(attachment.FileName)
                    ? "attachment.bin"
                    : attachment.FileName));
            children.Add(Ascii(FileMediaTypeId,
                string.IsNullOrWhiteSpace(attachment.ImageType)
                    ? "application/octet-stream"
                    : attachment.ImageType));
            children.Add(Binary(FileDataId, attachment.Data));
            children.Add(UInt(
                FileUidId,
                attachment.Uid == 0 ? NewUid() : attachment.Uid));
            children.AddRange(attachment.UnknownChildren);
            files.Add(Master(AttachedFileId, children));
        }
        return Master(AttachmentsId, files);
    }

    private byte[] BuildChapters()
    {
        if (_chapters.Count == 0)
            return [];
        var atoms = new List<byte[]>();
        foreach (MediaChapter chapter in _chapters)
        {
            var children = new List<byte[]>
            {
                UInt(ChapterUidId, chapter.Uid ?? NewUid()),
                UInt(ChapterTimeStartId, chapter.StartNanoseconds),
            };
            if (chapter.EndNanoseconds.HasValue)
                children.Add(UInt(
                    ChapterTimeEndId,
                    chapter.EndNanoseconds.Value));
            var display = new List<byte[]>
            {
                Utf8(ChapStringId, chapter.Title ?? ""),
            };
            string language = string.IsNullOrWhiteSpace(chapter.Language)
                ? "und"
                : chapter.Language;
            display.Add(Utf8(
                language.Contains('-')
                    ? ChapLanguageBcp47Id
                    : ChapLanguageId,
                language));
            children.Add(Master(ChapterDisplayId, display));
            atoms.Add(Master(ChapterAtomId, children));
        }
        return Master(
            ChaptersId,
            [Master(EditionEntryId, atoms)]);
    }

    private ElementRef SelectSeekSlot(
        IReadOnlyList<(ulong Id, ulong Position)> targets)
    {
        long firstCluster = _children
            .Where(child => child.Id == ClusterId)
            .Select(child => child.Offset)
            .DefaultIfEmpty(_segmentEnd)
            .Min();
        IEnumerable<ElementRef> candidates = _children
            .Where(child =>
                child.Offset < firstCluster &&
                (child.Id == SeekHeadId ||
                 child.Id == VoidId ||
                 IsDirtyMutable(child.Id)))
            .OrderBy(child => child.Id == SeekHeadId ? 0 :
                              child.Id == VoidId ? 1 : 2)
            .ThenBy(child => child.Offset);
        foreach (ElementRef candidate in candidates)
            if (TryBuildSeekHeadExact(
                    targets, candidate.TotalLength, out _))
                return candidate;
        throw new NotSupportedException(
            "The Matroska Segment has no pre-Cluster SeekHead, Void, " +
            "or replaced metadata slot large enough for an updated SeekHead.");
    }

    private bool IsDirtyMutable(ulong id) =>
        (id == InfoId && _infoDirty) ||
        (id == TagsId && _tagsDirty) ||
        (id == AttachmentsId && _attachmentsDirty) ||
        (id == ChaptersId && _chaptersDirty);

    private static byte[] BuildSeekHeadExact(
        IReadOnlyList<(ulong Id, ulong Position)> targets,
        long totalLength)
    {
        if (TryBuildSeekHeadExact(
                targets, totalLength, out byte[] result))
            return result;
        throw new InvalidOperationException(
            "The selected Matroska SeekHead slot is too small.");
    }

    private static bool TryBuildSeekHeadExact(
        IReadOnlyList<(ulong Id, ulong Position)> targets,
        long totalLength,
        out byte[] result)
    {
        result = null;
        if (totalLength > int.MaxValue || totalLength < 6)
            return false;
        for (int positionPadding = 0;
             positionPadding <= targets.Count * 7;
            positionPadding++)
        {
            int remainingPadding = positionPadding;
            var seeks = new List<byte[]>();
            foreach ((ulong id, ulong position) in targets)
            {
                int minimum = UIntWidth(position);
                int extra = Math.Min(8 - minimum, remainingPadding);
                remainingPadding -= extra;
                seeks.Add(Master(
                    0x4DBB,
                    [
                        Binary(0x53AB, IdBytes(id)),
                        UInt(0x53AC, position, minimum + extra),
                    ]));
            }
            if (remainingPadding != 0)
                continue;
            int contentLength = seeks.Sum(seek => seek.Length);
            for (int sizeWidth = 1; sizeWidth <= 8; sizeWidth++)
            {
                long payloadLength =
                    totalLength - IdBytes(SeekHeadId).Length -
                    sizeWidth;
                if (payloadLength < contentLength ||
                    !FitsVint((ulong)payloadLength, sizeWidth))
                    continue;
                long gap = payloadLength - contentLength;
                if (gap == 1)
                    continue;
                using var stream = new MemoryStream(
                    checked((int)totalLength));
                stream.Write(IdBytes(SeekHeadId));
                stream.Write(EncodeVint(
                    checked((ulong)payloadLength), sizeWidth));
                foreach (byte[] seek in seeks)
                    stream.Write(seek);
                if (gap >= 2)
                    stream.Write(BuildVoid(gap));
                result = stream.ToArray();
                return result.Length == totalLength;
            }
        }
        return false;
    }

    private TagGroup GetOrCreateGlobalGroup()
    {
        TagGroup group = _tagGroups.FirstOrDefault(item => item.IsGlobal);
        if (group is not null)
            return group;
        group = new TagGroup(
            true,
            Master(TargetsId, []),
            [],
            []);
        _tagGroups.Add(group);
        return group;
    }

    private IEnumerable<TagValue> GlobalValues() =>
        _tagGroups
            .Where(group => group.IsGlobal)
            .SelectMany(group => group.Values);

    private void RefreshStandardFields()
    {
        Title = "";
        Artist = "";
        AlbumArtist = "";
        Album = "";
        TrackNumber = null;
        TrackTotal = null;
        ReleaseDate = null;
        DiscNumber = null;
        DiscTotal = null;
        HasAlbumArtist = false;
        ParseStandardFields();
    }

    private void EnsureAttachmentsSupported()
    {
        if (_docType.Equals("webm",
                StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                "The WebM subset does not support Attachments.");
    }

    private void ResetCodec()
    {
        CodecName = "Matroska audio";
        CodecType = CodecType.Lossy;
        AverageBitrate = 0;
        MaxBitrate = 0;
        BitsPerSample = 0;
        Samplerate = 0;
        Channels = 0;
        DurationInFrames = 0;
    }

    private static ElementRef ReadElement(Stream stream, long limit)
    {
        long offset = stream.Position;
        (ulong id, int idWidth, _) =
            ReadVint(stream, removeMarker: false, limit);
        (ulong size, int sizeWidth, bool unknown) =
            ReadVint(stream, removeMarker: true, limit);
        long dataOffset = stream.Position;
        long dataLength = unknown
            ? limit - dataOffset
            : checked((long)size);
        if (dataLength < 0 || dataLength > limit - dataOffset)
            throw new InvalidDataException(
                $"EBML element 0x{id:X} exceeds its parent boundary.");
        return new ElementRef(
            id, offset, dataOffset,
            checked(idWidth + sizeWidth),
            dataLength, unknown);
    }

    private static (
        ulong Value,
        int Width,
        bool Unknown) ReadVint(
        Stream stream,
        bool removeMarker,
        long limit)
    {
        if (stream.Position >= limit)
            throw new EndOfStreamException(
                "Truncated EBML variable-length integer.");
        int first = stream.ReadByte();
        if (first <= 0)
            throw new InvalidDataException(
                "Invalid EBML variable-length integer.");
        int width = 1;
        int marker = 0x80;
        while ((first & marker) == 0)
        {
            width++;
            marker >>= 1;
            if (width > 8)
                throw new InvalidDataException(
                    "Invalid EBML variable-length integer.");
        }
        if (stream.Position + width - 1 > limit)
            throw new EndOfStreamException(
                "Truncated EBML variable-length integer.");
        ulong value = removeMarker
            ? checked((ulong)(first & (marker - 1)))
            : checked((ulong)first);
        for (int index = 1; index < width; index++)
        {
            int next = stream.ReadByte();
            if (next < 0)
                throw new EndOfStreamException(
                    "Truncated EBML variable-length integer.");
            value = (value << 8) | checked((byte)next);
        }
        bool unknown = removeMarker &&
            value == MaxVintValue(width);
        return (value, width, unknown);
    }

    private static IEnumerable<MemoryElement> Elements(
        ReadOnlyMemory<byte> payload)
    {
        int offset = 0;
        while (offset < payload.Length)
        {
            int start = offset;
            (ulong id, int idWidth, _) =
                ReadVint(payload.Span, ref offset, removeMarker: false);
            (ulong size, int sizeWidth, bool unknown) =
                ReadVint(payload.Span, ref offset, removeMarker: true);
            if (unknown || size > int.MaxValue ||
                size > checked((ulong)(payload.Length - offset)))
                throw new InvalidDataException(
                    $"Invalid child EBML element 0x{id:X}.");
            int length = checked((int)size);
            yield return new MemoryElement(
                id,
                payload.Slice(start,
                    checked(idWidth + sizeWidth + length)),
                payload.Slice(offset, length));
            offset += length;
        }
    }

    private static (
        ulong Value,
        int Width,
        bool Unknown) ReadVint(
        ReadOnlySpan<byte> data,
        ref int offset,
        bool removeMarker)
    {
        if ((uint)offset >= (uint)data.Length)
            throw new EndOfStreamException(
                "Truncated EBML variable-length integer.");
        int first = data[offset++];
        if (first == 0)
            throw new InvalidDataException(
                "Invalid EBML variable-length integer.");
        int width = 1;
        int marker = 0x80;
        while ((first & marker) == 0)
        {
            width++;
            marker >>= 1;
            if (width > 8)
                throw new InvalidDataException(
                    "Invalid EBML variable-length integer.");
        }
        if (width - 1 > data.Length - offset)
            throw new EndOfStreamException(
                "Truncated EBML variable-length integer.");
        ulong value = removeMarker
            ? checked((ulong)(first & (marker - 1)))
            : checked((ulong)first);
        for (int index = 1; index < width; index++)
            value = (value << 8) | data[offset++];
        bool unknown = removeMarker &&
            value == MaxVintValue(width);
        return (value, width, unknown);
    }

    private static byte[] ReadPayload(
        Stream stream,
        ElementRef element,
        int maximum,
        string name)
    {
        if (element.DataLength > maximum)
            throw new InvalidDataException(
                $"{name} exceeds the supported {maximum}-byte limit.");
        byte[] payload = new byte[checked((int)element.DataLength)];
        stream.Position = element.DataOffset;
        stream.ReadExactly(payload);
        return payload;
    }

    private static ulong ReadUnsigned(ReadOnlyMemory<byte> data)
    {
        if (data.Length is < 1 or > 8)
            throw new InvalidDataException(
                "An EBML unsigned integer must contain 1-8 bytes.");
        ulong value = 0;
        foreach (byte item in data.Span)
            value = (value << 8) | item;
        return value;
    }

    private static double ReadFloat(ReadOnlyMemory<byte> data)
    {
        if (data.Length == 4)
        {
            int bits = BinaryPrimitives.ReadInt32BigEndian(data.Span);
            return BitConverter.Int32BitsToSingle(bits);
        }
        if (data.Length == 8)
        {
            long bits = BinaryPrimitives.ReadInt64BigEndian(data.Span);
            return BitConverter.Int64BitsToDouble(bits);
        }
        throw new InvalidDataException(
            "An EBML float must contain 4 or 8 bytes.");
    }

    private static double ReadNumber(ReadOnlyMemory<byte> data) =>
        data.Length is 4 or 8 ? ReadFloat(data) : ReadUnsigned(data);

    private static string ReadUtf8(ReadOnlyMemory<byte> data) =>
        new UTF8Encoding(false, true).GetString(data.Span);

    private static string ReadAscii(ReadOnlyMemory<byte> data) =>
        Encoding.ASCII.GetString(data.Span);

    private static byte[] Master(
        ulong id,
        IEnumerable<byte[]> children)
    {
        byte[][] values = children.ToArray();
        int length = values.Sum(value => value.Length);
        using var stream = new MemoryStream();
        stream.Write(IdBytes(id));
        stream.Write(EncodeVint(checked((ulong)length)));
        foreach (byte[] value in values)
            stream.Write(value);
        return stream.ToArray();
    }

    private static byte[] Utf8(ulong id, string value) =>
        Binary(id, Encoding.UTF8.GetBytes(value ?? ""));

    private static byte[] Ascii(ulong id, string value) =>
        Binary(id, Encoding.ASCII.GetBytes(value ?? ""));

    private static byte[] UInt(
        ulong id,
        ulong value,
        int width = 0)
    {
        width = width == 0 ? UIntWidth(value) : width;
        if (width is < 1 or > 8 ||
            (width < 8 &&
             value >= (1UL << (width * 8))))
            throw new ArgumentOutOfRangeException(nameof(width));
        byte[] data = new byte[width];
        ulong remaining = value;
        for (int index = width - 1; index >= 0; index--)
        {
            data[index] = (byte)(remaining & 0xff);
            remaining >>= 8;
        }
        return Binary(id, data);
    }

    private static byte[] Binary(ulong id, ReadOnlySpan<byte> data)
    {
        byte[] idBytes = IdBytes(id);
        byte[] size = EncodeVint(checked((ulong)data.Length));
        byte[] result =
            new byte[checked(idBytes.Length + size.Length + data.Length)];
        idBytes.CopyTo(result, 0);
        size.CopyTo(result, idBytes.Length);
        data.CopyTo(result.AsSpan(idBytes.Length + size.Length));
        return result;
    }

    private static byte[] BuildVoid(long totalLength)
    {
        if (totalLength < 2 || totalLength > int.MaxValue)
            throw new InvalidOperationException(
                $"Cannot encode a {totalLength}-byte EBML Void.");
        for (int width = 1; width <= 8; width++)
        {
            long payloadLength = totalLength - 1 - width;
            if (payloadLength < 0 ||
                !FitsVint(checked((ulong)payloadLength), width))
                continue;
            byte[] result = new byte[checked((int)totalLength)];
            result[0] = 0xEC;
            EncodeVint(
                checked((ulong)payloadLength), width)
                .CopyTo(result, 1);
            return result;
        }
        throw new InvalidOperationException(
            $"Cannot encode a {totalLength}-byte EBML Void.");
    }

    private static byte[] EncodeVint(ulong value, int width = 0)
    {
        if (width == 0)
        {
            width = 1;
            while (width <= 8 && !FitsVint(value, width))
                width++;
        }
        if (width > 8 || !FitsVint(value, width))
            throw new ArgumentOutOfRangeException(nameof(value));
        byte[] result = new byte[width];
        ulong remaining = value;
        for (int index = width - 1; index >= 0; index--)
        {
            result[index] = (byte)(remaining & 0xff);
            remaining >>= 8;
        }
        result[0] |= checked((byte)(1 << (8 - width)));
        return result;
    }

    private static bool FitsVint(ulong value, int width) =>
        width is >= 1 and <= 8 &&
        value < MaxVintValue(width);

    private static ulong MaxVintValue(int width) =>
        width == 8
            ? 0x00FFFFFFFFFFFFFFUL
            : (1UL << (width * 7)) - 1;

    private static byte[] IdBytes(ulong id)
    {
        int width = 1;
        ulong test = id;
        while ((test >>= 8) != 0)
            width++;
        byte[] result = new byte[width];
        for (int index = width - 1; index >= 0; index--)
        {
            result[index] = (byte)(id & 0xff);
            id >>= 8;
        }
        return result;
    }

    private static int UIntWidth(ulong value)
    {
        int width = 1;
        while (width < 8 && value >= (1UL << (width * 8)))
            width++;
        return width;
    }

    private static bool TryReadFlacStreamInfo(
        byte[] privateData,
        out uint sampleRate,
        out uint channels,
        out uint bitsPerSample)
    {
        sampleRate = channels = bitsPerSample = 0;
        if (privateData is null)
            return false;
        ReadOnlySpan<byte> data = privateData;
        if (data.Length >= 42 &&
            data[..4].SequenceEqual("fLaC"u8))
            data = data[8..];
        if (data.Length < 18)
            return false;
        ulong packed = BinaryPrimitives.ReadUInt64BigEndian(
            data.Slice(10, 8));
        sampleRate = checked((uint)((packed >> 44) & 0xFFFFF));
        channels = checked((uint)(((packed >> 41) & 0x7) + 1));
        bitsPerSample =
            checked((uint)(((packed >> 36) & 0x1F) + 1));
        return sampleRate != 0;
    }

    private static string NormalizeMime(
        string mimeType,
        ReadOnlySpan<byte> data)
    {
        if (!string.IsNullOrWhiteSpace(mimeType))
        {
            string value = mimeType.Trim().ToLowerInvariant();
            if (value == "jpg")
                return "image/jpeg";
            if (!value.Contains('/'))
                return "image/" + value;
            return value;
        }
        if (data.Length >= 8 &&
            data[..8].SequenceEqual(
                new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return "image/png";
        if (data.Length >= 2 &&
            data[0] == 0xFF && data[1] == 0xD8)
            return "image/jpeg";
        return "application/octet-stream";
    }

    private static string CoverFileName(
        ID3v2Util.APICType type,
        string mime)
    {
        string stem = type switch
        {
            ID3v2Util.APICType.FrontCover => "cover",
            ID3v2Util.APICType.BackCover => "back",
            _ => "cover_" + type.ToString().ToLowerInvariant(),
        };
        string extension = mime.Equals(
            "image/png", StringComparison.OrdinalIgnoreCase)
            ? ".png"
            : mime.Equals(
                "image/jpeg", StringComparison.OrdinalIgnoreCase)
                ? ".jpg"
                : ".bin";
        return stem + extension;
    }

    private static ulong NewUid()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        return value == 0 ? 1UL : value;
    }

    private static string PreferredName(TagFields field) =>
        PreferredNames.TryGetValue(field, out string name)
            ? name
            : field.ToString().ToUpperInvariant();

    private static Dictionary<string, TagFields> CreateMappings()
    {
        var result = new Dictionary<string, TagFields>(
            StringComparer.OrdinalIgnoreCase);
        void Add(TagFields field, params string[] names)
        {
            foreach (string name in names)
                result[name] = field;
        }
        Add(TagFields.Title, "TITLE");
        Add(TagFields.Artist, "ARTIST");
        Add(TagFields.AlbumArtist, "ALBUMARTIST", "ALBUM_ARTIST");
        Add(TagFields.Album, "ALBUM");
        Add(TagFields.TrackNumber,
            "TRACK", "TRACKNUMBER", "TRACK_NUMBER", "PART_NUMBER");
        Add(TagFields.TotalTracks,
            "TRACKTOTAL", "TOTALTRACKS", "TOTAL_TRACKS", "TOTAL_PARTS");
        Add(TagFields.DiscNumber,
            "DISC", "DISCNUMBER", "DISC_NUMBER", "SET_PART");
        Add(TagFields.TotalDiscs,
            "DISCTOTAL", "TOTALDISCS", "TOTAL_DISCS");
        Add(TagFields.Date, "DATE", "YEAR", "DATE_RELEASED");
        Add(TagFields.Genre, "GENRE");
        Add(TagFields.Comment, "COMMENT", "COMMENTS");
        Add(TagFields.Copyright, "COPYRIGHT");
        Add(TagFields.Composer, "COMPOSER");
        Add(TagFields.Conductor, "CONDUCTOR");
        Add(TagFields.Lyrics, "LYRICS", "UNSYNCEDLYRICS");
        Add(TagFields.Label, "LABEL", "PUBLISHER");
        Add(TagFields.EncodedBy, "ENCODED_BY");
        Add(TagFields.EncoderSettings, "ENCODER", "ENCODER_SETTINGS");
        Add(TagFields.BPM, "BPM");
        Add(TagFields.Grouping, "GROUPING", "CONTENT_GROUP");
        Add(TagFields.Key, "KEY", "INITIAL_KEY");
        Add(TagFields.ISRC, "ISRC");
        Add(TagFields.Language, "LANGUAGE");
        Add(TagFields.Mood, "MOOD");
        Add(TagFields.Subtitle, "SUBTITLE");
        Add(TagFields.Writer, "WRITER", "WRITTEN_BY");
        Add(TagFields.Producer, "PRODUCER");
        Add(TagFields.OriginalYear, "ORIGINAL_YEAR");
        Add(TagFields.OriginalDate, "ORIGINAL_DATE", "DATE_RECORDED");
        Add(TagFields.CatalogNumber, "CATALOG_NUMBER", "CATALOGNUMBER");
        Add(TagFields.Barcode, "BARCODE");
        Add(TagFields.Media, "MEDIA");
        Add(TagFields.ReleaseCountry, "RELEASE_COUNTRY", "COUNTRY");
        Add(TagFields.ReplayGain_Track_Gain, "REPLAYGAIN_TRACK_GAIN");
        Add(TagFields.ReplayGain_Track_Peak, "REPLAYGAIN_TRACK_PEAK");
        Add(TagFields.ReplayGain_Album_Gain, "REPLAYGAIN_ALBUM_GAIN");
        Add(TagFields.ReplayGain_Album_Peak, "REPLAYGAIN_ALBUM_PEAK");
        Add(TagFields.MusicBrainz_TrackID, "MUSICBRAINZ_TRACKID");
        Add(TagFields.MusicBrainz_AlbumID, "MUSICBRAINZ_ALBUMID");
        Add(TagFields.MusicBrainz_ArtistID, "MUSICBRAINZ_ARTISTID");
        Add(TagFields.MusicBrainz_AlbumArtistID,
            "MUSICBRAINZ_ALBUMARTISTID");
        Add(TagFields.MusicBrainz_RecordingID,
            "MUSICBRAINZ_RECORDINGID");
        Add(TagFields.MusicBrainz_ReleaseGroupID,
            "MUSICBRAINZ_RELEASEGROUPID");
        return result;
    }

    private static Dictionary<TagFields, string> CreatePreferredNames() =>
        new()
        {
            [TagFields.Title] = "TITLE",
            [TagFields.Artist] = "ARTIST",
            [TagFields.AlbumArtist] = "ALBUMARTIST",
            [TagFields.Album] = "ALBUM",
            [TagFields.TrackNumber] = "TRACKNUMBER",
            [TagFields.TotalTracks] = "TOTALTRACKS",
            [TagFields.DiscNumber] = "DISCNUMBER",
            [TagFields.TotalDiscs] = "TOTALDISCS",
            [TagFields.Date] = "DATE_RELEASED",
            [TagFields.Genre] = "GENRE",
            [TagFields.Comment] = "COMMENT",
            [TagFields.Copyright] = "COPYRIGHT",
            [TagFields.Composer] = "COMPOSER",
            [TagFields.Conductor] = "CONDUCTOR",
            [TagFields.Lyrics] = "LYRICS",
            [TagFields.Label] = "PUBLISHER",
            [TagFields.EncodedBy] = "ENCODED_BY",
            [TagFields.EncoderSettings] = "ENCODER",
            [TagFields.BPM] = "BPM",
            [TagFields.Grouping] = "GROUPING",
            [TagFields.Key] = "INITIAL_KEY",
            [TagFields.ISRC] = "ISRC",
            [TagFields.Language] = "LANGUAGE",
            [TagFields.Mood] = "MOOD",
            [TagFields.Subtitle] = "SUBTITLE",
            [TagFields.Writer] = "WRITTEN_BY",
            [TagFields.Producer] = "PRODUCER",
            [TagFields.OriginalYear] = "ORIGINAL_YEAR",
            [TagFields.OriginalDate] = "DATE_RECORDED",
            [TagFields.CatalogNumber] = "CATALOG_NUMBER",
            [TagFields.Barcode] = "BARCODE",
            [TagFields.Media] = "MEDIA",
            [TagFields.ReleaseCountry] = "RELEASE_COUNTRY",
            [TagFields.ReplayGain_Track_Gain] = "REPLAYGAIN_TRACK_GAIN",
            [TagFields.ReplayGain_Track_Peak] = "REPLAYGAIN_TRACK_PEAK",
            [TagFields.ReplayGain_Album_Gain] = "REPLAYGAIN_ALBUM_GAIN",
            [TagFields.ReplayGain_Album_Peak] = "REPLAYGAIN_ALBUM_PEAK",
        };

    private sealed record ElementRef(
        ulong Id,
        long Offset,
        long DataOffset,
        int HeaderLength,
        long DataLength,
        bool UnknownSize)
    {
        public long TotalLength => checked(HeaderLength + DataLength);
    }

    private sealed record MemoryElement(
        ulong Id,
        ReadOnlyMemory<byte> Raw,
        ReadOnlyMemory<byte> Data);

    private sealed class TagGroup(
        bool isGlobal,
        byte[] targets,
        List<TagValue> values,
        List<byte[]> unknownChildren)
    {
        public bool IsGlobal { get; } = isGlobal;
        public byte[] Targets { get; } = targets;
        public List<TagValue> Values { get; } = values;
        public List<byte[]> UnknownChildren { get; } =
            unknownChildren;
    }

    private sealed class TagValue
    {
        public TagValue(string name, string text)
        {
            Name = name;
            Text = text;
            Modified = true;
        }

        public TagValue(
            string name,
            string text,
            byte[] binary,
            string language,
            bool? isDefault,
            byte[] raw)
        {
            Name = name;
            Text = text;
            Binary = binary;
            Language = language;
            IsDefault = isDefault;
            Raw = raw;
        }

        public string Name { get; }
        public string Text { get; }
        public byte[] Binary { get; }
        public string Language { get; }
        public bool? IsDefault { get; }
        public byte[] Raw { get; }
        public bool Modified { get; }
    }

    private sealed class MatroskaAttachment : IMetadataImage
    {
        private bool _dimensionsRead;
        private int _width;
        private int _height;

        public MatroskaAttachment(
            string fileName,
            string mimeType,
            string description,
            byte[] data,
            ulong uid,
            List<byte[]> unknownChildren)
        {
            FileName = fileName;
            ImageType = mimeType;
            Description = description;
            Data = data;
            Uid = uid;
            UnknownChildren = unknownChildren;
            PictureType = PictureTypeFromName(fileName);
        }

        public static MatroskaAttachment Create(
            ID3v2Util.APICType type,
            string mimeType,
            string description,
            string fileName,
            byte[] data)
        {
            var attachment = new MatroskaAttachment(
                fileName, mimeType, description,
                data.ToArray(), NewUid(), []);
            attachment.PictureType = type;
            return attachment;
        }

        public string FileName { get; }
        public string Description { get; }
        public string Category => PictureType.ToString();
        public string ImageType { get; }
        public int Width
        {
            get
            {
                ReadDimensions();
                return _width;
            }
        }
        public int Height
        {
            get
            {
                ReadDimensions();
                return _height;
            }
        }
        public int Size => Data.Length;
        public byte[] Data { get; }
        public string Hash { get; private set; }
        public ulong Uid { get; }
        public List<byte[]> UnknownChildren { get; }
        public ID3v2Util.APICType PictureType { get; private set; }
        public bool IsImage => ImageType.StartsWith(
            "image/", StringComparison.OrdinalIgnoreCase);

        public void HashImage(HashAlgorithm hash)
        {
            ArgumentNullException.ThrowIfNull(hash);
            Hash = Convert.ToBase64String(hash.ComputeHash(Data));
        }

        private void ReadDimensions()
        {
            if (_dimensionsRead)
                return;
            _dimensionsRead = true;
            (_width, _height) = ImageFile.GetImageDimensions(Data);
        }

        private static ID3v2Util.APICType PictureTypeFromName(
            string fileName)
        {
            string name = Path.GetFileNameWithoutExtension(
                fileName ?? "");
            if (name.StartsWith("back",
                    StringComparison.OrdinalIgnoreCase))
                return ID3v2Util.APICType.BackCover;
            if (name.StartsWith("cover",
                    StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("front",
                    StringComparison.OrdinalIgnoreCase))
                return ID3v2Util.APICType.FrontCover;
            return ID3v2Util.APICType.Other;
        }
    }
}
