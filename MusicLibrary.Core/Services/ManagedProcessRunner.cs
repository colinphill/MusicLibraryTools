using System.Diagnostics;
using System.Text;

namespace MusicLibrary.Core.Services;

public sealed record ManagedProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public interface IManagedProcessRunner
{
    Task<ManagedProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        IProgress<string>? standardOutputLines = null,
        CancellationToken ct = default);
}

public sealed class ManagedProcessRunner(
    int maximumCapturedCharacters = 65_536) : IManagedProcessRunner
{
    private readonly int _maximumCapturedCharacters =
        Math.Max(4_096, maximumCapturedCharacters);

    public async Task<ManagedProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        IProgress<string>? standardOutputLines = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory,
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = new Process
        {
            StartInfo = start,
        };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException(
                    $"Unable to start '{executable}'.");
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                $"Unable to start '{executable}': {error.Message}",
                error);
        }

        var stdout = new BoundedText(
            _maximumCapturedCharacters);
        var stderr = new BoundedText(
            _maximumCapturedCharacters);
        Task stdoutTask = DrainAsync(
            process.StandardOutput,
            stdout,
            standardOutputLines,
            ct);
        Task stderrTask = DrainAsync(
            process.StandardError,
            stderr,
            null,
            ct);
        try
        {
            await process.WaitForExitAsync(ct)
                .ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(
                    CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(
                        IgnoreCancellation(stdoutTask),
                        IgnoreCancellation(stderrTask))
                    .ConfigureAwait(false);
            }
            catch
            {
                // Preserve the caller's cancellation.
            }
            throw;
        }

        return new(
            process.ExitCode,
            stdout.ToString(),
            stderr.ToString());
    }

    private static async Task DrainAsync(
        StreamReader reader,
        BoundedText destination,
        IProgress<string>? lines,
        CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct)
                   .ConfigureAwait(false) is { } line)
        {
            destination.AppendLine(line);
            lines?.Report(line);
        }
    }

    private static async Task IgnoreCancellation(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private sealed class BoundedText(int maximum)
    {
        private readonly StringBuilder _builder = new();

        public void AppendLine(string value)
        {
            _builder.AppendLine(value);
            if (_builder.Length <= maximum)
                return;
            _builder.Remove(
                0,
                _builder.Length - maximum);
        }

        public override string ToString() =>
            _builder.ToString();
    }
}
