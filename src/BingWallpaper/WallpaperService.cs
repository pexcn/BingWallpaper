using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BingWallpaper;

/// <summary>
/// Applies wallpapers and prunes the local cache.
/// </summary>
internal static class WallpaperService
{
    private const string DesktopKeyPath = @"Control Panel\Desktop";

    /// <summary>
    /// Sets the desktop wallpaper. The style values must be written *before*
    /// SystemParametersInfoW, otherwise Windows applies the previous style.
    /// </summary>
    public static bool Apply(string imagePath, WallpaperFit fit)
    {
        if (!File.Exists(imagePath))
        {
            Logger.Error("wallpaper: apply skipped, file missing path=" + imagePath);
            return false;
        }

        string fullPath = Path.GetFullPath(imagePath);
        (string style, string tile) = GetStyleValues(fit);

        try
        {
            // Step 1: style first.
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
        }
        catch (Exception ex)
        {
            Logger.Error("wallpaper: writing style values failed", ex);
            return false;
        }

        // Step 2: tell Windows to load the image. Modern Windows accepts JPEG/PNG
        // directly, no BMP conversion needed.
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
