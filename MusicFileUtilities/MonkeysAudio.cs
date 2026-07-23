using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace MusicFileUtilities
{
    /// <summary>
    /// Monkey's Audio stream parser with trailing APEv2 metadata persistence.
    /// Supports the legacy 3.80-3.97 header and descriptor-based 3.98+ layout.
    /// </summary>
    public sealed class MonkeysAudioFile :
        ApeTaggedAudioFile,
        ICodecProvider
    {
        private const ushort MinimumVersion = 3800;
        private const ushort MaximumVersion = 3990;
        private const ushort HasPeakLevel = 4;
        private const ushort Is24Bit = 8;
        private const ushort HasSeekElements = 16;
        private const ushort Is8Bit = 1;

        public MonkeysAudioFile(
            string filename,
            bool readArtwork = true)
            : this(filename, readArtwork, knownLength: null)
        {
        }

        internal MonkeysAudioFile(
            string filename,
            bool readArtwork,
            long? knownLength)
            : base(filename)
        {
            using FileStream stream =
                Tools.OpenReadSequential(filename);
            long fileLength = knownLength ?? stream.Length;
            if (fileLength != stream.Length)
                fileLength = stream.Length;
            LoadApeTag(stream, readArtwork, fileLength);
            Parse(stream, AudioEndOffset);
        }

        public override IEnumerable<ICodecProvider> Codecs
        {
            get { yield return this; }
        }

        public string CodecName => "Monkey's Audio";
        public CodecType CodecType => CodecType.Lossless;
        public uint AverageBitrate { get; private set; }
        public uint MaxBitrate => AverageBitrate;
        public uint BitsPerSample { get; private set; }
        public uint Samplerate { get; private set; }
        public uint Channels { get; private set; }
        public uint DurationInFrames { get; private set; }
        public uint DurationInSeconds =>
            DurationInFrames / 75;
        public ushort FileVersion { get; private set; }
        public ushort CompressionLevel { get; private set; }

        private void Parse(
            Stream stream,
            long payloadEnd)
        {
            long macStart = FindStreamStart(stream, payloadEnd);
            Span<byte> prefix = stackalloc byte[6];
            ReadAt(stream, macStart, prefix, payloadEnd);
            if (!prefix[..4].SequenceEqual("MAC "u8))
                throw new InvalidDataException(
                    "The file is not a Monkey's Audio stream.");

            FileVersion =
                BinaryPrimitives.ReadUInt16LittleEndian(prefix[4..]);
            if (FileVersion < MinimumVersion ||
                FileVersion > MaximumVersion)
                throw new InvalidDataException(
                    $"Unsupported Monkey's Audio version {FileVersion}.");

            if (FileVersion >= 3980)
                ParseModern(stream, macStart, payloadEnd);
            else
                ParseLegacy(stream, macStart, payloadEnd);
        }

        private void ParseModern(
            Stream stream,
            long macStart,
            long payloadEnd)
        {
            Span<byte> descriptor = stackalloc byte[52];
            ReadAt(stream, macStart, descriptor, payloadEnd);
            uint descriptorLength =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    descriptor[8..12]);
            uint headerLength =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    descriptor[12..16]);
            uint seekTableLength =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    descriptor[16..20]);
            uint waveHeaderLength =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    descriptor[20..24]);
            ulong audioDataLength =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    descriptor[24..28]) |
                ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(
                    descriptor[28..32]) << 32);
            uint waveTailLength =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    descriptor[32..36]);
            if (descriptorLength < 52 ||
                headerLength < 24 ||
                seekTableLength / 4 < 1)
                throw new InvalidDataException(
                    "Invalid Monkey's Audio descriptor lengths.");

            long headerStart = AddChecked(
                macStart, descriptorLength);
            Span<byte> header = stackalloc byte[24];
            ReadAt(stream, headerStart, header, payloadEnd);
            CompressionLevel =
                BinaryPrimitives.ReadUInt16LittleEndian(header);
            uint blocksPerFrame =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    header[4..8]);
            uint finalFrameBlocks =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    header[8..12]);
            uint totalFrames =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    header[12..16]);
            BitsPerSample =
                BinaryPrimitives.ReadUInt16LittleEndian(
                    header[16..18]);
            Channels =
                BinaryPrimitives.ReadUInt16LittleEndian(
                    header[18..20]);
            Samplerate =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    header[20..24]);
            ValidateTechnicalProperties(
                blocksPerFrame,
                finalFrameBlocks,
                totalFrames);
            if (seekTableLength / 4 < totalFrames)
                throw new InvalidDataException(
                    "Monkey's Audio seek table is truncated.");

            long audioStart = AddChecked(
                macStart,
                descriptorLength,
                headerLength,
                seekTableLength,
                waveHeaderLength);
            if (audioStart < macStart ||
                payloadEnd < audioStart)
                throw new InvalidDataException(
                    "Monkey's Audio data exceeds the file bounds.");
            ulong available =
                (ulong)(payloadEnd - audioStart);
            if (audioDataLength == 0 ||
                audioDataLength >
                    ulong.MaxValue - waveTailLength ||
                audioDataLength + waveTailLength > available)
                throw new InvalidDataException(
                    "Monkey's Audio data exceeds the file bounds.");

            SetDurationAndBitrate(
                blocksPerFrame,
                finalFrameBlocks,
                totalFrames,
                audioDataLength);
        }

        private void ParseLegacy(
            Stream stream,
            long macStart,
            long payloadEnd)
        {
            Span<byte> header = stackalloc byte[32];
            ReadAt(stream, macStart, header, payloadEnd);
            CompressionLevel =
                BinaryPrimitives.ReadUInt16LittleEndian(
                    header[6..8]);
            ushort formatFlags =
                BinaryPrimitives.ReadUInt16LittleEndian(
                    header[8..10]);
            Channels =
                BinaryPrimitives.ReadUInt16LittleEndian(
                    header[10..12]);
            Samplerate =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    header[12..16]);
            uint waveHeaderLength =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    header[16..20]);
            uint waveTailLength =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    header[20..24]);
            uint totalFrames =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    header[24..28]);
            uint finalFrameBlocks =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    header[28..32]);

            long headerLength = 32;
            if ((formatFlags & HasPeakLevel) != 0)
                headerLength += 4;
            ulong seekTableLength;
            if ((formatFlags & HasSeekElements) != 0)
            {
                Span<byte> seekElements = stackalloc byte[4];
                ReadAt(
                    stream,
                    AddChecked(
                        macStart,
                        checked((ulong)headerLength)),
                    seekElements,
                    payloadEnd);
                seekTableLength = checked(
                    (ulong)BinaryPrimitives
                        .ReadUInt32LittleEndian(seekElements) * 4);
                headerLength += 4;
            }
            else
                seekTableLength = checked((ulong)totalFrames * 4);

            BitsPerSample = (formatFlags & Is8Bit) != 0
                ? 8u
                : (formatFlags & Is24Bit) != 0
                    ? 24u
                    : 16u;
            uint blocksPerFrame = FileVersion >= 3950
                ? 73728u * 4
                : FileVersion >= 3900 ||
                  (FileVersion >= 3800 &&
                   CompressionLevel >= 4000)
                    ? 73728u
                    : 9216u;
            ValidateTechnicalProperties(
                blocksPerFrame,
                finalFrameBlocks,
                totalFrames);
            if (seekTableLength / 4 < totalFrames)
                throw new InvalidDataException(
                    "Monkey's Audio seek table is truncated.");

            long audioStart = AddChecked(
                macStart,
                checked((ulong)headerLength),
                seekTableLength,
                waveHeaderLength,
                FileVersion < 3810 ? totalFrames : 0);
            if (audioStart < macStart ||
                audioStart > payloadEnd ||
                waveTailLength >
                    (ulong)(payloadEnd - audioStart))
                throw new InvalidDataException(
                    "Monkey's Audio data exceeds the file bounds.");
            long compressedLength =
                payloadEnd - audioStart - waveTailLength;
            if (compressedLength <= 0)
                throw new InvalidDataException(
                    "Monkey's Audio stream has no compressed data.");

            SetDurationAndBitrate(
                blocksPerFrame,
                finalFrameBlocks,
                totalFrames,
                checked((ulong)compressedLength));
        }

        private void ValidateTechnicalProperties(
            uint blocksPerFrame,
            uint finalFrameBlocks,
            uint totalFrames)
        {
            if (totalFrames == 0 ||
                blocksPerFrame == 0 ||
                finalFrameBlocks == 0 ||
                finalFrameBlocks > blocksPerFrame ||
                Samplerate == 0 ||
                Channels == 0 ||
                BitsPerSample == 0 ||
                BitsPerSample > 32)
                throw new InvalidDataException(
                    "Invalid Monkey's Audio technical properties.");
        }

        private void SetDurationAndBitrate(
            uint blocksPerFrame,
            uint finalFrameBlocks,
            uint totalFrames,
            ulong compressedBytes)
        {
            ulong totalSamples = checked(
                (ulong)(totalFrames - 1) *
                blocksPerFrame + finalFrameBlocks);
            DurationInFrames = ScaleRatio(
                totalSamples, 75, Samplerate);
            AverageBitrate = ScaleRatio(
                compressedBytes, 8UL * Samplerate, totalSamples);
        }

        private static long FindStreamStart(
            Stream stream,
            long payloadEnd)
        {
            if (payloadEnd < 6)
                throw new InvalidDataException(
                    "Truncated Monkey's Audio stream.");
            Span<byte> prefix = stackalloc byte[10];
            int prefixLength =
                checked((int)Math.Min(prefix.Length, payloadEnd));
            ReadAt(
                stream,
                0,
                prefix[..prefixLength],
                payloadEnd);
            if (prefixLength >= 4 &&
                prefix[..4].SequenceEqual("MAC "u8))
                return 0;
            if (prefixLength < 10 ||
                !prefix[..3].SequenceEqual("ID3"u8) ||
                prefix[6] > 0x7f ||
                prefix[7] > 0x7f ||
                prefix[8] > 0x7f ||
                prefix[9] > 0x7f)
                throw new InvalidDataException(
                    "The file is not a Monkey's Audio stream.");
            int size =
                (prefix[6] << 21) |
                (prefix[7] << 14) |
                (prefix[8] << 7) |
                prefix[9];
            long start =
                10L + size +
                ((prefix[5] & 0x10) != 0 ? 10 : 0);
            if (start > payloadEnd - 6)
                throw new InvalidDataException(
                    "Truncated leading ID3v2 tag.");
            return start;
        }

        private static void ReadAt(
            Stream stream,
            long offset,
            Span<byte> buffer,
            long payloadEnd)
        {
            if (offset < 0 ||
                buffer.Length > payloadEnd - offset)
                throw new InvalidDataException(
                    "Truncated Monkey's Audio header.");
            stream.Position = offset;
            stream.ReadExactly(buffer);
        }

        private static long AddChecked(
            long start,
            params ulong[] values)
        {
            if (start < 0)
                throw new InvalidDataException(
                    "Monkey's Audio offset is negative.");
            ulong result = (ulong)start;
            foreach (ulong value in values)
            {
                if (value > (ulong)long.MaxValue - result)
                    throw new InvalidDataException(
                        "Monkey's Audio offset exceeds the supported range.");
                result += value;
            }
            return (long)result;
        }

        private static uint ScaleRatio(
            ulong value,
            ulong multiplier,
            ulong divisor)
        {
            if (divisor == 0)
                return 0;
            decimal result =
                (decimal)value * multiplier / divisor;
            return result >= uint.MaxValue
                ? uint.MaxValue
                : (uint)result;
        }
    }
}
