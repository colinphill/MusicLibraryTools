using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

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

    }

    public class MetadataHelper
    {

    }

    public interface IMetadataProvider
    {

        string Title
        {
            get
            {
                return GetKnownMetadata().Where(kv => kv.Key == TagFields.Title).DefaultIfEmpty(KeyValuePair.Create(TagFields.Title, "Unknown")).FirstOrDefault().Value;
            }
        }

        string Artist
        {
            get
            {
                return GetKnownMetadata().Where(kv => kv.Key == TagFields.Artist).DefaultIfEmpty(KeyValuePair.Create(TagFields.Artist, "Unknown")).FirstOrDefault().Value;
            }
        }

        string AlbumArtist
        {
            get
            {
                return GetKnownMetadata().Where(kv => kv.Key == TagFields.AlbumArtist).DefaultIfEmpty(KeyValuePair.Create(TagFields.AlbumArtist, Artist)).FirstOrDefault().Value;
            }
        }

        string Album
        {
            get
            {
                return GetKnownMetadata().Where(kv => kv.Key == TagFields.Album).DefaultIfEmpty(KeyValuePair.Create(TagFields.Album, "Unknown")).FirstOrDefault().Value;
            }
        }

        int TrackNumber
        {
            get
            {
                return int.Parse(GetKnownMetadata().Where(kv => kv.Key == TagFields.TrackNumber).DefaultIfEmpty(KeyValuePair.Create(TagFields.TrackNumber, "0")).FirstOrDefault().Value);
            }
        }

        string TagType
        {
            get;
        }

        IEnumerable<KeyValuePair<TagFields, string>> GetKnownMetadata();
        IEnumerable<KeyValuePair<string, string>> GetTextMetadata();
        IEnumerable<IMetadataImage> GetImageMetadata();

    }
     
    public class Metadata
    {
        public static IMetadataProvider GetProvider(string path)
        {
            string extension = Path.GetExtension(path).ToLower();
            if (!File.Exists(path))
                throw new FileNotFoundException("File Not Found", path);
            switch (extension)
            {
                case ".mp3":
                    return new MP3File(path);

                case ".dsf":
                    return new DSFFile(path);

                case ".m4a":
                case ".mp4":
                case ".m4p":
                case ".m4r":
                    return new RootAtom(path);

                case ".ogg":
                    return new OggVorbisFile(path);

                case ".flac":
                    return new FLACFile(path);

                default:
                    throw new ArgumentException("Invalid File Type", "path");
            }

        }

    }

}
