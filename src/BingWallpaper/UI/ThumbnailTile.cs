using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using BingWallpaper.Theme;

namespace BingWallpaper.UI;

/// <summary>
/// One entry of the history grid: a thumbnail with the date and the title under it.
///
/// The grid used to be a ListView in LargeIcon view, which decided too much on its
/// own: the selection highlight covers the label and never the picture, its colours
/// come from the system rather than the palette, and the spacing between cells is
/// only reachable through LVM_SETICONSPACING. Everything here is drawn instead.
/// </summary>
internal sealed class ThumbnailTile : Control
{
    /// <summary>Logical pixels. The picture is 16:9, the rest holds the two lines.</summary>
    public const int TileWidth = 200;

    public const int TileHeight = 160;

    public const int TileMargin = 8;

    private Image? _thumbnail;
    private bool _hovered;
    private bool _isCurrent;
    private bool _isPinned;

    public ThumbnailTile(int index, BingImageInfo info)
    {
        Index = index;
        Info = info;

        // Scaled here rather than by AutoScaleMode.Dpi: the tiles are created when
        // the metadata arrives, which is long after the form ran its scaling pass.
        Size = new Size(DpiScale.Round(TileWidth), DpiScale.Round(TileHeight));
        Margin = new Padding(DpiScale.Round(TileMargin));
        TabStop = true;
        Cursor = Cursors.Hand;
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable,
            true);
    }

    /// <summary>Position in the metadata list - the index the tray context applies by.</summary>
    public int Index { get; }

    public BingImageInfo Info { get; }

    /// <summary>
    /// The tile owns the bitmap and disposes it.
    /// </summary>
    /// <remarks>
    /// The DesignerSerializationVisibility attributes on this and the two properties
    /// below are what the WFO1000 analyser asks for. Nothing here is ever serialised -
    /// there is no designer in this project, every control is built in code - so
    /// Hidden is both the honest answer and the one that keeps the build warning free.
    /// </remarks>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Image? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value))
            {
                return;
            }

            _thumbnail?.Dispose();
            _thumbnail = value;
            Invalidate();
        }
    }

    /// <summary>Whether this entry is the wallpaper currently on the desktop.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value)
            {
                return;
            }

            _isCurrent = value;
            Invalidate();
        }
    }

    /// <summary>
    /// Whether this entry is the pinned wallpaper. Only ever set on the tile that is
    /// current as well - the pin always follows the wallpaper on the desktop - so it
    /// relabels the badge that is already there instead of adding a second one.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned == value)
            {
                return;
            }

            _isPinned = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        ThemePalette palette = ThemeManager.Palette;
        Graphics g = e.Graphics;
        g.Clear(BackColor);

        // The picture box is 16:9 like the wallpaper itself; the two lines follow it
        // and whatever is left over stays as padding at the bottom of the tile.
        int lineHeight = Font.Height;
        Rectangle picture = new(0, 0, Width, Width * 9 / 16);
        int textTop = picture.Bottom + DpiScale.Round(8);

        PaintPicture(g, palette, picture);
        PaintFrame(g, palette, picture);

        if (_isCurrent)
        {
            PaintBadge(g, palette, picture);
        }

        TextRenderer.DrawText(
            g,
            Info.DisplayDate,
            Font,
            new Rectangle(0, textTop, Width, lineHeight),
            palette.SecondaryText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        TextRenderer.DrawText(
            g,
            Info.DisplayTitle,
            Font,
            new Rectangle(0, textTop + lineHeight, Width, lineHeight),
            palette.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        // ShowFocusCues, not Focused alone: Windows hides focus rectangles until the
        // user actually navigates by keyboard, and the tile that happens to hold the
        // focus when the window opens should not be ringed.
        if (Focused && ShowFocusCues)
        {
            using Pen focus = new(palette.Accent) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(focus, 0, 0, Width - 1, Height - 1);
        }
    }

    private void PaintPicture(Graphics g, ThemePalette palette, Rectangle picture)
    {
        if (_thumbnail is null)
        {
            using (SolidBrush placeholder = new(palette.ControlBackground))
            {
                g.FillRectangle(placeholder, picture);
            }

            TextRenderer.DrawText(
                g,
                "载入中…",
                Font,
                picture,
                palette.SecondaryText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return;
        }

        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(_thumbnail, picture, GetSourceRectangle(_thumbnail, picture), GraphicsUnit.Pixel);
    }

    private void PaintFrame(Graphics g, ThemePalette palette, Rectangle picture)
    {
        // No anti aliasing on purpose - see ThemedComboBox for what it does to the
        // corner pixels of a one pixel rectangle.
        Color colour = _isCurrent || _hovered ? palette.Accent : palette.Border;
        int width = _isCurrent ? Math.Max(2, DpiScale.Round(2)) : Math.Max(1, DpiScale.Round(1));
        int inset = width / 2;

        using Pen pen = new(colour, width);
        g.DrawRectangle(
            pen,
            picture.Left + inset,
            picture.Top + inset,
            picture.Width - width,
            picture.Height - width);
    }

    private void PaintBadge(Graphics g, ThemePalette palette, Rectangle picture)
    {
        string caption = _isPinned ? "已固定" : "当前";
        Size text = TextRenderer.MeasureText(caption, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
        int padX = DpiScale.Round(6);
        int padY = DpiScale.Round(3);
        Rectangle badge = new(
            picture.Right - text.Width - (padX * 2) - DpiScale.Round(4),
            picture.Top + DpiScale.Round(4),
            text.Width + (padX * 2),
            text.Height + (padY * 2));

        using (SolidBrush fill = new(palette.Accent))
        {
            g.FillRectangle(fill, badge);
        }

        TextRenderer.DrawText(
            g,
            caption,
            Font,
            badge,
            palette.GlyphMark,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    /// <summary>
    /// The part of the source to show so that it fills the box without distortion.
    /// Bing serves the thumbnail as 400x240 (5:3) while the box is 16:9, so a strip
    /// at the top and the bottom is cropped rather than the picture being squashed.
    /// </summary>
    private static Rectangle GetSourceRectangle(Image image, Rectangle target)
    {
        if (image.Width <= 0 || image.Height <= 0 || target.Height <= 0)
        {
            return new Rectangle(0, 0, Math.Max(1, image.Width), Math.Max(1, image.Height));
        }

        double targetAspect = (double)target.Width / target.Height;
        double sourceAspect = (double)image.Width / image.Height;

        if (sourceAspect > targetAspect)
        {
            int width = (int)Math.Round(image.Height * targetAspect);
            return new Rectangle((image.Width - width) / 2, 0, width, image.Height);
        }

        int height = (int)Math.Round(image.Width / targetAspect);
        return new Rectangle(0, (image.Height - height) / 2, image.Width, height);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        base.OnMouseDown(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
        {
            e.Handled = true;
            OnClick(EventArgs.Empty);
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _thumbnail?.Dispose();
            _thumbnail = null;
        }

        base.Dispose(disposing);
    }
}
