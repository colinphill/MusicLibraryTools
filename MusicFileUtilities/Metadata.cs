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

        uint Duration
        {
            get;
        }
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
        bool Compilation
        {
            get;
        }
    }

    public interface IMetadataAlterer
    {
        string Title
        {
            set;
        }
        string Artist
        {
            set;
        }
        string AlbumArtist
        {
            set;
        }
        string Album
        {
            set;
        }
        int TrackNumber
        {
            set;
        }
        void Write();
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
