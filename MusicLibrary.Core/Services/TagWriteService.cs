using MusicFileUtilities;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

/// <inheritdoc cref="ITagWriteService"/>
public sealed class TagWriteService : ITagWriteService
{
    private readonly IReindexService? _reindex;
    private readonly IFileMutationCoordinator _mutations;

    // The reindex service is optional so this service can be constructed standalone (unit tests).
    public TagWriteService(
        IReindexService? reindex = null,
        IFileMutationCoordinator? mutations = null)
    {
        _reindex = reindex;
        _mutations = mutations ?? FileMutationCoordinator.Shared;
    }

    public Task<BatchWriteResult> ApplyAsync(
        IReadOnlyList<string> paths,
        IReadOnlyList<TagEdit> edits,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
        => Task.Run(async () =>
        {
            var results = new List<FileWriteResult>(paths.Count);
            int done = 0;
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                using var mutation = await _mutations.AcquireAsync(path, ct);
                var result = ApplyToFile(path, edits);
                // Once the disk commit succeeds, cancellation must not strand a stale cache or turn
                // that committed write into an apparent failure. Cache errors are reported separately.
                if (result.Outcome == WriteOutcome.Saved && _reindex is not null)
                {
                    try
                    {
                        await _reindex.ReindexFileAsync(path, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        result = result with { CacheError = ex.Message };
                    }
                }
                results.Add(result);
                progress?.Report(++done);
            }
            return new BatchWriteResult(results);
        }, ct);

    private static FileWriteResult ApplyToFile(string path, IReadOnlyList<TagEdit> edits)
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
                return new FileWriteResult { Path = path, Outcome = WriteOutcome.Failed, Error = "Tag format is read-only." };

            var unsupported = new List<TagFields>();
            int applied = 0;
            foreach (var edit in edits)
            {
                try
                {
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
                return new FileWriteResult
                {
                    Path = path,
                    Outcome = WriteOutcome.Skipped,
                    UnsupportedFields = unsupported,
                };
            }

            file.SaveTags();
            return new FileWriteResult
            {
                Path = path,
                Outcome = WriteOutcome.Saved,
                UnsupportedFields = unsupported,
            };
        }
        catch (Exception ex)
        {
            return new FileWriteResult { Path = path, Outcome = WriteOutcome.Failed, Error = ex.Message };
        }
    }
}
