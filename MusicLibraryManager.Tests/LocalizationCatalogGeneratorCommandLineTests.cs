using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class LocalizationCatalogGeneratorCommandLineTests
{
    private static readonly SemaphoreSlim GeneratorBuildLock = new(1, 1);
    private static string? generatorAssemblyPath;

    [Fact]
    public void Generator_failure_is_reported_as_a_nonzero_exit_without_escaping()
    {
        using var error = new StringWriter(
            CultureInfo.InvariantCulture);
        var expected = new InvalidDataException(
            "Translation coverage is incomplete.");

        int exitCode = CatalogGeneratorCommandLine.Run(
            ["--check"],
            _ => throw expected,
            error);

        Assert.Equal(1, exitCode);
        Assert.Equal(
            $"Translation coverage is incomplete.{Environment.NewLine}",
            error.ToString());
    }

    [Fact]
    public void Successful_generator_exit_is_preserved_without_error_output()
    {
        using var error = new StringWriter(
            CultureInfo.InvariantCulture);

        int exitCode = CatalogGeneratorCommandLine.Run(
            ["--check"],
            args => args.Length + 6,
            error);

        Assert.Equal(7, exitCode);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Real_generator_check_passes_without_catalog_drift()
    {
        string repositoryRoot = FindRepositoryRoot();
        string assemblyPath =
            await EnsureGeneratorBuiltAsync(repositoryRoot);

        ProcessResult result = await RunDotnetAsync(
            repositoryRoot,
            assemblyPath,
            "--check");

        Assert.True(
            result.ExitCode == 0,
            DescribeFailure(
                "The real localization catalog generator reported drift.",
                result));
    }

    [Fact]
    public async Task Capture_rejects_changed_protected_terms_without_writing_output()
    {
        string repositoryRoot = FindRepositoryRoot();
        string assemblyPath =
            await EnsureGeneratorBuiltAsync(repositoryRoot);
        string temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "mlm-localization-capture-" +
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        try
        {
            string isolatedAssemblyPath =
                CreateIsolatedGeneratorRepository(
                    repositoryRoot,
                    assemblyPath,
                    temporaryRoot);
            string germanCatalog = Path.Combine(
                temporaryRoot,
                "MusicLibraryManager.Presentation",
                "Resources",
                "Strings.de-DE.resx");
            XDocument catalog = XDocument.Load(
                germanCatalog,
                LoadOptions.PreserveWhitespace);
            XElement value = catalog.Root!
                .Elements("data")
                .Single(entry =>
                    string.Equals(
                        (string?)entry.Attribute("name"),
                        "Transcode.Format.Flac",
                        StringComparison.Ordinal))
                .Element("value")!;
            Assert.Equal("FLAC", value.Value);
            value.Value = "FLAK";
            catalog.Save(
                germanCatalog,
                SaveOptions.DisableFormatting);

            string capturePath = Path.Combine(
                temporaryRoot,
                "captured-overrides.xml");
            string overridesPath = Path.Combine(
                repositoryRoot,
                "BuildTools",
                "LocalizationCatalogGenerator",
                "EditorialOverrides.xml");
            ProcessResult result = await RunDotnetAsync(
                temporaryRoot,
                isolatedAssemblyPath,
                "--capture-editorial-overrides",
                capturePath,
                "--editorial-overrides",
                overridesPath);

            Assert.NotEqual(0, result.ExitCode);
            Assert.False(
                File.Exists(capturePath),
                "Capture mode wrote an override file after protected-term validation failed.");
            Assert.Contains(
                "de-DE:Transcode.Format.Flac: protected localization terms changed",
                result.StandardError,
                StringComparison.Ordinal);
            Assert.Contains(
                "literal 'FLAC'",
                result.StandardError,
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
                Directory.Delete(
                    temporaryRoot,
                    recursive: true);
        }
    }

    private static async Task<string> EnsureGeneratorBuiltAsync(
        string repositoryRoot)
    {
        if (generatorAssemblyPath is { } existing &&
            File.Exists(existing))
            return existing;

        await GeneratorBuildLock.WaitAsync();
        try
        {
            if (generatorAssemblyPath is { } built &&
                File.Exists(built))
                return built;

            string projectPath = Path.Combine(
                repositoryRoot,
                "BuildTools",
                "LocalizationCatalogGenerator",
                "LocalizationCatalogGenerator.csproj");
            ProcessResult result = await RunDotnetAsync(
                repositoryRoot,
                "build",
                projectPath,
                "--configuration",
                "Release",
                "--nologo",
                "--verbosity",
                "minimal");
            Assert.True(
                result.ExitCode == 0,
                DescribeFailure(
                    "Could not build the real localization catalog generator.",
                    result));

            string expectedAssembly = Path.Combine(
                repositoryRoot,
                "BuildTools",
                "LocalizationCatalogGenerator",
                "bin",
                "Release",
                "net10.0",
                "LocalizationCatalogGenerator.dll");
            Assert.True(
                File.Exists(expectedAssembly),
                $"The generator build did not produce '{expectedAssembly}'.");
            generatorAssemblyPath = expectedAssembly;
            return expectedAssembly;
        }
        finally
        {
            GeneratorBuildLock.Release();
        }
    }

    private static string CreateIsolatedGeneratorRepository(
        string repositoryRoot,
        string assemblyPath,
        string temporaryRoot)
    {
        string sourceResources = Path.Combine(
            repositoryRoot,
            "MusicLibraryManager.Presentation",
            "Resources");
        string isolatedResources = Path.Combine(
            temporaryRoot,
            "MusicLibraryManager.Presentation",
            "Resources");
        Directory.CreateDirectory(isolatedResources);
        foreach (string source in
                 Directory.EnumerateFiles(
                     sourceResources,
                     "Strings*.resx",
                     SearchOption.TopDirectoryOnly))
        {
            File.Copy(
                source,
                Path.Combine(
                    isolatedResources,
                    Path.GetFileName(source)));
        }
        Directory.CreateDirectory(
            Path.Combine(
                temporaryRoot,
                "MusicLibraryManager.Tests"));

        string sourceOutput =
            Path.GetDirectoryName(assemblyPath)!;
        string relativeOutput =
            Path.GetRelativePath(
                repositoryRoot,
                sourceOutput);
        string isolatedOutput = Path.Combine(
            temporaryRoot,
            relativeOutput);
        CopyDirectory(
            sourceOutput,
            isolatedOutput);
        return Path.Combine(
            isolatedOutput,
            Path.GetFileName(assemblyPath));
    }

    private static void CopyDirectory(
        string source,
        string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in
                 Directory.EnumerateFiles(source))
        {
            File.Copy(
                file,
                Path.Combine(
                    destination,
                    Path.GetFileName(file)),
                overwrite: true);
        }
        foreach (string directory in
                 Directory.EnumerateDirectories(source))
        {
            CopyDirectory(
                directory,
                Path.Combine(
                    destination,
                    Path.GetFileName(directory)));
        }
    }

    private static async Task<ProcessResult> RunDotnetAsync(
        string workingDirectory,
        params string[] arguments)
    {
        string dotnetHost =
            Environment.GetEnvironmentVariable(
                "DOTNET_HOST_PATH") is
                { Length: > 0 } configuredHost
                ? configuredHost
                : "dotnet";
        var startInfo = new ProcessStartInfo
        {
            FileName = dotnetHost,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["AVALONIA_TELEMETRY_OPTOUT"] = "1";
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process
        {
            StartInfo = startInfo,
        };
        if (!process.Start())
            throw new InvalidOperationException(
                $"Could not start '{dotnetHost}'.");

        Task<string> standardOutput =
            process.StandardOutput.ReadToEndAsync();
        Task<string> standardError =
            process.StandardError.ReadToEndAsync();
        using var timeout =
            new CancellationTokenSource(
                TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException(
                $"'{dotnetHost} {string.Join(' ', arguments)}' exceeded three minutes.");
        }

        return new ProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError,
            dotnetHost,
            arguments);
    }

    private static string DescribeFailure(
        string message,
        ProcessResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine(message);
        builder.Append("Command: ");
        builder.Append(result.Executable);
        foreach (string argument in result.Arguments)
        {
            builder.Append(' ');
            builder.Append(argument);
        }
        builder.AppendLine();
        builder.AppendLine(
            $"Exit code: {result.ExitCode}");
        builder.AppendLine("Standard output:");
        builder.AppendLine(result.StandardOutput);
        builder.AppendLine("Standard error:");
        builder.Append(result.StandardError);
        return builder.ToString();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current =
            new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "MusicLibraryTools.sln")) &&
                File.Exists(Path.Combine(
                    current.FullName,
                    "BuildTools",
                    "LocalizationCatalogGenerator",
                    "LocalizationCatalogGenerator.csproj")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate the MusicLibraryTools repository root.");
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        string Executable,
        IReadOnlyList<string> Arguments);
}
