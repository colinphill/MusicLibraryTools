namespace iTunes.Binary;

/// <summary>Converts the location representation stored in a track record into a filesystem path.</summary>
public static class ItlLocation
{
    public static string? ToLocalPath(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return null;

        if (Uri.TryCreate(location, UriKind.Absolute, out Uri? uri) && uri.IsFile)
            return uri.LocalPath;

        return location;
    }
}
