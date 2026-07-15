#nullable enable
using System;
using System.Threading.Tasks;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

return await CheckRedundanciesCommand.RunAsync(args);

internal static class CheckRedundanciesCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? library = null;
        if (args.Length > 0)
        {
            if (args.Length != 2 || !args[0].Equals("--library", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Usage: CheckRedundancies [--library <file.itl>]");
                return 2;
            }
            library = args[1];
        }

        try
        {
            IRedundancyAnalysisService service = new RedundancyAnalysisService();
            RedundancyAnalysisResult result = await service.AnalyzeAsync(library,
                new Progress<OperationProgress>(value =>
                {
                    if (!string.IsNullOrWhiteSpace(value.Message)) Console.Error.WriteLine(value.Message);
                }));
            foreach (RedundancyGroup group in result.Groups)
            {
                foreach (RedundancyTrack track in group.Tracks)
                    Console.WriteLine($"{track.Artist} - {track.Title} ({track.Album}) [{track.Path}]");
                Console.WriteLine();
            }
            Console.WriteLine($"{result.Groups.Count:N0} redundancy group(s) among " +
                $"{result.ScannedTrackCount:N0} local tracks.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Redundancy analysis failed: " + error.Message);
            return 1;
        }
    }
}
