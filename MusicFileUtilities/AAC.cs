using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MusicFileUtilities
{
    /// <summary>
    /// Raw AAC/ADTS handler with a leading ID3v2 layer and/or trailing APEv2 layer.
    /// Tag edits rewrite only the outer tag regions and preserve every ADTS frame byte.
    /// </summary>
    public sealed class AACFile :
        IMediaFile,
        ICodecProvider,
        IMetadataWriter,
        IUserStringMetadata,
        IArtworkWriter
    {
        private static readonly uint[] SampleRates =
        [
            96000, 88200, 64000, 48000, 44100, 32000, 24000,
            22050, 16000, 12000, 11025, 8000, 7350,
        ];

        private static readonly uint[] ChannelCounts =
            [0, 1, 2, 3, 4, 5, 6, 8];

        private readonly AacId3Tag _id3;
        private readonly AacApeTag _ape;
        private string _filename;
        private bool _hasId3;
        private bool _hasApe;
        private long _audioStart;
        private long _audioEnd;

        public AACFile(string filename, bool readArtwork = true)
            : this(filename, readArtwork, knownLength: null)
        {
        }

        internal AACFile(
            string filename,
            bool readArtwork,
            long? knownLength)
        {
            _filename = filename;
            _id3 = new AacId3Tag(this);
            _ape = new AacApeTag(this);
            Parse(readArtwork, knownLength);
        }

        public IEnumerable<ICodecProvider> Codecs
        {
            get { yield return this; }
        }

        public IEnumerable<IMetadataProvider> Tags
        {
            get
            {
                if (_hasId3)
                    yield return _id3;
                if (_hasApe)
                    yield return _ape;
                if (!_hasId3 && !_hasApe)
                    yield return _id3;
            }
        }

        public string CodecName => "AAC";
        public CodecType CodecType => CodecType.Lossy;
        public uint AverageBitrate { get; private set; }
        public uint MaxBitrate { get; private set; }
        public uint BitsPerSample => 16;
        public uint Samplerate { get; private set; }
        public uint Channels { get; private set; }
        public uint DurationInFrames { get; private set; }
        public uint DurationInSeconds => DurationInFrames / 75;

        public void SetField(TagFields field, string value) =>
            PrimaryWriter.SetField(field, value);

        public void RemoveField(TagFields field) =>
            PrimaryWriter.RemoveField(field);

        public IEnumerable<KeyValuePair<string, string>> GetUserStrings() =>
            PrimaryUserStrings.GetUserStrings();

        public void SetUserString(string key, string value) =>
            PrimaryUserStrings.SetUserString(key, value);

        public void RemoveUserString(string key) =>
            PrimaryUserStrings.RemoveUserString(key);

        public void SetFrontCover(byte[] imageData, string mimeType) =>
            PrimaryArtwork.SetFrontCover(imageData, mimeType);

        public void RemoveImages() => PrimaryArtwork.RemoveImages();

        public void SetImages(IReadOnlyList<ArtworkImage> images) =>
            PrimaryArtwork.SetImages(images);

        public void Save(string outputPath = null) => SaveTags(outputPath);

        public void SaveTags(string outputPath = null)
        {
            string target = outputPath ?? _filename
                ?? throw new InvalidOperationException(
                    "No filename associated with this file.");
            bool writeId3 = _hasId3 || !_hasApe;
            bool writeApe = _hasApe;
            long audioLength = _audioEnd - _audioStart;
            byte[] id3Bytes = writeId3 ? _id3.Serialize() : [];
            byte[] apeBytes = writeApe ? _ape.ToByteArray() : [];
            string tempPath = Tools.CreateSiblingTempPath(target);
            try
            {
                using (FileStream source = Tools.OpenReadSequential(_filename))
                using (FileStream destination =
                       Tools.CreateWriteSequential(tempPath))
                {
                    if (writeId3)
                        destination.Write(id3Bytes);
                    source.Position = _audioStart;
                    Tools.CopyExactly(
                        source, destination, _audioEnd - _audioStart);
                    if (writeApe)
                        destination.Write(apeBytes);
                    destination.Flush(flushToDisk: true);
                }
                Tools.AtomicReplace(tempPath, target);
            }
            catch
            {
                Tools.DeleteIfExists(tempPath);
                throw;
            }

            _filename = target;
            _hasId3 = writeId3;
            _hasApe = writeApe;
            _audioStart = id3Bytes.Length;
            _audioEnd = _audioStart + audioLength;
        }

        private IMetadataWriter PrimaryWriter =>
            !_hasId3 && _hasApe ? _ape : _id3;

        private IUserStringMetadata PrimaryUserStrings =>
            !_hasId3 && _hasApe ? _ape : _id3;

        private IArtworkWriter PrimaryArtwork =>
            !_hasId3 && _hasApe ? _ape : _id3;

        private void Parse(bool readArtwork, long? knownLength)
        {
            using FileStream stream = Tools.OpenReadSequential(_filename);
            long fileLength = knownLength ?? stream.Length;
            _audioStart = ReadId3Length(stream, fileLength);
            _hasId3 = _audioStart != 0;
            if (_hasId3)
            {
                stream.Position = 0;
                _id3.Load(stream, readArtwork, _audioStart);
            }

            _hasApe = _ape.ReadTag(
                stream,
                onlyAtEnd: true,
                readArtwork: readArtwork,
                knownLength: fileLength);
            _audioEnd = _hasApe ? _ape.AudioEndOffset : fileLength;
            if (_audioEnd <= _audioStart)
                throw new InvalidDataException(
                    "AAC tag layers leave no audio payload.");
            ParseAdts(stream, _audioStart, _audioEnd);
        }

        private void ParseAdts(Stream stream, long start, long end)
        {
            stream.Position = start;
            byte[] header = new byte[7];
            ulong totalSamples = 0;
            ulong totalBytes = 0;
            uint? sampleRate = null;
            uint? channels = null;
            uint maximumBitrate = 0;
            int frames = 0;

            while (stream.Position < end)
            {
                long frameStart = stream.Position;
                if (end - frameStart < header.Length)
                    throw new InvalidDataException(
                        "Truncated AAC ADTS header.");
                stream.ReadExactly(header);
                if (header[0] != 0xFF ||
                    (header[1] & 0xF6) != 0xF0)
                    throw new InvalidDataException(
                        $"Invalid AAC ADTS sync at byte {frameStart}.");

                bool crcAbsent = (header[1] & 1) != 0;
                int samplingIndex = (header[2] >> 2) & 0x0F;
                if (samplingIndex >= SampleRates.Length)
                    throw new InvalidDataException(
                        "Invalid AAC ADTS sample-rate index.");
                uint frameSampleRate = SampleRates[samplingIndex];
                int channelConfiguration =
                    ((header[2] & 1) << 2) |
                    ((header[3] >> 6) & 3);
                uint frameChannels = ChannelCounts[channelConfiguration];
                int frameLength =
                    ((header[3] & 3) << 11) |
                    (header[4] << 3) |
                    (header[5] >> 5);
                int headerLength = crcAbsent ? 7 : 9;
                if (frameLength < headerLength ||
                    frameLength > end - frameStart)
                    throw new InvalidDataException(
                        "Invalid AAC ADTS frame length.");
                uint rawBlocks = (uint)(header[6] & 3) + 1;
                uint frameSamples = 1024 * rawBlocks;

                if (sampleRate.HasValue &&
                    sampleRate.Value != frameSampleRate)
                    throw new InvalidDataException(
                        "AAC sample rate changes between ADTS frames.");
                if (channels.HasValue &&
                    channels.Value != frameChannels)
                    throw new InvalidDataException(
                        "AAC channel configuration changes between ADTS frames.");
                sampleRate = frameSampleRate;
                channels = frameChannels;
                totalSamples = SaturatingAdd(totalSamples, frameSamples);
                totalBytes = SaturatingAdd(
                    totalBytes, (uint)frameLength);
                uint frameBitrate = ScaleRatio(
                    (uint)frameLength, 8UL * frameSampleRate,
                    frameSamples);
                maximumBitrate = Math.Max(maximumBitrate, frameBitrate);
                frames++;
                stream.Position = frameStart + frameLength;
            }

            if (frames == 0 || !sampleRate.HasValue)
                throw new InvalidDataException(
                    "The file contains no AAC ADTS frames.");
            Samplerate = sampleRate.Value;
            Channels = channels ?? 0;
            AverageBitrate = totalSamples == 0
                ? 0
                : ScaleRatio(
                    totalBytes, 8UL * Samplerate, totalSamples);
            MaxBitrate = maximumBitrate;
            DurationInFrames = ScaleSamplesToCdFrames(
                totalSamples, Samplerate);
        }

        private static long ReadId3Length(
            FileStream stream,
            long fileLength)
        {
            if (fileLength < 10)
                return 0;
            stream.Position = 0;
            Span<byte> header = stackalloc byte[10];
            stream.ReadExactly(header);
            if (!header[..3].SequenceEqual("ID3"u8))
                return 0;
            if (header[6] > 0x7F ||
                header[7] > 0x7F ||
                header[8] > 0x7F ||
                header[9] > 0x7F)
                throw new InvalidDataException(
                    "Invalid ID3v2 sync-safe size.");
            int bodySize =
                (header[6] << 21) |
                (header[7] << 14) |
                (header[8] << 7) |
                header[9];
            long total = 10L + bodySize +
                (header[3] == 4 && (header[5] & 0x10) != 0 ? 10L : 0L);
            if (total > fileLength)
                throw new InvalidDataException(
                    "ID3v2 tag extends beyond the AAC file.");
            return total;
        }

        private static uint ScaleSamplesToCdFrames(
            ulong samples,
            uint sampleRate) =>
            ScaleRatio(samples, 75, sampleRate);

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

        private static ulong SaturatingAdd(ulong left, ulong right) =>
            left > ulong.MaxValue - right
                ? ulong.MaxValue
                : left + right;

        private sealed class AacId3Tag : ID3v2Tag, IMetadataWriter
        {
            private readonly AACFile _owner;

            public AacId3Tag(AACFile owner) => _owner = owner;

            public void Load(
                Stream stream,
                bool readArtwork,
                long knownLength) =>
                ReadTag(stream, readArtwork, knownLength);

            public byte[] Serialize() => BuildTagBytes();

            public new void Save(string outputPath = null) =>
                _owner.SaveTags(outputPath);

            void IMetadataWriter.Save(string outputPath) =>
                _owner.SaveTags(outputPath);
        }

        private sealed class AacApeTag : APETag, IMetadataWriter
        {
            private readonly AACFile _owner;

            public AacApeTag(AACFile owner) => _owner = owner;

            public void Save(string outputPath = null) =>
                _owner.SaveTags(outputPath);
        }
    }
}
