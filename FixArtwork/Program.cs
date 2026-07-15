using ConsoleTools;
using MusicLibrary.Core.Services;

namespace FixArtwork;

/// <summary>Command-line adapter for the Core artwork-normalization workflow.</summary>
public static class Program
{
    private sealed record Options(string PlaylistName, string? LibraryPath, bool Apply);

    public static int Main(string[] args)
    {
        LogConsole.SwitchFile("FixArtwork.log");
        try
        {
            if (!TryParseArguments(args, out Options? options))
            {
                LogConsole.WriteLine("Usage: FixArtwork <playlist> [--library <file.itl>] [--apply]");
                return 2;
            }

            return RunAsync(options!).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            LogConsole.WriteLine($"FixArtwork: {exception.Message}");
            return 1;
        }
        finally
        {
            LogConsole.End();
        }
    }

    private static async Task<int> RunAsync(Options options)
    {
        var service = new ArtworkNormalizationService();
        var progress = new Progress<MusicLibrary.Core.Models.OperationProgress>(value =>
        {
            if (!string.IsNullOrWhiteSpace(value.CurrentPath))
                LogConsole.WriteLine($"{value.Message}: {value.CurrentPath}");
        });
        ArtworkNormalizationPlan plan = await service.PreviewAsync(
            new(options.PlaylistName, options.LibraryPath), progress);
        RenderPlan(plan);
        if (!plan.CanApply)
            return 4;
        if (!options.Apply)
        {
            LogConsole.WriteLine("Dry run: pass --apply to execute this exact reviewed plan.");
            return plan.Issues.Any(issue =>
                issue.Severity == MusicLibrary.Core.Models.OperationIssueSeverity.Warning) ? 1 : 0;
        }

        ArtworkNormalizationResult result = await service.ApplyAsync(plan, progress);
        LogConsole.WriteLine();
        LogConsole.WriteLine($"Updated media files: {result.UpdatedFileCount:N0}");
        LogConsole.WriteLine($"Updated ITL tracks:  {result.UpdatedTrackCount:N0}");
        if (result.JournalPath is not null)
            LogConsole.WriteLine($"Recovery journal:   {result.JournalPath}");
        return result.Issues.Any(issue =>
            issue.Severity == MusicLibrary.Core.Models.OperationIssueSeverity.Warning) ? 1 : 0;
    }

    private static void RenderPlan(ArtworkNormalizationPlan plan)
    {
        foreach (var issue in plan.Issues)
            LogConsole.WriteLine($"{issue.Severity}: {issue.Message}" +
                (issue.Path is null ? "" : $" [{issue.Path}]"));
        foreach (ArtworkNormalizationItem item in plan.Items)
            LogConsole.WriteLine($"Replace: {item.Path} — {item.Current.MimeType}, " +
                $"{item.Current.Width}x{item.Current.Height}, {item.Current.Size:N0} bytes -> " +
                $"image/jpeg, {item.Proposed.Width}x{item.Proposed.Height}, " +
                $"{item.Proposed.Size:N0} bytes");
        LogConsole.WriteLine();
        LogConsole.WriteLine($"Tracks inspected:       {plan.ScannedTrackCount:N0}");
        LogConsole.WriteLine($"Tracks with artwork:    {plan.ArtworkTrackCount:N0}");
        LogConsole.WriteLine($"Tracks already valid:   {plan.UnchangedCount:N0}");
        LogConsole.WriteLine($"Media files to replace: {plan.Items.Count:N0}");
    }

    private static bool TryParseArguments(string[] args, out Options? options)
    {
        bool apply = false;
        string? libraryPath = null;
        var operands = new List<string>();
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index].Equals("--apply", StringComparison.OrdinalIgnoreCase))
                apply = true;
            else if (args[index].Equals("--library", StringComparison.OrdinalIgnoreCase) &&
                     ++index < args.Length)
                libraryPath = args[index];
            else if (args[index].StartsWith("--", StringComparison.Ordinal))
            {
                options = null;
                return false;
            }
            else
                operands.Add(args[index]);
        }
        options = operands.Count == 1 ? new(operands[0], libraryPath, apply) : null;
        return options is not null;
    }
}
