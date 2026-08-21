using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using BingWallpaper.Theme;

namespace BingWallpaper.UI;

/// <summary>
/// Draws a <see cref="FluentMenuStrip"/> as a WinUI 3 menu flyout: a rounded
/// window with a hairline frame, entries that light up inside their own rounded
/// margin, and a check mark from the Fluent icon font in the column in front of the
/// text.
///
/// <para>
/// It derives from <see cref="ToolStripRenderer"/> rather than from the
/// professional renderer, because there is nothing of the latter left to keep: a
/// ProfessionalColorTable can only re-colour a Win32 menu, and a Win32 menu is
/// square, full width and drawn with a gradient. Everything below is painted from
/// the palette instead, which also means no colour is ever written onto an item -
/// the disabled ones are grey because they are drawn grey, not because something
/// set their ForeColor while they happened to be disabled.
/// </para>
/// </summary>
internal sealed class FluentMenuRenderer : ToolStripRenderer
{
    /// <summary>
    /// Segoe Fluent Icons / Segoe MDL2 Assets, U+E73E "CheckMark" - the glyph the
    /// WinUI ToggleMenuFlyoutItem template shows when an entry is checked. Written
    /// as an escape because the character itself is in a private use area and shows
    /// up as an empty box in every editor that has no icon font.
    /// </summary>
    private const string CheckMarkGlyph = "\uE73E";

    private static Font? _glyphFont;
    private static bool _glyphFontResolved;

    /// <summary>A rectangle with rounded corners, for a fill or a one pixel frame.</summary>
    public static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        GraphicsPath path = new GraphicsPath();
        int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        Rectangle corner = new Rectangle(bounds.Location, new Size(diameter, diameter));
        path.AddArc(corner, 180, 90);
        corner.X = bounds.Right - diameter;
        path.AddArc(corner, 270, 90);
        corner.Y = bounds.Bottom - diameter;
        path.AddArc(corner, 0, 90);
        corner.X = bounds.Left;
        path.AddArc(corner, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using SolidBrush brush = new SolidBrush(ThemeManager.Palette.MenuBackground);
        e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.ToolStrip.Size));
    }

    /// <summary>
    /// The hairline around the flyout. The corners it is drawn into are the ones
    /// the window itself is clipped to - by DWM on Windows 11, by a window region
    /// below that - so the frame follows the same radius rather than defining it.
    /// </summary>
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        Rectangle bounds = new Rectangle(Point.Empty, e.ToolStrip.Size);
        bounds.Width -= 1;
        bounds.Height -= 1;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        Graphics graphics = e.Graphics;
        SmoothingMode previous = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using (GraphicsPath path = RoundedRectangle(bounds, DpiScale.Round(FluentMenuMetrics.MenuCornerRadius)))
        using (Pen pen = new Pen(ThemeManager.Palette.MenuBorder))
        {
            graphics.DrawPath(pen, path);
        }

        graphics.SmoothingMode = previous;
    }

    /// <summary>
    /// The highlight and the check mark. Both are drawn here because this is the
    /// one hook a menu item always reaches: DrawItemCheck is only called when the
    /// drop down shows a check or image margin, and this menu shows neither - the
    /// check column belongs to the item layout instead.
    /// </summary>
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        ThemePalette palette = ThemeManager.Palette;
        Graphics graphics = e.Graphics;
        ToolStripItem item = e.Item;
        Rectangle highlight = FluentMenuMetrics.HighlightBounds(item.Size);

        if (item.Enabled && (item.Selected || item.Pressed))
        {
            SmoothingMode previous = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            using (GraphicsPath path = RoundedRectangle(highlight, DpiScale.Round(FluentMenuMetrics.ItemCornerRadius)))
            using (SolidBrush brush = new SolidBrush(item.Pressed ? palette.MenuItemPressed : palette.MenuItemHot))
            {
                graphics.FillPath(brush, path);
            }

            graphics.SmoothingMode = previous;
        }

        if (item is ToolStripMenuItem { Checked: true })
        {
            DrawCheckMark(
                graphics,
                FluentMenuMetrics.CheckBounds(highlight),
                item.Enabled ? palette.MenuTextSecondary : palette.MenuTextDisabled);
        }
    }

    /// <summary>
    /// The caption. Neither the rectangle nor the colour the item arrives with is
    /// used: the rectangle is the one the drop down menu layout computed for its own
    /// row, and the base renderer would replace the colour of a disabled entry with
    /// SystemColors.GrayText, which is a light grey in the dark theme.
    /// </summary>
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        ThemePalette palette = ThemeManager.Palette;
        Rectangle highlight = FluentMenuMetrics.HighlightBounds(e.Item.Size);
        TextRenderer.DrawText(
            e.Graphics,
            e.Text,
            e.TextFont,
            FluentMenuMetrics.TextBounds(highlight),
            e.Item.Enabled ? palette.MenuText : palette.MenuTextDisabled,
            FluentMenuMetrics.TextFlags | TextFormatFlags.EndEllipsis);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using SolidBrush brush = new SolidBrush(ThemeManager.Palette.MenuDivider);
        e.Graphics.FillRectangle(brush, FluentMenuMetrics.SeparatorBounds(e.Item.Size));
    }

    private static void DrawCheckMark(Graphics graphics, Rectangle bounds, Color colour)
    {
        Font? font = GlyphFont();
        if (font is not null)
        {
            TextRenderer.DrawText(
                graphics,
                CheckMarkGlyph,
                font,
                bounds,
                colour,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            return;
        }

        // No icon font on this machine: the same tick, drawn by hand.
        SmoothingMode previous = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using (Pen pen = new Pen(colour, Math.Max(1f, bounds.Width / 10f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        })
        {
            graphics.DrawLines(pen, new[]
            {
                new PointF(bounds.Left + (bounds.Width * 0.14f), bounds.Top + (bounds.Height * 0.52f)),
                new PointF(bounds.Left + (bounds.Width * 0.40f), bounds.Top + (bounds.Height * 0.76f)),
                new PointF(bounds.Left + (bounds.Width * 0.86f), bounds.Top + (bounds.Height * 0.24f)),
            });
        }

        graphics.SmoothingMode = previous;
    }

    /// <summary>
    /// The icon font WinUI draws its check mark from, at the size WinUI asks for.
    /// Segoe Fluent Icons is the Windows 11 one and Segoe MDL2 Assets the Windows 10
    /// one; a FontFamily that is not installed throws, which is how they are told
    /// apart - constructing a Font would silently substitute another family instead.
    /// </summary>
    private static Font? GlyphFont()
    {
        if (_glyphFontResolved)
        {
            return _glyphFont;
        }

        _glyphFontResolved = true;
        foreach (string family in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            try
            {
                using (FontFamily probe = new FontFamily(family))
                {
                    _glyphFont = new Font(
                        probe,
                        FluentMenuMetrics.CheckGlyph * DpiScale.Scale,
                        GraphicsUnit.Pixel);
                }

                return _glyphFont;
            }
            catch (ArgumentException)
            {
                // Not installed - try the next one.
            }
            catch (Exception ex)
            {
                Logger.Debug("Could not load the icon font " + family + ": " + ex.Message);
            }
        }

        Logger.Debug("No Fluent icon font is installed; check marks are drawn by hand.");
        return null;
    }
}
