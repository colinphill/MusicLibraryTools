using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MusicFileUtilities;

/// <summary>
/// Native ASF/WMA reader and writer. Metadata objects live in the ASF Header Object, so saves
/// rebuild only that object and copy the Data Object and any following indexes byte-for-byte.
/// </summary>
public sealed class AsfFile :
    IMediaFile,
    ICodecProvider,
    IMetadataWriter,
    IUserStringMetadata,
    IArtworkWriter
{
    internal static readonly Guid HeaderObject =
        new("75b22630-668e-11cf-a6d9-00aa0062ce6c");
    internal static readonly Guid DataObject =
        new("75b22636-668e-11cf-a6d9-00aa0062ce6c");
    private static readonly Guid FilePropertiesObject =
        new("8cabdca1-a947-11cf-8ee4-00c00c205365");
    private static readonly Guid StreamPropertiesObject =
        new("b7dc0791-a9b7-11cf-8ee6-00c00c205365");
    private static readonly Guid ContentDescriptionObject =
        new("75b22633-668e-11cf-a6d9-00aa0062ce6c");
    private static readonly Guid ExtendedContentDescriptionObject =
        new("d2d0a440-e307-11d2-97f0-00a0c95ea850");
    private static readonly Guid HeaderExtensionObject =
        new("5fbf03b5-a92e-11cf-8ee3-00c00c205365");
    private static readonly Guid MetadataObject =
        new("c5f8cbea-5baf-4877-8467-aa8c44fa4cca");
    private static readonly Guid MetadataLibraryObject =
        new("44231c94-9498-49d1-a141-1d134e457054");
    private static readonly Guid AudioMediaType =
        new("f8699e40-5b4d-11cf-a8fd-00805f5c442b");
    private static readonly Guid HeaderExtensionReserved =
        new("11d2d3ab-baa9-cf11-8ee6-00c00c205365");

    private readonly List<AsfObject> _headerChildren = [];
    private readonly List<AsfObject> _extensionChildren = [];
    private string _filename;
    private long _headerLength;
    private byte[] _headerExtensionPrefix;
    private byte[] _headerExtensionTrailingBytes = [];
    private byte[] _headerTrailingBytes = [];
    private bool _hadHeaderExtension;
    private bool _hasAudio;
    private bool _readArtwork;
    private AsfTag _tag;

    public AsfFile(
        string filename,
        bool readArtwork = true,
        long? knownLength = null)
    {
        _filename = filename ??
            throw new ArgumentNullException(nameof(filename));
        _readArtwork = readArtwork;
        Parse(knownLength);
    }

    public IEnumerable<ICodecProvider> Codecs
    {
        get
        {
            if (_hasAudio)
                yield return this;
        }
    }

    public IEnumerable<IMetadataProvider> Tags
    {
        get { yield return _tag; }
    }

    public string CodecName { get; private set; } = "";
    public CodecType CodecType { get; private set; } = CodecType.Lossy;
    public uint AverageBitrate { get; private set; }
    public uint MaxBitrate { get; private set; }
    public uint BitsPerSample { get; private set; }
    public uint Samplerate { get; private set; }
    public uint Channels { get; private set; }
    public uint DurationInFrames { get; private set; }
    public uint DurationInSeconds => DurationInFrames / 75;

    public void SetField(TagFields field, string value) =>
        _tag.SetField(field, value);

    public void RemoveField(TagFields field) =>
        _tag.RemoveField(field);

    public IEnumerable<KeyValuePair<string, string>> GetUserStrings() =>
        _tag.GetUserStrings();

    public void SetUserString(string key, string value) =>
        _tag.SetUserString(key, value);

    public void RemoveUserString(string key) =>
        _tag.RemoveUserString(key);

    public void SetFrontCover(byte[] imageData, string mimeType) =>
        _tag.SetFrontCover(imageData, mimeType);

    public void RemoveImages() =>
        _tag.RemoveImages();

    public void SetImages(IReadOnlyList<ArtworkImage> images) =>
        _tag.SetImages(images);

    public void SaveTags(string outputPath = null) =>
        Save(outputPath);

    public void Save(string outputPath = null)
    {
        string target = outputPath ?? _filename ??
            throw new InvalidOperationException(
                "No filename associated with this file.");
        string sourcePath = _filename;
        long sourceLength = new FileInfo(sourcePath).Length;
        byte[] header = BuildHeader(
            checked(sourceLength - _headerLength));
        string tempPath = Tools.CreateSiblingTempPath(target);
        try
        {
            using (FileStream source = Tools.OpenReadSequential(sourcePath))
            using (FileStream destination =
                   Tools.CreateWriteSequential(tempPath))
            {
                destination.Write(header);
                source.Position = _headerLength;
                Tools.CopyToEnd(source, destination);
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
        _headerChildren.Clear();
        _extensionChildren.Clear();
        _headerExtensionPrefix = null;
        _headerExtensionTrailingBytes = [];
        _headerTrailingBytes = [];
        _hadHeaderExtension = false;
        ResetCodec();

        using FileStream stream = Tools.OpenReadSequential(_filename);
        long fileLength = knownLength ?? stream.Length;
        if (fileLength < 30)
            throw new InvalidDataException("Truncated ASF Header Object.");

        byte[] fixedHeader = new byte[30];
        stream.ReadExactly(fixedHeader);
        if (ReadGuid(fixedHeader) != HeaderObject)
            throw new InvalidDataException(
                "The file does not begin with an ASF Header Object.");
        ulong declaredHeaderSize =
            BinaryPrimitives.ReadUInt64LittleEndian(fixedHeader.AsSpan(16));
        if (declaredHeaderSize < 30 ||
            declaredHeaderSize > (ulong)fileLength ||
            declaredHeaderSize > int.MaxValue)
            throw new InvalidDataException("Invalid ASF Header Object size.");
        _headerLength = checked((long)declaredHeaderSize);
        uint childCount =
            BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader.AsSpan(24));
        if (childCount > 1_000_000)
            throw new InvalidDataException(
                "Invalid ASF Header Object child count.");

        byte[] header = new byte[checked((int)_headerLength)];
        fixedHeader.CopyTo(header, 0);
        stream.ReadExactly(header.AsSpan(30));
        _tag = new AsfTag(this);

        int offset = 30;
        for (uint index = 0; index < childCount; index++)
        {
            AsfObject child = ReadObject(header, ref offset, header.Length);
            _headerChildren.Add(child);
            ParseHeaderChild(child);
        }
        _headerTrailingBytes = header.AsSpan(offset).ToArray();
        _tag.RefreshStandardFields();
    }

    private void ParseHeaderChild(AsfObject child)
    {
        if (child.Id == FilePropertiesObject)
        {
            ParseFileProperties(child.Raw);
            return;
        }
        if (child.Id == StreamPropertiesObject)
        {
            ParseStreamProperties(child.Raw);
            return;
        }
        if (child.Id == ContentDescriptionObject)
        {
            _tag.ReadContentDescription(child.Raw);
            return;
        }
        if (child.Id == ExtendedContentDescriptionObject)
        {
            _tag.ReadExtendedContentDescription(child.Raw);
            return;
        }
        if (child.Id is var metadataId &&
            (metadataId == MetadataObject ||
             metadataId == MetadataLibraryObject))
        {
            _tag.ReadMetadataDescriptors(
                child.Raw,
                child.Id == MetadataObject
                    ? AsfAttributeSource.Metadata
                    : AsfAttributeSource.MetadataLibrary);
            return;
        }
        if (child.Id != HeaderExtensionObject || _hadHeaderExtension)
            return;

        _hadHeaderExtension = true;
        if (child.Raw.Length < 46)
            throw new InvalidDataException(
                "Truncated ASF Header Extension Object.");
        _headerExtensionPrefix = child.Raw.AsSpan(24, 22).ToArray();
        uint extensionSize = BinaryPrimitives.ReadUInt32LittleEndian(
            child.Raw.AsSpan(42, 4));
        if (extensionSize > child.Raw.Length - 46)
            throw new InvalidDataException(
                "Invalid ASF Header Extension data size.");
        int nestedOffset = 46;
        int nestedEnd = checked(46 + (int)extensionSize);
        while (nestedOffset < nestedEnd)
        {
            AsfObject nested =
                ReadObject(child.Raw, ref nestedOffset, nestedEnd);
            _extensionChildren.Add(nested);
            if (nested.Id == MetadataLibraryObject)
                _tag.ReadMetadataDescriptors(
                    nested.Raw, AsfAttributeSource.MetadataLibrary);
            else if (nested.Id == MetadataObject)
                _tag.ReadMetadataDescriptors(
                    nested.Raw, AsfAttributeSource.Metadata);
        }
        if (nestedOffset != nestedEnd)
            throw new InvalidDataException(
                "Invalid ASF Header Extension boundary.");
        _headerExtensionTrailingBytes =
            child.Raw.AsSpan(nestedEnd).ToArray();
    }

    private void ParseFileProperties(byte[] raw)
    {
        if (raw.Length < 104)
            throw new InvalidDataException(
                "Truncated ASF File Properties Object.");
        ulong playDuration =
            BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(64, 8));
        ulong preroll =
            BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(80, 8));
        ulong prerollDuration = preroll > ulong.MaxValue / 10_000
            ? ulong.MaxValue
            : preroll * 10_000;
        ulong duration = playDuration > prerollDuration
            ? playDuration - prerollDuration
            : 0;
        ulong frames =
            duration / 10_000_000 * 75 +
            duration % 10_000_000 * 75 / 10_000_000;
        DurationInFrames = checked((uint)Math.Min(uint.MaxValue, frames));
        MaxBitrate =
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(100, 4));
    }

    private void ParseStreamProperties(byte[] raw)
    {
        if (!string.IsNullOrEmpty(CodecName))
            return;
        if (raw.Length < 78 || ReadGuid(raw.AsSpan(24, 16)) != AudioMediaType)
            return;
        uint formatLength =
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(64, 4));
        if (formatLength < 16 || formatLength > raw.Length - 78)
            throw new InvalidDataException(
                "Invalid ASF audio stream format length.");
        ReadOnlySpan<byte> format = raw.AsSpan(78, checked((int)formatLength));
        ushort formatTag =
            BinaryPrimitives.ReadUInt16LittleEndian(format);
        Channels =
            BinaryPrimitives.ReadUInt16LittleEndian(format.Slice(2));
        Samplerate =
            BinaryPrimitives.ReadUInt32LittleEndian(format.Slice(4));
        uint bytesPerSecond =
            BinaryPrimitives.ReadUInt32LittleEndian(format.Slice(8));
        BitsPerSample = format.Length >= 16
            ? (uint)BinaryPrimitives.ReadUInt16LittleEndian(format.Slice(14))
            : 0u;
        AverageBitrate = bytesPerSecond > uint.MaxValue / 8
            ? uint.MaxValue
            : bytesPerSecond * 8;
        if (MaxBitrate == 0)
            MaxBitrate = AverageBitrate;
        (CodecName, CodecType) = formatTag switch
        {
            0x0160 => ("Windows Media Audio 1", CodecType.Lossy),
            0x0161 => ("Windows Media Audio 2", CodecType.Lossy),
            0x0162 => ("Windows Media Audio Professional", CodecType.Lossy),
            0x0163 => ("Windows Media Audio Lossless", CodecType.Lossless),
            0x000a => ("Windows Media Audio Voice", CodecType.Lossy),
            0x0050 => ("MPEG Layer 2", CodecType.Lossy),
            0x0055 => ("MPEG Layer 3", CodecType.Lossy),
            0x0001 => ("PCM", CodecType.Lossless),
            _ => ($"ASF audio (0x{formatTag:x4})", CodecType.Lossy),
        };
        _hasAudio = true;
    }

    private void ResetCodec()
    {
        CodecName = "";
        _hasAudio = false;
        CodecType = CodecType.Lossy;
        AverageBitrate = 0;
        MaxBitrate = 0;
        BitsPerSample = 0;
        Samplerate = 0;
        Channels = 0;
        DurationInFrames = 0;
    }

    private byte[] BuildHeader(long postHeaderLength)
    {
        byte[] content = _tag.BuildContentDescription();
        byte[] extended = _tag.BuildExtendedContentDescription();
        byte[] extension = BuildHeaderExtension();
        var children = new List<byte[]>();
        bool wroteContent = false;
        bool wroteExtended = false;
        bool wroteExtension = false;

        foreach (AsfObject child in _headerChildren)
        {
            if (child.Id == ContentDescriptionObject)
            {
                if (!wroteContent && content is not null)
                    children.Add(content);
                wroteContent = true;
            }
            else if (child.Id == ExtendedContentDescriptionObject)
            {
                if (!wroteExtended && extended is not null)
                    children.Add(extended);
                wroteExtended = true;
            }
            else if (child.Id == HeaderExtensionObject && !wroteExtension)
            {
                if (extension is not null)
                    children.Add(extension);
                wroteExtension = true;
            }
            else if (child.Id != MetadataObject &&
                     child.Id != MetadataLibraryObject)
            {
                children.Add(child.Raw.ToArray());
            }
        }
        if (!wroteContent && content is not null)
            children.Add(content);
        if (!wroteExtended && extended is not null)
            children.Add(extended);
        if (!wroteExtension && extension is not null)
            children.Add(extension);

        long headerLength =
            30L +
            children.Sum(value => (long)value.Length) +
            _headerTrailingBytes.Length;
        long totalLength = checked(headerLength + postHeaderLength);
        foreach (byte[] child in children)
        {
            if (ReadGuid(child) == FilePropertiesObject && child.Length >= 48)
                BinaryPrimitives.WriteUInt64LittleEndian(
                    child.AsSpan(40, 8), checked((ulong)totalLength));
        }

        byte[] result = new byte[checked((int)headerLength)];
        WriteGuid(result, HeaderObject);
        BinaryPrimitives.WriteUInt64LittleEndian(
            result.AsSpan(16, 8), checked((ulong)headerLength));
        BinaryPrimitives.WriteUInt32LittleEndian(
            result.AsSpan(24, 4), checked((uint)children.Count));
        result[28] = 1;
        result[29] = 2;
        int offset = 30;
        foreach (byte[] child in children)
        {
            child.CopyTo(result, offset);
            offset += child.Length;
        }
        _headerTrailingBytes.CopyTo(result, offset);
        return result;
    }

    private byte[] BuildHeaderExtension()
    {
        byte[] metadata = _tag.BuildMetadataObject();
        byte[] library = _tag.BuildMetadataLibrary();
        if (!_hadHeaderExtension && metadata is null && library is null)
            return null;

        var nested = new List<byte[]>();
        bool wroteMetadata = false;
        bool wroteLibrary = false;
        foreach (AsfObject child in _extensionChildren)
        {
            if (child.Id == MetadataObject)
            {
                if (!wroteMetadata && metadata is not null)
                    nested.Add(metadata);
                wroteMetadata = true;
            }
            else if (child.Id == MetadataLibraryObject)
            {
                if (!wroteLibrary && library is not null)
                    nested.Add(library);
                wroteLibrary = true;
            }
            else
            {
                nested.Add(child.Raw.ToArray());
            }
        }
        if (!wroteMetadata && metadata is not null)
            nested.Add(metadata);
        if (!wroteLibrary && library is not null)
            nested.Add(library);

        byte[] prefix = _headerExtensionPrefix?.ToArray() ?? new byte[22];
        if (_headerExtensionPrefix is null)
        {
            WriteGuid(prefix, HeaderExtensionReserved);
            BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(16, 2), 6);
        }
        long dataLengthLong =
            nested.Sum(value => (long)value.Length);
        if (dataLengthLong > int.MaxValue)
            throw new InvalidOperationException(
                "ASF Header Extension metadata is too large.");
        int dataLength = checked((int)dataLengthLong);
        BinaryPrimitives.WriteUInt32LittleEndian(
            prefix.AsSpan(18, 4), checked((uint)dataLength));
        byte[] payload = new byte[checked(
            prefix.Length +
            dataLength +
            _headerExtensionTrailingBytes.Length)];
        prefix.CopyTo(payload, 0);
        int offset = prefix.Length;
        foreach (byte[] child in nested)
        {
            child.CopyTo(payload, offset);
            offset += child.Length;
        }
        _headerExtensionTrailingBytes.CopyTo(payload, offset);
        return BuildObject(HeaderExtensionObject, payload);
    }

    private static AsfObject ReadObject(
        byte[] source,
        ref int offset,
        int end)
    {
        if (offset < 0 || end - offset < 24)
            throw new InvalidDataException("Truncated ASF object.");
        ulong size =
            BinaryPrimitives.ReadUInt64LittleEndian(
                source.AsSpan(offset + 16, 8));
        if (size < 24 || size > (ulong)(end - offset) || size > int.MaxValue)
            throw new InvalidDataException("Invalid ASF object size.");
        int length = checked((int)size);
        byte[] raw = source.AsSpan(offset, length).ToArray();
        offset += length;
        return new AsfObject(ReadGuid(raw), raw);
    }

    internal static byte[] BuildObject(Guid id, ReadOnlySpan<byte> payload)
    {
        byte[] result = new byte[checked(24 + payload.Length)];
        WriteGuid(result, id);
        BinaryPrimitives.WriteUInt64LittleEndian(
            result.AsSpan(16, 8), checked((ulong)result.Length));
        payload.CopyTo(result.AsSpan(24));
        return result;
    }

    internal static Guid ReadGuid(ReadOnlySpan<byte> bytes) =>
        new(bytes[..16]);

    internal static void WriteGuid(Span<byte> destination, Guid value) =>
        value.TryWriteBytes(destination[..16]);

    private sealed record AsfObject(Guid Id, byte[] Raw);

    internal bool ReadArtwork => _readArtwork;

    internal sealed class AsfTag :
        TagBase,
        IMetadataWriter,
        IUserStringMetadata,
        IArtworkWriter
    {
        private const ushort UnicodeType = 0;
        private const ushort ByteArrayType = 1;
        private readonly AsfFile _owner;
        private readonly List<AsfAttribute> _attributes = [];

        private static readonly Dictionary<string, TagFields> Mappings =
            CreateMappings();

        private static readonly Dictionary<TagFields, string> PreferredKeys =
            new()
            {
                [TagFields.Title] = "Title",
                [TagFields.Artist] = "Author",
                [TagFields.Album] = "WM/AlbumTitle",
                [TagFields.AlbumArtist] = "WM/AlbumArtist",
                [TagFields.TrackNumber] = "WM/TrackNumber",
                [TagFields.DiscNumber] = "WM/PartOfSet",
                [TagFields.Date] = "WM/Year",
                [TagFields.Genre] = "WM/Genre",
                [TagFields.Comment] = "Description",
                [TagFields.Copyright] = "Copyright",
                [TagFields.Rating] = "Rating",
                [TagFields.Composer] = "WM/Composer",
                [TagFields.Conductor] = "WM/Conductor",
                [TagFields.Lyrics] = "WM/Lyrics",
                [TagFields.Label] = "WM/Publisher",
                [TagFields.EncodedBy] = "WM/EncodedBy",
                [TagFields.EncoderSettings] = "WM/EncodingSettings",
                [TagFields.BPM] = "WM/BeatsPerMinute",
                [TagFields.Grouping] = "WM/ContentGroupDescription",
                [TagFields.Key] = "WM/InitialKey",
                [TagFields.ISRC] = "WM/ISRC",
                [TagFields.Language] = "WM/Language",
                [TagFields.Mood] = "WM/Mood",
                [TagFields.Subtitle] = "WM/SubTitle",
                [TagFields.Writer] = "WM/Writer",
                [TagFields.Producer] = "WM/Producer",
                [TagFields.OriginalYear] = "WM/OriginalReleaseYear",
            };

        internal AsfTag(AsfFile owner) =>
            _owner = owner;

        public override string TagType => "ASF";

        public override IEnumerable<KeyValuePair<TagFields, string>>
            GetKnownMetadata()
        {
            foreach (AsfAttribute attribute in _attributes)
            {
                if (!attribute.TryGetString(out string value) ||
                    !TryMap(attribute.Name, out TagFields field))
                    continue;
                if (field is TagFields.TrackNumber or
                    TagFields.DiscNumber or
                    TagFields.MovementNumber)
                {
                    string[] parts = value.Split('/', 2);
                    yield return KeyValuePair.Create(field, parts[0]);
                    if (parts.Length == 2)
                    {
                        TagFields total = field switch
                        {
                            TagFields.TrackNumber => TagFields.TotalTracks,
                            TagFields.DiscNumber => TagFields.TotalDiscs,
                            _ => TagFields.MovementTotal,
                        };
                        yield return KeyValuePair.Create(total, parts[1]);
                    }
                }
                else
                {
                    yield return KeyValuePair.Create(field, value);
                }
            }
        }

        public override IEnumerable<KeyValuePair<string, string>>
            GetTextMetadata()
        {
            foreach (KeyValuePair<TagFields, string> field in
                     GetKnownMetadata())
                yield return KeyValuePair.Create(
                    field.Key.ToString(), field.Value);
        }

        public IEnumerable<KeyValuePair<string, string>> GetUserStrings()
        {
            foreach (AsfAttribute attribute in _attributes)
            {
                if (attribute.TryGetString(out string value) &&
                    !TryMap(attribute.Name, out _) &&
                    !attribute.Name.Equals(
                        "WM/Picture",
                        StringComparison.OrdinalIgnoreCase))
                    yield return KeyValuePair.Create(attribute.Name, value);
            }
        }

        public override IEnumerable<IMetadataImage> GetImageMetadata()
        {
            if (!_owner.ReadArtwork)
                yield break;
            foreach (AsfAttribute attribute in _attributes)
            {
                if (attribute.Type == ByteArrayType &&
                    attribute.Name.Equals(
                        "WM/Picture",
                        StringComparison.OrdinalIgnoreCase) &&
                    AsfArtwork.TryCreate(attribute.Value, out AsfArtwork image))
                    yield return image;
            }
        }

        public void SetField(TagFields field, string value)
        {
            if (field == TagFields.NullField)
                throw new ArgumentException(
                    "NullField is not writable.", nameof(field));
            if (field is TagFields.TrackNumber or TagFields.TotalTracks)
            {
                SetNumberingField(
                    field, value, TagFields.TrackNumber,
                    TagFields.TotalTracks, "WM/TrackNumber");
                return;
            }
            if (field is TagFields.DiscNumber or TagFields.TotalDiscs)
            {
                SetNumberingField(
                    field, value, TagFields.DiscNumber,
                    TagFields.TotalDiscs, "WM/PartOfSet");
                return;
            }
            if (field is TagFields.MovementNumber or TagFields.MovementTotal)
            {
                SetNumberingField(
                    field, value, TagFields.MovementNumber,
                    TagFields.MovementTotal, "WM/MovementNumber");
                return;
            }

            _attributes.RemoveAll(attribute =>
                TryMap(attribute.Name, out TagFields mapped) &&
                mapped == field);
            if (value is not null)
            {
                string key = PreferredKey(field);
                _attributes.Add(AsfAttribute.String(
                    key,
                    IsContentDescriptionKey(key)
                        ? AsfAttributeSource.Content
                        : AsfAttributeSource.Extended,
                    value));
            }
            RefreshStandardFields();
        }

        public void RemoveField(TagFields field) =>
            SetField(field, null);

        public void SetUserString(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException(
                    "A user-string key is required.", nameof(key));
            key = key.Trim();
            _attributes.RemoveAll(attribute =>
                attribute.Name.Equals(
                    key, StringComparison.OrdinalIgnoreCase));
            if (value is not null)
                _attributes.Add(AsfAttribute.String(
                    key, AsfAttributeSource.Extended, value));
            RefreshStandardFields();
        }

        public void RemoveUserString(string key) =>
            SetUserString(key, null);

        public void SetFrontCover(byte[] imageData, string mimeType)
        {
            if (imageData is null || imageData.Length == 0)
            {
                _attributes.RemoveAll(IsFrontCover);
                return;
            }
            _attributes.RemoveAll(IsFrontCover);
            _attributes.Add(AsfAttribute.Binary(
                "WM/Picture",
                AsfAttributeSource.MetadataLibrary,
                BuildPicture(
                    new ArtworkImage(
                        ID3v2Util.APICType.FrontCover,
                        NormalizeMime(mimeType, imageData),
                        "",
                        imageData))));
        }

        public void RemoveImages() =>
            _attributes.RemoveAll(attribute =>
                attribute.Name.Equals(
                    "WM/Picture",
                    StringComparison.OrdinalIgnoreCase));

        public void SetImages(IReadOnlyList<ArtworkImage> images)
        {
            ArgumentNullException.ThrowIfNull(images);
            RemoveImages();
            foreach (ArtworkImage image in images)
            {
                if (image?.Data is null || image.Data.Length == 0)
                    continue;
                _attributes.Add(AsfAttribute.Binary(
                    "WM/Picture",
                    AsfAttributeSource.MetadataLibrary,
                    BuildPicture(image with
                    {
                        MimeType = NormalizeMime(
                            image.MimeType, image.Data),
                    })));
            }
        }

        public void Save(string outputPath = null) =>
            _owner.Save(outputPath);

        internal void ReadContentDescription(byte[] raw)
        {
            if (raw.Length < 34)
                throw new InvalidDataException(
                    "Truncated ASF Content Description Object.");
            ushort[] lengths = new ushort[5];
            for (int index = 0; index < lengths.Length; index++)
                lengths[index] = BinaryPrimitives.ReadUInt16LittleEndian(
                    raw.AsSpan(24 + index * 2, 2));
            string[] names =
                ["Title", "Author", "Copyright", "Description", "Rating"];
            int offset = 34;
            for (int index = 0; index < names.Length; index++)
            {
                int length = lengths[index];
                EnsureAvailable(raw, offset, length);
                if (length > 0)
                    _attributes.Add(new AsfAttribute(
                        names[index],
                        raw.AsSpan(offset, length).ToArray(),
                        UnicodeType,
                        raw.AsSpan(offset, length).ToArray(),
                        AsfAttributeSource.Content));
                offset += length;
            }
        }

        internal void ReadExtendedContentDescription(byte[] raw)
        {
            if (raw.Length < 26)
                throw new InvalidDataException(
                    "Truncated ASF Extended Content Description Object.");
            int offset = 24;
            ushort count =
                BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(offset, 2));
            offset += 2;
            for (int index = 0; index < count; index++)
            {
                EnsureAvailable(raw, offset, 2);
                ushort nameLength =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        raw.AsSpan(offset, 2));
                offset += 2;
                EnsureAvailable(raw, offset, nameLength + 4);
                byte[] nameBytes =
                    raw.AsSpan(offset, nameLength).ToArray();
                string name = DecodeUnicode(nameBytes);
                offset += nameLength;
                ushort type =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        raw.AsSpan(offset, 2));
                ushort valueLength =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        raw.AsSpan(offset + 2, 2));
                offset += 4;
                EnsureAvailable(raw, offset, valueLength);
                _attributes.Add(new AsfAttribute(
                    name,
                    nameBytes,
                    type,
                    raw.AsSpan(offset, valueLength).ToArray(),
                    AsfAttributeSource.Extended));
                offset += valueLength;
            }
        }

        internal void ReadMetadataDescriptors(
            byte[] raw,
            AsfAttributeSource source)
        {
            if (raw.Length < 26)
                throw new InvalidDataException(
                    "Truncated ASF Metadata Object.");
            int offset = 24;
            ushort count =
                BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(offset, 2));
            offset += 2;
            for (int index = 0; index < count; index++)
            {
                EnsureAvailable(raw, offset, 12);
                ushort language =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        raw.AsSpan(offset, 2));
                ushort stream =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        raw.AsSpan(offset + 2, 2));
                ushort nameLength =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        raw.AsSpan(offset + 4, 2));
                ushort type =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        raw.AsSpan(offset + 6, 2));
                uint valueLength =
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        raw.AsSpan(offset + 8, 4));
                offset += 12;
                if (valueLength > int.MaxValue)
                    throw new InvalidDataException(
                        "Oversized ASF metadata value.");
                long descriptorLength =
                    (long)nameLength + valueLength;
                if (descriptorLength > int.MaxValue)
                    throw new InvalidDataException(
                        "Oversized ASF metadata descriptor.");
                EnsureAvailable(
                    raw, offset, checked((int)descriptorLength));
                byte[] nameBytes =
                    raw.AsSpan(offset, nameLength).ToArray();
                string name = DecodeUnicode(nameBytes);
                offset += nameLength;
                _attributes.Add(new AsfAttribute(
                    name,
                    nameBytes,
                    type,
                    raw.AsSpan(offset, checked((int)valueLength)).ToArray(),
                    source,
                    language,
                    stream));
                offset += checked((int)valueLength);
            }
        }

        internal byte[] BuildContentDescription()
        {
            string[] names =
                ["Title", "Author", "Copyright", "Description", "Rating"];
            byte[][] values = names.Select(name =>
                    _attributes.FirstOrDefault(attribute =>
                        attribute.Source == AsfAttributeSource.Content &&
                        attribute.Name.Equals(
                            name, StringComparison.OrdinalIgnoreCase))
                    ?.Value)
                .ToArray();
            if (values.All(value => value is null))
                return null;
            int payloadLength =
                10 + values.Sum(value => value?.Length ?? 0);
            byte[] payload = new byte[payloadLength];
            int offset = 10;
            for (int index = 0; index < values.Length; index++)
            {
                byte[] value = values[index] ?? [];
                if (value.Length > ushort.MaxValue)
                    throw new InvalidOperationException(
                        $"ASF Content Description value '{names[index]}' " +
                        "is too large.");
                BinaryPrimitives.WriteUInt16LittleEndian(
                    payload.AsSpan(index * 2, 2),
                    checked((ushort)value.Length));
                value.CopyTo(payload, offset);
                offset += value.Length;
            }
            return BuildObject(ContentDescriptionObject, payload);
        }

        internal byte[] BuildExtendedContentDescription()
        {
            AsfAttribute[] attributes = _attributes
                .Where(attribute =>
                    attribute.Source == AsfAttributeSource.Extended)
                .ToArray();
            if (attributes.Length == 0)
                return null;
            if (attributes.Length > ushort.MaxValue)
                throw new InvalidOperationException(
                    "Too many ASF Extended Content descriptors.");
            using var payload = new MemoryStream();
            using var writer = new BinaryWriter(
                payload, Encoding.Unicode, leaveOpen: true);
            writer.Write(checked((ushort)attributes.Length));
            foreach (AsfAttribute attribute in attributes)
            {
                if (attribute.NameBytes.Length > ushort.MaxValue ||
                    attribute.Value.Length > ushort.MaxValue)
                    throw new InvalidOperationException(
                        $"ASF attribute '{attribute.Name}' is too large " +
                        "for Extended Content Description.");
                writer.Write(checked((ushort)attribute.NameBytes.Length));
                writer.Write(attribute.NameBytes);
                writer.Write(attribute.Type);
                writer.Write(checked((ushort)attribute.Value.Length));
                writer.Write(attribute.Value);
            }
            return BuildObject(
                ExtendedContentDescriptionObject, payload.ToArray());
        }

        internal byte[] BuildMetadataLibrary()
        {
            return BuildMetadataDescriptors(
                AsfAttributeSource.MetadataLibrary,
                MetadataLibraryObject);
        }

        internal byte[] BuildMetadataObject()
        {
            return BuildMetadataDescriptors(
                AsfAttributeSource.Metadata,
                MetadataObject);
        }

        private byte[] BuildMetadataDescriptors(
            AsfAttributeSource source,
            Guid objectId)
        {
            AsfAttribute[] attributes = _attributes
                .Where(attribute =>
                    attribute.Source == source)
                .ToArray();
            if (attributes.Length == 0)
                return null;
            if (attributes.Length > ushort.MaxValue)
                throw new InvalidOperationException(
                    "Too many ASF Metadata Library descriptors.");
            using var payload = new MemoryStream();
            using var writer = new BinaryWriter(
                payload, Encoding.Unicode, leaveOpen: true);
            writer.Write(checked((ushort)attributes.Length));
            foreach (AsfAttribute attribute in attributes)
            {
                if (attribute.NameBytes.Length > ushort.MaxValue)
                    throw new InvalidOperationException(
                        $"ASF attribute name '{attribute.Name}' is too large.");
                writer.Write(attribute.Language);
                writer.Write(attribute.Stream);
                writer.Write(checked((ushort)attribute.NameBytes.Length));
                writer.Write(attribute.Type);
                writer.Write(checked((uint)attribute.Value.Length));
                writer.Write(attribute.NameBytes);
                writer.Write(attribute.Value);
            }
            return BuildObject(objectId, payload.ToArray());
        }

        internal void RefreshStandardFields()
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

        private void SetNumberingField(
            TagFields field,
            string value,
            TagFields numberField,
            TagFields totalField,
            string key)
        {
            string number = "";
            string total = "";
            AsfAttribute existing = _attributes.FirstOrDefault(attribute =>
                TryMap(attribute.Name, out TagFields mapped) &&
                mapped == numberField &&
                attribute.TryGetString(out _));
            if (existing is not null &&
                existing.TryGetString(out string current))
            {
                string[] parts = current.Split('/', 2);
                number = parts[0];
                if (parts.Length == 2)
                    total = parts[1];
            }
            if (field == numberField)
                number = value ?? "";
            else if (field == totalField)
                total = value ?? "";
            _attributes.RemoveAll(attribute =>
                TryMap(attribute.Name, out TagFields mapped) &&
                mapped == numberField);
            string combined = string.IsNullOrEmpty(total)
                ? number
                : number + "/" + total;
            if (!string.IsNullOrEmpty(combined))
                _attributes.Add(AsfAttribute.String(
                    key, AsfAttributeSource.Extended, combined));
            RefreshStandardFields();
        }

        private bool IsFrontCover(AsfAttribute attribute)
        {
            if (!attribute.Name.Equals(
                    "WM/Picture",
                    StringComparison.OrdinalIgnoreCase) ||
                attribute.Type != ByteArrayType)
                return false;
            return AsfArtwork.TryCreate(
                       attribute.Value, out AsfArtwork image) &&
                   image.PictureType == ID3v2Util.APICType.FrontCover;
        }

        private static byte[] BuildPicture(ArtworkImage image)
        {
            byte[] mime = NullTerminatedUnicode(image.MimeType ?? "");
            byte[] description =
                NullTerminatedUnicode(image.Description ?? "");
            byte[] result = new byte[checked(
                5 + mime.Length + description.Length + image.Data.Length)];
            result[0] = checked((byte)image.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(
                result.AsSpan(1, 4), checked((uint)image.Data.Length));
            mime.CopyTo(result, 5);
            description.CopyTo(result, 5 + mime.Length);
            image.Data.CopyTo(result, 5 + mime.Length + description.Length);
            return result;
        }

        private static string NormalizeMime(
            string mimeType,
            byte[] data)
        {
            if (!string.IsNullOrWhiteSpace(mimeType))
                return mimeType.Trim();
            return ImageFile.DetectImageFormat(data) switch
            {
                ImageFile.ImageFormat.Png => "image/png",
                ImageFile.ImageFormat.Gif => "image/gif",
                ImageFile.ImageFormat.Bmp => "image/bmp",
                ImageFile.ImageFormat.Jpeg => "image/jpeg",
                _ => "application/octet-stream",
            };
        }

        private static byte[] NullTerminatedUnicode(string value) =>
            Encoding.Unicode.GetBytes(value + "\0");

        private static string PreferredKey(TagFields field)
        {
            if (PreferredKeys.TryGetValue(field, out string key))
                return key;
            if (APEUtil.ReverseTagMappings.TryGetValue(field, out key))
                return key;
            return field.ToString();
        }

        private static bool IsContentDescriptionKey(string key) =>
            key.Equals("Title", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Author", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Copyright", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Description", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Rating", StringComparison.OrdinalIgnoreCase);

        private static bool TryMap(string key, out TagFields field)
        {
            if (Mappings.TryGetValue(key, out field))
                return true;
            if (APEUtil.SensitiveMap.TryGetValue(key, out field))
                return true;
            if (APEUtil.InsensitiveMap.TryGetValue(
                    key.ToUpperInvariant(), out field))
                return true;
            return Enum.TryParse(key, ignoreCase: true, out field) &&
                   field != TagFields.NullField;
        }

        private static Dictionary<string, TagFields> CreateMappings()
        {
            var result = new Dictionary<string, TagFields>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["Title"] = TagFields.Title,
                ["Author"] = TagFields.Artist,
                ["Artist"] = TagFields.Artist,
                ["WM/AlbumTitle"] = TagFields.Album,
                ["Album"] = TagFields.Album,
                ["WM/AlbumArtist"] = TagFields.AlbumArtist,
                ["Album_Artist"] = TagFields.AlbumArtist,
                ["WM/TrackNumber"] = TagFields.TrackNumber,
                ["Track"] = TagFields.TrackNumber,
                ["WM/PartOfSet"] = TagFields.DiscNumber,
                ["Disc"] = TagFields.DiscNumber,
                ["WM/Year"] = TagFields.Date,
                ["Date"] = TagFields.Date,
                ["WM/Genre"] = TagFields.Genre,
                ["Genre"] = TagFields.Genre,
                ["Description"] = TagFields.Comment,
                ["Comment"] = TagFields.Comment,
                ["WM/Comments"] = TagFields.Comment,
                ["Copyright"] = TagFields.Copyright,
                ["Rating"] = TagFields.Rating,
                ["WM/Composer"] = TagFields.Composer,
                ["WM/Conductor"] = TagFields.Conductor,
                ["WM/Lyrics"] = TagFields.Lyrics,
                ["WM/Publisher"] = TagFields.Label,
                ["WM/EncodedBy"] = TagFields.EncodedBy,
                ["WM/EncodingSettings"] = TagFields.EncoderSettings,
                ["Encoder"] = TagFields.EncoderSettings,
                ["WM/BeatsPerMinute"] = TagFields.BPM,
                ["WM/ContentGroupDescription"] = TagFields.Grouping,
                ["WM/InitialKey"] = TagFields.Key,
                ["WM/ISRC"] = TagFields.ISRC,
                ["WM/Language"] = TagFields.Language,
                ["WM/Mood"] = TagFields.Mood,
                ["WM/SubTitle"] = TagFields.Subtitle,
                ["WM/Writer"] = TagFields.Writer,
                ["WM/Producer"] = TagFields.Producer,
                ["WM/OriginalReleaseYear"] = TagFields.OriginalYear,
                ["WM/MovementNumber"] = TagFields.MovementNumber,
            };
            return result;
        }

        private static string DecodeUnicode(ReadOnlySpan<byte> bytes)
        {
            int length = bytes.Length & ~1;
            string value = Encoding.Unicode.GetString(bytes[..length]);
            return value.TrimEnd('\0');
        }

        private static void EnsureAvailable(
            byte[] source,
            int offset,
            int length)
        {
            if (offset < 0 || length < 0 || offset > source.Length - length)
                throw new InvalidDataException(
                    "Truncated ASF metadata descriptor.");
        }
    }

    internal enum AsfAttributeSource
    {
        Content,
        Extended,
        Metadata,
        MetadataLibrary,
    }

    internal sealed class AsfAttribute
    {
        internal AsfAttribute(
            string name,
            byte[] nameBytes,
            ushort type,
            byte[] value,
            AsfAttributeSource source,
            ushort language = 0,
            ushort stream = 0)
        {
            Name = name;
            NameBytes = nameBytes;
            Type = type;
            Value = value;
            Source = source;
            Language = language;
            Stream = stream;
        }

        internal string Name { get; }
        internal byte[] NameBytes { get; }
        internal ushort Type { get; }
        internal byte[] Value { get; }
        internal AsfAttributeSource Source { get; }
        internal ushort Language { get; }
        internal ushort Stream { get; }

        internal bool TryGetString(out string value)
        {
            value = null;
            if (Type != 0)
                return false;
            int length = Value.Length & ~1;
            value = Encoding.Unicode.GetString(Value, 0, length)
                .TrimEnd('\0');
            return true;
        }

        internal static AsfAttribute String(
            string name,
            AsfAttributeSource source,
            string value) =>
            new(
                name,
                Encoding.Unicode.GetBytes(name + "\0"),
                0,
                Encoding.Unicode.GetBytes(value + "\0"),
                source);

        internal static AsfAttribute Binary(
            string name,
            AsfAttributeSource source,
            byte[] value) =>
            new(
                name,
                Encoding.Unicode.GetBytes(name + "\0"),
                1,
                value,
                source);
    }
}

