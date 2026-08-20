using System;
using System.Runtime.InteropServices;

namespace BingWallpaper.UI;

/// <summary>
/// The application icon, as an HICON.
///
/// The same .ico is both the Win32 icon of the executable (ApplicationIcon in the
/// project file, which is what Explorer and the task bar read) and, through this
/// class, the icon of the tray entry and of every window title bar.
///
/// It is loaded out of the executable's own resources at the exact size that is
/// being asked for, so Windows picks the frame that matches instead of squashing
/// the 32x32 one into 16 or 20 pixels: the file carries a frame for each of
/// 256/128/64/48/32/24/20/16.
/// </summary>
internal static class AppIcon
{
    /// <summary>
    /// Resource id of the icon group the C# compiler writes when /win32icon is used -
    /// which is what the ApplicationIcon project property turns into. It is the same
    /// numeric value as IDI_APPLICATION, but in the module's own resources.
    /// </summary>
    private static readonly IntPtr ApplicationIconResource = new IntPtr(32512);

    private const uint IMAGE_ICON = 1;
    private const uint LR_DEFAULTCOLOR = 0;
    private const uint LR_DEFAULTSIZE = 0x00000040;
    private const int SM_CXICON = 11;
    private const int SM_CXSMICON = 49;

    /// <summary>Icon for the notification area, sized for the given DPI.</summary>
    public static IntPtr LoadTrayIcon(uint dpi) => Load(GetMetric(SM_CXSMICON, dpi));

    /// <summary>Icon for a window: the title bar takes the small frame out of it.</summary>
    public static IntPtr LoadWindowIcon(uint dpi) => Load(GetMetric(SM_CXICON, dpi));

    public static void Destroy(IntPtr icon)
    {
        if (icon == IntPtr.Zero)
        {
            return;
        }

        try
        {
            DestroyIcon(icon);
        }
        catch (Exception ex)
        {
            Logger.Debug("DestroyIcon failed: " + ex.Message);
        }
    }

    private static IntPtr Load(int size)
    {
        try
        {
            IntPtr module = GetModuleHandleW(null);
            IntPtr icon = LoadImageW(module, ApplicationIconResource, IMAGE_ICON, size, size, LR_DEFAULTCOLOR);
            if (icon != IntPtr.Zero)
            {
                return icon;
            }

            Logger.Warn("The executable carries no icon resource, falling back to ExtractIconEx.");
            IntPtr large = IntPtr.Zero;
            IntPtr small = IntPtr.Zero;
            if (ExtractIconExW(Paths.ExecutablePath, 0, ref large, ref small, 1) > 0)
            {
                // Whichever of the two is closer to the requested size; the other one
                // is not needed and would leak.
                bool wantSmall = size <= 24;
                IntPtr wanted = wantSmall ? small : large;
                Destroy(wantSmall ? large : small);
                if (wanted != IntPtr.Zero)
                {
                    return wanted;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not load the application icon: " + ex.Message);
        }

        // Last resort: the generic application icon, so the tray entry is at least
        // visible and clickable.
        try
        {
            return LoadImageW(IntPtr.Zero, ApplicationIconResource, IMAGE_ICON, 0, 0, LR_DEFAULTSIZE);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not load the generic application icon either: " + ex.Message);
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// The icon size Windows wants at this DPI. GetSystemMetricsForDpi exists since
    /// Windows 10 1607; the plain GetSystemMetrics behind it answers for the system
    /// DPI, which is the wrong answer on a monitor with its own scaling factor.
    /// </summary>
    private static int GetMetric(int index, uint dpi)
    {
        try
        {
            int value = GetSystemMetricsForDpi(index, dpi == 0 ? 96u : dpi);
            if (value > 0)
            {
                return value;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug("GetSystemMetricsForDpi failed: " + ex.Message);
        }

        return index == SM_CXSMICON ? 16 : 32;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(IntPtr instance, IntPtr name, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int iconIndex, ref IntPtr large, ref IntPtr small, uint icons);
}
