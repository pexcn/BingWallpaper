using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BingWallpaper;

/// <summary>
/// Keeps the history thumbnails as the bytes they arrived as.
///
/// <para>
/// The history window is built fresh on every open and disposed when it closes, so
/// nothing it owns outlives it - and downloading eight pictures again to show a list
/// the user was just looking at is a poor trade. The bytes live one step above the
/// window instead. Bytes, not bitmaps: a 400x240 JPEG is a few tens of KB, where the
/// decoded bitmap a tile paints from is 400 * 240 * 4 bytes, so this is both the
/// half that survives usefully and the cheap half to hold on to.
/// </para>
/// <para>
/// Not a general purpose cache. It holds the thumbnails of whichever image list is
/// current and nothing else, which is what bounds it to eight entries without an
/// eviction policy, a size cap or a clock - a tray process that runs for weeks would
/// otherwise collect eight more of these every daily refresh.
/// </para>
/// <para>
/// Everything here runs on the UI thread: the history window awaits its downloads
/// with ConfigureAwait(true), and <see cref="Retain"/> is driven by the tray context
/// as it replaces its own list. The dictionary therefore needs no locking.
/// </para>
/// </summary>
internal sealed class ThumbnailCache
{
    private readonly Dictionary<string, byte[]> _entries = new(StringComparer.Ordinal);
    private readonly BingClient _client;

    public ThumbnailCache(BingClient client)
    {
        _client = client;
    }

    /// <summary>Returns the thumbnail for <paramref name="url"/>, fetching it once.</summary>
    public async Task<byte[]> GetAsync(string url, CancellationToken cancellationToken)
    {
        if (_entries.TryGetValue(url, out byte[] cached))
        {
            return cached;
        }

        byte[] bytes = await _client
            .DownloadBytesAsync(url, cancellationToken)
            .ConfigureAwait(true);

        _entries[url] = bytes;
        return bytes;
    }

    /// <summary>
    /// Drops every thumbnail that does not belong to <paramref name="images"/>. Called
    /// wherever the current list is replaced, which is what keeps this bounded.
    /// </summary>
    public void Retain(IReadOnlyList<BingImageInfo> images)
    {
        HashSet<string> keep = new(StringComparer.Ordinal);
        for (int i = 0; i < images.Count; i++)
        {
            keep.Add(images[i].GetThumbnailUrl());
        }

        // Collected first: a dictionary cannot be written to while it is enumerated.
        List<string> stale = new();
        foreach (string url in _entries.Keys)
        {
            if (!keep.Contains(url))
            {
                stale.Add(url);
            }
        }

        foreach (string url in stale)
        {
            _entries.Remove(url);
        }
    }
}
