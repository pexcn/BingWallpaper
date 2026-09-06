using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace BingWallpaper;

/// <summary>
/// Applies wallpapers and prunes the local cache.
/// </summary>
internal static class WallpaperService
{
    private const string DesktopKeyPath = @"Control Panel\Desktop";

    /// <summary>
    /// Sets the desktop wallpaper, cutting to it.
    ///
    /// <para>
    /// For the paths that have no fade to run and nothing on screen to keep
    /// responsive: restoring a pin at startup, repairing the record after a picture
    /// moved between folders, re-applying the current one in a new fit. Everything
    /// the user clicks goes through <see cref="ApplyAsync"/> instead.
    /// </para>
    /// </summary>
    public static bool Apply(string imagePath, WallpaperFit fit)
    {
        string? fullPath = SetStyle(imagePath, fit);
        return fullPath is not null && SetWallpaper(fullPath, fit);
    }

    /// <summary>
    /// Sets the desktop wallpaper, crossfading out of the picture that is on the
    /// desktop right now unless <paramref name="fade"/> says otherwise.
    ///
    /// <para>
    /// <paramref name="previousPath"/> is not what the fade paints - it copies the
    /// wallpaper layer itself and needs nothing from here. It is only what tells a
    /// real change apart from re-applying the very same picture, which must not fade.
    /// </para>
    /// </summary>
    public static Task<bool> ApplyAsync(string imagePath, WallpaperFit fit, bool fade, string? previousPath)
    {
        if (!fade || IsSamePicture(imagePath, previousPath))
        {
            return ApplyCoreAsync(imagePath, fit);
        }

        return WallpaperTransition.RunAsync(() => ApplyCoreAsync(imagePath, fit));
    }

    /// <summary>
    /// The two steps of an apply, with the slow one off the UI thread.
    ///
    /// <para>
    /// SystemParametersInfoW does not return until Windows has decoded the picture
    /// all over again, re-encoded it and written TranscodedWallpaper - a couple of
    /// hundred milliseconds on an idle machine and well past a second on a downclocked
    /// one. It touches no window of this program, only the registry and a broadcast,
    /// so nothing holds it on the UI thread. Under a fade the cover is up and opaque
    /// for the whole wait, which is what turns it from a freeze into idle time.
    /// </para>
    /// </summary>
    private static async Task<bool> ApplyCoreAsync(string imagePath, WallpaperFit fit)
    {
        string? fullPath = SetStyle(imagePath, fit);
        if (fullPath is null)
        {
            return false;
        }

        return await Task.Run(() => SetWallpaper(fullPath, fit)).ConfigureAwait(true);
    }

    /// <summary>
    /// Step 1: the style values, which have to be written *before*
    /// SystemParametersInfoW or Windows applies the previous style. Returns the full
    /// path for step 2, or null when there is nothing to apply.
    /// </summary>
    private static string? SetStyle(string imagePath, WallpaperFit fit)
    {
        if (!File.Exists(imagePath))
        {
            Logger.Error("wallpaper: apply skipped, file missing path=" + imagePath);
            return null;
        }

        string fullPath = Path.GetFullPath(imagePath);
        (string style, string tile) = GetStyleValues(fit);

        try
        {
            using (RegistryKey? key = Registry.CurrentUser.CreateSubKey(DesktopKeyPath, writable: true))
            {
                if (key is null)
                {
                    throw new InvalidOperationException(@"Could not open HKCU\Control Panel\Desktop.");
                }

                key.SetValue("WallpaperStyle", style, RegistryValueKind.String);
                key.SetValue("TileWallpaper", tile, RegistryValueKind.String);
            }

            Logger.Debug("wallpaper: style set fit=" + fit + " wallpaperstyle=" + style + " tilewallpaper=" + tile);
            return fullPath;
        }
        catch (Exception ex)
        {
            Logger.Error("wallpaper: writing style values failed", ex);
            return null;
        }
    }

