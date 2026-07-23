using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace MusicFileUtilities
{
    /// <summary>Musepack SV7/SV8 stream with trailing APEv2 metadata.</summary>
    public sealed class MusepackFile :
        ApeTaggedAudioFile,
        ICodecProvider
    {
        private static readonly uint[] SampleRates =
            [44100, 48000, 37800, 32000];

        public MusepackFile(string filename, bool readArtwork = true)
            : this(filename, readArtwork, knownLength: null)
        {
        }

        internal MusepackFile(
            string filename,
            bool readArtwork,
            long? knownLength)
            : base(filename)
        {
            using FileStream stream = Tools.OpenReadSequential(filename);
            long length = knownLength ?? stream.Length;
            LoadApeTag(stream, readArtwork, length);
            Parse(stream);
        }

        public override IEnumerable<ICodecProvider> Codecs
        {
            get { yield return this; }
        }

        public string CodecName => "Musepack";
        public CodecType CodecType => CodecType.Lossy;
        public uint AverageBitrate { get; private set; }
        public uint MaxBitrate => AverageBitrate;
        public uint BitsPerSample => 16;
        public uint Samplerate { get; private set; }
        public uint Channels { get; private set; }
        public uint DurationInFrames { get; private set; }
        public uint DurationInSeconds => DurationInFrames / 75;
        public int StreamVersion { get; private set; }

        private void Parse(Stream stream)
        {
            if (AudioEndOffset < 8)
                throw new InvalidDataException("Truncated Musepack stream.");
            Span<byte> signature = stackalloc byte[4];
            ReadAt(stream, 0, signature);
            ulong totalSamples;
            if (signature[..3].SequenceEqual("MP+"u8))
                totalSamples = ParseSv7(stream, signature[3]);
            else if (signature.SequenceEqual("MPCK"u8))
                totalSamples = ParseSv8(stream);
            else
                throw new InvalidDataException(
                    "The file is not a Musepack stream.");

            if (totalSamples == 0 || Samplerate == 0 || Channels == 0)
                throw new InvalidDataException(
                    "Invalid Musepack technical properties.");
            DurationInFrames = ScaleRatio(
                totalSamples, 75, Samplerate);
            AverageBitrate = ScaleRatio(
                checked((ulong)AudioEndOffset),
                8UL * Samplerate,
                totalSamples);
        }

        private ulong ParseSv7(Stream stream, byte version)
        {
            if (version is not (0x07 or 0x17) ||
                AudioEndOffset < 24)
                throw new InvalidDataException(
                    "Unsupported Musepack SV7 header.");
            Span<byte> header = stackalloc byte[24];
            ReadAt(stream, 0, header);
            uint frameCount =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    header[4..8]);
            if (frameCount == 0)
                throw new InvalidDataException(
                    "Musepack stream reports no frames.");
            StreamVersion = 7;
            Samplerate = SampleRates[header[10] & 3];
            Channels = 2;
            return checked((ulong)frameCount * 1152);
        }

        private ulong ParseSv8(Stream stream)
        {
            long position = 4;
            Span<byte> tag = stackalloc byte[2];
            while (position < AudioEndOffset)
            {
                long chunkStart = position;
                ReadAt(stream, position, tag);
                position += 2;
                ulong encodedSize = ReadVariableLength(
                    stream, ref position);
                long headerLength = position - chunkStart;
                if (encodedSize < (ulong)headerLength ||
                    encodedSize > (ulong)(AudioEndOffset - chunkStart))
                    throw new InvalidDataException(
                        "Invalid Musepack SV8 chunk size.");
                long payloadLength =
                    checked((long)encodedSize - headerLength);
                if (tag.SequenceEqual("SH"u8))
                {
                    if (payloadLength < 9)
                        throw new InvalidDataException(
                            "Truncated Musepack SV8 stream header.");
                    long payloadEnd =
                        checked(position + payloadLength);
                    position += 4; // CRC
                    stream.Position = position;
                    int version = stream.ReadByte();
                    position++;
                    if (version != 8)
                        throw new InvalidDataException(
                            $"Unsupported Musepack stream version {version}.");
                    StreamVersion = version;
                    ulong samples = ReadVariableLength(
                        stream, ref position);
                    _ = ReadVariableLength(
                        stream, ref position); // leading silence
                    if (position + 2 > payloadEnd)
                        throw new InvalidDataException(
                            "Truncated Musepack SV8 technical header.");
                    Span<byte> technical = stackalloc byte[2];
                    ReadAt(stream, position, technical);
                    int rateIndex = technical[0] >> 5;
                    if (rateIndex >= SampleRates.Length)
                        throw new InvalidDataException(
                            "Invalid Musepack sample-rate index.");
                    Samplerate = SampleRates[rateIndex];
                    Channels = (uint)((technical[1] >> 4) + 1);
                    return samples;
                }
                position = checked(chunkStart + (long)encodedSize);
            }
            throw new InvalidDataException(
                "Musepack SV8 stream header was not found.");
        }

        private ulong ReadVariableLength(
            Stream stream,
            ref long position)
        {
            ulong value = 0;
            for (int index = 0; index < 10; index++)
            {
                if (position >= AudioEndOffset)
                    throw new InvalidDataException(
                        "Truncated Musepack variable-length integer.");
                stream.Position = position++;
                int current = stream.ReadByte();
                if (current < 0 ||
                    value > (ulong.MaxValue >> 7))
                    throw new InvalidDataException(
                        "Invalid Musepack variable-length integer.");
                value = (value << 7) |
                    (uint)(current & 0x7f);
                if ((current & 0x80) == 0)
                    return value;
            }
            throw new InvalidDataException(
                "Musepack variable-length integer is too long.");
        }

        private void ReadAt(
            Stream stream,
            long offset,
            Span<byte> buffer)
        {
            if (offset < 0 ||
                buffer.Length > AudioEndOffset - offset)
                throw new InvalidDataException(
                    "Truncated Musepack stream.");
            stream.Position = offset;
            stream.ReadExactly(buffer);
        }

        private static uint ScaleRatio(
            ulong value,
            ulong multiplier,
            ulong divisor)
        {
            decimal result = divisor == 0
                ? 0
                : (decimal)value * multiplier / divisor;
            return result >= uint.MaxValue
                ? uint.MaxValue
                : (uint)result;
        }
    }

    /// <summary>TTA1/TTA2 stream with trailing APEv2 metadata.</summary>
    public sealed class TrueAudioFile :
        ApeTaggedAudioFile,
        ICodecProvider
    {
        public TrueAudioFile(string filename, bool readArtwork = true)
            : this(filename, readArtwork, knownLength: null)
        {
        }

        internal TrueAudioFile(
            string filename,
            bool readArtwork,
            long? knownLength)
            : base(filename)
        {
            using FileStream stream = Tools.OpenReadSequential(filename);
            long length = knownLength ?? stream.Length;
            LoadApeTag(stream, readArtwork, length);
            Parse(stream);
        }

        public override IEnumerable<ICodecProvider> Codecs
        {
            get { yield return this; }
        }

        public string CodecName => "TTA";
        public CodecType CodecType => CodecType.Lossless;
        public uint AverageBitrate { get; private set; }
        public uint MaxBitrate => AverageBitrate;
        public uint BitsPerSample { get; private set; }
        public uint Samplerate { get; private set; }
        public uint Channels { get; private set; }
        public uint DurationInFrames { get; private set; }
        public uint DurationInSeconds => DurationInFrames / 75;
        public ushort FormatVersion { get; private set; }

        private void Parse(Stream stream)
        {
            long start = Id3v2End(stream);
            if (start > AudioEndOffset - 22)
                throw new InvalidDataException("Truncated TTA header.");
            Span<byte> header = stackalloc byte[22];
            stream.Position = start;
            stream.ReadExactly(header);
            if (!header[..4].SequenceEqual("TTA1"u8))
                throw new InvalidDataException(
                    "The file is not a TTA stream.");
            FormatVersion =
                BinaryPrimitives.ReadUInt16LittleEndian(
                    header[4..6]);
            Channels =
                BinaryPrimitives.ReadUInt16LittleEndian(
                    header[6..8]);
            BitsPerSample =
                BinaryPrimitives.ReadUInt16LittleEndian(
                    header[8..10]);
            Samplerate =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    header[10..14]);
            uint sampleCount =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    header[14..18]);
            if (FormatVersion is not (1 or 2) ||
                Channels == 0 ||
                BitsPerSample == 0 ||
                BitsPerSample > 32 ||
                Samplerate == 0 ||
                Samplerate > 1_000_000 ||
                sampleCount == 0)
                throw new InvalidDataException(
                    "Invalid TTA technical properties.");

            ulong frameSamples =
                (ulong)Samplerate * 256 / 245;
            ulong frameCount =
                ((ulong)sampleCount + frameSamples - 1) /
                frameSamples;
            ulong tableLength = checked(frameCount * 4 + 4);
            long tableStart = checked(start + 22);
            if (tableLength >
                (ulong)(AudioEndOffset - tableStart))
                throw new InvalidDataException(
                    "Truncated TTA seek table.");
            ulong compressedBytes = 0;
            stream.Position = tableStart;
            Span<byte> sizeBytes = stackalloc byte[4];
            for (ulong index = 0; index < frameCount; index++)
            {
                stream.ReadExactly(sizeBytes);
                compressedBytes = checked(
                    compressedBytes +
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        sizeBytes));
            }
            long frameStart = checked(
                tableStart + (long)tableLength);
            if (compressedBytes >
                (ulong)(AudioEndOffset - frameStart))
                throw new InvalidDataException(
                    "TTA frames exceed the file bounds.");

            DurationInFrames = ScaleRatio(
                sampleCount, 75, Samplerate);
            AverageBitrate = ScaleRatio(
                compressedBytes,
                8UL * Samplerate,
                sampleCount);
        }

        private static long Id3v2End(Stream stream)
        {
            if (stream.Length < 10)
                return 0;
            Span<byte> header = stackalloc byte[10];
            stream.Position = 0;
            stream.ReadExactly(header);
            if (!header[..3].SequenceEqual("ID3"u8) ||
                header[6] > 0x7f ||
                header[7] > 0x7f ||
                header[8] > 0x7f ||
                header[9] > 0x7f)
                return 0;
            int size =
                (header[6] << 21) |
                (header[7] << 14) |
                (header[8] << 7) |
                header[9];
            return checked(
                10L + size +
                ((header[5] & 0x10) != 0 ? 10 : 0));
        }

        private static uint ScaleRatio(
            ulong value,
            ulong multiplier,
            ulong divisor)
        {
            decimal result = divisor == 0
                ? 0
                : (decimal)value * multiplier / divisor;
            return result >= uint.MaxValue
                ? uint.MaxValue
                : (uint)result;
        }
    }

    /// <summary>TAK stream-info parser with trailing APEv2 metadata.</summary>
    public sealed class TakFile :
        ApeTaggedAudioFile,
        ICodecProvider
    {
        public TakFile(string filename, bool readArtwork = true)
            : this(filename, readArtwork, knownLength: null)
        {
        }

        internal TakFile(
            string filename,
            bool readArtwork,
            long? knownLength)
            : base(filename)
        {
            using FileStream stream = Tools.OpenReadSequential(filename);
            long length = knownLength ?? stream.Length;
            LoadApeTag(stream, readArtwork, length);
            Parse(stream);
        }

        public override IEnumerable<ICodecProvider> Codecs
        {
            get { yield return this; }
        }

        public string CodecName => "TAK";
        public CodecType CodecType => CodecType.Lossless;
        public uint AverageBitrate { get; private set; }
        public uint MaxBitrate => AverageBitrate;
        public uint BitsPerSample { get; private set; }
        public uint Samplerate { get; private set; }
        public uint Channels { get; private set; }
        public uint DurationInFrames { get; private set; }
        public uint DurationInSeconds => DurationInFrames / 75;
        public ulong SampleCount { get; private set; }

        private void Parse(Stream stream)
        {
            if (AudioEndOffset < 8)
                throw new InvalidDataException("Truncated TAK stream.");
            Span<byte> signature = stackalloc byte[4];
            stream.Position = 0;
            stream.ReadExactly(signature);
            if (!signature.SequenceEqual("tBaK"u8))
                throw new InvalidDataException(
                    "The file is not a TAK stream.");

            long position = 4;
            bool foundInfo = false;
            Span<byte> metadataHeader = stackalloc byte[4];
            while (position <= AudioEndOffset - 4)
            {
                stream.Position = position;
                stream.ReadExactly(metadataHeader);
                position += 4;
                int type = metadataHeader[0] & 0x7f;
                int size =
                    metadataHeader[1] |
                    (metadataHeader[2] << 8) |
                    (metadataHeader[3] << 16);
                if (type == 0)
                    break;
                if (size < 0 ||
                    size > AudioEndOffset - position)
                    throw new InvalidDataException(
                        "Invalid TAK metadata block size.");
                if (type == 1)
                {
                    if (foundInfo || size <= 3)
                        throw new InvalidDataException(
                            "Invalid TAK stream-info block.");
                    byte[] info = new byte[size - 3];
                    stream.Position = position;
                    stream.ReadExactly(info);
                    ParseStreamInfo(info);
                    foundInfo = true;
                }
                position += size;
            }
            if (!foundInfo ||
                position >= AudioEndOffset ||
                SampleCount == 0 ||
                Samplerate == 0 ||
                Channels == 0)
                throw new InvalidDataException(
                    "TAK stream information is missing or invalid.");

            DurationInFrames = ScaleRatio(
                SampleCount, 75, Samplerate);
            AverageBitrate = ScaleRatio(
                checked((ulong)(AudioEndOffset - position)),
                8UL * Samplerate,
                SampleCount);
        }

        private void ParseStreamInfo(ReadOnlySpan<byte> data)
        {
            var bits = new LittleEndianBitReader(data);
            _ = bits.Read(6); // codec
            _ = bits.Read(4); // profile
            _ = bits.Read(4); // frame duration type
            SampleCount = bits.Read(35);
            _ = bits.Read(3); // data type
            Samplerate = checked((uint)bits.Read(18) + 6000);
            BitsPerSample = checked((uint)bits.Read(5) + 8);
            Channels = checked((uint)bits.Read(4) + 1);
            if (bits.Read(1) != 0)
            {
                _ = bits.Read(5); // valid bits
                if (bits.Read(1) != 0)
                    for (uint channel = 0;
                         channel < Channels;
                         channel++)
                        _ = bits.Read(6);
            }
            if (Samplerate > 268143 ||
                BitsPerSample > 39 ||
                Channels > 16)
                throw new InvalidDataException(
                    "Invalid TAK technical properties.");
        }

        private static uint ScaleRatio(
            ulong value,
            ulong multiplier,
            ulong divisor)
        {
            decimal result = divisor == 0
                ? 0
                : (decimal)value * multiplier / divisor;
            return result >= uint.MaxValue
                ? uint.MaxValue
                : (uint)result;
        }

        private ref struct LittleEndianBitReader
        {
            private readonly ReadOnlySpan<byte> _data;
            private int _bitOffset;

            public LittleEndianBitReader(ReadOnlySpan<byte> data) =>
                _data = data;

            public ulong Read(int count)
            {
                if (count < 0 ||
                    count > 64 ||
                    count > _data.Length * 8 - _bitOffset)
                    throw new InvalidDataException(
                        "Truncated TAK stream information.");
                ulong value = 0;
                for (int bit = 0; bit < count; bit++)
                {
                    int source = _bitOffset++;
                    value |= (ulong)(
                        (_data[source / 8] >>
                         (source % 8)) & 1) << bit;
                }
                return value;
            }
        }
    }

    /// <summary>OptimFROG Lossless/Float/DualStream with trailing APEv2 metadata.</summary>
    public sealed class OptimFrogFile :
        ApeTaggedAudioFile,
        ICodecProvider
    {
        private readonly CodecType _codecType;
        private readonly string _codecName;

        public OptimFrogFile(string filename, bool readArtwork = true)
            : this(filename, readArtwork, knownLength: null)
        {
        }

        internal OptimFrogFile(
            string filename,
            bool readArtwork,
            long? knownLength)
            : base(filename)
        {
            string extension =
                Path.GetExtension(filename).ToLowerInvariant();
            _codecType = extension == ".ofs"
                ? CodecType.Lossy
                : CodecType.Lossless;
            _codecName = extension switch
            {
                ".ofs" => "OptimFROG DualStream",
                ".off" => "OptimFROG Float",
                _ => "OptimFROG",
            };
            using FileStream stream = Tools.OpenReadSequential(filename);
            long length = knownLength ?? stream.Length;
            LoadApeTag(stream, readArtwork, length);
            Parse(stream);
        }

        public override IEnumerable<ICodecProvider> Codecs
        {
            get { yield return this; }
        }

        public string CodecName => _codecName;
        public CodecType CodecType => _codecType;
        public uint AverageBitrate { get; private set; }
        public uint MaxBitrate => AverageBitrate;
        public uint BitsPerSample { get; private set; }
        public uint Samplerate { get; private set; }
        public uint Channels { get; private set; }
        public uint DurationInFrames { get; private set; }
        public uint DurationInSeconds => DurationInFrames / 75;
        public uint EncoderVersion { get; private set; }

        private void Parse(Stream stream)
        {
            if (AudioEndOffset < 23)
                throw new InvalidDataException(
                    "Truncated OptimFROG stream.");
            Span<byte> header = stackalloc byte[23];
            stream.Position = 0;
            stream.ReadExactly(header);
            if (!header[..4].SequenceEqual("OFR "u8) &&
                !header[..4].SequenceEqual("OFRX"u8))
                throw new InvalidDataException(
                    "The file is not an OptimFROG stream.");
            uint headerSize =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    header[4..8]);
            if (headerSize < 15 ||
                headerSize > 4096 ||
                8L + headerSize >= AudioEndOffset)
                throw new InvalidDataException(
                    "Invalid OptimFROG header size.");
            ulong sampleValues = 0;
            for (int index = 0; index < 6; index++)
                sampleValues |=
                    (ulong)header[8 + index] << (index * 8);
            byte format = header[14];
            byte channelConfiguration = header[15];
            Samplerate =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    header[16..20]);
            ushort packedVersion =
                BinaryPrimitives.ReadUInt16LittleEndian(
                    header[20..22]);
            EncoderVersion = checked(
                (uint)(packedVersion >> 4) + 4200);
            BitsPerSample = format <= 7
                ? checked((uint)(format / 2 + 1) * 8)
                : format <= 10
                    ? 32u
                    : 0u;
            Channels = channelConfiguration <= 1
                ? (uint)channelConfiguration + 1
                : checked((uint)channelConfiguration + 1);
            if (sampleValues == 0 ||
                BitsPerSample == 0 ||
                Samplerate == 0 ||
                Samplerate > 1_000_000 ||
                Channels == 0 ||
                Channels > 32 ||
                sampleValues < Channels ||
                sampleValues % Channels != 0)
                throw new InvalidDataException(
                    "Invalid OptimFROG technical properties.");

            ulong samplePoints = sampleValues / Channels;
            DurationInFrames = ScaleRatio(
                samplePoints, 75, Samplerate);
            AverageBitrate = ScaleRatio(
                checked((ulong)AudioEndOffset),
                8UL * Samplerate,
                samplePoints);
        }

        private static uint ScaleRatio(
            ulong value,
            ulong multiplier,
            ulong divisor)
        {
            decimal result = divisor == 0
                ? 0
                : (decimal)value * multiplier / divisor;
            return result >= uint.MaxValue
                ? uint.MaxValue
                : (uint)result;
        }
    }
}
