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

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE, Windows 11 (build 22000) and later.</summary>
    private const int DwmwaWindowCornerPreference = 33;

    /// <summary>DWMWCP_ROUND - the eight pixel radius Windows 11 rounds a flyout with.</summary>
    private const int DwmwcpRound = 2;

    /// <summary>PreferredAppMode.ForceDark</summary>
    private const int PreferredAppModeForceDark = 2;

    /// <summary>PreferredAppMode.Default</summary>
    private const int PreferredAppModeDefault = 0;

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
    /// Sets the process wide preferred app mode. Called once per theme switch.
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
            Logger.Debug("SetPreferredAppMode(" + mode + ") applied.");
        }
        catch (Exception ex)
        {
            _appModeFailed = true;
            Logger.Warn(
                "Undocumented uxtheme dark mode API unavailable, falling back to managed colours only: " +
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
                Logger.Debug("DwmSetWindowAttribute(20) returned HRESULT 0x" + hr.ToString("X8"));
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("DwmSetWindowAttribute failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Asks DWM to round the corners of a window, and reports whether it did.
    ///
    /// <para>
    /// This is the Windows 11 way, and the only one that gets anti aliased corners
    /// and the shadow that belongs to them. On Windows 10 the attribute does not
    /// exist and DwmSetWindowAttribute answers E_INVALIDARG, which is the signal the
    /// caller needs to fall back to clipping the window with a rounded region.
    /// </para>
    /// </summary>
    public static bool TryRoundCorners(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            int preference = DwmwcpRound;
            int hr = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
            if (hr == 0)
            {
                return true;
            }

            Logger.Debug("DwmSetWindowAttribute(33) returned HRESULT 0x" + hr.ToString("X8") + ", rounding by region instead.");
        }
        catch (Exception ex)
        {
            Logger.Debug("DwmSetWindowAttribute(33) failed: " + ex.Message);
        }

        return false;
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
            Logger.Debug("SetWindowTheme failed: " + ex.Message);
        }
    }
}
