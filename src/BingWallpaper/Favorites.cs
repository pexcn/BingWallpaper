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

    /// <summary>What to show under the picture; the file name when nothing better is known.</summary>
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

    /// <summary>Publication date for our own files, last write time for everyone else's.</summary>
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
/// Nothing here deletes a picture from favorites\. The three writes this class makes
/// are: move a file in, move one of *ours* back out, and replace favorites.txt.
/// </para>
/// </summary>
internal static class Favorites
{
    /// <summary>
    /// What SystemParametersInfoW accepts on Windows 10 and what a person is likely to
    /// drop in here. Anything else in the folder is ignored rather than reported.
    /// </summary>
    private static readonly string[] PictureExtensions = { ".jpg", ".jpeg", ".png", ".bmp" };

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

                bool isBing = BingImageInfo.TryParseFileName(file.Name, out string startDate, out _);
                DateTime sortKey = file.LastWriteTime;
                if (isBing && DateTime.TryParseExact(
                        startDate,
                        "yyyyMMdd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out DateTime published))
                {
                    sortKey = published;
                }

                string displayDate = sortKey.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                items.Add(new FavoriteItem(
                    file.Name,
                    Path.GetFileNameWithoutExtension(file.Name),
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
