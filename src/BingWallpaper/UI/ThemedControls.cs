using System;
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

    private static int GlyphSize => DpiScale.Round(15);

    private static int Gap => DpiScale.Round(7);

    public override Size GetPreferredSize(Size proposedSize)
    {
        Size text = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
        int height = Math.Max(GlyphSize, text.Height) + DpiScale.Round(6);
        return new Size(GlyphSize + Gap + text.Width + DpiScale.Round(4), height);
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
            using Pen pen = new Pen(border, Math.Max(1, DpiScale.Round(1)));
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

/// <summary>
/// Drop down list whose arrow button and focused value follow the palette.
///
/// ThemeManager switches every ComboBox to FlatStyle.Flat in the dark theme,
/// because the themed control draws a light frame around its text. Flat in turn
/// paints the arrow button as a light grey square with a black glyph, and no
/// property recolours it. A ComboBox is a native window that ignores
/// ControlStyles.UserPaint, so both the button and the value are painted over
/// after the control has finished drawing itself.
/// </summary>
internal sealed class ThemedComboBox : ComboBox
{
    /// <summary>WM_PAINT, winuser.h.</summary>
    private const int WM_PAINT = 0x000F;

    public ThemedComboBox() => DropDownStyle = ComboBoxStyle.DropDownList;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (m.Msg == WM_PAINT && ThemeManager.Palette.IsDark)
        {
            PaintOverlay();
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        // The value is painted differently with and without the focus, and the
        // native control does not repaint the whole client area when it changes.
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        Invalidate();
        base.OnLostFocus(e);
    }

    private void PaintOverlay()
    {
        ThemePalette palette = ThemeManager.Palette;
        using Graphics g = Graphics.FromHwnd(Handle);

        int buttonWidth = SystemInformation.VerticalScrollBarWidth;
        Rectangle button = new(Width - buttonWidth - 1, 1, buttonWidth, Height - 2);
        using (SolidBrush background = new(palette.ControlBackground))
        {
            g.FillRectangle(background, button);

            // A DropDownList paints its value in the system highlight colours while
            // it has the focus - a blue block behind white text, plus a dotted focus
            // rectangle - and neither colour comes from the palette. The value is
            // drawn again over the top; the focus shows as an accent border instead,
            // which is also what the radio buttons and check boxes use.
            if (Focused)
            {
                Rectangle text = new(1, 1, button.Left - 1, Height - 2);
                g.FillRectangle(background, text);

                // The inset the native control leaves in front of its text. NoPadding
                // is what makes it the whole inset: TextRenderer adds a glyph overhang
                // of its own otherwise, and the value would shift to the right by a
                // few pixels every time the control took the focus.
                text.Inflate(-DpiScale.Round(2), 0);
                TextRenderer.DrawText(
                    g,
                    Text,
                    Font,
                    text,
                    Enabled ? palette.Text : palette.SecondaryText,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding |
                    TextFormatFlags.EndEllipsis);
            }
        }

        // Before the chevron, and with no anti aliasing: an anti aliased one pixel
        // rectangle only partly covers its corner pixels, and the flat border
        // underneath is drawn in SystemColors.ControlDark, which stays light in the
        // dark theme - so the corners came out as four bright dots.
        using (Pen border = new(Focused ? palette.Accent : palette.Border))
        {
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }

        // A chevron, matching what the visual styles draw in the light palette.
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int arm = DpiScale.Round(4);
        int centreX = button.Left + (button.Width / 2);
        int centreY = button.Top + (button.Height / 2) - (arm / 2);
        using (Pen glyph = new(Enabled ? palette.Text : palette.SecondaryText, Math.Max(1, DpiScale.Round(1))))
        {
            g.DrawLines(glyph, new[]
            {
                new Point(centreX - arm, centreY),
                new Point(centreX, centreY + arm),
                new Point(centreX + arm, centreY),
            });
        }
    }
}

/// <summary>
/// One pixel horizontal rule.
///
/// A plain Panel would do, except that ThemeManager paints every unrecognised
/// control with the window background, which would make the line invisible.
/// Drawing it here keeps the separator on the palette in both themes.
/// </summary>
internal sealed class ThemedSeparator : Control
{
    public ThemedSeparator()
    {
        // A logical pixel: the form scales this through AutoScaleMode.Dpi, so
        // DpiScale must not be applied on top of it.
        Height = 1;
        TabStop = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        SetStyle(ControlStyles.Selectable, false);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using SolidBrush brush = new(ThemeManager.Palette.Border);
        e.Graphics.FillRectangle(brush, ClientRectangle);
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

    private static int GlyphSize => DpiScale.Round(15);

    private static int Gap => DpiScale.Round(7);

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (Text.Length == 0)
        {
            // Caption-less: a row label describes the box, so the glyph is all there is.
            return new Size(GlyphSize + DpiScale.Round(4), GlyphSize + DpiScale.Round(6));
        }

        Size text = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
        int height = Math.Max(GlyphSize, text.Height) + DpiScale.Round(6);
        return new Size(GlyphSize + Gap + text.Width + DpiScale.Round(4), height);
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

            using Pen tick = new Pen(palette.GlyphMark, Math.Max(1, DpiScale.Round(2)))
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
            using Pen pen = new Pen(border, Math.Max(1, DpiScale.Round(1)));
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
