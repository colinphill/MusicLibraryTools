#nullable enable
using System;
using System.Threading.Tasks;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

return await CrossSyncMusicCommand.RunAsync(args);

internal static class CrossSyncMusicCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParse(args, out CrossLibrarySyncRequest? request, out bool apply))
        {
            Console.Error.WriteLine(
                "Usage: CrossSyncMusic <libraryconfiguration.xml> [--library <file.itl>] " +
                "[--max-removals <count>] [--apply]");
            Console.Error.WriteLine("Preview is the default. Stale files are quarantined, never deleted.");
            return 2;
        }

        CrossLibrarySyncRequest parsedRequest = request!;
        var coordinator = new FileMutationCoordinator();
        var settings = new CommandLineAppSettings(parsedRequest.ConfigurationPath!);
        var itunes = new ItunesMediaMutationService(settings);
        ICrossLibrarySyncService service = new CrossLibrarySyncService(
            new LibraryOperationContextFactory(),
            new FileInventoryService(),
            new FileMutationPlanExecutor(coordinator, itunes));
        var progress = new SynchronousProgress<OperationProgress>(ReportProgress);

        try
        {
            CrossLibrarySyncPlan plan = await service.PreviewAsync(parsedRequest, progress);
            RenderPlan(plan);
            if (!apply)
            {
                Console.WriteLine("Preview only; pass --apply to execute this exact plan.");
                return plan.CanApply ? 0 : 4;
            }
            if (!plan.CanApply)
            {
                Console.Error.WriteLine("Apply refused because the reviewed plan contains blockers.");
                return 4;
            }

            CrossLibrarySyncResult result = await service.ApplyAsync(plan, progress);
            Console.WriteLine($"Applied: {result.Mutations.Copied:N0} copied, " +
                $"{result.Mutations.Replaced:N0} replaced, " +
                $"{result.Mutations.Quarantined:N0} quarantined, " +
                $"{result.UnchangedCount:N0} unchanged.");
            if (result.Mutations.JournalPath is not null)
                Console.WriteLine("Recovery journal: " + result.Mutations.JournalPath);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cross-library synchronization cancelled.");
            return 130;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Cross-library synchronization failed: " + error.Message);
            return 1;
        }
    }

    private static void RenderPlan(CrossLibrarySyncPlan plan)
    {
        foreach (OperationIssue issue in plan.Issues)
            Console.WriteLine($"{issue.Severity,-11} {issue.Code}: {issue.Message}" +
                (issue.Path is null ? "" : " [" + issue.Path + "]"));
        foreach (FileMutationAction action in plan.MutationPlan.Actions)
            Console.WriteLine($"{action.Kind,-10} {action.SourcePath} -> {action.DestinationPath}");
        Console.WriteLine($"Plan: {plan.Files.Count:N0} desired, {plan.UnchangedCount:N0} unchanged, " +
            $"{plan.StaleCount:N0} stale, {plan.MutationPlan.Actions.Count:N0} mutations.");
    }

    private static void ReportProgress(OperationProgress progress)
    {
        if (!string.IsNullOrWhiteSpace(progress.Message))
            Console.Error.WriteLine(progress.Message);
    }

    private static bool TryParse(string[] args, out CrossLibrarySyncRequest? request, out bool apply)
    {
        request = null;
        apply = false;
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
            return false;

        string configuration = args[0];
        string? library = null;
        int maxRemovals = 0;
        for (int index = 1; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument.Equals("--apply", StringComparison.OrdinalIgnoreCase))
                apply = true;
            else if (argument.Equals("--max-removals", StringComparison.OrdinalIgnoreCase) &&
                     index + 1 < args.Length && int.TryParse(args[++index], out maxRemovals) &&
                     maxRemovals >= 0)
            {
            }
            else if (argument.StartsWith("--max-removals=", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(argument["--max-removals=".Length..], out maxRemovals) &&
                     maxRemovals >= 0)
            {
            }
            else if (argument.Equals("--library", StringComparison.OrdinalIgnoreCase) &&
                     index + 1 < args.Length)
                library = args[++index];
            else
                return false;
        }

        request = new(configuration, library, maxRemovals);
        return true;
    }
}
