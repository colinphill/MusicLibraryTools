using System.Diagnostics;
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

            string assembly = Path.Combine(AppContext.BaseDirectory, "DumpITL.dll");
            Assert.True(File.Exists(assembly), $"DumpITL executable assembly was not found at {assembly}.");

            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add(assembly);
            start.ArgumentList.Add("validate");
            start.ArgumentList.Add(path);

            using Process process = Process.Start(start)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, stderr);
            Assert.Contains("validation: 0 error(s), 0 warning(s)", stdout);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
