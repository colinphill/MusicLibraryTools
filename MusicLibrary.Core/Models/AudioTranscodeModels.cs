using System.Collections.Immutable;

namespace MusicLibrary.Core.Models;

public enum AudioTranscodeRateMode
{
    Lossless,
    ConstantBitrate,
    AverageBitrate,
    VariableQuality,
    ConstrainedVariableBitrate,
    HybridBitrate,
    HybridQuality,
}

public enum AudioTranscodeDestinationMode
{
    Alongside,
    ReplaceOriginal,
    ChosenFolder,
}

public enum AudioTranscodeCollisionPolicy
{
    Stop,
    Suffix,
}

public enum AudioEncoderThreadingMode
{
    SingleThreaded,
    ThreadCountControllable,
    InternallyThreaded,
}

public enum AudioTranscodeToolKind
{
    Ffmpeg,
    WavPack,
    OptimFrog,
    MonkeysAudio,
}

public enum AudioToolProbeState
{
    NotProbed,
    Ready,
    Unavailable,
    InvalidOutput,
}

public static class AudioTranscodeFormatIds
{
    public const string Flac = "flac.flac";
    public const string AlacM4a = "alac.m4a";
    public const string AacM4a = "aac.m4a";
    public const string AacAdts = "aac.adts";
    public const string Mp3 = "mp3.mp3";
    public const string OpusOgg = "opus.ogg";
    public const string VorbisOgg = "vorbis.ogg";
    public const string WavPack = "wavpack.wv";
    public const string PcmWave = "pcm.wav";
    public const string PcmRf64 = "pcm.rf64";
    public const string PcmAiff = "pcm.aiff";
    public const string TrueAudio = "tta.tta";
    public const string OptimFrog = "optimfrog.ofr";
    public const string OptimFrogDualStream = "optimfrog.ofs";
    public const string OptimFrogFloat = "optimfrog.off";
    public const string MonkeysAudio = "monkeysaudio.ape";
}

public static class AudioTranscodeEncoderIds
{
    public const string Automatic = "auto";

    public static string Ffmpeg(string encoder) =>
        $"ffmpeg:{encoder}";

    public const string WavPackCli = "wavpack:cli";
    public const string OptimFrogOfr = "optimfrog:ofr";
    public const string OptimFrogOfs = "optimfrog:ofs";
    public const string OptimFrogOff = "optimfrog:off";
    public const string MonkeysAudioMac = "monkeysaudio:mac";
}

public sealed record AudioRateControlDescriptor(
    AudioTranscodeRateMode Mode,
    int? MinimumBitrateKbps = null,
    int? MaximumBitrateKbps = null,
    double? MinimumQuality = null,
    double? MaximumQuality = null,
    bool HigherQualityValueIsBetter = true);

public sealed record AudioEncoderDescriptor(
    string Id,
    AudioTranscodeToolKind Tool,
    string ExecutableEncoder,
    AudioEncoderThreadingMode ThreadingMode,
    ImmutableArray<AudioRateControlDescriptor> RateControls,
    ImmutableArray<int> SupportedSampleRates,
    ImmutableArray<int> SupportedBitDepths,
    bool SupportsCorrectionFile = false,
    bool SupportsDsd = false);

public sealed record AudioTranscodeFormatDescriptor(
    string Id,
    string Codec,
    string Container,
    string Extension,
    bool Lossless,
    ImmutableArray<string> EncoderIds,
    bool SupportsMetadata = true,
    bool SupportsArtwork = true,
    bool SupportsDsd = false,
    bool RequiresFloatInput = false);

public sealed record AudioToolProbeResult(
    AudioTranscodeToolKind Tool,
    AudioToolProbeState State,
    string ConfiguredValue,
    string? ResolvedExecutable,
    string? Version,
    ImmutableHashSet<string> Encoders,
    ImmutableHashSet<string> Decoders,
    ImmutableHashSet<string> Muxers,
    ImmutableHashSet<string> Demuxers,
    string? ErrorCode = null,
    string? DiagnosticDetail = null)
{
    public static AudioToolProbeResult Unavailable(
        AudioTranscodeToolKind tool,
        string configuredValue,
        string errorCode,
        string? detail = null) =>
        new(
            tool,
            AudioToolProbeState.Unavailable,
            configuredValue,
            null,
            null,
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet<string>.Empty,
            errorCode,
            detail);
}

public sealed record AudioSourceLayout(
    int AudioStreamCount,
    int NonAudioStreamCount,
    int AudioProgramCount);