    /// <summary>
    /// Step 2: tell Windows to load the image. Modern Windows accepts JPEG/PNG
    /// directly, no BMP conversion needed. This is the part that runs on a thread
    /// pool thread, so it reads the last error before anything else can overwrite it.
    /// </summary>
    private static bool SetWallpaper(string fullPath, WallpaperFit fit)
    {
        bool ok = NativeMethods.SystemParametersInfoW(
            NativeMethods.SPI_SETDESKWALLPAPER,
            0,
            fullPath,
            NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);

        int lastError = Marshal.GetLastWin32Error();
        Logger.Info(
            "wallpaper: applied ok=" + ok +
            " fit=" + fit +
            " path=" + fullPath +
            (ok ? string.Empty : " lasterror=" + lastError));

        return ok;
    }

    /// <summary>
    /// Whether the two paths name the same picture - the one change that must not
    /// fade, since fading a picture into itself is a flicker and nothing else.
    /// </summary>
    private static bool IsSamePicture(string imagePath, string? previousPath)
    {
        if (previousPath is null || previousPath.Length == 0)
        {
            // Nothing has been applied yet this session. The fade copies the desktop
            // rather than a file it has to recognise, so there is still something to
            // fade out of - the first change after a start gets one too.
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(previousPath),
                Path.GetFullPath(imagePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // Cutting is the safe answer to a question that could not be asked: a fade
            // to the same picture is a visible flicker, a missing fade is not.
            Logger.Warn("fade: comparing the two paths failed error=" + ex.Message);
            return true;
        }
    }

    /// <summary>Reads the wallpaper path Windows currently reports (best effort).</summary>
    public static string? GetCurrentWallpaperFromRegistry()
    {
        try
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(DesktopKeyPath, writable: false))
            {
                return key?.GetValue("Wallpaper") as string;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("wallpaper: reading the current path failed error=" + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Deletes cached wallpapers older than <paramref name="keepDays"/>.
    /// The files in <paramref name="protectedFiles"/> are never deleted, no matter
    /// how old they are. keepDays == 0 means "keep forever" and skips the whole pass.
    /// </summary>
    public static int Cleanup(string directory, int keepDays, IReadOnlyCollection<string>? protectedFiles)
    {
        if (keepDays <= 0)
        {
            Logger.Debug("cleanup: skipped, retention=forever");
            return 0;
        }

        if (!Directory.Exists(directory))
        {
            return 0;
        }

        HashSet<string> protectedSet = BuildProtectedSet(protectedFiles);

        DateTime threshold = DateTime.UtcNow.AddDays(-keepDays);
        int deleted = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*.jpg", SearchOption.TopDirectoryOnly))
            {
                string full = Path.GetFullPath(file);
                if (protectedSet.Contains(full))
                {
                    continue;
                }

                try
                {
                    FileInfo info = new FileInfo(full);
                    if (info.LastWriteTimeUtc >= threshold)
                    {
                        continue;
                    }

                    info.Delete();
                    deleted++;
                    Logger.Info("cleanup: deleted expired file=" + info.Name);
                }
                catch (Exception ex)
                {
                    Logger.Warn("cleanup: delete failed path=" + full + " error=" + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("cleanup: pass failed", ex);
        }

        // Debug when nothing happened: on a settled cache this fires on every cycle.
        string summary = "cleanup: done removed=" + deleted.ToString(CultureInfo.InvariantCulture) +
            " keepdays=" + keepDays.ToString(CultureInfo.InvariantCulture);
        if (deleted > 0)
        {
            Logger.Info(summary);
        }
        else
        {
            Logger.Debug(summary);
        }

        return deleted;
    }

    /// <summary>
    /// Removes the copies of a picture that are not in the configured resolution.
    /// Toggling the resolution setting leaves a "_UHD" and a "_1920x1080" file of
    /// the very same picture side by side; both decode to the same photo, so only
    /// the configured one is worth keeping.
    /// <para>
    /// A group is only pruned when the copy in the current resolution is actually
    /// present. That single condition is what makes the pass safe to run whatever
    /// the retention setting says: it can remove a redundant copy, never the last
    /// one, so no picture is ever lost here.
    /// </para>
    /// </summary>
    public static int RemoveStaleResolutions(
        string directory,
        ResolutionKind resolution,
        IReadOnlyCollection<string>? protectedFiles)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        string keepSuffix = "_" + AppConfig.ResolutionToString(resolution) + ".jpg";
        HashSet<string> protectedSet = BuildProtectedSet(protectedFiles);
        int deleted = 0;

        try
        {
            // Key: the file name without the resolution segment, i.e. one picture.
            Dictionary<string, List<string>> groups =
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (string file in Directory.EnumerateFiles(directory, "*.jpg", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                int cut = name.LastIndexOf('_');
                if (cut <= 0)
                {
                    continue;
                }

                string key = name.Substring(0, cut);
                if (!groups.TryGetValue(key, out List<string>? members))
                {
                    members = new List<string>(2);
                    groups[key] = members;
                }

                members.Add(file);
            }

            foreach (KeyValuePair<string, List<string>> group in groups)
            {
                if (group.Value.Count < 2)
                {
                    continue;
                }

                bool keeperPresent = group.Value.Exists(
                    file => Path.GetFileName(file).EndsWith(keepSuffix, StringComparison.OrdinalIgnoreCase));
                if (!keeperPresent)
                {
                    continue;
                }

                foreach (string file in group.Value)
                {
                    if (Path.GetFileName(file).EndsWith(keepSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string full = Path.GetFullPath(file);
                    if (protectedSet.Contains(full))
                    {
                        continue;
                    }

                    try
                    {
                        File.Delete(full);
                        deleted++;
                        Logger.Info("staleresolution: deleted file=" + Path.GetFileName(full));
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("staleresolution: delete failed path=" + full + " error=" + ex.Message);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("staleresolution: pass failed", ex);
        }

        if (deleted > 0)
        {
            Logger.Info(
                "staleresolution: done removed=" + deleted.ToString(CultureInfo.InvariantCulture) +
                " keeping=" + AppConfig.ResolutionToString(resolution));
        }

        return deleted;
    }

    /// <summary>Maps a fit mode to the registry values documented for HKCU\Control Panel\Desktop.</summary>
    public static (string WallpaperStyle, string TileWallpaper) GetStyleValues(WallpaperFit fit) => fit switch
    {
        WallpaperFit.Fill => ("10", "0"),
        WallpaperFit.Fit => ("6", "0"),
        WallpaperFit.Stretch => ("2", "0"),
        WallpaperFit.Tile => ("0", "1"),
        WallpaperFit.Center => ("0", "0"),
        WallpaperFit.Span => ("22", "0"),
        _ => ("10", "0"),
    };

    /// <summary>
    /// Normalizes the files a cleanup pass must leave alone. A set rather than a
    /// single path because the wallpaper on the desktop and the pinned one are not
    /// always the same file - they differ while a pinned picture is being restored
    /// or downloaded again.
    /// </summary>
    private static HashSet<string> BuildProtectedSet(IReadOnlyCollection<string>? files)
    {
        HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (files is null)
        {
            return set;
        }

        foreach (string file in files)
        {
            string? full = TryGetFullPath(file);
            if (full is not null)
            {
                set.Add(full);
            }
        }

        return set;
    }

    /// <summary>Normalizes a path for comparison, or null when there is nothing to protect.</summary>
    private static string? TryGetFullPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            Logger.Warn("cleanup: normalizing a protected path failed error=" + ex.Message);
            return null;
        }
    }

    /// <summary>Localized (zh-CN) display name of a fit mode, used by the settings UI.</summary>
    public static string GetFitDisplayName(WallpaperFit fit) => fit switch
    {
        WallpaperFit.Fill => "填充",
        WallpaperFit.Fit => "适应",
        WallpaperFit.Stretch => "拉伸",
        WallpaperFit.Tile => "平铺",
        WallpaperFit.Center => "居中",
        WallpaperFit.Span => "跨区",
        _ => fit.ToString(),
    };
}
