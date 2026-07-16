#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

return await CrossSyncPlaylistsCommand.RunAsync(args);

internal static class CrossSyncPlaylistsCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParse(args, out PlaylistExportRequest? request, out bool apply, out bool check))
        {
            Console.Error.WriteLine(
                "Usage: CrossSyncPlaylists <libraryconfiguration.xml> [clean|check] " +
                "[--library <file.itl>] [--apply]");
            return 2;
        }

        PlaylistExportRequest parsedRequest = request!;
        var coordinator = new FileMutationCoordinator();
        var settings = new CommandLineAppSettings(parsedRequest.ConfigurationPath!);
        var itunes = new ItunesMediaMutationService(settings);
        IPlaylistExportService service = new PlaylistExportService(
            new LibraryOperationContextFactory(), new FileInventoryService(),
            new FileMutationPlanExecutor(coordinator, itunes));
        var progress = new Progress<OperationProgress>(value =>
        {
            if (!string.IsNullOrWhiteSpace(value.Message)) Console.Error.WriteLine(value.Message);
        });

        try
        {
            PlaylistExportPlan plan = await service.PreviewAsync(parsedRequest, progress);
            foreach (OperationIssue issue in plan.Issues)
                Console.WriteLine($"{issue.Severity,-11} {issue.Code}: {issue.Message}" +
                    (issue.Path is null ? "" : " [" + issue.Path + "]"));
            foreach (PlaylistExportTargetPlan target in plan.Targets)
                Console.WriteLine($"Target {target.Target}: {target.Files.Count:N0} playlist(s), " +
                    $"{target.MissingTrackCount:N0} missing track mapping(s).");
            if (!check)
                foreach (FileMutationAction action in plan.MutationPlan.Actions)
                    Console.WriteLine($"{action.Kind,-16} {action.DestinationPath}");

            if (check || !apply)
            {
                if (!check) Console.WriteLine("Preview only; pass --apply to execute this exact plan.");
                return plan.CanApply ? 0 : 4;
            }
            if (!plan.CanApply)
            {
                Console.Error.WriteLine("Apply refused because the reviewed plan contains blockers.");
                return 4;
            }

            PlaylistExportResult result = await service.ApplyAsync(plan, progress);
            Console.WriteLine($"Applied {result.PlaylistCount:N0} playlist(s): " +
                $"{result.Mutations.Copied:N0} created, {result.Mutations.Replaced:N0} replaced, " +
                $"{result.Mutations.Quarantined:N0} quarantined.");
            if (result.Mutations.JournalPath is not null)
                Console.WriteLine("Recovery journal: " + result.Mutations.JournalPath);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Playlist export cancelled.");
            return 130;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Playlist export failed: " + error.Message);
            return 1;
        }
    }

    private static bool TryParse(string[] args, out PlaylistExportRequest? request,
        out bool apply, out bool check)
    {
        request = null;
        apply = false;
        check = false;
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
            return false;
        bool clean = false;
        string? library = null;
        for (int index = 1; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument.Equals("clean", StringComparison.OrdinalIgnoreCase)) clean = true;
            else if (argument.Equals("check", StringComparison.OrdinalIgnoreCase)) check = true;
            else if (argument.Equals("--apply", StringComparison.OrdinalIgnoreCase)) apply = true;
            else if (argument.Equals("--library", StringComparison.OrdinalIgnoreCase) &&
                     index + 1 < args.Length) library = args[++index];
            else return false;
        }
        request = new(args[0], library, clean);
        return true;
    }
}
