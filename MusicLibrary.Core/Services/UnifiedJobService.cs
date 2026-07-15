using System.Diagnostics;
using System.Text;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public interface IUnifiedJobService
{
    IReadOnlyList<UnifiedJobDescriptor> Catalog { get; }
    Task<UnifiedJobPlan> PreviewAsync(UnifiedJobDescriptor job, string executableDirectory,
        string arguments, IProgress<string>? progress = null, CancellationToken ct = default);
    Task<UnifiedJobResult> ApplyAsync(UnifiedJobPlan plan,
        IProgress<string>? progress = null, CancellationToken ct = default);
}

/// <summary>Shared dry-run/apply lifecycle for the workspace's existing operational tools.</summary>
public sealed class UnifiedJobService : IUnifiedJobService
{
    public IReadOnlyList<UnifiedJobDescriptor> Catalog { get; } =
    [
        new("playlist-sync", "Playlist sync", "CrossSyncPlaylists.exe",
            "Synchronize configured playlist files (dry-run by default).", UnifiedJobApplyMode.ApplyFlag, []),
        new("cross-library-sync", "Cross-library sync", "CrossSyncMusic.exe",
            "Synchronize a configured music target with removal limits.", UnifiedJobApplyMode.ApplyFlag, []),
        new("android-sync", "Android/device sync", "AndroidSync.exe",
            "Synchronize a source and device destination.", UnifiedJobApplyMode.ApplyFlag, []),
        new("car-card", "Car-card update", "UpdateCarCard.exe",
            "Update and rebalance the configured car-card target.", UnifiedJobApplyMode.ApplyFlag, []),
        new("smart-storage", "Smart-storage update", "UpdateSmartStorage.exe",
            "Update a managed portable storage destination.", UnifiedJobApplyMode.ApplyFlag, []),
        new("artwork-repair", "iTunes artwork repair", "FixArtwork.exe",
            "Repair artwork for an iTunes playlist.", UnifiedJobApplyMode.ApplyFlag, []),
        new("redundancies", "Redundancy report", "CheckRedundancies.exe",
            "Report likely duplicate iTunes tracks; this job is read-only.", UnifiedJobApplyMode.ReadOnly, []),
        new("itunes-validation", "iTunes library validation", "DumpITL.exe",
            "Validate structural and referential invariants in an ITL file.", UnifiedJobApplyMode.ReadOnly, ["validate"]),
    ];

    public async Task<UnifiedJobPlan> PreviewAsync(UnifiedJobDescriptor job, string executableDirectory,
        string arguments, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        string executable = ResolveExecutable(executableDirectory, job.ExecutableName);
        var info = new FileInfo(executable);
        var parsed = job.PrefixArguments.Concat(ParseArguments(arguments)).ToList();
        parsed.RemoveAll(argument => argument.Equals("--apply", StringComparison.OrdinalIgnoreCase));
        var result = await RunAsync(executable, parsed, progress, ct);
        return new(job, executable, parsed, info.Length, info.LastWriteTimeUtc,
            result.ExitCode, result.Output, DateTimeOffset.UtcNow);
    }

    public Task<UnifiedJobResult> ApplyAsync(UnifiedJobPlan plan,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanApply)
            throw new InvalidOperationException("This job does not have a successful applicable preview.");
        var info = new FileInfo(plan.ExecutablePath);
        if (!info.Exists || info.Length != plan.ExecutableLength ||
            info.LastWriteTimeUtc != plan.ExecutableLastWriteTimeUtc)
            throw new InvalidOperationException("The job executable changed since preview. Preview again before applying.");
        return RunAsync(plan.ExecutablePath, [.. plan.Arguments, "--apply"], progress, ct);
    }

    private static async Task<UnifiedJobResult> RunAsync(string executable,
        IReadOnlyList<string> arguments, IProgress<string>? progress, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var output = new StringBuilder();
        var gate = new object();
        void Capture(string? line)
        {
            if (line is null) return;
            lock (gate) output.AppendLine(line);
            progress?.Report(line);
        }
        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);
        var clock = Stopwatch.StartNew();
        if (!process.Start())
            throw new InvalidOperationException($"Could not start {executable}.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var registration = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            throw;
        }
        process.WaitForExit(); // flush asynchronous output events
        clock.Stop();
        lock (gate) return new(process.ExitCode, output.ToString(), clock.Elapsed);
    }

    private static string ResolveExecutable(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Choose the directory containing the workspace tools.");
        string path = Path.Combine(Path.GetFullPath(directory), name);
        if (!File.Exists(path) && !OperatingSystem.IsWindows())
            path = Path.Combine(Path.GetFullPath(directory), Path.GetFileNameWithoutExtension(name));
        if (!File.Exists(path))
            throw new FileNotFoundException($"Job executable was not found: {path}", path);
        return path;
    }

    public static IReadOnlyList<string> ParseArguments(string commandLine)
    {
        commandLine ??= "";
        var result = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        for (int index = 0; index < commandLine.Length; index++)
        {
            char ch = commandLine[index];
            if (ch == '"') { quoted = !quoted; continue; }
            if (ch == '\\' && index + 1 < commandLine.Length && commandLine[index + 1] == '"')
            {
                current.Append('"'); index++; continue;
            }
            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }
        if (quoted)
            throw new ArgumentException("Job arguments contain an unmatched quote.");
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }
}
