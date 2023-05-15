using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;

namespace MusicFileUtilities
{
    public class WavPackFile : IMediaFile, ICodecProvider
    {
        public IEnumerable<ICodecProvider> Codecs
        {
            get
            {
                yield return this;
            }
        }


        public IEnumerable<IMetadataProvider> Tags
        {
            get
            {
                if (tag_ != null)
                    yield return tag_;
            }
        }

        uint averagebitrate_ = 0;
        uint bitpersample_ = 0;
        uint samplerate_ = 44100;
        uint channels_ = 2;
        uint durationinframes_ = 0;

        string ICodecProvider.CodecName => "WavPack";

        CodecType ICodecProvider.CodecType => CodecType.Lossless;

        uint ICodecProvider.AverageBitrate => averagebitrate_;

        uint ICodecProvider.MaxBitrate => averagebitrate_;

        uint ICodecProvider.BitsPerSample => bitpersample_;

        uint ICodecProvider.Samplerate => samplerate_;

        uint ICodecProvider.Channels => channels_;

        uint ICodecProvider.DurationInFrames => durationinframes_;

        uint ICodecProvider.DurationInSeconds => durationinframes_ / 75;

        private APETag tag_ = null;

        private static readonly uint [] samplerates_ = { 6000, 8000, 9600, 11025, 12000, 16000, 22050,
            24000, 32000, 44100, 48000, 64000, 88200, 96000, 192000, 44100 };

        public WavPackFile(string filename)
        {
            using var s = File.OpenRead(filename);
            byte[] header = new byte[32];
            s.Read(header, 0, 32);
            while (Encoding.ASCII.GetString(header,0,4) == "wvpk")
            {
                int blocksize = Tools.Int32AtLE(header, 4) - 24;
                int version = Tools.Int16AtLE(header, 8);
                ulong blockindex = ((ulong)header[10]) << 32 | ((ulong)Tools.UInt32AtLE(header, 16));
                ulong totalsamples = ((ulong)header[11]) << 32 | ((ulong)Tools.UInt32AtLE(header, 12));
                uint blocksamples = Tools.UInt32AtLE(header, 20);
                uint flags = Tools.UInt32AtLE(header, 24);
                uint crc = Tools.UInt32AtLE(header, 28);
                uint multiplier = 1;
                if ((flags & 4) != 0)
                    channels_ = 1;
                bitpersample_ = ((flags & 3) + 1) << 3;
                samplerate_ = samplerates_[(flags >> 23) & 15];
                byte[] subblock = new byte[blocksize];
                s.Read(subblock, 0, blocksize);
                int offset = 0;
                while (offset < blocksize)
                {
                    byte id = subblock[offset++];
                    int ws = subblock[offset++];
                    if ((id & 0x80) != 0)
                    {
                        ws |= ((int)Tools.UInt16AtLE(subblock, offset)) << 8;
                        offset += 2;
                    }
                    if ((id & 0x3f) == 0xe)
                        multiplier = 1u << subblock[offset];
                    if ((id & 0x3f) == 0xd)
                        channels_ = subblock[offset];
                    if ((id & 0x3f) == 0x27)
                        samplerate_ = Tools.UInt16AtLE(subblock, offset) | (((uint)subblock[offset + 2]) << 8);
                    offset += ws * 2;
                }
                if ((flags & 0x80000000u) != 0)
                {
                    bitpersample_ = 1;
                    samplerate_ *= multiplier * 8;
                    totalsamples *= 8;
                }
                durationinframes_ = (uint)(75ul * totalsamples / samplerate_);
                averagebitrate_ = (uint)(s.Length * 8 / (durationinframes_ / 75));

                break;
            }
            tag_ = new APETag();
            if (!tag_.ReadTag(s))
                tag_ = new APETag(); 
        }
    }
}
