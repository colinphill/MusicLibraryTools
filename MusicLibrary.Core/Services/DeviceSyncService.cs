using System.Collections.ObjectModel;
using System.Text;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public enum FileTreeEndpointKind { Local, Adb }

public sealed record FileTreeEndpointDescriptor(
    FileTreeEndpointKind Kind,
    string Root,
    string? DeviceSerial = null)
{
    public string DisplayName => Kind == FileTreeEndpointKind.Adb ? "adb:" + Root : Root;
}

public sealed record FileTreeEntry(
    string RelativePath,
    bool IsDirectory,
    long Length,
    DateTime LastWriteTimeUtc);

public sealed record FileTreeSnapshot(
    FileTreeEndpointDescriptor Endpoint,
    IReadOnlyDictionary<string, FileTreeEntry> Entries,
    DateTimeOffset CapturedAtUtc);

/// <summary>A rooted file-tree endpoint. Paths passed to mutation methods are endpoint-native absolute paths.</summary>
public interface IFileTreeEndpoint
{
    FileTreeEndpointDescriptor Descriptor { get; }
    Task<FileTreeSnapshot> CaptureAsync(
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string path, CancellationToken ct = default);
    Task CreateDirectoryAsync(string path, CancellationToken ct = default);
    Task WriteFileAsync(string path, Stream source, DateTime modifiedUtc,
        IProgress<long>? progress = null, CancellationToken ct = default);
    Task MoveAsync(string sourcePath, string destinationPath, CancellationToken ct = default);
    Task DeleteFileAsync(string path, CancellationToken ct = default);
    Task DeleteDirectoryAsync(string path, CancellationToken ct = default);
    Task AppendJournalLinesAsync(string journalPath, IReadOnlyList<string> lines,
        CancellationToken ct = default);
}

public interface IFileTreeEndpointFactory
{
    FileTreeEndpointDescriptor Parse(string value);
    IFileTreeEndpoint Create(FileTreeEndpointDescriptor descriptor);
}

public sealed class FileTreeEndpointFactory : IFileTreeEndpointFactory
{
    public FileTreeEndpointDescriptor Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!value.StartsWith("adb:", StringComparison.OrdinalIgnoreCase))
            return new(FileTreeEndpointKind.Local, Path.GetFullPath(value));

        string path = NormalizeAdbPath(value[4..]);
        return new(FileTreeEndpointKind.Adb, path);
    }

    public IFileTreeEndpoint Create(FileTreeEndpointDescriptor descriptor) => descriptor.Kind switch
    {
        FileTreeEndpointKind.Local => new LocalFileTreeEndpoint(descriptor),
        FileTreeEndpointKind.Adb => new AdbFileTreeEndpoint(descriptor),
        _ => throw new ArgumentOutOfRangeException(nameof(descriptor)),
    };

    internal static string NormalizeAdbPath(string path)
    {
        path = path.Replace('\\', '/');
        bool absolute = path.StartsWith('/');
        var segments = new List<string>();
        foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0)
                    throw new ArgumentException("An Android path cannot traverse above its root.");
                segments.RemoveAt(segments.Count - 1);
            }
            else
                segments.Add(segment);
        }
        return (absolute ? "/" : "") + string.Join('/', segments);
    }
}

public sealed class LocalFileTreeEndpoint : IFileTreeEndpoint
{
    public FileTreeEndpointDescriptor Descriptor { get; }

    public LocalFileTreeEndpoint(FileTreeEndpointDescriptor descriptor)
    {
        if (descriptor.Kind != FileTreeEndpointKind.Local)
            throw new ArgumentException("A local endpoint requires a local descriptor.", nameof(descriptor));
        Descriptor = descriptor with { Root = Path.GetFullPath(descriptor.Root) };
    }

