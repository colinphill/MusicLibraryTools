using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace UpdateCarCard;

/// <summary>Command-line adapter for the Core car-card projection workflow.</summary>
public static class Program
{
    private sealed record Options(string ConfigurationPath, bool Rebalance, bool FixErrors,
        bool Initialize, int MaxRemovals, string? LibraryPath, bool Apply);

    public static async Task<int> Main(string[] args)
    {
        if (!TryParse(args, out Options? options))
        {
            Console.WriteLine("Usage: UpdateCarCard <LibraryConfiguration.xml> [rebalance] " +
                "[fixerrors] [--initialize] [--library <file.itl>] " +
                "[--max-removals <count>] [--apply]");
            return 2;
        }
        try
        {
            var inventories = new FileInventoryService();
            var settings = new CommandLineAppSettings(options!.ConfigurationPath);
            var itunes = new ItunesMediaMutationService(settings);
            var service = new CarCardService(new LibraryOperationContextFactory(), inventories,
                new FileMutationPlanExecutor(itunes: itunes));
            var progress = new SynchronousProgress<OperationProgress>(value =>
            {
                if (!string.IsNullOrWhiteSpace(value.Message))
                    Console.Error.WriteLine(value.CurrentPath is null ? value.Message :
                        $"{value.Message}: {value.CurrentPath}");
            });
            CarCardPlan plan = await service.PreviewAsync(new(options.ConfigurationPath,
                options.Rebalance, options.FixErrors, options.Initialize, options.MaxRemovals,
                options.LibraryPath), progress);
            RenderPlan(plan);
            if (!plan.CanApply) return 4;
            if (!options.Apply)
            {
                Console.WriteLine("Dry run: pass --apply to execute this exact reviewed plan.");
                return 0;
            }
            CarCardResult result = await service.ApplyAsync(plan, progress);
            Console.WriteLine($"Applied {result.LibraryTrackCount:N0} track(s) and " +
                $"{result.PlaylistCount:N0} playlist(s): {result.Mutations.Copied:N0} created, " +
                $"{result.Mutations.Replaced:N0} replaced, " +
                $"{result.Mutations.Quarantined:N0} quarantined.");
            if (result.Mutations.JournalPath is not null)
                Console.WriteLine("Recovery journal: " + result.Mutations.JournalPath);
            return 0;
        }
        catch (OperationCanceledException) { Console.WriteLine("UpdateCarCard cancelled."); return 3; }
        catch (Exception exception)
        {
            Console.Error.WriteLine("UpdateCarCard: " + exception.Message);
            return 1;
        }
    }

    private static void RenderPlan(CarCardPlan plan)
    {
        foreach (OperationIssue issue in plan.Issues)
            Console.WriteLine($"{issue.Severity}: {issue.Message}" +
                (issue.Path is null ? "" : $" [{issue.Path}]"));
        foreach (FileMutationAction action in plan.MutationPlan.Actions)
            Console.WriteLine($"{action.Kind,-16} {action.DestinationPath}");
        Console.WriteLine($"Plan: {plan.LibraryTrackCount:N0} tracks, " +
            $"{plan.InstalledTrackCount:N0} installs, {plan.UnchangedTrackCount:N0} unchanged, " +
            $"{plan.RemovedTrackCount:N0} removals, {plan.PlaylistCount:N0} playlists.");
    }

    private static bool TryParse(string[] args, out Options? options)
    {
        options = null;
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal)) return false;
        bool rebalance = false, fixErrors = false, initialize = false, apply = false;
        int maxRemovals = 0;
        string? library = null;
        for (int index = 1; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument.Equals("rebalance", StringComparison.OrdinalIgnoreCase)) rebalance = true;
            else if (argument.Equals("fixerrors", StringComparison.OrdinalIgnoreCase)) fixErrors = true;
            else if (argument.Equals("--initialize", StringComparison.OrdinalIgnoreCase)) initialize = true;
            else if (argument.Equals("--apply", StringComparison.OrdinalIgnoreCase)) apply = true;
            else if (argument.Equals("--library", StringComparison.OrdinalIgnoreCase) && ++index < args.Length)
                library = args[index];
            else if (argument.Equals("--max-removals", StringComparison.OrdinalIgnoreCase) &&
                     ++index < args.Length && int.TryParse(args[index], out maxRemovals) && maxRemovals >= 0) { }
            else if (argument.StartsWith("--max-removals=", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(argument["--max-removals=".Length..], out maxRemovals) && maxRemovals >= 0) { }
            else return false;
        }
        options = new(args[0], rebalance, fixErrors, initialize, maxRemovals, library, apply);
        return true;
    }
}
