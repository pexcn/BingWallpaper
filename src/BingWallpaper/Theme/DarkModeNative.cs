using System;
using System.Runtime.InteropServices;

namespace BingWallpaper.Theme;

/// <summary>
/// Win32 interop for dark mode.
///
/// The uxtheme entries below are *undocumented ordinal exports*. They are the only
/// way to make common controls (menus, scroll bars, list views) follow a dark
/// palette on Windows 10, and they are what every dark-mode Win32 application uses
/// (see the MIT licensed reference implementation adzm/win32-darkmode).
///
/// Risk: an ordinal can disappear or change meaning in a future Windows build.
/// Therefore every call here is wrapped in try/catch; on failure the application
/// logs and keeps running with the light palette instead of crashing.
///
/// Application.SetColorMode is deliberately NOT used: SystemColorMode is only
/// supported on Windows 11 and silently falls back to the classic light theme on
/// Windows 10, which is our minimum target (LTSC 2021, build 19044).
/// </summary>
internal static class DarkModeNative
{
    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE, stable value since build 19041.</summary>
    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>PreferredAppMode.AllowDark</summary>
    private const int PreferredAppModeAllowDark = 1;

    /// <summary>PreferredAppMode.ForceDark</summary>
    private const int PreferredAppModeForceDark = 2;

    /// <summary>PreferredAppMode.ForceLight</summary>
    private const int PreferredAppModeForceLight = 3;

    private static bool _appModeFailed;

    /// <summary>
    /// dwmapi!DwmSetWindowAttribute - Windows Vista+, attribute 20 needs build 19041+.
    /// Our minimum supported build is 19044, so no compatibility branch for the old
    /// attribute number 19 is required.
    /// </summary>
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    /// <summary>uxtheme!SetWindowTheme - Windows XP+.</summary>
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    /// <summary>
    /// uxtheme.dll ordinal #135, SetPreferredAppMode(int). Undocumented, semantics
    /// stable since Windows 10 1903. On 1809 the same ordinal was AllowDarkModeForApp(bool);
    /// we require 19044 so the int signature is correct.
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
    /// uxtheme.dll ordinal #136, FlushMenuThemes(). Undocumented, 1903+. Drops the
    /// cached menu theme so the next popup menu is drawn in the app mode set above;
    /// without it a menu keeps the colours it was first opened with.
    /// </summary>
    [DllImport("uxtheme.dll", EntryPoint = "#136", SetLastError = true)]
    private static extern void FlushMenuThemes();

    /// <summary>
    /// Sets the process wide preferred app mode. Called once per theme switch.
    /// <para>
    /// This is what colours the tray menu: it is a native popup drawn by Windows, so
    /// it follows the app mode rather than any managed palette. ForceLight rather
    /// than Default for the light theme, because Default means "whatever the system
    /// is set to" - which would hand a dark menu to a window painted light.
    /// </para>
    /// </summary>
    public static void SetAppMode(bool dark)
    {
        if (_appModeFailed)
        {
            return;
        }

        try
        {
            int mode = dark ? PreferredAppModeForceDark : PreferredAppModeForceLight;
            SetPreferredAppMode(mode);
            RefreshImmersiveColorPolicyState();
            FlushMenuThemes();
            Logger.Debug("darkmode: setpreferredappmode applied mode=" + mode);
        }
        catch (Exception ex)
        {
            _appModeFailed = true;
            // Warn rather than Debug: this one is process wide, so the whole app falls
            // back to managed colours. The per-window helpers below only lose a detail.
            Logger.Warn(
                "darkmode: uxtheme api unavailable, using managed colours only error=" +
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
            Logger.Debug("darkmode: allowdarkmodeforwindow failed error=" + ex.Message);
        }
    }

    /// <summary>Paints the title bar dark (or restores the light one).</summary>
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
                Logger.Debug("darkmode: dwmsetwindowattribute hresult=0x" + hr.ToString("X8"));
            }
        }
        catch (Exception ex)
        {
            Logger.Debug("darkmode: dwmsetwindowattribute failed error=" + ex.Message);
        }
    }

    /// <summary>
    /// Applies a themed window class. "DarkMode_Explorer" darkens list views, tree
    /// views and scroll bars; "DarkMode_CFD" darkens combo box and edit borders.
    /// Passing null restores the default theme.
    /// </summary>
    public static void ApplyWindowTheme(IntPtr handle, string? theme)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            SetWindowTheme(handle, theme, null);
        }
        catch (Exception ex)
        {
            Logger.Debug("darkmode: setwindowtheme failed error=" + ex.Message);
        }
    }

    /// <summary>Kept for completeness; AllowDark is the softer variant of ForceDark.</summary>
    public static void SetAllowDarkAppMode()
    {
        try
        {
            SetPreferredAppMode(PreferredAppModeAllowDark);
            RefreshImmersiveColorPolicyState();
        }
        catch (Exception ex)
        {
            Logger.Debug("darkmode: setpreferredappmode(allowdark) failed error=" + ex.Message);
        }
    }
}
