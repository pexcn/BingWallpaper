using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace BingWallpaper.UI;

/// <summary>
/// The application icon. The same .ico is both the Win32 icon of the executable
/// (ApplicationIcon in the project file, which is what Explorer and the task bar
/// read) and an embedded resource, which is what this class hands out.
///
/// Reading the embedded copy instead of calling Icon.ExtractAssociatedIcon is what
/// keeps the tray sharp: that API only ever returns the 32x32 frame, while the
/// notification area asks for 16, 20 or 24 device pixels depending on the DPI. The
/// file carries a frame for each of those three, so Windows Forms picks the one
/// that fits rather than squashing one bitmap into another size.
/// </summary>
internal static class AppIcon
{
    /// <summary>
    /// Assembly qualified name of the embedded icon. Set explicitly in the project
    /// file as well, so this string does not depend on any naming convention.
    /// </summary>
    private const string ResourceName = "BingWallpaper.Resources.app.ico";

    private static readonly Icon? Source = LoadSource();

    /// <summary>
    /// Icon for a window. The whole multi frame icon is handed over on purpose:
    /// Windows Forms takes the small frame for the title bar and the large one for
    /// the task bar entry.
    /// </summary>
    public static Icon Window { get; } = Source ?? Fallback();

    /// <summary>Icon for the notification area, at the DPI of the current session.</summary>
    public static Icon Tray { get; } = LoadTray();

    private static Icon? LoadSource()
    {
        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                Logger.Warn("appicon: resource not embedded name=" + ResourceName);
                return null;
            }

            return new Icon(stream);
        }
        catch (Exception ex)
        {
            Logger.Warn("appicon: reading the embedded resource failed error=" + ex.Message);
            return null;
        }
    }

    private static Icon LoadTray()
    {
        Icon? source = Source;
        if (source is null)
        {
            return Window;
        }

        Size size = SystemInformation.SmallIconSize;
        try
        {
            return new Icon(source, size);
        }
        catch (Exception ex)
        {
            Logger.Warn("appicon: picking a frame failed size=" + size.Width + " error=" + ex.Message);
            return source;
        }
    }

    /// <summary>
    /// Only reached if the embedded resource is gone: ask the shell for the icon of
    /// the executable, and settle for the generic one if even that fails.
    /// </summary>
    private static Icon Fallback()
    {
        try
        {
            Icon? icon = Icon.ExtractAssociatedIcon(Paths.ExecutablePath);
            if (icon is not null)
            {
                return icon;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("appicon: extracting the icon failed error=" + ex.Message);
        }

        return SystemIcons.Application;
    }
}
