using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MusicFileUtilities
{
    public sealed record ID3v1CompatibilityIssue(
        TagFields Field,
        string Message,
        int SourceByteCount,
        int MaximumByteCount);

    /// <summary>The fixed 128-byte ID3v1/ID3v1.1 metadata layer stored at the end of an MP3.</summary>
    public sealed class ID3v1Tag : TagBase, IMetadataWriter
    {
        private readonly Action<string> _save;
        private string _comment = "";
        private string _genre = "";

        internal ID3v1Tag(Action<string> save) => _save = save;

        public override string TagType => "ID3v1";

        public override IEnumerable<KeyValuePair<TagFields, string>>
            GetKnownMetadata()
        {
            if (!string.IsNullOrEmpty(Title))
                yield return KeyValuePair.Create(TagFields.Title, Title);
            if (!string.IsNullOrEmpty(Artist))
                yield return KeyValuePair.Create(TagFields.Artist, Artist);
            if (!string.IsNullOrEmpty(Album))
                yield return KeyValuePair.Create(TagFields.Album, Album);
            if (!string.IsNullOrEmpty(ReleaseDate))
                yield return KeyValuePair.Create(TagFields.Date, ReleaseDate);
            if (!string.IsNullOrEmpty(_comment))
                yield return KeyValuePair.Create(TagFields.Comment, _comment);
            if (TrackNumber.HasValue)
                yield return KeyValuePair.Create(
                    TagFields.TrackNumber,
                    TrackNumber.Value.ToString());
            if (!string.IsNullOrEmpty(_genre))
                yield return KeyValuePair.Create(TagFields.Genre, _genre);
        }

        public override IEnumerable<KeyValuePair<string, string>>
            GetTextMetadata() => GetKnownMetadata()
                .Select(value => KeyValuePair.Create(
                    value.Key.ToString(), value.Value));

        public override IEnumerable<IMetadataImage> GetImageMetadata() => [];

        public void SetField(TagFields field, string value)
        {
            switch (field)
            {
                case TagFields.Title:
                    Title = value ?? "";
                    break;
                case TagFields.Artist:
                    Artist = value ?? "";
                    break;
                case TagFields.Album:
                    Album = value ?? "";
                    break;
                case TagFields.Date:
                    ReleaseDate = value;
                    break;
                case TagFields.Comment:
                    _comment = value ?? "";
                    break;
                case TagFields.TrackNumber:
                    if (value is null)
                        TrackNumber = null;
                    else if (byte.TryParse(value, out byte track) && track != 0)
                        TrackNumber = track;
                    else
                        throw new ArgumentException(
                            "ID3v1.1 track numbers must be between 1 and 255.",
                            nameof(value));
                    break;
                case TagFields.Genre:
                    if (value is not null && !ID3v2Util.ID3v1Genres.Contains(
                            value, StringComparer.OrdinalIgnoreCase))
                        throw new ArgumentException(
                            $"'{value}' is not an ID3v1 genre.",
                            nameof(value));
                    _genre = value ?? "";
                    break;
                default:
                    throw new ArgumentException(
                        $"Unsupported tag field for ID3v1: {field}",
                        nameof(field));
            }
        }

        public void RemoveField(TagFields field) => SetField(field, null);

        public void Save(string outputPath = null) => _save(outputPath);

        internal bool Read(Stream stream, long length)
        {
            if (length < 128)
                return false;
            long previous = stream.Position;
            stream.Position = length - 128;
            byte[] data = new byte[128];
            stream.ReadExactly(data);
            stream.Position = previous;
            if (!data.AsSpan(0, 3).SequenceEqual("TAG"u8))
                return false;
            Title = Decode(data, 3, 30);
            Artist = Decode(data, 33, 30);
            Album = Decode(data, 63, 30);
            ReleaseDate = Decode(data, 93, 4);
            bool version11 = data[125] == 0 && data[126] != 0;
            _comment = Decode(data, 97, version11 ? 28 : 30);
            TrackNumber = version11 ? data[126] : null;
            _genre = data[127] < ID3v2Util.ID3v1Genres.Count
                ? ID3v2Util.ID3v1Genres[data[127]]
                : "";
            return true;
        }

        internal byte[] Serialize()
        {
            byte[] data = new byte[128];
            "TAG"u8.CopyTo(data);
            Encode(Title, data, 3, 30);
            Encode(Artist, data, 33, 30);
            Encode(Album, data, 63, 30);
            Encode(ReleaseDate, data, 93, 4);
            int commentLength = TrackNumber.HasValue ? 28 : 30;
            Encode(_comment, data, 97, commentLength);
            if (TrackNumber.HasValue)
            {
                data[125] = 0;
                data[126] = checked((byte)TrackNumber.Value);
            }
            int genre = ID3v2Util.ID3v1Genres
                .Select((value, index) => (value, index))
                .FirstOrDefault(pair => string.Equals(
                    pair.value, _genre,
                    StringComparison.OrdinalIgnoreCase)).index;
            data[127] = string.IsNullOrEmpty(_genre)
                ? byte.MaxValue
                : checked((byte)genre);
            return data;
        }

        public IReadOnlyList<ID3v1CompatibilityIssue>
            GetCompatibilityIssues()
        {
            var issues = new List<ID3v1CompatibilityIssue>();
            Check(TagFields.Title, Title, 30, issues);
            Check(TagFields.Artist, Artist, 30, issues);
            Check(TagFields.Album, Album, 30, issues);
            Check(TagFields.Date, ReleaseDate, 4, issues);
            Check(
                TagFields.Comment,
                _comment,
                TrackNumber.HasValue ? 28 : 30,
                issues);
            return issues;
        }

        internal void CopyFrom(IMetadataProvider source)
        {
            foreach (var value in source.GetKnownMetadata())
            {
                if (value.Key is TagFields.Title or TagFields.Artist or
                    TagFields.Album or TagFields.Date or TagFields.Comment or
                    TagFields.TrackNumber or TagFields.Genre)
                {
                    try { SetField(value.Key, value.Value); }
                    catch (ArgumentException) { }
                }
            }
        }

        private static string Decode(byte[] data, int offset, int count) =>
            Encoding.Latin1.GetString(data, offset, count)
                .TrimEnd('\0', ' ');

        private static void Encode(
            string value,
            byte[] destination,
            int offset,
            int maximum)
        {
            byte[] bytes = Encoding.Latin1.GetBytes(value ?? "");
            Array.Copy(bytes, 0, destination, offset, Math.Min(maximum, bytes.Length));
        }

        private static void Check(
            TagFields field,
            string value,
            int maximum,
            List<ID3v1CompatibilityIssue> issues)
        {
            try
            {
                ID3v2Util.ISO8859Encoding.GetBytes(value ?? "");
            }
            catch (EncoderFallbackException)
            {
                issues.Add(new(
                    field,
                    $"{field} contains characters that ID3v1 cannot represent.",
                    Encoding.UTF8.GetByteCount(value ?? ""),
                    maximum));
            }
            int bytes = Encoding.Latin1.GetByteCount(value ?? "");
            if (bytes > maximum)
                issues.Add(new(
                    field,
                    $"{field} requires {bytes} bytes; ID3v1 allows {maximum}.",
                    bytes,
                    maximum));
        }
    }
}
