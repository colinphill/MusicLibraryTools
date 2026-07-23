using System.Buffers.Binary;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class AudioFingerprintServiceTests
{
    [Fact]
    public void FpcalcOutput_ParsesCompressedFingerprintAndWholeFileDuration()
    {
        string path = Path.Combine(Path.GetTempPath(), "track.flac");

        AudioFingerprint result = FpcalcRunner.ParseOutput(
            path,
            """{"duration": 123.51, "fingerprint": "AQAD-test"}""");

        Assert.Equal(Path.GetFullPath(path), result.Path);
        Assert.Equal("AQAD-test", result.Fingerprint);
        Assert.Equal(TimeSpan.FromSeconds(123.51), result.Duration);
        Assert.Equal(124, result.LookupDurationSeconds);
        Assert.Equal("Chromaprint", result.Algorithm);
    }

    [Theory]
    [InlineData("""{"duration": 0, "fingerprint": "AQAD-test"}""")]
    [InlineData("""{"duration": 12.3, "fingerprint": ""}""")]
    [InlineData("""{"duration": 12.3, "fingerprint": [1, 2]}""")]
    [InlineData("not-json")]
    public void FpcalcOutput_RejectsUnusableResults(string output)
    {
        Assert.Throws<InvalidDataException>(() =>
            FpcalcRunner.ParseOutput("track.flac", output));
    }

    [Fact]
    public async Task Service_UsesPersonalExecutablePreferenceAndFullPath()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        string statePath = Path.Combine(
            Path.GetTempPath(), "mlm-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new AppSettings(statePath);
            settings.SetPreference(
                AudioFingerprintService.ExecutablePreferenceKey,
                "custom-fpcalc");
            var runner = new RecordingFpcalcRunner();
            var service = new AudioFingerprintService(runner, settings);
            var progress = new RecordingProgress();

            AudioFingerprint result =
                await service.GenerateAsync(media.Path, progress);

            Assert.Equal("custom-fpcalc", runner.Executable);
            Assert.Equal(Path.GetFullPath(media.Path), runner.Path);
            Assert.Equal("fingerprint-1", result.Fingerprint);
            Assert.Equal([0, 1], progress.Items.Select(item => item.Completed));
        }
        finally
        {
            try { File.Delete(statePath); } catch { }
        }
    }

    [Theory]
    [InlineData("sample.flac")]
    [InlineData("sample.mp3")]
    [InlineData("sample.ogg")]
    [InlineData("sample_alac.m4a")]
    [InlineData("sample.wv")]
    [InlineData("sample.dsf")]
    [InlineData("sample.aac")]
    [InlineData("sample.ape")]
    [InlineData("sample.mpc")]
    [InlineData("sample.tta")]
    [InlineData("sample.tak")]
    [InlineData("sample.ofr")]
    [InlineData("sample.ofs")]
    [InlineData("sample.off")]
    public async Task PayloadIdentity_IgnoresNativeMetadataChanges(
        string fixture)
    {
        using var media = MediaFixtures.Copy(fixture);
        var identities = new AudioPayloadIdentityService(
            MediaFormatRegistry.Default);

        string before = await identities.ComputeAsync(media.Path);
        IMediaFile file = MediaFile.GetFile(
            media.Path, readOnly: false, readArtwork: true);
        IMetadataWriter? writer = file as IMetadataWriter ??
            file.Tags.OfType<IMetadataWriter>().FirstOrDefault();
        if (writer is not null)
            writer.SetField(TagFields.Title, "Payload identity title");
        else
            Assert.IsAssignableFrom<VorbisComments>(file.Tags.First())
                .SetField(TagFields.Title, "Payload identity title");
        file.SaveTags();
        string after = await identities.ComputeAsync(media.Path);

        Assert.Equal(before, after);
    }

    [Fact]
    public async Task PayloadIdentity_ChangesWhenFlacAudioPayloadChanges()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        var identities = new AudioPayloadIdentityService(
            MediaFormatRegistry.Default);
        string before = await identities.ComputeAsync(media.Path);

        await using (var stream = new FileStream(
                         media.Path, FileMode.Append, FileAccess.Write))
            await stream.WriteAsync(new byte[] { 0x5a });

        string after = await identities.ComputeAsync(media.Path);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task PayloadIdentity_IgnoresSpeexCommentPacketChanges()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"payload_{Guid.NewGuid():N}.spx");
        WriteSpeexFixture(path);
        try
        {
            var identities = new AudioPayloadIdentityService(
                MediaFormatRegistry.Default);
            string before = await identities.ComputeAsync(path);

            var file = Assert.IsType<OggVorbisFile>(
                MediaFile.GetFile(path, readOnly: false));
            file.SetField(TagFields.Title, "A longer Speex title");
            file.SaveTags();

            string after = await identities.ComputeAsync(path);
            Assert.Equal(before, after);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Theory]
    [InlineData(".wav")]
    [InlineData(".aiff")]
    public async Task PayloadIdentity_IgnoresChunkedId3ButTracksAudio(
        string extension)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"payload_{Guid.NewGuid():N}{extension}");
        WriteChunkedPcmFixture(path, extension == ".wav");
        try
        {
            var identities = new AudioPayloadIdentityService(
                MediaFormatRegistry.Default);
            string before = await identities.ComputeAsync(path);

            IMediaFile file = MediaFile.GetFile(path, readOnly: false);
            Assert.IsAssignableFrom<IMetadataWriter>(file)
                .SetField(TagFields.Title, "Chunked title");
            file.SaveTags();
            string afterTag = await identities.ComputeAsync(path);

            Assert.Equal(before, afterTag);
            MutateChunkedAudio(path, extension == ".wav");
            string afterAudio = await identities.ComputeAsync(path);
            Assert.NotEqual(afterTag, afterAudio);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task PayloadIdentity_IgnoresRawAacApeLayerChanges()
    {
        using var media = MediaFixtures.Copy("sample.aac");
        var ape = new APETag();
        ape.SetField(TagFields.Title, "Initial APE title");
        await using (var stream = new FileStream(
                         media.Path, FileMode.Append, FileAccess.Write))
            await stream.WriteAsync(ape.ToByteArray());
        var identities = new AudioPayloadIdentityService(
            MediaFormatRegistry.Default);
        string before = await identities.ComputeAsync(media.Path);

        AACFile file = Assert.IsType<AACFile>(
            MediaFile.GetFile(media.Path, readOnly: false));
        file.SetField(TagFields.Title, "Updated APE title");
        file.SaveTags();

        string after = await identities.ComputeAsync(media.Path);
        Assert.Equal(before, after);
        Assert.Equal(
            "APE",
            MediaFile.GetFile(media.Path).Tags.First().TagType);
    }

    [Fact]
    public async Task PayloadIdentity_TracksMonkeyAudioFrameChanges()
    {
        using var media = MediaFixtures.Copy("sample.ape");
        var identities = new AudioPayloadIdentityService(
            MediaFormatRegistry.Default);
        string before = await identities.ComputeAsync(media.Path);
        byte[] bytes = await File.ReadAllBytesAsync(media.Path);

        bytes[80] ^= 0x5a;
        await File.WriteAllBytesAsync(media.Path, bytes);

        string after = await identities.ComputeAsync(media.Path);
        Assert.NotEqual(before, after);
    }

    [Theory]
    [InlineData("sample.mpc")]
    [InlineData("sample.tta")]
    [InlineData("sample.tak")]
    [InlineData("sample.ofr")]
    [InlineData("sample.ofs")]
    [InlineData("sample.off")]
    public async Task PayloadIdentity_TracksAdditionalApeV2CodecChanges(
        string fixture)
    {
        using var media = MediaFixtures.Copy(fixture);
        var identities = new AudioPayloadIdentityService(
            MediaFormatRegistry.Default);
        string before = await identities.ComputeAsync(media.Path);
        byte[] bytes = await File.ReadAllBytesAsync(media.Path);
        using (var stream = File.OpenRead(media.Path))
        {
            var tag = new APETag();
            Assert.True(tag.ReadTag(
                stream,
                onlyAtEnd: true,
                readArtwork: false,
                knownLength: stream.Length));
            bytes[checked((int)tag.AudioEndOffset - 1)] ^= 0x5a;
        }
        await File.WriteAllBytesAsync(media.Path, bytes);

        string after = await identities.ComputeAsync(media.Path);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task Service_ReusesFingerprintAfterMetadataOnlyEdit()
    {
        using var media = MediaFixtures.Copy("sample.flac");
        string root = Path.Combine(
            Path.GetTempPath(),
            "mlm-fingerprint-cache-" + Guid.NewGuid().ToString("N"));
        string statePath = Path.Combine(root, "settings.json");
        string databasePath = Path.Combine(root, "cache.db");
        try
        {
            Directory.CreateDirectory(root);
            var runner = new RecordingFpcalcRunner();
            var service = new AudioFingerprintService(
                runner,
                new AppSettings(statePath),
                new AudioPayloadIdentityService(MediaFormatRegistry.Default),
                new AudioFingerprintCache(databasePath));

            AudioFingerprint first =
                await service.GenerateAsync(media.Path);
            IMediaFile file = MediaFile.GetFile(
                media.Path, readOnly: false, readArtwork: true);
            IMetadataWriter writer = file as IMetadataWriter ??
                file.Tags.OfType<IMetadataWriter>().First();
            writer.SetField(TagFields.Title, "Cache-safe title edit");
            file.SaveTags();
            var restarted = new AudioFingerprintService(
                runner,
                new AppSettings(statePath),
                new AudioPayloadIdentityService(MediaFormatRegistry.Default),
                new AudioFingerprintCache(databasePath));
            AudioFingerprint second =
                await restarted.GenerateAsync(media.Path);

            Assert.Equal(1, runner.CallCount);
            Assert.Equal(first.Fingerprint, second.Fingerprint);

            await using (var stream = new FileStream(
                             media.Path, FileMode.Append, FileAccess.Write))
                await stream.WriteAsync(new byte[] { 0x33 });
            await restarted.GenerateAsync(media.Path);

            Assert.Equal(2, runner.CallCount);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private sealed class RecordingFpcalcRunner : IFpcalcRunner
    {
        public string? Executable { get; private set; }
        public string? Path { get; private set; }
        public int CallCount { get; private set; }

        public Task<AudioFingerprint> GenerateAsync(
            string executable,
            string path,
            CancellationToken ct = default)
        {
            Executable = executable;
            Path = path;
            CallCount++;
            return Task.FromResult(new AudioFingerprint(
                path,
                $"fingerprint-{CallCount}",
                TimeSpan.FromSeconds(1),
                1));
        }
    }

    private sealed class RecordingProgress : IProgress<OperationProgress>
    {
        public List<OperationProgress> Items { get; } = [];
        public void Report(OperationProgress value) => Items.Add(value);
    }

    private static void WriteSpeexFixture(string path)
    {
        byte[] header = new byte[80];
        "Speex   "u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(32), header.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(36), 16000);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(44), 4);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(48), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(52), 28000);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(56), 320);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(64), 1);
        var comments = new VorbisComments { Vendor = "test" };
        comments.SetField(TagFields.Title, "Old");

        using FileStream stream = File.Create(path);
        WriteOggPage(stream, header, 2, 0);
        WriteOggPage(stream, comments.ToByteArray(false), 0, 1);
        WriteOggPage(stream, [0x11, 0x22, 0x33, 0x44], 4, 2);
    }

    private static void WriteOggPage(
        Stream stream,
        byte[] packet,
        byte headerType,
        int sequence)
    {
        Assert.True(packet.Length < 255);
        byte[] page = new byte[28 + packet.Length];
        "OggS"u8.CopyTo(page);
        page[5] = headerType;
        BinaryPrimitives.WriteInt32LittleEndian(
            page.AsSpan(14), 0x24681357);
        BinaryPrimitives.WriteInt32LittleEndian(
            page.AsSpan(18), sequence);
        page[26] = 1;
        page[27] = (byte)packet.Length;
        packet.CopyTo(page, 28);
        stream.Write(page);
    }

    private static void WriteChunkedPcmFixture(
        string path,
        bool wave)
    {
        using var stream = new MemoryStream();
        stream.Write(wave ? "RIFF"u8 : "FORM"u8);
        stream.Write(new byte[4]);
        stream.Write(wave ? "WAVE"u8 : "AIFF"u8);
        if (wave)
        {
            byte[] format = new byte[16];
            BinaryPrimitives.WriteUInt16LittleEndian(format, 1);
            BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(2), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(
                format.AsSpan(4), 8000);
            BinaryPrimitives.WriteUInt32LittleEndian(
                format.AsSpan(8), 16000);
            BinaryPrimitives.WriteUInt16LittleEndian(
                format.AsSpan(12), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(
                format.AsSpan(14), 16);
            WritePcmChunk(stream, "fmt ", format, littleEndian: true);
            WritePcmChunk(
                stream, "data", [1, 2, 3, 4], littleEndian: true);
        }
        else
        {
            byte[] common = new byte[18];
            BinaryPrimitives.WriteUInt16BigEndian(common, 1);
            BinaryPrimitives.WriteUInt32BigEndian(common.AsSpan(2), 2);
            BinaryPrimitives.WriteUInt16BigEndian(common.AsSpan(6), 16);
            Convert.FromHexString("400BFA00000000000000")
                .CopyTo(common, 8); // 8000 Hz
            WritePcmChunk(stream, "COMM", common, littleEndian: false);
            WritePcmChunk(
                stream,
                "SSND",
                [0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4],
                littleEndian: false);
        }
        byte[] bytes = stream.ToArray();
        if (wave)
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(4), checked((uint)bytes.Length - 8));
        else
            BinaryPrimitives.WriteUInt32BigEndian(
                bytes.AsSpan(4), checked((uint)bytes.Length - 8));
        File.WriteAllBytes(path, bytes);
    }

    private static void WritePcmChunk(
        Stream stream,
        string id,
        byte[] data,
        bool littleEndian)
    {
        stream.Write(System.Text.Encoding.ASCII.GetBytes(id));
        Span<byte> size = stackalloc byte[4];
        if (littleEndian)
            BinaryPrimitives.WriteUInt32LittleEndian(
                size, checked((uint)data.Length));
        else
            BinaryPrimitives.WriteUInt32BigEndian(
                size, checked((uint)data.Length));
        stream.Write(size);
        stream.Write(data);
        if ((data.Length & 1) != 0)
            stream.WriteByte(0);
    }

    private static void MutateChunkedAudio(string path, bool wave)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Position = 12;
        Span<byte> header = stackalloc byte[8];
        while (stream.Position + 8 <= stream.Length)
        {
            stream.ReadExactly(header);
            string id = System.Text.Encoding.ASCII.GetString(header[..4]);
            uint size = wave
                ? BinaryPrimitives.ReadUInt32LittleEndian(header[4..])
                : BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            long dataOffset = stream.Position;
            if (id == (wave ? "data" : "SSND"))
            {
                stream.Position = dataOffset + (wave ? 0 : 8);
                int value = stream.ReadByte();
                stream.Position--;
                stream.WriteByte((byte)(value ^ 0xFF));
                return;
            }
            stream.Position = checked(
                dataOffset + size + (size & 1));
        }
        throw new InvalidDataException("Audio chunk was not found.");
    }
}
