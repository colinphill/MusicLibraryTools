using System.Security.Cryptography;
using System.Text;
using iTunes.Binary;
using MusicFileUtilities;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

public enum ItunesMediaMutationKind
{
    Refresh,
    Relocate,
    Add,
    Remove,
}

public sealed record ItunesMediaMutation(
    ItunesMediaMutationKind Kind,
    string? OriginalPath,
    string? CurrentPath,
    string? ReferencePath = null)
{
    public static ItunesMediaMutation Refresh(
        string path,
        string? referencePath = null) =>
        new(
            ItunesMediaMutationKind.Refresh,
            path,
            path,
            referencePath);

    public static ItunesMediaMutation Relocate(string originalPath, string currentPath) =>
        new(ItunesMediaMutationKind.Relocate, originalPath, currentPath);

    public static ItunesMediaMutation Add(
        string path,
        string? referencePath = null) =>
        new(
            ItunesMediaMutationKind.Add,
            null,
            path,
            referencePath);

    public static ItunesMediaMutation Remove(string path) =>
        new(ItunesMediaMutationKind.Remove, path, null);
}

public sealed record ItunesMediaMutationResult(
    bool Active,
    int RefreshedTracks,
    int RelocatedTracks,
    int ImportedTracks,
    int RemovedTracks,
    string? LibraryPath,
    string? MediaFolder,
    string? RecoveryPath,
    IReadOnlyList<string> Warnings);

public sealed record ItunesMediaIndexedFile(
    string Path,
    long Length,
    DateTime LastWriteTimeUtc);

public enum ItunesMediaReconciliationIssueKind
{
    MissingTrackedFile,
    UntrackedFile,
    PossibleRename,
}

public sealed record ItunesMediaReconciliationIssue(
    ItunesMediaReconciliationIssueKind Kind,
    string Path,
    string Message,
    string? RelatedPath = null);

public sealed record ItunesMediaReconciliationResult(
    bool Configured,
    bool Applied,
    int ChangedFiles,
    int UpdatedTracks,
    string? LibraryPath,
    string? MediaFolder,
    IReadOnlyList<ItunesMediaReconciliationIssue> Issues,
    IReadOnlyList<string> Warnings,
    string? Error = null)
{
    public static ItunesMediaReconciliationResult NotConfigured { get; } =
        new(false, false, 0, 0, null, null, [], []);
}

public interface IItunesMediaMutationSession : IAsyncDisposable
{
    bool Active { get; }
    string? LibraryPath { get; }
    string? MediaFolder { get; }

    Task<ItunesMediaMutationResult> CommitAsync(
        IReadOnlyList<ItunesMediaMutation> mutations,
        CancellationToken ct = default);

    /// <summary>
    /// Marks the caller's surrounding filesystem transaction durable and removes recovery data.
    /// Disposing an applied session without completing it restores both media backups and the ITL.
    /// </summary>
    Task CompleteAsync(CancellationToken ct = default);
}

public interface IItunesMediaMutationService
{
    /// <summary>
    /// Starts an ITL-aware mutation transaction when any candidate path is inside the configured
    /// iTunes Media Folder. Existing in-scope files are backed up when
    /// <paramref name="backupFiles"/> is true, allowing an ITL save failure to restore them.
    /// </summary>
    Task<IItunesMediaMutationSession> BeginAsync(
        IReadOnlyCollection<string> candidatePaths,
        bool backupFiles,
        CancellationToken ct = default);

    /// <summary>
    /// Starts a transaction against an explicitly selected ITL. This is used by workflows that
    /// can load a legacy standalone configuration rather than the active application settings.
    /// </summary>
    Task<IItunesMediaMutationSession> BeginAsync(
        IReadOnlyCollection<string> candidatePaths,
        bool backupFiles,
        string? libraryPath,
        CancellationToken ct = default);

    /// <summary>
    /// Reconciles externally modified files already represented by both the metadata cache and the
    /// configured ITL. Additions, deletions, and rename candidates are reported for review because
    /// inferring identity from paths alone could destroy playlist or playback state.
    /// </summary>
    Task<ItunesMediaReconciliationResult> ReconcileAsync(
        IReadOnlyCollection<ItunesMediaIndexedFile> indexedFiles,
        IReadOnlyCollection<string> indexedRoots,
        CancellationToken ct = default);
}

