using System;
using System.IO;
using System.Reflection;

namespace BingWallpaper;

/// <summary>
/// Resolves every path the application touches. Everything lives next to the
/// executable - this is a portable program, deleting the folder uninstalls it.
/// </summary>
internal static class Paths
{
    public const string ConfigFileName = "BingWallpaper.ini";
    public const string LogFileName = "BingWallpaper.log";
    public const string WallpaperFolderName = "wallpapers";

    /// <summary>Sub folder of the wallpaper cache holding the favourites.</summary>
    public const string FavoritesFolderName = "favorites";

    /// <summary>Title cache of the favourites, kept next to the pictures it describes.</summary>
    public const string FavoritesIndexFileName = "favorites.txt";

    /// <summary>
    /// Regenerable thumbnails of the favourites. Deliberately *beside* the favourites
    /// folder rather than inside it: this is the one place under wallpapers\ that the
    /// program deletes from, and keeping it out leaves "nothing in favorites\ is ever
    /// deleted" true without an exception anyone has to remember. It also keeps a copy
    /// of the favourites folder free of cache droppings.
    /// </summary>
    public const string ThumbnailFolderName = ".thumbs";

    static Paths()
    {
        string? exe = null;
        try
        {
            exe = Assembly.GetEntryAssembly()?.Location;
        }
        catch (Exception)
        {
            exe = null;
        }

        if (string.IsNullOrEmpty(exe))
        {
            exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        }

        if (!string.IsNullOrEmpty(exe))
        {
            ExecutablePath = Path.GetFullPath(exe!);
            BaseDirectory = Path.GetDirectoryName(ExecutablePath) ?? AppDomain.CurrentDomain.BaseDirectory;
        }
        else
        {
            BaseDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            ExecutablePath = Path.Combine(BaseDirectory, "BingWallpaper.exe");
        }
    }

    /// <summary>Directory that contains the executable.</summary>
    public static string BaseDirectory { get; }

    /// <summary>Absolute path of the running executable.</summary>
    public static string ExecutablePath { get; }

    public static string ConfigFile => Path.Combine(BaseDirectory, ConfigFileName);

    public static string LogFile => Path.Combine(BaseDirectory, LogFileName);

    public static string WallpaperDirectory => Path.Combine(BaseDirectory, WallpaperFolderName);

    /// <summary>
    /// Where favourited pictures live. Nothing in this program deletes a picture from
    /// here, and that is structural rather than checked: the cleanup passes enumerate
    /// TopDirectoryOnly, so a sub folder is out of their reach by construction.
    /// </summary>
    public static string FavoritesDirectory => Path.Combine(WallpaperDirectory, FavoritesFolderName);

    public static string FavoritesIndexFile => Path.Combine(FavoritesDirectory, FavoritesIndexFileName);

    public static string ThumbnailDirectory => Path.Combine(WallpaperDirectory, ThumbnailFolderName);

    /// <summary>
    /// Probes the program directory for write access by creating and deleting a
    /// temporary file. No silent fallback to %LOCALAPPDATA% is performed anywhere.
    /// </summary>
    public static bool IsBaseDirectoryWritable(out string? error)
    {
        string probe = Path.Combine(BaseDirectory, ".write-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (FileStream fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.WriteByte(0);
            }

            File.Delete(probe);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try
            {
                if (File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
            catch (Exception)
            {
                // Nothing else to do - the directory is not writable anyway.
            }

            return false;
        }
    }

    public static void EnsureWallpaperDirectory()
    {
        Directory.CreateDirectory(WallpaperDirectory);
    }

    /// <summary>
    /// Creates the favourites folder. Called when something is favourited, never at
    /// startup: an installation nobody has favourited in has no such folder, and the
    /// picker is happy to find none.
    /// </summary>
    public static void EnsureFavoritesDirectory()
    {
        Directory.CreateDirectory(FavoritesDirectory);
    }

    /// <summary>
    /// Creates the thumbnail cache and hides it. Hidden because it holds nothing the
    /// user put there and nothing worth copying away, while the wallpaper folder is
    /// something people do open in Explorer.
    /// </summary>
    public static void EnsureThumbnailDirectory()
    {
        DirectoryInfo directory = Directory.CreateDirectory(ThumbnailDirectory);
        try
        {
            if ((directory.Attributes & FileAttributes.Hidden) == 0)
            {
                directory.Attributes |= FileAttributes.Hidden;
            }
        }
        catch (Exception ex)
        {
            // A visible cache folder is cosmetic; the cache itself works either way.
            Logger.Debug("thumbnail: hiding the cache directory failed error=" + ex.Message);
        }
    }

    /// <summary>
    /// Turns a cached wallpaper file name into a full path. Every lookup goes through
    /// here, which is what keeps the order the two folders are searched in written
    /// down in exactly one place.
    /// <para>
    /// Favourites first: favouriting *moves* a picture out of the daily cache, so a
    /// name found there is the one copy of it on disk. A name in neither folder
    /// resolves to the daily cache - where a download would put it - so the answer
    /// doubles as "where this file belongs".
    /// </para>
    /// </summary>
    public static string ResolveWallpaperFile(string fileName)
    {
        string favorite = Path.Combine(FavoritesDirectory, fileName);
        return File.Exists(favorite) ? favorite : Path.Combine(WallpaperDirectory, fileName);
    }

    /// <summary>
    /// Replaces <paramref name="destination"/> with <paramref name="source"/>.
    /// File.Move has no overwrite overload on .NET Framework, and File.Replace is
    /// the atomic option when the destination already exists.
    /// </summary>
    public static void MoveOverwrite(string source, string destination)
    {
        if (File.Exists(destination))
        {
            try
            {
                File.Replace(source, destination, null);
                return;
            }
            catch (IOException)
            {
                File.Delete(destination);
            }
            catch (PlatformNotSupportedException)
            {
                File.Delete(destination);
            }
        }

        File.Move(source, destination);
    }
}
