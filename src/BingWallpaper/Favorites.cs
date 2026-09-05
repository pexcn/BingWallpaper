using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BingWallpaper;

/// <summary>
/// One favourited picture, as the picker shows it.
///
/// A struct because the list is the whole point: a few thousand entries have to cost
/// bytes rather than objects, and everything below the file name is either read off
/// the directory entry or filled in from favorites.txt afterwards.
/// </summary>
internal readonly struct FavoriteItem
{
    public FavoriteItem(
        string fileName,
        string title,
        string copyrightLink,
        string displayDate,
        bool isBingImage,
        DateTime sortKey,
        long length)
    {
        FileName = fileName;
        Title = title;
        CopyrightLink = copyrightLink;
        DisplayDate = displayDate;
        IsBingImage = isBingImage;
        SortKey = sortKey;
        Length = length;
    }

    /// <summary>Name inside favorites\, and the key of everything else.</summary>
    public string FileName { get; }

    /// <summary>
    /// What to show under the picture: the Bing title, or the file name with the date
    /// taken out of it - the tile draws the date on its own line - when that is all
    /// there is to go on.
    /// </summary>
    public string Title { get; }

    /// <summary>Bing's "see this picture" URL, or empty - it cannot be derived.</summary>
    public string CopyrightLink { get; }

    public string DisplayDate { get; }

    /// <summary>
    /// Whether the file name parses as one of ours. It is what decides which context
    /// menu an entry gets: only a Bing picture may be un-favourited, because only for
    /// it is wallpapers\ a place it can go back to (see Favorites.Remove).
    /// </summary>
    public bool IsBingImage { get; }

    /// <summary>
    /// The date the picture is from: publication date for our own files, a date read
    /// out of the name for the rest, and last write time only when the name has none.
    /// </summary>
    public DateTime SortKey { get; }

    public long Length { get; }

    public FavoriteItem WithMetadata(string title, string copyrightLink)
        => new FavoriteItem(FileName, title, copyrightLink, DisplayDate, IsBingImage, SortKey, Length);
}

/// <summary>
/// The favourites folder: listing it, and moving pictures in and out of it.
///
/// <para>
/// The directory is the only state there is. "Is this favourited" means "is the file
/// in favorites\", the order and the sizes come from one enumeration, and
/// favorites.txt is a title cache - losing it costs words, never pictures. That is
/// what makes a user dropping their own files in here an ordinary case instead of an
/// edge case: there is no second copy of the state to reconcile with.
/// </para>
/// <para>
/// The four writes this class makes are: move a file in, move one of *ours* back
/// out, send one of *theirs* to the recycle bin, and replace favorites.txt. A
/// picture of ours is never deleted from here - un-favouriting is what it has
/// instead, and that only moves it.
/// </para>
/// </summary>
internal static class Favorites
{
    /// <summary>
    /// What SystemParametersInfoW accepts on Windows 10 and what a person is likely to
    /// drop in here. Anything else in the folder is ignored rather than reported.
    /// </summary>
    private static readonly string[] PictureExtensions = { ".jpg", ".jpeg", ".png", ".bmp" };

    /// <summary>
    /// The earliest year a four digit run in a file name is read as one. Below it a
    /// run is a serial number rather than a photo, and DateTime.DaysInMonth throws
    /// outright at year zero.
    /// </summary>
    private const int MinNameYear = 1900;

    /// <summary>
    /// The longest an ASCII part of a file name can be and still be read as a market
    /// code rather than as a name. See <see cref="IsWorthShowing"/>.
    /// </summary>
    private const int ShortestAsciiCode = 2;

    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Title and source link of one Bing picture, as stored in favorites.txt.</summary>
    private readonly struct IndexRecord
    {
        public IndexRecord(string title, string copyrightLink)
        {
            Title = title;
            CopyrightLink = copyrightLink;
        }

        public string Title { get; }

        public string CopyrightLink { get; }
    }