/// <summary>
/// Couples local media mutations to the configured binary iTunes library. A session loads one ITL
/// document, serializes access to that library, and saves it once for the complete batch.
/// </summary>
public sealed class ItunesMediaMutationService : IItunesMediaMutationService
{
    private readonly IAppSettings? _settings;
    private readonly IMediaFormatRegistry _formats;
    private readonly SemaphoreSlim _libraryGate = new(1, 1);

    public ItunesMediaMutationService(
        IAppSettings? settings = null,
        IMediaFormatRegistry? formats = null)
    {
        _settings = settings;
        _formats = formats ?? MediaFormatRegistry.Default;
    }

    public async Task<ItunesMediaReconciliationResult> ReconcileAsync(
        IReadOnlyCollection<ItunesMediaIndexedFile> indexedFiles,
        IReadOnlyCollection<string> indexedRoots,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(indexedFiles);
        ArgumentNullException.ThrowIfNull(indexedRoots);
        LibraryConfiguration? configuration = _settings?.GetSnapshot().Configuration;
        if (configuration?.ItunesLibraryPath is not { } configuredLibrary)
            return ItunesMediaReconciliationResult.NotConfigured;

        string libraryPath = ItlFileEditor.ResolveLibraryPath(configuredLibrary);
        try
        {
            if (!File.Exists(libraryPath))
                throw new FileNotFoundException(
                    "The configured iTunes library is unavailable.", libraryPath);

            ItlLibrary library = await Task.Run(() => ItlLibrary.Load(libraryPath), ct)
                .ConfigureAwait(false);
            string mediaFolder = library.MusicFolderPath is { Length: > 0 } folder
                ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))
                : throw new InvalidDataException(
                    "The configured iTunes library does not contain a Media Folder path.");

            Dictionary<string, ItunesMediaIndexedFile> indexed = indexedFiles
                .Where(file => IsWithin(file.Path, mediaFolder) && IsMusicPath(file.Path))
                .GroupBy(file => Path.GetFullPath(file.Path), PathComparer)
                .ToDictionary(group => group.Key, group => group.First(), PathComparer);
            Dictionary<string, ItlTrack[]> tracked = library.Tracks
                .Where(track => track.LocalPath is { } path &&
                                IsWithin(path, mediaFolder) && IsMusicPath(path))
                .GroupBy(track => Path.GetFullPath(track.LocalPath!), PathComparer)
                .ToDictionary(group => group.Key, group => group.ToArray(), PathComparer);

            string[] refreshPaths = tracked
                .Where(pair => indexed.TryGetValue(pair.Key, out ItunesMediaIndexedFile? file) &&
                               pair.Value.Any(track => FileChanged(track, file)))
                .Select(pair => pair.Key)
                .ToArray();

            string[] normalizedRoots = indexedRoots
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Select(Path.GetFullPath)
                .Distinct(PathComparer)
                .ToArray();
            var missing = tracked.Where(pair =>
                    normalizedRoots.Any(root => IsWithin(pair.Key, root)) &&
                    !indexed.ContainsKey(pair.Key) &&
                    !File.Exists(pair.Key))
                .ToArray();
            var untracked = indexed.Where(pair => !tracked.ContainsKey(pair.Key)).ToArray();
            var issues = new List<ItunesMediaReconciliationIssue>();
            var pairedUntracked = new HashSet<string>(PathComparer);
            foreach (KeyValuePair<string, ItlTrack[]> oldFile in missing)
            {
                KeyValuePair<string, ItunesMediaIndexedFile>[] candidates = untracked
                    .Where(candidate => !pairedUntracked.Contains(candidate.Key) &&
                                        oldFile.Value.Any(track =>
                                            track.Size == (ulong)Math.Max(0, candidate.Value.Length)))
                    .Take(2)
                    .ToArray();
                if (candidates.Length == 1)
                {
                    pairedUntracked.Add(candidates[0].Key);
                    issues.Add(new(ItunesMediaReconciliationIssueKind.PossibleRename,
                        oldFile.Key,
                        $"A missing iTunes track and a new cached file have the same size; review this possible rename before changing track identity.",
                        candidates[0].Key));
                }
                else
                {
                    issues.Add(new(ItunesMediaReconciliationIssueKind.MissingTrackedFile,
                        oldFile.Key,
                        "The file is tracked by iTunes but is missing from the indexed Media Folder."));
                }
            }
            foreach (KeyValuePair<string, ItunesMediaIndexedFile> file in untracked)
            {
                if (!pairedUntracked.Contains(file.Key))
                    issues.Add(new(ItunesMediaReconciliationIssueKind.UntrackedFile,
                        file.Key,
                        "The indexed file is inside the iTunes Media Folder but has no iTunes track."));
            }

