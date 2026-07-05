using MusicFileUtilities;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

/// <inheritdoc cref="ITagWriteService"/>
public sealed class TagWriteService : ITagWriteService
{
    public Task<BatchWriteResult> ApplyAsync(
        IReadOnlyList<string> paths,
        IReadOnlyList<TagEdit> edits,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() =>
        {
            var results = new List<FileWriteResult>(paths.Count);
            int done = 0;
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(ApplyToFile(path, edits));
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
