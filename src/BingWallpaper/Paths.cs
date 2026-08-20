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
