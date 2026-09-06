using System;
using System.Collections.Generic;

namespace BingWallpaper;

/// <summary>
/// The play order of the random rotation: every favourite exactly once per round, in
/// a shuffled order, with a cursor that walks it in both directions.
///
/// <para>
/// A shuffled list plus a cursor, rather than a queue that is consumed. Drawing costs
/// the same either way, but this buys three things a queue cannot: stepping back to
/// the picture before this one, which is what the "上一张" row does while the rotation
/// is on; a reshuffle that can see what the last round closed with and refuse to open
/// the new one with it; and a folder that changes mid round being repaired in place
/// instead of throwing the round away. That last one matters most - the promise here
/// is that a round covers every picture, and starting over would break it every time
/// someone favourites something.
/// </para>
/// <para>
/// Not persisted, for the reason the stepping list is not: where a round has got to
/// is state of this session, and writing it out would either turn the configuration
/// file into a state file or add a disk write to every change of wallpaper. A restart
/// opens a new round, which is not something anyone can tell apart from a shuffle.
/// </para>
/// <para>
/// Single threaded by construction: every caller is on the UI thread, coming from the
/// tray menu or the rotation timer.
/// </para>
/// </summary>
internal sealed class ShufflePlaylist
{
    /// <summary>
    /// One instance for the life of the playlist, never a fresh one per shuffle: on
    /// .NET Framework the parameterless Random is seeded from Environment.TickCount,
    /// so two instances built within the same millisecond hand out the same sequence.
    /// </summary>
    private readonly Random _random = new Random();

    private readonly List<string> _order = new List<string>();

    /// <summary>Index of the picture that is playing, or -1 before the first draw.</summary>
    private int _cursor = -1;

    public int Count => _order.Count;

    /// <summary>Position within the round, 1 based. For the log line.</summary>
    public int Position => _cursor + 1;

    /// <summary>Whether this round has a picture before the current one.</summary>
    public bool HasPrevious => _cursor > 0;

    /// <summary>Forgets the round. The next draw shuffles from scratch.</summary>
    public void Clear()
    {
        _order.Clear();
        _cursor = -1;
    }

    /// <summary>
    /// Brings the order into line with the folder, keeping the round.
    ///
    /// <para>
    /// Called before every draw rather than kept in step by whoever writes to
    /// favorites\, for the reason the stepping rows re-enumerate: the folder is the
    /// only state there is, and Explorer, the picker and the tray menu all write to
    /// it, so a cached order here would need invalidating from three directions.
    /// </para>
    /// </summary>
    public void Sync(List<FavoriteItem> items)
    {
        HashSet<string> present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < items.Count; i++)
        {
            present.Add(items[i].FileName);
        }

        int removed = 0;
        for (int i = _order.Count - 1; i >= 0; i--)
        {
            if (present.Contains(_order[i]))
            {
                continue;
            }

            _order.RemoveAt(i);
            removed++;

            // Everything at or before the cursor took a slot the cursor was counting
            // on. Removing the playing picture itself leaves the cursor on its
            // predecessor, so the next draw moves into the slot it vacated rather
            // than stepping over whatever slid into it.
            if (i <= _cursor)
            {
                _cursor--;
            }
        }

        // Built after the removals, so this pass only looks for names the round has
        // never seen.
        HashSet<string> known = new HashSet<string>(_order, StringComparer.OrdinalIgnoreCase);
        int added = 0;
        for (int i = 0; i < items.Count; i++)
        {
            string fileName = items[i].FileName;
            if (!known.Add(fileName))
            {
                continue;
            }

            // Somewhere after the cursor, at random: a favourite added mid round
            // belongs to this round, which is what "every picture once per round"
            // has to mean for someone who is adding them while it plays.
            _order.Insert(_random.Next(_cursor + 1, _order.Count + 1), fileName);
            added++;
        }

        if (added != 0 || removed != 0)
        {
            Logger.Debug(
                "shuffle: order updated added=" + added + " removed=" + removed + " count=" + _order.Count);
        }
    }

    /// <summary>
    /// The next picture of the round, reshuffling once the round is out. Null only
    /// when there is nothing to play.
    /// </summary>
    public string? Next()
    {
        if (_order.Count == 0)
        {
            return null;
        }

        if (_cursor + 1 >= _order.Count)
        {
            // Reshuffled here rather than when the last picture was handed out, so
            // that anything Sync appended in the meantime still belongs to the round
            // that was running.
            Reshuffle(_cursor >= 0 ? _order[_cursor] : null);
            _cursor = -1;
        }

        _cursor++;
        return _order[_cursor];
    }

    /// <summary>
    /// The picture before the current one, or null at the start of the round. It does
    /// not wrap: the round before this one was a different shuffle and is gone, so
    /// there is nothing behind the first entry to go back to.
    /// </summary>
    public string? Previous()
    {
        if (_cursor <= 0)
        {
            return null;
        }

        _cursor--;
        return _order[_cursor];
    }

    /// <summary>Draws the order of a new round.</summary>
    /// <param name="avoidFirst">
    /// The picture the finished round closed with, when there was one. A new round
    /// opening on it is the one thing a shuffle must not look like, and with n
    /// pictures it happens on its own once every n rounds.
    /// </param>
    private void Reshuffle(string? avoidFirst)
    {
        // Fisher-Yates, in place: no second list, and every permutation equally likely.
        for (int i = _order.Count - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (_order[i], _order[j]) = (_order[j], _order[i]);
        }

        if (avoidFirst is not null
            && _order.Count > 1
            && string.Equals(_order[0], avoidFirst, StringComparison.OrdinalIgnoreCase))
        {
            int swap = _random.Next(1, _order.Count);
            (_order[0], _order[swap]) = (_order[swap], _order[0]);
        }

        Logger.Debug("shuffle: reshuffled count=" + _order.Count);
    }
}
