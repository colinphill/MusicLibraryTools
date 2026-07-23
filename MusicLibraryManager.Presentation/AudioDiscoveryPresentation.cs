using System.Collections.Immutable;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

public sealed record AudioDiscoveryRow(
    string Path,
    double? DurationSeconds,
    string? Fingerprint,
    Guid? AcoustId,
    double? Score,
    ImmutableArray<Guid> MusicBrainzRecordingIdValues,
    string Status)
{
    public string File => System.IO.Path.GetFileName(Path);
    public string Duration => DurationSeconds is null
        ? ""
        : TimeSpan.FromSeconds(DurationSeconds.Value).ToString(@"h\:mm\:ss");
    public string Confidence => Score is null ? "" : $"{Score:P1}";
    public string MusicBrainzRecordingIds =>
        string.Join(", ", MusicBrainzRecordingIdValues);
}

public static class AudioDiscoveryRows
{
    public static IEnumerable<AudioDiscoveryRow> Create(
        AcoustIdDiscoveryResult result)
    {
        foreach (AcoustIdFileDiscovery file in result.Files)
        {
            AcoustIdCandidate[] candidates =
                file.Lookup?.Candidates.ToArray() ?? [];
            if (candidates.Length == 0)
            {
                yield return new(
                    file.Path,
                    file.Fingerprint?.Duration.TotalSeconds,
                    file.Fingerprint?.Fingerprint,
                    null,
                    null,
                    [],
                    file.Issues.FirstOrDefault()?.Message ?? "No AcoustID match");
                continue;
            }
            foreach (AcoustIdCandidate candidate in candidates)
                yield return new(
                    file.Path,
                    file.Fingerprint?.Duration.TotalSeconds,
                    file.Fingerprint?.Fingerprint,
                    candidate.AcoustId,
                    candidate.Score,
                    candidate.MusicBrainzRecordingIds,
                    candidate.MusicBrainzRecordingIds.Length == 0
                        ? "Candidate has no MusicBrainz recording ID"
                        : "Candidate");
        }
    }

    public static OperationRecipe CreateTagRecipe(AudioDiscoveryRow row)
    {
        if (row.AcoustId is null || string.IsNullOrWhiteSpace(row.Fingerprint))
            throw new InvalidOperationException("Select a matched AcoustID candidate.");
        var operations = new List<MetadataOperation>
        {
            new AssignFieldOperation(
                MetadataFieldKey.Known(TagFields.AcoustID_Fingerprint),
                row.Fingerprint),
            new AssignFieldOperation(
                MetadataFieldKey.Known(TagFields.AcoustID_ID),
                row.AcoustId.Value.ToString()),
        };
        if (row.MusicBrainzRecordingIdValues.Length == 1)
            operations.Add(new AssignFieldOperation(
                MetadataFieldKey.Known(TagFields.MusicBrainz_RecordingID),
                row.MusicBrainzRecordingIdValues[0].ToString()));
        return OperationRecipe.Create(
            $"Audio identifiers: {row.File}", [.. operations]);
    }
}
