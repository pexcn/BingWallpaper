using System.Drawing;

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
        Color selectionText)
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

    public static ThemePalette Light { get; } = new(
        isDark: false,
        windowBackground: SystemColors.Control,
        controlBackground: SystemColors.Window,
        border: SystemColors.ControlDark,
        text: SystemColors.ControlText,
        secondaryText: SystemColors.GrayText,
        hover: SystemColors.ControlLight,
        selection: SystemColors.Highlight,
        selectionText: SystemColors.HighlightText);

    public static ThemePalette Dark { get; } = new(
        isDark: true,
        windowBackground: Color.FromArgb(0x20, 0x20, 0x20),
        controlBackground: Color.FromArgb(0x2D, 0x2D, 0x2D),
        border: Color.FromArgb(0x3F, 0x3F, 0x46),
        text: Color.FromArgb(0xFF, 0xFF, 0xFF),
        secondaryText: Color.FromArgb(0xC0, 0xC0, 0xC0),
        hover: Color.FromArgb(0x3D, 0x3D, 0x3D),
        selection: Color.FromArgb(0x00, 0x78, 0xD4),
        selectionText: Color.FromArgb(0xFF, 0xFF, 0xFF));

    public static ThemePalette For(bool dark) => dark ? Dark : Light;
}
