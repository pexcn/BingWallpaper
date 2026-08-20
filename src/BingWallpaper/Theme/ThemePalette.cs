using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace BingWallpaper.Theme;

/// <summary>
/// The colours of the surfaces this program paints itself: the tray menu and the
/// thumbnail tiles. Everything else is a stock control and takes its colours from
/// the Fluent theme.
///
/// The values are deliberately the Fluent ones, so the two kinds of surface sit
/// next to each other without a seam. Switching theme means swapping the palette
/// instance and repainting - there are no colour literals anywhere else.
/// </summary>
internal sealed class ThemePalette
{
    private ThemePalette(
        bool isDark,
        uint windowBackground,
        uint controlBackground,
        uint border,
        uint text,
        uint secondaryText,
        uint disabledText,
        uint hover,
        uint accent)
    {
        IsDark = isDark;
        WindowBackground = Brush(windowBackground);
        ControlBackground = Brush(controlBackground);
        Border = Brush(border);
        Text = Brush(text);
        SecondaryText = Brush(secondaryText);
        DisabledText = Brush(disabledText);
        Hover = Brush(hover);
        Accent = Brush(accent);
        AccentText = Brush(0xFFFFFFFF);
    }

    public bool IsDark { get; }

    /// <summary>Background of a window or of the tray menu.</summary>
    public IImmutableSolidColorBrush WindowBackground { get; }

    /// <summary>Background of an inset area - the picture box of a tile before it is loaded.</summary>
    public IImmutableSolidColorBrush ControlBackground { get; }

    public IImmutableSolidColorBrush Border { get; }

    public IImmutableSolidColorBrush Text { get; }

    public IImmutableSolidColorBrush SecondaryText { get; }

    public IImmutableSolidColorBrush DisabledText { get; }

    /// <summary>Background of the row the pointer is over.</summary>
    public IImmutableSolidColorBrush Hover { get; }

    /// <summary>Windows accent blue, used for the current tile and the check marks.</summary>
    public IImmutableSolidColorBrush Accent { get; }

    /// <summary>Text drawn on top of <see cref="Accent"/>.</summary>
    public IImmutableSolidColorBrush AccentText { get; }

    public static ThemePalette Light { get; } = new ThemePalette(
        isDark: false,
        windowBackground: 0xFFF9F9F9,
        controlBackground: 0xFFEDEDED,
        border: 0xFFD6D6D6,
        text: 0xFF1A1A1A,
        secondaryText: 0xFF5D5D5D,
        disabledText: 0xFF9D9D9D,
        hover: 0xFFEBEBEB,
        accent: 0xFF0078D4);

    public static ThemePalette Dark { get; } = new ThemePalette(
        isDark: true,
        windowBackground: 0xFF202020,
        controlBackground: 0xFF2D2D2D,
        border: 0xFF3F3F46,
        text: 0xFFFFFFFF,
        secondaryText: 0xFFC0C0C0,
        disabledText: 0xFF7A7A7A,
        hover: 0xFF3D3D3D,
        accent: 0xFF0078D4);

    public static ThemePalette For(bool dark) => dark ? Dark : Light;

    private static IImmutableSolidColorBrush Brush(uint argb) => new ImmutableSolidColorBrush(Color.FromUInt32(argb));
}
