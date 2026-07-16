using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace AnalyzeMetadata;

/// <summary>Command-line adapter for Core metadata indexing, analysis, and artist repair services.</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: AnalyzeMetadata <libraryconfiguration.xml> [checks]");
                return 2;
            }

            var checks = args.Skip(1).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool offerArtistRepairs = args.Skip(1).Any(argument =>
                argument.StartsWith("thresh", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(argument[6..], out _));

            using var library = new LibraryService(args[0]);
            Console.WriteLine("Indexing Files...");
            var indexProgress = new Progress<OperationProgress>(value =>
            {
                if ((value.Completed & 1023) == 0 && value.Completed > 0)
                    Console.WriteLine($"Indexed {value.Completed:N0} files...");
            });
            await library.IndexForOperationAsync(indexProgress);
            IReadOnlyList<TrackRecord> records = await library.GetAllRecordsAsync();
            Console.WriteLine($"Total Parsed Files: {records.Count:N0}");

            if (checks.Contains("basecheck"))
                RenderReport(LibraryAnalyzer.BasicMetadata(records));

            if (checks.Contains("incon"))
                RenderReport(LibraryAnalyzer.MetadataInconsistencies(records));

            if (checks.Contains("checksets"))
                RenderReport(await library.CheckSetsAsync());

            if (checks.Contains("checkhires"))
                RenderResolutionReport(
                    LibraryAnalyzer.CompareResolutionAlbums(records, "hires", "stereo"));

            if (checks.Contains("checkhiresmulti"))
                RenderResolutionReport(
                    LibraryAnalyzer.CompareResolutionAlbums(records, "hires", "multi"));

            if (checks.Contains("checklores"))
                RenderReport(LibraryAnalyzer.LowResolutionInHighResolutionTree(records));

            if (checks.Contains("checksr"))
                RenderReport(LibraryAnalyzer.HighResolutionAudio(records));

            if (checks.Contains("checkartists"))
                await AnalyzeArtistsAsync(library, records, offerArtistRepairs);

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("AnalyzeMetadata cancelled.");
            return 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"AnalyzeMetadata: {exception.Message}");
            return 1;
        }
    }

    private static void RenderReport(AnalysisReport report)
    {
        Console.WriteLine();
        Console.WriteLine($"{report.Name}: {report.Count:N0}");
        foreach (AnalysisFinding finding in report.Findings)
        {
            string path = finding.Path.Replace("\u00A0", "{&nbsp;}");
            Console.WriteLine($"{finding.Description}: {path}");
        }
    }

    private static void RenderResolutionReport(ResolutionComparisonReport report)
    {
        Console.WriteLine();
        foreach (ResolutionComparisonFinding finding in report.Findings)
        {
            ResolutionAlbum high = finding.HighResolution;
            switch (finding.Kind)
            {
                case ResolutionComparisonKind.TrackCountMismatch:
                    Console.WriteLine($"Count Mismatch,{finding.HighTrackCount}," +
                        $"{finding.StandardTrackCount},{finding.Standard!.Artist}," +
                        $"{finding.Standard.Album},{finding.Standard.Directory}");
                    break;
                case ResolutionComparisonKind.MetadataDifference:
                    Console.WriteLine($"Hit,{finding.ArtistDistance}," +
                        $"{finding.AlbumDistance},{finding.Standard!.Artist}," +
                        $"{finding.Standard.Album},{finding.Standard.Directory}");
                    Console.WriteLine($"   ,{finding.ArtistDistance}," +
                        $"{finding.AlbumDistance},{high.Artist},{high.Album}");
                    break;
                case ResolutionComparisonKind.Missing:
                    Console.WriteLine(
                        $"Miss,{high.Artist},{high.Album},{finding.MatchThreshold:0.0}");
                    break;
                case ResolutionComparisonKind.Ambiguous:
                    Console.WriteLine($"Multiple,{high.Artist},{high.Album}");
                    foreach (ResolutionAlbum candidate in finding.Candidates ?? [])
                    {
                        Console.WriteLine(
                            $"-->,{candidate.Artist},{candidate.Album},{candidate.Directory}");
                    }
                    break;
            }
        }
        Console.WriteLine($"{report.AlbumCount} {report.MatchedCount} " +
            $"{report.MissingCount} {report.AmbiguousCount}");
    }

    private static async Task AnalyzeArtistsAsync(
        LibraryService library,
        IReadOnlyList<TrackRecord> records,
        bool offerRepairs)
    {
        var reconciler = new ArtistReconciler(
            new MediaFileService(library),
            new TagWriteService(library));
        IReadOnlyList<SimilarArtistGroup> exactGroups =
            reconciler.FindSimilarArtists(records, threshold: 0);
        foreach (SimilarArtistGroup group in exactGroups)
        {
            foreach (ArtistVariant variant in group.Variants)
            {
                Console.WriteLine($"Variation: {variant.Name}");
                foreach (string? folder in variant.Paths
                             .Select(Path.GetDirectoryName)
                             .Where(path => path is not null)
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                    Console.WriteLine(folder);
            }
            Console.WriteLine();

            if (!offerRepairs)
                continue;

            Console.WriteLine("Select Variation:");
            for (int index = 0; index < group.Variants.Count; index++)
            {
                ArtistVariant variant = group.Variants[index];
                Console.WriteLine(
                    $"{index + 1}) {variant.Name} ({variant.TrackCount})");
            }
            Console.Write("-> ");
            if (!int.TryParse(Console.ReadLine(), out int selection) ||
                selection < 1 || selection > group.Variants.Count)
                continue;

            string canonical = group.Variants[selection - 1].Name;
            foreach (ArtistVariant variant in group.Variants.Where(
                         variant => !StringComparer.Ordinal.Equals(variant.Name, canonical)))
            {
                int changed = await reconciler.RenameArtistAsync(
                    variant.Paths, variant.Name, canonical);
                Console.WriteLine(
                    $"Updated {changed:N0} file(s): {variant.Name} -> {canonical}");
            }
        }

        IReadOnlyList<SimilarArtistGroup> fuzzyGroups =
            reconciler.FindSimilarArtists(records, threshold: 0.1);
        foreach (SimilarArtistGroup group in fuzzyGroups.Where(group =>
                     !exactGroups.Any(exact => group.Variants.All(variant =>
                         exact.Variants.Any(candidate =>
                             StringComparer.Ordinal.Equals(candidate.Name, variant.Name))))))
        {
            Console.WriteLine("Similar artist names:");
            foreach (ArtistVariant variant in group.Variants)
            {
                Console.WriteLine($"{variant.Name} ({variant.TrackCount})");
                foreach (string? folder in variant.Paths
                             .Select(Path.GetDirectoryName)
                             .Where(path => path is not null)
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                    Console.WriteLine($"--> {folder}");
            }
            Console.WriteLine();
        }
    }
}
