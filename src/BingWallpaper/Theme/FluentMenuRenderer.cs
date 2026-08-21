using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BingWallpaper.Theme;

/// <summary>
/// Feeds the renderer its colours. In the dark these are the palette's own; in the
/// light they came out of a plain <see cref="ProfessionalColorTable"/> when the
/// palette was built, so what is handed back here are the framework's own values and
/// a light menu is painted exactly as it was before this renderer existed.
/// </summary>
internal sealed class FluentColorTable : ProfessionalColorTable
{
    private readonly ThemePalette _palette;

    public FluentColorTable(ThemePalette palette)
    {
        _palette = palette;
        UseSystemColors = false;
    }

    public override Color ToolStripDropDownBackground => _palette.MenuBackground;

    public override Color ToolStripBorder => _palette.MenuBorder;

    public override Color MenuBorder => _palette.MenuBorder;

    public override Color MenuItemBorder => _palette.MenuItemBorder;

    public override Color MenuItemSelected => _palette.MenuItemHover;

    public override Color MenuItemSelectedGradientBegin => _palette.MenuItemHover;

    public override Color MenuItemSelectedGradientEnd => _palette.MenuItemHover;

    public override Color MenuItemPressedGradientBegin => _palette.MenuItemPressed;

    public override Color MenuItemPressedGradientMiddle => _palette.MenuItemPressed;

    public override Color MenuItemPressedGradientEnd => _palette.MenuItemPressed;

    public override Color ImageMarginGradientBegin => _palette.MenuBackground;

    public override Color ImageMarginGradientMiddle => _palette.MenuBackground;

    public override Color ImageMarginGradientEnd => _palette.MenuBackground;

    public override Color SeparatorDark => _palette.MenuSeparator;

    public override Color SeparatorLight => _palette.MenuSeparator;

    public override Color CheckBackground => _palette.MenuItemHover;

    public override Color CheckSelectedBackground => _palette.MenuItemHover;
}

/// <summary>
/// The tray menu's renderer, in both themes.
/// <para>
/// It draws as little as it can get away with. The highlight behind a row, its
/// outline and the popup border are all left to the base renderer, so their geometry
/// stays the framework's - down to the 1px it insets a highlight from the popup edge,
/// the sort of detail that is obvious in a screenshot and impossible to reproduce
/// from memory. What the base renderer cannot supply is colour: menus in Windows
/// Forms have no dark palette at all. So colour arrives through
/// <see cref="FluentColorTable"/>, and only three things are painted by hand.
/// </para>
/// <para>
/// Those three: the popup background, which the base renderer would put a gradient
/// on; the text, because a disabled row has to grey against the palette rather than
/// against the system menu colours; and the check mark, because the framework blits
/// it from a resource bitmap that is dark by design and so invisible on a dark menu.
/// The separator is drawn here too, for a reason of its own - see below.
/// </para>
/// <para>
/// Paint only, no metrics. Row height, icon column and popup padding all stay with
/// Windows Forms. Raising the row height towards the 32px a WinUI flyout uses was
/// tried and reverted: it needs the rest of the WinUI geometry (rounded rows inset
/// from the edge, a wider gutter), and that in turn needs
/// DWMWA_WINDOW_CORNER_PREFERENCE on the popup, which does not exist below build
/// 22000 while this program supports Windows 10.
/// </para>
/// </summary>
internal sealed class FluentMenuRenderer : ToolStripProfessionalRenderer
{
    /// <summary>
    /// How far the rule between two groups of items stays clear of the popup border,
    /// in logical (96 DPI) pixels.
    /// </summary>
    private const int SeparatorInset = 4;

    /// <summary>
    /// Stroke width of the check mark at 96 DPI. Thin enough to stay a tick rather
    /// than a brush stroke, heavy enough to read next to 9pt text.
    /// </summary>
    private const float CheckStroke = 2f;

    private readonly ThemePalette _palette;

    public FluentMenuRenderer(ThemePalette palette)
        : base(new FluentColorTable(palette))
    {
        _palette = palette;
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using (SolidBrush brush = new SolidBrush(_palette.MenuBackground))
        {
            e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.ToolStrip.Size));
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item is { Enabled: true } ? _palette.Text : _palette.SecondaryText;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item is { Enabled: true } ? _palette.Text : _palette.SecondaryText;
        base.OnRenderArrow(e);
    }

    /// <summary>
    /// Draws the check mark of a checked menu item: a bare tick in the icon column, no
    /// box and no accent fill behind it. That is also why it cannot be shared with
    /// <see cref="BingWallpaper.UI.ThemedCheckBox"/> - a check box is a control with a
    /// frame, a checked menu row is not.
    /// </summary>
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        Rectangle bounds = e.ImageRectangle;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        Graphics g = e.Graphics;
        SmoothingMode previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color color = e.Item is { Enabled: true } ? _palette.Text : _palette.SecondaryText;
        using (Pen tick = new Pen(color, Math.Max(1f, CheckStroke * DpiScale.Scale)))
        {
            tick.StartCap = LineCap.Round;
            tick.EndCap = LineCap.Round;
            tick.LineJoin = LineJoin.Round;

            // Spans the icon column almost corner to corner; a tick that sits in the
            // middle of it looks like a smaller glyph in a larger hole.
            g.DrawLines(tick, new[]
            {
                new PointF(bounds.Left + (bounds.Width * 0.18f), bounds.Top + (bounds.Height * 0.52f)),
                new PointF(bounds.Left + (bounds.Width * 0.42f), bounds.Top + (bounds.Height * 0.74f)),
                new PointF(bounds.Left + (bounds.Width * 0.82f), bounds.Top + (bounds.Height * 0.26f)),
            });
        }

        g.SmoothingMode = previous;
    }

    /// <summary>
    /// A single rule running the width of the popup, in both themes. Left to the base
    /// renderer this is instead two stacked lines - the etched look of an older
    /// Windows - starting past the icon column, and the dark menu never drew it that
    /// way. Matching one theme to the other is worth more here than matching either
    /// one to the framework.
    /// </summary>
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        Rectangle bounds = new Rectangle(Point.Empty, e.Item.Size);
        using (SolidBrush background = new SolidBrush(_palette.MenuBackground))
        {
            e.Graphics.FillRectangle(background, bounds);
        }

        int inset = DpiScale.Round(SeparatorInset);
        int right = bounds.Width - inset - 1;
        if (right <= inset)
        {
            return;
        }

        using (Pen pen = new Pen(_palette.MenuSeparator))
        {
            int y = bounds.Height / 2;
            e.Graphics.DrawLine(pen, inset, y, right, y);
        }
    }
}
