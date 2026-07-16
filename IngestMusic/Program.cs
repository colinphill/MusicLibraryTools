using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    bool apply = args.Any(a => a.Equals("--apply", StringComparison.OrdinalIgnoreCase));
    string[] operands = args.Where(a => !a.Equals("--apply", StringComparison.OrdinalIgnoreCase)).ToArray();
    if (operands.Length != 2)
    {
        Console.Error.WriteLine(
            "Usage: IngestMusic <source-directory> <libraryconfiguration.xml> [--apply]");
        return 1;
    }

    try
    {
        string configurationPath = Path.GetFullPath(operands[1]);
        bool usesLibraryConfiguration =
            System.Xml.Linq.XDocument.Load(configurationPath).Root?.Name.LocalName ==
            "LibraryConfiguration";
        IItunesMediaMutationService? itunes = null;
        if (usesLibraryConfiguration)
        {
            var settings = new CommandLineAppSettings(configurationPath);
            itunes = new ItunesMediaMutationService(settings);
            using var library = new LibraryService(settings, itunes: itunes);
            Console.Error.WriteLine("Indexing the configured library cache...");
            await library.IndexForOperationAsync();
        }
        else
        {
            Console.Error.WriteLine(
                "Warning: legacy ingest configuration detected; import it into a " +
                "LibraryConfiguration to use the shared library cache.");
        }

        IIngestMusicService service = new IngestMusicService(new FfmpegRunner(), itunes: itunes);
        var plan = await service.PreviewAsync(new IngestRequest(operands[0], configurationPath));
        PrintPlan(plan);
        if (!plan.CanApply)
            return 2;
        if (!apply)
        {
            Console.WriteLine("Dry-run mode; pass --apply to approve required derivations and execute this plan.");
            return 0;
        }

        var approvals = new List<IngestApprovalDecision>();
        if (plan.RequiredApprovals.Count > 0 && Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Apply requires interactive approval for missing CD-quality FLAC files, but standard input is redirected.");
            return 3;
        }
        foreach (var approval in plan.RequiredApprovals)
        {
            Console.WriteLine();
            Console.WriteLine($"CD-quality FLAC files are missing for {approval.AlbumDisplay}:");
            foreach (string track in approval.MissingTracks)
                Console.WriteLine($"  {track}");
            Console.Write("Derive these files from the high-resolution versions? [y/N] ");
            string? answer = Console.ReadLine();
            bool approved = answer?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true ||
                            answer?.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase) == true;
            approvals.Add(new IngestApprovalDecision(approval.AlbumKey, approved));
            if (!approved)
            {
                Console.WriteLine("Declined; the entire run was cancelled and nothing was changed.");
                return 3;
            }
        }

        var progress = new Progress<IngestProgress>(p =>
            Console.WriteLine($"[{p.CompletedItems}/{p.TotalItems}] {p.Album}: {p.Operation}"));
        var result = await service.ApplyAsync(plan, approvals, progress);
        if (result.Cancelled)
        {
            Console.Error.WriteLine(result.Message);
            return 3;
        }
        foreach (var album in result.Albums.Where(a => !a.Success))
            Console.Error.WriteLine($"FAILED {album.AlbumKey}: {album.Error}");
        Console.WriteLine($"Installed {result.Installed} files; {result.Failed} albums failed.");
        return result.Failed == 0 ? 0 : 4;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Cancelled.");
        return 3;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
}

static void PrintPlan(IngestPlan plan)
{
    Console.WriteLine($"Albums: {plan.Albums.Count}; source files: {plan.Files.Count}; approvals: {plan.RequiredApprovals.Count}; conflicts: {plan.Conflicts.Count}");
    foreach (var conflict in plan.Conflicts)
        Console.Error.WriteLine($"CONFLICT {conflict.Path}: {conflict.Message}");
    foreach (var file in plan.Files)
    {
        Console.WriteLine($"{file.SourceType}: {file.Source}");
        foreach (string line in file.Summary.Split(Environment.NewLine))
            Console.WriteLine($"  {line}");
    }
    if (plan.IgnoredFiles.Count > 0)
        Console.WriteLine(plan.Configuration.RemoveNonMusicAfterIngest
            ? $"Unsupported/non-audio files scheduled for {DisposalName(plan.Configuration)}: {plan.IgnoredFiles.Count}"
            : $"Unsupported/non-audio files left untouched: {plan.IgnoredFiles.Count}");
}

static string DisposalName(IngestMusicConfiguration configuration)
    => configuration.DeleteSourcesAfterIngest ? "deletion" : "quarantine";
