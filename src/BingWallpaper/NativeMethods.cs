using System;
using System.Drawing;
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

    /// <summary>user32!GetComboBoxInfo. Available since Windows 2000.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetComboBoxInfo(IntPtr hwndCombo, ref COMBOBOXINFO info);

    /// <summary>RECT, windef.h.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    /// <summary>
    /// COMBOBOXINFO, winuser.h. The drop down asks the control itself where its
    /// text area and its button are, rather than working them out again, and the
    /// list is a window of its own whose frame and scroll bar have to be themed
    /// separately from the combo box that owns it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct COMBOBOXINFO
    {
        public int cbSize;
        public RECT rcItem;
        public RECT rcButton;
        public int stateButton;
        public IntPtr hwndCombo;
        public IntPtr hwndItem;
        public IntPtr hwndList;
    }

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
