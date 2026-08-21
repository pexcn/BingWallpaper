using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using BingWallpaper.Theme;

namespace BingWallpaper.UI;

/// <summary>
/// The measurements of a WinUI 3 menu flyout, in logical pixels, each one named
/// after the theme resource it comes from (MenuFlyout_themeresources.xaml).
///
/// <para>
/// A tray menu is the one part of this program that is not a dialog, and on
/// Windows 11 nothing that pops up out of the shell looks like a Win32 menu any
/// more. So this is not a themed ToolStrip - it is a WinUI flyout rebuilt out of
/// the numbers WinUI itself uses: a rounded window, items that carry their own
/// rounded highlight inside a margin, and a 28 pixel column in front of the text
/// that a check mark can appear in without moving anything.
/// </para>
/// </summary>
internal static class FluentMenuMetrics
{
    /// <summary>MenuFlyoutPresenterThemePadding, 0,2,0,2.</summary>
    public const int PresenterPadding = 2;

    /// <summary>MenuFlyoutItemMargin, 4,2,4,2.</summary>
    public const int ItemMarginX = 4;

    public const int ItemMarginY = 2;

    /// <summary>MenuFlyoutItemThemePadding, 11,8,11,9.</summary>
    public const int ItemPaddingX = 11;

    public const int ItemPaddingTop = 8;

    public const int ItemPaddingBottom = 9;

    /// <summary>MenuFlyoutItemPlaceholderThemeThickness, 28,0,0,0 - the check column.</summary>
    public const int CheckColumn = 28;

    /// <summary>The FontSize of the CheckGlyph FontIcon in ToggleMenuFlyoutItem.</summary>
    public const int CheckGlyph = 12;

    /// <summary>MenuFlyoutThemeMinHeight.</summary>
    public const int MinItemHeight = 32;

    /// <summary>MenuFlyoutSeparatorHeight, and the 1 of MenuFlyoutSeparatorThemePadding.</summary>
    public const int SeparatorHeight = 1;

    public const int SeparatorPadding = 1;

    /// <summary>ControlCornerRadius, the radius of an item highlight.</summary>
    public const int ItemCornerRadius = 4;

    /// <summary>OverlayCornerRadius, the radius of the flyout itself.</summary>
    public const int MenuCornerRadius = 8;

    /// <summary>
    /// The pixel ToolStripSplitStackLayout holds back when it stretches an auto
    /// sized item across a vertical stack (itemSize.Width = preferred - margin - 1).
    /// The highlight is inset by one less on the right, so that it ends up centred
    /// in the menu rather than in the item.
    /// </summary>
    private const int LayoutSlack = 1;

    /// <summary>Flags the item text is measured and drawn with.</summary>
    public static TextFormatFlags TextFlags =>
        TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
        TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    /// <summary>Preferred size of a menu item holding a caption of <paramref name="text"/>.</summary>
    public static Size MeasureItem(Size text)
    {
        int height = Math.Max(
            DpiScale.Round(MinItemHeight),
            text.Height + DpiScale.Round(ItemPaddingTop + ItemPaddingBottom));

        return new Size(
            (DpiScale.Round(ItemMarginX) * 2)
                + (DpiScale.Round(ItemPaddingX) * 2)
                + DpiScale.Round(CheckColumn)
                + text.Width,
            height + (DpiScale.Round(ItemMarginY) * 2));
    }

    public static Size MeasureSeparator()
        => new Size(0, DpiScale.Round(SeparatorHeight) + (DpiScale.Round(SeparatorPadding) * 2));

    /// <summary>The rounded rectangle an item highlights itself with.</summary>
    public static Rectangle HighlightBounds(Size item)
        => Rectangle.FromLTRB(
            DpiScale.Round(ItemMarginX),
            DpiScale.Round(ItemMarginY),
            item.Width - DpiScale.Round(ItemMarginX) + LayoutSlack,
            item.Height - DpiScale.Round(ItemMarginY));

    /// <summary>The square the check glyph is centred in, at the head of the item.</summary>
    public static Rectangle CheckBounds(Rectangle highlight)
    {
        int size = DpiScale.Round(CheckGlyph);
        return new Rectangle(
            highlight.Left + DpiScale.Round(ItemPaddingX),
            highlight.Top + ((highlight.Height - size) / 2),
            size,
            size);
    }

    public static Rectangle TextBounds(Rectangle highlight)
        => Rectangle.FromLTRB(
            highlight.Left + DpiScale.Round(ItemPaddingX) + DpiScale.Round(CheckColumn),
            highlight.Top,
            highlight.Right - DpiScale.Round(ItemPaddingX),
            highlight.Bottom);

