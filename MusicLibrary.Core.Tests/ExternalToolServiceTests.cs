using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MusicLibrary.Core;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class ExternalToolServiceTests
{
    [Fact]
    public void SelectionPreviewExpandsFilesAsSeparateArguments()
    {
        using var temp = new TempDirectory();
        string executable = temp.CreateFile("tool.exe");
        string first = temp.CreateFile("one track.flac");
        string second = temp.CreateFile("two.flac");
        var service = new ExternalToolService(
            new RecordingRunner());
        var definition = new ExternalToolDefinition(
            Guid.NewGuid(),
            "Inspect selection",
            executable,
            ["--count", "{Count}", "{Files}"],
            temp.Path,
            ExternalToolInvocationMode.OnceForSelection);

        ExternalToolPlan plan = service.Preview(
            definition,
            [first, second]);

        Assert.True(plan.CanRun);
        ExternalToolInvocation invocation =
            Assert.Single(plan.Invocations);
        Assert.Equal(
            ["--count", "2", first, second],
            invocation.Arguments);
        Assert.Equal(temp.Path, invocation.WorkingDirectory);
        Assert.Equal([first, second], invocation.SourcePaths);
    }

    [Fact]
    public void PerFilePreviewExpandsDocumentedPlaceholders()
    {
        using var temp = new TempDirectory();
        string first = temp.CreateFile("one.flac");
        string second = temp.CreateFile("two track.mp3");
        var service = new ExternalToolService(
            new RecordingRunner());
        var definition = new ExternalToolDefinition(
            Guid.NewGuid(),
            "Per file",
            "path-tool",
            [
                "--input={File}",
                "{Directory}",
                "{FileName}",
                "{FileNameWithoutExtension}",
                "{Extension}",
                "{Index}/{Count}",
            ],
            "{Directory}",
            ExternalToolInvocationMode.OncePerFile);

        ExternalToolPlan plan = service.Preview(
            definition,
            [first, second]);

        Assert.True(plan.CanRun);
        Assert.Equal(2, plan.Invocations.Count);
        Assert.Equal(
            [
                $"--input={second}",
                temp.Path,
                "two track.mp3",
                "two track",
                ".mp3",
                "2/2",
            ],
            plan.Invocations[1].Arguments);
    }

    [Fact]
    public void PreviewBlocksUnsafeOrAmbiguousTemplates()
    {
        using var temp = new TempDirectory();
        string source = temp.CreateFile("one.flac");
        var service = new ExternalToolService(
            new RecordingRunner());
        var definition = new ExternalToolDefinition(
            Guid.NewGuid(),
            "Unsafe",
            "tool",
            ["--files={Files}", "{Unknown}"],
            InvocationMode:
                ExternalToolInvocationMode.OnceForSelection);

        ExternalToolPlan plan = service.Preview(
            definition,
            [source]);

        Assert.False(plan.CanRun);
        Assert.Contains(plan.Issues, issue =>
            issue.Code ==
            "external-tool-files-placeholder-required");
        Assert.Contains(plan.Issues, issue =>
            issue.Code ==
            "external-tool-files-placeholder-standalone");
        Assert.Contains(plan.Issues, issue =>
            issue.Code ==
            "external-tool-placeholder-unknown");
    }

    [Fact]
    public async Task RunIsSequentialProgressAwareAndCancellable()
    {
        using var temp = new TempDirectory();
        string first = temp.CreateFile("one.flac");
        string second = temp.CreateFile("two.flac");
        var runner = new RecordingRunner();
        var service = new ExternalToolService(runner);
        var definition = new ExternalToolDefinition(
            Guid.NewGuid(),
            "Inspect",
            "tool",
            ["{File}"],
            InvocationMode:
                ExternalToolInvocationMode.OncePerFile);
        ExternalToolPlan plan = service.Preview(
            definition,
            [first, second]);
        var progress = new List<OperationProgress>();

        ExternalToolRunResult result = await service.RunAsync(
            plan,
            new SynchronousProgress<OperationProgress>(progress.Add),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(2, runner.Invocations.Count);
        Assert.Equal(OperationPhase.Completed, progress[^1].Phase);

        var waiting = new RecordingRunner { WaitForCancellation = true };
        service = new ExternalToolService(waiting);
        using var cancellation = new CancellationTokenSource();
        Task<ExternalToolRunResult> run = service.RunAsync(
            plan,
            ct: cancellation.Token);
        await waiting.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run);
        Assert.True(waiting.CancellationObserved);
    }

    [Fact]
    public async Task RunRejectsFilesChangedAfterPreview()
    {
        using var temp = new TempDirectory();
        string source = temp.CreateFile("one.flac");
        var runner = new RecordingRunner();
        var service = new ExternalToolService(runner);
        var definition = new ExternalToolDefinition(
            Guid.NewGuid(),
            "Inspect",
            "tool",
            ["{File}"],
            InvocationMode:
                ExternalToolInvocationMode.OncePerFile);
        ExternalToolPlan plan = service.Preview(
            definition,
            [source]);
        await File.AppendAllTextAsync(
            source,
            "changed",
            TestContext.Current.CancellationToken);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.RunAsync(
                    plan,
                    ct: TestContext.Current.CancellationToken));

        Assert.Contains("Preview", error.Message);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public void StorePersistsPersonalDefinitions()
    {
        using var temp = new TempDirectory();
        var settings = new AppSettings(
            Path.Combine(temp.Path, "settings.json"));
        var store = new ExternalToolStore(settings);
        var definition = new ExternalToolDefinition(
            Guid.NewGuid(),
            "Inspect",
            "tool",
            ["{Files}"]);

        store.Save(definition);

        ExternalToolDefinition loaded =
            Assert.Single(new ExternalToolStore(
                new AppSettings(
                    Path.Combine(temp.Path, "settings.json"))).Load());
        Assert.Equal(definition.Id, loaded.Id);
        Assert.Equal(definition.Name, loaded.Name);
        Assert.Equal(definition.Executable, loaded.Executable);
        Assert.Equal(
            definition.Arguments.ToArray(),
            loaded.Arguments.ToArray());
        Assert.Equal(definition.InvocationMode, loaded.InvocationMode);

        store.Delete(definition.Id);
        Assert.Empty(store.Load());
    }

    [Fact]
    public void ServiceRegistrationIncludesExternalToolServices()
    {
        var services = new ServiceCollection();
        services.AddMusicLibraryCore();
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<ExternalToolService>(
            provider.GetRequiredService<IExternalToolService>());
        Assert.IsType<ExternalToolProcessRunner>(
            provider.GetRequiredService<IExternalToolProcessRunner>());
        Assert.IsType<ExternalToolStore>(
            provider.GetRequiredService<IExternalToolStore>());
    }

    [Fact]
    public async Task ProcessRunnerStartsWithoutShellAndCapturesOutput()
    {
        var runner = new ExternalToolProcessRunner();
        var invocation = new ExternalToolInvocation(
            "dotnet",
            ["--version"],
            null,
            []);

        ExternalToolProcessResult result = await runner.RunAsync(
            invocation,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(result.StandardOutput.Trim());
        Assert.Empty(result.StandardError.Trim());
    }

    private sealed class RecordingRunner :
        IExternalToolProcessRunner
    {
        public List<ExternalToolInvocation> Invocations { get; } = [];
        public bool WaitForCancellation { get; init; }
        public bool CancellationObserved { get; private set; }
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ExternalToolProcessResult> RunAsync(
            ExternalToolInvocation invocation,
            CancellationToken ct = default)
        {
            Invocations.Add(invocation);
            Started.TrySetResult(true);
            if (WaitForCancellation)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    CancellationObserved = true;
                    throw;
                }
            }
            return new(0, "ok", "");
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "mlm-tool-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public string CreateFile(string name)
        {
            string path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, "");
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
