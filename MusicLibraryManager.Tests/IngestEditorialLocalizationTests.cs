using System.Xml.Linq;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class IngestEditorialLocalizationTests
{
    [Fact]
    public void Ingest_copy_names_the_reviewed_plan_and_user_actions()
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
            "Preview ingest plan",
            resources["Ingest.Action.Preview"]);
        Assert.Equal(
            "Apply ingest plan",
            resources["Ingest.Dialog.Apply.Primary"]);
        Assert.Equal(
            "Incoming music folder",
            resources["Ingest.IncomingFolder"]);
        Assert.Equal(
            "CD-quality output generation was declined. The entire run was " +
            "canceled and nothing was changed.",
            resources["Ingest.Status.DerivationDeclined"]);
        Assert.Equal(
            "Source folder selected by drag and drop. Run Preflight or Preview.",
            resources["Ingest.Status.SourceDropped"]);
        Assert.Equal(
            "Ingest committed. Re-indexing the library…",
            resources["Ingest.Status.Reindexing"]);
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
