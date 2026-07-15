using MusicFileUtilities;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

/// <inheritdoc cref="ITagWriteService"/>
public sealed class TagWriteService : ITagWriteService
{
    private readonly IReindexService? _reindex;
    private readonly IFileMutationCoordinator _mutations;
    private readonly int _maxParallelism;

    // The reindex service is optional so this service can be constructed standalone (unit tests).
    public TagWriteService(
        IReindexService? reindex = null,
        IFileMutationCoordinator? mutations = null,
        int maxParallelism = 4)
    {
        _reindex = reindex;
        _mutations = mutations ?? FileMutationCoordinator.Shared;
        _maxParallelism = Math.Clamp(maxParallelism, 1, 16);
    }

    public Task<BatchWriteResult> ApplyAsync(
        IReadOnlyList<string> paths,
        IReadOnlyList<TagEdit> edits,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
        => Task.Run(async () =>
        {
            var results = new FileWriteResult[paths.Count];
            int nextIndex = -1;
            int done = 0;

            async Task Worker()
            {
                while (true)
                {
                    int index = Interlocked.Increment(ref nextIndex);
                    if (index >= paths.Count)
                        return;

                    ct.ThrowIfCancellationRequested();
                    string path = paths[index];
                    using var mutation = await _mutations.AcquireAsync(path, ct);
                    var applied = ApplyToFile(path, edits);
                    var result = applied.Result;
                    // Once the disk commit succeeds, cancellation must not strand a stale cache or
                    // turn that committed write into an apparent failure. Cache errors are separate.
                    if (result.Outcome == WriteOutcome.Saved && applied.SavedFile is not null && _reindex is not null)
                    {
                        try
                        {
                            await _reindex.ReindexFileAsync(path, applied.SavedFile, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            result = result with { CacheError = ex.Message };
                        }
                    }
                    results[index] = result;
                    progress?.Report(Interlocked.Increment(ref done));
                }
            }

            int workerCount = Math.Min(_maxParallelism, paths.Count);
            await Task.WhenAll(Enumerable.Range(0, workerCount).Select(_ => Worker()));
            return new BatchWriteResult(results);
        }, ct);

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

            var unsupported = new List<TagFields>();
            int applied = 0;
            var provider = file.Tags.First();
            foreach (var edit in edits)
            {
                try
                {
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
