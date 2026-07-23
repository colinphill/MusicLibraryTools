using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MusicLibrary.Core.Services;

public sealed record MusicBrainzCacheEntry<T>(
    T Value,
    DateTimeOffset RetrievedAtUtc,
    bool IsFresh);

public interface IMusicBrainzReleaseCache
{
    Task<MusicBrainzCacheEntry<T>?> ReadAsync<T>(
        string key,
        TimeSpan maximumAge,
        CancellationToken ct = default);

    Task WriteAsync<T>(
        string key,
        T value,
        DateTimeOffset retrievedAtUtc,
        CancellationToken ct = default);
}

/// <summary>
/// Small application-local SQLite cache for code-backed metadata providers.
/// Values are provider model snapshots, not portable library configuration.
/// </summary>
public sealed class MusicBrainzReleaseCache : IMusicBrainzReleaseCache
{
    private const int MaximumEntries = 2_000;
    private readonly string _databasePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public MusicBrainzReleaseCache() : this(Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "MusicLibraryTools",
        "metadata-source-cache.db"))
    {
    }

    public MusicBrainzReleaseCache(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    public async Task<MusicBrainzCacheEntry<T>?> ReadAsync<T>(
        string key,
        TimeSpan maximumAge,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (maximumAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection =
                await OpenAsync(ct).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT payload, retrieved_utc
                FROM MusicBrainzCache
                WHERE cache_key = $key;
                """;
            command.Parameters.AddWithValue("$key", key);
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;
            string payload = reader.GetString(0);
            if (!DateTimeOffset.TryParse(
                    reader.GetString(1),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out DateTimeOffset retrievedAtUtc))
                return null;
            T? value = JsonSerializer.Deserialize<T>(payload);
            if (value is null)
                return null;
            bool fresh = DateTimeOffset.UtcNow - retrievedAtUtc <= maximumAge;
            return new(value, retrievedAtUtc, fresh);
        }
        catch (Exception error) when (
            error is SqliteException or IOException or JsonException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync<T>(
        string key,
        T value,
        DateTimeOffset retrievedAtUtc,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        string payload = JsonSerializer.Serialize(value);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection =
                await OpenAsync(ct).ConfigureAwait(false);
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(ct)
                    .ConfigureAwait(false);
            await using (SqliteCommand upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText =
                    """
                    INSERT INTO MusicBrainzCache(
                        cache_key, payload, retrieved_utc)
                    VALUES($key, $payload, $retrieved)
                    ON CONFLICT(cache_key) DO UPDATE SET
                        payload = excluded.payload,
                        retrieved_utc = excluded.retrieved_utc;
                    """;
                upsert.Parameters.AddWithValue("$key", key);
                upsert.Parameters.AddWithValue("$payload", payload);
                upsert.Parameters.AddWithValue(
                    "$retrieved", retrievedAtUtc.ToString("O"));
                await upsert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await using (SqliteCommand prune = connection.CreateCommand())
            {
                prune.Transaction = transaction;
                prune.CommandText =
                    """
                    DELETE FROM MusicBrainzCache
                    WHERE cache_key IN (
                        SELECT cache_key
                        FROM MusicBrainzCache
                        ORDER BY retrieved_utc DESC
                        LIMIT -1 OFFSET $maximum);
                    """;
                prune.Parameters.AddWithValue("$maximum", MaximumEntries);
                await prune.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception error) when (
            error is SqliteException or IOException)
        {
            // Cache availability never prevents a live provider lookup.
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        string? directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        if (!_initialized)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS MusicBrainzCache(
                    cache_key TEXT PRIMARY KEY NOT NULL,
                    payload TEXT NOT NULL,
                    retrieved_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_MusicBrainzCache_Retrieved
                    ON MusicBrainzCache(retrieved_utc);
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _initialized = true;
        }
        return connection;
    }
}
