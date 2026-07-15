using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace AndroidSync;

/// <summary>Command-line adapter for the Core device-sync workflow.</summary>
public static class Program
{
    private sealed record Options(
        string Source, string Destination, bool Remap, int MaxRemovals, bool Apply);

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (!TryParse(args, out Options? options))
        {
            Console.WriteLine("Usage: AndroidSync <source> <destination> [remap] " +
                "[--max-removals <count>] [--apply]");
            return 2;
        }

        try
        {
            var service = new DeviceSyncService(new FileTreeEndpointFactory());
            var progress = new Progress<OperationProgress>(value =>
            {
                if (!string.IsNullOrWhiteSpace(value.Message))
                    Console.WriteLine(value.CurrentPath is null
                        ? value.Message
                        : $"{value.Message}: {value.CurrentPath}");
            });
            DeviceSyncPlan plan = await service.PreviewAsync(
                new(options!.Source, options.Destination, options.Remap, options.MaxRemovals),
                progress);
            RenderPlan(plan);
            if (!plan.CanApply) return 4;
            if (!options.Apply)
            {
                Console.WriteLine("Dry run: pass --apply to execute this exact reviewed plan.");
                return 0;
            }

            DeviceSyncResult result = await service.ApplyAsync(plan, progress);
            Console.WriteLine($"Applied: {result.CreatedDirectoryCount:N0} directories created, " +
                $"{result.CopiedFileCount:N0} files copied, {result.ReplacedFileCount:N0} replaced, " +
                $"{result.QuarantinedCount:N0} quarantined.");
            if (result.JournalPath is not null)
                Console.WriteLine("Recovery journal: " + result.JournalPath);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("AndroidSync cancelled.");
            return 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("AndroidSync: " + exception.Message);
            return 1;
        }
    }

    private static void RenderPlan(DeviceSyncPlan plan)
    {
        foreach (OperationIssue issue in plan.Issues)
            Console.WriteLine($"{issue.Severity}: {issue.Message}" +
                (issue.Path is null ? "" : $" [{issue.Path}]"));
        foreach (DeviceSyncAction action in plan.Actions)
            Console.WriteLine($"{action.Kind,-20} {action.RelativePath}");
        Console.WriteLine($"Plan: {plan.Actions.Count:N0} action(s), " +
            $"{plan.UnchangedFileCount:N0} unchanged file(s), {plan.RemovalCount:N0} removal(s).");
    }

    private static bool TryParse(string[] args, out Options? options)
    {
        options = null;
        if (args.Length < 2 || args[0].StartsWith("--", StringComparison.Ordinal) ||
            args[1].StartsWith("--", StringComparison.Ordinal))
            return false;
        bool remap = false, apply = false;
        int maxRemovals = 0;
        for (int index = 2; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument.Equals("remap", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--remap", StringComparison.OrdinalIgnoreCase))
                remap = true;
            else if (argument.Equals("--apply", StringComparison.OrdinalIgnoreCase))
                apply = true;
            else if (argument.Equals("--max-removals", StringComparison.OrdinalIgnoreCase) &&
                     ++index < args.Length && int.TryParse(args[index], out maxRemovals) && maxRemovals >= 0)
            { }
            else if (argument.StartsWith("--max-removals=", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(argument["--max-removals=".Length..], out maxRemovals) && maxRemovals >= 0)
            { }
            else return false;
        }
        options = new(args[0], args[1], remap, maxRemovals, apply);
        return true;
    }
}
