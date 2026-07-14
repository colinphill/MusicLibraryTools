using System.Diagnostics;
using iTunes.Binary;
using Xunit;

namespace DumpITL.Tests;

public sealed class CorpusRegressionTests
{
    [Fact]
    public void PrivateCorpusRemainsIdenticalAndXmlVerifiedWhenConfigured()
    {
        string? itl = Environment.GetEnvironmentVariable("DUMPITL_CORPUS_ITL");
        string? xml = Environment.GetEnvironmentVariable("DUMPITL_CORPUS_XML");
        if (string.IsNullOrWhiteSpace(itl) || string.IsNullOrWhiteSpace(xml) || !File.Exists(itl) || !File.Exists(xml))
            return;

        ItlEnvelope envelope = ItlEnvelope.Load(itl);
        byte[] original = (byte[])envelope.Body.Clone();
        ItlDocument document = ItlDocument.Parse(envelope);
        Assert.Equal(original, document.Serialize());
        Assert.DoesNotContain(document.Validate(), issue => issue.Severity == ItlValidationSeverity.Error);

        string output = RunCli("verify", itl, xml);
        Assert.DoesNotContain("mismatched", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("contentRating ok", output);
        Assert.Contains("clean         ok", output);
    }

    private static string RunCli(params string[] arguments)
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
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return stdout;
    }
}
