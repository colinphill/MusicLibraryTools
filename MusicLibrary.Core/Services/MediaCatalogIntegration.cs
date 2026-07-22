namespace MusicLibrary.Core.Services;

public enum MediaCatalogMutationKind
{
    Refresh,
    Relocate,
    Add,
    Remove,
}

public sealed record MediaCatalogMutation(
    MediaCatalogMutationKind Kind,
    string? OriginalPath,
    string? CurrentPath)
{
    public static MediaCatalogMutation Refresh(string path) =>
        new(MediaCatalogMutationKind.Refresh, path, path);

    public static MediaCatalogMutation Relocate(string originalPath, string currentPath) =>
        new(MediaCatalogMutationKind.Relocate, originalPath, currentPath);

    public static MediaCatalogMutation Add(string path) =>
        new(MediaCatalogMutationKind.Add, null, path);

    public static MediaCatalogMutation Remove(string path) =>
        new(MediaCatalogMutationKind.Remove, path, null);
}

public interface IMediaCatalogMutationSession : IAsyncDisposable
{
    bool Active { get; }

    Task CommitAsync(
        IReadOnlyList<MediaCatalogMutation> mutations,
        CancellationToken ct = default);

    Task CompleteAsync(CancellationToken ct = default);
}

/// <summary>
/// Optional bridge between filesystem mutations and an external media catalog. Integrations are
/// activated by their own configuration and therefore do not make a catalog mandatory.
/// </summary>
public interface IMediaCatalogIntegration
{
    string Id { get; }
    string DisplayName { get; }

    Task<IMediaCatalogMutationSession?> BeginAsync(
        IReadOnlyCollection<string> candidatePaths,
        bool backupFiles,
        CancellationToken ct = default);
}

/// <summary>Adapts the existing transactional binary-iTunes integration to the generic contract.</summary>
public sealed class ItunesMediaCatalogIntegration(
    IItunesMediaMutationService service) : IMediaCatalogIntegration
{
    public string Id => "itunes-itl";
    public string DisplayName => "iTunes library";

    public async Task<IMediaCatalogMutationSession?> BeginAsync(
        IReadOnlyCollection<string> candidatePaths,
        bool backupFiles,
        CancellationToken ct = default)
    {
        IItunesMediaMutationSession session = await service.BeginAsync(
            candidatePaths, backupFiles, ct).ConfigureAwait(false);
        return new Session(session);
    }

    private sealed class Session(IItunesMediaMutationSession inner) : IMediaCatalogMutationSession
    {
        public bool Active => inner.Active;

        public async Task CommitAsync(
            IReadOnlyList<MediaCatalogMutation> mutations,
            CancellationToken ct = default)
        {
            ItunesMediaMutation[] translated = mutations.Select(mutation => mutation.Kind switch
            {
                MediaCatalogMutationKind.Refresh =>
                    ItunesMediaMutation.Refresh(mutation.CurrentPath!),
                MediaCatalogMutationKind.Relocate =>
                    ItunesMediaMutation.Relocate(mutation.OriginalPath!, mutation.CurrentPath!),
                MediaCatalogMutationKind.Add =>
                    ItunesMediaMutation.Add(mutation.CurrentPath!),
                MediaCatalogMutationKind.Remove =>
                    ItunesMediaMutation.Remove(mutation.OriginalPath!),
                _ => throw new ArgumentOutOfRangeException(nameof(mutation.Kind)),
            }).ToArray();
            _ = await inner.CommitAsync(translated, ct).ConfigureAwait(false);
        }

        public Task CompleteAsync(CancellationToken ct = default) => inner.CompleteAsync(ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

internal sealed class MediaCatalogMutationSessionGroup : IAsyncDisposable
{
    private readonly IReadOnlyList<IMediaCatalogMutationSession> _sessions;

    private MediaCatalogMutationSessionGroup(
        IReadOnlyList<IMediaCatalogMutationSession> sessions) => _sessions = sessions;

    public static async Task<MediaCatalogMutationSessionGroup?> BeginAsync(
        IReadOnlyList<IMediaCatalogIntegration> integrations,
        IReadOnlyCollection<string> candidatePaths,
        bool backupFiles,
        CancellationToken ct)
    {
        if (integrations.Count == 0)
            return null;

        var sessions = new List<IMediaCatalogMutationSession>();
        try
        {
            foreach (IMediaCatalogIntegration integration in integrations)
            {
                IMediaCatalogMutationSession? session = await integration.BeginAsync(
                    candidatePaths, backupFiles, ct).ConfigureAwait(false);
                if (session is not null)
                    sessions.Add(session);
            }
            return sessions.Count == 0 ? null : new(sessions);
        }
        catch
        {
            foreach (IMediaCatalogMutationSession session in sessions.AsEnumerable().Reverse())
                await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task CommitAsync(
        IReadOnlyList<MediaCatalogMutation> mutations,
        CancellationToken ct)
    {
        foreach (IMediaCatalogMutationSession session in _sessions)
            if (session.Active)
                await session.CommitAsync(mutations, ct).ConfigureAwait(false);
    }

    public async Task CompleteAsync(CancellationToken ct)
    {
        foreach (IMediaCatalogMutationSession session in _sessions)
            if (session.Active)
                await session.CompleteAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IMediaCatalogMutationSession session in _sessions.AsEnumerable().Reverse())
            await session.DisposeAsync().ConfigureAwait(false);
    }
}
