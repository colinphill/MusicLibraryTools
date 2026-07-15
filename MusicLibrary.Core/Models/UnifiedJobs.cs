namespace MusicLibrary.Core.Models;

public enum UnifiedJobApplyMode { ApplyFlag, ReadOnly }

public sealed record UnifiedJobDescriptor(
    string Id,
    string Name,
    string ExecutableName,
    string Description,
    UnifiedJobApplyMode ApplyMode,
    IReadOnlyList<string> PrefixArguments);

public sealed record UnifiedJobPlan(
    UnifiedJobDescriptor Job,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    long ExecutableLength,
    DateTime ExecutableLastWriteTimeUtc,
    int PreviewExitCode,
    string PreviewOutput,
    DateTimeOffset CreatedAtUtc)
{
    public bool CanApply => Job.ApplyMode == UnifiedJobApplyMode.ApplyFlag && PreviewExitCode == 0;
}

public sealed record UnifiedJobResult(int ExitCode, string Output, TimeSpan Elapsed)
{
    public bool Success => ExitCode == 0;
}