    /// <summary>
    /// Lists the folder, newest first. One enumeration answers every column the picker
    /// shows except the Bing titles: DirectoryInfo.EnumerateFiles pre-fills Length and
    /// LastWriteTime from the directory entry itself, so the sizes and the sort keys
    /// cost no extra call. Nothing is decoded here.
    /// </summary>
    public static List<FavoriteItem> Scan()
    {
        List<FavoriteItem> items = new List<FavoriteItem>();
        DirectoryInfo directory = new DirectoryInfo(Paths.FavoritesDirectory);
        if (!directory.Exists)
        {
            return items;
        }

        try
        {
            foreach (FileInfo file in directory.EnumerateFiles())
            {
                if (!IsPicture(file.Name))
                {
                    continue;
                }

                string name = Path.GetFileNameWithoutExtension(file.Name);
                bool isBing = BingImageInfo.TryParseFileName(file.Name, out _, out string imageId);

                // The date a file of ours carries in front of its name is the same
                // eight digits the scanner reads off any other name, so there is one
                // parse here rather than two.
                DateTime sortKey;
                bool dated = TryParseDateInName(name, out sortKey, out int dateStart, out int dateLength);
                if (!dated)
                {
                    sortKey = file.LastWriteTime;
                }

                // Our own picture wears its id and nothing else: the date is drawn on
                // its own line above the caption and the resolution suffix describes
                // the download, not the photograph. LoadTitles puts the real title
                // back whenever favorites.txt still has one - it is the collections
                // carried over from another machine that end up wearing the id.
                string title = IsOurFileName(name, imageId)
                    ? imageId
                    : dated
                        ? StripDate(name, dateStart, dateLength)
                        : name;

                string displayDate = sortKey.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                items.Add(new FavoriteItem(
                    file.Name,
                    title,
                    string.Empty,
                    displayDate,
                    isBing,
                    sortKey,
                    file.Length));
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("favorites: listing the folder failed error=" + ex.Message);
        }

        // Newest first, and by name within a day so that the order does not wobble
        // between two openings of the window.
        items.Sort(static (left, right) =>
        {
            int byDate = right.SortKey.CompareTo(left.SortKey);
            return byDate != 0
                ? byDate
                : string.Compare(right.FileName, left.FileName, StringComparison.OrdinalIgnoreCase);
        });

        return items;
    }

    /// <summary>
    /// Fills in the Bing titles and source links from favorites.txt.
    ///
    /// Separate from <see cref="Scan"/> on purpose: the recent tab needs the file
    /// names to draw its stars and nothing else, so a window that is only opened to
    /// look at today's picture never touches this file.
    /// </summary>
    public static void LoadTitles(List<FavoriteItem> items)
    {
        Dictionary<string, IndexRecord> index = ReadIndex();
        if (index.Count == 0)
        {
            return;
        }

        for (int i = 0; i < items.Count; i++)
        {
            if (index.TryGetValue(items[i].FileName, out IndexRecord record))
            {
                items[i] = items[i].WithMetadata(record.Title, record.CopyrightLink);
            }
        }
    }

    /// <summary>Whether <paramref name="fileName"/> is in the favourites folder.</summary>
    public static bool Contains(string fileName)
        => File.Exists(Path.Combine(Paths.FavoritesDirectory, fileName));

    /// <summary>
    /// Reads the stored title of a single picture. The caller is expected to have a
    /// reason to believe there is one - this opens the file, and the whole point of
    /// the format is that it is read once per window, not once per lookup.
    /// </summary>
    public static bool TryGetMetadata(string fileName, out string title, out string copyrightLink)
    {
        if (ReadIndex().TryGetValue(fileName, out IndexRecord record))
        {
            title = record.Title;
            copyrightLink = record.CopyrightLink;
            return true;
        }

        title = string.Empty;
        copyrightLink = string.Empty;
        return false;
    }

    /// <summary>
    /// Favourites a cached picture: moves it from wallpapers\ into favorites\ and
    /// records its title.
    ///
    /// <para>
    /// A move, not a copy - same volume, so it is a rename: atomic, instant and it
    /// does not store the picture twice. Windows has already transcoded the wallpaper
    /// into its own copy by the time SystemParametersInfoW returns, so no handle is
    /// held on the file even when it is the one on the desktop.
    /// </para>
    /// <para>
    /// The file is moved first and the title written after: losing the title costs a
    /// line of text, losing the picture is what this whole feature exists to prevent.
    /// </para>
    /// </summary>
    public static bool Add(string fileName, string title, string copyrightLink)
    {
        string source = Path.Combine(Paths.WallpaperDirectory, fileName);
        string destination = Path.Combine(Paths.FavoritesDirectory, fileName);

        try
        {
            Paths.EnsureFavoritesDirectory();
            if (File.Exists(destination))
            {
                // Already favourited. The copy in the daily cache - if there is one -
                // is left where it is: it ages out under KeepDays on its own, and
                // deleting it here would be this class touching a picture it has no
                // business touching.
                Logger.Info("favorites: already present file=" + fileName);
            }
            else
            {
                File.Move(source, destination);
                Logger.Info("favorites: added file=" + fileName);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("favorites: adding failed file=" + fileName, ex);
            return false;
        }

        // Only our own pictures get a line: everything about a file the user dropped in
        // here is readable from the directory, so there would be nothing to write.
        if (BingImageInfo.TryParseFileName(fileName, out _, out _))
        {
            Dictionary<string, IndexRecord> index = ReadIndex();
            index[fileName] = new IndexRecord(Sanitize(title), Sanitize(copyrightLink));
            SaveIndex(index);
        }

        return true;
    }

    /// <summary>
    /// Un-favourites a Bing picture: moves it back into wallpapers\, where it ages
    /// under KeepDays like any other cached day.
    ///
    /// <para>
    /// Only ours, and this is the guard rather than a rule the UI is trusted to
    /// follow. wallpapers\ is the cleanup pass's territory: a file the user copied in
    /// carries the last write time of *their* original, so the next cleanup would
    /// delete it - and a .png would not even match the "*.jpg" it enumerates, leaving
    /// a file that is invisible in the UI and never removed. One deletes too eagerly,
    /// the other never; neither is a place to put a picture we promised to keep.
    /// </para>
    /// <para>
    /// The retention clock is restarted on the way out. A move keeps the last write
    /// time, so a picture that sat in favourites\ for longer than KeepDays would land
    /// back in wallpapers\ already expired and be deleted by the very next cleanup
    /// pass - one click, and the picture is gone with nothing said. Whatever else
    /// un-favouriting means, it does not mean delete, so a file leaves here with a
    /// full retention period ahead of it. The date the picture is *from* lives in its
    /// name and is what the UI sorts and shows; the timestamp is only what the
    /// cleanup pass counts, which is what makes it the right thing to touch here.
    /// </para>
    /// </summary>
    public static bool Remove(string fileName)
    {
        if (!BingImageInfo.TryParseFileName(fileName, out _, out _))
        {
            Logger.Warn("favorites: refusing to move a user supplied file out file=" + fileName);
            return false;
        }

        string source = Path.Combine(Paths.FavoritesDirectory, fileName);
        string destination = Path.Combine(Paths.WallpaperDirectory, fileName);

        try
        {
            Paths.EnsureWallpaperDirectory();

            // MoveOverwrite rather than Move: a copy of the same day may have been
            // downloaded again into the cache. Whatever it replaces is in wallpapers\,
            // never in favorites\.
            Paths.MoveOverwrite(source, destination);
            Touch(destination);
            Logger.Info("favorites: removed file=" + fileName);
        }
        catch (Exception ex)
        {
            Logger.Error("favorites: removing failed file=" + fileName, ex);
            return false;
        }

        // Only rewrite when there was something to rewrite. A picture with no line -
        // one carried over from another machine, say - costs no disk write here.
        Dictionary<string, IndexRecord> index = ReadIndex();
        if (index.Remove(fileName))
        {
            SaveIndex(index);
        }

        return true;
    }

    /// <summary>
    /// Dates a file to now, so that the retention pass counts from the moment it
    /// arrived in wallpapers\ rather than from whenever it was downloaded.
    ///
    /// <para>
    /// Best effort: a stamp that fails costs the picture its retention period, not
    /// the picture, and failing the whole un-favourite over it would be the worse
    /// trade. Warn rather than Debug - what it predicts is a wallpaper disappearing
    /// on some later cleanup, which is exactly what the log gets read to explain.
    /// </para>
    /// </summary>
    private static void Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            Logger.Warn("favorites: restarting the retention clock failed file=" +
                Path.GetFileName(path) + " error=" + ex.Message);
        }
    }

