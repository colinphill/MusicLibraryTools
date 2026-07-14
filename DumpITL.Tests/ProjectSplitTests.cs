using System.Diagnostics;
using System.Text.Json;
using iTunes.Binary;
using Xunit;

namespace DumpITL.Tests;

public sealed class ProjectSplitTests
{
    [Fact]
    public void PublicFormatApiIsProvidedByItlTools()
    {
        Assert.Equal("ITLTools", typeof(ItlEnvelope).Assembly.GetName().Name);
    }

    [Fact]
    public void StandaloneDumpItlValidatesALibrary()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dumpitl-split-{Guid.NewGuid():N}.itl");
        try
        {
            File.WriteAllBytes(path, SyntheticLibrary.CreateFile());
            (int exitCode, string stdout, string stderr) = RunCli("validate", path);
            Assert.True(exitCode == 0, stderr);
            Assert.Contains("validation: 0 error(s), 0 warning(s)", stdout);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void StandaloneDumpItlEmitsResearchSnapshotJson()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dumpitl-snapshot-cli-{Guid.NewGuid():N}.itl");
        try
        {
            File.WriteAllBytes(path, SyntheticLibrary.CreateFile());
            (int exitCode, string stdout, string stderr) = RunCli("snapshot", path);

            Assert.True(exitCode == 0, stderr);
            using JsonDocument json = JsonDocument.Parse(stdout);
            Assert.Equal(2, json.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(1, json.RootElement.GetProperty("parsedCounts").GetProperty("tracks").GetInt32());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("mprh").ValueKind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCli(params string[] arguments)
    {
        string assembly = Path.Combine(AppContext.BaseDirectory, "DumpITL.dll");
        Assert.True(File.Exists(assembly), $"DumpITL executable assembly was not found at {assembly}.");

        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(assembly);
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        using Process process = Process.Start(start)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }
}
