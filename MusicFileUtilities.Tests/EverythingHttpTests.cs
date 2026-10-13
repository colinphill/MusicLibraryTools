using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MetadataCaching;
using MusicLibraryTools;
using Xunit;

namespace MusicFileUtilities.Tests;

public class EverythingHttpTests
{
    private static readonly DateTime Modified = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private static object FileResult(string name, string path = @"D:\Music", long size = 42) => new
    {
        type = "file", name, path, size = size.ToString(), date_modified = Modified.ToFileTimeUtc().ToString(),
    };

    private static string Page(long total, params object[] results) => JsonSerializer.Serialize(new { totalResults = total, results });

    [Fact]
    public void PagesMapPathsAndDecodeFileTimesWithoutFilesystemAccess()
    {
        var requests = new List<Uri>();
        using var client = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri);
            return Json(requests.Count == 1
                ? Page(3, FileResult("Björk & #1.flac"), new { type = "folder", name = "Album", path = @"D:\Music" })
                : Page(3, FileResult("song.mp3", @"D:\Music\日本語")));
        }));
        string target = Path.Combine(Path.GetTempPath(), "does-not-exist", Guid.NewGuid().ToString("N"));
        var files = EverythingHttpEnumerator.Enumerate(target, new("http://localhost:8080/", @"D:\Music"),
            TestContext.Current.CancellationToken, client, pageSize: 2).ToArray();
        Assert.Equal(2, files.Length);
        Assert.Equal(Path.Combine(target, "Björk & #1.flac"), files[0].Name);
        Assert.Equal(Path.Combine(target, "日本語", "song.mp3"), files[1].Name);
        Assert.All(files, file => { Assert.Equal(Modified, file.Modified); Assert.Equal(42, file.Size); });
        Assert.Contains("offset=0", requests[0].Query);
        Assert.Contains("offset=2", requests[1].Query);
        Assert.Contains("path=1&regex=1&sort=path", requests[0].Query);
        Assert.Contains("search=" + Uri.EscapeDataString(@"^D:\\Music\\"), requests[0].Query);
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("traversal")]
    [InlineData("filename")]
    [InlineData("drive")]
    [InlineData("trailing-space")]
    [InlineData("size")]
    [InlineData("date")]
    [InlineData("empty-page")]
    [InlineData("changed-total")]
    [InlineData("duplicate")]
    [InlineData("html")]
    [InlineData("unauthorized")]
    public void InvalidOrIncompleteResponsesFailEnumeration(string failure)
    {
        int call = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            call++;
            return failure switch
            {
                "outside" => Json(Page(1, FileResult("song.flac", @"D:\MusicOther"))),
                "traversal" => Json(Page(1, FileResult("song.flac", @"D:\Music\..\Other"))),
                "filename" => Json(Page(1, FileResult(@"..\song.flac"))),
                "drive" => Json(Page(1, FileResult("song.flac", @"D:\Music\C:\Other"))),
                "trailing-space" => Json(Page(1, FileResult("song.flac", @"D:\Music\.. "))),
                "size" => Json(Page(1, FileResult("song.flac", size: -1))),
                "date" => Json(Page(1, new { type = "file", name = "song.flac", path = @"D:\Music", size = "42" })),
                "empty-page" => Json(Page(2)),
                "changed-total" => Json(call == 1 ? Page(2, FileResult("song.flac")) : Page(1)),
                "duplicate" => Json(Page(2, FileResult("song.flac"))),
                "html" => Json("<html>login</html>"),
                _ => new(HttpStatusCode.Unauthorized),
            };
        }));
        Assert.ThrowsAny<Exception>(() => EverythingHttpEnumerator.Enumerate("unused", new("http://localhost/", @"D:\Music"),
            TestContext.Current.CancellationToken, client, pageSize: 1).ToArray());
    }

    [Fact]
    public void SkipsUnsupportedFilesAndItlpPackagesAndHonorsCancellation()
    {
        using var client = new HttpClient(new Handler(_ => Json(Page(3,
            FileResult("cover.jpg"), FileResult("song.flac", @"D:\Music\Extras.itlp\Album"), FileResult("song.flac")))));
        Assert.Single(EverythingHttpEnumerator.Enumerate("unused", new("http://localhost/", @"D:\Music"),
            TestContext.Current.CancellationToken, client));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => EverythingHttpEnumerator.Enumerate("unused",
            new("http://localhost/", @"D:\Music"), cancellation.Token, client).ToArray());
    }

    [Fact]
    public void BasicAuthenticationReadsPasswordFromEnvironment()
    {
        string variable = "MLT_TEST_PASSWORD_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "secret");
        try
        {
            using var client = new HttpClient(new Handler(request =>
            {
                Assert.Equal("Basic", request.Headers.Authorization.Scheme);
                Assert.Equal("user:secret", Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter)));
                return Json(Page(0));
            }));
            Assert.Empty(EverythingHttpEnumerator.Enumerate("unused", new("http://localhost/", @"D:\Music", "user", variable),
                TestContext.Current.CancellationToken, client));
            Environment.SetEnvironmentVariable(variable, null);
            Assert.Throws<InvalidDataException>(() => EverythingHttpEnumerator.Enumerate("unused",
                new("http://localhost/", @"D:\Music", "user", variable), TestContext.Current.CancellationToken, client).ToArray());
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public async Task IndexerUsesHttpForSelectedRootAndPreservesCacheOnPartialFailure()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "mlt_http_" + Guid.NewGuid().ToString("N"));
        string remote = Path.Combine(workspace, "remote");
        string local = Path.Combine(workspace, "local");
        Directory.CreateDirectory(remote);
        Directory.CreateDirectory(local);
        File.Copy(MediaFixtures.Path_("sample.flac"), Path.Combine(remote, "sample.flac"));
        File.Copy(MediaFixtures.Path_("sample.mp3"), Path.Combine(remote, "unlisted.mp3"));
        File.Copy(MediaFixtures.Path_("sample.ogg"), Path.Combine(local, "sample.ogg"));
        var info = new FileInfo(Path.Combine(remote, "sample.flac"));
        object result = new { type = "file", name = info.Name, path = @"D:\IndexedMusic", size = info.Length.ToString(), date_modified = info.LastWriteTimeUtc.ToFileTimeUtc().ToString() };
        var mp3Info = new FileInfo(Path.Combine(remote, "unlisted.mp3"));
        object excludedByFormat = new { type = "file", name = mp3Info.Name, path = @"D:\IndexedMusic", size = mp3Info.Length.ToString(), date_modified = mp3Info.LastWriteTimeUtc.ToFileTimeUtc().ToString() };
        bool fail = false;
        await using var server = new Server(request => request.Contains("offset=0")
            ? Page(fail ? 3 : 2, result, excludedByFormat) : Page(3));
        try
        {
            using var database = MetadataDatabase.OpenDatabase("sqlite:" + Path.Combine(workspace, "cache.db"));
            var roots = new[]
            {
                new ScanRootDefinition(remote, ["Archive"]) { EverythingHttp = new(server.Url, @"D:\IndexedMusic"), Formats = [".flac"] },
                new ScanRootDefinition(local, ["Portable"]),
            };
            var first = await database.IndexFilesAsync(roots, ct: TestContext.Current.CancellationToken);
            Assert.Equal(2, first.Added);
            Assert.Equal(2, database.BuildCache([remote, local]).FileCache.Count);
            Assert.All(database.GetScanRootHealth(), health => Assert.Equal(ScanRootState.Healthy, health.State));
            var second = await database.IndexFilesAsync(roots, ct: TestContext.Current.CancellationToken);
            Assert.Equal(2, second.Unchanged);
            // Force a later page to fail after the first file was already processed. A previously
            // indexed but now unseen file must remain cached until a complete scan succeeds.
            File.Move(Path.Combine(remote, "unlisted.mp3"), Path.Combine(remote, "unseen.mp3"));
            await database.IndexFilesAsync([new ScanRootDefinition(remote, ["Archive"]), roots[1]], ct: TestContext.Current.CancellationToken);
            fail = true;
            var partial = await database.IndexFilesAsync(roots, ct: TestContext.Current.CancellationToken);
            Assert.Equal(0, partial.Removed);
            Assert.Equal(3, database.BuildCache([remote, local]).FileCache.Count);
            Assert.False(database.LastIndexCompletedSuccessfully);
            Assert.Contains(database.GetScanRootHealth(), health => health.Root == remote && health.State == ScanRootState.Degraded);
            fail = false;
            var recovered = await database.IndexFilesAsync(roots, ct: TestContext.Current.CancellationToken);
            Assert.Equal(1, recovered.Removed);
            Assert.Equal(2, database.BuildCache([remote, local]).FileCache.Count);
            Assert.True(database.LastIndexCompletedSuccessfully);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(workspace, true);
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    // A real loopback server exercises the indexer's default HttpClient and error propagation.
    private sealed class Server : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;
        public string Url { get; }
        public Server(Func<string, string> respond)
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
            _serve = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        using var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                        await using var stream = socket.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                        string request = await reader.ReadLineAsync(_stop.Token);
                        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))) { }
                        byte[] body = Encoding.UTF8.GetBytes(respond(request));
                        byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(header, _stop.Token);
                        await stream.WriteAsync(body, _stop.Token);
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            });
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _serve;
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
