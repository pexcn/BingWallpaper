using System;
using System.Runtime.InteropServices;

namespace BingWallpaper.Theme;

/// <summary>
/// Win32 interop for the parts of the UI that WinUI does not paint.
///
/// WinUI 3 themes its own windows through <c>ElementTheme</c>, but two things stay
/// out of its reach: the title bar Windows draws around a WinUI window, and the
/// tray menu, which is a real Win32 popup menu. The uxtheme entries below are
/// *undocumented ordinal exports* - they are the only way to make a Win32 menu
/// follow a dark palette on Windows 10, and they are what every dark-mode Win32
/// application uses (see the MIT licensed reference implementation
/// adzm/win32-darkmode).
///
/// Risk: an ordinal can disappear or change meaning in a future Windows build.
/// Therefore every call here is wrapped in try/catch; on failure the application
/// logs and keeps running with a light menu instead of crashing.
/// </summary>
internal static class DarkModeNative
{
    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE, stable value since build 19041.</summary>
    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>PreferredAppMode.Default</summary>
    private const int PreferredAppModeDefault = 0;

    /// <summary>PreferredAppMode.ForceDark</summary>
    private const int PreferredAppModeForceDark = 2;

    private static bool _appModeFailed;

    /// <summary>
    /// dwmapi!DwmSetWindowAttribute - Windows Vista+, attribute 20 needs build 19041+,
    /// which is this program's minimum.
    /// </summary>
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    /// <summary>
    /// uxtheme.dll ordinal #135, SetPreferredAppMode(int). Undocumented, semantics
    /// stable since Windows 10 1903. On 1809 the same ordinal was
    /// AllowDarkModeForApp(bool); we require 19041 so the int signature is correct.
    /// </summary>
    [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
    private static extern int SetPreferredAppMode(int preferredAppMode);

    /// <summary>uxtheme.dll ordinal #133, AllowDarkModeForWindow(HWND, BOOL). Undocumented, 1809+.</summary>
    [DllImport("uxtheme.dll", EntryPoint = "#133", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowDarkModeForWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool allow);

    /// <summary>uxtheme.dll ordinal #104, RefreshImmersiveColorPolicyState(). Undocumented, 1809+.</summary>
    [DllImport("uxtheme.dll", EntryPoint = "#104", SetLastError = true)]
    private static extern void RefreshImmersiveColorPolicyState();

    /// <summary>
    /// uxtheme.dll ordinal #136, FlushMenuThemes(). Undocumented, 1809+. Without it
    /// a menu keeps the colours it was themed with when the process started, so a
    /// theme switch would only reach the menu after a restart.
    /// </summary>
    [DllImport("uxtheme.dll", EntryPoint = "#136", SetLastError = true)]
    private static extern void FlushMenuThemes();

    /// <summary>
    /// Sets the process wide preferred app mode and repaints what is already
    /// themed. Called once per theme switch.
    /// </summary>
    public static void SetAppMode(bool dark)
    {
        if (_appModeFailed)
        {
            return;
        }

        try
        {
            int mode = dark ? PreferredAppModeForceDark : PreferredAppModeDefault;
            SetPreferredAppMode(mode);
            RefreshImmersiveColorPolicyState();
            FlushMenuThemes();
            Logger.Debug("SetPreferredAppMode(" + mode + ") applied.");
        }
        catch (Exception ex)
        {
            _appModeFailed = true;
            Logger.Warn(
                "Undocumented uxtheme dark mode API unavailable, the tray menu stays light: " +
                ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>Allows or denies dark mode for a single window.</summary>
    public static void AllowDarkModeForHandle(IntPtr handle, bool allow)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AllowDarkModeForWindow(handle, allow);
        }
        catch (Exception ex)
        {
            Logger.Debug("AllowDarkModeForWindow failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Paints the title bar dark (or restores the light one). WinUI colours the
    /// client area from the element theme, but the caption is drawn by the desktop
    /// window manager and only listens to this.
    /// </summary>
    public static void ApplyTitleBar(IntPtr handle, bool dark)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            int value = dark ? 1 : 0;
            int hr = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
            if (hr != 0)
            {
                Logger.Debug("DwmSetWindowAttribute(20) returned HRESULT 0x" + hr.ToString("X8"));
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("DwmSetWindowAttribute failed: " + ex.Message);
        }
    }
}