    public Task<FileTreeSnapshot> CaptureAsync(
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default) => Task.Run(() =>
    {
        var entries = new Dictionary<string, FileTreeEntry>(PathComparer);
        var root = new DirectoryInfo(Descriptor.Root);
        if (!root.Exists)
            throw new DirectoryNotFoundException($"Endpoint root was not found: {Descriptor.Root}");
        int completed = 0;
        foreach (FileSystemInfo info in root.EnumerateFileSystemInfos("*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     ReturnSpecialDirectories = false,
                     IgnoreInaccessible = false,
                     AttributesToSkip = 0,
                 }))
        {
            ct.ThrowIfCancellationRequested();
            string relative = NormalizeRelative(Path.GetRelativePath(Descriptor.Root, info.FullName));
            FileTreeEntry entry = info is DirectoryInfo
                // Child entries carry the state that matters. Directory mtimes are both delayed
                // and low-value on network/portable filesystems, so including them creates false
                // stale-plan failures without detecting changes that the child inventory misses.
                ? new(relative, true, 0, DateTime.MinValue)
                : new(relative, false, ((FileInfo)info).Length, info.LastWriteTimeUtc);
            if (!entries.TryAdd(relative, entry))
                throw new InvalidDataException($"Duplicate endpoint path: {relative}");
            if ((++completed & 0x7f) == 0)
                progress?.Report(new(OperationPhase.IndexingSources, completed,
                    CurrentPath: info.FullName, Message: "Inventorying local endpoint"));
        }
        return new FileTreeSnapshot(Descriptor, new ReadOnlyDictionary<string, FileTreeEntry>(entries),
            DateTimeOffset.UtcNow);
    }, ct);

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(path);
    }, ct);

    public async Task WriteFileAsync(string path, Stream source, DateTime modifiedUtc,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.sync.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                byte[] buffer = new byte[1024 * 1024];
                long transferred = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    progress?.Report(transferred += read);
                }
                await output.FlushAsync(ct).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            File.SetLastWriteTimeUtc(temporary, modifiedUtc);
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task MoveAsync(string sourcePath, string destinationPath, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            if (Directory.Exists(sourcePath)) Directory.Move(sourcePath, destinationPath);
            else File.Move(sourcePath, destinationPath, overwrite: false);
        }, ct);

    public Task DeleteFileAsync(string path, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(path)) File.Delete(path);
    }, ct);

    public Task DeleteDirectoryAsync(string path, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (Directory.Exists(path)) Directory.Delete(path, recursive: false);
    }, ct);

    public async Task AppendJournalLinesAsync(
        string journalPath, IReadOnlyList<string> lines, CancellationToken ct = default)
    {
        if (lines.Count == 0) return;
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        await using var stream = new FileStream(journalPath, FileMode.Append, FileAccess.Write,
            FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        foreach (string line in lines)
            await writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static string NormalizeRelative(string path) => path.Replace('\\', '/');
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

public sealed record DeviceSyncRequest(
    string Source,
    string Destination,
    bool RemapMusic = false,
    int MaxRemovals = 0);

public enum DeviceSyncMutationKind
{
    CreateDirectory,
    CopyFile,
    ReplaceFile,
    QuarantineFile,
    QuarantineDirectory,
}

public sealed record DeviceSyncAction(
    DeviceSyncMutationKind Kind,
    string RelativePath,
    string? SourceRelativePath,
    FileTreeEntry? ExpectedSource,
    FileTreeEntry? ExpectedDestination);

public sealed record DeviceSyncPlan(
    DeviceSyncRequest Request,
    FileTreeEndpointDescriptor Source,
    FileTreeEndpointDescriptor Destination,
    FileTreeSnapshot SourceSnapshot,
    FileTreeSnapshot DestinationSnapshot,
    IReadOnlyList<DeviceSyncAction> Actions,
    int UnchangedFileCount,
    int RemovalCount,
    string RecoveryRoot,
    IReadOnlyList<OperationIssue> Issues,
    DateTimeOffset CreatedAtUtc)
{
    public bool CanApply => Issues.All(issue => issue.Severity != OperationIssueSeverity.Blocker);
}

public sealed record DeviceSyncResult(
    int CreatedDirectoryCount,
    int CopiedFileCount,
    int ReplacedFileCount,
    int QuarantinedCount,
    string? JournalPath,
    IReadOnlyList<OperationIssue> Issues);

public interface IDeviceSyncService
{
    Task<DeviceSyncPlan> PreviewAsync(DeviceSyncRequest request,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
    Task<DeviceSyncResult> ApplyAsync(DeviceSyncPlan plan,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
}

/// <summary>
/// Computes and applies one deterministic tree projection for local or ADB endpoints. Both endpoint
/// inventories are captured once during preview and once during apply validation; action planning
/// itself performs no additional filesystem probes.
/// </summary>
public sealed class DeviceSyncService : IDeviceSyncService
{
    private static readonly string[] RemappedRoots =
        ["FLAC", "FLAC2", "HiResPCM", "HiResDSD", "HiResWV", "Lossy"];
    private readonly IFileTreeEndpointFactory _endpoints;
    private readonly IItunesMediaMutationService? _itunes;
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    public DeviceSyncService(
        IFileTreeEndpointFactory endpoints,
        IItunesMediaMutationService? itunes = null)
    {
        _endpoints = endpoints;
        _itunes = itunes;
    }

    public async Task<DeviceSyncPlan> PreviewAsync(DeviceSyncRequest request,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaxRemovals < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "MaxRemovals cannot be negative.");
        FileTreeEndpointDescriptor sourceDescriptor = _endpoints.Parse(request.Source);
        FileTreeEndpointDescriptor destinationDescriptor = _endpoints.Parse(request.Destination);
        var issues = ValidateRoots(sourceDescriptor, destinationDescriptor);
        if (issues.Any(issue => issue.Severity == OperationIssueSeverity.Blocker))
            return BlockedPlan(request, sourceDescriptor, destinationDescriptor, issues);

        IFileTreeEndpoint source = _endpoints.Create(sourceDescriptor);
        IFileTreeEndpoint destination = _endpoints.Create(destinationDescriptor);
        progress?.Report(new(OperationPhase.IndexingSources, Message: "Inventorying source and destination endpoints"));
        Task<FileTreeSnapshot> sourceTask = source.CaptureAsync(progress, ct);
        Task<FileTreeSnapshot> destinationTask = destination.CaptureAsync(progress, ct);
        await Task.WhenAll(sourceTask, destinationTask).ConfigureAwait(false);
        FileTreeSnapshot sourceSnapshot = await sourceTask.ConfigureAwait(false);
        FileTreeSnapshot destinationSnapshot = await destinationTask.ConfigureAwait(false);
        progress?.Report(new(OperationPhase.Planning, Message: "Computing device-tree differences"));
        return Plan(request, sourceSnapshot, destinationSnapshot, issues);
    }

    public async Task<DeviceSyncResult> ApplyAsync(DeviceSyncPlan plan,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanApply)
            throw new InvalidOperationException("The device-sync plan contains blocking issues.");
        await _applyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ApplyCoreAsync(plan, progress, ct).ConfigureAwait(false);
        }
        finally { _applyGate.Release(); }
    }

    private async Task<DeviceSyncResult> ApplyCoreAsync(DeviceSyncPlan plan,
        IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        IFileTreeEndpoint source = _endpoints.Create(plan.Source);
        IFileTreeEndpoint destination = _endpoints.Create(plan.Destination);
        progress?.Report(new(OperationPhase.Validating, Message: "Re-inventorying both endpoints before the first write"));
        Task<FileTreeSnapshot> sourceTask = source.CaptureAsync(progress, ct);
        Task<FileTreeSnapshot> destinationTask = destination.CaptureAsync(progress, ct);
        await Task.WhenAll(sourceTask, destinationTask).ConfigureAwait(false);
        EnsureSameSnapshot(plan.SourceSnapshot, await sourceTask.ConfigureAwait(false), "source");
        EnsureSameSnapshot(plan.DestinationSnapshot, await destinationTask.ConfigureAwait(false), "destination");
        if (plan.Actions.Count == 0)
            return new(0, 0, 0, 0, null, plan.Issues);

        ItunesMediaMutation[] itunesMutations = plan.Destination.Kind == FileTreeEndpointKind.Local
            ? BuildItunesMutations(plan).ToArray()
            : [];
        string[] mutationPaths = itunesMutations.SelectMany(mutation =>
                new[] { mutation.OriginalPath, mutation.CurrentPath })
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(PathComparer)
            .ToArray();
        await using IItunesMediaMutationSession? itunesSession =
            _itunes is null || mutationPaths.Length == 0
                ? null
                : await _itunes.BeginAsync(mutationPaths, backupFiles: false, ct)
                    .ConfigureAwait(false);
        await destination.CreateDirectoryAsync(plan.RecoveryRoot, ct).ConfigureAwait(false);
        string journalPath = Combine(plan.Destination, plan.RecoveryRoot, "journal.tsv");
        string operationId = Guid.NewGuid().ToString("N");
        await destination.AppendJournalLinesAsync(journalPath,
            [$"BEGIN\t{operationId}", .. plan.Actions.Select(action => PlanLine(plan, action))], ct)
            .ConfigureAwait(false);

        var rollback = new Stack<Func<Task>>();
        int directories = 0, copied = 0, replaced = 0, quarantined = 0;
        try
        {
            for (int index = 0; index < plan.Actions.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                DeviceSyncAction action = plan.Actions[index];
                string destinationPath = Combine(plan.Destination, plan.Destination.Root, action.RelativePath);
                string recoveryPath = Combine(plan.Destination, plan.RecoveryRoot, action.RelativePath);
                progress?.Report(new(OperationPhase.Applying, index, plan.Actions.Count,
                    destinationPath, action.Kind.ToString()));
                switch (action.Kind)
                {
                    case DeviceSyncMutationKind.CreateDirectory:
                        await destination.CreateDirectoryAsync(destinationPath, ct).ConfigureAwait(false);
                        rollback.Push(() => destination.DeleteDirectoryAsync(destinationPath));
                        await destination.AppendJournalLinesAsync(journalPath,
                            [$"INSTALL\tDIRECTORY\t{destinationPath}"], ct).ConfigureAwait(false);
                        directories++;
                        break;

                    case DeviceSyncMutationKind.QuarantineFile or DeviceSyncMutationKind.QuarantineDirectory:
                        await destination.MoveAsync(destinationPath, recoveryPath, ct).ConfigureAwait(false);
                        rollback.Push(() => destination.MoveAsync(recoveryPath, destinationPath));
                        await destination.AppendJournalLinesAsync(journalPath,
                            [$"QUARANTINE\tDEVICE\t{destinationPath}\t{recoveryPath}"], ct).ConfigureAwait(false);
                        quarantined++;
                        break;

                    case DeviceSyncMutationKind.CopyFile:
                        await CopySourceAsync(source, destination, plan, action, destinationPath,
                            progress, ct).ConfigureAwait(false);
                        rollback.Push(() => destination.DeleteFileAsync(destinationPath));
                        await destination.AppendJournalLinesAsync(journalPath,
                            [$"INSTALL\tDEVICE\t{destinationPath}"], ct).ConfigureAwait(false);
                        copied++;
                        break;

                    case DeviceSyncMutationKind.ReplaceFile:
                        await destination.MoveAsync(destinationPath, recoveryPath, ct).ConfigureAwait(false);
                        rollback.Push(() => destination.MoveAsync(recoveryPath, destinationPath));
                        await destination.AppendJournalLinesAsync(journalPath,
                            [$"QUARANTINE\tREPLACE\t{destinationPath}\t{recoveryPath}"], ct).ConfigureAwait(false);
                        await CopySourceAsync(source, destination, plan, action, destinationPath,
                            progress, ct).ConfigureAwait(false);
                        rollback.Push(() => destination.DeleteFileAsync(destinationPath));
                        await destination.AppendJournalLinesAsync(journalPath,
                            [$"INSTALL\tDEVICE\t{destinationPath}"], ct).ConfigureAwait(false);
                        replaced++;
                        break;
                }
            }
            if (itunesSession is not null)
                await itunesSession.CommitAsync(itunesMutations, CancellationToken.None)
                    .ConfigureAwait(false);
            await destination.AppendJournalLinesAsync(journalPath, [$"COMMIT\t{operationId}"], ct)
                .ConfigureAwait(false);
            if (itunesSession is not null)
                await itunesSession.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
            progress?.Report(new(OperationPhase.Completed, plan.Actions.Count, plan.Actions.Count,
                Message: "Device synchronization completed"));
            return new(directories, copied, replaced, quarantined, journalPath, plan.Issues);
        }
        catch (Exception applyError)
        {
            progress?.Report(new(OperationPhase.RollingBack, Message: "Rolling back device synchronization"));
            var errors = new List<Exception>();
            while (rollback.TryPop(out Func<Task>? undo))
            {
                try { await undo().ConfigureAwait(false); }
                catch (Exception error) { errors.Add(error); }
            }
            try
            {
                await destination.AppendJournalLinesAsync(journalPath, [errors.Count == 0
                    ? $"ROLLBACK\t{operationId}"
                    : $"ROLLBACK_FAILED\t{operationId}"]).ConfigureAwait(false);
            }
            catch (Exception error) { errors.Add(error); }
            if (errors.Count > 0)
                throw new AggregateException("Device synchronization failed and rollback was incomplete.",
                    [applyError, .. errors]);
            throw;
        }
    }

    private static IEnumerable<ItunesMediaMutation> BuildItunesMutations(DeviceSyncPlan plan)
    {
        foreach (DeviceSyncAction action in plan.Actions)
        {
            string path = Combine(plan.Destination, plan.Destination.Root, action.RelativePath);
            switch (action.Kind)
            {
                case DeviceSyncMutationKind.CopyFile:
                    yield return ItunesMediaMutation.Add(path);
                    break;
                case DeviceSyncMutationKind.ReplaceFile:
                    yield return ItunesMediaMutation.Refresh(path);
                    break;
                case DeviceSyncMutationKind.QuarantineFile:
                    yield return ItunesMediaMutation.Remove(path);
                    break;
                case DeviceSyncMutationKind.QuarantineDirectory:
                    foreach (FileTreeEntry file in plan.DestinationSnapshot.Entries.Values.Where(
                                 entry => !entry.IsDirectory &&
                                          (PathComparer.Equals(entry.RelativePath,
                                               action.RelativePath) ||
                                           entry.RelativePath.StartsWith(
                                               action.RelativePath.TrimEnd('/') + "/",
                                               OperatingSystem.IsWindows()
                                                   ? StringComparison.OrdinalIgnoreCase
                                                   : StringComparison.Ordinal))))
                    {
                        yield return ItunesMediaMutation.Remove(Combine(
                            plan.Destination, plan.Destination.Root, file.RelativePath));
                    }
                    break;
            }
        }
    }

    private static async Task CopySourceAsync(IFileTreeEndpoint source,
        IFileTreeEndpoint destination, DeviceSyncPlan plan, DeviceSyncAction action,
        string destinationPath, IProgress<OperationProgress>? operationProgress,
        CancellationToken ct)
    {
        if (action.SourceRelativePath is null || action.ExpectedSource is null)
            throw new InvalidDataException("A planned device copy has no source entry.");
        string sourcePath = Combine(plan.Source, plan.Source.Root, action.SourceRelativePath);
        await using Stream input = await source.OpenReadAsync(sourcePath, ct).ConfigureAwait(false);
        var bytes = new Progress<long>(transferred => operationProgress?.Report(new(
            OperationPhase.Applying, CurrentPath: destinationPath,
            Message: $"Transferred {transferred:N0}/{action.ExpectedSource.Length:N0} bytes")));
        await destination.WriteFileAsync(destinationPath, input,
            action.ExpectedSource.LastWriteTimeUtc, bytes, ct).ConfigureAwait(false);
    }

    private static DeviceSyncPlan Plan(DeviceSyncRequest request,
        FileTreeSnapshot source, FileTreeSnapshot destination, List<OperationIssue> issues)
    {
        var desired = new Dictionary<string, (FileTreeEntry Entry, string SourcePath)>(PathComparer);
        foreach (FileTreeEntry entry in source.Entries.Values)
        {
            string relative = request.RemapMusic ? Remap(entry.RelativePath) : entry.RelativePath;
            if (!desired.TryAdd(relative, (entry with { RelativePath = relative }, entry.RelativePath)))
                issues.Add(new("remap-collision", OperationIssueSeverity.Blocker,
                    $"Multiple source entries map to '{relative}'.", relative));
        }
        AddImplicitDirectories(desired);
        foreach ((string relative, (FileTreeEntry entry, _)) in desired)
        {
            if (!entry.IsDirectory && desired.Keys.Any(candidate =>
                    candidate.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase)))
                issues.Add(new("remap-type-collision", OperationIssueSeverity.Blocker,
                    $"A remapped file is also the parent of another entry: '{relative}'.", relative));
        }
        if (issues.Any(issue => issue.Severity == OperationIssueSeverity.Blocker))
            return CreatePlan(request, source, destination, [], 0, 0, issues);

        var actions = new List<DeviceSyncAction>();
        var quarantinedRoots = new HashSet<string>(PathComparer);
        var preservedRoots = new HashSet<string>(PathComparer);
        int removals = 0;

        // Type conflicts must leave before their desired replacement can be created.
        foreach ((string relative, (FileTreeEntry wanted, _)) in desired.OrderBy(pair => Depth(pair.Key)))
        {
            if (!destination.Entries.TryGetValue(relative, out FileTreeEntry? existing) ||
                existing.IsDirectory == wanted.IsDirectory)
                continue;
            if (existing.IsDirectory && HasProtectedDescendant(destination, relative))
            {
                issues.Add(new("protected-conflict", OperationIssueSeverity.Blocker,
                    "A protected destination subtree conflicts with the desired entry.", relative));
                continue;
            }
            actions.Add(new(existing.IsDirectory ? DeviceSyncMutationKind.QuarantineDirectory :
                DeviceSyncMutationKind.QuarantineFile, relative, null, null, existing));
            quarantinedRoots.Add(relative);
            removals += RemovalWeight(destination, relative);
        }

        // Quarantine minimal destination-only directory trees, preserving protected subtrees.
        foreach (FileTreeEntry directory in destination.Entries.Values
                     .Where(entry => entry.IsDirectory && !desired.ContainsKey(entry.RelativePath))
                     .OrderBy(entry => Depth(entry.RelativePath)))
        {
            if (Covered(directory.RelativePath, quarantinedRoots) ||
                Covered(directory.RelativePath, preservedRoots))
                continue;
            if (HasProtectedDescendant(destination, directory.RelativePath))
            {
                preservedRoots.Add(directory.RelativePath);
                continue;
            }
            actions.Add(new(DeviceSyncMutationKind.QuarantineDirectory, directory.RelativePath,
                null, null, directory));
            quarantinedRoots.Add(directory.RelativePath);
            removals += RemovalWeight(destination, directory.RelativePath);
        }
        foreach (FileTreeEntry file in destination.Entries.Values.Where(entry => !entry.IsDirectory))
        {
            if (desired.ContainsKey(file.RelativePath) || Covered(file.RelativePath, quarantinedRoots) ||
                Covered(file.RelativePath, preservedRoots) ||
                IsProtected(file.RelativePath))
                continue;
            actions.Add(new(DeviceSyncMutationKind.QuarantineFile, file.RelativePath,
                null, null, file));
            quarantinedRoots.Add(file.RelativePath);
            removals++;
        }

        foreach ((string relative, (FileTreeEntry wanted, string sourcePath)) in desired
                     .Where(pair => pair.Value.Entry.IsDirectory).OrderBy(pair => Depth(pair.Key)))
        {
            if (!destination.Entries.TryGetValue(relative, out FileTreeEntry? existing) ||
                !existing.IsDirectory)
                actions.Add(new(DeviceSyncMutationKind.CreateDirectory, relative, sourcePath,
                    wanted, existing));
        }

        int unchanged = 0;
        foreach ((string relative, (FileTreeEntry wanted, string sourcePath)) in desired
                     .Where(pair => !pair.Value.Entry.IsDirectory).OrderBy(pair => pair.Key, PathComparer))
        {
            if (!destination.Entries.TryGetValue(relative, out FileTreeEntry? existing) || existing.IsDirectory)
            {
                actions.Add(new(DeviceSyncMutationKind.CopyFile, relative, sourcePath, wanted, existing));
                continue;
            }
            if (wanted.Length != existing.Length ||
                wanted.LastWriteTimeUtc > existing.LastWriteTimeUtc.AddMinutes(65))
                actions.Add(new(DeviceSyncMutationKind.ReplaceFile, relative, sourcePath, wanted, existing));
            else
                unchanged++;
        }

        if (removals > request.MaxRemovals)
            issues.Add(new("removal-limit", OperationIssueSeverity.Blocker,
                $"The plan removes {removals:N0} entries, exceeding MaxRemovals {request.MaxRemovals:N0}."));
        return CreatePlan(request, source, destination, actions, unchanged, removals, issues);
    }

    private static DeviceSyncPlan CreatePlan(DeviceSyncRequest request,
        FileTreeSnapshot source, FileTreeSnapshot destination, IReadOnlyList<DeviceSyncAction> actions,
        int unchanged, int removals, IReadOnlyList<OperationIssue> issues)
    {
        string recoveryRoot = destination.Endpoint.Kind == FileTreeEndpointKind.Local
            ? destination.Endpoint.Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
              ".AndroidSync-quarantine" + Path.DirectorySeparatorChar + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")
            : destination.Endpoint.Root.TrimEnd('/') + ".AndroidSync-quarantine/" +
              DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        return new(request, source.Endpoint, destination.Endpoint, source, destination, actions,
            unchanged, removals, recoveryRoot, issues, DateTimeOffset.UtcNow);
    }

    private static DeviceSyncPlan BlockedPlan(DeviceSyncRequest request,
        FileTreeEndpointDescriptor source, FileTreeEndpointDescriptor destination,
        IReadOnlyList<OperationIssue> issues)
    {
        var sourceSnapshot = new FileTreeSnapshot(source,
            new ReadOnlyDictionary<string, FileTreeEntry>(new Dictionary<string, FileTreeEntry>()),
            DateTimeOffset.UtcNow);
        var destinationSnapshot = new FileTreeSnapshot(destination,
            new ReadOnlyDictionary<string, FileTreeEntry>(new Dictionary<string, FileTreeEntry>()),
            DateTimeOffset.UtcNow);
        return CreatePlan(request, sourceSnapshot, destinationSnapshot, [], 0, 0, issues);
    }

    private static List<OperationIssue> ValidateRoots(
        FileTreeEndpointDescriptor source, FileTreeEndpointDescriptor destination)
    {
        var issues = new List<OperationIssue>();
        bool sameEndpoint = source.Kind == destination.Kind &&
            StringComparer.Ordinal.Equals(source.DeviceSerial, destination.DeviceSerial);
        if (sameEndpoint && PathsOverlap(source, destination))
            issues.Add(new("root-overlap", OperationIssueSeverity.Blocker,
                "Source and destination roots overlap."));
        bool destinationIsRoot = destination.Kind == FileTreeEndpointKind.Adb
            ? destination.Root.Trim('/').Length == 0
            : StringComparer.OrdinalIgnoreCase.Equals(
                Path.TrimEndingDirectorySeparator(destination.Root),
                Path.TrimEndingDirectorySeparator(Path.GetPathRoot(destination.Root)!));
        if (destinationIsRoot)
            issues.Add(new("filesystem-root", OperationIssueSeverity.Blocker,
                "A filesystem root cannot be used as the destination.", destination.DisplayName));
        return issues;
    }

    private static bool PathsOverlap(FileTreeEndpointDescriptor first, FileTreeEndpointDescriptor second)
    {
        if (first.Kind == FileTreeEndpointKind.Adb)
        {
            string a = "/" + first.Root.Trim('/') + "/";
            string b = "/" + second.Root.Trim('/') + "/";
            return a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal);
        }
        string aLocal = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first.Root)) + Path.DirectorySeparatorChar;
        string bLocal = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second.Root)) + Path.DirectorySeparatorChar;
        return aLocal.StartsWith(bLocal, StringComparison.OrdinalIgnoreCase) ||
               bLocal.StartsWith(aLocal, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureSameSnapshot(FileTreeSnapshot expected,
        FileTreeSnapshot current, string role)
    {
        if (expected.Entries.Count != current.Entries.Count)
            throw new InvalidOperationException($"The {role} endpoint changed after preview. Preview again.");
        foreach ((string path, FileTreeEntry entry) in expected.Entries)
        {
            if (!current.Entries.TryGetValue(path, out FileTreeEntry? candidate) ||
                entry.IsDirectory != candidate.IsDirectory || entry.Length != candidate.Length ||
                entry.LastWriteTimeUtc != candidate.LastWriteTimeUtc)
                throw new InvalidOperationException(
                    $"The {role} endpoint changed after preview at '{path}'. Preview again.");
        }
    }

    private static void AddImplicitDirectories(
        Dictionary<string, (FileTreeEntry Entry, string SourcePath)> desired)
    {
        foreach (string path in desired.Keys.ToArray())
        {
            string? parent = Parent(path);
            while (!string.IsNullOrEmpty(parent))
            {
                if (!desired.ContainsKey(parent))
                    desired[parent] = (new(parent, true, 0, DateTime.MinValue), parent);
                parent = Parent(parent);
            }
        }
    }

    private static string Remap(string relative)
    {
        string[] parts = relative.Split('/');
        for (int index = 0; index < parts.Length; index++)
            if (RemappedRoots.Contains(parts[index], StringComparer.Ordinal))
                parts[index] = "Files";
        return string.Join('/', parts);
    }

    private static bool HasProtectedDescendant(FileTreeSnapshot snapshot, string relative) =>
        snapshot.Entries.Keys.Any(path => IsWithin(path, relative) && IsProtected(path));

    private static int RemovalWeight(FileTreeSnapshot snapshot, string relative) =>
        snapshot.Entries.Keys.Count(path => IsWithin(path, relative));

    private static bool Covered(string relative, IEnumerable<string> roots) =>
        roots.Any(root => IsWithin(relative, root));

    private static bool IsWithin(string path, string root) =>
        PathComparer.Equals(path, root) || path.StartsWith(root + "/",
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsProtected(string path) =>
        path.Contains("System Volume Information", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(".thumbnail", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("$RECYCLE", StringComparison.OrdinalIgnoreCase);

    private static string? Parent(string relative)
    {
        int separator = relative.LastIndexOf('/');
        return separator < 0 ? null : relative[..separator];
    }

    private static int Depth(string relative) => relative.Count(character => character == '/');

    private static string Combine(FileTreeEndpointDescriptor endpoint, string root, string relative)
    {
        if (string.IsNullOrEmpty(relative)) return root;
        return endpoint.Kind == FileTreeEndpointKind.Adb
            ? root.TrimEnd('/') + "/" + relative.Replace('\\', '/').TrimStart('/')
            : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string PlanLine(DeviceSyncPlan plan, DeviceSyncAction action)
    {
        string destination = Combine(plan.Destination, plan.Destination.Root, action.RelativePath);
        string recovery = Combine(plan.Destination, plan.RecoveryRoot, action.RelativePath);
        return action.Kind switch
        {
            DeviceSyncMutationKind.QuarantineFile or DeviceSyncMutationKind.QuarantineDirectory or
                DeviceSyncMutationKind.ReplaceFile => $"PLAN_QUARANTINE\tDEVICE\t{destination}\t{recovery}",
            _ => $"PLAN_INSTALL\tDEVICE\t{destination}",
        };
    }

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
