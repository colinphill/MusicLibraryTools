using System.Text;

internal static class AtomicOutputBatch
{
    public static void Commit(
        IReadOnlyDictionary<string, string> outputs,
        AtomicOutputBatchHooks? hooks = null)
    {
        var targets = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        var staged = new List<StagedOutput>();
        bool committed = false;
        Exception? primaryFailure = null;
        try
        {
            foreach ((string configuredPath, string contents) in
                     outputs.OrderBy(
                         item => Path.GetFullPath(item.Key),
                         StringComparer.Ordinal))
            {
                string target = Path.GetFullPath(configuredPath);
                if (!targets.Add(target))
                    throw new InvalidDataException(
                        $"Output target '{target}' is duplicated.");
                byte[] bytes =
                    new UTF8Encoding(false).GetBytes(contents);
                if (File.Exists(target) &&
                    File.ReadAllBytes(target).AsSpan()
                        .SequenceEqual(bytes))
                    continue;

                string? parent = Path.GetDirectoryName(target);
                if (string.IsNullOrWhiteSpace(parent))
                    throw new InvalidDataException(
                        $"Output target '{target}' has no parent directory.");
                Directory.CreateDirectory(parent);
                string stage = Path.Combine(
                    parent,
                    $".{Path.GetFileName(target)}." +
                    $"{Guid.NewGuid():N}.stage");
                using (var stream = new FileStream(
                           stage,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           bufferSize: 64 * 1024,
                           FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                staged.Add(
                    new StagedOutput(
                        target,
                        stage,
                        File.Exists(target),
                        Path.Combine(
                            parent,
                            $".{Path.GetFileName(target)}." +
                            $"{Guid.NewGuid():N}.backup")));
            }

            var applied = new List<StagedOutput>();
            try
            {
                foreach (StagedOutput output in staged)
                {
                    hooks?.BeforeApply?.Invoke(output.Target);
                    if (output.Existed)
                    {
                        File.Replace(
                            output.Stage,
                            output.Target,
                            output.Backup,
                            ignoreMetadataErrors: true);
                    }
                    else
                    {
                        File.Move(
                            output.Stage,
                            output.Target);
                    }
                    applied.Add(output);
                }
            }
            catch (Exception applyFailure)
            {
                primaryFailure = applyFailure;
                var rollbackFailures = new List<Exception>();
                foreach (StagedOutput output in
                         applied.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (output.Existed &&
                            File.Exists(output.Backup))
                        {
                            File.Move(
                                output.Backup,
                                output.Target,
                                overwrite: true);
                        }
                        else if (!output.Existed &&
                                 File.Exists(output.Target))
                        {
                            File.Delete(output.Target);
                        }
                    }
                    catch (Exception rollbackFailure)
                    {
                        rollbackFailures.Add(
                            new IOException(
                                $"Could not roll back output " +
                                $"'{output.Target}'. Its recovery backup " +
                                $"was preserved at '{output.Backup}'.",
                                rollbackFailure));
                    }
                }
                if (rollbackFailures.Count > 0)
                    throw new AggregateException(
                        "The output batch failed before commit and one or " +
                        "more outputs could not be rolled back.",
                        [applyFailure, .. rollbackFailures]);
                throw;
            }

            // This is the transaction decision. Every target is live from
            // this point forward; backup cleanup cannot turn a committed
            // batch back into an uncommitted one.
            committed = true;
            var cleanupFailures = new List<Exception>();
            foreach (StagedOutput output in staged)
            {
                try
                {
                    hooks?.BeforeBackupCleanup?.Invoke(
                        output.Backup);
                    if (File.Exists(output.Backup))
                        File.Delete(output.Backup);
                }
                catch (Exception cleanupFailure)
                {
                    cleanupFailures.Add(
                        new IOException(
                            $"Output '{output.Target}' was committed, but " +
                            $"its recovery backup '{output.Backup}' could " +
                            "not be removed.",
                            cleanupFailure));
                }
            }
            if (cleanupFailures.Count > 0)
                throw new AtomicOutputCleanupException(
                    cleanupFailures);
        }
        catch (Exception failure)
        {
            primaryFailure ??= failure;
            throw;
        }
        finally
        {
            var stageCleanupFailures = new List<Exception>();
            foreach (StagedOutput output in staged)
            {
                try
                {
                    if (File.Exists(output.Stage))
                        File.Delete(output.Stage);
                }
                catch (Exception cleanupFailure)
                {
                    stageCleanupFailures.Add(
                        new IOException(
                            $"Could not remove staged output " +
                            $"'{output.Stage}'.",
                            cleanupFailure));
                }
            }
            if (stageCleanupFailures.Count > 0 &&
                primaryFailure is null)
                throw new AggregateException(
                    committed
                        ? "The output batch committed, but staged-file " +
                          "cleanup was incomplete."
                        : "The output batch did not commit and staged-file " +
                          "cleanup was incomplete.",
                    stageCleanupFailures);
        }
    }

    private sealed record StagedOutput(
        string Target,
        string Stage,
        bool Existed,
        string Backup);
}

internal sealed record AtomicOutputBatchHooks(
    Action<string>? BeforeApply = null,
    Action<string>? BeforeBackupCleanup = null);

internal sealed class AtomicOutputCleanupException :
    AggregateException
{
    public AtomicOutputCleanupException(
        IEnumerable<Exception> cleanupFailures)
        : base(
            "The output batch committed successfully, but one or more " +
            "recovery backups could not be removed.",
            cleanupFailures)
    {
    }
}