    /// <summary>The rule of a separator: one logical pixel across the whole flyout.</summary>
    public static Rectangle SeparatorBounds(Size item)
        => new Rectangle(
            0,
            DpiScale.Round(SeparatorPadding),
            item.Width + LayoutSlack,
            DpiScale.Round(SeparatorHeight));
}

/// <summary>
/// The tray menu: a <see cref="ContextMenuStrip"/> that has had every piece of its
/// own appearance taken off it - no image margin, no check margin, no system
/// border - so that <see cref="FluentMenuRenderer"/> can put a WinUI flyout in its
/// place. The items size themselves (see <see cref="FluentMenuItem"/>), which is
/// what keeps the drop down menu layout from imposing its own 20 pixel rows.
/// </summary>
internal sealed class FluentMenuStrip : ContextMenuStrip
{
    private bool _cornersAreDwm;
    private Size _regionSize;

    public FluentMenuStrip()
    {
        // Both margins off: the check column is part of the item layout here, and a
        // WinForms image margin would draw a second, lighter column behind it.
        ShowImageMargin = false;
        ShowCheckMargin = false;

        Font? menuFont = SystemFonts.MenuFont;
        if (menuFont is not null)
        {
            Font = menuFont;
        }

        Renderer = new FluentMenuRenderer();
        ApplyTheme();
    }

    /// <summary>
    /// The presenter padding, and the reason it cannot simply be assigned:
    /// ToolStripDropDownMenu recomputes its own layout metrics on every pass and
    /// ends that pass with <c>Padding = DefaultPadding</c>, which would put the
    /// WinForms text padding back in front of every entry. Overriding the default is
    /// the only assignment that survives.
    ///
    /// <para>
    /// Only the top is padded. ToolStrip.GetPreferredSizeVertical adds two pixels of
    /// its own below the last item ("add Padding to the bottom if not Overflow"), so
    /// the gap under the last entry is already the one WinUI asks for.
    /// </para>
    /// </summary>
    protected override Padding DefaultPadding
        => new Padding(0, DpiScale.Round(FluentMenuMetrics.PresenterPadding), 0, 0);

    /// <summary>
    /// Follows the palette. The renderer reads it while painting, so a theme change
    /// is a repaint and nothing else - in particular no colour is ever written onto
    /// an item, which is what used to make a menu entry keep the grey it was given
    /// while it was still disabled.
    /// </summary>
    public void ApplyTheme()
    {
        BackColor = ThemeManager.Palette.MenuBackground;
        ForeColor = ThemeManager.Palette.MenuText;
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _cornersAreDwm = DarkModeNative.TryRoundCorners(Handle);
        _regionSize = Size.Empty;
        ApplyCornerRegion();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyCornerRegion();
    }

    /// <summary>
    /// Rounds the corners on the Windows 10 path, where DWM has no say over them:
    /// the window is clipped to a rounded rectangle instead. The region has to be
    /// rebuilt whenever the menu changes size, which a menu does every time its
    /// longest caption changes.
    /// </summary>
    private void ApplyCornerRegion()
    {
        if (_cornersAreDwm || !IsHandleCreated || Size == _regionSize || Width <= 0 || Height <= 0)
        {
            return;
        }

        _regionSize = Size;
        Region? previous = Region;
        using (GraphicsPath path = FluentMenuRenderer.RoundedRectangle(
            new Rectangle(Point.Empty, Size),
            DpiScale.Round(FluentMenuMetrics.MenuCornerRadius)))
        {
            Region = new Region(path);
        }

        previous?.Dispose();
    }
}

/// <summary>
/// One entry of the tray menu. It exists for its size: a menu item inside a
/// ToolStripDropDownMenu otherwise answers with the drop down's own MaxItemSize -
/// a row built from the WinForms text padding and image margin, which is about
/// twenty logical pixels tall. Overriding the measurement is what lets the WinUI
/// metrics through; everything visible about the item is drawn by the renderer.
/// </summary>
internal sealed class FluentMenuItem : ToolStripMenuItem
{
    public FluentMenuItem(string text, EventHandler? onClick)
        : base(text, null, onClick)
    {
    }

    public override Size GetPreferredSize(Size constrainingSize)
    {
        Size text = TextRenderer.MeasureText(
            Text ?? string.Empty,
            Font,
            new Size(int.MaxValue, int.MaxValue),
            FluentMenuMetrics.TextFlags);
        return FluentMenuMetrics.MeasureItem(text);
    }
}

/// <summary>The rule between two groups of entries; sized like the WinUI one.</summary>
internal sealed class FluentMenuSeparator : ToolStripSeparator
{
    public override Size GetPreferredSize(Size constrainingSize) => FluentMenuMetrics.MeasureSeparator();
}
