using System.Text.RegularExpressions;
using iTunes.Binary;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public sealed record ItunesValidationResult(
    string LibraryPath,
    IReadOnlyList<ItlValidationIssue> Issues,
    int ErrorCount,
    int WarningCount)
{
    public bool IsValid => ErrorCount == 0;
}

public interface IItunesValidationService
{
    Task<ItunesValidationResult> ValidateAsync(string libraryPath,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
}

public sealed class ItunesValidationService : IItunesValidationService
{
    public Task<ItunesValidationResult> ValidateAsync(string libraryPath,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);
        string resolved = ItlFileEditor.ResolveLibraryPath(libraryPath);
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(OperationPhase.LoadingLibrary, CurrentPath: resolved,
                Message: "Loading iTunes library document"));
            ItlDocument document = ItlDocument.Load(resolved);
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<ItlValidationIssue> issues = document.Validate();
            ct.ThrowIfCancellationRequested();
            int errors = issues.Count(issue => issue.Severity == ItlValidationSeverity.Error);
            int warnings = issues.Count(issue => issue.Severity == ItlValidationSeverity.Warning);
            progress?.Report(new(OperationPhase.Completed, issues.Count, issues.Count,
                resolved, $"Validation completed: {errors:N0} errors, {warnings:N0} warnings"));
            return new ItunesValidationResult(resolved, issues, errors, warnings);
        }, ct);
    }
}

public sealed record RedundancyTrack(
    int TrackId,
    string Artist,
    string Title,
    string Album,
    string Path);

public sealed record RedundancyGroup(
    string NormalizedArtist,
    string NormalizedTitle,
    IReadOnlyList<RedundancyTrack> Tracks);

public sealed record RedundancyAnalysisResult(
    string LibraryPath,
    int ScannedTrackCount,
    IReadOnlyList<RedundancyGroup> Groups);

public interface IRedundancyAnalysisService
{
    Task<RedundancyAnalysisResult> AnalyzeAsync(string? libraryPath = null,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
}

public sealed class RedundancyAnalysisService : IRedundancyAnalysisService
{
    private static readonly Regex VersionSuffix = new(@"^(.*)[ \t]+\(.*\)[ \t]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public Task<RedundancyAnalysisResult> AnalyzeAsync(string? libraryPath = null,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        string resolved = ItlFileEditor.ResolveLibraryPath(libraryPath);
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(OperationPhase.LoadingLibrary, CurrentPath: resolved,
                Message: "Loading iTunes library"));
            ItlLibrary library = ItlLibrary.Load(resolved);
            var groups = new Dictionary<(string Artist, string Title), List<RedundancyTrack>>();
            int scanned = 0;
            foreach (ItlTrack track in library.Tracks)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(track.LocalPath))
                    continue;
                string artist = (track.Artist ?? "").Trim().ToUpperInvariant();
                string title = track.Title ?? "";
                Match match = VersionSuffix.Match(title);
                string baseTitle = (match.Success ? match.Groups[1].Value : title)
                    .Trim().ToUpperInvariant();
                var key = (artist, baseTitle);
                if (!groups.TryGetValue(key, out List<RedundancyTrack>? tracks))
                    groups[key] = tracks = [];
                tracks.Add(new(track.Id, track.Artist ?? "", track.Title ?? "",
                    track.Album ?? "", track.LocalPath));
                scanned++;
                if ((scanned & 511) == 0)
                    progress?.Report(new(OperationPhase.Planning, scanned,
                        CurrentPath: track.LocalPath,
                        Message: $"Analyzed {scanned:N0} tracks"));
            }

            IReadOnlyList<RedundancyGroup> redundancies = groups
                .Where(pair => pair.Value.Count > 1)
                .OrderBy(pair => pair.Key.Artist, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.Title, StringComparer.Ordinal)
                .Select(pair => new RedundancyGroup(pair.Key.Artist, pair.Key.Title,
                    pair.Value.OrderBy(track => track.Album, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(track => track.Path, PathComparer).ToArray()))
                .ToArray();
            progress?.Report(new(OperationPhase.Completed, scanned, scanned, resolved,
                $"Found {redundancies.Count:N0} redundancy groups"));
            return new RedundancyAnalysisResult(resolved, scanned, redundancies);
        }, ct);
    }

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
