using System.Xml.Linq;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class DevicesEditorialLocalizationTests
{
    [Fact]
    public void Device_workflow_copy_names_scope_and_recovery_consequences()
    {
        Dictionary<string, string> resources = XDocument.Load(Path.Combine(
                FindRepositoryRoot(),
                "MusicLibraryManager.Presentation",
                "Resources",
                "Strings.resx"))
            .Root!
            .Elements("data")
            .ToDictionary(
                element =>
                    (string?)element.Attribute("name") ?? "",
                element =>
                    element.Element("value")?.Value ?? "",
                StringComparer.Ordinal);

        Assert.Equal(
            "Initialize destination",
            resources["Devices.Action.Initialize"]);
        Assert.Equal(
            "Restore previous sync",
            resources["Devices.Action.Restore"]);
        Assert.Equal(
            "Direct transfer (no staging or recovery)",
            resources["Devices.DirectTransfer"]);
        Assert.Equal(
            "Excluded path patterns (glob syntax, one per line)",
            resources["Devices.ExclusionGlobs"]);
        Assert.Equal(
            "Review the synchronization preview. Planned actions: {0:N0}; " +
            "removals: {1:N0}; bytes to transfer: {2:N0}.",
            resources["Devices.Status.PreviewReady"]);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current =
            new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(
                    current.FullName,
                    "MusicLibraryManager")) &&
                Directory.Exists(Path.Combine(
                    current.FullName,
                    "MusicLibraryManager.Presentation")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find the MusicLibraryTools repository root.");
    }
}
