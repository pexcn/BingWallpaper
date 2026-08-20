using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BingWallpaper.Theme;

/// <summary>Colour table that feeds the dark context menu renderer.</summary>
internal sealed class DarkColorTable : ProfessionalColorTable
{
    private readonly ThemePalette _palette;

    public DarkColorTable(ThemePalette palette)
    {
        _palette = palette;
        UseSystemColors = false;
    }

    public override Color ToolStripDropDownBackground => _palette.ControlBackground;

    public override Color ToolStripBorder => _palette.Border;

    public override Color MenuBorder => _palette.Border;

    public override Color MenuItemBorder => _palette.Selection;

    public override Color MenuItemSelected => _palette.Hover;

    public override Color MenuItemSelectedGradientBegin => _palette.Hover;

    public override Color MenuItemSelectedGradientEnd => _palette.Hover;

    public override Color MenuItemPressedGradientBegin => _palette.Hover;

    public override Color MenuItemPressedGradientMiddle => _palette.Hover;

    public override Color MenuItemPressedGradientEnd => _palette.Hover;

    public override Color ImageMarginGradientBegin => _palette.ControlBackground;

    public override Color ImageMarginGradientMiddle => _palette.ControlBackground;

    public override Color ImageMarginGradientEnd => _palette.ControlBackground;

    public override Color SeparatorDark => _palette.Border;

    public override Color SeparatorLight => _palette.Border;

    public override Color CheckBackground => _palette.Selection;

    public override Color CheckSelectedBackground => _palette.Selection;
}

/// <summary>
/// Dark renderer for the tray context menu. WinForms menus do not follow the
/// system dark theme on their own, so both the background and the text colour have
/// to be drawn by hand.
/// </summary>
internal sealed class DarkContextMenuRenderer : ToolStripProfessionalRenderer
{
    private readonly ThemePalette _palette;

    public DarkContextMenuRenderer(ThemePalette palette)
        : base(new DarkColorTable(palette))
    {
        _palette = palette;
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using (SolidBrush brush = new SolidBrush(_palette.ControlBackground))
        {
            e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.ToolStrip.Size));
        }
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using (SolidBrush brush = new SolidBrush(_palette.ControlBackground))
        {
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item is { Enabled: true } ? _palette.Text : _palette.SecondaryText;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = _palette.Text;
        base.OnRenderArrow(e);
    }

    /// <summary>
    /// Draws the check mark of a checked menu item. The base renderer blits a bitmap
    /// from the framework resources, which is a dark glyph meant for a light menu and
    /// all but invisible here; this is the same tick
    /// <see cref="BingWallpaper.UI.ThemedCheckBox"/> paints, so the two agree.
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

        using (SolidBrush fill = new SolidBrush(_palette.Accent))
        {
            g.FillRectangle(fill, bounds);
        }

        using (Pen tick = new Pen(_palette.GlyphMark, Math.Max(1, DpiScale.Round(2))))
        {
            tick.StartCap = LineCap.Round;
            tick.EndCap = LineCap.Round;
            g.DrawLines(tick, new[]
            {
                new PointF(bounds.Left + (bounds.Width * 0.22f), bounds.Top + (bounds.Height * 0.52f)),
                new PointF(bounds.Left + (bounds.Width * 0.44f), bounds.Top + (bounds.Height * 0.68f)),
                new PointF(bounds.Left + (bounds.Width * 0.78f), bounds.Top + (bounds.Height * 0.30f)),
            });
        }

        g.SmoothingMode = previous;
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using (SolidBrush background = new SolidBrush(_palette.ControlBackground))
        {
            e.Graphics.FillRectangle(background, new Rectangle(Point.Empty, e.Item.Size));
        }

        using (Pen pen = new Pen(_palette.Border))
        {
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 4, y, Math.Max(4, e.Item.Width - 4), y);
        }
    }
}
