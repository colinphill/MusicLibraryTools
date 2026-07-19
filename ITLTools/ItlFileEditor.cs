using System.Diagnostics;

namespace iTunes.Binary;

/// <summary>Safe file-level helpers for applications that edit a library without iTunes COM.</summary>
public static class ItlFileEditor
{
    public const string LibraryEnvironmentVariable = "ITUNES_ITL";

    public static string ResolveLibraryPath(string? specifiedPath = null)
    {
        string? path = specifiedPath;
        if (string.IsNullOrWhiteSpace(path))
            path = Environment.GetEnvironmentVariable(LibraryEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                "iTunes", "iTunes Library.itl");
        return Path.GetFullPath(path);
    }

    /// <summary>
    /// Refuses in-place editing while iTunes is running. iTunes owns and can overwrite its selected
    /// library, so concurrent offline edits cannot be made safe with file sharing alone.
    /// </summary>
    public static void EnsureItunesIsClosed()
    {
        Process[] processes = Process.GetProcessesByName("iTunes");
        try
        {
            if (processes.Length > 0)
                throw new InvalidOperationException("iTunes is running. Quit iTunes before editing an .itl file.");
        }
        finally
        {
            foreach (Process process in processes)
                process.Dispose();
        }
    }

    public static void SaveValidated(ItlDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        EnsureItunesIsClosed();

        // Some native fields (notably the absolute decoded-body anchors in mlqh) are derived from
        // the final section layout. A size-changing edit makes their in-memory values stale until
        // the writer refreshes them, so validate the exact rebuilt image that will be committed.
        byte[] rebuilt = ItlWriter.Build(document.Envelope, document.Serialize());
        ItlDocument written = ItlDocument.Parse(ItlEnvelope.Parse(rebuilt));
        ItlValidationIssue[] errors = [.. written.Validate()
            .Where(issue => issue.Severity == ItlValidationSeverity.Error)];
        if (errors.Length > 0)
            throw new InvalidDataException("The edited library failed validation: " +
                string.Join("; ", errors.Select(issue => $"{issue.Code}: {issue.Message}")));
        ItlWriter.SaveBuilt(rebuilt, path);
    }
}
