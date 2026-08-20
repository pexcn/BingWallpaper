using System;
using Microsoft.Win32;

namespace BingWallpaper.Theme;

/// <summary>
/// Owns the effective theme: resolves the configured <see cref="ThemeMode"/>
/// against the system preference and tells everyone that paints when it changes.
///
/// Much smaller than its Windows Forms predecessor, which had to carry a palette
/// and repaint every control by hand. WinUI does that part: a window only has to
/// pass <see cref="ElementTheme"/> down to its content. What is left here is the
/// decision itself, plus the two Win32 corners of the UI - the title bar and the
/// tray menu - that WinUI does not reach (see <see cref="DarkModeNative"/>).
/// </summary>
internal static class ThemeManager
{
    private const string PersonalizeKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    /// <summary>Raised after the effective theme changed; windows re-apply themselves.</summary>
    public static event EventHandler? ThemeChanged;

    public static ThemeMode Mode { get; private set; } = ThemeMode.System;

    public static bool IsDark { get; private set; }

    /// <summary>What a WinUI window has to hand to its content.</summary>
    public static Microsoft.UI.Xaml.ElementTheme ElementTheme => Mode switch
    {
        ThemeMode.Light => Microsoft.UI.Xaml.ElementTheme.Light,
        ThemeMode.Dark => Microsoft.UI.Xaml.ElementTheme.Dark,
        _ => Microsoft.UI.Xaml.ElementTheme.Default,
    };

    /// <summary>
    /// Read-only probe of the user's app theme preference. This does not violate the
    /// "no registry writes" rule - nothing is written here.
    /// </summary>
    public static bool IsSystemDark()
    {
        try
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath, writable: false))
            {
                object? value = key?.GetValue(AppsUseLightThemeValue);
                if (value is int number)
                {
                    // 0 = dark, 1 = light. A missing value counts as light.
                    return number == 0;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not read the system theme preference: " + ex.Message);
        }

        return false;
    }

    /// <summary>Applies a mode without raising the change event (startup path).</summary>
    public static void Initialize(ThemeMode mode)
    {
        Mode = mode;
        IsDark = Resolve(mode);
        DarkModeNative.SetAppMode(IsDark);
        Logger.Info("Theme initialized: mode=" + mode + " effective=" + (IsDark ? "Dark" : "Light"));
    }

    /// <summary>Switches the mode and repaints every window that is open.</summary>
    public static void SetMode(ThemeMode mode)
    {
        bool dark = Resolve(mode);
        bool changed = mode != Mode || dark != IsDark;
        Mode = mode;
        if (!changed)
        {
            return;
        }

        IsDark = dark;
        DarkModeNative.SetAppMode(dark);
        Logger.Info("Theme changed: mode=" + mode + " effective=" + (dark ? "Dark" : "Light"));
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Called when Windows broadcasts WM_SETTINGCHANGE/ImmersiveColorSet. Only the
    /// System mode follows along; an explicit Light or Dark choice is the user's,
    /// not the system's.
    /// </summary>
    public static void HandleSystemThemeChanged()
    {
        if (Mode != ThemeMode.System)
        {
            return;
        }

        bool dark = IsSystemDark();
        if (dark == IsDark)
        {
            return;
        }

        IsDark = dark;
        DarkModeNative.SetAppMode(dark);
        Logger.Info("System theme changed, now " + (dark ? "Dark" : "Light") + ".");
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static bool Resolve(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => false,
        ThemeMode.Dark => true,
        _ => IsSystemDark(),
    };
}
