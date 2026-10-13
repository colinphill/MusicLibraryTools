using System.Xml.Linq;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using MusicLibraryTools;
using Xunit;

namespace MusicLibrary.Core.Tests;

public class EverythingHttpConfigurationTests
{
    [Fact]
    public void ConfigurationRoundTripsAndCanDisableHttpWithoutRetainingOldAttributes()
    {
        string path = Path.Combine(Path.GetTempPath(), "mlt_http_config_" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            // Legacy configuration stays readable and migrates through the normal editor path.
            new XDocument(new XElement("LibraryConfiguration",
                new XElement("IndexTarget", new XAttribute("Path", @"\\server\Music"),
                    new XAttribute("EverythingHttpUrl", "http://localhost:8080/"),
                    new XAttribute("EverythingHttpRoot", @"D:\Music"),
                    new XAttribute("EverythingHttpUsername", "reader"),
                    new XAttribute("EverythingHttpPasswordEnvironmentVariable", "EVERYTHING_PASSWORD"),
                    new XAttribute("Custom", "preserve")))).Save(path);
            var expected = new EverythingHttpSettings("http://localhost:8080/", @"D:\Music", "reader", "EVERYTHING_PASSWORD");
            Assert.Equal(expected, Assert.Single(new LibraryConfiguration(path).IndexLocations).EverythingHttp);
            EditableLibraryConfig editor = EditableLibraryConfig.Load(path);
            Assert.Equal(expected, Assert.Single(editor.IndexTargets).EverythingHttp);
            editor.Save(path);
            Assert.Equal(expected, Assert.Single(new LibraryConfiguration(path).IndexLocations).EverythingHttp);
            string httpFingerprint = new LibraryConfiguration(path).PolicySnapshot.Fingerprint;
            editor = EditableLibraryConfig.Load(path);
            editor.IndexTargets[0].EverythingHttp = null;
            editor.Save(path);
            Assert.Null(Assert.Single(new LibraryConfiguration(path).IndexLocations).EverythingHttp);
            Assert.NotEqual(httpFingerprint, new LibraryConfiguration(path).PolicySnapshot.Fingerprint);
            XElement root = Assert.Single(XDocument.Load(path).Root!.Elements("IndexTarget"));
            Assert.DoesNotContain(root.Attributes(), attribute => attribute.Name.LocalName.StartsWith("EverythingHttp"));
            Assert.Equal("preserve", (string?)root.Attribute("Custom"));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("ftp://localhost/")]
    [InlineData("http://reader:password@localhost/")]
    [InlineData("http://localhost/?search=anything")]
    [InlineData("http://localhost/#fragment")]
    [InlineData("")]
    public void InvalidUrlsAreRejectedByEditorAndRuntime(string url)
    {
        Assert.Throws<InvalidDataException>(() => new EverythingHttpSettings(url).Validate());
        var config = EditableLibraryConfig.CreateNew();
        var target = config.CreateIndexTarget("music");
        target.EverythingHttp = new(url);
        config.IndexTargets.Add(target);
        Assert.Contains(config.Validate(), issue => issue.Severity == LibraryConfigurationIssueSeverity.Error);
    }

    [Fact]
    public void EditorRowDisablesHttpAndNormalizesOptionalValues()
    {
        var row = new IndexTargetEditorRow { UseEverythingHttp = true, EverythingHttpRoot = "  D:\\Music  ", EverythingHttpUsername = " " };
        Assert.Equal(@"D:\Music", row.BuildEverythingHttpSettings()!.Root);
        Assert.Null(row.BuildEverythingHttpSettings()!.Username);
        row.UseEverythingHttp = false;
        Assert.Null(row.BuildEverythingHttpSettings());
    }
}
