using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace iTunes.Binary;

/// <summary>Builds the track-identity keys used by imported type-514 playback state.</summary>
public static class ItlPlaybackStateKey
{
    /// <summary>
    /// Builds the normal iTunes 12.13.10.3 identity: a decimal Store Item ID when present,
    /// otherwise lowercase MD5 of the UTF-8 title, artist, and album concatenated in that order
    /// without separators. Title is required; missing artist or album values are omitted.
    /// </summary>
    /// <remarks>
    /// A separate native branch exists for a special media class and hashes two normalized
    /// byte-string fields. Its field meanings and eligibility flag are not yet proven, so this
    /// method deliberately models only the ordinary branch.
    /// </remarks>
    public static string? ForOrdinaryTrack(ItlTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return ForOrdinaryMetadata(track.StoreItemId, track.Title, track.Artist, track.Album);
    }

    /// <inheritdoc cref="ForOrdinaryTrack(ItlTrack)"/>
    public static string? ForOrdinaryMetadata(
        uint storeItemId,
        string? title,
        string? artist = null,
        string? album = null)
    {
        if (storeItemId != 0)
            return storeItemId.ToString(CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(title))
            return null;

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        Append(title);
        if (!string.IsNullOrEmpty(artist)) Append(artist);
        if (!string.IsNullOrEmpty(album)) Append(album);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

        void Append(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value));
    }

    /// <summary>
    /// Builds the special podcast fallback key from feed URL followed by episode URL. Native iTunes
    /// removes trailing ASCII spaces and duplicate slashes, while retaining the pair in <c>://</c>,
    /// before hashing the two normalized UTF-8 byte sequences without a separator.
    /// </summary>
    /// <remarks>
    /// Native iTunes prefers <see cref="ItlDataType.PodcastFeedUrl"/> and falls back to
    /// <see cref="ItlDataType.PodcastRssUrl"/> for the first value. Its precise media-flag eligibility
    /// test is not yet exposed by ITLTools, so callers must only use this method for podcast records.
    /// </remarks>
    public static string? ForPodcastMetadata(string? feedUrl, string? episodeUrl)
    {
        byte[] feed = NormalizePodcastUrl(feedUrl);
        byte[] episode = NormalizePodcastUrl(episodeUrl);
        if (feed.Length == 0 || episode.Length == 0)
            return null;

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        hash.AppendData(feed);
        hash.AppendData(episode);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static byte[] NormalizePodcastUrl(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return [];

        byte[] source = Encoding.UTF8.GetBytes(value);
        int length = source.Length;
        while (length > 0 && source[length - 1] == (byte)' ') length--;
        if (length == 0) return [];

        byte[] result = new byte[length];
        int written = 0;
        for (int index = 0; index < length; index++)
        {
            byte current = source[index];
            if (current == (byte)'/' && index > 0 && source[index - 1] == (byte)'/' &&
                !(index > 1 && source[index - 2] == (byte)':'))
                continue;
            result[written++] = current;
        }
        return result.AsSpan(0, written).ToArray();
    }
}