    /// <summary>
    /// Sends a picture the user put here themselves to the recycle bin.
    ///
    /// <para>
    /// The mirror image of <see cref="Remove"/>, and restricted the same way round:
    /// only a file that is *not* ours may be deleted, because ours has somewhere to
    /// go - un-favouriting moves it back to wallpapers\ and the retention pass takes
    /// it from there. A picture the user dropped in here has no such second home, so
    /// until now the only way to be rid of one was to go and find it in Explorer.
    /// </para>
    /// <para>
    /// favorites.txt needs no repair afterwards: a user supplied picture never had a
    /// line in it, and <see cref="SaveIndex"/> drops orphans on its next write anyway.
    /// </para>
    /// </summary>
    public static DeleteOutcome Delete(string fileName, IntPtr ownerWindow)
    {
        if (BingImageInfo.TryParseFileName(fileName, out _, out _))
        {
            Logger.Warn("favorites: refusing to delete one of our own pictures file=" + fileName);
            return DeleteOutcome.Failed;
        }

        DeleteOutcome outcome = NativeMethods.RecycleFile(
            ownerWindow,
            Path.Combine(Paths.FavoritesDirectory, fileName));

        if (outcome == DeleteOutcome.Deleted)
        {
            Logger.Info("favorites: deleted file=" + fileName);
        }

        return outcome;
    }

