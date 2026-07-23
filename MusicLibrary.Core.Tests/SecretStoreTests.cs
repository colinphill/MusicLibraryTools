using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class SecretStoreTests
{
    [Fact]
    public async Task SessionStoreRoundTripsAndDeletesSecret()
    {
        var store = new SessionSecretStore();

        await store.WriteAsync("discogs.token", "not-a-real-token");

        Assert.Equal("not-a-real-token",
            await store.ReadAsync("discogs.token"));
        Assert.Equal(SecretStoreKind.SessionOnly, store.Kind);
        Assert.False(store.IsPersistent);

        await store.DeleteAsync("discogs.token");

        Assert.Null(await store.ReadAsync("discogs.token"));
    }

    [Fact]
    public async Task CrossPlatformStoreUsesAvailableNativeBackend()
    {
        var backend = new MemoryBackend();
        var store = new CrossPlatformSecretStore(backend);

        await store.WriteAsync("discogs.token", "not-a-real-token");

        Assert.Equal("not-a-real-token",
            await store.ReadAsync("discogs.token"));
        Assert.Equal(
            SecretStoreKind.WindowsCredentialManager,
            store.Kind);
        Assert.True(store.IsPersistent);
    }

    [Fact]
    public async Task CrossPlatformStoreLatchesToSessionFallback()
    {
        var backend = new UnavailableBackend();
        var store = new CrossPlatformSecretStore(backend);

        await store.WriteAsync("discogs.token", "session-token");

        Assert.Equal(1, backend.CallCount);
        Assert.Equal(SecretStoreKind.SessionOnly, store.Kind);
        Assert.False(store.IsPersistent);
        Assert.Equal("session-token",
            await store.ReadAsync("discogs.token"));
        Assert.Equal(1, backend.CallCount);

        await store.DeleteAsync("discogs.token");

        Assert.Null(await store.ReadAsync("discogs.token"));
        Assert.Equal(1, backend.CallCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("contains spaces")]
    [InlineData("contains/slash")]
    public async Task InvalidKeysAreRejected(string key)
    {
        var store = new SessionSecretStore();

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.WriteAsync(key, "secret"));
    }

    [Fact]
    public async Task OperationsObserveCancellation()
    {
        var store = new SessionSecretStore();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.WriteAsync(
                "discogs.token",
                "secret",
                cancellation.Token));
    }

    private sealed class MemoryBackend : ISecretStoreBackend
    {
        private readonly SessionSecretStore _store = new();

        public SecretStoreKind Kind =>
            SecretStoreKind.WindowsCredentialManager;
        public bool IsPersistent => true;

        public Task<string?> ReadAsync(
            string key,
            CancellationToken ct = default) =>
            _store.ReadAsync(key, ct);

        public Task WriteAsync(
            string key,
            string secret,
            CancellationToken ct = default) =>
            _store.WriteAsync(key, secret, ct);

        public Task DeleteAsync(
            string key,
            CancellationToken ct = default) =>
            _store.DeleteAsync(key, ct);
    }

    private sealed class UnavailableBackend : ISecretStoreBackend
    {
        public int CallCount { get; private set; }

        public SecretStoreKind Kind =>
            SecretStoreKind.LinuxSecretService;
        public bool IsPersistent => true;

        public Task<string?> ReadAsync(
            string key,
            CancellationToken ct = default)
        {
            CallCount++;
            throw Unavailable();
        }

        public Task WriteAsync(
            string key,
            string secret,
            CancellationToken ct = default)
        {
            CallCount++;
            throw Unavailable();
        }

        public Task DeleteAsync(
            string key,
            CancellationToken ct = default)
        {
            CallCount++;
            throw Unavailable();
        }

        private static SecretStoreUnavailableException Unavailable() =>
            new("Native secret store is unavailable.");
    }
}
