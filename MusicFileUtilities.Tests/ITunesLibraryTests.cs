using System.Xml.Linq;
using iTunes;
using Xunit;

namespace MusicFileUtilities.Tests
{
    public class ITunesLibraryTests
    {
        [Fact]
        public void TrackParsesDiscNumberAndDiscCount()
        {
            XElement track = XElement.Parse(
                """
                <dict>
                  <key>Name</key><string>Movement I</string>
                  <key>Track Number</key><integer>3</integer>
                  <key>Track Count</key><integer>8</integer>
                  <key>Disc Number</key><integer>2</integer>
                  <key>Disc Count</key><integer>4</integer>
                </dict>
                """);

            var parsed = new iTunesTrack(track.Elements("key"));

            Assert.Equal(3, parsed.TrackNumber);
            Assert.Equal(8, parsed.TotalTracks);
            Assert.Equal(2, parsed.DiscNumber);
            Assert.Equal(4, parsed.TotalDiscs);
        }
    }
}
