using MusicFileUtilities;
using MusicLibrary.Core.Models;
using System.Runtime.ExceptionServices;

namespace MusicLibrary.Core.Services;

/// <inheritdoc cref="ITagWriteService"/>
public sealed class TagWriteService : ITagWriteService
{
    private readonly IReindexService? _reindex;
    private readonly IFileMutationCoordinator _mutations;
    private readonly int _maxParallelism;
    private readonly IItunesMediaMutationService? _itunes;

    // The reindex service is optional so this service can be constructed standalone (unit tests).
    public TagWriteService(
        IReindexService? reindex = null,
        IFileMutationCoordinator? mutations = null,
        int maxParallelism = 4,
        IItunesMediaMutationService? itunes = null)
    {
        _reindex = reindex;
        _mutations = mutations ?? FileMutationCoordinator.Shared;
        _maxParallelism = Math.Clamp(maxParallelism, 1, 16);
        _itunes = itunes;
    }

    public Task<BatchWriteResult> ApplyAsync(
        IReadOnlyList<string> paths,
        IReadOnlyList<TagEdit> edits,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
        => Task.Run(async () =>
        {
            var results = new FileWriteResult[paths.Count];
            var savedFiles = new IMediaFile?[paths.Count];
            int nextIndex = -1;
            int done = 0;
            string[] mutationPaths = paths.Distinct(PathComparer).ToArray();
            using IDisposable lease = await _mutations.AcquireAsync(mutationPaths, ct);
            await using IItunesMediaMutationSession? itunesSession = _itunes is null
                ? null
                : await _itunes.BeginAsync(mutationPaths, backupFiles: true, ct);

            async Task Worker()
            {
                while (true)
                {
                    int index = Interlocked.Increment(ref nextIndex);
                    if (index >= paths.Count)
                        return;

                    ct.ThrowIfCancellationRequested();
                    string path = paths[index];
                    var applied = ApplyToFile(path, edits);
                    results[index] = applied.Result;
                    savedFiles[index] = applied.SavedFile;
                    progress?.Report(Interlocked.Increment(ref done));
                }
            }

            int workerCount = Math.Min(_maxParallelism, paths.Count);
            Exception? workerError = null;
            try
            {
                await Task.WhenAll(Enumerable.Range(0, workerCount).Select(_ => Worker()));
            }
            catch (Exception ex)
            {
                // Files committed before cancellation/failure must still reach the cache.
                workerError = ex;
            }

            if (itunesSession is not null)
            {
                ItunesMediaMutation[] mutations = results
                    .Select((result, index) => (result, index))
                    .Where(item => item.result?.Outcome == WriteOutcome.Saved)
                    .Select(item => ItunesMediaMutation.Refresh(paths[item.index]))
                    .ToArray();
                await itunesSession.CommitAsync(mutations, CancellationToken.None);
                await itunesSession.CompleteAsync(CancellationToken.None);
            }

            if (_reindex is not null)
            {
                var changed = savedFiles
                    .Select((file, index) => (file, index))
                    .Where(item => item.file is not null)
                    .ToArray();
                foreach (var batch in changed.Chunk(32))
                {
                    try
                    {
                        await _reindex.ReindexFilesAsync(
                            batch.Select(item => (paths[item.index], item.file!)).ToArray(),
                            CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        foreach (var item in batch)
                            results[item.index] = results[item.index] with { CacheError = ex.Message };
                    }
                }
            }

            if (workerError is not null)
                ExceptionDispatchInfo.Capture(workerError).Throw();
            return new BatchWriteResult(results);
        }, ct);

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed record ApplyResult(FileWriteResult Result, IMediaFile? SavedFile = null);

    private static ApplyResult ApplyToFile(string path, IReadOnlyList<TagEdit> edits)
    {
        try
        {
            var file = MediaFile.GetFile(path);

            // The writer may live on the IMediaFile itself (MP3/FLAC/MP4/WavPack all implement
            // IMetadataWriter at the file level — WavPack's writer isn't the tag object but the file,
            // which creates the APEv2 tag on demand) or, for Ogg, only on the VorbisComments base
            // (OggVorbisFile doesn't advertise IMetadataWriter even though it can write). Prefer the
            // file so formats whose tag object isn't a writer still work.
            Action<TagFields, string?> setField;
            if (file is IMetadataWriter fileWriter)
                setField = (f, v) => fileWriter.SetField(f, v);
            else if (file is VorbisComments fileVorbis)
                setField = (f, v) => fileVorbis.SetField(f, v);
            else if (file.Tags.FirstOrDefault() is IMetadataWriter tagWriter)
                setField = (f, v) => tagWriter.SetField(f, v);
            else if (file.Tags.FirstOrDefault() is VorbisComments tagVorbis)
                setField = (f, v) => tagVorbis.SetField(f, v);
            else
                return new(new FileWriteResult { Path = path, Outcome = WriteOutcome.Failed, Error = "Tag format is read-only." });

            IUserStringMetadata? userStrings =
                file as IUserStringMetadata ??
                file.Tags.OfType<IUserStringMetadata>().FirstOrDefault();

            var unsupported = new List<TagFields>();
            int applied = 0;
            var provider = file.Tags.First();
            foreach (var edit in edits.Where(edit => edit.TargetId3Version is not null))
            {
                ID3v2Tag? id3 =
                    file as ID3v2Tag ??
                    file.Tags.OfType<ID3v2Tag>().FirstOrDefault();
                if (id3 is null)
                    return new(new FileWriteResult
                    {
                        Path = path,
                        Outcome = WriteOutcome.Failed,
                        Error = "The file does not contain a writable ID3v2 tag.",
                    });
                if (id3.Version == (int)edit.TargetId3Version!.Value)
                    continue;
                id3.ChangeVersion(edit.TargetId3Version.Value);
                applied++;
            }
            foreach (var edit in edits)
            {
                if (edit.TargetId3Version is not null)
                    continue;
                try
                {
                    if (edit.IsUserString)
                    {
                        if (userStrings is null)
                            return new(new FileWriteResult
                            {
                                Path = path,
                                Outcome = WriteOutcome.Failed,
                                Error = "This tag format does not support user-defined text fields.",
                            });

                        string key = edit.UserStringKey!.Trim();
                        string[] existingUserValues = userStrings.GetUserStrings()
                            .Where(item => string.Equals(
                                item.Key, key, StringComparison.OrdinalIgnoreCase))
                            .Select(item => item.Value)
                            .ToArray();
                        if (edit.Value is null
                                ? existingUserValues.Length == 0
                                : existingUserValues.Length == 1 &&
                                  existingUserValues[0] == edit.Value)
                            continue;

                        if (edit.Value is null)
                            userStrings.RemoveUserString(key);
                        else
                            userStrings.SetUserString(key, edit.Value);
                        applied++;
                        continue;
                    }

                    // Avoid a network write when the requested normalized value is already the
                    // file's sole value (or the requested removal is already absent).
                    var existing = provider.GetKnownMetadata()
                        .Where(kv => kv.Key == edit.Field)
                        .Select(kv => kv.Value)
                        .ToArray();
                    if (edit.Value is null ? existing.Length == 0 : existing.Length == 1 && existing[0] == edit.Value)
                        continue;

                    // SetField(field, null) removes the field.
                    setField(edit.Field, edit.Value);
                    applied++;
                }
                catch (ArgumentException)
                {
                    // Field can't be represented in this tag format — record and keep going.
                    unsupported.Add(edit.Field);
                }
            }

            if (applied == 0)
            {
                return new(new FileWriteResult
                {
                    Path = path,
                    Outcome = WriteOutcome.Skipped,
                    UnsupportedFields = unsupported,
                });
            }

            file.SaveTags();
            return new(new FileWriteResult
            {
                Path = path,
                Outcome = WriteOutcome.Saved,
                UnsupportedFields = unsupported,
            }, file);
        }
        catch (Exception ex)
        {
            return new(new FileWriteResult { Path = path, Outcome = WriteOutcome.Failed, Error = ex.Message });
        }
    }
}
