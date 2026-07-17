using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace UpdateSmartStorage;

/// <summary>Command-line adapter for the Core smart-storage workflow.</summary>
public static class Program
{
    private sealed record Options(string Destination, bool Initialize, int MaxRemovals,
        string? LibraryPath, bool Apply);

    public static async Task<int> Main(string[] args)
    {
        if (!TryParse(args, out Options? options))
        {
            Console.WriteLine("Usage: UpdateSmartStorage <destination> [--initialize] " +
                "[--library <file.itl>] [--max-removals <count>] [--apply]");
            return 2;
        }

        try
        {
            var inventories = new FileInventoryService();
            var service = new SmartStorageService(new SmartStorageLibraryLoader(), inventories,
                new FileMutationPlanExecutor());
            var progress = new SynchronousProgress<OperationProgress>(value =>
            {
                if (!string.IsNullOrWhiteSpace(value.Message))
                    Console.Error.WriteLine(value.CurrentPath is null
                        ? value.Message
                        : $"{value.Message}: {value.CurrentPath}");
            });
            SmartStoragePlan plan = await service.PreviewAsync(new(options!.Destination,
                options.Initialize, options.MaxRemovals, options.LibraryPath), progress);
            RenderPlan(plan);
            if (!plan.CanApply) return 4;
            if (!options.Apply)
            {
                Console.WriteLine("Dry run: pass --apply to execute this exact reviewed plan.");
                return 0;
            }

            SmartStorageResult result = await service.ApplyAsync(plan, progress);
            Console.WriteLine($"Applied {result.LibraryTrackCount:N0} library track(s), " +
                $"{result.PlaylistCount:N0} playlist(s), and {result.ArtworkCount:N0} artwork item(s): " +
                $"{result.Mutations.Copied:N0} created, {result.Mutations.Replaced:N0} replaced, " +
                $"{result.Mutations.Quarantined:N0} quarantined.");
            if (result.Mutations.JournalPath is not null)
                Console.WriteLine("Recovery journal: " + result.Mutations.JournalPath);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("UpdateSmartStorage cancelled.");
            return 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("UpdateSmartStorage: " + exception.Message);
            return 1;
        }
    }

    private static void RenderPlan(SmartStoragePlan plan)
    {
        foreach (OperationIssue issue in plan.Issues)
            Console.WriteLine($"{issue.Severity}: {issue.Message}" +
                (issue.Path is null ? "" : $" [{issue.Path}]"));
        foreach (FileMutationAction action in plan.MutationPlan.Actions)
            Console.WriteLine($"{action.Kind,-16} {action.DestinationPath}");
        Console.WriteLine($"Plan: {plan.LibraryTrackCount:N0} tracks, " +
            $"{plan.InstalledTrackCount:N0} installs, {plan.UnchangedTrackCount:N0} unchanged, " +
            $"{plan.StaleTrackCount:N0} stale, {plan.PlaylistCount:N0} playlists, " +
            $"{plan.ArtworkCount:N0} artwork items.");
    }

    private static bool TryParse(string[] args, out Options? options)
    {
        options = null;
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal)) return false;
        bool initialize = false, apply = false;
        int maxRemovals = 0;
        string? library = null;
        for (int index = 1; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument.Equals("--initialize", StringComparison.OrdinalIgnoreCase))
                initialize = true;
            else if (argument.Equals("--apply", StringComparison.OrdinalIgnoreCase))
                apply = true;
            else if (argument.Equals("--library", StringComparison.OrdinalIgnoreCase) &&
                     ++index < args.Length)
                library = args[index];
            else if (argument.Equals("--max-removals", StringComparison.OrdinalIgnoreCase) &&
                     ++index < args.Length && int.TryParse(args[index], out maxRemovals) && maxRemovals >= 0)
            { }
            else if (argument.StartsWith("--max-removals=", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(argument["--max-removals=".Length..], out maxRemovals) && maxRemovals >= 0)
            { }
            else return false;
        }
        options = new(args[0], initialize, maxRemovals, library, apply);
        return true;
    }
}
