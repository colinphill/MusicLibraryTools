internal static class CatalogGeneratorCommandLine
{
    public static int Run(
        string[] args,
        Func<string[], int> runGenerator,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(runGenerator);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            return runGenerator(args);
        }
        catch (Exception exception)
        {
            error.WriteLine(exception.Message);
            return 1;
        }
    }
}
