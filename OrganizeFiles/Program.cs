using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace OrganizeFiles;

/// <summary>Command-line adapter for the Core library-organization workflow.</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Skip(1).Any(argument =>
                !argument.Equals("--apply", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine(
                "Usage: OrganizeFiles <libraryconfiguration.xml> [--apply]");
            return 2;
        }

        bool apply = args.Skip(1).Any(argument =>
            argument.Equals("--apply", StringComparison.OrdinalIgnoreCase));
        try
        {
            using var service = new LibraryService(args[0]);
            Console.Error.WriteLine("Indexing...");
            var indexProgress = new SynchronousProgress<OperationProgress>(value =>
            {
                if (!string.IsNullOrWhiteSpace(value.Message))
                    Console.Error.WriteLine(value.CurrentPath is null
                        ? value.Message
                        : $"{value.Message}: {value.CurrentPath}");
            });
            await service.IndexForOperationAsync(indexProgress);

            IReadOnlyList<PlannedMove> plan = await service.PreviewMovesAsync();
            foreach (PlannedMove move in plan)
                Console.WriteLine($"{move.Source} -> {move.Destination}");
            Console.WriteLine($"Planned moves: {plan.Count:N0}");

            if (!apply)
            {
                Console.WriteLine(
                    "Dry run: pass --apply to execute this reviewed plan.");
                return 0;
            }

            int total = plan.Count;
            var progress = new SynchronousProgress<int>(completed =>
                Console.Error.WriteLine($"Moved {completed:N0}/{total:N0}"));
            OrganizeResult result = await service.ApplyMovesAsync(plan, progress);
            foreach ((string source, string error) in result.Errors)
                Console.Error.WriteLine($"Move failed: {source}: {error}");
            foreach ((string source, string error) in result.CacheErrors)
                Console.Error.WriteLine($"Cache refresh failed: {source}: {error}");
            Console.WriteLine($"Moved: {result.Moved:N0}; failed: {result.FailedCount:N0}");
            if (result.JournalPath is not null)
                Console.WriteLine($"Recovery journal: {result.JournalPath}");
            return result.FailedCount == 0 ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("OrganizeFiles cancelled.");
            return 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"OrganizeFiles: {exception.Message}");
            return 1;
        }
    }
}