            if (refreshPaths.Length == 0)
                return new(true, false, 0, 0, libraryPath, mediaFolder, issues, []);

            await using IItunesMediaMutationSession session =
                await BeginAsync(refreshPaths, backupFiles: false, ct).ConfigureAwait(false);
            ItunesMediaMutationResult result = await session.CommitAsync(
                refreshPaths
                    .Select(path =>
                        ItunesMediaMutation.Refresh(path))
                    .ToArray(),
                ct)
                .ConfigureAwait(false);
            await session.CompleteAsync(ct).ConfigureAwait(false);
            return new(true, result.Active, refreshPaths.Length, result.RefreshedTracks,
                libraryPath, mediaFolder, issues, result.Warnings);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return new(true, false, 0, 0, libraryPath, null, [], [], error.Message);
        }
    }

    public async Task<IItunesMediaMutationSession> BeginAsync(
        IReadOnlyCollection<string> candidatePaths,
        bool backupFiles,
        CancellationToken ct = default)
        => await BeginAsync(candidatePaths, backupFiles, libraryPath: null, ct)
            .ConfigureAwait(false);

    public async Task<IItunesMediaMutationSession> BeginAsync(
        IReadOnlyCollection<string> candidatePaths,
        bool backupFiles,
        string? libraryPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidatePaths);
        string? configuredLibrary = string.IsNullOrWhiteSpace(libraryPath)
            ? _settings?.GetSnapshot().Configuration?.ItunesLibraryPath
            : libraryPath;
        if (string.IsNullOrWhiteSpace(configuredLibrary))
            return InactiveSession.Instance;

        string resolvedLibraryPath = ItlFileEditor.ResolveLibraryPath(configuredLibrary);
        if (!File.Exists(resolvedLibraryPath))
            throw new FileNotFoundException(
                "The configured iTunes library is unavailable; media changes cannot be synchronized.",
                resolvedLibraryPath);

        string[] normalizedCandidates = candidatePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();

        await _libraryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            FileSnapshot beforeRead = Capture(resolvedLibraryPath);
            byte[] libraryBytes = await File.ReadAllBytesAsync(resolvedLibraryPath, ct)
                .ConfigureAwait(false);
            FileSnapshot snapshot = Capture(resolvedLibraryPath);
            if (snapshot != beforeRead)
                throw new InvalidOperationException(
                    "The configured iTunes library changed while it was being loaded.");
            ItlEnvelope envelope = await Task.Run(() => ItlEnvelope.Parse(libraryBytes), ct)
                .ConfigureAwait(false);
            ItlLibrary library = await Task.Run(() => ItlLibrary.Parse(envelope), ct)
                .ConfigureAwait(false);
            string mediaFolder = library.MusicFolderPath is { Length: > 0 } folder
                ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))
                : throw new InvalidDataException(
                    "The configured iTunes library does not contain a Media Folder path.");
            if (!normalizedCandidates.Any(path => IsWithin(path, mediaFolder)))
            {
                _libraryGate.Release();
                return InactiveSession.Instance;
            }

            ItlFileEditor.EnsureItunesIsClosed();
            ItlDocument document = await Task.Run(() => ItlDocument.Parse(envelope), ct)
                .ConfigureAwait(false);
            string hash = Convert.ToHexString(SHA256.HashData(libraryBytes));
            string recoveryRoot = Path.Combine(
                resolvedLibraryPath + ".MusicLibraryItl-recovery",
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" +
                Guid.NewGuid().ToString("N")[..8]);
            var backups = new Dictionary<string, string>(PathComparer);
            if (backupFiles)
            {
                foreach (string path in normalizedCandidates.Where(path =>
                             IsWithin(path, mediaFolder) && File.Exists(path)))
                {
                    ct.ThrowIfCancellationRequested();
                    string backup = BackupPath(recoveryRoot, path);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    await CopyDurablyAsync(path, backup, ct).ConfigureAwait(false);
                    backups[path] = backup;
                }
            }
            return new Session(this, document, resolvedLibraryPath, mediaFolder, snapshot, hash,
                recoveryRoot, backups);
        }
        catch
        {
            _libraryGate.Release();
            throw;
        }
    }

    private sealed class Session : IItunesMediaMutationSession
    {
        private readonly ItunesMediaMutationService _owner;
        private readonly ItlDocument _document;
        private readonly FileSnapshot _librarySnapshot;
        private readonly string _libraryHash;
        private readonly string _recoveryRoot;
        private readonly IReadOnlyDictionary<string, string> _backups;
        private bool _applied;
        private bool _completed;
        private bool _disposed;

        public Session(
            ItunesMediaMutationService owner,
            ItlDocument document,
            string libraryPath,
            string mediaFolder,
            FileSnapshot librarySnapshot,
            string libraryHash,
            string recoveryRoot,
            IReadOnlyDictionary<string, string> backups)
        {
            _owner = owner;
            _document = document;
            LibraryPath = libraryPath;
            MediaFolder = mediaFolder;
            _librarySnapshot = librarySnapshot;
            _libraryHash = libraryHash;
            _recoveryRoot = recoveryRoot;
            _backups = backups;
        }

        public bool Active => true;
        public string LibraryPath { get; }
        public string MediaFolder { get; }

        public async Task<ItunesMediaMutationResult> CommitAsync(
            IReadOnlyList<ItunesMediaMutation> mutations,
            CancellationToken ct = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Session));
            if (_applied)
                throw new InvalidOperationException("The iTunes media mutation session is already applied.");
            ArgumentNullException.ThrowIfNull(mutations);

            try
            {
                ValidateSnapshot(LibraryPath, _librarySnapshot);
                if (!StringComparer.Ordinal.Equals(
                        await HashFileAsync(LibraryPath, ct).ConfigureAwait(false), _libraryHash))
                    throw new InvalidOperationException(
                        "The iTunes library changed during the media operation.");

                int refreshed = 0, relocated = 0, imported = 0, removed = 0;
                var warnings = new List<string>();
                foreach (ItunesMediaMutation mutation in mutations)
                {
                    ct.ThrowIfCancellationRequested();
                    if (mutation.ReferencePath is { } reference &&
                        _document.FindTracksByPath(reference).Count == 0)
                        continue;
                    switch (mutation.Kind)
                    {
                        case ItunesMediaMutationKind.Refresh:
                        {
                            string path = Required(mutation.CurrentPath, "refresh path");
                            if (!IsWithin(path, MediaFolder) || !File.Exists(path) ||
                                !_owner.IsMusicPath(path))
                                break;
                            IReadOnlyList<ItlRecord> matches = _document.FindTracksByPath(path);
                            ItlLocalTrackMetadata metadata = ReadMetadata(path);
                            FileInfo file = new(path);
                            if (matches.Count > 0)
                            {
                                foreach (ItlRecord track in matches)
                                    _document.RefreshLocalTrack(track, path, metadata, file.Length,
                                        file.LastWriteTimeUtc);
                                refreshed += matches.Count;
                                AddDuplicateWarning(matches, path, warnings);
                            }
                            break;
                        }

                        case ItunesMediaMutationKind.Relocate:
                        {
                            string original = Required(mutation.OriginalPath, "original path");
                            string current = Required(mutation.CurrentPath, "current path");
                            if (!IsWithin(original, MediaFolder) && !IsWithin(current, MediaFolder))
                                break;
                            IReadOnlyList<ItlRecord> matches = _document.RelocateTracks(original, current);
                            if (matches.Count == 0 && IsWithin(current, MediaFolder) &&
                                File.Exists(current) && _owner.IsMusicPath(current))
                            {
                                FileInfo file = new(current);
                                _document.ImportLocalTrack(current, ReadMetadata(current), file.Length,
                                    file.LastWriteTimeUtc);
                                imported++;
                            }
                            else
                            {
                                relocated += matches.Count;
                                AddDuplicateWarning(matches, original, warnings);
                                if (File.Exists(current) && _owner.IsMusicPath(current))
                                {
                                    FileInfo file = new(current);
                                    ItlLocalTrackMetadata metadata = ReadMetadata(current);
                                    foreach (ItlRecord track in matches)
                                        _document.RefreshLocalTrack(track, current, metadata,
                                            file.Length, file.LastWriteTimeUtc);
                                    refreshed += matches.Count;
                                }
                            }
                            break;
                        }

                        case ItunesMediaMutationKind.Add:
                        {
                            string path = Required(mutation.CurrentPath, "added path");
                            if (!IsWithin(path, MediaFolder) || !File.Exists(path) ||
                                !_owner.IsMusicPath(path))
                                break;
                            IReadOnlyList<ItlRecord> existing = _document.FindTracksByPath(path);
                            FileInfo file = new(path);
                            ItlLocalTrackMetadata metadata = ReadMetadata(path);
                            if (existing.Count == 0)
                            {
                                _document.ImportLocalTrack(path, metadata, file.Length,
                                    file.LastWriteTimeUtc);
                                imported++;
                            }
                            else
                            {
                                foreach (ItlRecord track in existing)
                                    _document.RefreshLocalTrack(track, path, metadata, file.Length,
                                        file.LastWriteTimeUtc);
                                refreshed += existing.Count;
                            }
                            break;
                        }

                        case ItunesMediaMutationKind.Remove:
                        {
                            string path = Required(mutation.OriginalPath, "removed path");
                            if (!IsWithin(path, MediaFolder))
                                break;
                            ItlRecord[] matches = [.. _document.FindTracksByPath(path)];
                            AddDuplicateWarning(matches, path, warnings);
                            foreach (ItlRecord track in matches)
                                if (_document.RemoveTrack(track.GetTrackId()))
                                    removed++;
                            break;
                        }
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }

                if (refreshed + relocated + imported + removed > 0)
                {
                    Directory.CreateDirectory(_recoveryRoot);
                    string libraryBackup = Path.Combine(_recoveryRoot, "iTunes Library.itl");
                    await CopyDurablyAsync(LibraryPath, libraryBackup, ct).ConfigureAwait(false);
                    await WriteJournalAsync(mutations, libraryBackup, ct).ConfigureAwait(false);

                    // Build once before replacing the live file so unsupported opaque-reference
                    // changes fail while the original library is still installed.
                    _ = ItlWriter.Build(_document.Envelope, _document.Serialize());
                    ItlFileEditor.SaveValidated(_document, LibraryPath);
                    _ = ItlDocument.Load(LibraryPath);
                }

                _applied = true;
                return new(true, refreshed, relocated, imported, removed, LibraryPath,
                    MediaFolder, null, warnings);
            }
            catch
            {
                await RestoreAsync().ConfigureAwait(false);
                throw;
            }
        }

        public Task CompleteAsync(CancellationToken ct = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Session));
            if (!_applied)
                throw new InvalidOperationException(
                    "Apply the iTunes media mutations before completing the transaction.");
            ct.ThrowIfCancellationRequested();
            _completed = true;
            CleanupSuccessfulRecovery();
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                if (!_completed)
                    await RestoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _owner._libraryGate.Release();
            }
        }

        private async Task RestoreAsync()
        {
            foreach ((string path, string backup) in _backups.Reverse())
            {
                if (!File.Exists(backup))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await RestoreAtomicallyAsync(backup, path).ConfigureAwait(false);
            }

            string libraryBackup = Path.Combine(_recoveryRoot, "iTunes Library.itl");
            if (File.Exists(libraryBackup))
                await RestoreAtomicallyAsync(libraryBackup, LibraryPath).ConfigureAwait(false);
        }

        private async Task WriteJournalAsync(
            IReadOnlyList<ItunesMediaMutation> mutations,
            string libraryBackup,
            CancellationToken ct)
        {
            string journalPath = Path.Combine(_recoveryRoot, "journal.tsv");
            await using var stream = new FileStream(journalPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteLineAsync($"BEGIN\t{Guid.NewGuid():N}".AsMemory(), ct);
            await writer.WriteLineAsync(
                $"BACKUP\tITL\t{LibraryPath}\t{libraryBackup}".AsMemory(), ct);
            foreach ((string path, string backup) in _backups)
                await writer.WriteLineAsync($"BACKUP\tMEDIA\t{path}\t{backup}".AsMemory(), ct);
            foreach (ItunesMediaMutation mutation in mutations)
                await writer.WriteLineAsync(
                    $"ITL\t{mutation.Kind}\t{mutation.OriginalPath}\t{mutation.CurrentPath}"
                        .AsMemory(), ct);
            await writer.FlushAsync(ct);
            stream.Flush(true);
        }

        private void CleanupSuccessfulRecovery()
        {
            try
            {
                if (Directory.Exists(_recoveryRoot))
                    Directory.Delete(_recoveryRoot, recursive: true);
                string? parent = Path.GetDirectoryName(_recoveryRoot);
                if (parent is not null && Directory.Exists(parent) &&
                    !Directory.EnumerateFileSystemEntries(parent).Any())
                    Directory.Delete(parent);
            }
            catch
            {
                // A successful media/ITL commit must not become a failure because recovery cleanup
                // was blocked by antivirus or a transient network-share handle.
            }
        }
    }

    private sealed class InactiveSession : IItunesMediaMutationSession
    {
        public static InactiveSession Instance { get; } = new();
        public bool Active => false;
        public string? LibraryPath => null;
        public string? MediaFolder => null;

        public Task<ItunesMediaMutationResult> CommitAsync(
            IReadOnlyList<ItunesMediaMutation> mutations,
            CancellationToken ct = default) =>
            Task.FromResult(new ItunesMediaMutationResult(
                false, 0, 0, 0, 0, null, null, null, []));

        public Task CompleteAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ItlLocalTrackMetadata ReadMetadata(string path)
    {
        IMediaFile media = MediaFile.GetFile(path, readOnly: true);
        IMetadataProvider tag = media.Tags.First();
        ICodecProvider codec = media.Codecs.First();
        Dictionary<TagFields, string> fields = tag.GetKnownMetadata()
            .GroupBy(item => item.Key)
            .ToDictionary(group => group.Key, group => group.First().Value);

        string? Value(TagFields field) =>
            fields.TryGetValue(field, out string? value) && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : null;
        int? Number(TagFields field) =>
            int.TryParse(Value(field), out int value) ? value : null;
        bool Flag(TagFields field) =>
            Value(field) is { } value &&
            (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("yes", StringComparison.OrdinalIgnoreCase));
        int? year = null;
        string? date = Value(TagFields.Date);
        if (date is { Length: >= 4 } && int.TryParse(date[..4], out int parsedYear))
            year = parsedYear;

        return new ItlLocalTrackMetadata
        {
            Title = Value(TagFields.Title) ?? tag.Title,
            Artist = Value(TagFields.Artist) ?? tag.Artist,
            AlbumArtist = Value(TagFields.AlbumArtist) ??
                          (tag.HasAlbumArtist ? tag.AlbumArtist : null),
            Album = Value(TagFields.Album) ?? tag.Album,
            Genre = Value(TagFields.Genre),
            Composer = Value(TagFields.Composer),
            Grouping = Value(TagFields.Grouping),
            Comment = Value(TagFields.Comment),
            SortTitle = Value(TagFields.TitleSort),
            SortArtist = Value(TagFields.ArtistSort),
            SortAlbumArtist = Value(TagFields.AlbumArtistSort),
            SortAlbum = Value(TagFields.AlbumSort),
            SortComposer = Value(TagFields.ComposerSort),
            Kind = KindFor(path, codec),
            TrackNumber = Number(TagFields.TrackNumber) ?? tag.TrackNumber,
            TrackCount = Number(TagFields.TotalTracks) ?? tag.TrackTotal,
            DiscNumber = Number(TagFields.DiscNumber) ?? tag.DiscNumber,
            DiscCount = Number(TagFields.TotalDiscs) ?? tag.DiscTotal,
            Year = year,
            Bpm = Number(TagFields.BPM),
            Duration = TimeSpan.FromSeconds(codec.DurationInSeconds),
            BitRateKbps = checked((int)Math.Round(codec.AverageBitrate / 1000d)),
            ArtworkCount = media.Tags.Sum(candidate => candidate.GetImageMetadata().Count()),
            Compilation = Flag(TagFields.Compilation),
            Gapless = Flag(TagFields.Gapless),
        };
    }

    private static string KindFor(string path, ICodecProvider codec) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".m4a" when codec.CodecType == CodecType.Lossless => "Apple Lossless audio file",
            ".m4a" => "AAC audio file",
            ".mp4" => "MPEG-4 audio file",
            ".m4p" => "Protected AAC audio file",
            ".m4r" => "AAC audio file",
            ".m4b" => "Audiobook",
            ".m4v" => "MPEG-4 video file",
            ".mp3" => "MPEG audio file",
            _ => codec.CodecName,
        };

    private static void AddDuplicateWarning(
        IReadOnlyCollection<ItlRecord> records,
        string path,
        ICollection<string> warnings)
    {
        if (records.Count > 1)
            warnings.Add(
                $"{records.Count} iTunes track records reference '{path}'; all were updated.");
    }

    private static bool IsWithin(string path, string root)
    {
        string normalizedPath = Path.GetFullPath(path);
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return PathComparer.Equals(normalizedPath, normalizedRoot) ||
               normalizedPath.StartsWith(
                   normalizedRoot + Path.DirectorySeparatorChar,
                   PathComparison);
    }

    private bool IsMusicPath(string path) =>
        _formats.SupportsPath(path, MediaFormatCapabilities.ReadMetadata);

    private static bool FileChanged(ItlTrack track, ItunesMediaIndexedFile file)
    {
        if (track.Size != (ulong)Math.Max(0, file.Length))
            return true;
        DateTime? modified = track.DateModified;
        return modified is null ||
               Math.Abs((modified.Value - file.LastWriteTimeUtc.ToUniversalTime()).TotalSeconds) > 2;
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"An iTunes media mutation is missing its {name}.")
            : Path.GetFullPath(value);

    private static FileSnapshot Capture(string path)
    {
        FileInfo info = new(path);
        return new(info.Length, info.LastWriteTimeUtc);
    }

    private static void ValidateSnapshot(string path, FileSnapshot expected)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.Length != expected.Length ||
            info.LastWriteTimeUtc != expected.LastWriteTimeUtc)
            throw new InvalidOperationException(
                $"The configured iTunes library changed during the operation: {path}");
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static async Task CopyDurablyAsync(string source, string destination, CancellationToken ct)
    {
        await using (FileStream input = new(source, FileMode.Open, FileAccess.Read,
                         FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
        await using (FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write,
                         FileShare.None, 64 * 1024, FileOptions.WriteThrough))
        {
            await input.CopyToAsync(output, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
            output.Flush(true);
        }
    }

    private static async Task RestoreAtomicallyAsync(string backup, string destination)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.restore");
        try
        {
            await using (FileStream input = new(backup, FileMode.Open, FileAccess.Read,
                             FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
            await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
                output.Flush(true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static string BackupPath(string recoveryRoot, string path)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path)));
        return Path.Combine(recoveryRoot, "media", Convert.ToHexString(hash),
            Path.GetFileName(path));
    }

    private sealed record FileSnapshot(long Length, DateTime LastWriteTimeUtc);

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
