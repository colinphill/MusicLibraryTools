namespace MusicLibrary.Core.Models;

/// <summary>
/// Selects how a retained replacement can be reversed. The default deliberately preserves the
/// historical whole-original behavior for callers that have not explicitly opted in.
/// </summary>
public enum RecoveryPayloadPolicy
{
    FullOriginal = 0,
    AdaptiveReverseDelta = 1,
}

public enum RecoveryPayloadKind
{
    FullOriginal = 0,
    ReverseDelta = 1,
}

/// <summary>Storage retained by one completed mutation plan for later recovery.</summary>
public sealed record RecoveryStorageSummary(
    long OriginalBytes,
    long RetainedBytes,
    int FullOriginalCount,
    int ReverseDeltaCount)
{
    public long SavedBytes => Math.Max(0, OriginalBytes - RetainedBytes);

    public double SavingsFraction => OriginalBytes <= 0
        ? 0
        : (double)SavedBytes / OriginalBytes;

    public static RecoveryStorageSummary Empty { get; } = new(0, 0, 0, 0);

    public RecoveryStorageSummary Add(RecoveryStorageSummary other) => new(
        checked(OriginalBytes + other.OriginalBytes),
        checked(RetainedBytes + other.RetainedBytes),
        checked(FullOriginalCount + other.FullOriginalCount),
        checked(ReverseDeltaCount + other.ReverseDeltaCount));
}
