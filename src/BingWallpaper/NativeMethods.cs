using System;
using System.Runtime.InteropServices;

namespace BingWallpaper;

/// <summary>
/// Every P/Invoke the program makes. Avalonia draws the windows, but four things
/// still have no managed equivalent: setting the desktop wallpaper, owning a
/// notification area icon, handing Windows the right icon frame for a title bar,
/// and putting an error in front of the user before the UI toolkit is up.
///
/// Each entry lists its Win32 name and the minimum Windows version it needs; the
/// program requires Windows 10 1903 (build 18362), so anything below that is
/// simply available.
/// </summary>
internal static class NativeMethods
{
    /// <summary>SPI_SETDESKWALLPAPER - SystemParametersInfoW action code.</summary>
    public const uint SPI_SETDESKWALLPAPER = 0x0014;

    /// <summary>SPIF_UPDATEINIFILE - persist the change to the user profile.</summary>
    public const uint SPIF_UPDATEINIFILE = 0x01;

    /// <summary>SPIF_SENDCHANGE - broadcast WM_SETTINGCHANGE so Explorer repaints.</summary>
    public const uint SPIF_SENDCHANGE = 0x02;

    public const int WM_DESTROY = 0x0002;
    public const int WM_CLOSE = 0x0010;
    public const int WM_SETICON = 0x0080;

    /// <summary>The first message id applications may use for their own purposes.</summary>
    public const int WM_APP = 0x8000;

    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_LBUTTONDBLCLK = 0x0203;
    public const int WM_RBUTTONUP = 0x0205;

    public const int ICON_SMALL = 0;
    public const int ICON_BIG = 1;

    public const int SM_CXICON = 11;
    public const int SM_CXSMICON = 49;

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_1903 = 19;

    public const int MB_OK = 0x00000000;
    public const int MB_ICONERROR = 0x00000010;
    public const int MB_TOPMOST = 0x00040000;

    // Shell_NotifyIconW
    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;

    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;

    /// <summary>
    /// Number of characters of NOTIFYICONDATAW.szTip Windows reads while the icon
    /// uses the pre-Shell-5.0 behaviour, which is what this program registers.
    /// </summary>
    public const int TipLength = 64;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// NOTIFYICONDATAW in its current (v4, 976 byte) shape. The character arrays are
    /// fixed size buffers rather than marshalled strings so the whole structure stays
    /// blittable - that is what lets it be passed by reference with no marshalling
    /// stub, which is both faster and the shape Native AOT likes best.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public IntPtr hIconSm;
    }

    /// <summary>user32!SystemParametersInfoW.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

    /// <summary>user32!GetDpiForSystem. Windows 10 version 1607 and later.</summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    /// <summary>user32!GetSystemMetricsForDpi. Windows 10 version 1607 and later.</summary>
    [DllImport("user32.dll")]
    public static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern unsafe ushort RegisterClassExW(in WNDCLASSEXW wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern unsafe IntPtr CreateWindowExW(
        uint exStyle,
        char* className,
        char* windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessageW(string message);

    /// <summary>
    /// user32!CreateIconFromResourceEx. The resource bits are one frame of an .ico
    /// file - both the classic DIB shape and the PNG compressed shape are understood.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateIconFromResourceEx(
        IntPtr presbits,
        uint dwResSize,
        [MarshalAs(UnmanagedType.Bool)] bool fIcon,
        uint dwVer,
        int cxDesired,
        int cyDesired,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIconW(uint message, in NOTIFYICONDATAW data);

    /// <summary>
    /// dwmapi!DwmSetWindowAttribute. Attribute 20 (DWMWA_USE_IMMERSIVE_DARK_MODE)
    /// is what paints a title bar dark; on Windows 10 builds before 1903 the same
    /// attribute had the number 19.
    /// </summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, in int value, int size);

    /// <summary>Reads the system DPI, falling back to 96 when the call fails.</summary>
    public static uint GetSystemDpiSafe()
    {
        try
        {
            uint dpi = GetDpiForSystem();
            return dpi == 0 ? 96u : dpi;
        }
        catch (Exception ex)
        {
            Logger.Warn("GetDpiForSystem failed: " + ex.Message);
            return 96u;
        }
    }

    /// <summary>
    /// GetSystemMetricsForDpi with a fallback, used to ask for the icon size the
    /// notification area and the title bar actually want at the current DPI.
    /// </summary>
    public static int GetMetricForDpi(int index, uint dpi, int fallback)
    {
        try
        {
            int value = GetSystemMetricsForDpi(index, dpi);
            return value > 0 ? value : fallback;
        }
        catch (Exception ex)
        {
            Logger.Warn("GetSystemMetricsForDpi(" + index + ") failed: " + ex.Message);
            return fallback;
        }
    }

    /// <summary>
    /// Asks the desktop window manager to paint the title bar of a window dark.
    /// Avalonia does this for its own windows, so this is only ever a safety net
    /// for the Windows 10 builds where it does not take; failures are ignored on
    /// purpose - a light title bar is a blemish, not a malfunction.
    /// </summary>
    public static void SetDarkTitleBar(IntPtr hWnd, bool dark)
    {
        if (hWnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            int value = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, in value, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_1903, in value, sizeof(int));
            }
        }
        catch (Exception ex)
        {
            Logger.Debug("DwmSetWindowAttribute failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Last resort error report: a plain Win32 message box. Used before Avalonia is
    /// running and when showing the themed error window itself fails.
    /// </summary>
    public static void ShowMessageBox(string title, string text)
    {
        try
        {
            MessageBoxW(IntPtr.Zero, text, title, MB_OK | MB_ICONERROR | MB_TOPMOST);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not show the message box: " + ex.Message);
        }
    }
}
