using System;
using System.Runtime.InteropServices;

namespace BingWallpaper;

/// <summary>
/// P/Invoke declarations shared across the program. Theme interop lives in
/// Theme/DarkModeNative.cs, the notification area and its menu in UI/TrayIcon.cs.
/// Every entry lists its Win32 name and the minimum Windows version it requires.
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

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_ICONINFORMATION = 0x00000040;
    private const uint MB_TOPMOST = 0x00040000;

    /// <summary>
    /// user32!SystemParametersInfoW. Available since Windows 2000.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

    /// <summary>
    /// user32!GetDpiForSystem. Available since Windows 10 version 1607 (build 14393),
    /// which is below our minimum of build 19041.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    /// <summary>user32!GetDpiForWindow. Windows 10 1607 (build 14393) and later.</summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    /// <summary>user32!SetForegroundWindow. Available since Windows 2000.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>user32!MessageBoxW. Available since Windows 2000.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

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
    /// The last resort message box, for failures that happen before - or instead of -
    /// the XAML application: a directory that cannot be written to has to be reported
    /// while there is not a single WinUI window to report it in.
    /// </summary>
    public static void ShowError(string caption, string text) => Show(caption, text, MB_ICONERROR);

    public static void ShowInfo(string caption, string text) => Show(caption, text, MB_ICONINFORMATION);

    private static void Show(string caption, string text, uint icon)
    {
        try
        {
            MessageBoxW(IntPtr.Zero, text, caption, MB_OK | icon | MB_TOPMOST);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not show a message box: " + ex.Message);
        }
    }
}
