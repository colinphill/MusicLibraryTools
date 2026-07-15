using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MusicLibrary.Core.Models;

namespace MusicLibrary.App.ViewModels;

/// <summary>What the user has decided to do with an analysis finding.</summary>
public enum AnalysisFindingDisposition
{
    Active,
    Completed,
    Deferred,
    Ignored,
}

/// <summary>
/// An immutable analysis result snapshot. The contained finding and action view models remain
/// mutable so review state survives while the user navigates between runs.
/// </summary>
public sealed class AnalysisRunViewModel : ViewModelBase
{
    public string Name { get; }
    public string Summary { get; }
    public DateTimeOffset CreatedAt { get; }
    public AnalysisResultView View { get; }
    public IReadOnlyList<AnalysisProblemGroupViewModel> FindingGroups { get; }
    public IReadOnlyList<DuplicateGroup> Duplicates { get; }
    public IReadOnlyList<ArtistGroupViewModel> ArtistGroups { get; }
    public IReadOnlyList<AnalysisConflictGroupViewModel> ConflictGroups { get; }
    public IReadOnlyList<AnalysisRepairItemViewModel> RepairItems { get; }
    public IReadOnlyList<AlbumMetadataMatrix> Matrices { get; }
    public AnalysisRepairPlan? RepairPlan { get; }
    public int Count { get; }

    public int ActiveFindingCount => FindingGroups.Sum(group => group.ActiveCount);
    public string DisplayLabel => $"{Name} · {Count:N0} · {CreatedAt:HH:mm:ss}";

    private AnalysisRunViewModel(
        string name,
        string summary,
        AnalysisResultView view,
        int count,
        IReadOnlyList<AnalysisProblemGroupViewModel>? findingGroups = null,
        IReadOnlyList<DuplicateGroup>? duplicates = null,
        IReadOnlyList<ArtistGroupViewModel>? artistGroups = null,
        IReadOnlyList<AnalysisConflictGroupViewModel>? conflictGroups = null,
        IReadOnlyList<AnalysisRepairItemViewModel>? repairItems = null,
        AnalysisRepairPlan? repairPlan = null,
        IReadOnlyList<AlbumMetadataMatrix>? matrices = null)
    {
        Name = name;
        Summary = summary;
        View = view;
        Count = count;
        CreatedAt = DateTimeOffset.Now;
        FindingGroups = findingGroups ?? [];
        Duplicates = duplicates ?? [];
        ArtistGroups = artistGroups ?? [];
        ConflictGroups = conflictGroups ?? [];
        RepairItems = repairItems ?? [];
        RepairPlan = repairPlan;
        Matrices = matrices ?? [];

        foreach (var group in FindingGroups)
            group.PropertyChanged += FindingGroupChanged;
    }

    public static AnalysisRunViewModel ForFindings(
        AnalysisReport report,
        IReadOnlyList<TrackRecord> records,
        string summary) =>
        new(report.Name, summary, AnalysisResultView.Findings, report.Count,
            findingGroups: AnalysisProblemGroupViewModel.Build(report.Findings, records));

    public static AnalysisRunViewModel ForDuplicates(
        string name,
        IReadOnlyList<DuplicateGroup> groups,
        string summary) =>
        new(name, summary, AnalysisResultView.Duplicates, groups.Count, duplicates: groups);

    public static AnalysisRunViewModel ForArtists(
        string name,
        IReadOnlyList<ArtistGroupViewModel> groups,
        string summary) =>
        new(name, summary, AnalysisResultView.Artists, groups.Count, artistGroups: groups);

    public static AnalysisRunViewModel ForConflicts(
        IReadOnlyList<AnalysisConflictGroupViewModel> groups,
        string summary) =>
        new("Album artist conflicts", summary, AnalysisResultView.Conflicts, groups.Count,
            conflictGroups: groups);

    public static AnalysisRunViewModel ForRepairs(
        AnalysisRepairPlan plan,
        IReadOnlyList<AnalysisRepairItemViewModel> items,
        string summary) =>
        new(plan.Name, summary, AnalysisResultView.Repairs, items.Count,
            repairItems: items, repairPlan: plan);

