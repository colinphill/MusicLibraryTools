using System.Xml.Linq;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class LibraryEditorialLocalizationTests
{
    [Fact]
    public void Library_copy_names_pending_state_and_external_tool_risk()
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
            "Inspector (pending edits)",
            resources["Library.Action.InspectorUnsaved"]);
        Assert.Equal(
            "Show in file manager",
            resources["Library.Action.Reveal"]);
        Assert.Equal(
            "Run “{0}”? Planned invocations: {1:N0}. External tools can " +
            "change files and are outside MusicLibraryManager recovery.",
            resources["Library.Dialog.ExternalTool.Message"]);
        Assert.Equal(
            "Apply or discard the current metadata edits before changing " +
            "file paths.",
            resources["Library.FileOperation.MetadataEditsPending"]);
        Assert.Equal(
            "Pending preview discarded. No files were changed.",
            resources["Library.Operation.PendingReverted"]);
        Assert.Equal(
            "pending changes",
            resources["Library.PendingChanges.CountLabel"]);
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
