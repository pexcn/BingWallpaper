using System;
using System.Drawing;

namespace BingWallpaper.Theme;

/// <summary>
/// The state a control part is painted in. The values are the uxtheme state ids
/// shared by the push button, check box, radio button and drop down parts
/// (vsstyle.h: PBS_NORMAL/HOT/PRESSED/DISABLED, CBRO_*, CBXSR_*), so one value can
/// be handed both to the visual styles renderer and to the palette.
/// </summary>
internal enum PartState
{
    Normal = 1,
    Hot = 2,
    Pressed = 3,
    Disabled = 4,
}

/// <summary>
/// Every colour the interface uses. Switching theme means swapping the palette
/// instance and repainting - there are no colour literals anywhere else.
///
/// <para>
/// The palette answers to two different design languages on purpose, because the
/// interface shows two different kinds of surface. The settings window is a Win32
/// dialog and its colours are the ones a dialog uses; in the light theme they are
/// barely reached at all, since every framed control there is drawn by uxtheme
/// itself (see <see cref="BingWallpaper.UI.ControlPainter"/>). The tray menu is a
/// flyout, and Windows 11 draws flyouts with WinUI 3 - so the menu colours below
/// are the WinUI theme resources, named after their resource keys.
/// </para>
/// </summary>
internal sealed class ThemePalette
{
    /// <summary>Fallback colour of AcrylicBackgroundFillColorDefaultBrush, light.</summary>
    private static readonly Color LightFlyoutBase = Color.FromArgb(0xF9, 0xF9, 0xF9);

    /// <summary>Fallback colour of AcrylicBackgroundFillColorDefaultBrush, dark.</summary>
    private static readonly Color DarkFlyoutBase = Color.FromArgb(0x2C, 0x2C, 0x2C);

    private ThemePalette(bool isDark) => IsDark = isDark;

    public bool IsDark { get; }

    // ---- dialog surfaces -----------------------------------------------------

    /// <summary>Dialog background.</summary>
    public Color Window { get; private set; }

    /// <summary>Background of a framed field: drop down, list, text box.</summary>
    public Color Field { get; private set; }

    /// <summary>Face of a push button.</summary>
    public Color Surface { get; private set; }

    public Color SurfaceHot { get; private set; }

    public Color SurfacePressed { get; private set; }

    /// <summary>Frames and separators.</summary>
    public Color Border { get; private set; }

    /// <summary>Frame of a control under the pointer.</summary>
    public Color BorderHot { get; private set; }

    public Color BorderDisabled { get; private set; }

    public Color Text { get; private set; }

    /// <summary>Text of a disabled control.</summary>
    public Color DisabledText { get; private set; }

    /// <summary>Captions that sit next to the real text, such as a tile date.</summary>
    public Color SecondaryText { get; private set; }

    public Color Selection { get; private set; }

    public Color SelectionText { get; private set; }

    /// <summary>Windows accent blue: focus frames, checked glyphs, badges.</summary>
    public Color Accent { get; private set; }

    /// <summary>Fill of an unchecked check box or radio button.</summary>
    public Color GlyphBackground { get; private set; }

    /// <summary>Outline of an unchecked check box or radio button.</summary>
    public Color GlyphBorder { get; private set; }

    /// <summary>The tick or dot drawn on the accent fill.</summary>
    public Color GlyphMark { get; private set; }

    // ---- flyout surfaces (WinUI 3) -------------------------------------------

    /// <summary>MenuFlyoutPresenterBackground, resolved to the acrylic fallback colour.</summary>
    public Color MenuBackground { get; private set; }

    /// <summary>MenuFlyoutPresenterBorderBrush = SurfaceStrokeColorFlyout.</summary>
    public Color MenuBorder { get; private set; }

    /// <summary>MenuFlyoutSeparatorBackground = DividerStrokeColorDefault.</summary>
    public Color MenuDivider { get; private set; }

    /// <summary>MenuFlyoutItemBackgroundPointerOver = SubtleFillColorSecondary.</summary>
    public Color MenuItemHot { get; private set; }

    /// <summary>MenuFlyoutItemBackgroundPressed = SubtleFillColorTertiary.</summary>
    public Color MenuItemPressed { get; private set; }

    /// <summary>MenuFlyoutItemForeground = TextFillColorPrimary.</summary>
    public Color MenuText { get; private set; }

    /// <summary>The check glyph colour: MenuFlyoutSubItemChevron = TextFillColorSecondary.</summary>
    public Color MenuTextSecondary { get; private set; }

    /// <summary>MenuFlyoutItemForegroundDisabled = TextFillColorDisabled.</summary>
    public Color MenuTextDisabled { get; private set; }

