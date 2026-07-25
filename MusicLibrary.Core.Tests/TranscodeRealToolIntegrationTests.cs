using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class TranscodeRealToolIntegrationTests
{
    [Fact]
    public async Task ConfiguredFfmpegAdvertisedPairsProduceReadableAudio()
    {
        string? ffmpeg = Environment.GetEnvironmentVariable(
            "MUSICLIBRARY_FFMPEG");
        if (string.IsNullOrWhiteSpace(ffmpeg) ||
            !File.Exists(ffmpeg))
            return;
        using var temp = new TempDirectory();
        AppSettings settings = CreateSettings(
            temp,
            ffmpeg);
        var processes = new ManagedProcessRunner();
        using var capabilities =
            new AudioTranscodeCapabilityService(
                settings,
                processes);
        AudioTranscodeCapabilitySnapshot snapshot =
            await capabilities.GetAsync(
                forceRefresh: true,
                TestContext.Current.CancellationToken);
        var adapter = new AudioTranscodeAdapter(
            settings,
            processes);
        string source = MediaFixtures.Path_("sample.flac");
        int pair = 0;

        foreach (AudioTranscodeFormatDescriptor format in
                 snapshot.Formats)
        {
            foreach (string encoderId in
                     format.EncoderIds)
            {
                AudioEncoderDescriptor encoder =
                    snapshot.FindEncoder(encoderId)!;
                if (encoder.Tool !=
                    AudioTranscodeToolKind.Ffmpeg)
                    continue;
                AudioTranscodeSettings reviewed =
                    SettingsFor(format, encoder);
                string destination = Path.Combine(
                    temp.Path,
                    $"{pair++:D2}" +
                    format.Extension);

                await adapter.EncodeAsync(
                    source,
                    destination,
                    reviewed,
                    encoder,
                    threadCount: 1,
                    ct: TestContext.Current
                        .CancellationToken);

                Assert.True(
                    new FileInfo(destination).Length > 0,
                    $"{format.Id}/{encoder.Id}");
                Assert.NotEmpty(
                    MediaFile.GetFile(
                            destination,
                            readOnly: true)
                        .Codecs);
            }
        }
        Assert.True(pair >= 10);
    }

    [Fact]
    public async Task ConfiguredOptimFrogToolsEncodeAndVerifyAllThreeFormats()
    {
        string? directory = Environment.GetEnvironmentVariable(
            "MUSICLIBRARY_OPTIMFROG_DIR");
        string? ffmpeg = Environment.GetEnvironmentVariable(
            "MUSICLIBRARY_FFMPEG");
        if (string.IsNullOrWhiteSpace(directory) ||
            string.IsNullOrWhiteSpace(ffmpeg) ||
            !File.Exists(ffmpeg) ||
            !File.Exists(Path.Combine(
                directory,
                OperatingSystem.IsWindows()
                    ? "ofr.exe"
                    : "ofr")))
            return;
        using var temp = new TempDirectory();
        AppSettings settings = CreateSettings(
            temp,
            ffmpeg);
        settings.SetPreference(
            OptimFrogFingerprintInputService
                .ToolsDirectoryPreferenceKey,
            directory);
        var adapter = new AudioTranscodeAdapter(
            settings,
            new ManagedProcessRunner());
        string source = MediaFixtures.Path_("sample.flac");
        (string FormatId,
            string EncoderId,
            string Executable,
            string Extension,
            bool Lossless)[] pairs =
        [
            (
                AudioTranscodeFormatIds.OptimFrog,
                AudioTranscodeEncoderIds.OptimFrogOfr,
                "ofr",
                ".ofr",
                true
            ),
            (
                AudioTranscodeFormatIds.OptimFrogDualStream,
                AudioTranscodeEncoderIds.OptimFrogOfs,
                "ofs",
                ".ofs",
                false
            ),
            (
                AudioTranscodeFormatIds.OptimFrogFloat,
                AudioTranscodeEncoderIds.OptimFrogOff,
                "off",
                ".ofr",
                true
            ),
        ];
        foreach (var pair in pairs)
        {
            var encoder = new AudioEncoderDescriptor(
                pair.EncoderId,
                AudioTranscodeToolKind.OptimFrog,
                pair.Executable,
                AudioEncoderThreadingMode.SingleThreaded,
                pair.Lossless
                    ? [new(AudioTranscodeRateMode.Lossless)]
                    :
                    [
                        new(
                            AudioTranscodeRateMode
                                .VariableQuality,
                            MinimumQuality: 0,
                            MaximumQuality: 6),
                    ],
                [],
                []);
            var reviewed = new AudioTranscodeSettings(
                pair.FormatId,
                pair.EncoderId,
                pair.Lossless
                    ? AudioTranscodeRateMode.Lossless
                    : AudioTranscodeRateMode.VariableQuality,
                Quality:
                    pair.Lossless ? null : 3);
            string destination = Path.Combine(
                temp.Path,
                pair.Executable +
                pair.Extension);

            await adapter.EncodeAsync(
                source,
                destination,
                reviewed,
                encoder,
                threadCount: 0,
                ct: TestContext.Current
                    .CancellationToken);

            Assert.True(
                new FileInfo(destination).Length > 0,
                pair.FormatId);

            if (pair.EncoderId ==
                AudioTranscodeEncoderIds.OptimFrogOff)
            {
                var flacEncoder =
                    new AudioEncoderDescriptor(
                        AudioTranscodeEncoderIds
                            .Ffmpeg("flac"),
                        AudioTranscodeToolKind.Ffmpeg,
                        "flac",
                        AudioEncoderThreadingMode
                            .ThreadCountControllable,
                        [
                            new(
                                AudioTranscodeRateMode
                                    .Lossless),
                        ],
                        [],
                        [16, 24, 32]);
                string decodedDestination =
                    Path.Combine(
                        temp.Path,
                        "decoded-float.flac");

                await adapter.EncodeAsync(
                    destination,
                    decodedDestination,
                    new(
                        AudioTranscodeFormatIds.Flac,
                        flacEncoder.Id,
                        AudioTranscodeRateMode
                            .Lossless),
                    flacEncoder,
                    threadCount: 1,
                    ct: TestContext.Current
                        .CancellationToken);

                Assert.True(
                    new FileInfo(decodedDestination)
                        .Length > 0);
                Assert.NotEmpty(
                    MediaFile.GetFile(
                            decodedDestination,
                            readOnly: true)
                        .Codecs);
            }
        }
    }

    [Fact]
    public async Task ConfiguredWavPackEncodesLosslessAndHybridCorrectionOutputs()
    {
        string? wavpack =
            Environment.GetEnvironmentVariable(
                "MUSICLIBRARY_WAVPACK");
        string? ffmpeg =
            Environment.GetEnvironmentVariable(
                "MUSICLIBRARY_FFMPEG");
        if (string.IsNullOrWhiteSpace(wavpack) ||
            string.IsNullOrWhiteSpace(ffmpeg) ||
            !File.Exists(wavpack) ||
            !File.Exists(ffmpeg))
            return;
        string wvunpack =
            TranscodeCorrectionVerificationService
                .ResolveSiblingTool(
                    wavpack,
                    "wvunpack");
        if (!File.Exists(wvunpack))
            return;
        using var temp = new TempDirectory();
        AppSettings settings = CreateSettings(
            temp,
            ffmpeg,
            wavpack);
        var processes = new ManagedProcessRunner();
        var adapter = new AudioTranscodeAdapter(
            settings,
            processes);
        var correction =
            new TranscodeCorrectionVerificationService(
                settings,
                processes);
        var encoder = new AudioEncoderDescriptor(
            AudioTranscodeEncoderIds.WavPackCli,
            AudioTranscodeToolKind.WavPack,
            "wavpack",
            AudioEncoderThreadingMode.SingleThreaded,
            [
                new(
                    AudioTranscodeRateMode.Lossless),
                new(
                    AudioTranscodeRateMode
                        .HybridBitrate,
                    200,
                    960),
            ],
            [],
            [8, 16, 24, 32],
            SupportsCorrectionFile: true);
        string source =
            MediaFixtures.Path_("sample.flac");
        string lossless =
            Path.Combine(temp.Path, "lossless.wv");
        await adapter.EncodeAsync(
            source,
            lossless,
            new(
                AudioTranscodeFormatIds.WavPack,
                encoder.Id,
                AudioTranscodeRateMode.Lossless),
            encoder,
            threadCount: 0,
            ct: TestContext.Current
                .CancellationToken);
        Assert.True(
            new FileInfo(lossless).Length > 0);

        string hybrid =
            Path.Combine(temp.Path, "hybrid.wv");
        await adapter.EncodeAsync(
            source,
            hybrid,
            new(
                AudioTranscodeFormatIds.WavPack,
                encoder.Id,
                AudioTranscodeRateMode
                    .HybridBitrate,
                BitrateKbps: 320,
                CreateCorrectionFile: true),
            encoder,
            threadCount: 0,
            ct: TestContext.Current
                .CancellationToken);
        string correctionPath =
            Path.ChangeExtension(hybrid, ".wvc");
        Assert.True(
            new FileInfo(hybrid).Length > 0);
        Assert.True(
            new FileInfo(correctionPath).Length > 0);

        string reconstructed =
            await correction.ReconstructAsync(
                hybrid,
                AudioTranscodeToolKind.WavPack,
                TestContext.Current
                    .CancellationToken);
        try
        {
            Assert.True(
                new FileInfo(reconstructed)
                    .Length > 0);
        }
        finally
        {
            File.Delete(reconstructed);
        }
    }

    private static AudioTranscodeSettings SettingsFor(
        AudioTranscodeFormatDescriptor format,
        AudioEncoderDescriptor encoder)
    {
        AudioRateControlDescriptor rate =
            encoder.RateControls[0];
        bool bitrateMode = rate.Mode is
            AudioTranscodeRateMode.ConstantBitrate or
            AudioTranscodeRateMode.AverageBitrate or
            AudioTranscodeRateMode
                .ConstrainedVariableBitrate;
        bool qualityMode = rate.Mode is
            AudioTranscodeRateMode.VariableQuality or
            AudioTranscodeRateMode.HybridQuality;
        return new(
            format.Id,
            encoder.Id,
            rate.Mode,
            BitrateKbps:
                bitrateMode
                    ? Math.Clamp(
                        192,
                        rate.MinimumBitrateKbps ?? 1,
                        rate.MaximumBitrateKbps ??
                        int.MaxValue)
                    : null,
            Quality:
                qualityMode
                    ? Math.Clamp(
                        4,
                        rate.MinimumQuality ??
                        double.MinValue,
                        rate.MaximumQuality ??
                        double.MaxValue)
                    : null);
    }

    private static AppSettings CreateSettings(
        TempDirectory temp,
        string ffmpeg,
        string wavpack =
            "__missing_wavpack_for_transcode_test__")
    {
        string configPath = Path.Combine(
            temp.Path,
            "library.xml");
        new EditableLibraryConfig
        {
            FfmpegPath = ffmpeg,
            WavpackPath = wavpack,
        }.Save(configPath);
        var settings = new AppSettings(
            Path.Combine(
                temp.Path,
                "settings.json"));
        settings.LoadConfig(configPath);
        return settings;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "transcode-real-tools-" +
                Guid.NewGuid().ToString("N"));

        public TempDirectory() =>
            Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(
                    Path,
                    recursive: true);
            }
            catch
            {
            }
        }
    }
}
