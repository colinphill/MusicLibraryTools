namespace MusicLibrary.Core.Services;

public interface IWavpackRunner
{
    Task PreflightAsync(
        string executable,
        CancellationToken ct = default);

    Task EncodeDsdAsync(
        string executable,
        string input,
        string output,
        CancellationToken ct = default);
}
