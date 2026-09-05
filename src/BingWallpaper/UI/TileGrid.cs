using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using BingWallpaper.Theme;

namespace BingWallpaper.UI;

/// <summary>What a picture is to the desktop right now.</summary>
internal enum TileMark
{
    /// <summary>Not the wallpaper on screen.</summary>
    None,

    /// <summary>On screen, and the rotation is free to replace it.</summary>
    Applied,

    /// <summary>On screen and held there - the rotation will not touch it.</summary>
    Pinned,
}

/// <summary>Everything the grid needs in order to paint one tile.</summary>
internal readonly struct TileInfo
{
    public TileInfo(string date, string title, TileMark mark, bool starred, Image? thumbnail, bool thumbnailFailed)
    {
        Date = date;
        Title = title;
        Mark = mark;
        Starred = starred;
        Thumbnail = thumbnail;
        ThumbnailFailed = thumbnailFailed;
    }

    public string Date { get; }

    public string Title { get; }

    /// <summary>State of the picture. Rightmost mark in the top right corner.</summary>
    public TileMark Mark { get; }

    /// <summary>
    /// Whether to mark the tile as favourited. Left of the state mark, or in its slot
    /// when the picture carries none.
    /// </summary>
    public bool Starred { get; }

    /// <summary>The bitmap to paint, or null while there is none yet.</summary>
    public Image? Thumbnail { get; }

    /// <summary>Whether the thumbnail is never going to arrive, as opposed to not yet.</summary>
    public bool ThumbnailFailed { get; }
}

/// <summary>
/// What a <see cref="TileGrid"/> paints. One grid, two implementations: the last
/// eight days come off the network, the favourites off the disk cache.
/// </summary>
internal interface ITileSource
{
    /// <summary>Raised on the UI thread when the tile at that index has changed.</summary>
    event Action<int>? TileChanged;

    int Count { get; }

    /// <summary>Shown in the middle of the grid while <see cref="Count"/> is zero.</summary>
    string EmptyText { get; }

    TileInfo GetInfo(int index);

    /// <summary>
    /// Declares the range worth holding bitmaps for - the visible tiles plus a screen
    /// on either side. Called on every scroll and resize.
    /// </summary>
    void SetWindow(int first, int count);
}

internal sealed class TileEventArgs : EventArgs
{
    public TileEventArgs(int index, Point location)
    {
        Index = index;
        Location = location;
    }

    public int Index { get; }

    /// <summary>Where it happened, in client coordinates.</summary>
    public Point Location { get; }
}

/// <summary>
/// A virtualized thumbnail grid: one control, one bitmap per visible tile.
///
/// <para>
/// The predecessor gave every entry a control of its own inside a FlowLayoutPanel,
/// which is fine for the eight tiles of the recent tab and falls over in the
/// favourites: a couple of thousand entries would be a couple of thousand window
/// handles to create and lay out before the window could be shown - every time it is
/// opened, cached thumbnails or not - each with its own message queue and its own
/// permanently held decoded bitmap.
/// </para>
/// <para>
/// So the entries are data, the painting happens here for the rows that are on screen,
/// and the bitmaps are held by the source for the visible range only. What it costs is
/// hit testing, hover and keyboard navigation written out by hand; what it buys is a
/// window that opens in the same time whether it holds eight pictures or five thousand.
/// </para>
/// </summary>
internal sealed class TileGrid : ScrollableControl
{
    /// <summary>Logical pixels. The picture is 16:9, the rest holds the two lines.</summary>
    public const int TileWidth = 200;

    public const int TileHeight = 160;

    public const int TileMargin = 8;

    public const int GridPadding = 8;

    private ITileSource? _source;
    private int _columns = 1;
    private int _originX;
    private int _hovered = -1;
    private int _focused = -1;
    private int _windowFirst = -1;
    private int _windowCount;

