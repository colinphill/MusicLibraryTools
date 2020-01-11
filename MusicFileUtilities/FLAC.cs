/* 
 * SVN Information:
 * 
 * $HeadURL: file:///Z:/SVN_Repositories/MusicLibraryTools/trunk/MusicFileUtilities/FLAC.cs $
 * $Date: 2014-10-18 06:43:07 -0600 (Sat, 18 Oct 2014) $
 * $Revision: 23 $
 * $Author: colin $
 * 
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MusicFileUtilities
{
    [Serializable]
    public class FLACFile : VorbisComments, ICodecProvider
    {
        public string Filename
        {
            get;
            set;
        }

        public string Name => "FLAC";

        public CodecType Type => CodecType.Lossless;

        public uint AverageBitrate
        {
            get;
            protected set;
        }

        public uint MaxBitrate
        {
            get
            {
                return AverageBitrate;
            }
        }

        public uint BitsPerSample
        {
            get;
            protected set;
        }

        public uint Samplerate
        {
            get;
            protected set;
        }

        public uint Channels
        {
            get;
            protected set;
        }

        private void ParseStreamInfo(byte [] si, long length)
        {
            Samplerate = (((uint)Tools.UInt16AtBE(si, 10)) << 4) | (uint)(si[12] >> 4);
            Channels = (uint)(((si[12] >> 1) & 7) + 1);
            BitsPerSample = (uint)(((si[12] & 1) << 4) | (si[13] >> 4)) + 1;
            ulong samples = Tools.UInt32AtBE(si, 14);
            samples |= ((ulong)(si[13] & 0xf)) << 32;
            long seconds = (long)samples / Samplerate;
            AverageBitrate = (uint)(length / seconds * 8);
        }

        public FLACFile(string filename)
        {
            Filename = filename;

            FileStream s = new FileStream(filename, FileMode.Open, FileAccess.Read);
            
            byte [] b = new byte[4];
            s.Read(b, 0, 4);
            if (Encoding.ASCII.GetString(b) != "fLaC")
                throw new InvalidDataException();

            bool last = false;
            while (!last)
            {
                s.Read(b, 0, 4);
                
                long len = b[1];
                len = (len << 8) | b[2];
                len = (len << 8) | b[3];

                if ((b[0] & 127) == 0) // STREAMINFO
                {
                    byte[] si = new byte[len];
                    s.Read(si, 0, (int)len);
                    ParseStreamInfo(si, s.Length);
                }
                else if ((b[0] & 127) == 4)
                {
                    byte [] vc = new byte[len];
                    s.Read(vc, 0, (int)len);
                    FromByteArray(vc);
                }
                else if ((b[0] & 127) == 6)
                {
                    byte[] p = new byte[len];
                    s.Read(p, 0, (int)len);
                    Artworks.Add(new VorbisArtwork(p));
                }
                else
                    s.Seek(len, SeekOrigin.Current);
                
                last = ((b[0] & 128) == 128);

            }
    
            s.Close();
        }

    }

}