using MusicLibraryTools.Build;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: SyncerResourceVerifier <Syncer.Client.dll> <server-root>");
    return 2;
}

try
{
    SyncerResourceVerifier.Verify(args[0], args[1]);
    Console.WriteLine("Verified four embedded Android syncer daemons in {0}.", args[0]);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
