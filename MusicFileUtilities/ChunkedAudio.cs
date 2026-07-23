using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MusicFileUtilities
{
    /// <summary>
    /// Shared native chunk handling for RIFF/RF64 WAVE and FORM AIFF/AIFC files.
    /// Metadata remains an ordinary ID3v2 tag, but its bytes are owned by a container chunk.
    /// </summary>
    public abstract class ChunkedAudioFile :
        ID3v2Tag, ICodecProvider, IMediaFile, IMetadataWriter
    {
        private const uint SizeSentinel = uint.MaxValue;
        private readonly bool _littleEndian;
        private string _containerId = "";
        private string _formType = "";
        private string _id3ChunkId;

        protected ChunkedAudioFile(
            string filename,
            bool littleEndian,
            bool readArtwork,
            long? knownLength)
        {
            _filename = filename;
            _littleEndian = littleEndian;
            Parse(readArtwork, knownLength);
        }

        public IEnumerable<ICodecProvider> Codecs
        {
            get { yield return this; }
        }

        public IEnumerable<IMetadataProvider> Tags
        {
            get { yield return this; }
        }

        public string CodecName { get; private set; } = "";
        public CodecType CodecType { get; private set; } = CodecType.Lossless;
        public uint AverageBitrate { get; private set; }
        public uint MaxBitrate => AverageBitrate;
        public uint BitsPerSample { get; private set; }
        public uint Samplerate { get; private set; }
        public uint Channels { get; private set; }
        public uint DurationInFrames { get; private set; }
        public uint DurationInSeconds => DurationInFrames / 75;

        public bool IsRf64 => _containerId == "RF64";
        public bool IsAifc => _formType == "AIFC";

        public void SaveTags(string outputPath = null)
        {
            string target = outputPath ?? _filename
                ?? throw new InvalidOperationException(
                    "No filename associated with this file.");
            byte[] tag = BuildTagBytes();
            RewriteContainer(target, tag);
            _filename = target;
        }

        // ID3v2Tag.Save assumes a leading tag. Hide and explicitly remap it so callers using
        // IMetadataWriter cannot accidentally rewrite a chunked container as an MP3-like file.
        public new void Save(string outputPath = null) => SaveTags(outputPath);
        void IMetadataWriter.Save(string outputPath) => SaveTags(outputPath);

        private void Parse(bool readArtwork, long? knownLength)
        {
            using FileStream stream = Tools.OpenReadSequential(_filename);
            long fileLength = knownLength ?? stream.Length;
            if (fileLength < 12)
                throw new InvalidDataException("Truncated audio container.");

            byte[] containerHeader = new byte[12];
            stream.ReadExactly(containerHeader);
            _containerId = Encoding.ASCII.GetString(containerHeader, 0, 4);
            _formType = Encoding.ASCII.GetString(containerHeader, 8, 4);
            ValidateContainer();

            var rf64 = new Rf64SizeState();
            ulong dataSize = 0;
            uint blockAlign = 0;
            uint sampleFrames = 0;
            bool foundFormat = false;
            bool foundData = false;
            bool foundTag = false;

            while (stream.Position + 8 <= fileLength)
            {
                long chunkHeaderOffset = stream.Position;
                byte[] chunkHeader = new byte[8];
                stream.ReadExactly(chunkHeader);
                string chunkId = Encoding.ASCII.GetString(chunkHeader, 0, 4);
                uint declaredSize = ReadUInt32(chunkHeader.AsSpan(4));
                long dataOffset = stream.Position;

                if (_littleEndian && chunkId == "ds64")
                    rf64 = ReadDs64(stream, declaredSize);
                ulong actualSize = ResolveChunkSize(
                    chunkId, declaredSize, rf64);
                long next = ValidateAndGetNextChunk(
                    dataOffset, actualSize, fileLength, chunkId);

                if (_littleEndian && chunkId == "fmt ")
                {
                    ParseWaveFormat(stream, actualSize, out blockAlign);
                    foundFormat = true;
                }
                else if (!_littleEndian && chunkId == "COMM")
                {
                    ParseAiffFormat(stream, actualSize, out sampleFrames);
                    foundFormat = true;
                }
                else if ((_littleEndian && chunkId == "data") ||
                         (!_littleEndian && chunkId == "SSND"))
                {
                    dataSize = actualSize;
                    foundData = true;
                }
                else if (IsId3Chunk(chunkId))
                {
                    if (!foundTag)
                    {
                        ReadId3Chunk(stream, actualSize, readArtwork);
                        _id3ChunkId = chunkId;
                        foundTag = true;
                    }
                }

                stream.Position = next;
                if (stream.Position <= chunkHeaderOffset)
                    throw new InvalidDataException(
                        $"Invalid non-advancing '{chunkId}' chunk.");
            }

            if (!foundFormat)
                throw new InvalidDataException(
                    $"{_formType} format chunk is missing.");
            if (!foundData)
                throw new InvalidDataException(
                    $"{_formType} audio-data chunk is missing.");
            if (!foundTag)
                ParseStandardFields();

            if (_littleEndian)
            {
                ulong samples = blockAlign == 0 ? 0 : dataSize / blockAlign;
                DurationInFrames = ScaleSamplesToCdFrames(samples, Samplerate);
            }
            else
            {
                DurationInFrames = ScaleSamplesToCdFrames(
                    sampleFrames, Samplerate);
            }
        }

        private void ValidateContainer()
        {
            if (_littleEndian)
            {
                if ((_containerId != "RIFF" && _containerId != "RF64") ||
                    _formType != "WAVE")
                    throw new InvalidDataException(
                        "The file is not a RIFF/RF64 WAVE container.");
                _id3ChunkId = "id3 ";
            }
            else
            {
                if (_containerId != "FORM" ||
                    (_formType != "AIFF" && _formType != "AIFC"))
                    throw new InvalidDataException(
                        "The file is not an AIFF/AIFC FORM container.");
                _id3ChunkId = "ID3 ";
            }
        }

        private void ParseWaveFormat(
            Stream stream,
            ulong size,
            out uint blockAlign)
        {
            if (size < 16)
                throw new InvalidDataException("Truncated WAVE fmt chunk.");
            Span<byte> format = stackalloc byte[16];
            stream.ReadExactly(format);
            ushort encoding = BinaryPrimitives.ReadUInt16LittleEndian(format);
            Channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
            Samplerate = BinaryPrimitives.ReadUInt32LittleEndian(format[4..]);
            uint byteRate = BinaryPrimitives.ReadUInt32LittleEndian(format[8..]);
            blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(format[12..]);
            BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(format[14..]);
            AverageBitrate = ClampUInt32((ulong)byteRate * 8);

            (CodecName, CodecType) = encoding switch
            {
                0x0001 => ("PCM", CodecType.Lossless),
                0x0003 => ("IEEE Float", CodecType.Lossless),
                0x0006 => ("A-law", CodecType.Lossy),
                0x0007 => ("mu-law", CodecType.Lossy),
                0xFFFE => ("WAVE Extensible", CodecType.Lossless),
                _ => ($"WAVE 0x{encoding:X4}", CodecType.Lossy),
            };
        }

        private void ParseAiffFormat(
            Stream stream,
            ulong size,
            out uint sampleFrames)
        {
            if (size < 18)
                throw new InvalidDataException("Truncated AIFF COMM chunk.");
            Span<byte> common = stackalloc byte[22];
            int readSize = _formType == "AIFC" ? 22 : 18;
            if (size < (ulong)readSize)
                throw new InvalidDataException("Truncated AIFC COMM chunk.");
            stream.ReadExactly(common[..readSize]);

            Channels = BinaryPrimitives.ReadUInt16BigEndian(common);
            sampleFrames = BinaryPrimitives.ReadUInt32BigEndian(common[2..]);
            BitsPerSample = BinaryPrimitives.ReadUInt16BigEndian(common[6..]);
            Samplerate = DecodeExtendedSampleRate(common.Slice(8, 10));
            string compression = _formType == "AIFC"
                ? Encoding.ASCII.GetString(common.Slice(18, 4))
                : "NONE";
            (CodecName, CodecType) = compression switch
            {
                "NONE" or "twos" => ("PCM", CodecType.Lossless),
                "sowt" => ("PCM (little-endian)", CodecType.Lossless),
                "fl32" or "FL32" => ("IEEE Float", CodecType.Lossless),
                "fl64" or "FL64" => ("IEEE Double", CodecType.Lossless),
                "alaw" or "ALAW" => ("A-law", CodecType.Lossy),
                "ulaw" or "ULAW" => ("mu-law", CodecType.Lossy),
                _ => ($"AIFC {compression}", CodecType.Lossy),
            };
            AverageBitrate = ClampUInt32(
                (ulong)BitsPerSample * Samplerate * Channels);
        }

        private void ReadId3Chunk(
            Stream stream,
            ulong size,
            bool readArtwork)
        {
            if (size > int.MaxValue)
                throw new InvalidDataException("ID3 chunk is too large.");
            byte[] bytes = new byte[(int)size];
            stream.ReadExactly(bytes);
            using var tag = new MemoryStream(bytes, writable: false);
            ReadTag(tag, readArtwork, bytes.Length);
        }

        private void RewriteContainer(string target, byte[] tag)
        {
            string sourcePath = _filename ?? target;
            string tempPath = Tools.CreateSiblingTempPath(target);
            try
            {
                using (FileStream source = Tools.OpenReadSequential(sourcePath))
                using (FileStream destination = Tools.CreateWriteSequential(tempPath))
                {
                    byte[] containerHeader = new byte[12];
                    source.ReadExactly(containerHeader);
                    destination.Write(containerHeader);

                    var rf64 = new Rf64SizeState();
                    long ds64OutputDataOffset = -1;
                    bool wroteTag = false;
                    while (source.Position + 8 <= source.Length)
                    {
                        long sourceChunkStart = source.Position;
                        byte[] chunkHeader = new byte[8];
                        source.ReadExactly(chunkHeader);
                        string chunkId = Encoding.ASCII.GetString(
                            chunkHeader, 0, 4);
                        uint declaredSize = ReadUInt32(
                            chunkHeader.AsSpan(4));
                        long sourceDataOffset = source.Position;

                        if (_littleEndian && chunkId == "ds64")
                        {
                            rf64 = ReadDs64(source, declaredSize);
                            source.Position = sourceDataOffset;
                        }
                        ulong actualSize = ResolveChunkSize(
                            chunkId, declaredSize, rf64);
                        long next = ValidateAndGetNextChunk(
                            sourceDataOffset,
                            actualSize,
                            source.Length,
                            chunkId);

                        if (IsId3Chunk(chunkId))
                        {
                            if (!wroteTag)
                            {
                                WriteId3Chunk(destination, chunkId, tag);
                                wroteTag = true;
                            }
                            source.Position = next;
                            continue;
                        }

                        long outputChunkStart = destination.Position;
                        source.Position = sourceChunkStart;
                        Tools.CopyExactly(
                            source,
                            destination,
                            checked(next - sourceChunkStart));
                        if (_littleEndian && chunkId == "ds64")
                            ds64OutputDataOffset = outputChunkStart + 8;
                    }

                    if (source.Position < source.Length)
                        Tools.CopyToEnd(source, destination);
                    if (!wroteTag)
                        WriteId3Chunk(destination, _id3ChunkId, tag);

                    long outputLength = destination.Length;
                    PatchContainerSize(
                        destination, outputLength, ds64OutputDataOffset);
                    destination.Flush(flushToDisk: true);
                }

                Tools.AtomicReplace(tempPath, target);
            }
            catch
            {
                Tools.DeleteIfExists(tempPath);
                throw;
            }
        }

        private void WriteId3Chunk(
            Stream destination,
            string chunkId,
            byte[] tag)
        {
            if ((ulong)tag.Length > uint.MaxValue)
                throw new InvalidOperationException(
                    "ID3 chunk exceeds the container size limit.");
            Span<byte> header = stackalloc byte[8];
            Encoding.ASCII.GetBytes(chunkId, header);
            WriteUInt32(header[4..], (uint)tag.Length);
            destination.Write(header);
            destination.Write(tag);
            if ((tag.Length & 1) != 0)
                destination.WriteByte(0);
        }

        private void PatchContainerSize(
            FileStream destination,
            long outputLength,
            long ds64OutputDataOffset)
        {
            ulong formSize = checked((ulong)(outputLength - 8));
            if (_containerId == "RF64")
            {
                if (ds64OutputDataOffset < 0)
                    throw new InvalidDataException(
                        "RF64 container has no ds64 chunk.");
                destination.Position = 4;
                Span<byte> sentinel = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(
                    sentinel, SizeSentinel);
                destination.Write(sentinel);
                destination.Position = ds64OutputDataOffset;
                Span<byte> size64 = stackalloc byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(size64, formSize);
                destination.Write(size64);
            }
            else
            {
                if (formSize > uint.MaxValue)
                    throw new InvalidOperationException(
                        "Container exceeds its 32-bit size limit.");
                destination.Position = 4;
                Span<byte> size32 = stackalloc byte[4];
                WriteUInt32(size32, (uint)formSize);
                destination.Write(size32);
            }
        }

        private Rf64SizeState ReadDs64(Stream stream, uint declaredSize)
        {
            if (declaredSize < 28)
                throw new InvalidDataException("Truncated RF64 ds64 chunk.");
            Span<byte> fixedFields = stackalloc byte[28];
            stream.ReadExactly(fixedFields);
            var state = new Rf64SizeState
            {
                RiffSize = BinaryPrimitives.ReadUInt64LittleEndian(fixedFields),
                DataSize = BinaryPrimitives.ReadUInt64LittleEndian(
                    fixedFields[8..]),
            };
            uint tableLength = BinaryPrimitives.ReadUInt32LittleEndian(
                fixedFields[24..]);
            ulong required = 28UL + 12UL * tableLength;
            if (required > declaredSize)
                throw new InvalidDataException(
                    "Truncated RF64 ds64 size table.");
            Span<byte> entry = stackalloc byte[12];
            for (uint index = 0; index < tableLength; index++)
            {
                stream.ReadExactly(entry);
                string id = Encoding.ASCII.GetString(entry[..4]);
                ulong size = BinaryPrimitives.ReadUInt64LittleEndian(
                    entry[4..]);
                if (!state.AdditionalSizes.TryGetValue(
                        id, out Queue<ulong> values))
                {
                    values = new Queue<ulong>();
                    state.AdditionalSizes.Add(id, values);
                }
                values.Enqueue(size);
            }
            Tools.SkipExactly(stream, checked((long)(declaredSize - required)));
            return state;
        }

        private static ulong ResolveChunkSize(
            string chunkId,
            uint declaredSize,
            Rf64SizeState rf64)
        {
            if (declaredSize != SizeSentinel)
                return declaredSize;
            if (chunkId == "data" && rf64.DataSize.HasValue)
                return rf64.DataSize.Value;
            if (rf64.AdditionalSizes.TryGetValue(
                    chunkId, out Queue<ulong> values) &&
                values.Count != 0)
                return values.Dequeue();
            throw new InvalidDataException(
                $"RF64 chunk '{chunkId}' has no 64-bit size.");
        }

        private static long ValidateAndGetNextChunk(
            long dataOffset,
            ulong size,
            long fileLength,
            string chunkId)
        {
            if (size > long.MaxValue)
                throw new InvalidDataException(
                    $"Chunk '{chunkId}' is too large.");
            long end;
            try
            {
                end = checked(dataOffset + (long)size);
                if ((size & 1) != 0)
                    end = checked(end + 1);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException(
                    $"Chunk '{chunkId}' size overflows the container.",
                    exception);
            }
            if (end > fileLength)
                throw new InvalidDataException(
                    $"Chunk '{chunkId}' extends beyond the file.");
            return end;
        }

        private bool IsId3Chunk(string chunkId) =>
            _littleEndian
                ? chunkId.Equals("id3 ", StringComparison.OrdinalIgnoreCase)
                : chunkId == "ID3 ";

        private uint ReadUInt32(ReadOnlySpan<byte> value) =>
            _littleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(value)
                : BinaryPrimitives.ReadUInt32BigEndian(value);

        private void WriteUInt32(Span<byte> target, uint value)
        {
            if (_littleEndian)
                BinaryPrimitives.WriteUInt32LittleEndian(target, value);
            else
                BinaryPrimitives.WriteUInt32BigEndian(target, value);
        }

        private static uint DecodeExtendedSampleRate(
            ReadOnlySpan<byte> value)
        {
            ushort exponentAndSign =
                BinaryPrimitives.ReadUInt16BigEndian(value);
            bool negative = (exponentAndSign & 0x8000) != 0;
            int exponent = exponentAndSign & 0x7FFF;
            ulong mantissa = BinaryPrimitives.ReadUInt64BigEndian(value[2..]);
            if (negative || exponent == 0 || mantissa == 0)
                return 0;
            if (exponent == 0x7FFF)
                throw new InvalidDataException(
                    "Invalid AIFF sample rate.");
            double rate = mantissa *
                          Math.Pow(2, exponent - 16383 - 63);
            if (!double.IsFinite(rate) ||
                rate <= 0 ||
                rate > uint.MaxValue)
                throw new InvalidDataException(
                    "Invalid AIFF sample rate.");
            return checked((uint)Math.Round(rate));
        }

        private static uint ScaleSamplesToCdFrames(
            ulong samples,
            uint sampleRate)
        {
            if (sampleRate == 0)
                return 0;
            ulong whole = samples / sampleRate;
            ulong remainder = samples % sampleRate;
            ulong frames = whole > ulong.MaxValue / 75
                ? ulong.MaxValue
                : whole * 75;
            if (frames != ulong.MaxValue)
            {
                ulong partial = remainder * 75 / sampleRate;
                frames = frames > ulong.MaxValue - partial
                    ? ulong.MaxValue
                    : frames + partial;
            }
            return ClampUInt32(frames);
        }

        private static uint ClampUInt32(ulong value) =>
            value > uint.MaxValue ? uint.MaxValue : (uint)value;

        private sealed class Rf64SizeState
        {
            public ulong? RiffSize { get; init; }
            public ulong? DataSize { get; init; }
            public Dictionary<string, Queue<ulong>> AdditionalSizes
                { get; } = new(StringComparer.Ordinal);
        }
    }

    public sealed class WaveFile : ChunkedAudioFile
    {
        public WaveFile(string filename, bool readArtwork = true)
            : this(filename, readArtwork, knownLength: null)
        {
        }

        internal WaveFile(
            string filename,
            bool readArtwork,
            long? knownLength)
            : base(filename, littleEndian: true, readArtwork, knownLength)
        {
        }
    }

    public sealed class AiffFile : ChunkedAudioFile
    {
        public AiffFile(string filename, bool readArtwork = true)
            : this(filename, readArtwork, knownLength: null)
        {
        }

        internal AiffFile(
            string filename,
            bool readArtwork,
            long? knownLength)
            : base(filename, littleEndian: false, readArtwork, knownLength)
        {
        }
    }
}
