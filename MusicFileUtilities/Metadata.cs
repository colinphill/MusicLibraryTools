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

        string Title
        {
            get;
        }

        string Artist
        {
            get;
        }

        string AlbumArtist
        {
            get;
        }

        string Album
        {
            get;
        }

        int TrackNumber
        {
            get;
        }

        string TagType
        {
            get;
        }

        IEnumerable<KeyValuePair<TagFields, string>> GetKnownMetadata();
        IEnumerable<KeyValuePair<string, string>> GetTextMetadata();
        IEnumerable<IMetadataImage> GetImageMetadata();

    }

    public abstract class TagBase : IMetadataProvider
    {
        public string Title { get; protected set; } = "";
        public string Artist { get; protected set; } = "";
        public string AlbumArtist { get; protected set; } = "";
        public string Album { get; protected set; } = "";
        public int TrackNumber { get; protected set; } = 0;

        protected void ParseStandardFields()
        {
            int tn = 0;
            foreach (var kv in GetKnownMetadata().Reverse())
            {
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
                        break;
                    case TagFields.Album:
                        Album = kv.Value;
                        break;
                    case TagFields.TrackNumber:
                        if (!int.TryParse(kv.Value, out tn))
                            tn = 0;
                        TrackNumber = tn;
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
     
    public class Metadata
    {
        public static IMetadataProvider GetProvider(string path, HashAlgorithm hash = null)
        {
            string extension = Path.GetExtension(path).ToLower();
            if (!File.Exists(path))
                throw new FileNotFoundException("File Not Found", path);

            IMetadataProvider provider = null;
            switch (extension)
            {
                case ".mp3":
                    provider = new MP3File(path);
                    break;

                case ".dsf":
                    provider = new DSFFile(path);
                    break;

                case ".m4a":
                case ".mp4":
                case ".m4p":
                case ".m4r":
                    provider = new MP4File(path);
                    break;

                case ".ogg":
                    provider = new OggVorbisFile(path);
                    break;

                case ".flac":
                    provider = new FLACFile(path);
                    break;

                default:
                    throw new ArgumentException("Invalid File Type", "path");
            }

            if (hash != null)
                foreach (var image in provider.GetImageMetadata())
                    image.HashImage(hash);

            return provider;
        }
    }

}
