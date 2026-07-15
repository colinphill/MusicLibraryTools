using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security.Cryptography;

namespace MusicFileUtilities
{

    public class NoMetadataException : Exception
    {
        public NoMetadataException(string item) : base("No Metadata - " + item)
        {
        }
    }

    public class UnsupportedMetadataEncodingException : Exception
    {
        public UnsupportedMetadataEncodingException(string item) : base("Unsupported Metadata Encoding - " + item)
        {
        }
    }

    public class MetadataOptions
    {
        public static bool UseLegacyEncodings { get; set; } = false;
    }

    [Serializable]
    public enum CodecType { Lossy, Lossless };

    public interface IMediaFile
    {
        public IEnumerable<ICodecProvider> Codecs { get; }
        public IEnumerable<IMetadataProvider> Tags { get; }
        void SaveTags(string outputPath = null);
    }

    public interface ICodecProvider
    {
        string CodecName
        {
            get;
        }
        CodecType CodecType
        {
            get;
        }
        uint AverageBitrate
        {
            get;
        }
        uint MaxBitrate
        {
            get;
        }
        uint BitsPerSample
        {
            get;
        }
        uint Samplerate
        {
            get;
        }
        uint Channels
        {
            get;
        }

        uint DurationInFrames
        {
            get;
        }

        uint DurationInSeconds
        {
            get;
        }
    }

    public interface IMetadataImage
    {
        string Description
        {
            get;
        }

        string Category
        {
            get;
        }

        string ImageType
        {
            get;
        }

        int Width
        {
            get;
        }

        int Height
        {
            get;
        }

        int Size
        {
            get;
        }

        byte [] Data
        {
            get;
        }

        string Hash
        {
            get;
        }

        void HashImage(HashAlgorithm hash);

    }

    public class MetadataHelper
    {

    }

    public interface IMetadataProvider
    {

        string Title { get; }

        string Artist { get; }

        string AlbumArtist { get; }

        string Album { get; }

        int? TrackNumber { get; }

        string TagType { get; }

        string ReleaseDate { get; }

        int? TrackTotal { get; }

        int? DiscNumber { get; }

        int? DiscTotal { get; }

        bool HasAlbumArtist { get; }

        IEnumerable<KeyValuePair<TagFields, string>> GetKnownMetadata();
        IEnumerable<KeyValuePair<string, string>> GetTextMetadata();
        IEnumerable<IMetadataImage> GetImageMetadata();

    }

    public interface IMetadataWriter
    {
        void SetField(TagFields field, string value);
        void RemoveField(TagFields field);
        void Save(string outputPath = null);
    }

    /// <summary>
    /// Uniform embedded-artwork writing across formats. Implemented where the underlying tag can
    /// carry pictures; the front cover is the common case. Call <see cref="IMediaFile.SaveTags"/>
    /// (or <see cref="IMetadataWriter.Save"/>) afterwards to persist. Formats without an
    /// implementation are treated as artwork-read-only by callers.
    /// </summary>
    public interface IArtworkWriter
    {
        /// <summary>Replace (or add) the front-cover image. Passing null/empty data removes covers.</summary>
        void SetFrontCover(byte[] imageData, string mimeType);

        /// <summary>Remove all embedded images.</summary>
        void RemoveImages();

        /// <summary>
        /// Replace the entire embedded-image set with the given images (each carrying a picture
        /// type such as FrontCover/BackCover/Media). Formats without per-image type semantics
        /// (MP4) store them all as cover art.
        /// </summary>
        void SetImages(IReadOnlyList<ArtworkImage> images);
    }

    /// <summary>A single image to embed: its picture type, MIME type, description, and bytes.</summary>
    public sealed record ArtworkImage(ID3v2Util.APICType Type, string MimeType, string Description, byte[] Data);

    public abstract class TagBase : IMetadataProvider
    {
        public string Title { get; protected set; } = "";
        public string Artist { get; protected set; } = "";
        public string AlbumArtist { get; protected set; } = "";
        public string Album { get; protected set; } = "";
        public int? TrackNumber { get; protected set; } = null;
        public int? TrackTotal { get; protected set; } = null;
        public string ReleaseDate { get; protected set; } = null;
        public int? DiscNumber { get; protected set; } = null;
        public int? DiscTotal { get; protected set; } = null;
        public bool HasAlbumArtist { get; protected set; } = false;

        protected void ParseStandardFields()
        {
            int n = 0;
            // Keep the first value seen for each field, iterating forward. (Previously this
            // buffered and reversed the entire sequence just to get first-wins semantics.)
            var seen = new HashSet<TagFields>();
            foreach (var kv in GetKnownMetadata())
            {
                if (!seen.Add(kv.Key))
                    continue;
                switch (kv.Key)
                {
                    case TagFields.Title:
                        Title = kv.Value;
                        break;
                    case TagFields.Artist:
                        Artist = kv.Value;
                        break;
                    case TagFields.AlbumArtist:
                        AlbumArtist = kv.Value;
                        HasAlbumArtist = true;
                        break;
                    case TagFields.Album:
                        Album = kv.Value;
                        break;
                    case TagFields.TrackNumber:
                        if (int.TryParse(kv.Value, out n))
                            TrackNumber = n;
                        break;
                    case TagFields.TotalTracks:
                        if (int.TryParse(kv.Value, out n))
                            TrackTotal = n;
                        break;
                    case TagFields.DiscNumber:
                        if (int.TryParse(kv.Value, out n))
                            DiscNumber = n;
                        break;
                    case TagFields.TotalDiscs:
                        if (int.TryParse(kv.Value, out n))
                            DiscTotal = n;
                        break;
                    case TagFields.Date:
                        ReleaseDate = kv.Value;
                        break;
                    default:
                        break;
                }    
            }
            if (string.IsNullOrEmpty(AlbumArtist))
                AlbumArtist = Artist;
        }

        public abstract string TagType { get; }
        public abstract IEnumerable<KeyValuePair<TagFields,string>> GetKnownMetadata();
        public abstract IEnumerable<KeyValuePair<string,string>> GetTextMetadata();
        public abstract IEnumerable<IMetadataImage> GetImageMetadata();
    }
     
    public class MediaFile
    {
        public static IMediaFile GetFile(string path, HashAlgorithm hash = null, bool readOnly = false)
        {
            string extension = Path.GetExtension(path).ToLower();
            // No File.Exists pre-check: on a network share it costs a full round-trip per file,
            // and the parser's own open throws FileNotFoundException for a missing file anyway.

            IMediaFile file = null;
            switch (extension)
            {
                case ".wv":
                    file = new WavPackFile(path);
                    break;

                case ".mp3":
                    file = new MP3File(path);
                    break;

                case ".dsf":
                    file = new DSFFile(path);
                    break;

                case ".m4a":
                case ".mp4":
                case ".m4p":
                case ".m4r":
                    file = new MP4File(path, readOnly);
                    break;

                case ".ogg":
                    file = new OggVorbisFile(path);
                    break;

                case ".flac":
                    file = new FLACFile(path);
                    break;

                default:
                    throw new ArgumentException("Invalid File Type", "path");
            }

            if (hash != null)
                foreach (var image in file.Tags.SelectMany(t => t.GetImageMetadata()))
                    image.HashImage(hash);

            return file;
        }
    }

}
