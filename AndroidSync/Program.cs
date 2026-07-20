using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace AndroidSync;

/// <summary>Command-line adapter for the Core device-sync workflow.</summary>
public static class Program
{
    private sealed record Options(
        string Source, string Destination, string? Serial, string? AdbPath,
        int MtimeTolerance, bool DeleteExtras, bool Direct, int? MaxRemovals, bool Apply);

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (!TryParse(args, out Options? options))
        {
            Console.WriteLine("Usage: AndroidSync <source> <destination> [--serial <device>] " +
                "[--adb <path>] [--mtime-tolerance <seconds>] [--no-delete] [--direct] " +
                "[--max-removals <count>] [--apply]");
            return 2;
        }

        try
        {
            var service = new DeviceSyncService(new SyncerClientAdapter());
            var progress = new SynchronousProgress<OperationProgress>(value =>
            {
                if (!string.IsNullOrWhiteSpace(value.Message))
                    Console.Error.WriteLine(value.CurrentPath is null
                        ? value.Message
                        : $"{value.Message}: {value.CurrentPath}");
            });
            DeviceSyncPlan plan = await service.PreviewAsync(
                new(options!.Source, options.Destination, options.Serial, options.AdbPath,
                    MtimeToleranceSeconds: options.MtimeTolerance,
                    DeleteExtras: options.DeleteExtras, Direct: options.Direct,
                    MaxRemovals: options.MaxRemovals),
                progress);
            RenderPlan(plan);
            if (!plan.CanApply)
            {
                TryDeletePlan(plan.PlanFilePath);
                return 4;
            }
            if (!options.Apply)
            {
                TryDeletePlan(plan.PlanFilePath);
                Console.WriteLine("Dry run: pass --apply to execute this exact reviewed plan.");
                return 0;
            }

            DeviceSyncResult result = await service.ApplyAsync(plan, progress);
            Console.WriteLine($"Applied: {result.CreatedDirectoryCount:N0} directories created, " +
                $"{result.CopiedFileCount:N0} files copied, {result.QuarantinedCount:N0} quarantined, " +
                $"{result.TransferredBytes:N0} bytes transferred.");
            if (result.RecoveryId is not null)
                Console.WriteLine("Recovery run: " + result.RecoveryId);
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
            $"{plan.FileCount:N0} file transfer(s), {plan.DirectoryCount:N0} directorie(s), " +
            $"{plan.RemovalCount:N0} removal(s), {plan.TransferBytes:N0} byte(s).");
    }

    private static void TryDeletePlan(string path)
    {
        try { File.Delete(path); }
        catch { }
    }

    private static bool TryParse(string[] args, out Options? options)
    {
        options = null;
        if (args.Length < 2 || args[0].StartsWith("--", StringComparison.Ordinal) ||
            args[1].StartsWith("--", StringComparison.Ordinal))
            return false;
        bool apply = false, deleteExtras = true, direct = false;
        string? serial = null, adbPath = null;
        int mtimeTolerance = 60;
        int? maxRemovals = null;
        for (int index = 2; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument.Equals("--apply", StringComparison.OrdinalIgnoreCase))
                apply = true;
            else if (argument.Equals("--no-delete", StringComparison.OrdinalIgnoreCase))
                deleteExtras = false;
            else if (argument.Equals("--direct", StringComparison.OrdinalIgnoreCase))
                direct = true;
            else if (argument.Equals("--serial", StringComparison.OrdinalIgnoreCase) && ++index < args.Length)
                serial = args[index];
            else if (argument.Equals("--adb", StringComparison.OrdinalIgnoreCase) && ++index < args.Length)
                adbPath = args[index];
            else if (argument.Equals("--mtime-tolerance", StringComparison.OrdinalIgnoreCase) &&
                     ++index < args.Length && int.TryParse(args[index], out mtimeTolerance) && mtimeTolerance >= 0)
            { }
            else if (argument.Equals("--max-removals", StringComparison.OrdinalIgnoreCase) &&
                     ++index < args.Length && int.TryParse(args[index], out int parsedMaximum) && parsedMaximum >= 0)
                maxRemovals = parsedMaximum;
            else if (argument.StartsWith("--max-removals=", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(argument["--max-removals=".Length..], out parsedMaximum) && parsedMaximum >= 0)
                maxRemovals = parsedMaximum;
            else return false;
        }
        options = new(args[0], args[1], serial, adbPath, mtimeTolerance,
            deleteExtras, direct, maxRemovals, apply);
        return true;
    }
}