    /// <summary>
    /// Reads favorites.txt into a dictionary keyed by file name. A file that is not
    /// there, or that cannot be read at all, is not an error: every Bing picture then
    /// falls back to its file name, which is the same thing a missing line does.
    /// </summary>
    private static Dictionary<string, IndexRecord> ReadIndex()
    {
        Dictionary<string, IndexRecord> index =
            new Dictionary<string, IndexRecord>(StringComparer.OrdinalIgnoreCase);

        string path = Paths.FavoritesIndexFile;
        if (!File.Exists(path))
        {
            return index;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex)
        {
            Logger.Warn("favorites: reading the title cache failed error=" + ex.Message);
            return index;
        }

        int skipped = 0;
        foreach (string line in lines)
        {
            if (line.Length == 0)
            {
                continue;
            }

            // Three columns, tab separated. A tab never appears in Bing's own text and
            // is stripped on the way in, so splitting is all the parsing there is.
            string[] columns = line.Split('\t');
            if (columns.Length < 2 || columns[0].Length == 0)
            {
                skipped++;
                continue;
            }

            index[columns[0]] = new IndexRecord(columns[1], columns.Length > 2 ? columns[2] : string.Empty);
        }

        if (skipped > 0)
        {
            Logger.Warn("favorites: skipped malformed lines count=" + skipped.ToString(CultureInfo.InvariantCulture));
        }

        return index;
    }

