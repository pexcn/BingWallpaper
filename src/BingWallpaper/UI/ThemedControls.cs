using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using BingWallpaper.Theme;

namespace BingWallpaper.UI;

/// <summary>
/// Owner drawn radio button.
///
/// The stock WinForms glyph is painted by the system theme against the control
/// background: on a dark background the result is a black circle with a black
/// dot, which is what the light/dark themed dialog looked like. Drawing the
/// glyph ourselves keeps the checked state accent blue in both themes.
/// </summary>
internal sealed class ThemedRadioButton : RadioButton
{
    private bool _hovered;

    public ThemedRadioButton(string text)
    {
        Text = text;
        FlatStyle = FlatStyle.Flat;
        AutoSize = true;
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
    }

    private int GlyphSize => LogicalToDeviceUnits(15);

    private int Gap => LogicalToDeviceUnits(7);

    public override Size GetPreferredSize(Size proposedSize)
    {
        Size text = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
        int height = Math.Max(GlyphSize, text.Height) + LogicalToDeviceUnits(6);
        return new Size(GlyphSize + Gap + text.Width + LogicalToDeviceUnits(4), height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        ThemePalette palette = ThemeManager.Palette;
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int size = GlyphSize;
        Rectangle glyph = new(0, (Height - size) / 2, size - 1, size - 1);

        if (Checked)
        {
            Color fill = Enabled ? palette.Accent : palette.SecondaryText;
            using (SolidBrush brush = new(fill))
            {
                g.FillEllipse(brush, glyph);
            }

            int inset = Math.Max(3, size / 4);
            Rectangle dot = Rectangle.Inflate(glyph, -inset, -inset);
            using SolidBrush mark = new(palette.GlyphMark);
            g.FillEllipse(mark, dot);
        }
        else
        {
            using (SolidBrush brush = new(palette.GlyphBackground))
            {
                g.FillEllipse(brush, glyph);
            }

            Color border = !Enabled ? palette.Border : _hovered ? palette.Accent : palette.GlyphBorder;
            using Pen pen = new(border, LogicalToDeviceUnits(1));
            g.DrawEllipse(pen, glyph);
        }

        Rectangle textBounds = new(size + Gap, 0, Width - size - Gap, Height);
        TextRenderer.DrawText(
            g,
            Text,
            Font,
            textBounds,
            Enabled ? ForeColor : palette.SecondaryText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
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

    protected override void OnCheckedChanged(EventArgs e)
    {
        Invalidate();
        base.OnCheckedChanged(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }
}

/// <summary>Owner drawn check box, same reasoning as <see cref="ThemedRadioButton"/>.</summary>
internal sealed class ThemedCheckBox : CheckBox
{
    private bool _hovered;

    public ThemedCheckBox(string text)
    {
        Text = text;
        FlatStyle = FlatStyle.Flat;
        AutoSize = true;
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
    }

    private int GlyphSize => LogicalToDeviceUnits(15);

    private int Gap => LogicalToDeviceUnits(7);

    public override Size GetPreferredSize(Size proposedSize)
    {
        Size text = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
        int height = Math.Max(GlyphSize, text.Height) + LogicalToDeviceUnits(6);
        return new Size(GlyphSize + Gap + text.Width + LogicalToDeviceUnits(4), height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        ThemePalette palette = ThemeManager.Palette;
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int size = GlyphSize;
        Rectangle glyph = new(0, (Height - size) / 2, size - 1, size - 1);

        if (Checked)
        {
            using SolidBrush brush = new(Enabled ? palette.Accent : palette.SecondaryText);
            g.FillRectangle(brush, glyph);

            using Pen tick = new(palette.GlyphMark, LogicalToDeviceUnits(2))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            float left = glyph.Left + (glyph.Width * 0.22f);
            float middleX = glyph.Left + (glyph.Width * 0.44f);
            float right = glyph.Left + (glyph.Width * 0.78f);
            float middleY = glyph.Top + (glyph.Height * 0.68f);
            g.DrawLines(tick, new[]
            {
                new PointF(left, glyph.Top + (glyph.Height * 0.52f)),
                new PointF(middleX, middleY),
                new PointF(right, glyph.Top + (glyph.Height * 0.30f)),
            });
        }
        else
        {
            using (SolidBrush brush = new(palette.GlyphBackground))
            {
                g.FillRectangle(brush, glyph);
            }

            Color border = !Enabled ? palette.Border : _hovered ? palette.Accent : palette.GlyphBorder;
            using Pen pen = new(border, LogicalToDeviceUnits(1));
            g.DrawRectangle(pen, glyph);
        }

        Rectangle textBounds = new(size + Gap, 0, Width - size - Gap, Height);
        TextRenderer.DrawText(
            g,
            Text,
            Font,
            textBounds,
            Enabled ? ForeColor : palette.SecondaryText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
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

    protected override void OnCheckedChanged(EventArgs e)
    {
        Invalidate();
        base.OnCheckedChanged(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }
}
