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
        // Environment.ProcessPath points at the real exe even for single-file
        // publishes (AppContext.BaseDirectory may point at the extraction dir).
        string? exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            ExecutablePath = Path.GetFullPath(exe);
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
            using (FileStream fs = new(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
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
            catch
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
}
