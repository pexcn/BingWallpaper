using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using BingWallpaper.Theme;

namespace BingWallpaper.UI;

/// <summary>
/// Owner drawn radio button, in both themes.
///
/// The stock WinForms glyph is painted by the system theme against the control
/// background: on a dark background the result is a black circle with a black
/// dot, and no property recolours it. Drawing the glyph ourselves keeps the
/// checked state accent blue in both themes.
///
/// Handing the control back to the Win32 BUTTON class under FlatStyle.System was
/// tried and reverted: native here means the flat grey-and-black glyph of the
/// classic theme, two logical pixels smaller than this one. The blue ring of the
/// Windows 10 settings app is a XAML control with no Win32 equivalent, so the
/// only way to have it is to draw it.
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
/// Drop down list, drawn by the system in both themes.
///
/// It used to be painted over in the dark theme: FlatStyle.Flat there (the themed
/// control drew a light frame around its text), which in turn made the arrow
/// button a light grey square, so button, value and frame were all redrawn after
/// the native control had finished. That overlay is gone - the dark frame is left
/// to SetWindowTheme("DarkMode_CFD") in ThemeManager plus the process wide
/// ForceDark app mode, which is what Explorer itself relies on.
/// </summary>
internal sealed class ThemedComboBox : ComboBox
{
    public ThemedComboBox() => DropDownStyle = ComboBoxStyle.DropDownList;
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

/// <summary>
/// Owner drawn push button, in both themes.
/// <para>
/// A plain Button does not centre its caption in itself: the WinForms button
/// adapter derives a text rectangle from the border width, the focus rectangle and
/// the padding, and centres the caption in <em>that</em>. With the Chinese UI font
/// the result visibly sits low in the button, and it cannot be corrected from the
/// outside - button height and padding both change the box the text is centred in,
/// so they move the button without moving the text inside it. Drawing the caption
/// straight into the client rectangle is the fix.
/// </para>
/// <para>
/// FlatStyle.System was tried for the light theme and reverted. The native BUTTON
/// class does centre its own caption, but its pressed state is the system's faint
/// one - a slightly deeper grey - where this one goes to the selection colour.
/// The dialog has a single button and it should answer a held mouse the same way
/// in both themes.
/// </para>
/// </summary>
internal sealed class ThemedButton : Button
{
    private bool _hovered;
    private bool _pressed;

    public ThemedButton()
    {
        // Flat with no border of its own: everything below is painted by hand, and
        // this keeps the base class from reserving room for a frame it will not draw.
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        ThemePalette palette = ThemeManager.Palette;
        Graphics g = e.Graphics;

        Color face = palette.IsDark ? palette.ControlBackground : SystemColors.Control;
        Color caption = Enabled ? palette.Text : palette.SecondaryText;

        if (Enabled && _pressed)
        {
            face = palette.Selection;
            caption = palette.SelectionText;
        }
        else if (Enabled && _hovered)
        {
            face = palette.Hover;
        }

        using (SolidBrush brush = new(face))
        {
            g.FillRectangle(brush, ClientRectangle);
        }

        // IsDefault carries the accent the way Windows rings the default button of a
        // dialog - the base class would have drawn that frame itself, and no longer does.
        Color border = !Enabled ? palette.Border
            : _hovered || _pressed || IsDefault ? palette.Accent
            : palette.GlyphBorder;

        // No anti aliasing: it only partly covers the corner pixels of a one pixel
        // rectangle, and four faint dots read as a smudge rather than as a frame.
        // The path is inset by half the pen width instead of hugging the client edge: a
        // pen is centred on the path, and DpiScale.Round(1) is two pixels from 150% up,
        // so an edge hugging rectangle loses the outer half of its top and left strokes
        // to clipping and keeps all of the bottom and right ones. That reads as a drop
        // shadow rather than as a frame.
        int stroke = Math.Max(1, DpiScale.Round(1));
        using (Pen pen = new(border, stroke))
        {
            int edge = stroke / 2;
            g.DrawRectangle(pen, edge, edge, Width - stroke, Height - stroke);
        }

        TextRenderer.DrawText(
            g,
            Text,
            Font,
            ClientRectangle,
            caption,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

        // ShowFocusCues, not Focused alone: this button is given the focus as soon as
        // the settings window appears, and a ring around it before the user has
        // touched the keyboard would just look like an error.
        if (Focused && ShowFocusCues)
        {
            int inset = DpiScale.Round(3);
            Rectangle focus = Rectangle.Inflate(ClientRectangle, -inset, -inset);
            using Pen pen = new(caption) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(pen, focus.X, focus.Y, focus.Width - 1, focus.Height - 1);
        }
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
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        // Holding the button down and dragging out of it cancels the click, dragging
        // back in arms it again - the base class tracks that on its own, and the click
        // does fire on a release back inside. OnMouseLeave clears _pressed and nothing
        // else ever sets it again, so without this the paint says "not pressed" while
        // the release is about to activate the button.
        if (Capture && (e.Button & MouseButtons.Left) == MouseButtons.Left)
        {
            bool inside = ClientRectangle.Contains(e.Location);
            if (inside != _pressed)
            {
                _pressed = inside;
                Invalidate();
            }
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_pressed)
        {
            _pressed = false;
            Invalidate();
        }

        base.OnMouseUp(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Space is the only key that holds a button down; Enter goes through
        // PerformClick, which has no sustained pressed state on a stock button either.
        // ButtonBase does record the space press, but only in a flag it keeps to
        // itself, and ControlStyles.UserPaint takes its repaint path out of the
        // picture - so the keyboard would activate this button without it ever
        // looking pressed.
        if (e.KeyCode == Keys.Space && !_pressed)
        {
            _pressed = true;
            Invalidate();
        }

        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && _pressed)
        {
            _pressed = false;
            Invalidate();
        }

        base.OnKeyUp(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        // No key up is coming once the focus is gone, so a space that was still held
        // would leave the button painted as pressed for good.
        _pressed = false;
        Invalidate();
        base.OnLostFocus(e);
    }
}
