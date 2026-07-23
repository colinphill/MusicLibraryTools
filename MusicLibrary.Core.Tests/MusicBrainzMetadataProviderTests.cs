using System.Net;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class MusicBrainzMetadataProviderTests
{
    private const string RecordedReleasePage =
        """
        {
          "release-count": 2,
          "release-offset": 0,
          "releases": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "title": "Example Album",
              "date": "2001-02-03",
              "country": "US",
              "status": "Official",
              "barcode": "0123456789012",
              "artist-credit": [
                { "name": "Example Artist", "joinphrase": "" }
              ],
              "release-group": {
                "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "title": "Example Album",
                "primary-type": "Album"
              },
              "label-info": [
                {
                  "catalog-number": "CAT-001",
                  "label": { "name": "Example Label" }
                }
              ],
              "media": [
                {
                  "position": 1,
                  "format": "CD",
                  "tracks": [
                    {
                      "position": 1,
                      "number": "1",
                      "title": "Example Song",
                      "length": 241000,
                      "artist-credit": [{ "name": "Example Artist" }],
                      "recording": {
                        "id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                        "title": "Example Song"
                      }
                    }
                  ]
                }
              ]
            },
            {
              "id": "22222222-2222-2222-2222-222222222222",
              "title": "Example Album",
              "date": "2002",
              "country": "GB",
              "status": "Official",
              "artist-credit": [{ "name": "Example Artist" }],
              "release-group": {
                "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "title": "Example Album",
                "primary-type": "Album"
              },
              "media": [
                { "position": 1, "format": "Digital Media", "tracks": [] }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void RecordedPage_PreservesEditionAndTrackDetails()
    {
        MusicBrainzReleasePage page =
            MusicBrainzMetadataProvider.ParseReleasePage(RecordedReleasePage);

        Assert.Equal(2, page.Total);
        MusicBrainzReleaseCandidate first = page.Releases[0];
        Assert.Equal("Example Album", first.Title);
        Assert.Equal("Example Artist", first.ArtistCredit);
        Assert.Equal("2001-02-03", first.Date);
        Assert.Equal("US", first.Country);
        Assert.Equal("Official", first.Status);
        Assert.Equal("0123456789012", first.Barcode);
        Assert.Equal("Example Label", first.Label);
        Assert.Equal("CAT-001", first.CatalogNumber);
        Assert.Equal(["CD"], first.Formats);
        MusicBrainzTrackCandidate track = Assert.Single(first.Tracks);
        Assert.Equal(1, track.MediumPosition);
        Assert.Equal(1, track.TrackPosition);
        Assert.Equal(241000, track.LengthMilliseconds);
        Assert.Equal(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            track.RecordingId);
    }

    [Fact]
    public async Task Provider_ReportsProgressAndUsesRecordingBrowseEndpoint()
    {
        var transport = new RecordingTransport(new(
            HttpStatusCode.OK, RecordedReleasePage));
        var provider = new MusicBrainzMetadataProvider(transport);
        var progress = new RecordingProgress();
        Guid recordingId =
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        MusicBrainzReleaseResult result =
            await provider.ResolveRecordingAsync(recordingId, progress);

        Assert.Equal(2, result.Releases.Length);
        Assert.Contains($"recording={recordingId:D}", transport.Uri!.Query);
        Assert.Contains("recordings", transport.Uri.Query);
        Assert.Equal(2, progress.Items[^1].Completed);
        Assert.Equal(2, progress.Items[^1].Total);
    }

    [Fact]
    public void BrowseUri_RequestsJsonAndEditionIncludes()
    {
        Uri uri = MusicBrainzMetadataProvider.BuildBrowseUri(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

        Assert.Equal("musicbrainz.org", uri.Host);
        Assert.Equal("/ws/2/release", uri.AbsolutePath);
        Assert.Contains("fmt=json", uri.Query);
        Assert.Contains("release-groups", uri.Query);
        Assert.Contains("media", uri.Query);
        Assert.Contains("labels", uri.Query);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData(
        """{"release-count":1,"release-offset":0,"releases":[{"id":"bad"}]}""")]
    public void Page_RejectsMalformedProviderData(string content)
    {
        Assert.Throws<InvalidDataException>(() =>
            MusicBrainzMetadataProvider.ParseReleasePage(content));
    }

    private sealed class RecordingTransport(MusicBrainzHttpResult result)
        : IMusicBrainzHttpTransport
    {
        public Uri? Uri { get; private set; }

        public Task<MusicBrainzHttpResult> GetAsync(
            Uri uri,
            CancellationToken ct = default)
        {
            Uri = uri;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingProgress : IProgress<OperationProgress>
    {
        public List<OperationProgress> Items { get; } = [];
        public void Report(OperationProgress value) => Items.Add(value);
    }
}