public sealed class AsfArtwork : IMetadataImage
{
    private readonly byte[] _data;
    private int _width;
    private int _height;
    private bool _dimensionsRead;

    private AsfArtwork(
        ID3v2Util.APICType pictureType,
        string mimeType,
        string description,
        byte[] data)
    {
        PictureType = pictureType;
        ImageType = mimeType;
        Description = description;
        _data = data;
    }

    public ID3v2Util.APICType PictureType { get; }
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
    public int Size => _data.Length;
    public byte[] Data => _data;
    public string Hash { get; private set; }

    public void HashImage(HashAlgorithm hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        Hash = Convert.ToBase64String(hash.ComputeHash(_data));
    }

    internal static bool TryCreate(
        byte[] value,
        out AsfArtwork artwork)
    {
        artwork = null;
        if (value is null || value.Length < 5)
            return false;
        uint dataLength =
            BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(1, 4));
        int offset = 5;
        if (!TryReadNullTerminatedUnicode(
                value, ref offset, out string mimeType) ||
            !TryReadNullTerminatedUnicode(
                value, ref offset, out string description) ||
            dataLength > int.MaxValue ||
            dataLength > value.Length - offset)
            return false;
        byte[] data = value.AsSpan(
            offset, checked((int)dataLength)).ToArray();
        artwork = new AsfArtwork(
            (ID3v2Util.APICType)value[0],
            mimeType,
            description,
            data);
        return true;
    }

    private static bool TryReadNullTerminatedUnicode(
        byte[] source,
        ref int offset,
        out string value)
    {
        value = null;
        int start = offset;
        while (offset + 1 < source.Length)
        {
            if (source[offset] == 0 && source[offset + 1] == 0)
            {
                value = Encoding.Unicode.GetString(
                    source, start, offset - start);
                offset += 2;
                return true;
            }
            offset += 2;
        }
        return false;
    }

    private void ReadDimensions()
    {
        if (_dimensionsRead)
            return;
        _dimensionsRead = true;
        (_width, _height) = ImageFile.GetImageDimensions(_data);
    }
}
