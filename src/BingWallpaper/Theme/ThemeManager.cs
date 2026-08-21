using System;
using System.Drawing;
using System.Windows.Forms;
using BingWallpaper.UI;
using Microsoft.Win32;

namespace BingWallpaper.Theme;

/// <summary>
/// Owns the current palette, detects the system theme and repaints every window.
///
/// Everything here is hand written on purpose - see DarkModeNative for why
/// Application.SetColorMode cannot be used on Windows 10.
/// </summary>
internal static class ThemeManager
{
    private const string PersonalizeKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    /// <summary>Raised after the palette changed; forms re-apply themselves.</summary>
    public static event EventHandler? ThemeChanged;

    public static ThemeMode Mode { get; private set; } = ThemeMode.System;

    public static ThemePalette Palette { get; private set; } = ThemePalette.Light;

    public static bool IsDark => Palette.IsDark;

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
        Palette = ThemePalette.For(Resolve(mode));
        DarkModeNative.SetAppMode(Palette.IsDark);
        Logger.Info("Theme initialized: mode=" + mode + " effective=" + (Palette.IsDark ? "Dark" : "Light"));
    }

    /// <summary>Switches the mode and repaints all registered windows.</summary>
    public static void SetMode(ThemeMode mode)
    {
        bool dark = Resolve(mode);
        bool changed = mode != Mode || dark != Palette.IsDark;
        Mode = mode;
        if (!changed)
        {
            return;
        }

        Palette = ThemePalette.For(dark);
        DarkModeNative.SetAppMode(dark);
        Logger.Info("Theme changed: mode=" + mode + " effective=" + (dark ? "Dark" : "Light"));
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Called when WM_SETTINGCHANGE/"ImmersiveColorSet" arrives. Only the
    /// "follow system" mode reacts to it.
    /// </summary>
    public static void HandleSystemThemeChanged()
    {
        if (Mode != ThemeMode.System)
        {
            return;
        }

        bool dark = IsSystemDark();
        if (dark == Palette.IsDark)
        {
            return;
        }

        Palette = ThemePalette.For(dark);
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

    /// <summary>
    /// Applies the real Windows UI font. .NET Framework still defaults controls to
    /// MS Sans Serif 8.25pt, which looks dated and measures differently from the
    /// font the rest of the system uses (Segoe UI / Microsoft YaHei UI 9pt).
    /// </summary>
    public static void ApplySystemFont(Control control)
    {
        try
        {
            Font? font = SystemFonts.MessageBoxFont;
            if (font is not null)
            {
                control.Font = font;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not apply the system UI font: " + ex.Message);
        }
    }

    /// <summary>Applies the palette to a whole window, including its title bar.</summary>
    public static void ApplyToForm(Form form)
    {
        if (form.IsHandleCreated)
        {
            DarkModeNative.AllowDarkModeForHandle(form.Handle, Palette.IsDark);
            DarkModeNative.ApplyTitleBar(form.Handle, Palette.IsDark);
        }

        ApplyToControl(form);
        form.Invalidate(invalidateChildren: true);
    }

    /// <summary>
    /// Recursively colours a control tree.
    ///
    /// <para>
    /// There is little left to do here: every control that has a frame or a glyph
    /// paints itself from the palette (see <see cref="ControlPainter"/>), so this
    /// only hands out the two colours a control cannot derive - the surface it sits
    /// on and the colour of its text - and lets the ones with a native window of
    /// their own re-theme it.
    /// </para>
    /// </summary>
    public static void ApplyToControl(Control control)
    {
        ThemePalette palette = Palette;

        switch (control)
        {
            case ThemedComboBox comboBox:
                comboBox.ApplyTheme();
                break;

            case TextBox textBox:
                textBox.BackColor = palette.Field;
                textBox.ForeColor = palette.Text;
                textBox.BorderStyle = BorderStyle.FixedSingle;
                // DarkMode_CFD is the theme Windows uses for the edit fields of its
                // own dark dialogs; it darkens the frame and the scroll bars.
                ApplyNativeTheme(textBox, palette.IsDark ? "DarkMode_CFD" : null);
                break;

            default:
                control.BackColor = palette.Window;
                control.ForeColor = palette.Text;
                control.Invalidate();
                break;
        }

        foreach (Control child in control.Controls)
        {
            ApplyToControl(child);
        }
    }

    private static void ApplyNativeTheme(Control control, string? theme)
    {
        if (!control.IsHandleCreated)
        {
            return;
        }

        DarkModeNative.AllowDarkModeForHandle(control.Handle, Palette.IsDark);
        DarkModeNative.ApplyWindowTheme(control.Handle, theme);
    }
}
