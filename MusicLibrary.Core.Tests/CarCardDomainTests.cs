using System.Xml.Serialization;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class CarCardDomainTests
{
    [Fact]
    public void BalancedTreeRetainsEveryMappingAndStablePathsAcrossXmlRoundTrip()
    {
        var settings = new CarCardBalanceSettings(BalanceSize: 4, RebalanceSize: 5,
            MaxDepthDisparity: 1, BalanceBreak: 3);
        var tree = new CarBalancedPathNode();
        string[] names = Enumerable.Range(0, 31).Select(index => $"Artist {index:D2}").ToArray();
        foreach (string name in names) tree.AddItem(name, settings);
        tree.Rebalance(settings, force: true);
        var paths = names.ToDictionary(name => name, name => tree.FindNode(name, settings).Path);

        CarBalancedPathNode restored = RoundTrip(tree);

        Assert.Equal(names, restored.GetAllItems().OrderBy(name => name).ToArray());
        foreach (string name in names)
            Assert.Equal(paths[name], restored.FindNode(name, settings).Path);
    }

    [Fact]
    public void SyncDatabaseUsesLegacyCompatibleElementNamesAndRestoresMaps()
    {
        var database = new CarSyncDatabase();
        database.ArtistMap["Beatles"] = ["The Beatles"];
        database.ContributingArtistMap["Bowie"] = ["David Bowie"];
        database.HashSet.Playlists.Add(new() { Name = "Road.m3u", Hash = "hash" });
        database.HashSet.Artists["Beatles"] = [new() { Name = "All Tracks.m3u", Hash = "artist-hash" }];

        using var stream = new MemoryStream();
        new XmlSerializer(typeof(CarSyncDatabase)).Serialize(stream, database);
        string xml = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        stream.Position = 0;
        var restored = Assert.IsType<CarSyncDatabase>(
            new XmlSerializer(typeof(CarSyncDatabase)).Deserialize(stream));

        Assert.Contains("<SyncDatabase", xml);
        Assert.Contains("<ArtistMapElements>", xml);
        Assert.Contains("<ArtistPlaylists>", xml);
        Assert.DoesNotContain("SerializedNodes", xml);
        Assert.Equal(["The Beatles"], restored.ArtistMap["Beatles"]);
        Assert.Equal("Road.m3u", Assert.Single(restored.HashSet.Playlists).Name);
        Assert.Equal("All Tracks.m3u", Assert.Single(restored.HashSet.Artists["Beatles"]).Name);
    }

    private static T RoundTrip<T>(T value)
    {
        using var stream = new MemoryStream();
        var serializer = new XmlSerializer(typeof(T));
        serializer.Serialize(stream, value);
        stream.Position = 0;
        return Assert.IsType<T>(serializer.Deserialize(stream));
    }
}
