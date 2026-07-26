using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class TranscodeFoundationTests
{
    [Fact]
    public void IngestTranscodeResolverUsesStableSharedSettings()
    {
        LibraryIngestRecipe recipe = IngestRecipe() with
        {
            TranscodeFormatId =
                AudioTranscodeFormatIds.MonkeysAudio,
            TranscodeEncoderId =
                AudioTranscodeEncoderIds.MonkeysAudioMac,
            TranscodeRateMode =
                AudioTranscodeRateMode.Lossless.ToString(),
            TranscodeCompressionEffort = 8,
            SampleRateHz = 48_000,
            BitsPerSample = 24,
        };

        AudioTranscodeSettings settings =
            Assert.IsType<AudioTranscodeSettings>(
                IngestTranscodeSettingsResolver.Resolve(recipe));

        Assert.Equal(
            AudioTranscodeFormatIds.MonkeysAudio,
            settings.FormatId);
        Assert.Equal(
            AudioTranscodeEncoderIds.MonkeysAudioMac,
            settings.EncoderId);
        Assert.Equal(
            AudioTranscodeRateMode.Lossless,
            settings.RateMode);
        Assert.Equal(8, settings.CompressionEffort);
        Assert.Equal(".ape",
            IngestTranscodeSettingsResolver.OutputExtension(recipe));
        Assert.Equal("ape",
            IngestTranscodeSettingsResolver.OutputCodec(recipe));
    }

    [Fact]
    public void LegacyIngestRecipeRemainsOnLegacyExecutionPath()
    {
        LibraryIngestRecipe recipe = IngestRecipe() with
        {
            OutputExtension = ".flac",
            Codec = "flac",
            Encoder = "flac",
        };

        Assert.False(
            IngestTranscodeSettingsResolver.UsesSharedEngine(recipe));
        Assert.Null(
            IngestTranscodeSettingsResolver.Resolve(recipe));
        Assert.Equal(".flac",
            IngestTranscodeSettingsResolver.OutputExtension(recipe));
    }

    [Fact]
    public void IngestTranscodeResolverRejectsUnsupportedRateMode()
    {
        AudioTranscodeCapabilitySnapshot snapshot = new(
            [],
            [
                new(
                    AudioTranscodeFormatIds.MonkeysAudio,
                    "ape",
                    "ape",
                    ".ape",
                    true,
                    [
                        AudioTranscodeEncoderIds
                            .MonkeysAudioMac,
                    ]),
            ],
            [
                new(
                    AudioTranscodeEncoderIds.MonkeysAudioMac,
                    AudioTranscodeToolKind.MonkeysAudio,
                    "MAC",
                    AudioEncoderThreadingMode.ThreadCountControllable,
                    [
                        new(AudioTranscodeRateMode.Lossless),
                    ],
                    [],
                    [16, 24]),
            ],
            DateTimeOffset.UtcNow,
            1);
        var settings = new AudioTranscodeSettings(
            AudioTranscodeFormatIds.MonkeysAudio,
            AudioTranscodeEncoderIds.MonkeysAudioMac,
            AudioTranscodeRateMode.ConstantBitrate,
            BitrateKbps: 320);

        bool resolved =
            IngestTranscodeSettingsResolver.TryResolveCapability(
                snapshot,
                settings,
                out _,
                out _,
                out string? error);

        Assert.False(resolved);
        Assert.Contains("Rate mode", error);
    }

    [Fact]
    public void IngestTranscodeResolverRequiresCorrectionSupportFromTheSelectedRateMode()
    {
        AudioTranscodeCapabilitySnapshot snapshot =
            CorrectionCapabilitySnapshot();
        var lossless = new AudioTranscodeSettings(
            AudioTranscodeFormatIds.WavPack,
            AudioTranscodeEncoderIds.WavPackCli,
            AudioTranscodeRateMode.Lossless,
            CreateCorrectionFile: true);

        bool losslessResolved =
            IngestTranscodeSettingsResolver.TryResolveCapability(
                snapshot,
                lossless,
                out _,
                out _,
                out string? losslessError);
        bool hybridResolved =
            IngestTranscodeSettingsResolver.TryResolveCapability(
                snapshot,
                lossless with
                {
                    RateMode =
                        AudioTranscodeRateMode.HybridBitrate,
                },
                out _,
                out _,
                out string? hybridError);

        Assert.False(losslessResolved);
        Assert.Contains(
            nameof(AudioTranscodeRateMode.Lossless),
            losslessError);
        Assert.True(hybridResolved, hybridError);
    }

    [Fact]
    public async Task CapabilityProbeAdvertisesConfiguredMonkeysAudioMac()
    {
        using var service = new AudioTranscodeCapabilityService(
            new MemorySettings(),
            new MacProbeRunner());

        AudioTranscodeCapabilitySnapshot snapshot =
            await service.GetAsync(
                ct: TestContext.Current.CancellationToken);

        AudioTranscodeFormatDescriptor format =
            Assert.Single(
                snapshot.Formats,
                candidate =>
                    candidate.Id ==
                    AudioTranscodeFormatIds.MonkeysAudio);
        AudioEncoderDescriptor encoder = Assert.Single(
            snapshot.Encoders,
            candidate =>
                candidate.Id ==
                AudioTranscodeEncoderIds.MonkeysAudioMac);
        Assert.Contains(encoder.Id, format.EncoderIds);
        Assert.Equal(
            AudioEncoderThreadingMode.ThreadCountControllable,
            encoder.ThreadingMode);
    }

    [Theory]
    [InlineData(0, "-c1000")]
    [InlineData(3, "-c2000")]
    [InlineData(5, "-c3000")]
    [InlineData(7, "-c4000")]
    [InlineData(10, "-c5000")]
    public void MonkeysAudioEffortMapsToNativeCompressionMode(
        int effort,
        string expected) =>
        Assert.Equal(
            expected,
            AudioTranscodeAdapter
                .MonkeysAudioCompressionMode(effort));

    [Fact]
    public void FfmpegTableParserUsesExactIdentifiers()
    {
        const string output = """
             A..... flac                 FLAC encoder
             A..... flac_fixed           Test-only similarly named encoder
             V..... h264                 Video encoder
             A..... libmp3lame           MP3 encoder
            """;

        var values = AudioTranscodeCapabilityService.ParseToolTable(
            output,
            requiredFlag: 'A');

        Assert.Contains("flac", values);
        Assert.Contains("flac_fixed", values);
        Assert.Contains("libmp3lame", values);
        Assert.DoesNotContain("h264", values);
        Assert.DoesNotContain("fla", values);
    }

    private static LibraryIngestRecipe IngestRecipe() => new(
        Id: "shared-transcode",
        Name: "Shared transcode",
        Enabled: true,
        InputExtensions: [".flac"],
        RequireLossless: null,
        MinimumSampleRateHz: null,
        MinimumBitsPerSample: null,
        InputChannels: null,
        MatchAnyQualityMinimum: false,
        Action: LibraryIngestAction.Transcode,
        DestinationRootId: Guid.NewGuid(),
        DestinationLegacyRole: LibraryIngestRole.None,
        OutputExtension: null,
        Codec: null,
        Encoder: null,
        BitrateKbps: null,
        SampleRateHz: null,
        BitsPerSample: null,
        OutputChannels: null,
        PreserveMetadata: true,
        PreserveArtwork: true,
        CollisionPolicy: LibraryPathCollisionPolicy.Stop);

    [Theory]
    [InlineData(
        " A..... libopus Opus\r\n A..... libvorbis Vorbis\r\n",
        'A',
        "libopus",
        "libvorbis")]
    [InlineData(
        " DE matroska,webm Matroska / WebM\n D  mov,mp4,m4a QuickTime\n",
        'D',
        "webm",
        "m4a")]
    [InlineData(
        " E  ipod iPod output\n E  wav WAV / WAVE\n",
        'E',
        "ipod",
        "wav")]
    public void FfmpegTableParserHandlesRepresentativePlatformOutput(
        string output,
        char flag,
        string first,
        string second)
    {
        ImmutableHashSet<string> values =
            AudioTranscodeCapabilityService.ParseToolTable(
                output,
                flag);

        Assert.Contains(first, values);
        Assert.Contains(second, values);
    }

    [Fact]
    public async Task SourceLayoutInspectorCountsAudioProgramsAndOtherStreams()
    {
        const string output = """
            {
              "programs": [
                {
                  "program_id": 1,
                  "streams": [
                    { "codec_type": "audio" },
                    { "codec_type": "video" }
                  ]
                },
                {
                  "program_id": 2,
                  "streams": [
                    { "codec_type": "audio" }
                  ]
                }
              ],
              "streams": [
                { "codec_type": "audio" },
                { "codec_type": "video" },
                { "codec_type": "audio" }
              ]
            }
            """;
        var service = new AudioSourceLayoutInspector(
            new MemorySettings(),
            new FixedProcessRunner(output));

        AudioSourceLayout layout =
            await service.InspectAsync(
                "source.mkv",
                TestContext.Current.CancellationToken);

        Assert.Equal(2, layout.AudioStreamCount);
        Assert.Equal(1, layout.NonAudioStreamCount);
        Assert.Equal(2, layout.AudioProgramCount);
    }

    [Theory]
    [InlineData(
        true,
        "transcode.replace-multiple-audio-programs",
        OperationIssueSeverity.Blocker)]
    [InlineData(
        false,
        "transcode.separate-primary-audio",
        OperationIssueSeverity.Warning)]
    public void AdditionalStreamsBlockReplacementButWarnForSeparateOutput(
        bool replace,
        string expectedCode,
        OperationIssueSeverity expectedSeverity)
    {
        var issues = new List<OperationIssue>();

        AudioTranscodeService.AddSourceLayoutIssues(
            new(1, 1, 1),
            replace,
            "source.mkv",
            issues);

        OperationIssue issue = Assert.Single(issues);
        Assert.Equal(expectedCode, issue.Code);
        Assert.Equal(expectedSeverity, issue.Severity);
    }

    [Fact]
    public async Task CapabilityProbeCachesUntilForcedAndAdvertisesOnlyExactEncoderMatches()
    {
        var settings = new MemorySettings();
        var runner = new ProbeRunner();
        using var service = new AudioTranscodeCapabilityService(
            settings,
            runner);

        AudioTranscodeCapabilitySnapshot first =
            await service.GetAsync(
                ct: TestContext.Current.CancellationToken);
        int calls = runner.Calls;
        AudioTranscodeCapabilitySnapshot cached =
            await service.GetAsync(
                ct: TestContext.Current.CancellationToken);
        AudioTranscodeCapabilitySnapshot forced =
            await service.GetAsync(
                forceRefresh: true,
                TestContext.Current.CancellationToken);

        Assert.Same(first, cached);
        Assert.True(runner.Calls > calls);
        AudioTranscodeFormatDescriptor flac = Assert.Single(
            first.Formats,
            item => item.Id == AudioTranscodeFormatIds.Flac);
        Assert.Contains(
            AudioTranscodeEncoderIds.Ffmpeg("flac"),
            flac.EncoderIds);
        Assert.DoesNotContain(
            AudioTranscodeEncoderIds.Ffmpeg("flac_fixed"),
            flac.EncoderIds);
        Assert.Equal(first.ConfigurationVersion, forced.ConfigurationVersion);
    }

    [Fact]
    public void CapabilityCatalogProjectsCorrectionSupportOnlyToCompatibleRateModes()
    {
        AudioToolProbeResult wavPack = new(
            AudioTranscodeToolKind.WavPack,
            AudioToolProbeState.Ready,
            "wavpack",
            "wavpack",
            "test",
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                "wavpack",
                "correction"),
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                "wavpack",
                "correction"),
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                "wv"),
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                "wav",
                "dsf"));
        AudioToolProbeResult optimFrog = new(
            AudioTranscodeToolKind.OptimFrog,
            AudioToolProbeState.Ready,
            "optimfrog",
            "optimfrog",
            "test",
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                "ofs"),
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                "ofs"),
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                "ofs"),
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                "wav",
                "raw"));

        AudioTranscodeCapabilityService.BuildCatalog(
            [wavPack, optimFrog],
            out _,
            out ImmutableArray<
                AudioEncoderDescriptor> encoders);

        AudioEncoderDescriptor wavPackEncoder =
            Assert.Single(
                encoders,
                encoder =>
                    encoder.Id ==
                    AudioTranscodeEncoderIds
                        .WavPackCli);
        Assert.False(
            Assert.Single(
                    wavPackEncoder.RateControls,
                    rate =>
                        rate.Mode ==
                        AudioTranscodeRateMode
                            .Lossless)
                .SupportsCorrectionFile);
        AudioRateControlDescriptor[]
            wavPackHybridRates =
            [
                .. wavPackEncoder.RateControls
                    .Where(
                        rate =>
                            rate.Mode is
                                AudioTranscodeRateMode
                                    .HybridBitrate or
                                AudioTranscodeRateMode
                                    .HybridQuality),
            ];
        Assert.Equal(
            2,
            wavPackHybridRates.Length);
        Assert.All(
            wavPackHybridRates,
            rate =>
                Assert.True(
                    rate.SupportsCorrectionFile));
        AudioEncoderDescriptor ofsEncoder =
            Assert.Single(
                encoders,
                encoder =>
                    encoder.Id ==
                    AudioTranscodeEncoderIds
                        .OptimFrogOfs);
        Assert.True(
            ofsEncoder.SupportsCorrectionFile);
        Assert.All(
            ofsEncoder.RateControls,
            rate =>
                Assert.True(
                    rate.SupportsCorrectionFile));

        AudioToolProbeResult noCorrection =
            wavPack with
            {
                Encoders =
                    ImmutableHashSet.Create(
                        StringComparer.Ordinal,
                        "wavpack"),
                Decoders =
                    ImmutableHashSet.Create(
                        StringComparer.Ordinal,
                        "wavpack"),
            };
        AudioTranscodeCapabilityService.BuildCatalog(
            [noCorrection],
            out _,
            out ImmutableArray<
                AudioEncoderDescriptor>
                encodersWithoutCorrection);
        AudioEncoderDescriptor unsupported =
            Assert.Single(
                encodersWithoutCorrection,
                encoder =>
                    encoder.Id ==
                    AudioTranscodeEncoderIds
                        .WavPackCli);
        Assert.False(
            unsupported.SupportsCorrectionFile);
        Assert.All(
            unsupported.RateControls,
            rate =>
                Assert.False(
                    rate.SupportsCorrectionFile));
    }

    [Fact]
    public void SourceValidationRequiresAnExactFfmpegDemuxer()
    {
        AudioTranscodeCapabilitySnapshot snapshot =
            SnapshotWithTools(
                FfmpegProbe(demuxers: ["flac_fixed"]));
        var issues = new List<OperationIssue>();

        AudioTranscodeService.ValidateSourceCapability(
            snapshot,
            FfmpegEncoder(),
            "source.flac",
            issues);

        OperationIssue issue = Assert.Single(issues);
        Assert.Equal(
            "transcode.source-container-unavailable",
            issue.Code);
    }

    [Fact]
    public void SourceValidationAcceptsAConfiguredFfmpegDemuxer()
    {
        AudioTranscodeCapabilitySnapshot snapshot =
            SnapshotWithTools(
                FfmpegProbe(demuxers: ["flac"]));
        var issues = new List<OperationIssue>();

        AudioTranscodeService.ValidateSourceCapability(
            snapshot,
            FfmpegEncoder(),
            "source.flac",
            issues);

        Assert.Empty(issues);
    }

    [Theory]
    [InlineData(".aac", "aac")]
    [InlineData(".aiff", "aiff")]
    [InlineData(".ape", "ape")]
    [InlineData(".wma", "asf")]
    [InlineData(".dsf", "dsf")]
    [InlineData(".flac", "flac")]
    [InlineData(".m4a", "mov")]
    [InlineData(".mkv", "matroska")]
    [InlineData(".webm", "webm")]
    [InlineData(".mp3", "mp3")]
    [InlineData(".mpc", "musepack")]
    [InlineData(".opus", "ogg")]
    [InlineData(".rf64", "wav")]
    [InlineData(".tak", "tak")]
    [InlineData(".tta", "tta")]
    [InlineData(".wv", "wv")]
    public void SourceValidationCoversAdvertisedInputFamilies(
        string extension,
        string demuxer)
    {
        AudioTranscodeCapabilitySnapshot snapshot =
            SnapshotWithTools(
                FfmpegProbe(demuxers: [demuxer]));
        var issues = new List<OperationIssue>();

        AudioTranscodeService.ValidateSourceCapability(
            snapshot,
            FfmpegEncoder(),
            "source" + extension,
            issues);

        Assert.Empty(issues);
    }

    [Fact]
    public void OptimFrogSourceRequiresItsMatchingDecoder()
    {
        AudioTranscodeCapabilitySnapshot snapshot =
            SnapshotWithTools(
                FfmpegProbe(demuxers: ["wav"]),
                new AudioToolProbeResult(
                    AudioTranscodeToolKind.OptimFrog,
                    AudioToolProbeState.Ready,
                    "tools",
                    "tools",
                    "test",
                    ImmutableHashSet.Create(
                        StringComparer.Ordinal,
                        "ofr"),
                    ImmutableHashSet.Create(
                        StringComparer.Ordinal,
                        "ofr"),
                    ImmutableHashSet.Create(
                        StringComparer.Ordinal,
                        "ofr"),
                    ImmutableHashSet.Create(
                        StringComparer.Ordinal,
                        "wav")));
        var issues = new List<OperationIssue>();

        AudioTranscodeService.ValidateSourceCapability(
            snapshot,
            FfmpegEncoder(),
            "source.ofs",
            issues);

        OperationIssue issue = Assert.Single(issues);
        Assert.Equal(
            "transcode.source-decoder-unavailable",
            issue.Code);
    }

    [Fact]
    public void OptimFrogFloatContainerAcceptsOffDecoderForOfrExtension()
    {
        AudioTranscodeCapabilitySnapshot snapshot =
            SnapshotWithTools(
                FfmpegProbe(demuxers: ["wav"]),
                new AudioToolProbeResult(
                    AudioTranscodeToolKind.OptimFrog,
                    AudioToolProbeState.Ready,
                    "tools",
                    "tools",
                    "test",
                    ImmutableHashSet.Create(
                        StringComparer.Ordinal,
                        "off"),
                    ImmutableHashSet.Create(
                        StringComparer.Ordinal,
                        "off"),
                    ImmutableHashSet.Create(
                        StringComparer.Ordinal,
                        "off"),
                    ImmutableHashSet.Create(
                        StringComparer.Ordinal,
                        "wav")));
        var issues = new List<OperationIssue>();

        AudioTranscodeService.ValidateSourceCapability(
            snapshot,
            FfmpegEncoder(),
            "float.ofr",
            issues);

        Assert.Empty(issues);
    }

    [Fact]
    public void NumericCollisionSuffixProtectsCorrectionSidecarAsAUnit()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "MusicLibraryTools.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string destination =
                Path.Combine(directory, "track.wv");
            File.WriteAllText(
                Path.ChangeExtension(destination, ".wvc"),
                "unrelated");
            var issues = new List<OperationIssue>();

            string resolved =
                AudioTranscodeService.ResolveCollision(
                    Path.Combine(directory, "source.flac"),
                    destination,
                    AudioTranscodeCollisionPolicy.Suffix,
                    new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase),
                    issues,
                    ".wvc");

            Assert.Equal(
                Path.Combine(directory, "track (2).wv"),
                resolved);
            Assert.Empty(issues);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ChosenFolderPreservesLayoutFromConfiguredLibraryRoot()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "MusicLibraryTools.Tests",
            Guid.NewGuid().ToString("N"));
        string libraryRoot = Path.Combine(
            directory,
            "library");
        string sourceDirectory = Path.Combine(
            libraryRoot,
            "Artist",
            "Album");
        string source = Path.Combine(
            sourceDirectory,
            "Track.flac");
        string outputRoot = Path.Combine(
            directory,
            "output");
        string configPath = Path.Combine(
            directory,
            "library.xml");
        Directory.CreateDirectory(sourceDirectory);
        try
        {
            File.Copy(
                MediaFixtures.Path_("sample.flac"),
                source);
            var editable = new EditableLibraryConfig
            {
                IndexTargets =
                [
                    new IndexTargetEntry
                    {
                        Target = libraryRoot,
                    },
                ],
            };
            editable.Save(configPath);
            var settings = new MemorySettings(
                new LibraryConfiguration(configPath));
            AudioTranscodeService service =
                CreatePreviewService(settings);

            AudioTranscodePlan plan =
                await service.PreviewAsync(
                    PreviewRequest(
                        [source],
                        outputRoot,
                        preserveLayout: true),
                    ct: TestContext.Current
                        .CancellationToken);

            AudioTranscodePlanItem item =
                Assert.Single(plan.Items);
            Assert.Equal(
                Path.Combine(
                    outputRoot,
                    "Artist",
                    "Album",
                    "Track.flac"),
                item.DestinationPath);
            Assert.DoesNotContain(
                item.Issues,
                issue => issue.Severity ==
                    OperationIssueSeverity.Blocker);
        }
        finally
        {
            Directory.Delete(
                directory,
                recursive: true);
        }
    }

    [Fact]
    public async Task ChosenFolderNeverWritesBackOverSource()
    {
        using var source =
            MediaFixtures.Copy("sample.flac");
        string sourceDirectory =
            Path.GetDirectoryName(source.Path)!;
        AudioTranscodeService service =
            CreatePreviewService(
                new MemorySettings());

        AudioTranscodePlan plan =
            await service.PreviewAsync(
                PreviewRequest(
                    [source.Path],
                    sourceDirectory,
                    preserveLayout: false),
                ct: TestContext.Current
                    .CancellationToken);

        AudioTranscodePlanItem item =
            Assert.Single(plan.Items);
        Assert.Equal(
            Path.Combine(
                sourceDirectory,
                Path.GetFileNameWithoutExtension(
                    source.Path) +
                " (transcoded).flac"),
            item.DestinationPath);
        Assert.NotEqual(
            source.Path,
            item.DestinationPath);
    }

    [Fact]
    public async Task FlattenedBatchCollisionsStopOrSuffixDeterministically()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "MusicLibraryTools.Tests",
            Guid.NewGuid().ToString("N"));
        string firstDirectory = Path.Combine(
            directory,
            "first");
        string secondDirectory = Path.Combine(
            directory,
            "second");
        string outputRoot = Path.Combine(
            directory,
            "output");
        string first = Path.Combine(
            firstDirectory,
            "Track.flac");
        string second = Path.Combine(
            secondDirectory,
            "Track.flac");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        try
        {
            File.Copy(
                MediaFixtures.Path_("sample.flac"),
                first);
            File.Copy(
                MediaFixtures.Path_("sample.flac"),
                second);
            AudioTranscodeService service =
                CreatePreviewService(
                    new MemorySettings());

            AudioTranscodePlan stopped =
                await service.PreviewAsync(
                    PreviewRequest(
                        [second, first],
                        outputRoot,
                        preserveLayout: false),
                    ct: TestContext.Current
                        .CancellationToken);
            Assert.Equal(
                Path.Combine(
                    outputRoot,
                    "Track.flac"),
                stopped.Items[0].DestinationPath);
            Assert.Contains(
                stopped.Items[1].Issues,
                issue => issue.Code ==
                    "transcode.destination-exists" &&
                    issue.Severity ==
                    OperationIssueSeverity.Blocker);

            AudioTranscodeRequest suffixRequest =
                PreviewRequest(
                    [second, first],
                    outputRoot,
                    preserveLayout: false) with
                {
                    Destination =
                        PreviewRequest(
                            [second, first],
                            outputRoot,
                            preserveLayout: false)
                        .Destination with
                        {
                            CollisionPolicy =
                                AudioTranscodeCollisionPolicy
                                    .Suffix,
                        },
                };
            AudioTranscodePlan suffixed =
                await service.PreviewAsync(
                    suffixRequest,
                    ct: TestContext.Current
                        .CancellationToken);

            Assert.Equal(
                [
                    Path.Combine(
                        outputRoot,
                        "Track.flac"),
                    Path.Combine(
                        outputRoot,
                        "Track (2).flac"),
                ],
                suffixed.Items.Select(
                    item => item.DestinationPath));
            Assert.All(
                suffixed.Items,
                item => Assert.DoesNotContain(
                    item.Issues,
                    issue => issue.Severity ==
                        OperationIssueSeverity.Blocker));
        }
        finally
        {
            Directory.Delete(
                directory,
                recursive: true);
        }
    }

    [Fact]
    public async Task DsdToPcmRequiresExplicitRateAndDepth()
    {
        using var source =
            MediaFixtures.Copy("sample.dsf");
        string outputRoot =
            Path.GetDirectoryName(source.Path)!;
        AudioTranscodeService service =
            CreatePreviewService(
                new MemorySettings());
        AudioTranscodeRequest missing =
            PreviewRequest(
                [source.Path],
                outputRoot,
                preserveLayout: false);

        AudioTranscodePlan blocked =
            await service.PreviewAsync(
                missing,
                ct: TestContext.Current
                    .CancellationToken);
        Assert.Contains(
            Assert.Single(blocked.Items).Issues,
            issue => issue.Code ==
                "transcode.dsd-pcm-settings-required" &&
                issue.Severity ==
                OperationIssueSeverity.Blocker);

        AudioTranscodePlan explicitPlan =
            await service.PreviewAsync(
                missing with
                {
                    Settings = missing.Settings with
                    {
                        SampleRateHz = 88_200,
                        BitsPerSample = 24,
                    },
                },
                ct: TestContext.Current
                    .CancellationToken);
        Assert.DoesNotContain(
            Assert.Single(explicitPlan.Items).Issues,
            issue => issue.Code ==
                "transcode.dsd-pcm-settings-required");
    }

    [Fact]
    public async Task PreviewRequiresCorrectionSupportFromTheSelectedRateMode()
    {
        using var source =
            MediaFixtures.Copy("sample.wav");
        string outputRoot =
            Path.GetDirectoryName(source.Path)!;
        AudioTranscodeService service =
            CreatePreviewService(
                new MemorySettings(),
                new StaticCapabilityService(
                    CorrectionCapabilitySnapshot()));
        AudioTranscodeRequest request =
            PreviewRequest(
                [source.Path],
                outputRoot,
                preserveLayout: false) with
            {
                Settings = new(
                    AudioTranscodeFormatIds.WavPack,
                    AudioTranscodeEncoderIds.WavPackCli,
                    AudioTranscodeRateMode.Lossless,
                    CreateCorrectionFile: true),
            };

        AudioTranscodePlan blocked =
            await service.PreviewAsync(
                request,
                ct: TestContext.Current
                    .CancellationToken);
        AudioTranscodePlan allowed =
            await service.PreviewAsync(
                request with
                {
                    Settings = request.Settings with
                    {
                        RateMode =
                            AudioTranscodeRateMode
                                .HybridBitrate,
                    },
                },
                ct: TestContext.Current
                    .CancellationToken);

        Assert.Contains(
            blocked.Issues,
            issue =>
                issue.Code ==
                "transcode.correction-unavailable" &&
                issue.Severity ==
                OperationIssueSeverity.Blocker);
        Assert.DoesNotContain(
            allowed.Issues,
            issue =>
                issue.Code ==
                "transcode.correction-unavailable");
    }

    [Fact]
    public async Task StageRevalidatesCorrectionSupportBeforeStartingEncoderWork()
    {
        using var source =
            MediaFixtures.Copy("sample.wav");
        string outputRoot =
            Path.GetDirectoryName(source.Path)!;
        AudioTranscodeCapabilitySnapshot supported =
            CorrectionCapabilitySnapshot();
        var capabilities =
            new MutableCapabilityService(supported);
        AudioTranscodeService service =
            CreatePreviewService(
                new MemorySettings(),
                capabilities);
        AudioTranscodeRequest request =
            PreviewRequest(
                [source.Path],
                outputRoot,
                preserveLayout: false) with
            {
                Settings = new(
                    AudioTranscodeFormatIds.WavPack,
                    AudioTranscodeEncoderIds.WavPackCli,
                    AudioTranscodeRateMode.HybridBitrate,
                    BitrateKbps: 320,
                    CreateCorrectionFile: true),
            };
        AudioTranscodePlan plan =
            await service.PreviewAsync(
                request,
                ct: TestContext.Current
                    .CancellationToken);
        Assert.True(plan.CanApply);
        AudioEncoderDescriptor changedEncoder =
            Assert.Single(supported.Encoders) with
            {
                RateControls =
                [
                    new(
                        AudioTranscodeRateMode
                            .Lossless),
                    new(
                        AudioTranscodeRateMode
                            .HybridBitrate,
                        200,
                        960),
                ],
            };
        capabilities.Snapshot = supported with
        {
            Encoders = [changedEncoder],
            ConfigurationVersion =
                supported.ConfigurationVersion + 1,
        };

        InvalidOperationException error =
            await Assert.ThrowsAsync<
                InvalidOperationException>(
                () => service.StageAsync(
                    plan,
                    ct: TestContext.Current
                        .CancellationToken));

        Assert.Contains(
            "correction file",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StageRevalidationBypassesTheProductionCapabilityCache()
    {
        using var source =
            MediaFixtures.Copy("sample.wav");
        string outputRoot =
            Path.GetDirectoryName(source.Path)!;
        var settings = new MemorySettings();
        var runner =
            new MutableWavPackProbeRunner();
        using var capabilities =
            new AudioTranscodeCapabilityService(
                settings,
                runner);
        var adapter = new RecordingAdapter();
        AudioTranscodeService service =
            CreatePreviewService(
                settings,
                capabilities,
                adapter);
        AudioTranscodeRequest request =
            PreviewRequest(
                [source.Path],
                outputRoot,
                preserveLayout: false) with
            {
                Settings = new(
                    AudioTranscodeFormatIds.WavPack,
                    AudioTranscodeEncoderIds.WavPackCli,
                    AudioTranscodeRateMode.HybridBitrate,
                    BitrateKbps: 320,
                    CreateCorrectionFile: true),
            };

        AudioTranscodePlan plan =
            await service.PreviewAsync(
                request,
                ct: TestContext.Current
                    .CancellationToken);
        Assert.True(plan.CanApply);
        Assert.Equal(1, runner.WavPackHelpCalls);
        runner.SupportsCorrectionFile = false;

        InvalidOperationException error =
            await Assert.ThrowsAsync<
                InvalidOperationException>(
                () => service.StageAsync(
                    plan,
                    ct: TestContext.Current
                        .CancellationToken));

        Assert.Contains(
            "reviewed transcode settings",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, runner.WavPackHelpCalls);
        Assert.Null(adapter.SourcePath);
    }

    [Theory]
    [InlineData(AudioTranscodeFormatIds.WavPack, ".wvc")]
    [InlineData(
        AudioTranscodeFormatIds.OptimFrogDualStream,
        ".ofc")]
    public void CorrectionFormatsUseStableSidecarExtensions(
        string formatId,
        string expected)
    {
        var format = new AudioTranscodeFormatDescriptor(
            formatId,
            "codec",
            "container",
            ".audio",
            true,
            []);
        var settings = new AudioTranscodeSettings(
            formatId,
            AudioTranscodeEncoderIds.Automatic,
            AudioTranscodeRateMode.HybridBitrate,
            CreateCorrectionFile: true);

        Assert.Equal(
            expected,
            AudioTranscodeService.CorrectionSidecarExtension(
                format,
                settings));
    }

    [Fact]
    public void PreviewCapacityEstimateBlocksBeforeEncoding()
    {
        string source = Path.GetFullPath("large.flac");
        string destination = Path.GetFullPath("output.flac");
        var items = new List<AudioTranscodePlanItem>
        {
            new(
                Guid.NewGuid(),
                source,
                destination,
                new(
                    true,
                    false,
                    100 * 1024 * 1024,
                    DateTime.UtcNow)
                {
                    Path = source,
                },
                OperationPathSnapshot.Missing(destination),
                "",
                new(
                    AudioTranscodeFormatIds.Flac,
                    AudioTranscodeEncoderIds.Automatic,
                    AudioTranscodeRateMode.Lossless),
                []),
        };
        var format = new AudioTranscodeFormatDescriptor(
            AudioTranscodeFormatIds.Flac,
            "flac",
            "flac",
            ".flac",
            true,
            []);

        AudioTranscodeService.AddPreviewCapacityIssues(
            items,
            format,
            new FixedRecoverySpaceProbe(1024));

        OperationIssue issue =
            Assert.Single(items[0].Issues);
        Assert.Equal(
            "transcode.recovery-space",
            issue.Code);
        Assert.Equal(
            OperationIssueSeverity.Blocker,
            issue.Severity);
    }

    [Fact]
    public void OutputOutsideConfiguredIndexRootsWarnsThatItIsSessionOnly()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "MusicLibraryTools.Tests",
            Guid.NewGuid().ToString("N"));
        string root = Path.Combine(directory, "library");
        string outside = Path.Combine(
            directory,
            "exports",
            "track.flac");
        string configPath = Path.Combine(
            directory,
            "library.xml");
        Directory.CreateDirectory(root);
        try
        {
            var editable = new EditableLibraryConfig
            {
                IndexTargets =
                [
                    new IndexTargetEntry
                    {
                        Target = root,
                    },
                ],
            };
            editable.Save(configPath);
            var issues = new List<OperationIssue>();

            AudioTranscodeService.AddInternalCatalogIssues(
                new LibraryConfiguration(configPath),
                outside,
                issues);

            OperationIssue issue = Assert.Single(issues);
            Assert.Equal(
                "transcode.output-session-only",
                issue.Code);
            Assert.Equal(
                OperationIssueSeverity.Warning,
                issue.Severity);
        }
        finally
        {
            Directory.Delete(
                directory,
                recursive: true);
        }
    }

    [Fact]
    public void DitherIsAppliedOnlyWhenIntegerPrecisionCanBeLost()
    {
        string source =
            MediaFixtures.Path_("sample.flac");

        Assert.False(
            AudioTranscodeAdapter.RequiresDither(
                source,
                24));
        Assert.True(
            AudioTranscodeAdapter.RequiresDither(
            source,
            8));
    }

    [Fact]
    public void LossySourceGetsDeterministicAutomaticIntegerProjection()
    {
        string source =
            MediaFixtures.Path_("sample.mp3");

        Assert.Equal(
            24,
            AudioTranscodeAdapter
                .EffectiveIntegerConversionBitDepth(
                    new(
                        AudioTranscodeFormatIds.Flac,
                        AudioTranscodeEncoderIds
                            .Ffmpeg("flac"),
                        AudioTranscodeRateMode
                            .Lossless),
                    source));
        Assert.Equal(
            16,
            AudioTranscodeAdapter
                .EffectiveIntegerConversionBitDepth(
                    new(
                        AudioTranscodeFormatIds.PcmWave,
                        AudioTranscodeEncoderIds.Automatic,
                        AudioTranscodeRateMode
                            .Lossless),
                    source));
        Assert.Null(
            AudioTranscodeAdapter
                .EffectiveIntegerConversionBitDepth(
                    new(
                        AudioTranscodeFormatIds.OptimFrogFloat,
                        AudioTranscodeEncoderIds.OptimFrogOff,
                        AudioTranscodeRateMode
                            .Lossless),
                    source));
    }

    [Fact]
    public void IntegerLosslessSourcePreservesItsPrecisionByDefault()
    {
        Assert.Null(
            AudioTranscodeAdapter
                .EffectiveIntegerConversionBitDepth(
                    new(
                        AudioTranscodeFormatIds.Flac,
                        AudioTranscodeEncoderIds
                            .Ffmpeg("flac"),
                        AudioTranscodeRateMode
                            .Lossless),
                    MediaFixtures.Path_(
                        "sample.flac")));
    }

    [Fact]
    public void OptimFrogFloatUsesFloatingPointPcmBridge()
    {
        var settings = new AudioTranscodeSettings(
            AudioTranscodeFormatIds.OptimFrogFloat,
            AudioTranscodeEncoderIds.OptimFrogOff,
            AudioTranscodeRateMode.Lossless);

        Assert.Equal(
            "pcm_f32le",
            AudioTranscodeAdapter.PcmBridgeCodec(
                settings,
                floatOutput: true));
        Assert.Equal(
            "high",
            AudioTranscodeAdapter.OptimFrogFloatMode(
                5));
        Assert.Equal(
            "extranew-light",
            AudioTranscodeAdapter.OptimFrogFloatMode(
                10));
    }

    [Fact]
    public async Task TransformedPcmReferenceUsesReviewedConversionSettings()
    {
        using var source = MediaFixtures.Copy("sample.flac");
        var runner = new ReferenceProcessRunner();
        var service = new TranscodePcmReferenceService(
            new MemorySettings(),
            runner);
        var settings = new AudioTranscodeSettings(
            AudioTranscodeFormatIds.Flac,
            AudioTranscodeEncoderIds.Ffmpeg("flac"),
            AudioTranscodeRateMode.Lossless,
            SampleRateHz: 48_000,
            BitsPerSample: 8);
        string stagedOutput = Path.Combine(
            Path.GetDirectoryName(source.Path)!,
            Guid.NewGuid().ToString("N") +
            ".flac");

        string reference = await service.CreateAsync(
            source.Path,
            settings,
            stagedOutput,
            TestContext.Current.CancellationToken);
        try
        {
            Assert.True(File.Exists(reference));
            int filterIndex = runner.Arguments.IndexOf("-af");
            Assert.True(filterIndex >= 0);
            string filter =
                runner.Arguments[filterIndex + 1];
            Assert.Contains(
                "48000",
                filter,
                StringComparison.Ordinal);
            Assert.Contains(
                "dither_method=triangular_hp",
                filter,
                StringComparison.Ordinal);
            Assert.Contains("-map_metadata", runner.Arguments);
            Assert.Contains("flac", runner.Arguments);
        }
        finally
        {
            File.Delete(reference);
        }
    }

    [Theory]
    [InlineData(
        AudioTranscodeToolKind.WavPack,
        "wvunpack",
        "-o")]
    [InlineData(
        AudioTranscodeToolKind.OptimFrog,
        "ofs",
        "--decode")]
    public async Task CorrectionVerificationReconstructsWithNativeDecoder(
        AudioTranscodeToolKind tool,
        string expectedExecutable,
        string expectedArgument)
    {
        using var source = MediaFixtures.Copy("sample.flac");
        var runner = new ReferenceProcessRunner();
        var service =
            new TranscodeCorrectionVerificationService(
                new MemorySettings(),
                runner);

        string reconstructed =
            await service.ReconstructAsync(
                source.Path,
                tool,
                TestContext.Current.CancellationToken);
        try
        {
            Assert.True(File.Exists(reconstructed));
            Assert.Contains(
                expectedExecutable,
                Path.GetFileNameWithoutExtension(
                    runner.Executable),
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                expectedArgument,
                runner.Arguments);
        }
        finally
        {
            File.Delete(reconstructed);
        }
    }

    [Fact]
    public async Task AutomaticSchedulerRunsIndependentSingleThreadedFilesConcurrently()
    {
        var scheduler = new TranscodeWorkScheduler(
            new MemorySettings(),
            processorCount: 4,
            perVolumeLimit: 4);
        int active = 0;
        int maximumActive = 0;
        TranscodeWorkItem<int>[] items =
        [
            .. Enumerable.Range(0, 8).Select(index =>
                new TranscodeWorkItem<int>(
                    index,
                    index,
                    $"volume-{index}",
                    AudioEncoderThreadingMode.SingleThreaded)),
        ];

        IReadOnlyList<TranscodeWorkResult<int>> results =
            await scheduler.RunAsync(
                items,
                async (_, _, ct) =>
                {
                    int now = Interlocked.Increment(ref active);
                    UpdateMaximum(ref maximumActive, now);
                    await Task.Delay(40, ct);
                    Interlocked.Decrement(ref active);
                },
                ct: TestContext.Current.CancellationToken);

        Assert.InRange(maximumActive, 2, 4);
        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal(
            Enumerable.Range(0, 8),
            results.Select(result => result.Index));
    }

    [Fact]
    public async Task SchedulerHonorsManualBudgetAndPerVolumeIoGate()
    {
        var scheduler = new TranscodeWorkScheduler(
            new MemorySettings(),
            processorCount: 8,
            perVolumeLimit: 2);
        scheduler.SaveSettings(new(
            Automatic: false,
            MaximumProcesses: 5));
        int active = 0;
        int maximumActive = 0;
        TranscodeWorkItem<int>[] items =
        [
            .. Enumerable.Range(0, 10).Select(index =>
                new TranscodeWorkItem<int>(
                    index,
                    index,
                    "same-volume",
                    AudioEncoderThreadingMode.SingleThreaded)),
        ];

        await scheduler.RunAsync(
            items,
            async (_, _, ct) =>
            {
                int now = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, now);
                await Task.Delay(30, ct);
                Interlocked.Decrement(ref active);
            },
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, maximumActive);
    }

    [Fact]
    public async Task AutomaticSchedulerCompletesMultiFileBatchFasterThanSingleWorker()
    {
        var scheduler = new TranscodeWorkScheduler(
            new MemorySettings(),
            processorCount: 4,
            perVolumeLimit: 4);
        TranscodeWorkItem<int>[] items =
        [
            .. Enumerable.Range(0, 8).Select(index =>
                new TranscodeWorkItem<int>(
                    index,
                    index,
                    $"volume-{index}",
                    AudioEncoderThreadingMode
                        .SingleThreaded)),
        ];

        scheduler.SaveSettings(new(
            Automatic: false,
            MaximumProcesses: 1));
        TimeSpan serial =
            await MeasureSchedulerAsync(
                scheduler,
                items);
        scheduler.SaveSettings(new(
            Automatic: true,
            MaximumProcesses: 4));
        TimeSpan parallel =
            await MeasureSchedulerAsync(
                scheduler,
                items);

        Assert.True(
            parallel < serial * 0.75,
            $"Automatic scheduling took {parallel}; " +
            $"single-worker mode took {serial}.");
    }

    [Fact]
    public async Task HighContentionVolumeGatesRemainBoundedWhileBothVolumesProgress()
    {
        var scheduler = new TranscodeWorkScheduler(
            new MemorySettings(),
            processorCount: 8,
            perVolumeLimit: 1);
        int[] activeByVolume = [0, 0];
        int[] maximumByVolume = [0, 0];
        int globalActive = 0;
        int maximumGlobal = 0;
        TranscodeWorkItem<int>[] items =
        [
            .. Enumerable.Range(0, 32).Select(index =>
                new TranscodeWorkItem<int>(
                    index,
                    index,
                    index % 2 == 0
                        ? "volume-a"
                        : "volume-b",
                    AudioEncoderThreadingMode
                        .SingleThreaded)),
        ];

        IReadOnlyList<TranscodeWorkResult<int>> results =
            await scheduler.RunAsync(
                items,
                async (value, _, ct) =>
                {
                    int volume = value % 2;
                    int currentVolume =
                        Interlocked.Increment(
                            ref activeByVolume[
                                volume]);
                    UpdateMaximum(
                        ref maximumByVolume[volume],
                        currentVolume);
                    int currentGlobal =
                        Interlocked.Increment(
                            ref globalActive);
                    UpdateMaximum(
                        ref maximumGlobal,
                        currentGlobal);
                    await Task.Delay(10, ct);
                    Interlocked.Decrement(
                        ref globalActive);
                    Interlocked.Decrement(
                        ref activeByVolume[volume]);
                },
                ct: TestContext.Current
                    .CancellationToken);

        Assert.Equal(1, maximumByVolume[0]);
        Assert.Equal(1, maximumByVolume[1]);
        Assert.Equal(2, maximumGlobal);
        Assert.All(
            results,
            result => Assert.True(
                result.Succeeded));
    }

    [Fact]
    public async Task ControllableEncoderThreadAllocationsStayWithinCpuBudget()
    {
        var scheduler = new TranscodeWorkScheduler(
            new MemorySettings(),
            processorCount: 6,
            perVolumeLimit: 6);
        int activeThreads = 0;
        int maximumThreads = 0;
        TranscodeWorkItem<int>[] items =
        [
            .. Enumerable.Range(0, 8).Select(index =>
                new TranscodeWorkItem<int>(
                    index,
                    index,
                    $"volume-{index}",
                    AudioEncoderThreadingMode.ThreadCountControllable)),
        ];

        await scheduler.RunAsync(
            items,
            async (_, threads, ct) =>
            {
                int now = Interlocked.Add(
                    ref activeThreads,
                    threads);
                UpdateMaximum(ref maximumThreads, now);
                await Task.Delay(30, ct);
                Interlocked.Add(
                    ref activeThreads,
                    -threads);
            },
            ct: TestContext.Current.CancellationToken);

        Assert.InRange(maximumThreads, 1, 6);
    }

    [Fact]
    public async Task CancellationStopsQueuedAndActiveSchedulerWork()
    {
        var scheduler = new TranscodeWorkScheduler(
            new MemorySettings(),
            processorCount: 4,
            perVolumeLimit: 4);
        using var cancellation = new CancellationTokenSource();
        int started = 0;
        TranscodeWorkItem<int>[] items =
        [
            .. Enumerable.Range(0, 20).Select(index =>
                new TranscodeWorkItem<int>(
                    index,
                    index,
                    $"volume-{index}",
                    AudioEncoderThreadingMode.SingleThreaded)),
        ];

        Task run = scheduler.RunAsync(
            items,
            async (_, _, ct) =>
            {
                if (Interlocked.Increment(ref started) == 2)
                    cancellation.Cancel();
                await Task.Delay(
                    TimeSpan.FromSeconds(10),
                    ct);
            },
            ct: cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run);
        Assert.True(started < items.Length);
    }

    [Fact]
    public async Task ManagedProcessRunnerCapsDiagnosticsAndReportsLines()
    {
        var runner = new ManagedProcessRunner(
            maximumCapturedCharacters: 4_096);
        var lines = new RecordingProgress<string>();

        ManagedProcessResult result = await runner.RunAsync(
            ManagedProcessFixtureExecutable,
            ["--managed-process", "large"],
            standardOutputLines: lines,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.InRange(result.StandardOutput.Length, 1, 4_096);
        Assert.InRange(result.StandardError.Length, 1, 4_096);
        Assert.Contains("stdout-0999", result.StandardOutput);
        Assert.Contains("stderr-0999", result.StandardError);
        Assert.Equal(1_000, lines.Values.Count);
    }

    [Fact]
    public async Task ManagedProcessRunnerPassesArgumentsWithoutShellParsing()
    {
        var runner = new ManagedProcessRunner();
        string[] arguments =
        [
            "--managed-process",
            "arguments",
            "value with spaces",
            "$(not-a-command)",
            "semi;colon",
        ];

        ManagedProcessResult result = await runner.RunAsync(
            ManagedProcessFixtureExecutable,
            arguments,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(
            arguments.Skip(2),
            JsonSerializer.Deserialize<string[]>(
                result.StandardOutput));
    }

    [Fact]
    public async Task ManagedProcessRunnerCancellationTerminatesPromptly()
    {
        var runner = new ManagedProcessRunner();
        using var cancellation =
            new CancellationTokenSource(
                TimeSpan.FromMilliseconds(250));
        var elapsed = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(
                ManagedProcessFixtureExecutable,
                ["--managed-process", "wait"],
                ct: cancellation.Token));

        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(5),
            $"Cancellation took {elapsed.Elapsed}.");
    }

    [Fact]
    public async Task StagingUsesReviewedMetadataSourceOverrideForEncodingAndProjection()
    {
        using var source = MediaFixtures.Copy("sample.flac");
        using var projected =
            MediaFixtures.Copy("sample.flac");
        string destination = Path.Combine(
            Path.GetDirectoryName(source.Path)!,
            Guid.NewGuid().ToString("N") +
            ".flac");
        var settings = new MemorySettings();
        var adapter = new RecordingAdapter();
        var projection =
            new RecordingProjection();
        var coordinator =
            new FileMutationCoordinator();
        var journals =
            new OperationJournalService(coordinator);
        var reviewed =
            new ReviewedChangeBatchService(
                new FileMutationPlanExecutor(
                    coordinator),
                journals);
        var history =
            new ReviewedChangeHistoryService(
                settings,
                journals);
        var decodedVerifier =
            new SuccessfulDecodedVerifier();
        var pcmReference =
            new RecordingPcmReference();
        var service = new AudioTranscodeService(
            settings,
            new FixedCapabilityService(),
            adapter,
            projection,
            new TranscodeWorkScheduler(
                settings,
                processorCount: 2),
            reviewed,
            history,
            decodedVerifier,
            pcmReference: pcmReference);
        int sourceBits = checked((int)MediaFile.GetFile(
                source.Path,
                readOnly: true)
            .Codecs.First().BitsPerSample);
        AudioTranscodeSettings transcodeSettings =
            new(
                AudioTranscodeFormatIds.Flac,
                AudioTranscodeEncoderIds.Ffmpeg(
                    "flac"),
                AudioTranscodeRateMode.Lossless,
                BitsPerSample: sourceBits);
        AudioTranscodeRequest request = new(
            [source.Path],
            transcodeSettings,
            new(
                AudioTranscodeDestinationMode.Alongside,
                null,
                true,
                "{Name}{Extension}",
                AudioTranscodeCollisionPolicy.Stop));
        var item = new AudioTranscodePlanItem(
            Guid.NewGuid(),
            source.Path,
            destination,
            FileSnapshot(source.Path),
            OperationPathSnapshot.Missing(destination),
            FileHash(source.Path),
            transcodeSettings,
            []);
        var plan = new AudioTranscodePlan(
            Guid.NewGuid(),
            request,
            [item],
            [],
            DateTimeOffset.UtcNow,
            1);
        var operationProgress =
            new RecordingProgress<OperationProgress>();

        AudioTranscodeStageResult stage =
            await service.StageWithSourceOverridesAsync(
                plan,
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [source.Path] = projected.Path,
                },
                operationProgress,
                ct: TestContext.Current
                    .CancellationToken);
        try
        {
            Assert.Equal(
                projected.Path,
                adapter.SourcePath);
            Assert.Equal(
                projected.Path,
                projection.SourcePath);
            Assert.Single(stage.ReadyItems);
            Assert.Contains(
                operationProgress.Values,
                value => value.Message?.Contains(
                    "% encoded",
                    StringComparison.Ordinal) == true);
            Assert.Contains(
                operationProgress.Values,
                value => value.MessageKey ==
                    "Transcode.Progress.Encoding" &&
                    value.MessageArguments.Length == 3);
            Assert.Equal(1, pcmReference.Calls);
            DecodedAudioPair pair = Assert.Single(
                Assert.Single(
                    decodedVerifier.Pairs));
            Assert.Equal(
                pcmReference.LastPath,
                pair.FirstPath);
            Assert.False(
                File.Exists(pcmReference.LastPath));
        }
        finally
        {
            await service.DiscardStageAsync(
                stage,
                TestContext.Current
                    .CancellationToken);
        }
    }

    private static void UpdateMaximum(
        ref int target,
        int value)
    {
        while (true)
        {
            int current = Volatile.Read(ref target);
            if (value <= current ||
                Interlocked.CompareExchange(
                    ref target,
                    value,
                    current) == current)
                return;
        }
    }

    private static async Task<TimeSpan>
        MeasureSchedulerAsync(
        ITranscodeWorkScheduler scheduler,
        IReadOnlyList<TranscodeWorkItem<int>> items)
    {
        var elapsed = Stopwatch.StartNew();
        await scheduler.RunAsync(
            items,
            (_, _, ct) =>
                Task.Delay(50, ct),
            ct: TestContext.Current
                .CancellationToken);
        return elapsed.Elapsed;
    }

    private static AudioTranscodeRequest PreviewRequest(
        IEnumerable<string> sourcePaths,
        string outputRoot,
        bool preserveLayout) =>
        new(
            [.. sourcePaths],
            new(
                AudioTranscodeFormatIds.Flac,
                AudioTranscodeEncoderIds.Ffmpeg(
                    "flac"),
                AudioTranscodeRateMode.Lossless),
            new(
                AudioTranscodeDestinationMode.ChosenFolder,
                outputRoot,
                preserveLayout,
                "{Name}{Extension}",
                AudioTranscodeCollisionPolicy.Stop));

    private static AudioTranscodeService CreatePreviewService(
        IAppSettings settings,
        IAudioTranscodeCapabilityService? capabilities =
            null,
        IAudioTranscodeAdapter? adapter = null)
    {
        var coordinator =
            new FileMutationCoordinator();
        var journals =
            new OperationJournalService(
                coordinator);
        return new(
            settings,
            capabilities ??
            new FixedCapabilityService(),
            adapter ??
            new RecordingAdapter(),
            new RecordingProjection(),
            new TranscodeWorkScheduler(
                settings,
                processorCount: 2),
            new ReviewedChangeBatchService(
                new FileMutationPlanExecutor(
                    coordinator),
                journals),
            new ReviewedChangeHistoryService(
                settings,
                journals),
            new SuccessfulDecodedVerifier());
    }

    private static AudioTranscodeCapabilitySnapshot
        CorrectionCapabilitySnapshot() =>
        new(
            [],
            [
                new(
                    AudioTranscodeFormatIds.WavPack,
                    "wavpack",
                    "wv",
                    ".wv",
                    true,
                    [
                        AudioTranscodeEncoderIds
                            .WavPackCli,
                    ]),
            ],
            [
                new(
                    AudioTranscodeEncoderIds.WavPackCli,
                    AudioTranscodeToolKind.WavPack,
                    "wavpack",
                    AudioEncoderThreadingMode
                        .SingleThreaded,
                    [
                        new(
                            AudioTranscodeRateMode
                                .Lossless),
                        new(
                            AudioTranscodeRateMode
                                .HybridBitrate,
                            200,
                            960,
                            SupportsCorrectionFile:
                                true),
                    ],
                    [],
                    [16, 24],
                    SupportsCorrectionFile: true),
            ],
            DateTimeOffset.UtcNow,
            1);

    private static AudioTranscodeCapabilitySnapshot SnapshotWithTools(
        params AudioToolProbeResult[] tools) =>
        new(
            [.. tools],
            [],
            [],
            DateTimeOffset.UtcNow,
            1);

    private static AudioToolProbeResult FfmpegProbe(
        IEnumerable<string> demuxers) =>
        new(
            AudioTranscodeToolKind.Ffmpeg,
            AudioToolProbeState.Ready,
            "ffmpeg",
            "ffmpeg",
            "test",
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                "flac"),
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                "flac"),
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                "flac"),
            demuxers.ToImmutableHashSet(
                StringComparer.Ordinal));

    private static AudioEncoderDescriptor FfmpegEncoder() =>
        new(
            AudioTranscodeEncoderIds.Ffmpeg("flac"),
            AudioTranscodeToolKind.Ffmpeg,
            "flac",
            AudioEncoderThreadingMode.ThreadCountControllable,
            [new(AudioTranscodeRateMode.Lossless)],
            [],
            [16, 24]);

    private static OperationPathSnapshot FileSnapshot(
        string path)
    {
        var info = new FileInfo(path);
        return new(
            true,
            false,
            info.Length,
            info.LastWriteTimeUtc)
        {
            Path = Path.GetFullPath(path),
        };
    }

    private static string FileHash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(
            SHA256.HashData(stream));
    }

    private static string ManagedProcessFixtureExecutable =>
        Path.Combine(
            AppContext.BaseDirectory,
            "FpcalcFixture",
            OperatingSystem.IsWindows()
                ? "FpcalcFixture.exe"
                : "FpcalcFixture");

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }

    private sealed class FixedCapabilityService :
        IAudioTranscodeCapabilityService
    {
        public Task<AudioTranscodeCapabilitySnapshot> GetAsync(
            bool forceRefresh = false,
            CancellationToken ct = default) =>
            Task.FromResult(new AudioTranscodeCapabilitySnapshot(
                [FfmpegProbe(["flac", "dsf"])],
                [
                    new(
                        AudioTranscodeFormatIds.Flac,
                        "flac",
                        "flac",
                        ".flac",
                        true,
                        [
                            AudioTranscodeEncoderIds
                                .Ffmpeg("flac"),
                        ]),
                ],
                [FfmpegEncoder()],
                DateTimeOffset.UtcNow,
                1));

        public void Invalidate()
        {
        }
    }

    private sealed class StaticCapabilityService(
        AudioTranscodeCapabilitySnapshot snapshot) :
        IAudioTranscodeCapabilityService
    {
        public Task<AudioTranscodeCapabilitySnapshot> GetAsync(
            bool forceRefresh = false,
            CancellationToken ct = default) =>
            Task.FromResult(snapshot);

        public void Invalidate()
        {
        }
    }

    private sealed class MutableCapabilityService(
        AudioTranscodeCapabilitySnapshot snapshot) :
        IAudioTranscodeCapabilityService
    {
        public AudioTranscodeCapabilitySnapshot Snapshot
        {
            get;
            set;
        } = snapshot;

        public Task<AudioTranscodeCapabilitySnapshot> GetAsync(
            bool forceRefresh = false,
            CancellationToken ct = default) =>
            Task.FromResult(Snapshot);

        public void Invalidate()
        {
        }
    }

    private sealed class RecordingAdapter :
        IAudioTranscodeAdapter
    {
        public string? SourcePath { get; private set; }

        public Task EncodeAsync(
            string sourcePath,
            string destinationPath,
            AudioTranscodeSettings settings,
            AudioEncoderDescriptor encoder,
            int threadCount,
            IProgress<AudioTranscodeAdapterProgress>?
                progress = null,
            CancellationToken ct = default)
        {
            SourcePath = sourcePath;
            File.Copy(
                sourcePath,
                destinationPath);
            progress?.Report(new(
                "encoding",
                TimeSpan.FromHours(1)));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProjection :
        ITranscodeMetadataProjectionService
    {
        public string? SourcePath { get; private set; }

        public IReadOnlyList<OperationIssue> Project(
            string sourcePath,
            string destinationPath,
            bool preserveMetadata,
            bool preserveArtwork)
        {
            SourcePath = sourcePath;
            return [];
        }
    }

    private sealed class SuccessfulDecodedVerifier :
        IDecodedAudioVerificationService
    {
        public List<IReadOnlyList<DecodedAudioPair>>
            Pairs { get; } = [];

        public Task<AnalysisReport> VerifyAsync(
            string ffmpegExecutable,
            IReadOnlyList<DecodedAudioPair> pairs,
            IProgress<DecodedAudioProgress>? progress = null,
            CancellationToken ct = default)
        {
            Pairs.Add(pairs);
            return Task.FromResult(
                new AnalysisReport(
                    "decoded",
                    []));
        }
    }

    private sealed class RecordingPcmReference :
        ITranscodePcmReferenceService
    {
        public int Calls { get; private set; }
        public string? LastPath { get; private set; }

        public Task<string> CreateAsync(
            string sourcePath,
            AudioTranscodeSettings settings,
            string stagedOutputPath,
            CancellationToken ct = default)
        {
            Calls++;
            LastPath = Path.Combine(
                Path.GetDirectoryName(
                    stagedOutputPath)!,
                Guid.NewGuid().ToString("N") +
                ".reference.flac");
            File.Copy(
                sourcePath,
                LastPath);
            return Task.FromResult(LastPath);
        }
    }

    private sealed class FixedRecoverySpaceProbe(
        long? available) : IRecoverySpaceProbe
    {
        public long? GetAvailableFreeSpace(
            string root) =>
            available;
    }

    private sealed class ProbeRunner : IManagedProcessRunner
    {
        public int Calls { get; private set; }

        public Task<ManagedProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string? workingDirectory = null,
            IProgress<string>? standardOutputLines = null,
            CancellationToken ct = default)
        {
            Calls++;
            if (!executable.Equals(
                    "ffmpeg",
                    StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException(executable);
            string output = arguments.Last() switch
            {
                "-version" => "ffmpeg version test",
                "-encoders" => """
                    A..... flac
                    A..... flac_fixed
                    A..... libmp3lame
                    A..... aac
                    """,
                "-decoders" => """
                    A..... flac
                    A..... mp3
                    A..... aac
                    """,
                "-muxers" => """
                    E flac
                    E mp3
                    E ipod
                    E adts
                    """,
                "-demuxers" => """
                    D flac
                    D mp3
                    D mov
                    D aac
                    """,
                _ => "",
            };
            return Task.FromResult(
                new ManagedProcessResult(0, output, ""));
        }
    }

    private sealed class MutableWavPackProbeRunner :
        IManagedProcessRunner
    {
        private int _wavPackHelpCalls;

        public bool SupportsCorrectionFile
        {
            get;
            set;
        } = true;

        public int WavPackHelpCalls =>
            Volatile.Read(ref _wavPackHelpCalls);

        public Task<ManagedProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string? workingDirectory = null,
            IProgress<string>? standardOutputLines = null,
            CancellationToken ct = default)
        {
            if (!executable.Equals(
                    "wavpack",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new FileNotFoundException(executable);
            }

            if (arguments.SequenceEqual(["--version"]))
            {
                return Task.FromResult(
                    new ManagedProcessResult(
                        0,
                        "WavPack version test",
                        ""));
            }

            if (arguments.SequenceEqual(["--help"]))
            {
                Interlocked.Increment(
                    ref _wavPackHelpCalls);
                string help = SupportsCorrectionFile
                    ? "WavPack help with correction support"
                    : "WavPack help";
                return Task.FromResult(
                    new ManagedProcessResult(
                        0,
                        help,
                        ""));
            }

            return Task.FromResult(
                new ManagedProcessResult(
                    1,
                    "",
                    "Unexpected arguments."));
        }
    }

    private sealed class MacProbeRunner :
        IManagedProcessRunner
    {
        public Task<ManagedProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string? workingDirectory = null,
            IProgress<string>? standardOutputLines = null,
            CancellationToken ct = default)
        {
            if (!executable.Equals(
                    "MAC",
                    StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException(executable);
            const string help = """
                Monkey's Audio Console
                Compress: input.wav output.ape -c2000 -threads=#
                Verify: input.ape -v
                """;
            return Task.FromResult(
                new ManagedProcessResult(0, help, ""));
        }
    }

    private sealed class FixedProcessRunner(
        string standardOutput) : IManagedProcessRunner
    {
        public Task<ManagedProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string? workingDirectory = null,
            IProgress<string>? standardOutputLines = null,
            CancellationToken ct = default) =>
            Task.FromResult(
                new ManagedProcessResult(
                    0,
                    standardOutput,
                    ""));
    }

    private sealed class ReferenceProcessRunner :
        IManagedProcessRunner
    {
        public string Executable { get; private set; } =
            string.Empty;
        public List<string> Arguments { get; } = [];

        public Task<ManagedProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string? workingDirectory = null,
            IProgress<string>? standardOutputLines = null,
            CancellationToken ct = default)
        {
            Executable = executable;
            Arguments.AddRange(arguments);
            File.WriteAllText(
                arguments[^1],
                "reference");
            return Task.FromResult(
                new ManagedProcessResult(0, "", ""));
        }
    }

    private sealed class MemorySettings(
        LibraryConfiguration? configuration = null) :
        IAppSettings
    {
        private readonly ConcurrentDictionary<string, string>
            _preferences = new(StringComparer.Ordinal);

        public string? ConfigPath => null;
        public LibraryConfiguration? Configuration =>
            configuration;
        public AppConfigurationSnapshot GetSnapshot() =>
            new(null, configuration, 1);
        public event EventHandler? ConfigurationChanged
        {
            add
            {
            }
            remove
            {
            }
        }
        public void LoadConfig(string path) =>
            throw new NotSupportedException();
        public string? GetRememberedConfigPath() => null;
        public IReadOnlyList<string> RecentConfigPaths => [];
        public void ClearRecentConfigs()
        {
        }
        public string? GetPreference(string key) =>
            _preferences.GetValueOrDefault(key);
        public void SetPreference(string key, string? value)
        {
            if (value is null)
                _preferences.TryRemove(key, out _);
            else
                _preferences[key] = value;
        }
    }
}
