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
using System.Linq;
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
        private List<(byte Type, byte[] Data, int Length)> _otherMetaBlocks = new();
        private byte[] _streamInfoData = null;

        // Raw PICTURE block bytes exactly as read from disk. Save() compares these against the
        // current Artworks so the VORBIS_COMMENT-only in-place fast path is only taken when the
        // artwork on disk is unchanged (otherwise added/removed art would be silently dropped).
        private List<byte[]> _originalPictureBlocks = new();

        // Tracks the VORBIS_COMMENT block's location for in-place saving.
        private long _vcBlockOffset = -1;   // file offset of the block's 4-byte header
        private int _vcBlockDataLen = 0;    // data length of the block as parsed
        private bool _vcIsLast = false;     // whether the block had the last-block bit
        private long _paddingAfterVcOffset = -1;
        private int _paddingAfterVcDataLen;
        private bool _paddingAfterVcIsLast;

        internal bool LastSaveWasInPlace { get; private set; }

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

        private void ParseStreamInfo(byte [] si, long length)
        {
            Samplerate = (((uint)Tools.UInt16AtBE(si, 10)) << 4) | (uint)(si[12] >> 4);
            Channels = (uint)(((si[12] >> 1) & 7) + 1);
            BitsPerSample = (uint)(((si[12] & 1) << 4) | (si[13] >> 4)) + 1;
            ulong samples = Tools.UInt32AtBE(si, 14);
            samples |= ((ulong)(si[13] & 0xf)) << 32;
            if (samples == 0 || Samplerate == 0)
            {
                AverageBitrate = 0;
                DurationInFrames = 0;
                return;
            }
            // Compute bitrate directly from sample count to avoid truncating the
            // duration to whole seconds (which is 0 for sub-second tracks -> divide by zero).
            AverageBitrate = (uint)((ulong)length * 8 * Samplerate / samples);
            DurationInFrames = (uint)(75 * samples / Samplerate);
        }

        public FLACFile(string filename)
        {
            Filename = filename;

            using FileStream s = Tools.OpenReadSequential(filename);
            Parse(s);
        }

        internal FLACFile(Stream stream, string filename)
        {
            Filename = filename;
            Parse(stream);
        }

        private void Parse(Stream s)
        {
            byte[] si = null;
            byte [] b = new byte[4];
            s.ReadExactly(b);
            if (Encoding.ASCII.GetString(b) != "fLaC")
                throw new InvalidDataException();

            bool last = false;
            bool previousWasVorbisComment = false;
            while (!last)
            {
                s.ReadExactly(b);

                long len = b[1];
                len = (len << 8) | b[2];
                len = (len << 8) | b[3];

                byte blockType = (byte)(b[0] & 127);
                bool isLast = (b[0] & 128) == 128;
                long blockOffset = s.Position - 4;

                if (blockType == 0) // STREAMINFO
                {
                    si = new byte[len];
                    s.ReadExactly(si);
                    _streamInfoData = si;
                }
                else if (blockType == 4) // VORBIS_COMMENT
                {
                    _vcBlockOffset = s.Position - 4; // position of the 4-byte block header
                    _vcBlockDataLen = (int)len;
                    _vcIsLast = (b[0] & 128) == 128;
                    byte [] vc = new byte[len];
                    s.ReadExactly(vc);
                    FromByteArray(vc);
                }
                else if (blockType == 6) // PICTURE
                {
                    byte[] p = new byte[len];
                    s.ReadExactly(p);
                    _originalPictureBlocks.Add(p);
                    Artworks.Add(new VorbisArtwork(p));
                }
                else
                {
                    // PADDING has no semantic payload. On a network share, skip it instead of
                    // transferring and retaining bytes that a rewrite may safely regenerate. A
                    // final padding block needs no seek at all: no later byte is parsed.
                    byte[] raw = null;
                    if (blockType == 1 && !isLast)
                        s.Seek(len, SeekOrigin.Current);
                    else if (blockType != 1)
                    {
                        raw = new byte[len];
                        s.ReadExactly(raw);
                    }
                    _otherMetaBlocks.Add((blockType, raw, (int)len));

                    if (blockType == 1 && previousWasVorbisComment)
                    {
                        _paddingAfterVcOffset = blockOffset;
                        _paddingAfterVcDataLen = (int)len;
                        _paddingAfterVcIsLast = (b[0] & 128) == 128;
                    }
                }

                last = isLast;
                previousWasVorbisComment = blockType == 4;

            }

            if (si != null)
                ParseStreamInfo(si, s.Length);

        }

        public void SaveTags(string outputPath = null) => Save(outputPath);

        // True when the in-memory Artworks serialize identically to the PICTURE blocks read
        // from disk. When false (art added/removed/edited), the in-place VORBIS_COMMENT-only
        // rewrite would not reflect the change, so Save() must do a full rewrite.
        private bool ArtworksMatchDisk()
        {
            if (Artworks.Count != _originalPictureBlocks.Count)
                return false;
            for (int i = 0; i < Artworks.Count; i++)
                if (!Artworks[i].ToByteArray().AsSpan().SequenceEqual(_originalPictureBlocks[i]))
                    return false;
            return true;
        }

        internal static void WriteMetaBlockHeader(Stream fs, byte type, int len, bool isLast)
        {
            if (len < 0 || len > 0xFFFFFF)
                throw new ArgumentOutOfRangeException(nameof(len), "A FLAC metadata block length must fit in 24 bits.");
            fs.WriteByte(isLast ? (byte)(type | 0x80) : type);
            fs.WriteByte((byte)((len >> 16) & 0xFF));
            fs.WriteByte((byte)((len >> 8) & 0xFF));
            fs.WriteByte((byte)(len & 0xFF));
        }

        public void Save(string outputPath = null)
        {
            LastSaveWasInPlace = false;
            string target = outputPath ?? Filename
                ?? throw new InvalidOperationException("No filename associated with this file.");

            byte[] newVCData = ToByteArray(false);

            // In-place: new VC data fits within original block space, no audio data moves, and
            // the PICTURE blocks are unchanged (the in-place path only rewrites VORBIS_COMMENT).
            if (outputPath == null && _vcBlockOffset >= 0 && newVCData.Length <= _vcBlockDataLen && ArtworksMatchDisk())
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
                LastSaveWasInPlace = true;
                return;
            }

            // A VORBIS_COMMENT may grow into the immediately following PADDING block without
            // moving the audio stream. The four-byte padding header is part of the reclaimable
            // span; preserve any remainder as a smaller padding block when possible.
            if (outputPath == null && _vcBlockOffset >= 0 && _paddingAfterVcOffset == _vcBlockOffset + 4 + _vcBlockDataLen &&
                newVCData.Length > _vcBlockDataLen &&
                newVCData.Length <= _vcBlockDataLen + 4 + _paddingAfterVcDataLen && ArtworksMatchDisk())
            {
                int available = _vcBlockDataLen + 4 + _paddingAfterVcDataLen;
                int leftover = available - newVCData.Length;
                bool regionIsLast = _paddingAfterVcIsLast;
                using FileStream fs = new FileStream(Filename, FileMode.Open, FileAccess.ReadWrite);
                fs.Seek(_vcBlockOffset, SeekOrigin.Begin);
                if (leftover >= 4)
                {
                    WriteMetaBlockHeader(fs, 4, newVCData.Length, isLast: false);
                    fs.Write(newVCData, 0, newVCData.Length);
                    _paddingAfterVcOffset = fs.Position;
                    WriteMetaBlockHeader(fs, 1, leftover - 4, isLast: regionIsLast);
                    _paddingAfterVcDataLen = leftover - 4;
                    _paddingAfterVcIsLast = regionIsLast;
                    _vcBlockDataLen = newVCData.Length;
                    _vcIsLast = false;
                }
                else
                {
                    // With fewer than four bytes left there is no room for another metadata
                    // header. Absorb the gap into the comment block as harmless trailing bytes.
                    WriteMetaBlockHeader(fs, 4, available, isLast: regionIsLast);
                    fs.Write(newVCData, 0, newVCData.Length);
                    if (leftover > 0)
                        fs.Write(new byte[leftover], 0, leftover);
                    _vcBlockDataLen = available;
                    _vcIsLast = regionIsLast;
                    _paddingAfterVcOffset = -1;
                    _paddingAfterVcDataLen = 0;
                }
                LastSaveWasInPlace = true;
                return;
            }

            // Build list of serialized metadata blocks: (type, data)
            // Order: STREAMINFO first, then other preserved blocks, then VORBIS_COMMENT, then PICTUREs
            var blocks = new List<(byte Type, byte[] Data, int Length)>();

            if (_streamInfoData != null)
                blocks.Add((0, _streamInfoData, _streamInfoData.Length));

            foreach (var block in _otherMetaBlocks)
                blocks.Add(block);

            blocks.Add((4, newVCData, newVCData.Length));

            foreach (VorbisArtwork art in Artworks)
            {
                byte[] data = art.ToByteArray();
                blocks.Add((6, data, data.Length));
            }

            // FLAC metadata block lengths are exactly 24 bits. Truncating a larger length in
            // the header while writing all bytes makes the excess bytes look like audio data.
            foreach (var (_, _, length) in blocks)
                if (length > 0xFFFFFF)
                    throw new InvalidOperationException("A FLAC metadata block cannot exceed 16,777,215 bytes.");

            string tempPath = Tools.CreateSiblingTempPath(target);
            try
            {
                string sourcePath = Filename ?? target;
                {
                    using FileStream source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read);
                    using FileStream dest = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write);

                    // Skip past original fLaC marker and all metadata blocks in source
                    byte[] marker = new byte[4];
                    source.ReadExactly(marker); // "fLaC"

                    // Skip original metadata blocks
                    bool lastSrc = false;
                    while (!lastSrc)
                    {
                        byte[] hdr = new byte[4];
                        source.ReadExactly(hdr);
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
                        var (type, data, blockLen) = blocks[i];
                        WriteMetaBlockHeader(dest, type, blockLen, isLast);
                        if (data is not null)
                            dest.Write(data, 0, data.Length);
                        else
                            dest.Seek(blockLen, SeekOrigin.Current);
                    }

                    // Copy audio data
                    Tools.CopyToEnd(source, dest);
                    dest.Flush(flushToDisk: true);
                }

                Tools.AtomicReplace(tempPath, target);
            }
            catch
            {
                Tools.DeleteIfExists(tempPath);
                throw;
            }
            Filename = target;

            // The metadata layout just changed wholesale; the cached VORBIS_COMMENT offset and
            // PICTURE bytes no longer describe the file. Disable the in-place fast path for any
            // subsequent Save() on this instance (it would otherwise patch stale offsets).
            _vcBlockOffset = -1;
            _originalPictureBlocks = Artworks.Select(a => a.ToByteArray()).ToList();
        }

    }

}
