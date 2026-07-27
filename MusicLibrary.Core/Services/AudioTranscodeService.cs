using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

public sealed record AudioTranscodeAdapterProgress(
    string Phase,
    TimeSpan? EncodedTime = null);

public interface IAudioTranscodeAdapter
{
    Task EncodeAsync(
        string sourcePath,
        string destinationPath,
        AudioTranscodeSettings settings,
        AudioEncoderDescriptor encoder,
        int threadCount,
        IProgress<AudioTranscodeAdapterProgress>? progress = null,
        CancellationToken ct = default);
}

public interface ITranscodeMetadataProjectionService
{
    IReadOnlyList<OperationIssue> Project(
        string sourcePath,
        string destinationPath,
        bool preserveMetadata,
        bool preserveArtwork);

}

public interface IAudioTranscodeService
{
    Task<AudioTranscodePlan> PreviewAsync(
        AudioTranscodeRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    Task<AudioTranscodeStageResult> StageAsync(
        AudioTranscodePlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    Task<AudioTranscodeStageResult> StageWithSourceOverridesAsync(
        AudioTranscodePlan plan,
        IReadOnlyDictionary<string, string> sourceOverrides,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default) =>
        StageAsync(plan, progress, ct);

    Task<AudioTranscodeApplyResult> ApplyAsync(
        AudioTranscodeStageResult stage,
        IReadOnlySet<Guid>? readyItemIds = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    Task<AudioTranscodeApplyResult> ApplyBatchAsync(
        IReadOnlyList<AudioTranscodeStageResult> stages,
        IReadOnlySet<Guid>? readyItemIds = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);

    Task<AudioTranscodeApplyResult> ApplyReviewedBatchAsync(
        IReadOnlyList<AudioTranscodeStageResult> stages,
        IReadOnlyList<FileMutationPlan> additionalParticipants,
        IReadOnlySet<Guid>? readyItemIds = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default) =>
        ApplyBatchAsync(
            stages,
            readyItemIds,
            progress,
            ct);

    Task DiscardStageAsync(
        AudioTranscodeStageResult stage,
        CancellationToken ct = default);
}

public sealed class AudioTranscodeAdapter(
    IAppSettings appSettings,
    IManagedProcessRunner processes) : IAudioTranscodeAdapter
{
    public async Task EncodeAsync(
        string sourcePath,
        string destinationPath,
        AudioTranscodeSettings settings,
        AudioEncoderDescriptor encoder,
        int threadCount,
        IProgress<AudioTranscodeAdapterProgress>? progress = null,
        CancellationToken ct = default)
    {
        string preparedSource = sourcePath;
        string? decodedSource = null;
        try
        {
            string[] sourceDecoders =
                OptimFrogExecutablesForSource(
                    sourcePath);
            if (sourceDecoders.Length > 0)
            {
                decodedSource = SiblingTemporary(
                    destinationPath,
                    ".source.wav");
                Exception? lastError = null;
                foreach (string sourceDecoder in
                         sourceDecoders)
                {
                    try
                    {
                        await DecodeOptimFrogAsync(
                                sourceDecoder,
                                sourcePath,
                                decodedSource,
                                ct)
                            .ConfigureAwait(false);
                        lastError = null;
                        break;
                    }
                    catch (OperationCanceledException)
                        when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        lastError = error;
                        TryDelete(decodedSource);
                    }
                }
                if (lastError is not null)
                    throw new InvalidOperationException(
                        "No configured OptimFROG decoder could " +
                        "read the source.",
                        lastError);
                preparedSource = decodedSource;
            }

            switch (encoder.Tool)
            {
                case AudioTranscodeToolKind.Ffmpeg:
                    await EncodeFfmpegAsync(
                        preparedSource,
                        destinationPath,
                        settings,
                        encoder,
                        threadCount,
                        progress,
                        ct).ConfigureAwait(false);
                    return;
                case AudioTranscodeToolKind.WavPack:
                    await EncodeWavPackAsync(
                        preparedSource,
                        destinationPath,
                        settings,
                        progress,
                        ct).ConfigureAwait(false);
                    return;
                case AudioTranscodeToolKind.OptimFrog:
                    await EncodeOptimFrogAsync(
                        preparedSource,
                        destinationPath,
                        settings,
                        encoder,
                        progress,
                        ct).ConfigureAwait(false);
                    return;
                case AudioTranscodeToolKind.MonkeysAudio:
                    await EncodeMonkeysAudioAsync(
                        preparedSource,
                        destinationPath,
                        settings,
                        threadCount,
                        progress,
                        ct).ConfigureAwait(false);
                    return;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(encoder.Tool));
            }
        }
        finally
        {
            TryDelete(decodedSource);
        }
    }

    private async Task DecodeOptimFrogAsync(
        string executableName,
        string sourcePath,
        string destinationPath,
        CancellationToken ct)
    {
        string directory =
            appSettings.GetPreference(
                OptimFrogFingerprintInputService
                    .ToolsDirectoryPreferenceKey) ??
            Path.Combine(
                AppContext.BaseDirectory,
                "tools",
                "optimfrog");
        string executable = Path.Combine(
            directory,
            OperatingSystem.IsWindows()
                ? executableName + ".exe"
                : executableName);
        ManagedProcessResult result =
            await processes.RunAsync(
                executable,
                [
                    "--decode",
                    sourcePath,
                    "--output",
                    destinationPath,
                ],
                ct: ct).ConfigureAwait(false);
        EnsureSuccess(executable, result);
        if (!File.Exists(destinationPath) ||
            new FileInfo(destinationPath).Length == 0)
            throw new InvalidDataException(
                "The OptimFROG decoder did not produce PCM audio.");
    }

    private static string[] OptimFrogExecutablesForSource(
        string sourcePath) =>
        Path.GetExtension(sourcePath).ToLowerInvariant() switch
        {
            ".ofr" => ["ofr", "off"],
            ".ofs" => ["ofs"],
            ".off" => ["off"],
            _ => [],
        };

    private async Task EncodeFfmpegAsync(
        string sourcePath,
        string destinationPath,
        AudioTranscodeSettings settings,
        AudioEncoderDescriptor encoder,
        int threadCount,
        IProgress<AudioTranscodeAdapterProgress>? progress,
        CancellationToken ct)
    {
        string executable =
            appSettings.GetSnapshot()
                .Configuration?.FfmpegPath ??
            "ffmpeg";
        var arguments = new List<string>
        {
            "-y",
            "-hide_banner",
            "-nostdin",
            "-loglevel",
            "error",
            "-progress",
            "pipe:1",
            "-i",
            sourcePath,
            "-map",
            "0:a:0",
            "-map_metadata",
            "-1",
            "-c:a",
            encoder.ExecutableEncoder,
        };
        AddRateControl(
            arguments,
            settings,
            encoder.ExecutableEncoder);
        AddSampleConversion(
            arguments,
            settings,
            sourcePath);
        if (settings.CompressionEffort is >= 0 &&
            encoder.ExecutableEncoder.Equals(
                "flac",
                StringComparison.Ordinal))
        {
            arguments.Add("-compression_level");
            arguments.Add(
                Math.Clamp(
                        settings.CompressionEffort,
                        0,
                        12)
                    .ToString(CultureInfo.InvariantCulture));
        }
        if (threadCount > 0)
        {
            arguments.Add("-threads");
            arguments.Add(
                threadCount.ToString(
                    CultureInfo.InvariantCulture));
        }
        if (Path.GetExtension(destinationPath).Equals(
                ".rf64",
                StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("-rf64");
            arguments.Add("always");
            arguments.Add("-f");
            arguments.Add("wav");
        }
        arguments.Add(destinationPath);

        var lineProgress = new Progress<string>(line =>
        {
            if (!line.StartsWith(
                    "out_time_us=",
                    StringComparison.Ordinal) ||
                !long.TryParse(
                    line.AsSpan("out_time_us=".Length),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long microseconds))
                return;
            progress?.Report(new(
                "encoding",
                TimeSpan.FromMicroseconds(
                    Math.Max(0, microseconds))));
        });
        ManagedProcessResult result =
            await processes.RunAsync(
                executable,
                arguments,
                standardOutputLines: lineProgress,
                ct: ct).ConfigureAwait(false);
        EnsureSuccess(
            executable,
            result);
    }

    private async Task EncodeWavPackAsync(
        string sourcePath,
        string destinationPath,
        AudioTranscodeSettings settings,
        IProgress<AudioTranscodeAdapterProgress>? progress,
        CancellationToken ct)
    {
        string executable =
            appSettings.GetSnapshot()
                .Configuration?.WavpackPath ??
            "wavpack";
        string input = sourcePath;
        string? bridge = null;
        try
        {
            if (!Path.GetExtension(sourcePath).Equals(
                    ".wav",
                    StringComparison.OrdinalIgnoreCase) &&
                !Path.GetExtension(sourcePath).Equals(
                    ".dsf",
                    StringComparison.OrdinalIgnoreCase))
            {
                bridge = SiblingTemporary(
                    destinationPath,
                    ".wav");
                await EncodePcmBridgeAsync(
                    sourcePath,
                    bridge,
                    settings,
                    ct).ConfigureAwait(false);
                input = bridge;
            }

            var arguments = new List<string>
            {
                "-q",
                "-y",
                "-v",
            };
            if (Path.GetExtension(input).Equals(
                    ".dsf",
                    StringComparison.OrdinalIgnoreCase))
                arguments.Add("--import-id3");
            if (settings.RateMode is
                AudioTranscodeRateMode.HybridBitrate or
                AudioTranscodeRateMode.HybridQuality)
            {
                double rate =
                    settings.RateMode ==
                        AudioTranscodeRateMode.HybridBitrate
                        ? settings.BitrateKbps ?? 320
                        : settings.Quality ?? 4;
                arguments.Add(
                    "-b" +
                    rate.ToString(
                        "0.###",
                        CultureInfo.InvariantCulture));
                if (settings.CreateCorrectionFile)
                    arguments.Add("-c");
            }
            arguments.Add(input);
            arguments.Add("-o");
            arguments.Add(destinationPath);
            progress?.Report(new("encoding"));
            ManagedProcessResult result =
                await processes.RunAsync(
                    executable,
                    arguments,
                    ct: ct).ConfigureAwait(false);
            EnsureSuccess(executable, result);
        }
        finally
        {
            TryDelete(bridge);
        }
    }

    private async Task EncodeOptimFrogAsync(
        string sourcePath,
        string destinationPath,
        AudioTranscodeSettings settings,
        AudioEncoderDescriptor encoder,
        IProgress<AudioTranscodeAdapterProgress>? progress,
        CancellationToken ct)
    {
        string directory =
            appSettings.GetPreference(
                OptimFrogFingerprintInputService
                    .ToolsDirectoryPreferenceKey) ??
            Path.Combine(
                AppContext.BaseDirectory,
                "tools",
                "optimfrog");
        string executable = Path.Combine(
            directory,
            OperatingSystem.IsWindows()
                ? encoder.ExecutableEncoder + ".exe"
                : encoder.ExecutableEncoder);
        string bridge = SiblingTemporary(
            destinationPath,
            ".wav");
        try
        {
            await EncodePcmBridgeAsync(
                sourcePath,
                bridge,
                settings,
                ct,
                floatOutput:
                    encoder.ExecutableEncoder.Equals(
                        "off",
                        StringComparison.Ordinal))
                .ConfigureAwait(false);
            var arguments = new List<string>
            {
                "--encode",
                "--overwrite",
                "--md5",
            };
            if (encoder.ExecutableEncoder.Equals(
                    "ofs",
                    StringComparison.Ordinal))
            {
                if (settings.RateMode ==
                    AudioTranscodeRateMode.AverageBitrate)
                {
                    arguments.Add("--bitrate");
                    arguments.Add(
                        (settings.BitrateKbps ?? 339)
                        .ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    arguments.Add("--quality");
                    arguments.Add(
                        (settings.Quality ?? 3)
                        .ToString(
                            "0.###",
                            CultureInfo.InvariantCulture));
                }
                if (settings.CreateCorrectionFile)
                    arguments.Add("--correction");
            }
            else
            {
                if (encoder.ExecutableEncoder.Equals(
                        "off",
                        StringComparison.Ordinal))
                {
                    arguments.Add("--mode");
                    arguments.Add(
                        OptimFrogFloatMode(
                            settings.CompressionEffort));
                }
                else
                {
                    arguments.Add("--preset");
                    arguments.Add(
                        Math.Clamp(
                                settings.CompressionEffort,
                                0,
                                10)
                            .ToString(
                                CultureInfo.InvariantCulture));
                }
            }
            arguments.Add(bridge);
            arguments.Add("--output");
            arguments.Add(destinationPath);
            progress?.Report(new("encoding"));
            ManagedProcessResult result =
                await processes.RunAsync(
                    executable,
                    arguments,
                    ct: ct).ConfigureAwait(false);
            EnsureSuccess(executable, result);
            ManagedProcessResult verify =
                await processes.RunAsync(
                    executable,
                    ["--verify", destinationPath],
                    ct: ct).ConfigureAwait(false);
            EnsureSuccess(executable, verify);
        }
        finally
        {
            TryDelete(bridge);
        }
    }

    private async Task EncodeMonkeysAudioAsync(
        string sourcePath,
        string destinationPath,
        AudioTranscodeSettings settings,
        int threadCount,
        IProgress<AudioTranscodeAdapterProgress>? progress,
        CancellationToken ct)
    {
        string executable =
            appSettings.GetSnapshot()
                .Configuration?
                .MonkeysAudioPath ??
            "MAC";
        string input = sourcePath;
        string? bridge = null;
        try
        {
            bool isWave =
                Path.GetExtension(sourcePath).Equals(
                    ".wav",
                    StringComparison.OrdinalIgnoreCase);
            if (!isWave ||
                settings.SampleRateHz is not null ||
                settings.BitsPerSample is not null)
            {
                bridge = SiblingTemporary(
                    destinationPath,
                    ".wav");
                AudioTranscodeSettings bridgeSettings =
                    settings.BitsPerSample is not null
                        ? settings
                        : settings with
                        {
                            BitsPerSample =
                                PcmBridgeBitDepth(
                                    settings,
                                    sourcePath),
                        };
                await EncodePcmBridgeAsync(
                        sourcePath,
                        bridge,
                        bridgeSettings,
                        ct)
                    .ConfigureAwait(false);
                input = bridge;
            }

            var arguments = new List<string>
            {
                input,
                destinationPath,
                MonkeysAudioCompressionMode(
                    settings.CompressionEffort),
            };
            if (threadCount > 0)
                arguments.Add(
                    "-threads=" +
                    threadCount.ToString(
                        CultureInfo.InvariantCulture));
            progress?.Report(new("encoding"));
            ManagedProcessResult result =
                await processes.RunAsync(
                    executable,
                    arguments,
                    ct: ct).ConfigureAwait(false);
            EnsureSuccess(executable, result);
            if (!File.Exists(destinationPath) ||
                new FileInfo(destinationPath).Length == 0)
                throw new InvalidDataException(
                    "MAC did not produce a Monkey's Audio output.");
            ManagedProcessResult verify =
                await processes.RunAsync(
                    executable,
                    [destinationPath, "-v"],
                    ct: ct).ConfigureAwait(false);
            EnsureSuccess(executable, verify);
        }
        finally
        {
            TryDelete(bridge);
        }
    }

    internal static string MonkeysAudioCompressionMode(
        int compressionEffort) =>
        Math.Clamp(
            compressionEffort,
            0,
            10) switch
        {
            <= 1 => "-c1000",
            <= 3 => "-c2000",
            <= 5 => "-c3000",
            <= 7 => "-c4000",
            _ => "-c5000",
        };

    internal static int PcmBridgeBitDepth(
        AudioTranscodeSettings settings,
        string sourcePath)
    {
        int? converted =
            EffectiveIntegerConversionBitDepth(
                settings,
                sourcePath);
        if (converted is not null)
            return converted.Value;
        try
        {
            uint sourceBits =
                MediaFile.GetFile(
                        sourcePath,
                        readOnly: true)
                    .Codecs.FirstOrDefault()?
                    .BitsPerSample ?? 0;
            return sourceBits switch
            {
                > 0 and <= 16 => 16,
                <= 24 => 24,
                _ => 32,
            };
        }
        catch
        {
            return 24;
        }
    }

    private async Task EncodePcmBridgeAsync(
        string sourcePath,
        string destinationPath,
        AudioTranscodeSettings settings,
        CancellationToken ct,
        bool floatOutput = false)
    {
        string ffmpeg =
            appSettings.GetSnapshot()
                .Configuration?.FfmpegPath ??
            "ffmpeg";
        var arguments = new List<string>
        {
            "-y",
            "-hide_banner",
            "-nostdin",
            "-loglevel",
            "error",
            "-i",
            sourcePath,
            "-map",
            "0:a:0",
            "-map_metadata",
            "-1",
            "-c:a",
            PcmBridgeCodec(
                settings,
                floatOutput),
        };
        AddSampleConversion(
            arguments,
            settings,
            sourcePath);
        arguments.Add(destinationPath);
        ManagedProcessResult result =
            await processes.RunAsync(
                ffmpeg,
                arguments,
                ct: ct).ConfigureAwait(false);
        EnsureSuccess(ffmpeg, result);
    }

    internal static string PcmBridgeCodec(
        AudioTranscodeSettings settings,
        bool floatOutput) =>
        floatOutput
            ? "pcm_f32le"
            : settings.BitsPerSample switch
            {
                <= 16 => "pcm_s16le",
                <= 24 => "pcm_s24le",
                _ => "pcm_s32le",
            };

    internal static string OptimFrogFloatMode(
        int compressionEffort) =>
        Math.Clamp(
            compressionEffort,
            0,
            10) switch
        {
            <= 1 => "fast",
            <= 3 => "normal",
            <= 5 => "high",
            6 => "turbonew",
            7 => "fastnew",
            8 => "normalnew",
            9 => "highnew-light",
            _ => "extranew-light",
        };

    private static void AddRateControl(
        List<string> arguments,
        AudioTranscodeSettings settings,
        string encoder)
    {
        if (settings.RateMode is
            AudioTranscodeRateMode.ConstantBitrate or
            AudioTranscodeRateMode.AverageBitrate or
            AudioTranscodeRateMode.ConstrainedVariableBitrate)
        {
            arguments.Add("-b:a");
            arguments.Add(
                $"{settings.BitrateKbps ?? 256}k");
        }
        if (settings.RateMode ==
            AudioTranscodeRateMode.VariableQuality)
        {
            arguments.Add(
                encoder.Equals(
                    "libfdk_aac",
                    StringComparison.Ordinal)
                    ? "-vbr"
                    : "-q:a");
            arguments.Add(
                (settings.Quality ?? 4)
                .ToString(
                    "0.###",
                    CultureInfo.InvariantCulture));
        }
        if (encoder.Contains(
                "opus",
                StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("-vbr");
            arguments.Add(settings.RateMode switch
            {
                AudioTranscodeRateMode.ConstantBitrate =>
                    "off",
                AudioTranscodeRateMode
                    .ConstrainedVariableBitrate =>
                    "constrained",
                _ => "on",
            });
        }
    }

    internal static void AddSampleConversion(
        List<string> arguments,
        AudioTranscodeSettings settings,
        string sourcePath)
    {
        int? effectiveBits =
            EffectiveIntegerConversionBitDepth(
                settings,
                sourcePath);
        if (settings.SampleRateHz is null &&
            effectiveBits is null)
            return;
        var options = new List<string>();
        if (settings.SampleRateHz is { } rate)
            options.Add(
                rate.ToString(
                    CultureInfo.InvariantCulture));
        options.Add("filter_size=64");
        options.Add("phase_shift=10");
        options.Add("exact_rational=1");
        if (effectiveBits is { } bits)
        {
            string format = bits <= 16
                ? "s16"
                : "s32";
            options.Add($"osf={format}");
            if (RequiresDither(
                    sourcePath,
                    bits))
                options.Add(
                    "dither_method=triangular_hp");
        }
        arguments.Add("-af");
        arguments.Add(
            "aresample=" +
            string.Join(':', options));
    }

    internal static int? EffectiveIntegerConversionBitDepth(
        AudioTranscodeSettings settings,
        string sourcePath)
    {
        if (settings.BitsPerSample is { } requested)
            return requested;
        if (!RequiresAutomaticIntegerProjection(
                sourcePath))
            return null;
        if (settings.FormatId ==
                AudioTranscodeFormatIds.OptimFrogFloat ||
            settings.EncoderId.EndsWith(
                "pcm_f32le",
                StringComparison.Ordinal) ||
            settings.EncoderId.EndsWith(
                "pcm_f32be",
                StringComparison.Ordinal))
            return null;
        return settings.FormatId switch
        {
            AudioTranscodeFormatIds.Flac or
            AudioTranscodeFormatIds.AlacM4a or
            AudioTranscodeFormatIds.TrueAudio or
            AudioTranscodeFormatIds.MonkeysAudio =>
                24,
            AudioTranscodeFormatIds.PcmWave or
            AudioTranscodeFormatIds.PcmRf64 or
            AudioTranscodeFormatIds.PcmAiff
                when settings.EncoderId.Contains(
                    "pcm_s16",
                    StringComparison.Ordinal) ||
                     settings.EncoderId ==
                        AudioTranscodeEncoderIds.Automatic =>
                16,
            AudioTranscodeFormatIds.PcmWave or
            AudioTranscodeFormatIds.PcmRf64 or
            AudioTranscodeFormatIds.PcmAiff
                when settings.EncoderId.Contains(
                    "pcm_s24",
                    StringComparison.Ordinal) =>
                24,
            AudioTranscodeFormatIds.PcmWave or
            AudioTranscodeFormatIds.PcmRf64 or
            AudioTranscodeFormatIds.PcmAiff or
            AudioTranscodeFormatIds.WavPack or
            AudioTranscodeFormatIds.OptimFrog or
            AudioTranscodeFormatIds.OptimFrogDualStream =>
                32,
            _ => null,
        };
    }

    private static bool RequiresAutomaticIntegerProjection(
        string sourcePath)
    {
        try
        {
            ICodecProvider? codec =
                MediaFile.GetFile(
                        sourcePath,
                        readOnly: true)
                    .Codecs.FirstOrDefault();
            return codec is null ||
                codec.CodecType == CodecType.Lossy ||
                codec.BitsPerSample == 0 ||
                codec.CodecName.Contains(
                    "float",
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    internal static bool RequiresDither(
        string sourcePath,
        int targetBits)
    {
        try
        {
            ICodecProvider? codec =
                MediaFile.GetFile(
                        sourcePath,
                        readOnly: true)
                    .Codecs.FirstOrDefault();
            if (codec is null)
                return true;
            return codec.BitsPerSample == 0 ||
                codec.BitsPerSample > targetBits ||
                codec.CodecName.Contains(
                    "float",
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // An unknown source precision is treated conservatively so an
            // integer conversion cannot introduce correlated truncation.
            return true;
        }
    }

    private static void EnsureSuccess(
        string executable,
        ManagedProcessResult result)
    {
        if (result.ExitCode == 0)
            return;
        string detail =
            string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput.Trim()
                : result.StandardError.Trim();
        throw new InvalidOperationException(
            $"'{executable}' exited with code " +
            $"{result.ExitCode}: {detail}");
    }

    private static string SiblingTemporary(
        string destinationPath,
        string extension) =>
        Path.Combine(
            Path.GetDirectoryName(destinationPath)!,
            "." + Path.GetFileName(destinationPath) +
            "." + Guid.NewGuid().ToString("N") +
            extension);

    private static void TryDelete(string? path)
    {
        try
        {
            if (path is not null &&
                File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}

public sealed class TranscodeMetadataProjectionService :
    ITranscodeMetadataProjectionService
{
    public IReadOnlyList<OperationIssue> Project(
        string sourcePath,
        string destinationPath,
        bool preserveMetadata,
        bool preserveArtwork)
    {
        var issues = new List<OperationIssue>();
        var projectedKnown =
            new List<KeyValuePair<TagFields, string>>();
        var projectedCustom =
            new List<KeyValuePair<string, string>>();
        IMetadataImage[] projectedImages = [];
        IMediaFile source = MediaFile.GetFile(
            sourcePath,
            readOnly: true,
            readArtwork: preserveArtwork);
        FileAttributes attributes =
            File.GetAttributes(destinationPath);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(
                destinationPath,
                attributes &
                ~FileAttributes.ReadOnly);
        IMediaFile destination = MediaFile.GetFile(
            destinationPath);
        IMetadataWriter writer =
            destination as IMetadataWriter ??
            destination.Tags.OfType<IMetadataWriter>()
                .FirstOrDefault() ??
            throw new InvalidDataException(
                $"Output tag format is not writable: " +
                $"{destinationPath}");

        if (preserveMetadata)
        {
            foreach (KeyValuePair<TagFields, string> field in
                     source.Tags.SelectMany(tag =>
                             tag.GetKnownMetadata())
                         .GroupBy(field => field.Key)
                         .SelectMany(group =>
                             group.Distinct()))
            {
                try
                {
                    writer.SetField(
                        field.Key,
                        field.Value);
                    projectedKnown.Add(field);
                }
                catch (Exception error)
                    when (error is
                          ArgumentException or
                          NotSupportedException)
                {
                    issues.Add(new(
                        "transcode.metadata-not-representable",
                        OperationIssueSeverity.Warning,
                        $"The destination cannot represent " +
                        $"'{field.Key}'.",
                        sourcePath));
                }
            }

            IUserStringMetadata? sourceStrings =
                source as IUserStringMetadata ??
                source.Tags
                    .OfType<IUserStringMetadata>()
                    .FirstOrDefault();
            IUserStringMetadata? destinationStrings =
                destination as IUserStringMetadata ??
                destination.Tags
                    .OfType<IUserStringMetadata>()
                    .FirstOrDefault();
            if (sourceStrings is not null &&
                destinationStrings is not null)
            {
                foreach (KeyValuePair<string, string> field in
                         sourceStrings
                             .GetAddressableUserStrings())
                {
                    try
                    {
                        destinationStrings.SetUserString(
                            field.Key,
                            field.Value);
                        projectedCustom.Add(field);
                    }
                    catch (Exception error)
                        when (error is
                              ArgumentException or
                              NotSupportedException)
                    {
                        issues.Add(new(
                            "transcode.custom-metadata-not-representable",
                            OperationIssueSeverity.Warning,
                            $"The destination cannot represent " +
                            $"the custom field '{field.Key}'.",
                            sourcePath));
                    }
                }
            }
            else if (sourceStrings?.GetAddressableUserStrings()
                         .Any() == true)
            {
                issues.Add(new(
                    "transcode.custom-metadata-not-representable",
                    OperationIssueSeverity.Warning,
                    "The destination cannot represent custom fields.",
                    sourcePath));
            }
        }

        if (preserveArtwork)
        {
            IArtworkWriter? artworkWriter =
                destination as IArtworkWriter ??
                destination.Tags
                    .OfType<IArtworkWriter>()
                    .FirstOrDefault();
            IMetadataImage[] images =
                [.. source.Tags.SelectMany(tag =>
                    tag.GetImageMetadata())];
            if (artworkWriter is null &&
                images.Length > 0)
            {
                issues.Add(new(
                    "transcode.artwork-not-representable",
                    OperationIssueSeverity.Warning,
                    "The destination cannot represent embedded artwork.",
                    sourcePath));
            }
            else if (artworkWriter is not null)
            {
                artworkWriter.SetImages(
                    images.Select(image =>
                        new ArtworkImage(
                            ParsePictureType(
                                image.Category),
                            NormalizeMime(
                                image.ImageType),
                            image.Description ?? "",
                            image.Data))
                    .ToList());
                projectedImages = images;
            }
        }
        destination.SaveTags();
        VerifyProjectedValues(
            destinationPath,
            projectedKnown,
            projectedCustom,
            projectedImages);
        return issues;
    }

    private static void VerifyProjectedValues(
        string destinationPath,
        IReadOnlyList<KeyValuePair<TagFields, string>>
            projectedKnown,
        IReadOnlyList<KeyValuePair<string, string>>
            projectedCustom,
        IReadOnlyList<IMetadataImage> projectedImages)
    {
        IMediaFile destination = MediaFile.GetFile(
            destinationPath,
            readOnly: true,
            readArtwork: projectedImages.Count > 0);
        ILookup<TagFields, string> actualKnown =
            destination.Tags.SelectMany(tag =>
                    tag.GetKnownMetadata())
                .ToLookup(
                    item => item.Key,
                    item => item.Value);
        foreach (KeyValuePair<TagFields, string> expected in
                 projectedKnown)
        {
            if (!actualKnown[expected.Key].Contains(
                    expected.Value,
                    StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"Projected metadata verification failed for '{expected.Key}'.");
            }
        }
        IUserStringMetadata? destinationStrings =
            destination as IUserStringMetadata ??
            destination.Tags
                .OfType<IUserStringMetadata>()
                .FirstOrDefault();
        IReadOnlyDictionary<string, string> actualCustom =
            destinationStrings?.GetAddressableUserStrings()
                .ToDictionary(
                    item => item.Key,
                    item => item.Value,
                    StringComparer.Ordinal) ??
            new Dictionary<string, string>();
        foreach (KeyValuePair<string, string> expected in
                 projectedCustom)
        {
            if (!actualCustom.TryGetValue(
                    expected.Key,
                    out string? written) ||
                !StringComparer.Ordinal.Equals(
                    expected.Value,
                    written))
            {
                throw new InvalidDataException(
                    $"Projected custom metadata verification failed for '{expected.Key}'.");
            }
        }
        IMetadataImage[] actualImages =
        [
            .. destination.Tags.SelectMany(tag =>
                tag.GetImageMetadata()),
        ];
        if (projectedImages.Count != actualImages.Length)
            throw new InvalidDataException(
                "Projected artwork count verification failed.");
        for (int index = 0;
             index < projectedImages.Count;
             index++)
        {
            if (!projectedImages[index].Data.AsSpan()
                    .SequenceEqual(actualImages[index].Data))
            {
                throw new InvalidDataException(
                    $"Projected artwork verification failed at image {index + 1}.");
            }
        }
    }

    private static ID3v2Util.APICType ParsePictureType(
        string? value) =>
        Enum.TryParse(
            value,
            true,
            out ID3v2Util.APICType type)
            ? type
            : ID3v2Util.APICType.FrontCover;

    private static string NormalizeMime(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "image/jpeg";
        return value.Contains('/')
            ? value
            : "image/" +
              value.TrimStart('.')
                  .ToLowerInvariant();
    }
}

public sealed class AudioTranscodeService(
    IAppSettings settings,
    IAudioTranscodeCapabilityService capabilities,
    IAudioTranscodeAdapter adapter,
    ITranscodeMetadataProjectionService metadata,
    ITranscodeWorkScheduler scheduler,
    IReviewedChangeBatchService reviewedChanges,
    IReviewedChangeHistoryService history,
    IDecodedAudioVerificationService decodedVerification,
    IRecoverySpaceProbe? recoverySpace = null,
    IAudioSourceLayoutInspector? sourceLayout = null,
    ITranscodePcmReferenceService? pcmReference = null,
    ITranscodeCorrectionVerificationService?
        correctionVerification = null,
    IReindexService? reindex = null) :
    IAudioTranscodeService
{
    public async Task<AudioTranscodePlan> PreviewAsync(
        AudioTranscodeRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        AudioTranscodeCapabilitySnapshot snapshot =
            await capabilities.GetAsync(
                ct: ct).ConfigureAwait(false);
        AudioTranscodeFormatDescriptor? format =
            snapshot.FindFormat(
                request.Settings.FormatId);
        var planIssues = new List<OperationIssue>();
        if (format is null)
        {
            planIssues.Add(new(
                "transcode.format-unavailable",
                OperationIssueSeverity.Blocker,
                "The selected output format is not available."));
            return new(
                Guid.NewGuid(),
                request,
                [],
                [.. planIssues],
                DateTimeOffset.UtcNow,
                snapshot.ConfigurationVersion);
        }

        AudioEncoderDescriptor? encoder =
            ResolveEncoder(
                snapshot,
                format,
                request.Settings.EncoderId);
        if (encoder is null)
        {
            planIssues.Add(new(
                "transcode.encoder-unavailable",
                OperationIssueSeverity.Blocker,
                "The selected encoder is not available."));
        }
        else
        {
            ValidateSettings(
                request.Settings,
                encoder,
                planIssues);
        }

        string[] sources =
        [
            .. request.SourcePaths
                .Where(path =>
                    !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(PathComparer)
                .OrderBy(path => path, PathComparer),
        ];
        string? commonDirectory =
            sources.Length == 0
                ? null
                : CommonDirectory(sources);
        LibraryConfiguration? configuration =
            settings.GetSnapshot().Configuration;
        var claimed = new HashSet<string>(
            PathComparer);
        var items = new List<AudioTranscodePlanItem>();
        for (int index = 0;
             index < sources.Length;
             index++)
        {
            ct.ThrowIfCancellationRequested();
            string source = sources[index];
            progress?.Report(new(
                OperationPhase.Planning,
                index,
                sources.Length,
                source,
                "Planning transcode",
                MessageKey:
                    "Transcode.Progress.Planning"));
            var issues = new List<OperationIssue>();
            OperationPathSnapshot sourceSnapshot =
                Capture(source);
            if (!sourceSnapshot.Exists ||
                sourceSnapshot.IsDirectory)
            {
                issues.Add(new(
                    "transcode.source-missing",
                    OperationIssueSeverity.Blocker,
                    "The source file does not exist.",
                    source));
            }
            else if (encoder is not null)
            {
                ValidateSourceCapability(
                    snapshot,
                    encoder,
                    source,
                    issues);
                await ValidateSourceLayoutAsync(
                        source,
                        request.Destination.Mode ==
                        AudioTranscodeDestinationMode
                            .ReplaceOriginal,
                        issues,
                        ct)
                    .ConfigureAwait(false);
            }
            if (Path.GetExtension(source).Equals(
                    ".dsf",
                    StringComparison.OrdinalIgnoreCase) &&
                !format.SupportsDsd &&
                (request.Settings.SampleRateHz is null ||
                 request.Settings.BitsPerSample is null))
            {
                issues.Add(new(
                    "transcode.dsd-pcm-settings-required",
                    OperationIssueSeverity.Blocker,
                    "DSD-to-PCM conversion requires an explicit " +
                    "sample rate and bit depth.",
                    source));
            }
            string destination = ResolveDestination(
                request,
                format,
                source,
                PreferredLayoutRoot(
                    configuration,
                    source,
                    commonDirectory),
                issues);
            destination = ResolveCollision(
                source,
                destination,
                request.Destination.CollisionPolicy,
                claimed,
                issues,
                CorrectionSidecarExtension(
                    format,
                    request.Settings));
            AddInternalCatalogIssues(
                configuration,
                destination,
                issues);
            string? correctionExtension =
                CorrectionSidecarExtension(
                    format,
                    request.Settings);
            ImmutableArray<AudioTranscodePlannedSidecar>
                sidecars = correctionExtension is null
                    ? []
                    :
                    [
                        new(
                            Path.ChangeExtension(
                                destination,
                                correctionExtension),
                            Capture(
                                Path.ChangeExtension(
                                    destination,
                                    correctionExtension))),
                    ];
            if (!issues.Any(issue =>
                    issue.Severity ==
                    OperationIssueSeverity.Blocker))
            {
                claimed.Add(destination);
                foreach (AudioTranscodePlannedSidecar sidecar in
                         sidecars)
                    claimed.Add(sidecar.DestinationPath);
            }
            OperationPathSnapshot destinationSnapshot =
                Capture(destination);
            string sha = sourceSnapshot.Exists &&
                         !sourceSnapshot.IsDirectory
                ? await Sha256Async(
                    source,
                    ct).ConfigureAwait(false)
                : string.Empty;
            items.Add(new(
                Guid.NewGuid(),
                source,
                destination,
                sourceSnapshot,
                destinationSnapshot,
                sha,
                request.Settings,
                [.. issues],
                sidecars));
        }
        AddPreviewCapacityIssues(
            items,
            format,
            recoverySpace ??
            SystemRecoverySpaceProbe.Instance);

        progress?.Report(new(
            OperationPhase.Completed,
            sources.Length,
            sources.Length,
            Message:
                $"Reviewed {sources.Length:N0} transcode file(s)",
            MessageKey:
                "Transcode.Progress.Reviewed",
            MessageArguments:
                [sources.Length]));
        return new(
            Guid.NewGuid(),
            request,
            [.. items],
            [.. planIssues],
            DateTimeOffset.UtcNow,
            snapshot.ConfigurationVersion);
    }

    internal static void ValidateSourceCapability(
        AudioTranscodeCapabilitySnapshot snapshot,
        AudioEncoderDescriptor encoder,
        string sourcePath,
        ICollection<OperationIssue> issues)
    {
        string extension =
            Path.GetExtension(sourcePath).ToLowerInvariant();
        string[] optimFrogDecoders = extension switch
        {
            ".ofr" => ["ofr", "off"],
            ".ofs" => ["ofs"],
            ".off" => ["off"],
            _ => [],
        };
        if (optimFrogDecoders.Length > 0)
        {
            AudioToolProbeResult? specialist =
                snapshot.Tools.FirstOrDefault(tool =>
                    tool.Tool ==
                    AudioTranscodeToolKind.OptimFrog &&
                    tool.State == AudioToolProbeState.Ready);
            if (specialist is null ||
                !optimFrogDecoders.Any(
                    specialist.Decoders.Contains))
            {
                issues.Add(new(
                    "transcode.source-decoder-unavailable",
                    OperationIssueSeverity.Blocker,
                    "The configured OptimFROG tools cannot decode " +
                    "this source.",
                    sourcePath));
                return;
            }
            extension = ".wav";
        }

        bool usesDirectWavPackInput =
            encoder.Tool == AudioTranscodeToolKind.WavPack &&
            extension is ".wav" or ".dsf";
        if (usesDirectWavPackInput)
            return;

        AudioToolProbeResult? ffmpeg =
            snapshot.Tools.FirstOrDefault(tool =>
                tool.Tool == AudioTranscodeToolKind.Ffmpeg &&
                tool.State == AudioToolProbeState.Ready);
        if (ffmpeg is null)
        {
            issues.Add(new(
                "transcode.source-decoder-unavailable",
                OperationIssueSeverity.Blocker,
                "FFmpeg is required to decode this source.",
                sourcePath));
            return;
        }

        string[] demuxers = extension switch
        {
            ".aac" => ["aac"],
            ".aif" or ".aiff" or ".aifc" => ["aiff"],
            ".ape" => ["ape"],
            ".asf" or ".wma" or ".wmv" => ["asf"],
            ".dsf" => ["dsf"],
            ".flac" => ["flac"],
            ".m4a" or ".m4b" or ".m4r" or ".m4v" or ".mp4" =>
                ["mov", "mp4", "m4a"],
            ".mka" or ".mkv" or ".weba" or ".webm" =>
                ["matroska", "webm"],
            ".mp3" => ["mp3"],
            ".mpc" => ["mpc", "musepack"],
            ".ogg" or ".opus" or ".spx" => ["ogg"],
            ".rf64" or ".wav" => ["wav"],
            ".tak" => ["tak"],
            ".tta" => ["tta"],
            ".wv" => ["wv"],
            _ => [],
        };
        if (demuxers.Length == 0 ||
            !demuxers.Any(ffmpeg.Demuxers.Contains))
        {
            issues.Add(new(
                "transcode.source-container-unavailable",
                OperationIssueSeverity.Blocker,
                "FFmpeg cannot demux this source format.",
                sourcePath));
        }
    }

    public Task<AudioTranscodeStageResult> StageAsync(
        AudioTranscodePlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default) =>
        StageCoreAsync(
            plan,
            sourceOverrides: null,
            progress,
            ct);

    public Task<AudioTranscodeStageResult>
        StageWithSourceOverridesAsync(
            AudioTranscodePlan plan,
            IReadOnlyDictionary<string, string>
                sourceOverrides,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(
            sourceOverrides);
        return StageCoreAsync(
            plan,
            sourceOverrides,
            progress,
            ct);
    }

    private async Task<AudioTranscodeStageResult> StageCoreAsync(
        AudioTranscodePlan plan,
        IReadOnlyDictionary<string, string>?
            sourceOverrides,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        AudioTranscodeCapabilitySnapshot snapshot =
            await capabilities.GetAsync(
                forceRefresh: true,
                ct: ct).ConfigureAwait(false);
        AudioTranscodeFormatDescriptor format =
            snapshot.FindFormat(
                plan.Request.Settings.FormatId) ??
            throw new InvalidOperationException(
                "The reviewed output format is no longer available.");
        AudioEncoderDescriptor encoder =
            ResolveEncoder(
                snapshot,
                format,
                plan.Request.Settings.EncoderId) ??
            throw new InvalidOperationException(
                "The reviewed encoder is no longer available.");
        var currentSettingsIssues =
            new List<OperationIssue>();
        ValidateSettings(
            plan.Request.Settings,
            encoder,
            currentSettingsIssues);
        OperationIssue? currentSettingsBlocker =
            currentSettingsIssues.FirstOrDefault(
                issue =>
                    issue.Severity ==
                    OperationIssueSeverity.Blocker);
        if (currentSettingsBlocker is not null)
        {
            throw new InvalidOperationException(
                "The reviewed transcode settings are no longer " +
                $"available. {currentSettingsBlocker.Message}");
        }
        AudioTranscodePlanItem[] applicable =
        [
            .. plan.Items.Where(item => item.CanApply),
        ];
        var staged =
            new ConcurrentDictionary<
                Guid,
                AudioTranscodeStagedItem>();
        var workerItems = applicable
            .Select((item, index) =>
                new TranscodeWorkItem<
                    AudioTranscodePlanItem>(
                    index,
                    item,
                    VolumeKey(item.DestinationPath),
                    encoder.ThreadingMode))
            .ToArray();
        var durations = applicable.ToDictionary(
            item => item.Id,
            item => SourceDuration(item.SourcePath));
        var encodedTimes =
            new ConcurrentDictionary<Guid, TimeSpan>();
        var progressGate = new object();
        TranscodeSchedulerProgress schedulerState =
            new(0, applicable.Length, 0, []);
        void ReportAggregate()
        {
            lock (progressGate)
            {
                double totalSeconds = durations.Values.Sum(
                    value => value.TotalSeconds);
                double encodedSeconds =
                    encodedTimes.Sum(pair =>
                        Math.Min(
                            pair.Value.TotalSeconds,
                            durations.GetValueOrDefault(
                                pair.Key).TotalSeconds));
                double? percent = totalSeconds > 0
                    ? Math.Clamp(
                        encodedSeconds /
                        totalSeconds * 100,
                        0,
                        100)
                    : null;
                string message =
                    schedulerState.Active > 0
                        ? $"Transcoding " +
                          $"{schedulerState.Active:N0} file(s) · " +
                          $"{schedulerState.Elapsed:hh\\:mm\\:ss}" +
                          (percent is null
                              ? ""
                              : $" · {percent.Value:0}% encoded")
                        : "Preparing transcode";
                progress?.Report(new(
                    OperationPhase.Applying,
                    schedulerState.Completed,
                    schedulerState.Total,
                    schedulerState.ActiveItems
                        .FirstOrDefault(),
                    message,
                    MessageKey:
                        schedulerState.Active > 0
                            ? percent is null
                                ? "Transcode.Progress.EncodingUnknown"
                                : "Transcode.Progress.Encoding"
                            : "Transcode.Progress.Preparing",
                    MessageArguments:
                        schedulerState.Active > 0
                            ? percent is null
                                ?
                                [
                                    schedulerState.Active,
                                    schedulerState.Elapsed,
                                ]
                                :
                                [
                                    schedulerState.Active,
                                    schedulerState.Elapsed,
                                    percent.Value,
                                ]
                            : []));
            }
        }
        var schedulerProgress =
            new InlineProgress<
                TranscodeSchedulerProgress>(
                value =>
                {
                    schedulerState = value;
                    ReportAggregate();
                });
        ImmutableArray<string> ownedStageDirectories =
        [
            .. workerItems
                .Select(item =>
                    Path.GetDirectoryName(
                        StagePath(
                            plan.Id,
                            item.Value))!)
                .Distinct(PathComparer),
        ];
        IReadOnlyList<
                TranscodeWorkResult<AudioTranscodePlanItem>>
            results;
        try
        {
            results = await scheduler.RunAsync(
                workerItems,
                async (item, threads, cancellationToken) =>
                {
                    string stagePath = StagePath(
                        plan.Id,
                        item);
                    Directory.CreateDirectory(
                        Path.GetDirectoryName(stagePath)!);
                    try
                    {
                        string projectionSource =
                            sourceOverrides?
                                .GetValueOrDefault(
                                    item.SourcePath) ??
                            item.SourcePath;
                        ValidateSnapshot(
                            item.SourcePath,
                            item.SourceSnapshot,
                            "source");
                        string beforeHash =
                            await Sha256Async(
                                item.SourcePath,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (!beforeHash.Equals(
                                item.SourceSha256,
                                StringComparison.Ordinal))
                            throw new InvalidOperationException(
                                "The source changed after preview.");
                        await adapter.EncodeAsync(
                                projectionSource,
                                stagePath,
                                item.Settings,
                                encoder,
                                threads,
                                new InlineProgress<
                                    AudioTranscodeAdapterProgress>(
                                    value =>
                                    {
                                        if (value.EncodedTime is
                                            { } encoded)
                                            encodedTimes[item.Id] =
                                                encoded;
                                        ReportAggregate();
                                    }),
                                cancellationToken)
                            .ConfigureAwait(false);
                        IReadOnlyList<OperationIssue>
                            metadataIssues = metadata.Project(
                                projectionSource,
                                stagePath,
                                plan.Request.PreserveMetadata,
                                plan.Request.PreserveArtwork);
                        ValidateOutput(
                            item.SourcePath,
                            stagePath,
                            format,
                            item.Settings);
                        bool transformsPcm =
                            item.Settings.SampleRateHz is not null ||
                            AudioTranscodeAdapter
                                .EffectiveIntegerConversionBitDepth(
                                    item.Settings,
                                    item.SourcePath) is not null;
                        bool hasCorrection =
                            SidecarsOrEmpty(item.Sidecars).Length > 0;
                        if (format.Lossless &&
                            encoder.Tool !=
                                AudioTranscodeToolKind.OptimFrog &&
                            !hasCorrection &&
                            (!transformsPcm ||
                             pcmReference is not null))
                        {
                            string ffmpeg =
                                settings.GetSnapshot()
                                    .Configuration?.FfmpegPath ??
                                "ffmpeg";
                            string comparisonPath =
                                item.SourcePath;
                            string? referencePath = null;
                            try
                            {
                                if (transformsPcm &&
                                    pcmReference is not null)
                                {
                                    referencePath =
                                        await pcmReference.CreateAsync(
                                                item.SourcePath,
                                                item.Settings,
                                                stagePath,
                                                cancellationToken)
                                            .ConfigureAwait(false);
                                    comparisonPath = referencePath;
                                }
                                await VerifyDecodedAsync(
                                        ffmpeg,
                                        comparisonPath,
                                        stagePath,
                                        referencePath is null
                                            ? "lossless transcode"
                                            : "transformed " +
                                              "lossless transcode",
                                        cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            finally
                            {
                                TryDelete(referencePath);
                            }
                        }
                        string afterHash =
                            await Sha256Async(
                                item.SourcePath,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (!afterHash.Equals(
                                item.SourceSha256,
                                StringComparison.Ordinal))
                            throw new InvalidOperationException(
                                "The source changed while it was " +
                                "being transcoded.");
                        string outputHash =
                            await Sha256Async(
                                stagePath,
                                cancellationToken)
                            .ConfigureAwait(false);
                        ImmutableArray<AudioTranscodeStagedSidecar>
                            stagedSidecars =
                            await CaptureStagedSidecarsAsync(
                                item,
                                stagePath,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (hasCorrection)
                        {
                            if (correctionVerification is null)
                                throw new InvalidOperationException(
                                    "Correction reconstruction " +
                                    "verification is unavailable.");
                            if (transformsPcm &&
                                pcmReference is null)
                                throw new InvalidOperationException(
                                    "Transformed PCM verification is " +
                                    "unavailable.");
                            string? reconstructedPath = null;
                            string? referencePath = null;
                            try
                            {
                                reconstructedPath =
                                    await correctionVerification
                                        .ReconstructAsync(
                                            stagePath,
                                            encoder.Tool,
                                            cancellationToken)
                                        .ConfigureAwait(false);
                                string comparisonPath =
                                    item.SourcePath;
                                if (transformsPcm &&
                                    pcmReference is not null)
                                {
                                    referencePath =
                                        await pcmReference.CreateAsync(
                                                item.SourcePath,
                                                item.Settings,
                                                stagePath,
                                                cancellationToken)
                                            .ConfigureAwait(false);
                                    comparisonPath = referencePath;
                                }
                                string ffmpeg =
                                    settings.GetSnapshot()
                                        .Configuration?.FfmpegPath ??
                                    "ffmpeg";
                                await VerifyDecodedAsync(
                                        ffmpeg,
                                        comparisonPath,
                                        reconstructedPath,
                                        "correction-file reconstruction",
                                        cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            finally
                            {
                                TryDelete(reconstructedPath);
                                TryDelete(referencePath);
                            }
                        }
                        staged[item.Id] = new(
                            item with
                            {
                                Issues =
                                [
                                    .. item.Issues,
                                    .. metadataIssues,
                                ],
                            },
                            AudioTranscodeStageState.Ready,
                            stagePath,
                            outputHash,
                            new FileInfo(stagePath).Length,
                            Sidecars: stagedSidecars);
                    }
                    catch
                    {
                        TryDelete(stagePath);
                        foreach (
                            AudioTranscodePlannedSidecar sidecar in
                            SidecarsOrEmpty(item.Sidecars))
                            TryDelete(
                                Path.ChangeExtension(
                                    stagePath,
                                    Path.GetExtension(
                                        sidecar.DestinationPath)));
                        throw;
                    }
                },
                item => item.SourcePath,
                schedulerProgress,
                ct).ConfigureAwait(false);
        }
        catch
        {
            CleanupStageDirectories(
                ownedStageDirectories);
            throw;
        }
        foreach (TranscodeWorkResult<
                     AudioTranscodePlanItem> result in results)
        {
            if (result.Succeeded)
                continue;
            staged[result.Value.Id] = new(
                result.Value,
                AudioTranscodeStageState.Failed,
                null,
                null,
                0,
                "transcode.stage-failed",
                result.Error?.Message);
        }
        foreach (AudioTranscodePlanItem blocked in
                 plan.Items.Where(item => !item.CanApply))
        {
            staged[blocked.Id] = new(
                blocked,
                AudioTranscodeStageState.Failed,
                null,
                null,
                0,
                "transcode.preview-blocked",
                blocked.Issues.FirstOrDefault(
                    issue =>
                        issue.Severity ==
                        OperationIssueSeverity.Blocker)
                    ?.Message);
        }
        return new(
            plan,
            [
                .. plan.Items.Select(item =>
                    staged[item.Id]),
            ])
        {
            OwnedStageDirectories =
                ownedStageDirectories,
        };
    }

    public Task<AudioTranscodeApplyResult> ApplyAsync(
        AudioTranscodeStageResult stage,
        IReadOnlySet<Guid>? readyItemIds = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default) =>
        ApplyBatchAsync(
            [stage],
            readyItemIds,
            progress,
            ct);

    public Task<AudioTranscodeApplyResult> ApplyBatchAsync(
        IReadOnlyList<AudioTranscodeStageResult> stages,
        IReadOnlySet<Guid>? readyItemIds = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default) =>
        ApplyReviewedBatchAsync(
            stages,
            [],
            readyItemIds,
            progress,
            ct);

    public async Task<AudioTranscodeApplyResult>
        ApplyReviewedBatchAsync(
            IReadOnlyList<AudioTranscodeStageResult> stages,
            IReadOnlyList<FileMutationPlan>
                additionalParticipants,
            IReadOnlySet<Guid>? readyItemIds = null,
            IProgress<OperationProgress>? progress = null,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentNullException.ThrowIfNull(
            additionalParticipants);
        (
            AudioTranscodeStageResult Stage,
            AudioTranscodeStagedItem Item)[] ready =
        [
            .. stages.SelectMany(stage =>
                stage.ReadyItems
                    .Where(item =>
                        readyItemIds is null ||
                        readyItemIds.Contains(
                            item.PlanItem.Id))
                    .Select(item => (stage, item))),
        ];
        if (ready.Length == 0)
            return new(0, [], [], [], []);

        ValidateApplyCapacity(
            ready,
            recoverySpace ??
            SystemRecoverySpaceProbe.Instance);

        var issues = new List<OperationIssue>();
        var participantPlans =
            new List<FileMutationPlan>(
                additionalParticipants);
        foreach (var volume in
                 ready.GroupBy(
                     row =>
                         VolumeKey(
                             row.Item.PlanItem.DestinationPath),
                     PathComparer))
        {
            ct.ThrowIfCancellationRequested();
            (AudioTranscodeStageResult Stage,
                AudioTranscodeStagedItem Item)[] volumeItems =
            [
                .. volume.OrderBy(row =>
                    row.Item.PlanItem.DestinationPath,
                    PathComparer),
            ];
            string anchor =
                Path.GetDirectoryName(
                    volumeItems[0]
                        .Item
                        .PlanItem.DestinationPath)!;
            string recoveryRoot = Path.Combine(
                RecoveryContainer(anchor),
                DateTime.UtcNow.ToString(
                    "yyyyMMdd-HHmmssfff",
                    CultureInfo.InvariantCulture) +
                "-" +
                Guid.NewGuid().ToString("N"));
            var actions = new List<FileMutationAction>();
            foreach (var row in volumeItems)
            {
                AudioTranscodeStagedItem item = row.Item;
                string stagedPath = item.StagedPath!;
                AudioTranscodePlanItem planItem =
                    item.PlanItem;
                ValidateSnapshot(
                    planItem.SourcePath,
                    planItem.SourceSnapshot,
                    "source");
                string currentHash =
                    await Sha256Async(
                        planItem.SourcePath,
                        ct).ConfigureAwait(false);
                if (!currentHash.Equals(
                        planItem.SourceSha256,
                        StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"The source changed after staging: " +
                        $"{planItem.SourcePath}");
                ValidateSnapshot(
                    planItem.DestinationPath,
                    planItem.DestinationSnapshot,
                    "destination");
                foreach (AudioTranscodePlannedSidecar sidecar in
                         SidecarsOrEmpty(planItem.Sidecars))
                    ValidateSnapshot(
                        sidecar.DestinationPath,
                        sidecar.DestinationSnapshot,
                        "sidecar destination");
                OperationPathSnapshot stagedSnapshot =
                    Capture(stagedPath);
                bool replace =
                    row.Stage.Plan.Request.Destination.Mode ==
                    AudioTranscodeDestinationMode.ReplaceOriginal;
                bool samePath = PathComparer.Equals(
                    planItem.SourcePath,
                    planItem.DestinationPath);
                if (replace && samePath)
                {
                    actions.Add(new(
                        FileMutationKind.Replace,
                        stagedPath,
                        planItem.SourcePath,
                        stagedSnapshot,
                        planItem.SourceSnapshot,
                        CatalogPolicy:
                            FileMutationCatalogPolicy.MirrorSource,
                        CatalogReferencePath:
                            planItem.SourcePath));
                }
                else
                {
                    actions.Add(new(
                        FileMutationKind.Copy,
                        stagedPath,
                        planItem.DestinationPath,
                        stagedSnapshot,
                        planItem.DestinationSnapshot,
                        CatalogPolicy:
                            FileMutationCatalogPolicy.MirrorSource,
                        CatalogReferencePath:
                            planItem.SourcePath));
                    if (replace)
                    {
                        string quarantine = Path.Combine(
                            recoveryRoot,
                            "sources",
                            Convert.ToHexString(
                                SHA256.HashData(
                                    System.Text.Encoding.UTF8
                                        .GetBytes(
                                            planItem.SourcePath))),
                            Path.GetFileName(
                                planItem.SourcePath));
                        actions.Add(new(
                            FileMutationKind.Quarantine,
                            planItem.SourcePath,
                            quarantine,
                            planItem.SourceSnapshot,
                            Capture(quarantine)));
                    }
                }
                foreach (AudioTranscodeStagedSidecar sidecar in
                         SidecarsOrEmpty(item.Sidecars))
                {
                    actions.Add(new(
                        FileMutationKind.Copy,
                        sidecar.StagedPath,
                        sidecar.DestinationPath,
                        Capture(sidecar.StagedPath),
                        sidecar.DestinationSnapshot,
                        CatalogPolicy:
                            FileMutationCatalogPolicy.None));
                }
            }

            var mutationPlan = new FileMutationPlan(
                "MusicLibraryManager.Transcode",
                anchor,
                recoveryRoot,
                actions,
                [],
                DateTimeOffset.UtcNow,
                RetainRecovery: true,
                PolicyFingerprint:
                    settings.GetSnapshot()
                        .Configuration?
                        .PolicySnapshot.Fingerprint,
                LibraryId:
                    settings.GetSnapshot()
                        .Configuration?
                        .LibraryId,
                RecoveryPayloadPolicy:
                    RecoveryPayloadPolicy.FullOriginal);
            participantPlans.Add(mutationPlan);
        }

        ImmutableArray<AudioTranscodeRequest> redoRequests =
        [
            .. stages
                .Select(stage =>
                {
                    ImmutableArray<string> appliedSources =
                    [
                        .. ready
                            .Where(row =>
                                ReferenceEquals(
                                    row.Stage,
                                    stage))
                            .Select(row =>
                                row.Item.PlanItem.SourcePath),
                    ];
                    return stage.Plan.Request with
                    {
                        SourcePaths = appliedSources,
                    };
                })
                .Where(request =>
                    request.SourcePaths.Length > 0),
        ];
        string[] reviewedSources =
        [
            .. ready.Select(row =>
                    row.Item.PlanItem.SourcePath)
                .Concat(
                    additionalParticipants
                        .SelectMany(participant =>
                            participant.Actions)
                        .Where(action =>
                            action.Kind is
                                FileMutationKind.Replace or
                                FileMutationKind
                                    .ReplaceGenerated)
                        .Select(action =>
                            action.DestinationPath))
                .Distinct(PathComparer),
        ];
        int changedFiles = ready
            .Select(row =>
                row.Item.PlanItem.DestinationPath)
            .Concat(
                additionalParticipants.SelectMany(
                    participant =>
                        participant.Actions)
                    .Where(action =>
                        action.Kind is
                            FileMutationKind.Copy or
                            FileMutationKind.Replace or
                            FileMutationKind
                                .ReplaceGenerated or
                            FileMutationKind.Write)
                    .Select(action =>
                        action.DestinationPath))
            .Distinct(PathComparer)
            .Count();
        ImmutableArray<string> destinationPaths =
        [
            .. ready.Select(row =>
                    row.Item.PlanItem.DestinationPath)
                .Concat(ready.SelectMany(row =>
                    SidecarsOrEmpty(row.Item.Sidecars)
                        .Select(sidecar =>
                            sidecar.DestinationPath))),
        ];

        // Membership is captured before the filesystem commit. An active
        // index may delay or cancel this read, but it can no longer turn a
        // durable transcode into an apparent failed/cancelled operation.
        (
            ImmutableArray<string> indexedSources,
            IReadOnlyList<OperationIssue> membershipIssues) =
            await CaptureInternalCatalogMembershipAsync(
                    ready,
                    ct)
                .ConfigureAwait(false);
        issues.AddRange(membershipIssues);

        ReviewedChangeBatchPlan batch =
            reviewedChanges.CreatePlan(participantPlans);
        ReviewedChangeBatchResult batchResult =
            await reviewedChanges.ApplyAsync(
                batch,
                progress,
                ct).ConfigureAwait(false);
        foreach (FileMutationSummary summary in
                 batchResult.ParticipantResults)
            issues.AddRange(summary.Issues);

        // Record semantic history immediately after the durable batch commit
        // and before any cache gate or staging cleanup can delay the caller.
        try
        {
            history.Record(new(
                Guid.NewGuid(),
                ReviewedChangeKindIds.AudioTranscode,
                DateTimeOffset.UtcNow,
                batchResult.JournalPaths,
                [.. reviewedSources],
                destinationPaths,
                batchResult.CoordinatorManifestPath,
                redoRequests[0],
                redoRequests,
                indexedSources));
        }
        catch (Exception error)
        {
            issues.Add(new(
                "transcode.history-record-failed",
                OperationIssueSeverity.Warning,
                "The transcode committed, but its Undo history could not " +
                "be recorded: " + error.Message));
        }

        PostCommitReconciliationHandle? reconciliation =
            reindex is null ||
            indexedSources.IsDefaultOrEmpty
                ? null
                : PostCommitReconciliationQueue.Shared.Enqueue(
                    () => RefreshInternalCatalogAsync(
                        ready,
                        indexedSources),
                    "transcode.catalog-refresh-failed",
                    "The committed transcode catalog refresh failed");

        foreach (var row in ready)
        {
            TryDelete(row.Item.StagedPath);
            foreach (AudioTranscodeStagedSidecar sidecar in
                     SidecarsOrEmpty(row.Item.Sidecars))
                TryDelete(sidecar.StagedPath);
        }
        foreach (AudioTranscodeStageResult stage in stages)
            CleanupStageDirectories(stage);

        return new(
            changedFiles,
            batchResult.JournalPaths,
            [.. reviewedSources],
            destinationPaths,
            [.. issues])
        {
            PostCommitReconciliation = reconciliation,
        };
    }

    public Task DiscardStageAsync(
        AudioTranscodeStageResult stage,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stage);
        foreach (AudioTranscodeStagedItem item in
                 stage.Items)
        {
            ct.ThrowIfCancellationRequested();
            TryDelete(item.StagedPath);
            foreach (AudioTranscodeStagedSidecar sidecar in
                     SidecarsOrEmpty(item.Sidecars))
                TryDelete(sidecar.StagedPath);
        }
        CleanupStageDirectories(stage);
        return Task.CompletedTask;
    }

    private static AudioEncoderDescriptor? ResolveEncoder(
        AudioTranscodeCapabilitySnapshot snapshot,
        AudioTranscodeFormatDescriptor format,
        string encoderId)
    {
        string? resolved =
            encoderId.Equals(
                AudioTranscodeEncoderIds.Automatic,
                StringComparison.Ordinal)
                ? format.EncoderIds.FirstOrDefault()
                : format.EncoderIds.Contains(
                    encoderId,
                    StringComparer.Ordinal)
                    ? encoderId
                    : null;
        return resolved is null
            ? null
            : snapshot.FindEncoder(resolved);
    }

    private static void ValidateSettings(
        AudioTranscodeSettings settings,
        AudioEncoderDescriptor encoder,
        List<OperationIssue> issues)
    {
        AudioRateControlDescriptor? rate =
            encoder.RateControls.FirstOrDefault(item =>
                item.Mode == settings.RateMode);
        if (rate is null)
        {
            issues.Add(new(
                "transcode.rate-mode-unavailable",
                OperationIssueSeverity.Blocker,
                "The selected encoder does not support this rate mode."));
            return;
        }
        if (settings.BitrateKbps is { } bitrate &&
            (rate.MinimumBitrateKbps is { } minimumBitrate &&
             bitrate < minimumBitrate ||
             rate.MaximumBitrateKbps is { } maximumBitrate &&
             bitrate > maximumBitrate))
            issues.Add(new(
                "transcode.bitrate-out-of-range",
                OperationIssueSeverity.Blocker,
                "The selected bitrate is outside the encoder's range."));
        if (settings.Quality is { } quality &&
            (rate.MinimumQuality is { } minimumQuality &&
             quality < minimumQuality ||
             rate.MaximumQuality is { } maximumQuality &&
             quality > maximumQuality))
            issues.Add(new(
                "transcode.quality-out-of-range",
                OperationIssueSeverity.Blocker,
                "The selected quality is outside the encoder's range."));
        if (settings.BitsPerSample is { } bits &&
            !encoder.SupportedBitDepths.IsDefaultOrEmpty &&
            !encoder.SupportedBitDepths.Contains(bits))
            issues.Add(new(
                "transcode.bit-depth-unavailable",
                OperationIssueSeverity.Blocker,
                "The selected encoder does not support this bit depth."));
        if (settings.CreateCorrectionFile &&
            (!encoder.SupportsCorrectionFile ||
             !rate.SupportsCorrectionFile))
            issues.Add(new(
                "transcode.correction-unavailable",
                OperationIssueSeverity.Blocker,
                "The selected encoder and rate mode do not support a correction file."));
    }

    internal static void AddPreviewCapacityIssues(
        List<AudioTranscodePlanItem> items,
        AudioTranscodeFormatDescriptor format,
        IRecoverySpaceProbe probe)
    {
        foreach (IGrouping<string, AudioTranscodePlanItem>
                 volume in items
                     .Where(item => item.CanApply)
                     .GroupBy(
                         item => VolumeKey(
                             item.DestinationPath),
                         PathComparer))
        {
            try
            {
                long required = 1024 * 1024;
                foreach (AudioTranscodePlanItem item in volume)
                {
                    long estimate = format.Lossless
                        ? checked(
                            item.SourceSnapshot.Length +
                            item.SourceSnapshot.Length / 4)
                        : Math.Max(
                            1024 * 1024,
                            item.SourceSnapshot.Length / 2);
                    required = checked(
                        required +
                        estimate * 2);
                }
                long? available =
                    probe.GetAvailableFreeSpace(
                        volume.Key);
                if (available is null ||
                    available >= required)
                    continue;
                foreach (AudioTranscodePlanItem item in
                         volume.ToArray())
                {
                    int index = items.FindIndex(candidate =>
                        candidate.Id == item.Id);
                    items[index] = item with
                    {
                        Issues = item.Issues.Add(new(
                            "transcode.recovery-space",
                            OperationIssueSeverity.Blocker,
                            $"At least {required:N0} free bytes are " +
                            "estimated for transcode staging.",
                            item.DestinationPath)),
                    };
                }
            }
            catch (OverflowException)
            {
                foreach (AudioTranscodePlanItem item in
                         volume.ToArray())
                {
                    int index = items.FindIndex(candidate =>
                        candidate.Id == item.Id);
                    items[index] = item with
                    {
                        Issues = item.Issues.Add(new(
                            "transcode.recovery-space",
                            OperationIssueSeverity.Blocker,
                            "The transcode staging estimate exceeds " +
                            "the supported capacity range.",
                            item.DestinationPath)),
                    };
                }
            }
        }
    }

    internal static void AddInternalCatalogIssues(
        LibraryConfiguration? configuration,
        string destinationPath,
        ICollection<OperationIssue> issues)
    {
        if (configuration is null)
            return;
        LibraryIndexLocation[] roots =
            configuration.IndexLocations.ToArray();
        if (roots.Length == 0 ||
            LibraryRootPermissionPolicy.MostSpecific(
                destinationPath,
                roots) is not null)
            return;
        issues.Add(new(
            "transcode.output-session-only",
            OperationIssueSeverity.Warning,
            "The output is outside the configured index roots " +
            "and will remain available only in this session.",
            destinationPath));
    }

    private async Task<(
        ImmutableArray<string> IndexedSources,
        IReadOnlyList<OperationIssue> Issues)>
        CaptureInternalCatalogMembershipAsync(
        IReadOnlyList<(
            AudioTranscodeStageResult Stage,
            AudioTranscodeStagedItem Item)> ready,
        CancellationToken ct)
    {
        if (reindex is null)
            return ([], []);

        var indexedSources =
            new HashSet<string>(PathComparer);
        var issues = new List<OperationIssue>();
        foreach (string source in ready
                     .Select(row =>
                         row.Item.PlanItem.SourcePath)
                     .Distinct(PathComparer)
                     .OrderBy(path => path, PathComparer))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await reindex.IsIndexedFileAsync(
                            source,
                            ct)
                        .ConfigureAwait(false))
                    indexedSources.Add(source);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                issues.Add(new(
                    "transcode.catalog-membership-failed",
                    OperationIssueSeverity.Warning,
                    "The transcode could not determine whether the source " +
                    "belongs to the loaded library. It will remain " +
                    "session-only: " + error.Message,
                    source));
            }
        }
        return (
            [.. indexedSources.OrderBy(
                path => path,
                PathComparer)],
            issues);
    }

    private async Task<IReadOnlyList<OperationIssue>>
        RefreshInternalCatalogAsync(
        IReadOnlyList<(
            AudioTranscodeStageResult Stage,
            AudioTranscodeStagedItem Item)> ready,
        ImmutableArray<string> indexedSources)
    {
        if (reindex is null ||
            indexedSources.IsDefaultOrEmpty)
            return [];

        HashSet<string> tracked =
            indexedSources.ToHashSet(PathComparer);
        var issues = new List<OperationIssue>();
        var reindexed =
            new Dictionary<string, bool>(PathComparer);
        var removed = new HashSet<string>(PathComparer);
        foreach (var row in ready
                     .Where(row =>
                         tracked.Contains(
                             row.Item.PlanItem.SourcePath))
                     .OrderBy(row =>
                         row.Item.PlanItem.DestinationPath,
                         PathComparer))
        {
            string source =
                row.Item.PlanItem.SourcePath;
            string destination =
                row.Item.PlanItem.DestinationPath;
            bool destinationReady;
            if (!reindexed.TryGetValue(
                    destination,
                    out destinationReady))
            {
                try
                {
                    await reindex.ReindexFileAsync(
                            destination,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    reindexed[destination] = true;
                    destinationReady = true;
                }
                catch (Exception error)
                {
                    reindexed[destination] = false;
                    destinationReady = false;
                    issues.Add(new(
                        "transcode.catalog-refresh-failed",
                        OperationIssueSeverity.Warning,
                        "The committed transcode output could not be " +
                        "refreshed in the library catalog: " +
                        error.Message,
                        destination));
                }
            }

            bool replace =
                row.Stage.Plan.Request.Destination.Mode ==
                AudioTranscodeDestinationMode.ReplaceOriginal;
            if (!destinationReady ||
                !replace ||
                PathComparer.Equals(source, destination) ||
                !removed.Add(source))
                continue;
            try
            {
                await reindex.RemoveIndexedFileAsync(
                        source,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception error)
            {
                issues.Add(new(
                    "transcode.catalog-refresh-failed",
                    OperationIssueSeverity.Warning,
                    "The replaced transcode source could not be removed " +
                    "from the library catalog: " + error.Message,
                    source));
            }
        }
        return issues;
    }

    private static void ValidateApplyCapacity(
        IReadOnlyList<(
            AudioTranscodeStageResult Stage,
            AudioTranscodeStagedItem Item)> ready,
        IRecoverySpaceProbe probe)
    {
        foreach (var volume in ready.GroupBy(
                     row => VolumeKey(
                         row.Item.PlanItem.DestinationPath),
                     PathComparer))
        {
            long required = checked(
                1024 * 1024 +
                volume.Sum(row =>
                    checked(
                        row.Item.OutputLength +
                        SidecarsOrEmpty(
                                row.Item.Sidecars)
                            .Sum(sidecar =>
                                sidecar.Length))));
            long? available =
                probe.GetAvailableFreeSpace(volume.Key);
            if (available is not null &&
                available < required)
                throw new IOException(
                    $"The destination volume has " +
                    $"{available:N0} free bytes; " +
                    $"{required:N0} are required to commit " +
                    "the staged transcode.");
        }
    }

    private static string ResolveDestination(
        AudioTranscodeRequest request,
        AudioTranscodeFormatDescriptor format,
        string source,
        string? commonDirectory,
        List<OperationIssue> issues)
    {
        string directory;
        switch (request.Destination.Mode)
        {
            case AudioTranscodeDestinationMode.Alongside:
            case AudioTranscodeDestinationMode.ReplaceOriginal:
                directory = Path.GetDirectoryName(source)!;
                break;
            case AudioTranscodeDestinationMode.ChosenFolder:
                if (string.IsNullOrWhiteSpace(
                        request.Destination.RootDirectory))
                {
                    issues.Add(new(
                        "transcode.destination-required",
                        OperationIssueSeverity.Blocker,
                        "Choose an output folder.",
                        source));
                    directory = Path.GetDirectoryName(source)!;
                    break;
                }
                directory = Path.GetFullPath(
                    request.Destination.RootDirectory);
                if (request.Destination.PreserveSourceLayout &&
                    commonDirectory is not null)
                {
                    string relativeDirectory =
                        Path.GetRelativePath(
                            commonDirectory,
                            Path.GetDirectoryName(source)!);
                    if (!relativeDirectory.Equals(
                            ".",
                            StringComparison.Ordinal))
                        directory = Path.Combine(
                            directory,
                            relativeDirectory);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        string template =
            string.IsNullOrWhiteSpace(
                request.Destination.FileNameTemplate)
                ? "{Name}{Extension}"
                : request.Destination.FileNameTemplate;
        string name = Path.GetFileNameWithoutExtension(
            source);
        string encoder = request.Settings.EncoderId.Equals(
                AudioTranscodeEncoderIds.Automatic,
                StringComparison.Ordinal)
            ? "auto"
            : request.Settings.EncoderId.Split(':').Last();
        string expanded = template
            .Replace(
                "{Name}",
                name,
                StringComparison.Ordinal)
            .Replace(
                "{Extension}",
                format.Extension,
                StringComparison.Ordinal)
            .Replace(
                "{Codec}",
                format.Codec,
                StringComparison.Ordinal)
            .Replace(
                "{Encoder}",
                encoder,
                StringComparison.Ordinal)
            .Replace(
                "{SampleRate}",
                request.Settings.SampleRateHz?
                    .ToString(CultureInfo.InvariantCulture) ??
                "source",
                StringComparison.Ordinal)
            .Replace(
                "{BitsPerSample}",
                request.Settings.BitsPerSample?
                    .ToString(CultureInfo.InvariantCulture) ??
                "source",
                StringComparison.Ordinal);
        if (Path.GetExtension(expanded).Length == 0)
            expanded += format.Extension;
        if (expanded.IndexOfAny(
                Path.GetInvalidFileNameChars()) >= 0 ||
            expanded.Contains(
                Path.DirectorySeparatorChar) ||
            expanded.Contains(
                Path.AltDirectorySeparatorChar))
        {
            issues.Add(new(
                "transcode.filename-invalid",
                OperationIssueSeverity.Blocker,
                "The output filename template produced an invalid name.",
                source));
            expanded =
                name + format.Extension;
        }
        string destination = Path.Combine(
            directory,
            expanded);
        if (request.Destination.Mode !=
                AudioTranscodeDestinationMode.ReplaceOriginal &&
            PathComparer.Equals(
                source,
                destination))
            destination = Path.Combine(
                directory,
                name + " (transcoded)" +
                format.Extension);
        return Path.GetFullPath(destination);
    }

    private static string? PreferredLayoutRoot(
        LibraryConfiguration? configuration,
        string source,
        string? commonDirectory)
    {
        string sourceDirectory =
            Path.GetDirectoryName(source)!;
        string? configuredRoot = configuration?
            .IndexLocations
            .Select(location => location.Target)
            .Where(path =>
                !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(root =>
                IsWithin(sourceDirectory, root))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault();
        return configuredRoot ?? commonDirectory;
    }

    internal static string ResolveCollision(
        string source,
        string destination,
        AudioTranscodeCollisionPolicy policy,
        HashSet<string> claimed,
        List<OperationIssue> issues,
        string? companionExtension = null)
    {
        bool mayReplaceSource =
            PathComparer.Equals(
                source,
                destination);
        if ((IsAvailable(destination, claimed) ||
             mayReplaceSource) &&
            CompanionAvailable(
                destination,
                companionExtension,
                claimed))
            return destination;
        if (policy == AudioTranscodeCollisionPolicy.Stop)
        {
            issues.Add(new(
                "transcode.destination-exists",
                OperationIssueSeverity.Blocker,
                "The output path already exists.",
                destination));
            return destination;
        }
        string directory =
            Path.GetDirectoryName(destination)!;
        string name =
            Path.GetFileNameWithoutExtension(destination);
        string extension =
            Path.GetExtension(destination);
        for (int suffix = 2; suffix < 100_000; suffix++)
        {
            string candidate = Path.Combine(
                directory,
                $"{name} ({suffix}){extension}");
            if (IsAvailable(candidate, claimed) &&
                CompanionAvailable(
                    candidate,
                    companionExtension,
                    claimed))
                return candidate;
        }
        issues.Add(new(
            "transcode.destination-collision-exhausted",
            OperationIssueSeverity.Blocker,
            "A unique output filename could not be generated.",
            destination));
        return destination;
    }

    private static bool IsAvailable(
        string path,
        HashSet<string> claimed) =>
        !File.Exists(path) &&
        !Directory.Exists(path) &&
        !claimed.Contains(path);

    private static ImmutableArray<T> SidecarsOrEmpty<T>(
        ImmutableArray<T> values) =>
        values.IsDefault ? [] : values;

    private static bool CompanionAvailable(
        string destination,
        string? companionExtension,
        HashSet<string> claimed) =>
        companionExtension is null ||
        IsAvailable(
            Path.ChangeExtension(
                destination,
                companionExtension),
            claimed);

    internal static string? CorrectionSidecarExtension(
        AudioTranscodeFormatDescriptor format,
        AudioTranscodeSettings settings)
    {
        if (!settings.CreateCorrectionFile)
            return null;
        return format.Id switch
        {
            AudioTranscodeFormatIds.WavPack => ".wvc",
            AudioTranscodeFormatIds.OptimFrogDualStream => ".ofc",
            _ => null,
        };
    }

    private static async Task<
        ImmutableArray<AudioTranscodeStagedSidecar>>
        CaptureStagedSidecarsAsync(
            AudioTranscodePlanItem item,
            string stagedPath,
            CancellationToken ct)
    {
        if (item.Sidecars.IsDefaultOrEmpty)
            return [];
        var sidecars =
            ImmutableArray.CreateBuilder<
                AudioTranscodeStagedSidecar>(
                item.Sidecars.Length);
        foreach (AudioTranscodePlannedSidecar planned in
                 item.Sidecars)
        {
            string extension =
                Path.GetExtension(planned.DestinationPath);
            string stagedSidecar =
                Path.ChangeExtension(
                    stagedPath,
                    extension);
            if (!File.Exists(stagedSidecar) ||
                new FileInfo(stagedSidecar).Length == 0)
                throw new InvalidDataException(
                    "The encoder did not produce the requested " +
                    "correction file.");
            sidecars.Add(new(
                stagedSidecar,
                planned.DestinationPath,
                planned.DestinationSnapshot,
                await Sha256Async(
                    stagedSidecar,
                    ct).ConfigureAwait(false),
                new FileInfo(stagedSidecar).Length));
        }
        return sidecars.ToImmutable();
    }

    private async Task ValidateSourceLayoutAsync(
        string sourcePath,
        bool replacingSource,
        ICollection<OperationIssue> issues,
        CancellationToken ct)
    {
        try
        {
            AudioSourceLayout layout;
            if (sourceLayout is not null)
            {
                layout = await sourceLayout.InspectAsync(
                        sourcePath,
                        ct)
                    .ConfigureAwait(false);
            }
            else
            {
                int audioStreams = MediaFile.GetFile(
                        sourcePath,
                        readOnly: true)
                    .Codecs.Count();
                layout = new(
                    audioStreams,
                    0,
                    audioStreams > 0 ? 1 : 0);
            }

            AddSourceLayoutIssues(
                layout,
                replacingSource,
                sourcePath,
                issues);
        }
        catch (OperationCanceledException)
            when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            issues.Add(new(
                "transcode.source-inspection-failed",
                replacingSource
                    ? OperationIssueSeverity.Blocker
                    : OperationIssueSeverity.Warning,
                "The source audio layout could not be inspected: " +
                error.Message,
                sourcePath));
        }
    }

    internal static void AddSourceLayoutIssues(
        AudioSourceLayout layout,
        bool replacingSource,
        string sourcePath,
        ICollection<OperationIssue> issues)
    {
        bool hasAdditionalStreams =
            layout.AudioStreamCount != 1 ||
            layout.AudioProgramCount != 1 ||
            layout.NonAudioStreamCount > 0;
        if (hasAdditionalStreams &&
            replacingSource)
            issues.Add(new(
                "transcode.replace-multiple-audio-programs",
                OperationIssueSeverity.Blocker,
                "Replacing a source requires exactly one audio " +
                "stream and no additional streams.",
                sourcePath));
        else if (hasAdditionalStreams)
            issues.Add(new(
                "transcode.separate-primary-audio",
                OperationIssueSeverity.Warning,
                "Only the primary audio stream will be written " +
                "to the separate output.",
                sourcePath));
    }

    private static void ValidateOutput(
        string sourcePath,
        string path,
        AudioTranscodeFormatDescriptor format,
        AudioTranscodeSettings settings)
    {
        if (!File.Exists(path) ||
            new FileInfo(path).Length == 0)
            throw new InvalidDataException(
                "The encoder produced no output.");
        IMediaFile output = MediaFile.GetFile(
            path,
            readOnly: true);
        ICodecProvider codec =
            output.Codecs.FirstOrDefault() ??
            throw new InvalidDataException(
                "The generated file has no audio stream.");
        ICodecProvider sourceCodec =
            MediaFile.GetFile(
                    sourcePath,
                    readOnly: true)
                .Codecs.FirstOrDefault() ??
            throw new InvalidDataException(
                "The source file has no audio stream.");
        if (format.Lossless &&
            codec.CodecType != CodecType.Lossless)
            throw new InvalidDataException(
                "The generated file is not lossless.");
        if (!format.Lossless &&
            codec.CodecType != CodecType.Lossy)
            throw new InvalidDataException(
                "The generated file is not lossy.");
        if (settings.SampleRateHz is { } rate &&
            codec.Samplerate != rate)
            throw new InvalidDataException(
                "The generated file has an unexpected sample rate.");
        int? expectedBits =
            AudioTranscodeAdapter
                .EffectiveIntegerConversionBitDepth(
                    settings,
                    sourcePath);
        if (format.Lossless &&
            expectedBits is { } bits &&
            codec.BitsPerSample != bits)
            throw new InvalidDataException(
                "The generated file has an unexpected bit depth.");
        if (codec.Channels != sourceCodec.Channels)
            throw new InvalidDataException(
                "The generated file has an unexpected channel count.");
        long durationDifference = Math.Abs(
            (long)codec.DurationInFrames -
            sourceCodec.DurationInFrames);
        if (durationDifference > 75)
            throw new InvalidDataException(
                "The generated file has an unexpected duration.");
    }

    private static async Task<string> Sha256Async(
        string path,
        CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(
            stream,
            ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private async Task VerifyDecodedAsync(
        string ffmpeg,
        string firstPath,
        string secondPath,
        string description,
        CancellationToken ct)
    {
        AnalysisReport decoded =
            await decodedVerification.VerifyAsync(
                    ffmpeg,
                    [
                        new(
                            firstPath,
                            secondPath,
                            description),
                    ],
                    ct: ct)
                .ConfigureAwait(false);
        if (decoded.Findings.Count > 0)
            throw new InvalidDataException(
                decoded.Findings[0].Description);
    }

    private static TimeSpan SourceDuration(
        string path)
    {
        try
        {
            var codec = MediaFile.GetFile(
                    path,
                    readOnly: true)
                .Codecs
                .FirstOrDefault();
            return codec is null
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(
                    codec.DurationInFrames / 75d);
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    private static string StagePath(
        Guid planId,
        AudioTranscodePlanItem item) =>
        Path.Combine(
            Path.GetDirectoryName(
                item.DestinationPath)!,
            ".MusicLibraryManager-staging",
            "transcode",
            planId.ToString("N"),
            item.Id.ToString("N") +
            Path.GetExtension(
                item.DestinationPath));

    private static void CleanupStageDirectories(
        AudioTranscodeStageResult stage)
    {
        CleanupStageDirectories(
            stage.OwnedStageDirectories
                .Concat(
                    stage.Items.Select(item =>
                        item.StagedPath is null
                            ? null
                            : Path.GetDirectoryName(
                                item.StagedPath)))
                .Where(directory =>
                    directory is not null)
                .Select(directory =>
                    directory!)
                .Distinct(PathComparer));
    }

    private static void CleanupStageDirectories(
        IEnumerable<string> directories)
    {
        foreach (string directory in directories
                     .Distinct(PathComparer))
        {
            try
            {
                if (Directory.Exists(directory) &&
                    !Directory.EnumerateFileSystemEntries(
                            directory)
                        .Any())
                    Directory.Delete(directory);
            }
            catch
            {
            }
        }
    }

    private static void TryDelete(string? path)
    {
        try
        {
            if (path is not null &&
                File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static OperationPathSnapshot Capture(
        string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            var file = new FileInfo(fullPath);
            return new(
                true,
                false,
                file.Length,
                file.LastWriteTimeUtc)
            {
                Path = fullPath,
            };
        }
        if (Directory.Exists(fullPath))
        {
            var directory = new DirectoryInfo(fullPath);
            return new(
                true,
                true,
                0,
                directory.LastWriteTimeUtc)
            {
                Path = fullPath,
            };
        }
        return OperationPathSnapshot.Missing(
            fullPath);
    }

    private static void ValidateSnapshot(
        string path,
        OperationPathSnapshot expected,
        string role)
    {
        OperationPathSnapshot actual = Capture(path);
        if (expected.Exists != actual.Exists ||
            expected.IsDirectory != actual.IsDirectory ||
            expected.Exists &&
            (expected.Length != actual.Length ||
             expected.LastWriteTimeUtc !=
             actual.LastWriteTimeUtc))
            throw new InvalidOperationException(
                $"The reviewed {role} changed: {path}");
    }

    internal static string? CommonDirectory(
        IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return null;
        string common =
            Path.GetDirectoryName(paths[0])!;
        string root =
            Path.GetPathRoot(common) ?? "";
        foreach (string path in paths.Skip(1))
        {
            string directory =
                Path.GetDirectoryName(path)!;
            if (!PathComparer.Equals(
                    Path.GetPathRoot(directory),
                    root))
                return null;
            while (!IsWithin(directory, common))
                common =
                    Path.GetDirectoryName(common) ??
                    root;
        }
        return Path.TrimEndingDirectorySeparator(
            common);
    }

    private static bool IsWithin(
        string path,
        string root)
    {
        string normalizedPath =
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(path));
        string normalizedRoot =
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(root));
        return PathComparer.Equals(
                   normalizedPath,
                   normalizedRoot) ||
               normalizedPath.StartsWith(
                   normalizedRoot +
                   Path.DirectorySeparatorChar,
                   PathComparison);
    }

    private static string VolumeKey(
        string path) =>
        Path.GetPathRoot(
            Path.GetFullPath(path)) ??
        Path.GetDirectoryName(
            Path.GetFullPath(path))!;

    private static string RecoveryContainer(
        string anchor)
    {
        string normalized =
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(anchor));
        string root =
            Path.TrimEndingDirectorySeparator(
                Path.GetPathRoot(normalized) ??
                normalized);
        return PathComparer.Equals(
                normalized,
                root)
            ? Path.Combine(
                normalized,
                ".MusicLibraryManager-recovery")
            : normalized +
              ".MusicLibraryManager-recovery";
    }

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private sealed class InlineProgress<T>(
        Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