    public TileGrid()
    {
        AutoScroll = true;
        TabStop = true;
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable,
            true);
    }

    /// <summary>A tile was clicked, or activated with Enter or Space.</summary>
    public event EventHandler<TileEventArgs>? ItemActivated;

    /// <summary>A tile was right clicked, or asked for a menu from the keyboard.</summary>
    public event EventHandler<TileEventArgs>? ItemMenuRequested;

    /// <summary>The tile under the mouse changed; read <see cref="HoveredIndex"/>.</summary>
    public event EventHandler? HoveredIndexChanged;

    /// <summary>Index under the mouse, or -1.</summary>
    public int HoveredIndex => _hovered;

    /// <summary>Index the keyboard is on, or -1.</summary>
    public int FocusedIndex => _focused;

    /// <summary>Device pixel size of one cell, tile plus its margins.</summary>
    public static int CellWidth => DpiScale.Round(TileWidth) + (DpiScale.Round(TileMargin) * 2);

    public static int CellHeight => DpiScale.Round(TileHeight) + (DpiScale.Round(TileMargin) * 2);

    public static int EdgePadding => DpiScale.Round(GridPadding);

    /// <summary>
    /// Swaps the data behind the grid. The scroll position and the focus belong to the
    /// list that was showing, so both go back to the top.
    /// </summary>
    public void SetSource(ITileSource? source)
    {
        if (ReferenceEquals(_source, source))
        {
            return;
        }

        if (_source is not null)
        {
            _source.TileChanged -= OnTileChanged;
        }

        _source = source;
        if (_source is not null)
        {
            _source.TileChanged += OnTileChanged;
        }

        _hovered = -1;
        _focused = source is { Count: > 0 } ? 0 : -1;
        _windowFirst = -1;
        _windowCount = 0;
        AutoScrollPosition = new Point(0, 0);
        Relayout();

        // The first window has to be declared here: without a scroll or a resize to
        // follow, nothing else would ever ask the source to load anything.
        UpdateWindow();
        Invalidate();
    }

    /// <summary>Re-reads the source without moving the view - the F5 path.</summary>
    public void Reload(bool keepPosition)
    {
        int offset = keepPosition ? -AutoScrollPosition.Y : 0;
        _windowFirst = -1;

        // The list may have got shorter - un-favouriting is one of the things that
        // brings us here - and the focus has to land somewhere that still exists.
        int count = _source?.Count ?? 0;
        _hovered = -1;
        _focused = Math.Min(_focused, count - 1);

        Relayout();
        AutoScrollPosition = new Point(0, offset);
        UpdateWindow();
        Invalidate();
    }

    /// <summary>
    /// Scrolls by a wheel notch. Public because the form forwards the wheel here: the
    /// message goes to whichever control has the focus, which is the segmented control
    /// right after the window opens.
    /// </summary>
    public void ScrollByWheel(int delta)
    {
        if (delta == 0)
        {
            return;
        }

        int lines = SystemInformation.MouseWheelScrollLines;
        int step = lines < 0
            ? Math.Max(1, ClientSize.Height - CellHeight) // "one screen at a time"
            // A row counts as three lines, so the default setting of three moves exactly
            // one row. Multiplied before the division on purpose: dividing first drops
            // the remainder of a cell height that is not a multiple of three, which at
            // 96 DPI is two pixels a notch and drifts the grid off the row it started on.
            : Math.Max(1, lines * CellHeight / 3);

        ScrollBy(-delta * step / 120);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // The scroll bar is drawn by the system, not by us: it is non client, so the
        // palette cannot reach it and only the theme name can.
        ApplyScrollBarTheme();

        // Removed first: a handle can be recreated, and the second subscription would
        // then apply the theme twice for the rest of the window's life.
        ThemeManager.ThemeChanged -= OnThemeChanged;
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            if (_source is not null)
            {
                _source.TileChanged -= OnTileChanged;
                _source = null;
            }
        }

        base.Dispose(disposing);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyScrollBarTheme();

    private void ApplyScrollBarTheme()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        bool dark = ThemeManager.Palette.IsDark;
        DarkModeNative.AllowDarkModeForHandle(Handle, dark);
        DarkModeNative.ApplyWindowTheme(Handle, dark ? "DarkMode_Explorer" : "Explorer");
    }

    private void OnTileChanged(int index)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        Rectangle bounds = GetTileBounds(index);
        bounds.Offset(AutoScrollPosition);
        bounds.Inflate(1, 1);
        Invalidate(bounds);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout();
        UpdateWindow();
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        UpdateWindow();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // Deliberately not calling base: ScrollableControl scrolls on the wheel too,
        // and the two would compound into a jump of two notches per notch.
        ScrollByWheel(e.Delta);

        // An unhandled wheel goes to DefWindowProc, which hands it to the parent - and
        // the parent hands it straight back here, so a notch would scroll twice.
        if (e is HandledMouseEventArgs handled)
        {
            handled.Handled = true;
        }
    }

    /// <summary>
    /// Columns, content height and the scroll range, from the size the control has now.
    ///
    /// The scroll bar is decided before the columns rather than discovered afterwards:
    /// deriving the width from ClientSize instead would let a bar that appears take a
    /// column away, which makes the content taller, which keeps the bar - a layout that
    /// can flip between two answers forever on a border case.
    /// </summary>
    private void Relayout()
    {
        int count = _source?.Count ?? 0;
        int cellWidth = CellWidth;
        int cellHeight = CellHeight;
        int padding = EdgePadding;

        int available = Width;
        int columns = ColumnsFor(available, cellWidth, padding);
        int rows = RowsFor(count, columns);
        int height = (rows * cellHeight) + (padding * 2);

        if (height > Height)
        {
            available = Width - SystemInformation.VerticalScrollBarWidth;
            columns = ColumnsFor(available, cellWidth, padding);
            rows = RowsFor(count, columns);
            height = (rows * cellHeight) + (padding * 2);
        }

        _columns = columns;

        // Centred rather than left aligned: whatever a whole number of columns leaves
        // over is split between the two edges instead of piling up on the right. It is
        // measured against the width decided above, not against ClientSize, so it does
        // not depend on the scroll bar having appeared yet.
        _originX = Math.Max(padding, (available - (columns * cellWidth)) / 2);

        Size wanted = new Size(0, height);
        if (AutoScrollMinSize != wanted)
        {
            AutoScrollMinSize = wanted;
        }
    }

    private static int ColumnsFor(int width, int cellWidth, int padding)
        => Math.Max(1, (width - (padding * 2)) / cellWidth);

    private static int RowsFor(int count, int columns) => count <= 0 ? 0 : ((count - 1) / columns) + 1;

    /// <summary>Bounds of a tile in content coordinates (before the scroll offset).</summary>
    private Rectangle GetTileBounds(int index)
    {
        int column = index % _columns;
        int row = index / _columns;
        int margin = DpiScale.Round(TileMargin);
        return new Rectangle(
            _originX + (column * CellWidth) + margin,
            EdgePadding + (row * CellHeight) + margin,
            DpiScale.Round(TileWidth),
            DpiScale.Round(TileHeight));
    }

    /// <summary>The first and last index that can be on screen at the current offset.</summary>
    private void GetVisibleRange(out int first, out int last)
    {
        int count = _source?.Count ?? 0;
        if (count == 0)
        {
            first = 0;
            last = -1;
            return;
        }

        int offset = -AutoScrollPosition.Y;
        int cellHeight = CellHeight;
        int firstRow = Math.Max(0, (offset - EdgePadding) / cellHeight);
        int lastRow = Math.Max(0, (offset + ClientSize.Height - EdgePadding) / cellHeight);

        first = Math.Min(count - 1, firstRow * _columns);
        last = Math.Min(count - 1, ((lastRow + 1) * _columns) - 1);
    }

    /// <summary>
    /// Tells the source which tiles to hold bitmaps for: what is visible plus a screen
    /// above and below, so a slow scroll paints from memory instead of from the queue.
    /// </summary>
    private void UpdateWindow()
    {
        ITileSource? source = _source;
        if (source is null)
        {
            return;
        }

        GetVisibleRange(out int first, out int last);
        if (last < first)
        {
            return;
        }

        int screen = Math.Max(_columns, ((ClientSize.Height / CellHeight) + 1) * _columns);
        int from = Math.Max(0, first - screen);
        int to = Math.Min(source.Count - 1, last + screen);
        int count = to - from + 1;

        if (from == _windowFirst && count == _windowCount)
        {
            return;
        }

        _windowFirst = from;
        _windowCount = count;
        source.SetWindow(from, count);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        ThemePalette palette = ThemeManager.Palette;
        Graphics g = e.Graphics;
        ITileSource? source = _source;

        if (source is null || source.Count == 0)
        {
            TextRenderer.DrawText(
                g,
                source?.EmptyText ?? string.Empty,
                Font,
                ClientRectangle,
                palette.SecondaryText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }

        // Offset per tile rather than by a world transform on the Graphics: half of
        // what a tile is made of is drawn through TextRenderer, which is GDI and does
        // not look at the GDI+ transform unless every call opts in - so a scrolled
        // grid would paint its pictures in one place and its captions in another.
        Point origin = AutoScrollPosition;

        GetVisibleRange(out int first, out int last);
        for (int i = first; i <= last; i++)
        {
            Rectangle bounds = GetTileBounds(i);
            bounds.Offset(origin);
            PaintTile(g, palette, i, bounds, source.GetInfo(i));
        }
    }

    private void PaintTile(Graphics g, ThemePalette palette, int index, Rectangle bounds, TileInfo info)
    {
        int lineHeight = Font.Height;
        Rectangle picture = new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Width * 9 / 16);
        int textTop = picture.Bottom + DpiScale.Round(8);

        PaintPicture(g, palette, picture, info);
        PaintFrame(g, palette, picture, index, info);

        PaintMarks(g, palette, picture, info);

        TextRenderer.DrawText(
            g,
            info.Date,
            Font,
            new Rectangle(bounds.X, textTop, bounds.Width, lineHeight),
            palette.SecondaryText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        TextRenderer.DrawText(
            g,
            info.Title,
            Font,
            new Rectangle(bounds.X, textTop + lineHeight, bounds.Width, lineHeight),
            palette.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        // ShowFocusCues, not Focused alone: Windows hides focus rectangles until the
        // user actually navigates by keyboard, and the tile that happens to hold the
        // focus when the window opens should not be ringed.
        if (index == _focused && Focused && ShowFocusCues)
        {
            using Pen focus = new Pen(palette.Accent) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(focus, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        }
    }

    private void PaintPicture(Graphics g, ThemePalette palette, Rectangle picture, TileInfo info)
    {
        if (info.Thumbnail is null)
        {
            using (SolidBrush placeholder = new SolidBrush(palette.ControlBackground))
            {
                g.FillRectangle(placeholder, picture);
            }

            TextRenderer.DrawText(
                g,
                info.ThumbnailFailed ? "无法显示" : "载入中…",
                Font,
                picture,
                palette.SecondaryText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return;
        }

        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(info.Thumbnail, picture, GetSourceRectangle(info.Thumbnail, picture), GraphicsUnit.Pixel);

        // Handed straight back: the modes stay on the Graphics, and at HighQuality
        // every later coordinate shifts half a pixel towards the origin - which is how
        // one pixel frames end up drawing two of their four sides.
        g.PixelOffsetMode = PixelOffsetMode.Default;
        g.InterpolationMode = InterpolationMode.Default;
    }

    private void PaintFrame(Graphics g, ThemePalette palette, Rectangle picture, int index, TileInfo info)
    {
        // No anti aliasing on purpose - see ThemedComboBox for what it does to the
        // corner pixels of a one pixel rectangle. Hover and "current" share one
        // emphasised frame; the corner mark is what keeps the two states apart.
        bool emphasised = index == _hovered || info.Mark != TileMark.None;
        Color colour = emphasised ? palette.Accent : palette.Border;
        int width = emphasised ? Math.Max(2, DpiScale.Round(2)) : Math.Max(1, DpiScale.Round(1));
        int inset = width / 2;

        using Pen pen = new Pen(colour, width);
        g.DrawRectangle(
            pen,
            picture.Left + inset,
            picture.Top + inset,
            picture.Width - width,
            picture.Height - width);
    }

    /// <summary>
    /// The corner marks: top right, read right to left as the strength drops off -
    /// what the picture is to the desktop right now, then whether it is a favourite.
    /// Both are 20px discs on purpose. A text badge is as wide as whatever it says, so
    /// anything sitting beside one would land on a different x on every tile; equal
    /// discs make the corner a predictable row.
    /// </summary>
    private static void PaintMarks(Graphics g, ThemePalette palette, Rectangle picture, TileInfo info)
    {
        if (info.Mark == TileMark.None && !info.Starred)
        {
            return;
        }

        int size = DpiScale.Round(20);
        int gap = DpiScale.Round(4);
        int inset = DpiScale.Round(4);
        int x = picture.Right - inset - size;
        int y = picture.Top + inset;

        SmoothingMode previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (SolidBrush disc = new SolidBrush(palette.Accent))
        using (SolidBrush mark = new SolidBrush(palette.GlyphMark))
        {
            if (info.Mark != TileMark.None)
            {
                Rectangle bounds = new Rectangle(x, y, size, size);
                g.FillEllipse(disc, bounds);

                if (info.Mark == TileMark.Pinned)
                {
                    PaintPadlock(g, mark, palette.GlyphMark, bounds);
                }
                else
                {
                    PaintDot(g, mark, bounds);
                }

                x -= size + gap;
            }

            if (info.Starred)
            {
                Rectangle bounds = new Rectangle(x, y, size, size);
                g.FillEllipse(disc, bounds);
                g.FillPolygon(mark, BuildStar(bounds), FillMode.Winding);
            }
        }

        g.SmoothingMode = previous;
    }

    /// <summary>
    /// The "locked" glyph: a padlock, not a pin. Every string in the UI says 锁定 /
    /// 已锁定, and the Chinese for a pin is 固定 - the picture has to match the word.
    /// Drawn by hand rather than shipped as an icon: a shackle arc over a rounded body,
    /// laid out on a 20 unit grid so it scales with whatever the DPI made of the disc.
    /// </summary>
    private static void PaintPadlock(Graphics g, Brush mark, Color colour, Rectangle bounds)
    {
        float unit = bounds.Width / 20f;
        float centreX = bounds.X + (bounds.Width / 2f);
        float bodyWidth = 10f * unit;
        float bodyHeight = 7f * unit;
        float radius = 2f * unit;

        // 8 rather than 9, which is where the whole glyph would be centred on the 20
        // unit box: the body is solid and the shackle is a thin stroke, so a padlock
        // measured to the middle reads as sitting low. Optical centre over geometric.
        float bodyTop = bounds.Y + (8f * unit);

        using (GraphicsPath body = new GraphicsPath())
        {
            RectangleF box = new RectangleF(centreX - (bodyWidth / 2f), bodyTop, bodyWidth, bodyHeight);
            body.AddArc(box.Left, box.Top, radius, radius, 180, 90);
            body.AddArc(box.Right - radius, box.Top, radius, radius, 270, 90);
            body.AddArc(box.Right - radius, box.Bottom - radius, radius, radius, 0, 90);
            body.AddArc(box.Left, box.Bottom - radius, radius, radius, 90, 90);
            body.CloseFigure();
            g.FillPath(mark, body);
        }

        // The arc ends exactly on the top edge of the body, so the two shapes meet
        // without a seam and the shackle needs no separate legs.
        float shackle = 3.2f * unit;
        using Pen pen = new Pen(colour, Math.Max(1f, 1.8f * unit))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        g.DrawArc(pen, centreX - shackle, bodyTop - shackle, shackle * 2f, shackle * 2f, 180, 180);
    }

    /// <summary>
    /// The "on screen right now" glyph: a solid dot. The quietest of the three marks on
    /// purpose - it says where the wallpaper is, not that anything is being held.
    /// </summary>
    private static void PaintDot(Graphics g, Brush mark, Rectangle bounds)
    {
        float size = bounds.Width * 0.4f;
        g.FillEllipse(
            mark,
            bounds.X + ((bounds.Width - size) / 2f),
            bounds.Y + ((bounds.Height - size) / 2f),
            size,
            size);
    }

    /// <summary>Ten points of a five pointed star inscribed in <paramref name="bounds"/>.</summary>
    private static PointF[] BuildStar(Rectangle bounds)
    {
        float centreX = bounds.X + (bounds.Width / 2f);
        float centreY = bounds.Y + (bounds.Height / 2f);
        float outer = bounds.Width * 0.32f;
        float inner = outer * 0.42f;

        PointF[] points = new PointF[10];
        for (int i = 0; i < 10; i++)
        {
            // Starting at -90 degrees puts a point at the top; every other vertex is
            // the inner radius, which is what makes the arms.
            double angle = (-Math.PI / 2) + (i * Math.PI / 5);
            float radius = (i % 2 == 0) ? outer : inner;
            points[i] = new PointF(
                centreX + (float)(Math.Cos(angle) * radius),
                centreY + (float)(Math.Sin(angle) * radius));
        }

        return points;
    }

    /// <summary>
    /// The part of the source to show so that it fills the box without distortion:
    /// the thumbnails are not 16:9 (Bing serves 400x240, a portrait photo is anything),
    /// so a strip is cropped rather than the picture squashed.
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

    /// <summary>Index at a client point, or -1 for the gaps between the tiles.</summary>
    public int HitTest(Point client)
    {
        int count = _source?.Count ?? 0;
        if (count == 0)
        {
            return -1;
        }

        Point content = new Point(client.X - AutoScrollPosition.X, client.Y - AutoScrollPosition.Y);
        int column = (content.X - _originX) / CellWidth;
        int row = (content.Y - EdgePadding) / CellHeight;
        if (content.X < _originX || content.Y < EdgePadding || column < 0 || column >= _columns || row < 0)
        {
            return -1;
        }

        int index = (row * _columns) + column;
        if (index < 0 || index >= count)
        {
            return -1;
        }

        // Inside the tile itself, not in the margin around it.
        return GetTileBounds(index).Contains(content) ? index : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHovered(HitTest(e.Location));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHovered(-1);
    }

    private void SetHovered(int index)
    {
        if (_hovered == index)
        {
            return;
        }

        InvalidateTile(_hovered);
        _hovered = index;
        InvalidateTile(_hovered);
        Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
        HoveredIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    private void InvalidateTile(int index)
    {
        if (index >= 0)
        {
            OnTileChanged(index);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        int index = HitTest(e.Location);
        if (index >= 0)
        {
            SetFocusedIndex(index, scrollIntoView: false);
        }
    }

    /// <summary>
    /// The context menu opens on the release, not on the press: a popup shown while
    /// the button is still down swallows the release that follows it, and every native
    /// window on this system opens its menu the same way.
    /// </summary>
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            ItemMenuRequested?.Invoke(this, new TileEventArgs(HitTest(e.Location), e.Location));
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        int index = HitTest(e.Location);
        if (index >= 0)
        {
            ItemActivated?.Invoke(this, new TileEventArgs(index, e.Location));
        }
    }

    /// <summary>Without this the arrow keys are eaten by the dialog navigation.</summary>
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) switch
    {
        Keys.Left or Keys.Right or Keys.Up or Keys.Down => true,
        Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown => true,
        _ => base.IsInputKey(keyData),
    };

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int count = _source?.Count ?? 0;
        if (count == 0)
        {
            base.OnKeyDown(e);
            return;
        }

        int rowsPerScreen = Math.Max(1, ClientSize.Height / CellHeight);
        int current = _focused >= 0 ? _focused : 0;
        int target = e.KeyCode switch
        {
            Keys.Left => current - 1,
            Keys.Right => current + 1,
            Keys.Up => current - _columns,
            Keys.Down => current + _columns,
            Keys.PageUp => current - (_columns * rowsPerScreen),
            Keys.PageDown => current + (_columns * rowsPerScreen),
            Keys.Home => 0,
            Keys.End => count - 1,
            _ => current,
        };

        if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
        {
            e.Handled = true;
            if (_focused >= 0)
            {
                ItemActivated?.Invoke(this, new TileEventArgs(_focused, GetTileCentre(_focused)));
            }

            return;
        }

        if (e.KeyCode == Keys.Apps || (e.KeyCode == Keys.F10 && e.Shift))
        {
            e.Handled = true;
            if (_focused >= 0)
            {
                ItemMenuRequested?.Invoke(this, new TileEventArgs(_focused, GetTileCentre(_focused)));
            }

            return;
        }

        if (target != current)
        {
            e.Handled = true;
            SetFocusedIndex(Math.Max(0, Math.Min(count - 1, target)), scrollIntoView: true);
            return;
        }

        base.OnKeyDown(e);
    }

    private Point GetTileCentre(int index)
    {
        Rectangle bounds = GetTileBounds(index);
        bounds.Offset(AutoScrollPosition);
        return new Point(bounds.Left + (bounds.Width / 2), bounds.Top + (bounds.Height / 2));
    }

    private void SetFocusedIndex(int index, bool scrollIntoView)
    {
        if (_focused != index)
        {
            InvalidateTile(_focused);
            _focused = index;
            InvalidateTile(_focused);
        }

        if (scrollIntoView)
        {
            EnsureVisible(index);
        }
    }

    private void EnsureVisible(int index)
    {
        Rectangle bounds = GetTileBounds(index);
        int offset = -AutoScrollPosition.Y;
        int top = bounds.Top - DpiScale.Round(TileMargin) - EdgePadding;
        int bottom = bounds.Bottom + DpiScale.Round(TileMargin) + EdgePadding;

        if (top < offset)
        {
            ScrollTo(top);
        }
        else if (bottom > offset + ClientSize.Height)
        {
            ScrollTo(bottom - ClientSize.Height);
        }
    }

    private void ScrollBy(int delta) => ScrollTo(-AutoScrollPosition.Y + delta);

    private void ScrollTo(int offset)
    {
        int limit = Math.Max(0, AutoScrollMinSize.Height - ClientSize.Height);
        int clamped = Math.Max(0, Math.Min(limit, offset));
        if (clamped == -AutoScrollPosition.Y)
        {
            return;
        }

        // The setter takes a positive offset and stores its negation, which is what
        // AutoScrollPosition reads back as - the one asymmetry of this API.
        AutoScrollPosition = new Point(0, clamped);
        UpdateWindow();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateTile(_focused);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateTile(_focused);
    }
}