public sealed record AudioTranscodeCapabilitySnapshot(
    ImmutableArray<AudioToolProbeResult> Tools,
    ImmutableArray<AudioTranscodeFormatDescriptor> Formats,
    ImmutableArray<AudioEncoderDescriptor> Encoders,
    DateTimeOffset GeneratedAtUtc,
    long ConfigurationVersion)
{
    public AudioEncoderDescriptor? FindEncoder(string id) =>
        Encoders.FirstOrDefault(item =>
            item.Id.Equals(id, StringComparison.Ordinal));

    public AudioTranscodeFormatDescriptor? FindFormat(string id) =>
        Formats.FirstOrDefault(item =>
            item.Id.Equals(id, StringComparison.Ordinal));
}

public sealed record AudioTranscodeSettings(
    string FormatId,
    string EncoderId,
    AudioTranscodeRateMode RateMode,
    int? BitrateKbps = null,
    double? Quality = null,
    int? SampleRateHz = null,
    int? BitsPerSample = null,
    int CompressionEffort = 5,
    bool CreateCorrectionFile = false);

public sealed record AudioTranscodeDestinationSpec(
    AudioTranscodeDestinationMode Mode,
    string? RootDirectory,
    bool PreserveSourceLayout,
    string FileNameTemplate,
    AudioTranscodeCollisionPolicy CollisionPolicy);

public sealed record AudioTranscodePreset(
    Guid Id,
    string Name,
    AudioTranscodeSettings Settings,
    bool PreserveMetadata,
    bool PreserveArtwork,
    bool PreserveSourceLayout,
    string FileNameTemplate,
    AudioTranscodeCollisionPolicy CollisionPolicy,
    DateTimeOffset ModifiedAtUtc);

public sealed record AudioTranscodeRequest(
    ImmutableArray<string> SourcePaths,
    AudioTranscodeSettings Settings,
    AudioTranscodeDestinationSpec Destination,
    bool PreserveMetadata = true,
    bool PreserveArtwork = true);

public sealed record AudioTranscodePlanItem(
    Guid Id,
    string SourcePath,
    string DestinationPath,
    OperationPathSnapshot SourceSnapshot,
    OperationPathSnapshot DestinationSnapshot,
    string SourceSha256,
    AudioTranscodeSettings Settings,
    ImmutableArray<OperationIssue> Issues,
    ImmutableArray<AudioTranscodePlannedSidecar> Sidecars = default)
{
    public bool CanApply => Issues.All(issue =>
        issue.Severity != OperationIssueSeverity.Blocker);
}

public sealed record AudioTranscodePlannedSidecar(
    string DestinationPath,
    OperationPathSnapshot DestinationSnapshot);

public sealed record AudioTranscodePlan(
    Guid Id,
    AudioTranscodeRequest Request,
    ImmutableArray<AudioTranscodePlanItem> Items,
    ImmutableArray<OperationIssue> Issues,
    DateTimeOffset CreatedAtUtc,
    long ConfigurationVersion)
{
    public bool CanApply =>
        Items.Length > 0 &&
        Items.Any(item => item.CanApply) &&
        Issues.All(issue =>
            issue.Severity != OperationIssueSeverity.Blocker);
}

public enum AudioTranscodeStageState
{
    Ready,
    Failed,
    Cancelled,
}

public sealed record AudioTranscodeStagedItem(
    AudioTranscodePlanItem PlanItem,
    AudioTranscodeStageState State,
    string? StagedPath,
    string? OutputSha256,
    long OutputLength,
    string? ErrorCode = null,
    string? DiagnosticDetail = null,
    ImmutableArray<AudioTranscodeStagedSidecar> Sidecars = default);

public sealed record AudioTranscodeStagedSidecar(
    string StagedPath,
    string DestinationPath,
    OperationPathSnapshot DestinationSnapshot,
    string Sha256,
    long Length);

public sealed record AudioTranscodeStageResult(
    AudioTranscodePlan Plan,
    ImmutableArray<AudioTranscodeStagedItem> Items)
{
    public ImmutableArray<AudioTranscodeStagedItem> ReadyItems =>
        [.. Items.Where(item =>
            item.State == AudioTranscodeStageState.Ready)];

    public ImmutableArray<AudioTranscodeStagedItem> FailedItems =>
        [.. Items.Where(item =>
            item.State == AudioTranscodeStageState.Failed)];
}

public sealed record AudioTranscodeApplyResult(
    int ChangedFiles,
    ImmutableArray<string> JournalPaths,
    ImmutableArray<string> SourcePaths,
    ImmutableArray<string> DestinationPaths,
    ImmutableArray<OperationIssue> Issues);
