using System;
using System.Drawing;
using System.Windows.Forms;

namespace BingWallpaper.Theme;

/// <summary>
/// Every colour the UI uses. Switching theme means swapping the palette instance
/// and repainting - no colour literals anywhere else in the code base.
/// </summary>
internal sealed class ThemePalette
{
    private ThemePalette(
        bool isDark,
        Color windowBackground,
        Color controlBackground,
        Color border,
        Color text,
        Color secondaryText,
        Color hover,
        Color selection,
        Color selectionText,
        Color accent,
        Color glyphBackground,
        Color glyphBorder,
        Color glyphMark,
        Color menuBackground,
        Color menuBorder,
        Color menuItemHover,
        Color menuItemPressed,
        Color menuItemBorder,
        Color menuSeparator)
    {
        IsDark = isDark;
        WindowBackground = windowBackground;
        ControlBackground = controlBackground;
        Border = border;
        Text = text;
        SecondaryText = secondaryText;
        Hover = hover;
        Selection = selection;
        SelectionText = selectionText;
        Accent = accent;
        GlyphBackground = glyphBackground;
        GlyphBorder = glyphBorder;
        GlyphMark = glyphMark;
        MenuBackground = menuBackground;
        MenuBorder = menuBorder;
        MenuItemHover = menuItemHover;
        MenuItemPressed = menuItemPressed;
        MenuItemBorder = menuItemBorder;
        MenuSeparator = menuSeparator;
    }

    public bool IsDark { get; }

    public Color WindowBackground { get; }

    public Color ControlBackground { get; }

    public Color Border { get; }

    public Color Text { get; }

    public Color SecondaryText { get; }

    public Color Hover { get; }

    public Color Selection { get; }

    public Color SelectionText { get; }

    /// <summary>Windows accent blue, used for checked radio buttons and check boxes.</summary>
    public Color Accent { get; }

    /// <summary>Fill of an unchecked radio/check glyph.</summary>
    public Color GlyphBackground { get; }

    /// <summary>Outline of an unchecked radio/check glyph.</summary>
    public Color GlyphBorder { get; }

    /// <summary>The dot/tick drawn on top of the accent fill.</summary>
    public Color GlyphMark { get; }

    // The six colours below are the menu's own. In the dark they are the window
    // colours over again, but they stay separate entries because the light theme
    // does not mirror its window that way: there they come from the framework's
    // ProfessionalColorTable, so a light menu keeps looking exactly like the one
    // Windows Forms drew before this renderer existed.

    /// <summary>Surface of the menu popup.</summary>
    public Color MenuBackground { get; }

    /// <summary>Hairline around the menu popup.</summary>
    public Color MenuBorder { get; }

    /// <summary>Fill of the bar behind the row under the pointer.</summary>
    public Color MenuItemHover { get; }

    /// <summary>Same bar while the mouse button is held down.</summary>
    public Color MenuItemPressed { get; }

    /// <summary>Outline around that bar.</summary>
    public Color MenuItemBorder { get; }

    /// <summary>The thin rule between two groups of menu items.</summary>
    public Color MenuSeparator { get; }

    public static ThemePalette Light { get; } = CreateLight();

    public static ThemePalette Dark { get; } = CreateDark();

    public static ThemePalette For(bool dark) => dark ? Dark : Light;

    private static ThemePalette CreateLight()
    {
        // Read rather than guessed. Every hand picked approximation of these was
        // visibly off against the menu the framework draws, and the values move with
        // the Windows visual style anyway.
        ProfessionalColorTable system = new ProfessionalColorTable();

        return new ThemePalette(
            isDark: false,
            windowBackground: SystemColors.Control,
            controlBackground: SystemColors.Window,
            border: SystemColors.ControlDark,
            text: SystemColors.ControlText,
            secondaryText: SystemColors.GrayText,
            hover: SystemColors.ControlLight,
            selection: SystemColors.Highlight,
            selectionText: SystemColors.HighlightText,
            accent: Color.FromArgb(0x00, 0x78, 0xD4),
            glyphBackground: Color.White,
            glyphBorder: Color.FromArgb(0x86, 0x86, 0x86),
            glyphMark: Color.White,
            menuBackground: system.ToolStripDropDownBackground,
            menuBorder: system.MenuBorder,
            menuItemHover: system.MenuItemSelected,
            // The framework presses a row with a three stop gradient. One flat colour
            // stands in for it: the state lasts as long as the mouse button is down.
            menuItemPressed: system.MenuItemPressedGradientMiddle,
            menuItemBorder: system.MenuItemBorder,
            menuSeparator: system.SeparatorDark);
    }

    private static ThemePalette CreateDark()
    {
        Color surface = Color.FromArgb(0x2D, 0x2D, 0x2D);
        Color border = Color.FromArgb(0x3F, 0x3F, 0x46);
        Color hover = Color.FromArgb(0x3D, 0x3D, 0x3D);
        Color accent = Color.FromArgb(0x00, 0x78, 0xD4);

        return new ThemePalette(
            isDark: true,
            windowBackground: Color.FromArgb(0x20, 0x20, 0x20),
            controlBackground: surface,
            border: border,
            text: Color.FromArgb(0xFF, 0xFF, 0xFF),
            secondaryText: Color.FromArgb(0xC0, 0xC0, 0xC0),
            hover: hover,
            selection: accent,
            selectionText: Color.FromArgb(0xFF, 0xFF, 0xFF),
            accent: accent,
            glyphBackground: surface,
            glyphBorder: Color.FromArgb(0x9A, 0x9A, 0x9A),
            glyphMark: Color.White,
            menuBackground: surface,
            menuBorder: border,
            menuItemHover: hover,
            // No separate pressed state in the dark, the way the menu had none before.
            menuItemPressed: hover,
            menuItemBorder: accent,
            menuSeparator: border);
    }
}
