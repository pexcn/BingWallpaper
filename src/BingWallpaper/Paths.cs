using System;
using System.Diagnostics;
using System.IO;

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

    static Paths()
    {
        // Environment.ProcessPath rather than Assembly.Location: the second one is
        // empty in a single file publish, and it points at the managed assembly
        // rather than at the host executable in every other layout.
        string? exe = null;
        try
        {
            exe = Environment.ProcessPath;
        }
        catch (Exception)
        {
            exe = null;
        }

        if (string.IsNullOrEmpty(exe))
        {
            exe = Process.GetCurrentProcess().MainModule?.FileName;
        }

        if (!string.IsNullOrEmpty(exe))
        {
            ExecutablePath = Path.GetFullPath(exe!);
            BaseDirectory = Path.GetDirectoryName(ExecutablePath) ?? AppContext.BaseDirectory;
        }
        else
        {
            BaseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
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
    /// Turns a cached wallpaper file name into a full path. Every lookup goes
    /// through here so that the day the pictures are split across sub folders, the
    /// order they are searched in is defined in exactly one place.
    /// </summary>
    public static string ResolveWallpaperFile(string fileName)
        => Path.Combine(WallpaperDirectory, fileName);

    /// <summary>
    /// Replaces <paramref name="destination"/> with <paramref name="source"/>.
    /// File.Replace is the atomic option when the destination already exists; the
    /// overwriting File.Move is the fallback for the file systems it refuses to
    /// work on.
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
                // Falls through to the overwriting move.
            }
            catch (PlatformNotSupportedException)
            {
                // Same.
            }
        }

        File.Move(source, destination, overwrite: true);
    }
}