    /// <summary>
    /// Writes the whole file, dropping the lines whose picture is gone.
    ///
    /// <para>
    /// Full rewrite because the only thing that triggers it is a click: a couple of
    /// thousand entries are around 280 KB and a millisecond of StringBuilder, in the
    /// same click that already moved a file and repainted a grid.
    /// </para>
    /// <para>
    /// Written to a .tmp and renamed rather than over the top of the old file:
    /// WriteAllText truncates first, so a crash between the truncate and the last byte
    /// leaves an empty or half written file with the old content already gone. The
    /// window is a millisecond wide, but a title that is past Bing's eight day window
    /// cannot be fetched again - and the protection is three lines we already own.
    /// </para>
    /// </summary>
    private static void SaveIndex(Dictionary<string, IndexRecord> index)
    {
        string path = Paths.FavoritesIndexFile;
        string tmp = path + ".tmp";
        try
        {
            HashSet<string> present = ListPictureNames();
            StringBuilder sb = new StringBuilder(index.Count * 140);
            foreach (KeyValuePair<string, IndexRecord> pair in index)
            {
                if (!present.Contains(pair.Key))
                {
                    continue;
                }

                sb.Append(pair.Key)
                    .Append('\t')
                    .Append(pair.Value.Title)
                    .Append('\t')
                    .AppendLine(pair.Value.CopyrightLink);
            }

            File.WriteAllText(tmp, sb.ToString(), Utf8NoBom);
            Paths.MoveOverwrite(tmp, path);
        }
        catch (Exception ex)
        {
            Logger.Warn("favorites: writing the title cache failed error=" + ex.Message);
        }
        finally
        {
            // The rename consumed the temporary file on the way that worked; this is
            // for the ways that did not. Our own leftovers are the last thing that
            // should be accumulating in a folder we never clean.
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("favorites: removing the temporary title cache failed error=" + ex.Message);
            }
        }
    }

    /// <summary>The picture names currently in the folder, for comparing against.</summary>
    private static HashSet<string> ListPictureNames()
    {
        HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string path in Directory.EnumerateFiles(Paths.FavoritesDirectory))
            {
                string name = Path.GetFileName(path);
                if (IsPicture(name))
                {
                    names.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("favorites: listing the folder failed error=" + ex.Message);
        }

        return names;
    }

    /// <summary>
    /// Whether a name is one this program wrote, rather than one that merely parses
    /// like it.
    ///
    /// <para>
    /// BingImageInfo.TryParseFileName asks only for date_middle_suffix, which
    /// 20250101_a_beach.jpg satisfies as readily as one of ours - and reading "a" out
    /// of it as an image id would throw away half of the name the user chose. The
    /// resolution suffix is the part no one types by accident, so it is what the
    /// caption trusts. The looser test still decides which context menu an entry
    /// gets: what may move back to wallpapers\ is a question about where a file can
    /// go, not about what to call it.
    /// </para>
    /// </summary>
    private static bool IsOurFileName(string name, string imageId)
        => imageId.Length > 0
            && (EndsWithSegment(name, AppConfig.ResolutionToString(ResolutionKind.Uhd))
                || EndsWithSegment(name, AppConfig.ResolutionToString(ResolutionKind.FullHd)));

    private static bool EndsWithSegment(string name, string suffix)
        => name.Length > suffix.Length
            && name[name.Length - suffix.Length - 1] == '_'
            && string.CompareOrdinal(name, name.Length - suffix.Length, suffix, 0, suffix.Length) == 0;

    /// <summary>
    /// Pulls a date out of a file name: 20250101, 2025-01-01 or 2025_01_01 - and,
    /// with a separator, single digit fields as well (2025-1-1) - anywhere in the
    /// name and with anything around it. IMG_20250101_120000.jpg is what a camera or
    /// a screenshot tool usually hands over; 2025-1-1 is what a person types.
    ///
    /// <para>
    /// Worth the scan because the fallback is so much worse: a picture the user
    /// dropped in here carries whatever last write time the copy gave it, so a folder
    /// brought over in one go arrives as a single undifferentiated day and sorts by
    /// nothing at all. The name is then the only place the date the picture is *from*
    /// still exists.
    /// </para>
    /// <para>
    /// A candidate has to begin where a digit run begins, which is what keeps serial
    /// numbers out: of "1234567890123456" only the leading eight digits are ever
    /// tried, and they still have to be a real calendar date. Trailing digits are
    /// allowed - "20250101120000" is a timestamp, not a fourteen digit number.
    /// </para>
    /// <para>
    /// The span that matched comes back with the date because <see cref="StripDate"/>
    /// cuts exactly it out of the caption: one parse decides both what the date is
    /// and what the name still says, so the two can never disagree about which
    /// characters were the date.
    /// </para>
    /// </summary>
    private static bool TryParseDateInName(
        string name,
        out DateTime date,
        out int start,
        out int length)
    {
        for (int i = 0; i + 8 <= name.Length; i++)
        {
            if (i > 0 && IsDigit(name[i - 1]))
            {
                continue;
            }

            if (TryReadDate(name, i, out date, out int end))
            {
                start = i;
                length = end - i;
                return true;
            }
        }

        date = default(DateTime);
        start = 0;
        length = 0;
        return false;
    }

    /// <summary>
    /// Reads a date starting exactly at <paramref name="start"/>: yyyyMMdd, or
    /// yyyy-M-d / yyyy_M_d with one or two digit fields, both separators the same
    /// character.
    ///
    /// <para>
    /// The month and the day are only allowed to vary in width once a separator has
    /// established where they begin and end - "2025-1-12" is unambiguous, a compact
    /// run of digits is not. Hand rolled rather than Substring plus TryParseExact:
    /// this runs at every offset of every file name in the folder, and the string it
    /// would allocate is thrown away on the very next line.
    /// </para>
    /// </summary>
    private static bool TryReadDate(string name, int start, out DateTime date, out int end)
    {
        date = default(DateTime);
        end = start;

        if (!TryReadNumber(name, start, 4, out int year))
        {
            return false;
        }

        int month;
        int day;
        char separator = start + 4 < name.Length ? name[start + 4] : '\0';
        if (separator == '-' || separator == '_')
        {
            // The second separator has to match the first, so that 2025-01_01 is not
            // a date and neither is a range like 2025-01 01.
            if (!TryReadField(name, start + 5, out month, out int afterMonth) ||
                afterMonth >= name.Length || name[afterMonth] != separator ||
                !TryReadField(name, afterMonth + 1, out day, out int afterDay))
            {
                return false;
            }

            // A variable width field cannot tell 2025-1-12 from 2025-1-123 by its own
            // length, so the day has to end where the digits end. The compact form
            // has no such need: eight digits are eight digits whatever follows them.
            if (afterDay < name.Length && IsDigit(name[afterDay]))
            {
                return false;
            }

            end = afterDay;
        }
        else
        {
            if (start + 8 > name.Length ||
                !TryReadNumber(name, start + 4, 2, out month) ||
                !TryReadNumber(name, start + 6, 2, out day))
            {
                return false;
            }

            // The eight digits, and only those: a timestamp carries its time along
            // behind them and that is part of the caption, not part of the date.
            end = start + 8;
        }

        if (year < MinNameYear || month < 1 || month > 12 ||
            day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        DateTime parsed = new DateTime(year, month, day);

        // Tomorrow rather than today: the name may have been stamped in a time zone
        // ahead of this one. Beyond that it is not a date the picture can be from,
        // and taking it would park the entry at the top of a newest-first list until
        // the calendar caught up.
        if (parsed > DateTime.Today.AddDays(1))
        {
            return false;
        }

        date = parsed;
        return true;
    }

    /// <summary>
    /// Reads a one or two digit field and reports where it ended. Greedy, which needs
    /// no backtracking here: what stops it is either the separator or the end of the
    /// name, neither of which is a digit.
    /// </summary>
    private static bool TryReadField(string name, int start, out int value, out int end)
    {
        value = 0;
        end = start;
        while (end < name.Length && end - start < 2 && IsDigit(name[end]))
        {
            value = (value * 10) + (name[end] - '0');
            end++;
        }

        return end > start;
    }

    /// <summary>Reads exactly <paramref name="count"/> digits as a number.</summary>
    private static bool TryReadNumber(string name, int start, int count, out int value)
    {
        value = 0;
        for (int i = start; i < start + count; i++)
        {
            if (!IsDigit(name[i]))
            {
                return false;
            }

            value = (value * 10) + (name[i] - '0');
        }

        return true;
    }

    /// <summary>
    /// char.IsDigit would also accept Arabic-Indic and other Unicode digits, which
    /// are not what the arithmetic above means by a digit.
    /// </summary>
    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    /// <summary>
    /// Takes the date back out of a name, for the caption.
    ///
    /// <para>
    /// The tile draws the date on its own line above the title, so a title that
    /// repeats it says nothing twice - IMG_20250101_120000 is one date and one
    /// timestamp, and only the second half is a name. What is left on either side of
    /// the cut is trimmed of separators and rejoined by one: the separator that
    /// followed the date, so that a name spaced out as "photo - 2025-01-01 - final"
    /// does not come back with " - - " in the middle of it.
    /// </para>
    /// <para>
    /// Nothing else is touched - no case changes, no underscores turned into spaces.
    /// The caption is the only thing on screen that resembles the file name, and it
    /// has to stay recognisable when the user goes looking for the picture in
    /// Explorer. For the same reason a name that has nothing left worth showing keeps
    /// the whole of itself: 20250101.jpg has no other name, and a blank caption reads
    /// as a bug rather than as an absence.
    /// </para>
    /// </summary>
    private static string StripDate(string name, int start, int length)
    {
        int headStart = TrimSeparators(name, 0, start, out int headEnd);
        int tailStart = TrimSeparators(name, start + length, name.Length, out int tailEnd);

        string head = headEnd > headStart
            ? name.Substring(headStart, headEnd - headStart)
            : string.Empty;
        string tail = tailEnd > tailStart
            ? name.Substring(tailStart, tailEnd - tailStart)
            : string.Empty;

        string stripped;
        if (head.Length == 0 || tail.Length == 0)
        {
            stripped = head.Length == 0 ? tail : head;
        }
        else
        {
            // The separator that followed the date, or the one that preceded it, or -
            // when the date sat between two ordinary characters, as in "abc20250101x"
            // - nothing at all, because nothing was there to begin with.
            int after = start + length;
            string joint = after < name.Length && IsSeparator(name[after])
                ? name.Substring(after, 1)
                : IsSeparator(name[start - 1])
                    ? name.Substring(start - 1, 1)
                    : string.Empty;

            stripped = string.Concat(head, joint, tail);
        }

        return IsWorthShowing(stripped) ? stripped : name;
    }

    /// <summary>
    /// Whether what the date left behind is a name at all: one part of it has to be
    /// longer than a market code.
    ///
    /// <para>
    /// A picture some other downloader named is the case this exists for -
    /// 20210603-zh.jpg, 20210603-zh-cn.jpg - where every part left over is a locale
    /// or a sequence number and says less than the file name it would replace. So the
    /// test runs per part rather than over the whole string, which is what tells
    /// "zh-cn" from "a_beach".
    /// </para>
    /// <para>
    /// Two characters of anything but ASCII are a word rather than a code - 台北 is a
    /// place - so the alphabet decides as much as the length does.
    /// </para>
    /// </summary>
    private static bool IsWorthShowing(string stripped)
    {
        int run = 0;
        foreach (char c in stripped)
        {
            if (c > 127)
            {
                return true;
            }

            run = IsSeparator(c) ? 0 : run + 1;
            if (run > ShortestAsciiCode)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Narrows [<paramref name="start"/>, <paramref name="end"/>) to what it holds
    /// besides separators. Indices rather than String.Trim so that the two halves of
    /// a name cost one string each in the end, not one per step.
    /// </summary>
    private static int TrimSeparators(string name, int start, int end, out int trimmedEnd)
    {
        while (start < end && IsSeparator(name[start]))
        {
            start++;
        }

        while (end > start && IsSeparator(name[end - 1]))
        {
            end--;
        }

        trimmedEnd = end;
        return start;
    }

    /// <summary>
    /// What a file name puts between its parts. The dot is in here because the
    /// extension is already gone by this point, so any that is left is a separator
    /// like the rest.
    /// </summary>
    private static bool IsSeparator(char c) => c == '_' || c == '-' || c == '.' || c == ' ';

    private static bool IsPicture(string fileName)
    {
        string extension = Path.GetExtension(fileName);
        foreach (string candidate in PictureExtensions)
        {
            if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The entire escaping story: a tab would invent a column and a newline a record.
    /// Neither occurs in Bing's text, which is why removing them is enough and no
    /// quoting rules are needed on the way back in.
    /// </summary>
    private static string Sanitize(string value)
    {
        if (value.IndexOf('\t') < 0 && value.IndexOf('\n') < 0 && value.IndexOf('\r') < 0)
        {
            return value;
        }

        StringBuilder sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c != '\t' && c != '\n' && c != '\r')
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