    public static ThemePalette Light { get; } = new ThemePalette(isDark: false)
    {
        Window = SystemColors.Control,
        Field = SystemColors.Window,
        Surface = SystemColors.Control,
        SurfaceHot = SystemColors.ControlLight,
        SurfacePressed = SystemColors.ControlDark,
        Border = SystemColors.ControlDark,
        // The frame the Aero theme puts around a hovered field.
        BorderHot = Color.FromArgb(0x7E, 0xB4, 0xEA),
        BorderDisabled = SystemColors.ControlLight,
        Text = SystemColors.ControlText,
        DisabledText = SystemColors.GrayText,
        SecondaryText = SystemColors.GrayText,
        Selection = SystemColors.Highlight,
        SelectionText = SystemColors.HighlightText,
        Accent = Color.FromArgb(0x00, 0x78, 0xD4),
        GlyphBackground = SystemColors.Window,
        GlyphBorder = Color.FromArgb(0x70, 0x70, 0x70),
        GlyphMark = Color.White,

        MenuBackground = LightFlyoutBase,
        MenuBorder = Over(0x0F000000, LightFlyoutBase),
        MenuDivider = Over(0x0F000000, LightFlyoutBase),
        MenuItemHot = Over(0x09000000, LightFlyoutBase),
        MenuItemPressed = Over(0x06000000, LightFlyoutBase),
        MenuText = Over(0xE4000000, LightFlyoutBase),
        MenuTextSecondary = Over(0x9E000000, LightFlyoutBase),
        MenuTextDisabled = Over(0x5C000000, LightFlyoutBase),
    };

    public static ThemePalette Dark { get; } = new ThemePalette(isDark: true)
    {
        // SolidBackgroundFillColorBase, which is also what Windows paints its own
        // dark dialogs with.
        Window = Color.FromArgb(0x20, 0x20, 0x20),
        Field = Color.FromArgb(0x2D, 0x2D, 0x2D),
        Surface = Color.FromArgb(0x2D, 0x2D, 0x2D),
        SurfaceHot = Color.FromArgb(0x32, 0x32, 0x32),
        SurfacePressed = Color.FromArgb(0x27, 0x27, 0x27),
        Border = Color.FromArgb(0x3D, 0x3D, 0x3D),
        BorderHot = Color.FromArgb(0x53, 0x53, 0x53),
        BorderDisabled = Color.FromArgb(0x2B, 0x2B, 0x2B),
        Text = Color.White,
        DisabledText = Color.FromArgb(0x71, 0x71, 0x71),
        SecondaryText = Color.FromArgb(0xCC, 0xCC, 0xCC),
        Selection = Color.FromArgb(0x00, 0x78, 0xD4),
        SelectionText = Color.White,
        Accent = Color.FromArgb(0x00, 0x78, 0xD4),
        GlyphBackground = Color.FromArgb(0x2D, 0x2D, 0x2D),
        // ControlStrongStrokeColorDefault over the window: the one control frame
        // Windows draws bright even in the dark theme, so an empty check box does
        // not disappear into the dialog behind it.
        GlyphBorder = Color.FromArgb(0x9A, 0x9A, 0x9A),
        GlyphMark = Color.White,

        MenuBackground = DarkFlyoutBase,
        MenuBorder = Over(0x33000000, DarkFlyoutBase),
        MenuDivider = Over(0x15FFFFFF, DarkFlyoutBase),
        MenuItemHot = Over(0x0FFFFFFF, DarkFlyoutBase),
        MenuItemPressed = Over(0x0AFFFFFF, DarkFlyoutBase),
        MenuText = Color.White,
        MenuTextSecondary = Over(0xC5FFFFFF, DarkFlyoutBase),
        MenuTextDisabled = Over(0x5DFFFFFF, DarkFlyoutBase),
    };

    public static ThemePalette For(bool dark) => dark ? Dark : Light;

    /// <summary>Face of a push button, or of the closed part of a drop down.</summary>
    public Color FaceFor(PartState state) => state switch
    {
        PartState.Hot => SurfaceHot,
        PartState.Pressed => SurfacePressed,
        _ => Surface,
    };

    /// <summary>Frame of a push button, a drop down or a glyph.</summary>
    public Color BorderFor(PartState state) => state switch
    {
        PartState.Hot => BorderHot,
        PartState.Pressed => Accent,
        PartState.Disabled => BorderDisabled,
        _ => Border,
    };

    public Color TextFor(PartState state) => state == PartState.Disabled ? DisabledText : Text;

    /// <summary>
    /// Composites a WinUI colour - all of the flyout ones carry an alpha channel -
    /// over the opaque surface it is meant to sit on, and keeps the result.
    ///
    /// <para>
    /// Storing them composited rather than drawing them with their alpha is what
    /// makes the menu one predictable set of colours: TextRenderer draws through
    /// GDI, which has no alpha to work with, and a translucent fill painted twice
    /// (a hover behind a pressed state) would not land on the WinUI value either.
    /// </para>
    /// </summary>
    /// <param name="layer">The WinUI colour, as the 0xAARRGGBB literal it is written as.</param>
    /// <param name="under">The opaque surface it is composited over.</param>
    private static Color Over(uint layer, Color under)
    {
        Color colour = Color.FromArgb(unchecked((int)layer));
        float alpha = colour.A / 255f;

        int Mix(int over, int below) => (int)Math.Round((over * alpha) + (below * (1f - alpha)));

        return Color.FromArgb(
            Mix(colour.R, under.R),
            Mix(colour.G, under.G),
            Mix(colour.B, under.B));
    }
}
