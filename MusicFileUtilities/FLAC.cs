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
    public class FLACFile : VorbisComments, ICodecProvider, IMediaFile, IMetadataWriter
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
                yield return this;
            }
        }

        public string Filename
        {
            get;
            set;
        }

        // Raw bytes of metadata blocks other than Vorbis comment (type 4) and picture (type 6),
        // captured during parsing so Save() can rewrite the file faithfully.
        private List<(byte Type, byte[] Data)> _otherMetaBlocks = new();
        private byte[] _streamInfoData = null;

        // Tracks the VORBIS_COMMENT block's location for in-place saving.
        private long _vcBlockOffset = -1;   // file offset of the block's 4-byte header
        private int _vcBlockDataLen = 0;    // data length of the block as parsed
        private bool _vcIsLast = false;     // whether the block had the last-block bit

        public string CodecName => "FLAC";

        public CodecType CodecType => CodecType.Lossless;

        public uint AverageBitrate
        {
            get;
            protected set;
        }

        public uint MaxBitrate => AverageBitrate;

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

        public uint DurationInFrames
        {
            get;
            protected set;
        }

        public uint DurationInSeconds => DurationInFrames / 75;

        private void ParseStreamInfo(byte [] si, long length, long framessize)
        {
            Samplerate = (((uint)Tools.UInt16AtBE(si, 10)) << 4) | (uint)(si[12] >> 4);
            Channels = (uint)(((si[12] >> 1) & 7) + 1);
            BitsPerSample = (uint)(((si[12] & 1) << 4) | (si[13] >> 4)) + 1;
            ulong samples = Tools.UInt32AtBE(si, 14);
            samples |= ((ulong)(si[13] & 0xf)) << 32;
            long seconds = (long)samples / Samplerate;
            AverageBitrate = (uint)(length / seconds * 8);
            DurationInFrames = (uint)(75 * samples / Samplerate);
        }

        public FLACFile(string filename)
        {
            Filename = filename;

            FileStream s = new FileStream(filename, FileMode.Open, FileAccess.Read);

            byte[] si = null;
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

                byte blockType = (byte)(b[0] & 127);

                if (blockType == 0) // STREAMINFO
                {
                    si = new byte[len];
                    s.Read(si, 0, (int)len);
                    _streamInfoData = si;
                }
                else if (blockType == 4) // VORBIS_COMMENT
                {
                    _vcBlockOffset = s.Position - 4; // position of the 4-byte block header
                    _vcBlockDataLen = (int)len;
                    _vcIsLast = (b[0] & 128) == 128;
                    byte [] vc = new byte[len];
                    s.Read(vc, 0, (int)len);
                    FromByteArray(vc);
                }
                else if (blockType == 6) // PICTURE
                {
                    byte[] p = new byte[len];
                    s.Read(p, 0, (int)len);
                    Artworks.Add(new VorbisArtwork(p));
                }
                else
                {
                    byte[] raw = new byte[len];
                    s.Read(raw, 0, (int)len);
                    _otherMetaBlocks.Add((blockType, raw));
                }

                last = ((b[0] & 128) == 128);

            }

            if (si != null)
                ParseStreamInfo(si, s.Length, s.Length - s.Position);

            s.Close();
        }

        public void SaveTags(string outputPath = null) => Save(outputPath);

        private static void WriteMetaBlockHeader(FileStream fs, byte type, int len, bool isLast)
        {
            fs.WriteByte(isLast ? (byte)(type | 0x80) : type);
            fs.WriteByte((byte)((len >> 16) & 0xFF));
            fs.WriteByte((byte)((len >> 8) & 0xFF));
            fs.WriteByte((byte)(len & 0xFF));
        }

        public void Save(string outputPath = null)
        {
            string target = outputPath ?? Filename
                ?? throw new InvalidOperationException("No filename associated with this file.");

            byte[] newVCData = ToByteArray(false);

            // In-place: new VC data fits within original block space, no audio data moves
            if (outputPath == null && _vcBlockOffset >= 0 && newVCData.Length <= _vcBlockDataLen)
            {
                int leftover = _vcBlockDataLen - newVCData.Length;
                using FileStream fs = new FileStream(Filename, FileMode.Open, FileAccess.ReadWrite);
                fs.Seek(_vcBlockOffset, SeekOrigin.Begin);
                if (leftover >= 4)
                {
                    // Write smaller VORBIS_COMMENT followed by PADDING to fill the remaining space.
                    // The PADDING content (leftover-4 bytes) is already in the file; no need to zero it.
                    WriteMetaBlockHeader(fs, 4, newVCData.Length, isLast: false);
                    fs.Write(newVCData, 0, newVCData.Length);
                    WriteMetaBlockHeader(fs, 1, leftover - 4, isLast: _vcIsLast);
                }
                else
                {
                    // Exact fit or 1-3 byte gap: expand the VC block length to cover the full old space.
                    // FromByteArray reads by count so the extra bytes are harmless.
                    WriteMetaBlockHeader(fs, 4, _vcBlockDataLen, isLast: _vcIsLast);
                    fs.Write(newVCData, 0, newVCData.Length);
                    if (leftover > 0)
                        fs.Write(new byte[leftover], 0, leftover);
                }
                return;
            }

            // Build list of serialized metadata blocks: (type, data)
            // Order: STREAMINFO first, then other preserved blocks, then VORBIS_COMMENT, then PICTUREs
            var blocks = new List<(byte Type, byte[] Data)>();

            if (_streamInfoData != null)
                blocks.Add((0, _streamInfoData));

            foreach (var block in _otherMetaBlocks)
                blocks.Add(block);

            blocks.Add((4, newVCData));

            foreach (VorbisArtwork art in Artworks)
                blocks.Add((6, art.ToByteArray()));

            string tempPath = target + ".tmp~";
            try
            {
                string sourcePath = Filename ?? target;
                using FileStream source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read);
                using FileStream dest = new FileStream(tempPath, FileMode.Create, FileAccess.Write);

                // Skip past original fLaC marker and all metadata blocks in source
                byte[] marker = new byte[4];
                source.Read(marker, 0, 4); // "fLaC"

                // Skip original metadata blocks
                bool lastSrc = false;
                while (!lastSrc)
                {
                    byte[] hdr = new byte[4];
                    source.Read(hdr, 0, 4);
                    long len = ((long)hdr[1] << 16) | ((long)hdr[2] << 8) | hdr[3];
                    lastSrc = (hdr[0] & 128) != 0;
                    source.Seek(len, SeekOrigin.Current);
                }
                // source now positioned at start of audio data

                // Write fLaC marker
                dest.Write(Encoding.ASCII.GetBytes("fLaC"), 0, 4);

                // Write metadata blocks
                for (int i = 0; i < blocks.Count; i++)
                {
                    bool isLast = (i == blocks.Count - 1);
                    var (type, data) = blocks[i];
                    byte typeByte = isLast ? (byte)(type | 0x80) : type;
                    int blockLen = data.Length;
                    dest.WriteByte(typeByte);
                    dest.WriteByte((byte)((blockLen >> 16) & 0xFF));
                    dest.WriteByte((byte)((blockLen >> 8) & 0xFF));
                    dest.WriteByte((byte)(blockLen & 0xFF));
                    dest.Write(data, 0, data.Length);
                }

                // Copy audio data
                byte[] buffer = new byte[65536];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    dest.Write(buffer, 0, read);
            }
            catch
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
                throw;
            }

            if (File.Exists(target)) File.Delete(target);
            File.Move(tempPath, target);
            Filename = target;
        }

    }

}