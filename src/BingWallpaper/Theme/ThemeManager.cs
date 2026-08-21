using System;
using System.Drawing;
using System.Windows.Forms;
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

    /// <summary>Recursively colours a control tree.</summary>
    public static void ApplyToControl(Control control)
    {
        ThemePalette palette = Palette;
        bool dark = palette.IsDark;

        switch (control)
        {
            case Form form:
                form.BackColor = palette.WindowBackground;
                form.ForeColor = palette.Text;
                break;

            case Button button:
                // Every button in this UI is a ThemedButton, which reads the palette
                // while painting: the colours here only keep the control's own
                // background from showing through during a resize, and the repaint is
                // what actually applies the theme. Setting FlatStyle would fight the
                // one the owner drawn button picked for itself.
                button.BackColor = dark ? palette.ControlBackground : SystemColors.Control;
                button.ForeColor = palette.Text;
                button.Invalidate();
                break;

            case CheckBox or RadioButton:
                control.BackColor = palette.WindowBackground;
                control.ForeColor = palette.Text;

                // The owner drawn check/radio glyphs read the palette while painting.
                control.Invalidate();
                break;

            case GroupBox:
                control.BackColor = palette.WindowBackground;
                control.ForeColor = palette.Text;
                break;

            case LinkLabel link:
                link.BackColor = palette.WindowBackground;
                link.ForeColor = palette.Text;
                link.LinkColor = dark ? palette.Selection : SystemColors.HotTrack;
                link.ActiveLinkColor = palette.Selection;
                link.VisitedLinkColor = dark ? palette.Selection : SystemColors.HotTrack;
                break;

            case Label:
                control.BackColor = palette.WindowBackground;
                control.ForeColor = palette.Text;
                break;

            case TextBox textBox:
                textBox.BackColor = palette.ControlBackground;
                textBox.ForeColor = palette.Text;
                textBox.BorderStyle = BorderStyle.FixedSingle;
                ApplyNativeTheme(textBox, dark ? "DarkMode_CFD" : null);
                break;

            case ComboBox comboBox:
                comboBox.BackColor = palette.ControlBackground;
                comboBox.ForeColor = palette.Text;
                comboBox.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
                ApplyNativeTheme(comboBox, dark ? "DarkMode_CFD" : null);
                break;

            case NumericUpDown numeric:
                numeric.BackColor = palette.ControlBackground;
                numeric.ForeColor = palette.Text;
                numeric.BorderStyle = BorderStyle.FixedSingle;
                ApplyNativeTheme(numeric, dark ? "DarkMode_CFD" : null);
                break;

            case ListView listView:
                listView.BackColor = palette.ControlBackground;
                listView.ForeColor = palette.Text;
                listView.BorderStyle = BorderStyle.FixedSingle;
                ApplyNativeTheme(listView, dark ? "DarkMode_Explorer" : "Explorer");
                break;

            case ProgressBar:
                break;

            default:
                control.BackColor = palette.WindowBackground;
                control.ForeColor = palette.Text;
                break;
        }

        if (control.ContextMenuStrip is not null)
        {
            ApplyToMenu(control.ContextMenuStrip);
        }

        foreach (Control child in control.Controls)
        {
            ApplyToControl(child);
        }
    }

    /// <summary>
    /// Themes a context menu. Both themes go through one renderer, rather than the
    /// framework's in the light and a custom one in the dark: two renderers drift, and
    /// the details they drift on (how long a separator is, how a checked row is
    /// marked) are exactly the ones a screenshot puts side by side. The light theme
    /// loses nothing by it - its colours are still the framework's own, read off a
    /// <see cref="System.Windows.Forms.ProfessionalColorTable"/> in
    /// <see cref="ThemePalette"/>.
    /// </summary>
    public static void ApplyToMenu(ToolStrip menu)
    {
        ThemePalette palette = Palette;
        menu.BackColor = palette.MenuBackground;
        menu.ForeColor = palette.Text;

        // Assigning a renderer implicitly switches RenderMode to Custom, which is
        // what the professional renderer subclass needs.
        menu.Renderer = new FluentMenuRenderer(palette);

        if (menu.IsHandleCreated)
        {
            DarkModeNative.AllowDarkModeForHandle(menu.Handle, palette.IsDark);
        }

        ApplyToMenuItems(menu.Items, palette);
    }

    /// <summary>
    /// Re-colours the items of a menu after their Enabled state changed.
    /// <para>
    /// ToolStripMenuItem greys disabled text on its own only as long as nobody has
    /// assigned ForeColor; this theme assigns it, so the colour becomes a snapshot of
    /// whatever Enabled was at that moment. An item that starts out disabled and is
    /// enabled later therefore keeps the grey it was given - which is why this has to
    /// be called whenever the menu state is recomputed, in both themes.
    /// </para>
    /// </summary>
    public static void RefreshMenuItemColors(ToolStrip menu) => ApplyToMenuItems(menu.Items, Palette);

    private static void ApplyToMenuItems(ToolStripItemCollection items, ThemePalette palette)
    {
        foreach (ToolStripItem item in items)
        {
            item.BackColor = palette.MenuBackground;
            item.ForeColor = item.Enabled ? palette.Text : palette.SecondaryText;

            if (item is ToolStripMenuItem menuItem && menuItem.HasDropDownItems)
            {
                menuItem.DropDown.BackColor = palette.MenuBackground;
                menuItem.DropDown.ForeColor = palette.Text;
                ApplyToMenuItems(menuItem.DropDownItems, palette);
            }
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
