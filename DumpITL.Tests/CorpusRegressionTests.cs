using System.Buffers.Binary;
using System.Diagnostics;
using iTunes.Binary;
using MusicLibrary.Core.Services;
using Xunit;

namespace DumpITL.Tests;

public sealed class CorpusRegressionTests
{
    [Fact]
    public async Task PrivateCorpusImportSharesNativeAlbumAndArtistKeysWhenConfigured()
    {
        string? itl = Environment.GetEnvironmentVariable("DUMPITL_CORPUS_ITL");
        string? media = Environment.GetEnvironmentVariable("DUMPITL_CORPUS_IMPORT");
        if (string.IsNullOrWhiteSpace(itl) || string.IsNullOrWhiteSpace(media) ||
            !File.Exists(itl) || !File.Exists(media))
            return;

        string directory = Path.Combine(Path.GetTempPath(), $"itl-corpus-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string copy = Path.Combine(directory, "iTunes Library.itl");
        File.Copy(itl, copy);
        try
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            var service = new ItunesMediaMutationService();
            await using IItunesMediaMutationSession session =
                await service.BeginAsync([media], backupFiles: false, copy, ct);
            Assert.True(session.Active);
            ItunesMediaMutationResult result = await session.CommitAsync(
                [ItunesMediaMutation.Add(media)], ct);
            Assert.Equal(1, result.ImportedTracks);
            await session.CompleteAsync(ct);

            ItlDocument document = ItlDocument.Load(copy);
            ItlRecord track = Assert.Single(document.FindTracksByPath(media));
            ItlRecord album = Assert.Single(document.Albums,
                candidate => ItlDocument.RecordIdOf(candidate) == track.GetAlbumId());
            ItlRecord artist = Assert.Single(document.Artists,
                candidate => ItlDocument.RecordIdOf(candidate) == track.GetArtistId());
            uint albumKey = Key(track.Field((int)ItlDataType.Album)!);
            uint artistKey = Key(artist.Field((int)ItlDataType.ArtistRecordName)!);
            Assert.Equal(albumKey, Key(album.Field((int)ItlDataType.AlbumRecordName)!));
            Assert.Equal(artistKey, Key(track.Field((int)ItlDataType.Artist)!));
            Assert.Equal(artistKey, Key(track.Field((int)ItlDataType.AlbumArtist)!));
            Assert.Equal(artistKey, Key(album.Field((int)ItlDataType.AlbumRecordArtist)!));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        static uint Key(ItlField field) =>
            BinaryPrimitives.ReadUInt32LittleEndian(field.Header.AsSpan(16));
    }

    [Fact]
    public void PrivateCorpusRemainsIdenticalAndXmlVerifiedWhenConfigured()
    {
        string? itl = Environment.GetEnvironmentVariable("DUMPITL_CORPUS_ITL");
        string? xml = Environment.GetEnvironmentVariable("DUMPITL_CORPUS_XML");
        if (string.IsNullOrWhiteSpace(itl) || string.IsNullOrWhiteSpace(xml) || !File.Exists(itl) || !File.Exists(xml))
            return;

        ItlEnvelope envelope = ItlEnvelope.Load(itl);
        byte[] original = (byte[])envelope.Body.Clone();
        ItlDocument document = ItlDocument.Parse(envelope);
        Assert.Equal(original, document.Serialize());
        Assert.DoesNotContain(document.Validate(), issue => issue.Severity == ItlValidationSeverity.Error);

        ItlLibrary library = ItlLibrary.Parse(envelope);
        Assert.Equal(@"Z:\iTunes\AAC", library.MusicFolderPath);
        ItlSmartPlaylist[] smart = [.. library.Playlists.Select(playlist => playlist.Smart).OfType<ItlSmartPlaylist>()];
        Assert.Equal(15, smart.Length);
        foreach (ItlSmartPlaylist playlist in smart)
        {
            (byte[] info, byte[] criteria) = playlist.Encode();
            Assert.Equal(playlist.Info.Raw, info);
            Assert.Equal(playlist.Criteria.Raw, criteria);
        }
        Assert.Contains(smart, playlist => playlist.Criteria.Rules.Any(rule => rule.NestedCriteria is not null));
        Assert.Contains(smart.SelectMany(Flatten), rule => rule.ValueKind == ItlSmartValueKind.String);
        Assert.Contains(smart.SelectMany(Flatten), rule => rule.ValueKind == ItlSmartValueKind.Date && rule.RelativeSeconds != 0);

        string output = RunCli("verify", itl, xml);
        Assert.DoesNotContain("mismatched", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("contentRating ok", output);
        Assert.Contains("clean         ok", output);

        string smartOutput = RunCli("re", itl, "smart", xml);
        Assert.DoesNotContain("differ", smartOutput, StringComparison.OrdinalIgnoreCase);

        static IEnumerable<ItlSmartRule> Flatten(ItlSmartPlaylist playlist) => FlattenCriteria(playlist.Criteria);
        static IEnumerable<ItlSmartRule> FlattenCriteria(ItlSmartCriteria criteria) =>
            criteria.Rules.SelectMany(rule => rule.NestedCriteria is null
                ? [rule]
                : new[] { rule }.Concat(FlattenCriteria(rule.NestedCriteria)));
    }

    private static string RunCli(params string[] arguments)
    {
        string assembly = Path.Combine(AppContext.BaseDirectory, "DumpITL.dll");
        Assert.True(File.Exists(assembly), $"DumpITL executable assembly was not found at {assembly}.");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(assembly);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return stdout;
    }
}