    public static AnalysisRunViewModel ForMatrices(
        IReadOnlyList<AlbumMetadataMatrix> matrices,
        string summary) =>
        new("Album metadata matrix", summary, AnalysisResultView.Matrix, matrices.Count,
            matrices: matrices);

    private void FindingGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AnalysisProblemGroupViewModel.ActiveCount))
            OnPropertyChanged(nameof(ActiveFindingCount));
    }
}

/// <summary>All findings for one problem, divided into albums.</summary>
public sealed class AnalysisProblemGroupViewModel : ViewModelBase
{
    public string Problem { get; }
    public IReadOnlyList<AnalysisAlbumGroupViewModel> Albums { get; }
    public int Count => Albums.Sum(album => album.Count);
    public int ActiveCount => Albums.Sum(album => album.ActiveCount);

    private AnalysisProblemGroupViewModel(string problem, IReadOnlyList<AnalysisAlbumGroupViewModel> albums)
    {
        Problem = problem;
        Albums = albums;
        foreach (var album in Albums)
            album.PropertyChanged += AlbumChanged;
    }

    public static IReadOnlyList<AnalysisProblemGroupViewModel> Build(
        IReadOnlyList<AnalysisFinding> findings,
        IReadOnlyList<TrackRecord> records)
    {
        var recordsByPath = records
            .GroupBy(record => record.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return findings
            .Select(finding => new AnalysisFindingViewModel(
                finding,
                AlbumLabel(finding.Path, recordsByPath.GetValueOrDefault(finding.Path))))
            .GroupBy(item => item.Problem, StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new AnalysisProblemGroupViewModel(
                group.Key,
                group.GroupBy(item => item.Album, StringComparer.CurrentCultureIgnoreCase)
                    .OrderBy(album => album.Key, StringComparer.CurrentCultureIgnoreCase)
                    .Select(album => new AnalysisAlbumGroupViewModel(
                        album.Key,
                        album.OrderBy(item => item.Path, StringComparer.CurrentCultureIgnoreCase).ToList()))
                    .ToList()))
            .ToList();
    }

    private static string AlbumLabel(string path, TrackRecord? record)
    {
        if (record is not null && !string.IsNullOrWhiteSpace(record.Album))
            return $"{record.EffectiveAlbumArtist} — {record.StrippedAlbum ?? record.Album}";

        var directory = Path.GetDirectoryName(path);
        var folder = string.IsNullOrWhiteSpace(directory) ? null : Path.GetFileName(directory);
        return string.IsNullOrWhiteSpace(folder) ? "(album unavailable)" : folder;
    }

    private void AlbumChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AnalysisAlbumGroupViewModel.ActiveCount))
            return;
        OnPropertyChanged(nameof(ActiveCount));
    }
}

/// <summary>Findings for one album within a problem group.</summary>
public sealed class AnalysisAlbumGroupViewModel : ViewModelBase
{
    public string Album { get; }
    public IReadOnlyList<AnalysisFindingViewModel> Findings { get; }
    public int Count => Findings.Count;
    public int ActiveCount => Findings.Count(finding => finding.Disposition == AnalysisFindingDisposition.Active);

    public AnalysisAlbumGroupViewModel(string album, IReadOnlyList<AnalysisFindingViewModel> findings)
    {
        Album = album;
        Findings = findings;
        foreach (var finding in Findings)
            finding.PropertyChanged += FindingChanged;
    }

    private void FindingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AnalysisFindingViewModel.Disposition))
            OnPropertyChanged(nameof(ActiveCount));
    }
}

/// <summary>A finding plus its review disposition within a retained analysis run.</summary>
public partial class AnalysisFindingViewModel : ViewModelBase
{
    public AnalysisFinding Finding { get; }
    public string Path => Finding.Path;
    public string Description => Finding.Description;
    public string Problem => Finding.Problem ?? Finding.Description;
    public string Album { get; }
    public IReadOnlyList<AnalysisFindingDisposition> Dispositions { get; } =
        Enum.GetValues<AnalysisFindingDisposition>();

    [ObservableProperty]
    private AnalysisFindingDisposition _disposition;

    public AnalysisFindingViewModel(AnalysisFinding finding, string album)
    {
        Finding = finding;
        Album = album;
    }
}
