using System;
using System.Runtime.InteropServices;

namespace BingWallpaper;

/// <summary>
/// P/Invoke declarations that are not theme related (theme interop lives in
/// Theme/DarkModeNative.cs). Every entry lists its Win32 name and the minimum
/// Windows version it requires.
/// </summary>
internal static class NativeMethods
{
    /// <summary>SPI_SETDESKWALLPAPER - SystemParametersInfoW action code.</summary>
    public const uint SPI_SETDESKWALLPAPER = 0x0014;

    /// <summary>SPIF_UPDATEINIFILE - persist the change to the user profile.</summary>
    public const uint SPIF_UPDATEINIFILE = 0x01;

    /// <summary>SPIF_SENDCHANGE - broadcast WM_SETTINGCHANGE so Explorer repaints.</summary>
    public const uint SPIF_SENDCHANGE = 0x02;

    /// <summary>WM_SETTINGCHANGE - broadcast when a system wide setting changes.</summary>
    public const int WM_SETTINGCHANGE = 0x001A;

    /// <summary>
    /// WS_EX_COMPOSITED - compose the window and its children off screen and put the
    /// result up in one piece, instead of revealing the window and letting the
    /// children paint themselves into it afterwards.
    /// </summary>
    public const int WS_EX_COMPOSITED = 0x02000000;

    /// <summary>RECT, windef.h.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>
    /// PAINTSTRUCT, winuser.h. Only the device context is ever read back, but the
    /// fields before it have to be laid out for the window manager to write into -
    /// the reserved tail does not, so it gets no fields of its own. Size pins the
    /// struct to the 72 bytes the window manager writes on x64, which is what this
    /// executable targets (see PlatformTarget in the csproj).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 72)]
    public struct PAINTSTRUCT
    {
        public IntPtr Hdc;
        public int Erase;
        public RECT PaintRectangle;
        public int Restore;
        public int IncUpdate;
    }

    /// <summary>
    /// user32!BeginPaint. Available since Windows 2000. Opens the painting cycle of
    /// a window: it hands back a device context and validates the update region, so
    /// it has to be paired with EndPaint or the window keeps asking to be painted.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern IntPtr BeginPaint(IntPtr hWnd, ref PAINTSTRUCT paint);

    /// <summary>user32!EndPaint. Available since Windows 2000.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT paint);

    /// <summary>
    /// gdi32!SaveDC. Pushes the state of a device context - clipping region, selected
    /// objects, drawing modes - and returns a level to restore it by. Available since
    /// Windows 2000.
    /// </summary>
    [DllImport("gdi32.dll")]
    public static extern int SaveDC(IntPtr hdc);

    /// <summary>gdi32!RestoreDC. Available since Windows 2000.</summary>
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RestoreDC(IntPtr hdc, int savedState);

    /// <summary>
    /// user32!SystemParametersInfoW. Available since Windows 2000.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

    /// <summary>
    /// user32!GetDpiForSystem. Available since Windows 10 version 1607 (build 14393),
    /// which is below our minimum of build 19044.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    /// <summary>user32!SetForegroundWindow. Available since Windows 2000.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

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
}
